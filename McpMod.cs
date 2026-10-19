using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2_MCP;

[ModInitializer("Initialize")]
public static partial class McpMod
{
    public const string Version = "0.4.0";
    public const int DefaultPort = 15526;
    private const string ConfigFileName = "STS2_MCP.conf";

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int? LoadConfigPort()
    {
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (modDir == null) return null;

            string configPath = Path.Combine(modDir, ConfigFileName);
            if (!File.Exists(configPath))
            {
                try
                {
                    var defaultConfig = new Dictionary<string, object> { ["port"] = DefaultPort };
                    string json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
                    File.WriteAllText(configPath, json);
                    GD.Print($"[STS2 MCP] Created default config at {configPath}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    GD.Print($"[STS2 MCP] No config found at {configPath}; using default port {DefaultPort}");
                }
                return null;
            }

            string content = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("port", out var portElem)
                && portElem.TryGetInt32(out int port)
                && port is > 0 and <= 65535)
            {
                return port;
            }

            GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
            return null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to load config: {ex.Message}, using default port {DefaultPort}");
            return null;
        }
    }

    internal static int CurrentPort { get; private set; } = DefaultPort;
    internal static string? ClientId { get; private set; }
    internal static string PortSource { get; private set; } = "default";

    // Port precedence: STS2MCP_PORT env > --clientId N (15526 + N/1000) > config file > default.
    internal static (int port, string source, string? warning) ResolvePort(
        string? envPort, string? clientIdArg, int? configPort)
    {
        var warnings = new List<string>();

        if (!string.IsNullOrEmpty(envPort))
        {
            if (int.TryParse(envPort, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int ep) && ep is >= 1 and <= 65535)
                return (ep, "env", null);
            warnings.Add($"Invalid STS2MCP_PORT '{envPort}' (expected 1..65535), ignored");
        }

        if (clientIdArg != null)
        {
            if (long.TryParse(clientIdArg, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long n)
                && n > 0 && n % 1000 == 0 && n / 1000 <= 1000)
            {
                return ((int)(DefaultPort + n / 1000), $"clientId={n}",
                    warnings.Count > 0 ? string.Join("; ", warnings) : null);
            }
            warnings.Add($"--clientId '{clientIdArg}' is not a positive multiple of 1000 with N/1000 <= 1000, not used for port");
        }

        string? warn = warnings.Count > 0 ? string.Join("; ", warnings) : null;
        if (configPort is >= 1 and <= 65535)
            return (configPort.Value, "config", warn);
        return (DefaultPort, "default", warn);
    }

    public static void Initialize()
    {
        try
        {
            // Optional settings UI patches should not block the HTTP bridge itself.
            TryApplyHarmonyPatches();

            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(OverlayTick));

            string? clientIdArg = null;
            try { clientIdArg = MegaCrit.Sts2.Core.Helpers.CommandLineHelper.GetValue("clientId"); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Failed to read --clientId: {ex.Message}"); }
            var (port, portSource, portWarning) = ResolvePort(
                System.Environment.GetEnvironmentVariable("STS2MCP_PORT"), clientIdArg, LoadConfigPort());
            CurrentPort = port;
            ClientId = clientIdArg;
            PortSource = portSource;
            if (portWarning != null)
                GD.PrintErr($"[STS2 MCP] {portWarning}");

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] Failed to bind port {port}: {ex}");
                throw;
            }

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{Version} (mp-port) server started on http://localhost:{port}/ (port source: {portSource})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to start: {ex}");
        }
    }

    private static void TryApplyHarmonyPatches()
    {
        try
        {
            new Harmony("com.sts2mcp").PatchAll();
        }
        catch (Exception ex)
        {
            GD.Print(
                $"[STS2 MCP] Optional Harmony settings UI injection skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProcessMainThreadQueue()
    {
        int processed = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && processed < 10)
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Main thread action error: {ex}"); }
            processed++;
        }
    }

    internal static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    internal static Task RunOnMainThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void ServerLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = _listener.GetContext();
                // Handle each request asynchronously so we don't block the listener
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (path == "/")
            {
                SendJson(response, new Dictionary<string, object?>
                {
                    ["message"] = $"Hello from STS2 MCP v{Version}",
                    ["status"] = "ok",
                    ["port"] = CurrentPort,
                    ["client_id"] = ClientId,
                    ["pid"] = System.Environment.ProcessId,
                    ["mp_port_build"] = true
                });
            }
            else if (path == "/api/v1/singleplayer")
            {
                // Hard-block singleplayer endpoint during multiplayer runs
                // to prevent calling the non-sync-safe end_turn path
                if (IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Multiplayer run is active. Use /api/v1/multiplayer instead.");
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostAction(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/multiplayer")
            {
                // Guard: reject multiplayer endpoint during singleplayer runs
                if (!IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Not in a multiplayer run. Use /api/v1/singleplayer instead.");
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetMultiplayerState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostMultiplayerAction(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/profiles")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfiles(response);
                else if (request.HttpMethod == "POST")
                    HandlePostProfiles(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/profile")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfile(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/compendium")
            {
                if (request.HttpMethod == "GET")
                    HandleGetCompendium(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/overlay")
            {
                if (request.HttpMethod == "POST")
                    HandlePostOverlay(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/wiki")
            {
                if (request.HttpMethod == "GET")
                    HandleGetWiki(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else
            {
                SendError(response, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            try
            {
                SendError(context.Response, 500, $"Internal error: {ex.Message}");
            }
            catch { /* response may already be closed */ }
        }
    }

    // Called on HTTP thread (not main thread) as a best-effort guard.
    // The try/catch handles race conditions during run transitions.
    // Authoritative checks happen inside RunOnMainThread lambdas.
    internal static bool IsMultiplayerRun()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress
                && MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type.IsMultiplayer();
        }
        catch { return false; }
    }

    private static void HandleGetMultiplayerState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";

        try
        {
            var stateTask = RunOnMainThread(() => BuildMultiplayerGameState());
            var state = stateTask.GetAwaiter().GetResult();

            if (format == "markdown")
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetMultiplayerState: {ex}");
            try
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to read multiplayer game state: {ex.Message}",
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace
                });
            }
            catch { /* response may be unusable */ }
        }
    }

    private static void HandlePostMultiplayerAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Menu actions (FTUE/popup dismissal, game-over, character select, etc.) are
        // scene-tree-driven and equally valid in MP. Route them to the shared handler
        // so MP clients can dismiss blocking FTUE prompts without going through the
        // run-mode-specific dispatcher.
        if (action == "menu_select")
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                var resultTask = RunOnMainThread(() => ExecuteMenuSelect(option, seed));
                var result = resultTask.GetAwaiter().GetResult();
                SendJson(response, result);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}");
            }
            return;
        }

        try
        {
            var resultTask = RunOnMainThread(() => ExecuteMultiplayerAction(action, parsed));
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Multiplayer action failed: {ex.Message}");
        }
    }

    private static void HandleGetState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";

        try
        {
            var stateTask = RunOnMainThread(() => BuildGameState());
            var state = stateTask.GetAwaiter().GetResult();

            if (format == "markdown")
            {
                try
                {
                    SendText(response, FormatAsMarkdown(state), "text/markdown");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[STS2 MCP] FormatAsMarkdown failed, returning JSON: {ex}");
                    SendJson(response, state);
                }
            }
            else
            {
                SendJson(response, state);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetState: {ex}");
            try
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to read game state: {ex.Message}",
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace
                });
            }
            catch { /* response may be unusable */ }
        }
    }

    private static void HandlePostAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Handle menu actions separately (no run required)
        if (action == "menu_select")
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                var resultTask = RunOnMainThread(() => ExecuteMenuSelect(option, seed));
                var result = resultTask.GetAwaiter().GetResult();
                SendJson(response, result);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}");
            }
            return;
        }

        try
        {
            var resultTask = RunOnMainThread(() => ExecuteAction(action, parsed));
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Action failed: {ex.Message}");
        }
    }
}
