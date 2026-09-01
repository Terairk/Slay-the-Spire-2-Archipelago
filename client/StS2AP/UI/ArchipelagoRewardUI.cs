using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Utils;

namespace StS2AP.UI;

/// <summary>
/// Thin lifecycle adapter around MegaCrit's native reward screen. AP owns only the persistent
/// receipt catalog and close/reopen semantics; NRewardsScreen owns all presentation and input.
/// </summary>
public static class ArchipelagoRewardUI
{
    private enum ReturnDestination
    {
        Room,
        Map,
        Deck,
    }

    private sealed record NativeMenuSession(bool InitiallyEmpty);

    private static readonly Dictionary<RewardsSet, NativeMenuSession> Sessions = new();

    private static NRewardsScreen? _screen;
    private static RewardsSet? _set;
    private static bool _opening;
    private static bool _closing;
    private static bool _hotkeysRegistered;
    private static ReturnDestination _returnDestination;

    public static Action? OnScreenClosed;

    public static bool IsOpen =>
        _screen != null && GodotObject.IsInstanceValid(_screen) && _screen.IsInsideTree();

    internal static bool IsActive =>
        _screen != null && IsOpen && ActiveScreenContext.Instance.IsCurrent(_screen);

    public static void Toggle()
    {
        if (!IsOpen)
        {
            ShowRewards();
            return;
        }

        if (IsActive)
        {
            Hide();
            return;
        }

        if (!TrySkipOwnedCardPicker())
            LogUtility.Debug("Ignoring AP reward toggle while a nested overlay is active");
    }

    /// <summary>
    /// Opens a fixed snapshot. Items received while the menu is already open intentionally appear
    /// the next time it is opened, so the active native RewardsSet never changes underneath input.
    /// </summary>
    public static void ShowRewards()
    {
        if (IsOpen || _opening)
            return;

        _opening = true;
        Callable.From(() =>
        {
            TaskHelper.RunSafely(OpenOnMainThread());
        }).CallDeferred();
    }

    private static async Task OpenOnMainThread()
    {
        bool opened = false;
        try
        {
            if (IsOpen)
                return;
            PrepareForOpen();
            opened = await ApNativeRewardMenu.Open();
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Failed to open AP reward menu: {ex}");
        }
        finally
        {
            if (!opened && !IsOpen)
                RestoreDestination(_returnDestination);
            _opening = false;
        }
    }

    internal static void ShowNativeMenu(RewardsSet set, bool initiallyEmpty)
    {
        if (GameUtility.CurrentPlayer?.RunState == null)
            throw new InvalidOperationException("Cannot show AP rewards without an active run.");
        if (IsOpen)
            throw new InvalidOperationException("An AP reward menu is already open.");

        Sessions[set] = new NativeMenuSession(initiallyEmpty);
        _set = set;
        _closing = false;

        try
        {
            _screen = NRewardsScreen.ShowScreen(
                set,
                isTerminal: false,
                GameUtility.CurrentPlayer.RunState
            );
            RegisterHotkeys();
            LogUtility.Success(
                $"Native AP reward screen opened with {set.Rewards.Count} reward(s)"
            );
        }
        catch
        {
            Sessions.Remove(set);
            _set = null;
            _screen = null;
            throw;
        }
    }

    public static void Hide()
    {
        if (_closing || !IsOpen || _screen == null || _set == null)
            return;

        _closing = true;
        UnregisterHotkeys();
        if (Sessions.TryGetValue(_set, out NativeMenuSession? session)
            && !session.InitiallyEmpty)
        {
            try
            {
                // MegaCrit uses this synchronizer for the native reward lifecycle in every game
                // mode, including single-player. Skipping completes the active RewardsSet task.
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
            }
            catch (Exception ex)
            {
                LogUtility.Error($"Could not close native AP reward menu: {ex.Message}");
            }
        }

        NOverlayStack.Instance?.Remove(_screen);
    }

    internal static void CloseToMap()
    {
        _returnDestination = ReturnDestination.Map;
        Hide();
    }

    internal static void CloseToDeck()
    {
        _returnDestination = ReturnDestination.Deck;
        Hide();
    }

    public static void RemoveUI()
    {
        UnregisterHotkeys();
        if (_screen != null && GodotObject.IsInstanceValid(_screen))
            _screen.QueueFreeSafely();
        if (_set != null)
            Sessions.Remove(_set);
        _screen = null;
        _set = null;
        _opening = false;
        _closing = false;
        _returnDestination = ReturnDestination.Room;
    }

