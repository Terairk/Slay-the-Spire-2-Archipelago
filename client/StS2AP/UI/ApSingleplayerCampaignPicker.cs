using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using StS2AP.Utils;
using static StS2AP.UI.ApCampaignUi;

namespace StS2AP.UI;

/// <summary>Character selection leads to a run list, then that run's retained milestones.</summary>
public sealed partial class ApSingleplayerCampaignPicker : Control, IScreenContext
{
    private NCharacterSelectScreen _screen = null!;
    private string _character = "";
    private VBoxContainer _list = null!;
    private bool _loading;
    private Control? _defaultFocus;
    public Control? DefaultFocusedControl => _defaultFocus;

    public static void Show(NCharacterSelectScreen screen, string character)
    {
        var picker = new ApSingleplayerCampaignPicker { _screen = screen, _character = character };
        picker.Build();
        var container = NModalContainer.Instance
            ?? throw new InvalidOperationException("The modal container is unavailable.");
        container.Clear();
        container.Add(picker, true);
    }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -530; panel.OffsetRight = 530;
        panel.OffsetTop = -350; panel.OffsetBottom = 350;
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle());
        AddChild(panel);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        panel.AddChild(root);
        root.AddChild(CreateLabel($"AP Singleplayer — {_character}", 30, HorizontalAlignment.Center));
        root.AddChild(CreateLabel("Choose a run, then a checkpoint. AP server checks are not rewound.", 19));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_list);
        var cancel = CreateButton("Cancel");
        cancel.Pressed += () => { if (!_loading) NModalContainer.Instance?.Clear(); };
        root.AddChild(cancel);
        ShowRuns();
    }

    private void ClearList()
    {
        foreach (Node child in _list.GetChildren()) { _list.RemoveChild(child); child.QueueFree(); }
    }

    private void ShowRuns()
    {
        ClearList();
        var start = CreateButton("Start New Run", primary: true);
        start.Pressed += () =>
        {
            if (_loading) return;
            if (!ArchipelagoClient.CanSelectCharacter(BetaMainCompatibility.GetLocalCharacter(_screen.Lobby), out string reason))
            { NotificationUtility.ShowRawText(reason); return; }
            NModalContainer.Instance?.Clear();
            _screen.Lobby.SetReady(ready: true);
        };
        _list.AddChild(start);
        _defaultFocus = start;
        if (IsInsideTree()) start.GrabFocus();
        try
        {
            var identity = ApSingleplayerSaves.CurrentIdentity();
            var entries = ApSingleplayerSaves.Archive.List().ToArray();
            foreach (var run in entries.Where(e => e.Run?.Owner == identity && e.Run.Character == _character)
                .Select(e => e.Run!).OrderByDescending(run => run.CreatedAt))
            {
                var button = CreateButton($"{run.CreatedAt.ToLocalTime():g} · {run.Status} · "
                    + $"{run.Checkpoints.Count}/6 checkpoints · {run.Id.ToString("N")[..8]}");
                button.Pressed += () => { if (!_loading) ShowCheckpoints(run); };
                _list.AddChild(button);
            }
            foreach (var entry in entries.Where(e => e.Error != null))
                _list.AddChild(CreateLabel($"Unreadable run: {entry.Error}", 18));
        }
        catch (Exception ex) { _list.AddChild(CreateLabel($"Cannot list saves: {ex.Message}", 18)); }
    }

    private void ShowCheckpoints(SingleplayerRunArchive.Run run)
    {
        ClearList();
        var back = CreateButton("Back to Runs");
        back.Pressed += () => { if (!_loading) ShowRuns(); };
        _list.AddChild(back);
        _defaultFocus = back;
        foreach (string key in SingleplayerRunArchive.Milestones)
        {
            string label = key switch
            {
                "1-ancient" => "Act 1 — Initial Ancient",
                "1-boss" => "Act 1 — Boss defeated",
                "2-boss" => "Act 2 — Boss defeated",
                _ => $"Act {key[0]} — Treasure",
            };
            bool exists = run.Checkpoints.TryGetValue(key, out var snapshot);
            bool allowed = ApSingleplayerSaves.CanLoad(key, _character, run.StartOfAct);
            var button = CreateButton(label + (exists ? $" · Floor {snapshot!.Floor}" : " · Not reached"));
            button.Disabled = !exists || !allowed;
            button.TooltipText = !allowed ? "Start of Act Ancient progression is locked."
                : exists ? $"Saved {snapshot!.SavedAt.ToLocalTime():g}" : "No checkpoint recorded.";
            button.Pressed += () => { if (!_loading) _ = Load(run, key); };
            _list.AddChild(button);
        }
        back.GrabFocus();
    }

    private async Task Load(SingleplayerRunArchive.Run run, string key)
    {
        _loading = true;
        NModalContainer.Instance?.Clear();
        try
        {
            await ApSingleplayerSaves.Load(run, key);
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Failed to load local AP checkpoint {run.Id:N}/{key}: {ex}");
            NotificationUtility.ShowRawText($"Could not load checkpoint: {ex.Message}. Saved checkpoints were preserved.");
            // Setup may have partially initialized the native run. Return through its cleanup
            // with AP ownership still active, instead of allowing a second setup on stale state.
            if (ApSingleplayerSaves.OwnsRun)
                if (MegaCrit.Sts2.Core.Nodes.NGame.Instance is { } game)
                    await game.ReturnToMainMenuAfterRun();
        }
        finally { _loading = false; }
    }
}
