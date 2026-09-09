using StS2AP.Data;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using StS2AP.Patches;
using StS2AP.Utils;

namespace StS2AP.Persistence;

internal static class ApSingleplayerSaves
{
    private static SingleplayerRunArchive.Run? _selected;
    private static SingleplayerRunArchive? _archive;
    // Set before native setup, whose reload counter writes through the native save manager.
    internal static bool OwnsRun => _selected != null;

    internal static SingleplayerRunArchive Archive => new(ProjectSettings.GlobalizePath(
        $"user://ArchipelagoSingleplayerRuns/profile-{SaveManager.Instance.CurrentProfileId}"));

    internal static SingleplayerRunArchive.Identity CurrentIdentity()
    {
        var session = ArchipelagoClient.Session
            ?? throw new InvalidOperationException("Connect to the AP slot before selecting a run.");
        if (!ArchipelagoClient.IsConnected || string.IsNullOrWhiteSpace(session.RoomState.Seed))
            throw new InvalidOperationException("The AP slot is not connected.");
        return new(session.RoomState.Seed, session.ConnectionInfo.Team, session.ConnectionInfo.Slot);
    }

    internal static void BeginNew(string character)
    {
        _selected = new() { Id = Guid.NewGuid(), Owner = CurrentIdentity(), Character = character,
            StartOfAct = AncientSettingsUtility.ForNewRun.Location == AncientRelicLocation.StartOfAct };
        _archive = Archive;
    }

    internal static bool CanLoad(string key, string character, bool startOfAct)
    {
        var settings = ArchipelagoClient.Settings;
        if (settings == null || !settings.Characters.TryGetValue(character, out CharacterConfig? config))
            return false;
        return SingleplayerRunArchive.IsAllowed(key,
            startOfAct && settings.APWorldVersion > Constants.VERSION_0_5_3,
            ArchipelagoClient.Progress.MaxProgressiveAncientLevel(config.CharOffset));
    }

    internal static async Task Load(SingleplayerRunArchive.Run run, string key)
    {
        if (!CanLoad(key, run.Character, run.StartOfAct)) throw new InvalidOperationException("This checkpoint's Start of Act Ancient is locked.");
        var archive = Archive;
        string payload = archive.Load(run.Id, key, CurrentIdentity(), run.Character);
        _selected = run;
        _archive = archive;
        // Keep ownership on setup failure: native cleanup must still preserve the unrelated save.
        await Patches_NCharacterSelectScreen.RestoreRun(payload, $"local AP checkpoint {key}", run.Character);
    }

    internal static void Save(SerializableRun snapshot, string kind)
    {
        try
        {
            var run = _selected ?? throw new InvalidOperationException("No AP singleplayer run is selected.");
            var archive = _archive ?? throw new InvalidOperationException("No AP save directory is selected.");
            string key = $"{snapshot.CurrentActIndex + 1}-{kind}";
            if (!CanLoad(key, run.Character, run.StartOfAct)) return;
            archive.Save(run, key, Patches_RunSaveManager.SaveRun.SerializeAndCompress(snapshot),
                snapshot.MapPointHistory?.Sum(act => act.Count) ?? 0);
            LogUtility.Info($"AP local checkpoint saved: run={run.Id:N}, checkpoint={key}");
            NotificationUtility.ShowRawText("AP checkpoint saved locally.", timeout: 3.5,
                includeInDevConsole: false);
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Failed to save local AP checkpoint: {ex}");
            NotificationUtility.ShowRawText("AP checkpoint save failed. Previous checkpoints were preserved; check the log.");
        }
    }

    internal static void MarkEnded(bool victory)
    {
        if (_selected == null || _archive == null || MultiplayerSupport.IsRealMultiplayerRun) return;
        try { _archive.MarkEnded(_selected.Id, victory); }
        catch (Exception ex) { LogUtility.Error($"Could not mark local AP run ended: {ex}"); }
    }

    internal static void ClearSelection() { _selected = null; _archive = null; }
}