    internal static bool IsApRewardSet(RewardsSet set) => Sessions.ContainsKey(set);

    internal static bool ShouldKeepEmptyScreenOpen(RewardsSet set) =>
        Sessions.TryGetValue(set, out NativeMenuSession? session) && session.InitiallyEmpty;

    internal static bool ShouldHandleProceedWithoutNativeSkip(RewardsSet set) =>
        Sessions.TryGetValue(set, out NativeMenuSession? session) && session.InitiallyEmpty;

    internal static bool CanSelectNativeReward(NRewardButton button)
    {
        if (button.Reward is not ApNativeRewardMenu.IApNativeReward reward)
            return true;
        if (reward.CanClaim(out string reason))
            return true;

        NotificationUtility.ShowRawText(
            string.IsNullOrWhiteSpace(reason) ? "This AP reward cannot be claimed." : reason
        );
        return false;
    }

    internal static void CloseWithoutNativeSkip(NRewardsScreen screen)
    {
        if (!ReferenceEquals(screen, _screen))
            return;
        _closing = true;
        UnregisterHotkeys();
        NOverlayStack.Instance?.Remove(screen);
    }

    internal static void NotifyNativeScreenClosed(NRewardsScreen screen, RewardsSet set)
    {
        if (!Sessions.Remove(set) || !ReferenceEquals(screen, _screen))
            return;

        UnregisterHotkeys();
        _screen = null;
        _set = null;
        _closing = false;
        ReturnDestination destination = _returnDestination;
        _returnDestination = ReturnDestination.Room;
        OnScreenClosed?.Invoke();
        RestoreDestination(destination);
    }

    private static void RegisterHotkeys()
    {
        if (_hotkeysRegistered || NHotkeyManager.Instance == null)
            return;
        NHotkeyManager.Instance.PushHotkeyPressedBinding(MegaInput.cancel, Hide);
        NHotkeyManager.Instance.PushHotkeyPressedBinding(MegaInput.pauseAndBack, Hide);
        NHotkeyManager.Instance.PushHotkeyReleasedBinding(MegaInput.viewMap, CloseToMap);
        NHotkeyManager.Instance.PushHotkeyReleasedBinding(MegaInput.viewDeckAndTabLeft, CloseToDeck);
        _hotkeysRegistered = true;
    }

    private static void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered || NHotkeyManager.Instance == null)
            return;
        NHotkeyManager.Instance.RemoveHotkeyPressedBinding(MegaInput.cancel, Hide);
        NHotkeyManager.Instance.RemoveHotkeyPressedBinding(MegaInput.pauseAndBack, Hide);
        NHotkeyManager.Instance.RemoveHotkeyReleasedBinding(MegaInput.viewMap, CloseToMap);
        NHotkeyManager.Instance.RemoveHotkeyReleasedBinding(MegaInput.viewDeckAndTabLeft, CloseToDeck);
        _hotkeysRegistered = false;
    }

    private static bool TrySkipOwnedCardPicker()
    {
        if (!IsOpen
            || NOverlayStack.Instance?.Peek() is not NCardRewardSelectionScreen picker
            || !ActiveScreenContext.Instance.IsCurrent(picker))
        {
            return false;
        }

        // Removing the native picker resolves OptionSelected() with null, which is the exact
        // CardReward skip result. Do not guess which alternative button represents Skip: relics
        // may add other actions to the same container.
        NOverlayStack.Instance.Remove(picker);
        return true;
    }

    private static void PrepareForOpen()
    {
        _returnDestination = ReturnDestination.Room;
        NCapstoneContainer? capstoneContainer = NCapstoneContainer.Instance;
        ICapstoneScreen? currentCapstone = capstoneContainer?.CurrentCapstoneScreen;
        if (currentCapstone is NDeckViewScreen)
            _returnDestination = ReturnDestination.Deck;
        if (currentCapstone != null)
            capstoneContainer!.Close();

        NMapScreen? mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen != true)
            return;
        if (currentCapstone == null)
            _returnDestination = ReturnDestination.Map;
        mapScreen.Close(animateOut: false);
    }

    private static void RestoreDestination(ReturnDestination destination)
    {
        switch (destination)
        {
            case ReturnDestination.Map:
                NMapScreen.Instance?.Open(isOpenedFromTopBar: true);
                break;
            case ReturnDestination.Deck:
                Player? player = GameUtility.CurrentPlayer;
                if (player != null)
                {
                    NDeckViewScreen.ShowScreen(player);
                    NRun.Instance?.GlobalUi.TopBar.Deck.ToggleAnimState();
                }
                break;
        }
    }
}
