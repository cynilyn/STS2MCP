using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2_MCP;

// Pre-run lobby control for headless / multi-instance multiplayer clients:
// /api/v1/lobby (GET state, POST select_character / ready / unready) plus an
// environment-driven auto-ready state machine (default OFF).
// Only touches: NCharacterSelectButton.Select(), StartRunLobby.SetReady(), and the
// two SaveManager FTUE setters. Never touches RunState, entities or combat.
public static partial class McpMod
{
    private const ulong AutoReadyIntervalMs = 500;
    private const ulong AutoReadyRetryMs = 5000;
    private const ulong AutoReadyReunreadyMs = 2000;
    private const int AutoReadyMaxAttempts = 10;

    // ---- auto-ready configuration (read once at Initialize) ----
    private static bool _arEnabled;
    private static string _arCharacter = "";
    private static int _arMinPlayers;
    private static bool _arDisableTutorials;

    // ---- auto-ready runtime state (main thread only) ----
    private static string _arState = "disabled";
    private static int _arAttempts;
    private static string? _arLastError;
    private static string? _arDetail;
    private static bool _arManualUnready;
    private static StartRunLobby? _arManualUnreadyLobby;
    private static StartRunLobby? _arLobby;
    private static StartRunLobby? _arTutorialsLobby;
    private static StartRunLobby? _arFtueLobby;
    private static ulong _arLastRun;
    private static ulong _arWaitUntil;
    private static ulong _arDoneUnreadySince;

