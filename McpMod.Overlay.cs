using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

// RL watch overlay: an external policy (sts2_bot rl.mcp_watch) POSTs its action probabilities here and the mod
// draws them above the matching cards / map nodes / event buttons, plus a fixed panel (value, note, top choices).
// The HTTP thread only swaps an immutable snapshot; all node work happens in OverlayTick on the main thread.

public class OverlayItem
{
    public int Slot { get; set; }
    public double Prob { get; set; }
    public bool Chosen { get; set; }
    public bool Rule { get; set; }
    public string? Label { get; set; }
}

public class OverlayPanel
{
    public string? Title { get; set; }
    public double? Value { get; set; }
    public double? WinProb { get; set; }
    public string? Note { get; set; }
    public List<string>? Highlights { get; set; }
    public string? Agree { get; set; }
    public List<OverlayItem>? Top { get; set; }
}

public class OverlayPayload
{
    // combat | hand_select | card_reward | map | event | panel (panel only)
    public string? Screen { get; set; }
    public List<OverlayItem>? Items { get; set; }
    public OverlayPanel? Panel { get; set; }
    public bool Clear { get; set; }
}

public static partial class McpMod
{
    private static volatile OverlayPayload? _overlay;
    private static DateTime _overlayAt;
    private const double OverlayTtlSec = 120;

    private static CanvasLayer? _ovLayer;
    private static Control? _ovRoot;
    private static readonly List<Label> _ovLabels = new();
    private static PanelContainer? _ovPanel;
    private static Label? _ovPanelText;
    private static ProgressBar? _ovBar;
    private static Font? _ovFont;
    private static List<Control?> _ovTargets = new();
    private static ulong _ovLastScan;
    private static string? _ovScanScreen;
    private static bool _ovErrorLogged;

    private static readonly Color OvChosen = new(0.45f, 0.95f, 0.55f);
    private static readonly Color OvRule = new(1.0f, 0.78f, 0.3f);
    private static readonly Color OvPlain = new(0.92f, 0.94f, 1.0f);

