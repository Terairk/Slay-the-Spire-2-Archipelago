using Godot;
using MegaCrit.Sts2.Core.Saves;
using StS2AP.Utils;
using StS2AP.Utils.RunGraph;
using static StS2AP.UI.RunGraph.RunGraphStyle;

namespace StS2AP.UI.RunGraph;

/// <summary>
/// Main-menu list of every AP run with a graph log on this profile, newest first. Each row opens
/// the same graph as the game-over screen, drawn from the game's saved run history.
/// </summary>
internal sealed partial class ApPastRunsOverlay : Control
{
    private const string NodeName = "ApPastRunsOverlay";

    internal static void Show(Control host)
    {
        host.GetNodeOrNull(NodeName)?.QueueFree();
        var overlay = new ApPastRunsOverlay { Name = NodeName };
        overlay.Build();
        host.AddChild(overlay);
    }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 100;

        var dim = new ColorRect { Color = Backdrop, MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        dim.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true })
                QueueFree();
        };
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -640; panel.OffsetRight = 640;
        panel.OffsetTop = -440; panel.OffsetBottom = 440;
        var panelStyle = Box(PanelColor, PanelBorder, 16, 26f, borderWidth: 2);
        panelStyle.ShadowColor = new Color(0f, 0f, 0f, 0.6f);
        panelStyle.ShadowSize = 28;
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 16);
        panel.AddChild(root);

        var header = new HBoxContainer();
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddChild(Label("Past AP Runs", 34, Text, bold: true));
        titles.AddChild(Label("Every Archipelago run recorded on this profile, newest first", 18, TextMuted));
        header.AddChild(titles);
        var close = CreateButton("Close  (Esc)", Card, PanelBorder);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        close.Shortcut = new Shortcut { Events = new Godot.Collections.Array { new InputEventAction { Action = "ui_cancel" } } };
        close.Pressed += QueueFree;
        header.AddChild(close);
        root.AddChild(header);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);
        root.AddChild(scroll);

        var store = ApRunGraphRecorder.Store;
        Control? firstFocus = null;
        foreach (long startTime in store.ListStartTimes())
        {
            var log = store.Load(startTime, out string? error);
            if (log == null)
            {
                if (error != null)
                    LogUtility.Warn(error);
                continue;
            }
            var (row, open) = BuildRow(log);
            list.AddChild(row);
            if (!open.Disabled)
                firstFocus ??= open;
        }

        if (list.GetChildCount() == 0)
        {
            var empty = Label("No AP runs recorded yet. Finish a run while connected to Archipelago and it will appear here.", 20, TextMuted);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            list.AddChild(empty);
        }

        // Focus the first row, or Close, so a controller can use the list.
        Control focus = firstFocus ?? close;
        Ready += () => focus.GrabFocus();
    }

    private (PanelContainer Row, Button Open) BuildRow(ApRunGraphLog log)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = Box(Card, PanelBorder, 12, 14f);
        // The left edge shows the result at a glance.
        style.BorderColor = log.Outcome switch
        {
            ApRunOutcome.Won => Filler,
            ApRunOutcome.Lost => Death,
            _ => TextMuted,
        };
        style.BorderWidthLeft = 4;
        style.BorderWidthTop = style.BorderWidthRight = style.BorderWidthBottom = 0;
        row.AddThemeStyleboxOverride("panel", style);

        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 24);
        row.AddChild(line);

        var who = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        string outcome = log.Outcome switch
        {
            ApRunOutcome.Won => "Victory",
            ApRunOutcome.Lost => "Defeated",
            _ => "Unfinished",
        };
        who.AddChild(Label($"{PrettyCharacter(log.Character)} · {outcome}", 22, Text, bold: true));
        string when = DateTimeOffset.FromUnixTimeSeconds(log.StartTime).ToLocalTime().ToString("g");
        who.AddChild(Label($"{when} · {log.SlotName}", 15, TextMuted));
        line.AddChild(who);

        var sent = log.Events.Where(e => e.Kind == ApRunGraphEventKind.CheckSent && !e.Abandoned).ToList();
        line.AddChild(Figure($"{sent.Count}", "checks sent", Filler));
        line.AddChild(Figure($"{sent.Count(e => e.Classification == ApRunGraphItemType.Progression)}", "progression", Progression));
        line.AddChild(Figure($"{log.Events.Count(e => e.Kind == ApRunGraphEventKind.ItemClaimed)}", "claimed", Claim));

        var open = CreateButton("View graph", Card.Lightened(0.08f), Progression);
        open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        open.Pressed += () => OpenGraph(log);
        // The game only saves a run's history when it ends, and the graph is drawn from that history.
        if (log.Outcome == null)
        {
            open.Text = "Not finished";
            open.Disabled = true;
            open.TooltipText = "This run never reached a game-over screen, so the game saved no history to draw.";
            open.AddThemeColorOverride("font_disabled_color", TextMuted);
            open.AddThemeStyleboxOverride("disabled", Box(Card, PanelBorder, 10, 8f));
        }
        line.AddChild(open);
        return (row, open);
    }

    private static VBoxContainer Figure(string value, string caption, Color accent)
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(110f, 0f) };
        box.AddThemeConstantOverride("separation", 0);
        var number = Label(value, 26, accent, bold: true);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(number);
        var label = Label(caption, 14, TextMuted);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(label);
        return box;
    }

    private void OpenGraph(ApRunGraphLog log)
    {
        // The game saves a run's history as "{StartTime}.run" once the run ends.
        var history = SaveManager.Instance.LoadRunHistory($"{log.StartTime}.run");
        if (!history.Success || history.SaveData == null)
        {
            NotificationUtility.ShowRawText("The game has no saved history for this run, so its graph can't be drawn. Unfinished runs have none yet.");
            return;
        }
        ApRunGraphOverlay.Show(this, history.SaveData.MapPointHistory, log);
    }

    private static string PrettyCharacter(string character) => string.IsNullOrEmpty(character)
        ? "Unknown character"
        : char.ToUpperInvariant(character[0]) + character[1..].ToLowerInvariant();

    private static Button CreateButton(string text, Color background, Color border)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(170f, 46f),
        };
        button.AddThemeStyleboxOverride("normal", Box(background, border, 10, 8f));
        button.AddThemeStyleboxOverride("hover", Box(background.Lightened(0.12f), Progression, 10, 8f));
        button.AddThemeStyleboxOverride("focus", Box(background.Lightened(0.12f), Progression, 10, 8f));
        button.AddThemeStyleboxOverride("pressed", Box(background.Darkened(0.2f), Progression, 10, 8f));
        button.AddThemeFontOverride("font", Bold);
        button.AddThemeFontSizeOverride("font_size", 19);
        button.AddThemeColorOverride("font_color", Text);
        button.AddThemeColorOverride("font_hover_color", Text);
        return button;
    }
}