    internal static void InitLobbyAutoReady()
    {
        try
        {
            _arCharacter = (System.Environment.GetEnvironmentVariable("STS2MCP_AUTO_READY") ?? "").Trim();
            string? minStr = System.Environment.GetEnvironmentVariable("STS2MCP_AUTO_READY_MIN_PLAYERS");
            _arMinPlayers = int.TryParse(minStr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int mp) && mp > 0 ? mp : 0;
            string tut = (System.Environment.GetEnvironmentVariable("STS2MCP_DISABLE_TUTORIALS") ?? "").Trim();
            _arDisableTutorials = tut == "1" || tut.Equals("true", StringComparison.OrdinalIgnoreCase);
            _arEnabled = _arCharacter.Length > 0;
            _arState = _arEnabled ? "idle" : "disabled";
            if (_arEnabled)
                GD.Print($"[STS2 MCP] auto-ready: enabled character={_arCharacter} min_players={_arMinPlayers} disable_tutorials={_arDisableTutorials}");
        }
        catch (Exception ex)
        {
            _arEnabled = false;
            _arState = "disabled";
            GD.PrintErr($"[STS2 MCP] auto-ready init failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Validation (main thread only)
    // ------------------------------------------------------------------

    private static bool SafeRunInProgress()
    {
        try { return RunManager.Instance.IsInProgress; }
        catch { return false; }
    }

    // Returns null when a valid lobby screen was found (screen set), else a reason string.
    private static string? FindValidLobby(SceneTree? tree, bool allowHost, out NCharacterSelectScreen? screen)
    {
        screen = null;
        if (tree?.Root == null) return "no_character_select_screen";

        string? firstReason = null;
        foreach (var s in FindAll<NCharacterSelectScreen>(tree.Root))
        {
            string? reason = ValidateLobbyScreen(s, allowHost);
            if (reason == null)
            {
                screen = s;
                return null;
            }
            firstReason ??= reason;
        }
        return firstReason ?? "no_character_select_screen";
    }

    private static string? ValidateLobbyScreen(NCharacterSelectScreen s, bool allowHost)
    {
        if (!IsLiveNode(s) || !s.IsInsideTree()) return "no_character_select_screen";
        var lobby = s.Lobby;
        if (lobby == null) return "lobby_null";
        var net = lobby.NetService;
        if (net == null || !net.IsConnected) return "not_connected";
        bool typeOk = net.Type == NetGameType.Client || (allowHost && net.Type == NetGameType.Host);
        if (!typeOk) return "not_client_lobby";
        ulong netId = net.NetId;
        if (!lobby.Players.Any(p => p.id == netId)) return "local_player_missing";
        if (SafeRunInProgress()) return "run_in_progress";
        return null;
    }

    private static string? LocalCharacterId(StartRunLobby lobby)
    {
        try { return lobby.LocalPlayer.character?.Id.Entry; }
        catch { return null; }
    }

    private static string NormalizeCharacterId(string id)
    {
        id = id.Trim();
        const string prefix = "CHARACTER.";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            id = id.Substring(prefix.Length);
        return id;
    }

    // ------------------------------------------------------------------
    // Selection (shared by POST select_character and auto-ready)
    // ------------------------------------------------------------------

    private enum SelectOutcome { Ok, NoOp, NotFound, Locked, Failed }

    private static (SelectOutcome outcome, string message) SelectCharacterOnScreen(
        NCharacterSelectScreen screen, string requested)
    {
        string want = NormalizeCharacterId(requested);
        var buttons = FindAll<NCharacterSelectButton>(screen);
        NCharacterSelectButton? match = null;
        foreach (var b in buttons)
        {
            var cm = b.Character;
            if (cm == null) continue;
            if (string.Equals(NormalizeCharacterId(cm.Id.Entry), want, StringComparison.OrdinalIgnoreCase))
            {
                match = b;
                break;
            }
        }

        if (match == null)
        {
            var ids = buttons.Where(b => b.Character != null).Select(b => b.Character.Id.Entry);
            return (SelectOutcome.NotFound,
                $"Character '{requested}' not found. Available: {string.Join(", ", ids)}");
        }
        if (match.IsLocked)
            return (SelectOutcome.Locked, $"Character '{requested}' is locked");

        var lobby = screen.Lobby;
        string? current = lobby != null ? LocalCharacterId(lobby) : null;
        if (current != null && string.Equals(NormalizeCharacterId(current), want, StringComparison.OrdinalIgnoreCase))
            return (SelectOutcome.NoOp, "already selected");

        try
        {
            // The only selection path. Deliberately NOT calling Lobby.SetLocalCharacter:
            // Select() sends the lobby message itself and a second path could double-send.
            match.Select();
        }
        catch (Exception ex)
        {
            return (SelectOutcome.Failed, $"Select() failed: {ex.GetType().Name}: {ex.Message}");
        }
        return (SelectOutcome.Ok, "selected");
    }

    // ------------------------------------------------------------------
    // HTTP
    // ------------------------------------------------------------------

    private static Dictionary<string, object?> AutoReadyStatus()
    {
        return new Dictionary<string, object?>
        {
            ["enabled"] = _arEnabled,
            ["character"] = _arCharacter.Length > 0 ? _arCharacter : null,
            ["min_players"] = _arMinPlayers,
            ["state"] = _arState,
            ["attempts"] = _arAttempts,
            ["last_error"] = _arLastError,
            ["manual_unready"] = _arManualUnready,
            ["detail"] = _arDetail
        };
    }

    private static Dictionary<string, object?> BuildLobbyGetState(bool allowHost)
    {
        var result = new Dictionary<string, object?>();
        var tree = Engine.GetMainLoop() as SceneTree;
        string? reason = FindValidLobby(tree, allowHost, out var screen);
        result["run_in_progress"] = SafeRunInProgress();
        if (reason != null || screen == null || screen.Lobby == null)
        {
            result["in_lobby"] = false;
            result["reason"] = reason ?? "no_character_select_screen";
            result["auto_ready"] = AutoReadyStatus();
            return result;
        }

        var lobby = screen.Lobby;
        result["in_lobby"] = true;
        result["lobby"] = BuildStartRunLobbyState(lobby);

        var characters = new List<Dictionary<string, object?>>();
        foreach (var btn in FindAll<NCharacterSelectButton>(screen))
        {
            try
            {
                if (btn.Character == null) continue;
                characters.Add(new Dictionary<string, object?>
                {
                    ["id"] = btn.Character.Id.Entry,
                    ["locked"] = btn.IsLocked,
                    ["selected"] = btn.IsSelected
                });
            }
            catch { /* node may be mid-teardown */ }
        }
        result["characters"] = characters;
        result["local_character"] = LocalCharacterId(lobby);
        result["is_local_ready"] = lobby.LocalPlayer.isReady;
        result["players_count"] = lobby.Players.Count;
        result["auto_ready"] = AutoReadyStatus();
        return result;
    }

    private static void HandleLobby(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod == "GET")
        {
            string? ahq = request.QueryString["allow_host"];
            bool allowHost = string.Equals(ahq, "true", StringComparison.OrdinalIgnoreCase) || ahq == "1";
            try
            {
                var state = RunOnMainThread(() => BuildLobbyGetState(allowHost)).GetAwaiter().GetResult();
                SendJson(response, state);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] HandleLobby GET: {ex}");
                SendError(response, 500, $"Failed to read lobby state: {ex.Message}");
            }
            return;
        }

        if (request.HttpMethod != "POST")
        {
            SendError(response, 405, "Method not allowed");
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try { parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
        catch { SendError(response, 400, "Invalid JSON"); return; }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem)
            || actionElem.ValueKind != JsonValueKind.String)
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        try
        {
            var (status, payload) = RunOnMainThread(() => ExecuteLobbyAction(actionElem.GetString() ?? "", parsed))
                .GetAwaiter().GetResult();
            response.StatusCode = status;
            SendJson(response, payload);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleLobby POST: {ex}");
            SendError(response, 500, $"Lobby action failed: {ex.Message}");
        }
    }

    private static (int status, Dictionary<string, object?> payload) LobbyError(int status, string message,
        Dictionary<string, object?>? extra = null)
    {
        var d = Error(message);
        if (extra != null)
            foreach (var kv in extra) d[kv.Key] = kv.Value;
        return (status, d);
    }

    private static (int status, Dictionary<string, object?> payload) ExecuteLobbyAction(
        string action, Dictionary<string, JsonElement> parsed)
    {
        bool allowHost = parsed.TryGetValue("allow_host", out var ah) && ah.ValueKind == JsonValueKind.True;
        var tree = Engine.GetMainLoop() as SceneTree;
        string? reason = FindValidLobby(tree, allowHost, out var screen);
        if (reason != null || screen == null || screen.Lobby == null)
            return LobbyError(409, $"Not in a valid lobby: {reason ?? "no_character_select_screen"}",
                new Dictionary<string, object?> { ["reason"] = reason ?? "no_character_select_screen" });

        var lobby = screen.Lobby;

        switch (action)
        {
            case "select_character":
            {
                if (!parsed.TryGetValue("character", out var chElem) || chElem.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(chElem.GetString()))
                    return LobbyError(400, "Missing 'character' (string)");

                var (outcome, message) = SelectCharacterOnScreen(screen, chElem.GetString()!);
                switch (outcome)
                {
                    case SelectOutcome.NotFound: return LobbyError(404, message);
                    case SelectOutcome.Locked: return LobbyError(409, message);
                    case SelectOutcome.Failed: return LobbyError(500, message);
                }
                return (200, new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = message,
                    ["local_character"] = LocalCharacterId(lobby)
                });
            }

            case "ready":
            {
                int minPlayers = 0;
                if (parsed.TryGetValue("min_players", out var mpElem) && mpElem.ValueKind == JsonValueKind.Number
                    && mpElem.TryGetInt32(out int mpv))
                    minPlayers = mpv;
                if (lobby.LocalPlayer.isReady)
                    return LobbyError(409, "already ready");
                if (lobby.Players.Count < minPlayers)
                    return LobbyError(409, $"Not enough players: {lobby.Players.Count} < {minPlayers}",
                        new Dictionary<string, object?>
                        {
                            ["players_count"] = lobby.Players.Count,
                            ["min_players"] = minPlayers
                        });
                lobby.SetReady(true);
                _arManualUnready = false;
                return (200, new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["is_local_ready"] = lobby.LocalPlayer.isReady,
                    ["players_count"] = lobby.Players.Count
                });
            }

            case "unready":
            {
                lobby.SetReady(false);
                _arManualUnready = true;
                _arManualUnreadyLobby = lobby;
                return (200, new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["is_local_ready"] = lobby.LocalPlayer.isReady,
                    ["players_count"] = lobby.Players.Count
                });
            }