    private static void HandlePostOverlay(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, System.Text.Encoding.UTF8))
            body = reader.ReadToEnd();
        OverlayPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<OverlayPayload>(body, _jsonOptions);
        }
        catch (Exception ex)
        {
            SendError(response, 400, $"Invalid overlay JSON: {ex.Message}");
            return;
        }
        _overlayAt = DateTime.UtcNow;
        _overlay = p;
        SendJson(response, new { status = "ok" });
    }

    private static void OverlayTick()
    {
        try
        {
            if (!EnsureOverlayNodes())
                return;
            OverlayFont();
            var p = _overlay;
            if (p == null || p.Clear || (DateTime.UtcNow - _overlayAt).TotalSeconds > OverlayTtlSec)
            {
                HideOverlay();
                return;
            }

            ulong now = Time.GetTicksMsec();
            if (now - _ovLastScan > 150 || _ovScanScreen != p.Screen)
            {
                _ovTargets = FindOverlayTargets(p.Screen);
                _ovLastScan = now;
                _ovScanScreen = p.Screen;
            }

            int used = 0;
            foreach (var it in p.Items ?? new List<OverlayItem>())
            {
                if (it.Slot < 0 || it.Slot >= _ovTargets.Count)
                    continue;
                var target = _ovTargets[it.Slot];
                if (target == null || !IsNodeVisible(target))
                    continue;
                var lbl = OverlayLabel(used++);
                lbl.Text = (it.Chosen ? "★ " : "") + $"{it.Prob * 100:0}%" + (it.Rule ? " 규칙" : "");
                lbl.AddThemeColorOverride("font_color", it.Chosen ? OvChosen : it.Rule ? OvRule : OvPlain);
                lbl.ResetSize();
                var rect = target.GetGlobalRect();
                lbl.Position = new Vector2(rect.Position.X + rect.Size.X / 2 - lbl.Size.X / 2,
                                           rect.Position.Y - lbl.Size.Y - 4);
                lbl.Visible = true;
            }
            for (int i = used; i < _ovLabels.Count; i++)
                _ovLabels[i].Visible = false;

            UpdateOverlayPanel(p.Panel);
        }
        catch (Exception ex)
        {
            if (!_ovErrorLogged)
            {
                GD.PrintErr($"[STS2 MCP] overlay error: {ex}");
                _ovErrorLogged = true;
            }
        }
    }

    private static bool EnsureOverlayNodes()
    {
        if (IsLiveNode(_ovLayer))
            return true;
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return false;
        _ovLabels.Clear();
        _ovLayer = new CanvasLayer { Layer = 120, Name = "Sts2McpRlOverlay" };
        _ovRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _ovRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ovLayer.AddChild(_ovRoot);

        _ovPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.11f, 0.82f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10
        };
        _ovPanel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _ovBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false,
            CustomMinimumSize = new Vector2(320, 10), MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _ovPanelText = NewOverlayLabel(20);
        box.AddChild(_ovBar);
        box.AddChild(_ovPanelText);
        _ovPanel.AddChild(box);
        _ovRoot.AddChild(_ovPanel);

        tree.Root.CallDeferred(Node.MethodName.AddChild, _ovLayer);
        return false;
    }

    private static Label NewOverlayLabel(int size)
    {
        var lbl = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeConstantOverride("outline_size", 8);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        var font = OverlayFont();
        if (font != null)
            lbl.AddThemeFontOverride("font", font);
        return lbl;
    }

    // Prefer the game's own font (it looks right), but only one that actually has Hangul: the first label found
    // may use a Latin-only font. Until one is found (rescanned every 2 s), fall back to the Korean system font.
    private static Font? _ovSysFont;
    private static bool _ovGameFont;
    private static ulong _ovFontScan;

    private static Font? OverlayFont()
    {
        if (_ovGameFont)
            return _ovFont;
        ulong now = Time.GetTicksMsec();
        if (now - _ovFontScan > 2000 && Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            _ovFontScan = now;
            var game = FindGameHangulFont(tree.Root);
            if (game != null)
            {
                _ovFont = game;
                _ovGameFont = true;
                ApplyOverlayFont(game);
                return game;
            }
        }
        if (_ovSysFont == null)
        {
            try
            {
                _ovSysFont = new SystemFont
                {
                    FontNames = new[] { "Malgun Gothic", "맑은 고딕", "Noto Sans CJK KR", "Noto Sans KR", "sans-serif" },
                    FontWeight = 600
                };
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] overlay font: {ex.Message}");
            }
        }
        _ovFont ??= _ovSysFont;
        return _ovFont;
    }

    private static Font? FindGameHangulFont(Node root)
    {
        foreach (var rtl in FindAll<RichTextLabel>(root))
        {
            var f = rtl.GetThemeFont("normal_font");
            if (f != null && f.HasChar(0xAC00) && f.HasChar(0xD55C))      // '가', '한'
                return f;
        }
        foreach (var l in FindAll<Label>(root))
        {
            var f = l.GetThemeFont("font");
            if (f != null && f.HasChar(0xAC00) && f.HasChar(0xD55C))
                return f;
        }
        return null;
    }

    private static void ApplyOverlayFont(Font font)
    {
        foreach (var l in _ovLabels)
            l.AddThemeFontOverride("font", font);
        _ovPanelText?.AddThemeFontOverride("font", font);
    }

    private static Label OverlayLabel(int i)
    {
        while (_ovLabels.Count <= i)
        {
            var lbl = NewOverlayLabel(28);
            _ovRoot!.AddChild(lbl);
            _ovLabels.Add(lbl);
        }
        return _ovLabels[i];
    }

    private static void HideOverlay()
    {
        foreach (var l in _ovLabels)
            l.Visible = false;
        if (_ovPanel != null)
            _ovPanel.Visible = false;
    }

    private static void UpdateOverlayPanel(OverlayPanel? panel)
    {
        if (_ovPanel == null || _ovPanelText == null || _ovBar == null)
            return;
        if (panel == null)
        {
            _ovPanel.Visible = false;
            return;
        }
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(panel.Title))
            lines.Add(panel.Title!);
        double bar;
        if (panel.WinProb is double wp)
        {
            lines.Add($"승률 {wp * 100:0}%");
            bar = wp;
        }
        else if (panel.Value is double v)
        {
            lines.Add($"형세 {v:+0.00;-0.00}");
            bar = 0.5 + 0.5 * Math.Tanh(v);
        }
        else
        {
            bar = 0.5;
        }
        _ovBar.Value = bar;
        _ovBar.Visible = panel.WinProb != null || panel.Value != null;
        foreach (var t in panel.Top ?? new List<OverlayItem>())
            lines.Add((t.Chosen ? "★ " : "   ") + $"{t.Prob * 100,3:0}%  {t.Label}" + (t.Rule ? "  [규칙]" : ""));
        if (!string.IsNullOrEmpty(panel.Note))
            lines.Add(panel.Note!);
        if (panel.Highlights is { Count: > 0 })
            lines.Add(string.Join("  ", panel.Highlights.Select(HighlightName)));
        if (!string.IsNullOrEmpty(panel.Agree))
            lines.Add(panel.Agree!);
        _ovPanelText.Text = string.Join("\n", lines);
        _ovPanel.ResetSize();
        var vp = _ovRoot!.GetViewportRect().Size;
        _ovPanel.Position = new Vector2(vp.X - _ovPanel.Size.X - 24, 96);
        _ovPanel.Visible = true;
    }

    private static string HighlightName(string h) => h switch
    {
        "swing" => "[형세 급변]",
        "close_call" => "[고민한 수]",
        "disagree" => "[규칙과 다름]",
        _ => h
    };

    // Slot order must match the indices the Python side uses for MCP commands.
    private static List<Control?> FindOverlayTargets(string? screen)
    {
        var result = new List<Control?>();
        switch (screen)
        {
            case "combat":
            {
                // play_card card_index = position in the model hand; map each model card to its holder node.
                var hand = NPlayerHand.Instance;
                if (hand == null || !RunManager.Instance.IsInProgress)
                    break;
                var runState = RunManager.Instance.DebugOnlyGetState();
                var me = runState == null ? null : LocalContext.GetMe(runState);
                var cards = me?.PlayerCombatState?.Hand?.Cards;
                if (cards == null)
                    break;
                var holders = hand.ActiveHolders.ToList();
                foreach (var card in cards)
                    result.Add(holders.FirstOrDefault(h => h.CardModel == card) as Control);
                break;
            }
            case "hand_select":
            {
                var hand = NPlayerHand.Instance;
                if (hand != null)
                    result.AddRange(hand.ActiveHolders.Select(h => h as Control));
                break;
            }
            case "card_reward":
            {
                if (NOverlayStack.Instance?.Peek() is NCardRewardSelectionScreen screenNode)
                    result.AddRange(FindAllSortedByPosition<NCardHolder>(screenNode).Select(h => (Control?)h));
                break;
            }
            case "map":
            {
                var mapScreen = NMapScreen.Instance;
                if (mapScreen != null)
                    result.AddRange(FindAll<NMapPoint>(mapScreen)
                        .Where(mp => mp.State == MapPointState.Travelable && mp.Point != null)
                        .OrderBy(mp => mp.Point!.coord.col)
                        .Select(mp => (Control?)mp));
                break;
            }
            case "event":
            {
                var room = NEventRoom.Instance;
                if (room != null)
                    result.AddRange(FindAll<NEventOptionButton>(room).Select(b => (Control?)b));
                break;
            }
        }
        return result;
    }
}
