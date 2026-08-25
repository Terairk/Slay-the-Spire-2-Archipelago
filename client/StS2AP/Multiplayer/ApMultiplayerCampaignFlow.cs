using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.UI;
using StS2AP.Utils;

namespace StS2AP.Multiplayer;

/// <summary>Coordinates the native host flow with the AP campaign picker.</summary>
public static class ApMultiplayerCampaignFlow
{
    private static bool _resumingNativeStart;

    public static bool IsResumingNativeStart => _resumingNativeStart;

    internal static void OpenPicker(NMultiplayerHostSubmenu hostSubmenu, GameMode gameMode)
    {
        ApMultiplayerCampaignStore.CancelPendingNewCampaign();
        try
        {
            ApMultiplayerCampaignStore.ImportCanonicalSave();
        }
        catch (Exception ex)
        {
            LogUtility.Warn($"Could not import the existing multiplayer host save: {ex.Message}");
        }
        ApMultiplayerCampaignPicker.Show(hostSubmenu, gameMode);
    }

    internal static void ResumeNewCampaign(
        NMultiplayerHostSubmenu hostSubmenu,
        GameMode gameMode)
    {
        _resumingNativeStart = true;
        try
        {
            hostSubmenu.StartHost(gameMode);
        }
        finally
        {
            _resumingNativeStart = false;
        }
    }

    internal static bool AllowNewCampaignEmbark(
        NCharacterSelectScreen screen,
        CharacterModel character)
    {
        if (!ApMultiplayerCampaignStore.IsStartingNewCampaign
            || screen.Lobby.NetService.Type != NetGameType.Host
            || !ApMultiplayerCampaignStore.TryGetActiveCampaignForCharacter(
                character.Id.Entry,
                out ApMultiplayerCampaignStore.CampaignMetadata existing))
        {
            return true;
        }

        string key = $"AP_MP_REPLACE_{Guid.NewGuid():N}";
        TextUtility.RegisterLocString(
            key + "_HEADER",
            "Replace Multiplayer Campaign?",
            "ap"
        );
        TextUtility.RegisterLocString(
            key + "_BODY",
            $"{character.Id.Entry} already has an active campaign for this Archipelago slot. "
                + "Confirm to archive its checkpoint and start this new campaign.",
            "ap"
        );

        var popup = new ConfirmPopup
        {
            Header = TextUtility.GetLocString(key + "_HEADER", "ap"),
            Body = TextUtility.GetLocString(key + "_BODY", "ap"),
            ButtonPressed = confirmed =>
            {
                if (!confirmed)
                    return;

                try
                {
                    ApMultiplayerCampaignStore.ArchiveCampaign(existing.CampaignId);
                    AccessTools.Method(typeof(NCharacterSelectScreen), "OnEmbarkPressed")
                        ?.Invoke(screen, null);
                }
                catch (Exception ex)
                {
                    LogUtility.Error($"Could not replace the existing AP campaign: {ex}");
                    NotificationUtility.ShowRawText(
                        "The existing campaign could not be archived. The new run was not started."
                    );
                }
            },
        };
        popup.Show();
        return false;
    }

    internal static bool ValidateLoadLobbyRoster(
        NMultiplayerLoadGameScreen screen,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            object? value = AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby")
                ?.GetValue(screen);
            if (value is not LoadRunLobby lobby)
                return true;

            HashSet<ulong> connectedIds = BetaMainCompatibility
                .GetConnectedRunPlayerNetIds(lobby)
                .ToHashSet();
            connectedIds.Add(lobby.NetService.NetId);
            return ApMultiplayerCampaignStore.ValidateSelectedCampaignRoster(
                connectedIds,
                out reason
            );
        }
        catch (Exception ex)
        {
            // Compatibility guards fail open; the embedded AP identity check still prevents
            // a mismatched local process from inheriting AP progress after launch.
            LogUtility.Warn($"Could not inspect the saved-run lobby roster: {ex.Message}");
            return true;
        }
    }
}