            default:
                return LobbyError(400, $"Unknown lobby action: {action}. Use: select_character, ready, unready");
        }
    }

    // ------------------------------------------------------------------
    // Auto-ready state machine
    // ------------------------------------------------------------------

    private static void ArSetState(string state, string? detail = null)
    {
        if (_arState != state)
            GD.Print($"[STS2 MCP] auto-ready: {_arState} -> {state}" + (detail != null ? $" ({detail})" : ""));
        _arState = state;
        _arDetail = detail;
    }

    private static void ArResetToIdle(string why, StartRunLobby? newLobby = null)
    {
        _arLobby = null;
        _arAttempts = 0;
        _arWaitUntil = 0;
        _arDoneUnreadySince = 0;
        if (newLobby == null || !ReferenceEquals(_arManualUnreadyLobby, newLobby))
        {
            _arManualUnready = false;
            _arManualUnreadyLobby = null;
        }
        _arLastError = null;
        ArSetState("idle", why);
    }

    private static void LobbyAutoReadyTick()
    {
        try
        {
            if (!_arEnabled) return;
            ulong now = Time.GetTicksMsec();
            if (_arLastRun != 0 && now - _arLastRun < AutoReadyIntervalMs) return;
            _arLastRun = now;
            AutoReadyStep(now);
        }
        catch (Exception ex)
        {
            try { GD.PrintErr($"[STS2 MCP] auto-ready tick error: {ex}"); } catch { }
        }
    }

    private static void AutoReadyStep(ulong now)
    {
        // During a run the lobby screen is gone; keep the state (e.g. done) untouched.
        if (SafeRunInProgress())
        {
            if (_arState == "verifying_ready") ArEnterDone();
            return;
        }

        var tree = Engine.GetMainLoop() as SceneTree;
        string? reason = FindValidLobby(tree, false, out var screen);
        var lobby = reason == null ? screen?.Lobby : null;

        if (_arState != "idle" && _arState != "disabled")
        {
            if (screen == null || lobby == null || !ReferenceEquals(lobby, _arLobby))
                ArResetToIdle(reason ?? "lobby changed", lobby);
        }

        if (screen == null || lobby == null || tree?.Root == null)
            return;

        if (_arWaitUntil != 0 && now < _arWaitUntil) return;
        _arWaitUntil = 0;

        string target = NormalizeCharacterId(_arCharacter);

        switch (_arState)
        {
            case "idle":
            {
                _arLobby = lobby;
                _arAttempts = 0;
                if (!ReferenceEquals(_arManualUnreadyLobby, lobby))
                {
                    _arManualUnready = false;
                    _arManualUnreadyLobby = null;
                }
                _arLastError = null;
                if (_arDisableTutorials && !ReferenceEquals(_arTutorialsLobby, lobby))
                {
                    _arTutorialsLobby = lobby;
                    try
                    {
                        SaveManager.Instance.SetFtuesEnabled(false);
                        SaveManager.Instance.MarkFtueAsComplete("accept_tutorials_ftue");
                        GD.Print("[STS2 MCP] auto-ready: tutorials disabled via SaveManager");
                    }
                    catch (Exception ex)
                    {
                        _arLastError = $"disable tutorials failed: {ex.Message}";
                        GD.PrintErr($"[STS2 MCP] auto-ready: {_arLastError}");
                    }
                }
                if (!ReferenceEquals(_arFtueLobby, lobby))
                {
                    _arFtueLobby = lobby;
                    if (IsAnyFtueVisible(tree.Root))
                        _arLastError = AutoDismissFtue(tree.Root);
                }
                ArSetState("selecting");
                break;
            }

            case "selecting":
            {
                string? cur = LocalCharacterId(lobby);
                if (cur != null && string.Equals(NormalizeCharacterId(cur), target, StringComparison.OrdinalIgnoreCase))
                {
                    ArSetState("waiting_roster");
                    break;
                }
                if (_arAttempts >= AutoReadyMaxAttempts)
                {
                    ArFail($"selection attempts exhausted ({AutoReadyMaxAttempts})");
                    break;
                }
                _arAttempts++;
                var (outcome, message) = SelectCharacterOnScreen(screen, _arCharacter);
                switch (outcome)
                {
                    case SelectOutcome.NotFound:
                    case SelectOutcome.Locked:
                        ArFail(message);
                        break;
                    case SelectOutcome.Failed:
                        _arLastError = message;
                        GD.PrintErr($"[STS2 MCP] auto-ready: {message} (attempt {_arAttempts})");
                        _arWaitUntil = now + AutoReadyRetryMs;
                        break;
                    case SelectOutcome.NoOp:
                        ArSetState("waiting_roster");
                        break;
                    default:
                        ArSetState("verifying_character");
                        break;
                }
                break;
            }

            case "verifying_character":
            {
                string? cur = LocalCharacterId(lobby);
                if (cur != null && string.Equals(NormalizeCharacterId(cur), target, StringComparison.OrdinalIgnoreCase))
                    ArSetState("waiting_roster");
                else
                    ArSetState("selecting", "local character not confirmed");
                break;
            }

            case "waiting_roster":
            {
                int count = lobby.Players.Count;
                if (count >= _arMinPlayers)
                    ArSetState("readying");
                else
                    _arDetail = $"players {count}/{_arMinPlayers}";
                break;
            }

            case "readying":
            {
                if (_arManualUnready) { _arDetail = "manual_unready"; break; }
                if (lobby.LocalPlayer.isReady) { ArEnterDone(); break; }
                if (lobby.Players.Count < _arMinPlayers)
                {
                    ArSetState("waiting_roster", "roster shrank before ready");
                    break;
                }
                if (_arAttempts >= AutoReadyMaxAttempts)
                {
                    ArFail($"attempts exhausted ({AutoReadyMaxAttempts})");
                    break;
                }
                _arAttempts++;
                try
                {
                    lobby.SetReady(true);
                    ArSetState("verifying_ready");
                }
                catch (Exception ex)
                {
                    _arLastError = $"SetReady failed: {ex.Message}";
                    GD.PrintErr($"[STS2 MCP] auto-ready: {_arLastError} (attempt {_arAttempts})");
                    _arWaitUntil = now + AutoReadyRetryMs;
                }
                break;
            }

            case "verifying_ready":
            {
                if (lobby.LocalPlayer.isReady)
                    ArEnterDone();
                else
                    ArSetState("readying", "ready not confirmed");
                break;
            }

            case "done":
            {
                if (lobby.LocalPlayer.isReady || _arManualUnready)
                {
                    _arDoneUnreadySince = 0;
                    break;
                }
                if (_arDoneUnreadySince == 0) _arDoneUnreadySince = now;
                else if (now - _arDoneUnreadySince >= AutoReadyReunreadyMs)
                {
                    _arDoneUnreadySince = 0;
                    ArSetState("readying", "ready flag dropped");
                }
                break;
            }

            default:
                break;
        }
    }

    private static void ArEnterDone()
    {
        _arAttempts = 0;
        _arDoneUnreadySince = 0;
        _arLastError = null;
        ArSetState("done");
    }

    private static void ArFail(string error)
    {
        _arLastError = error;
        GD.PrintErr($"[STS2 MCP] auto-ready: failed: {error}");
        ArSetState("failed", error);
    }

    // Reuses the ExecuteMenuSelect FTUE / popup paths. Prefers an option that declines
    // or disables tutorials; otherwise the first actionable advance/confirm option.
    private static string? AutoDismissFtue(Node root)
    {
        try
        {
            var ftue = BuildVisibleFtueState(root);
            if (ftue == null) return null;
            string screenName = ftue.TryGetValue("menu_screen", out var ms) ? ms?.ToString() ?? "?" : "?";
            var names = new List<string>();
            if (ftue.TryGetValue("options", out var optObj) && optObj is List<Dictionary<string, object?>> opts)
            {
                foreach (var o in opts)
                {
                    bool enabled = !o.TryGetValue("enabled", out var en) || en is not bool eb || eb;
                    if (enabled && o.TryGetValue("name", out var nm) && nm is string ns)
                        names.Add(ns);
                }
            }
            if (names.Count == 0) return $"ftue {screenName}: no actionable option";

            string[] decline = { "no", "decline", "disable", "skip" };
            string choice = names[0];
            foreach (var d in decline)
            {
                var hit = names.FirstOrDefault(n => string.Equals(n, d, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { choice = hit; break; }
            }

            var res = ExecuteMenuSelect(choice, null);
            string st = res.TryGetValue("status", out var sv) ? sv?.ToString() ?? "?" : "?";
            string msg = res.TryGetValue("message", out var mv) ? mv?.ToString() ?? ""
                : res.TryGetValue("error", out var ev) ? ev?.ToString() ?? "" : "";
            string outcome = $"ftue {screenName}: option '{choice}' -> {st} {msg}".Trim();
            GD.Print($"[STS2 MCP] auto-ready: {outcome}");
            return outcome;
        }
        catch (Exception ex)
        {
            return $"ftue dismissal failed: {ex.Message}";
        }
    }
}
