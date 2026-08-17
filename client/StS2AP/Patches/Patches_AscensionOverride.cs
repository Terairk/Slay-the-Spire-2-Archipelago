using Godot;
using HarmonyLib;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching;
using StS2AP.Models;

namespace StS2AP.Patches
{
    /// <summary>
    /// Patches for to override ascension UI behavior and values for Archipelago.
    /// </summary>
    public static class Patches_AscensionOverride
    {
        #region Set In-Game Ascension Level


        /// <summary>
        /// Sets the hover tooltip based on the currently enabled ascensions.
        /// </summary>
        [HarmonyPatch(typeof(NTopBarPortraitTip), "OnFocus")]
        public static class ChangeHoverTip
        {
            [HarmonyPrefix]
            public static bool Prefix(NTopBarPortraitTip __instance)
            {
                if(__instance.ShowTip)
                {
                    NHoverTipSet.CreateAndShow(__instance, ArchipelagoClient.Progress.Ascensions.HoverTip)
                        ?.SetGlobalPosition(__instance.GlobalPosition + new Vector2(0f, __instance.Size.Y + 20f));
                }
                return false;
            }
        }

        private static MegaLabel? _ascensionLabel;
        public static MegaLabel? AscensionLabel
        {
            get
            {
                if(_ascensionLabel == null || !GodotObject.IsInstanceValid(_ascensionLabel))
                {
                    return null;
                }
                return _ascensionLabel;
            }
        }

        /// <summary>
        /// Sets the Ascension number in the top left during a run to be the number of enabled ascensions.
        /// </summary>
        public static void ChangeAscensionLabel(String newText)
        {
            Callable.From(() => AscensionLabel?.SetTextAutoSize(newText)).CallDeferred();
        }


        /// <summary>
        /// Captures the ascension number label, so we can change the number.
        /// </summary>
        [HarmonyPatch(typeof(NTopBar), "Initialize")]
        public static class CaptureAscensionLabel
        {
            [HarmonyPostfix]
            public static void PostFix(MegaLabel ____ascensionLabel)
            {
                _ascensionLabel = ____ascensionLabel;
                ChangeAscensionLabel(ArchipelagoClient.Progress.Ascensions.CurrentAscension.Count.ToString());
            }
        }


        ///<summary>
        /// Changes the AscensionManager lookup to check an in memory Set for whether a particular level is toggled.
        /// During a run, everything gets piped to this method.  There are some things that happen outside of runs, but
        /// we mostly don't care.
        /// </summary>
        [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager), "HasLevel")]
        public static class InGameAscensionOverride
        {
            [HarmonyPostfix]
            public static void Postfix(AscensionLevel level, ref bool __result)
            {
                if(!RunManager.Instance.IsInProgress)
                {
                    // Not sure we can trust the CurrentAscension Set in this case or not.
                    return;
                }
                __result = ArchipelagoClient.Progress.Ascensions.HasLevel(level);
            }
        }
        
        /// <summary>
        /// Helps disable Double Boss when that ascension was active, but is no longer.
        /// </summary>
        [HarmonyPatch(typeof(SavedActMap), nameof(SavedActMap.SecondBossMapPoint), MethodType.Getter)]
        public static class DisableSecondBossMapPointSaved
        {
            [HarmonyPostfix]
            public static void Postfix(ref MapPoint? __result)
            {
                if (!ArchipelagoClient.Progress.Ascensions.CurrentAscension.Contains(AscensionLevel.DoubleBoss))
                {
                    __result = null;
                }
            }

        }

        /// <summary>
        /// Helps disable Double Boss when that ascension was active, but is no longer.
        /// </summary>
        [HarmonyPatch(typeof(SerializableActMap), nameof(SerializableActMap.SecondBossPoint), MethodType.Getter)]
        public static class DisableSecondBossMapPointSerializable
        {
            [HarmonyPostfix]
            public static void Postfix(ref SerializableMapPoint? __result)
            {
                if (!ArchipelagoClient.Progress.Ascensions.CurrentAscension.Contains(AscensionLevel.DoubleBoss))
                {
                    __result = null;
                }
            }

        }

        /// <summary>
        /// Helps disable Double Boss when that ascension was active, but is no longer.
        /// </summary>
        [HarmonyPatch(typeof(StandardActMap), nameof(StandardActMap.SecondBossMapPoint), MethodType.Getter)]
        public static class DisableSecondBossMapPoint
        {
            [HarmonyPostfix]
            public static void Postfix(ref MapPoint? __result)
            {
                if (!ArchipelagoClient.Progress.Ascensions.CurrentAscension.Contains(AscensionLevel.DoubleBoss))
                {
                    __result = null;
                }
            }

        }

        #endregion

        #region Update Ascension-Related UI

        private static readonly AccessTools.FieldRef<NCharacterSelectScreen, NAscensionPanel>
            AscensionPanelRef =
                PrivateAccess.FieldRef<NCharacterSelectScreen, NAscensionPanel>("_ascensionPanel");

        private static readonly AccessTools.FieldRef<NAscensionPanel, MegaLabel>
            AscensionLevelLabelRef =
                PrivateAccess.FieldRef<NAscensionPanel, MegaLabel>("_ascensionLevel");

        private static readonly AccessTools.FieldRef<NAscensionPanel, MegaRichTextLabel>
            AscensionInfoRef =
                PrivateAccess.FieldRef<NAscensionPanel, MegaRichTextLabel>("_info");

        private static readonly AccessTools.FieldRef<NAscensionPanel, NButton> LeftArrowRef =
            PrivateAccess.FieldRef<NAscensionPanel, NButton>("_leftArrow");

        private static readonly AccessTools.FieldRef<NAscensionPanel, NButton> RightArrowRef =
            PrivateAccess.FieldRef<NAscensionPanel, NButton>("_rightArrow");

        private static NCharacterSelectScreen? _activeCharacterSelectScreen;
        private static CharacterConfig? _selectedCharacterConfig;
        private static bool _selectedCharacterUnlocked;

        /// <summary>
        /// Replaces the vanilla profile-based ascension presentation with the effective
        /// AP ascension configuration for the selected character. This deliberately writes
        /// only to the panel's labels: calling SetAscensionLevel would emit a lobby signal
        /// and could persist the AP value as the character's vanilla preferred ascension.
        /// </summary>
        public static void UpdateCharacterSelectAscension(
            NCharacterSelectScreen screen,
            NCharacterSelectButton characterButton,
            CharacterModel character
        )
        {
            _activeCharacterSelectScreen = screen;
            _selectedCharacterUnlocked = !characterButton.IsLocked;

            if (!ArchipelagoClient.Settings.Characters.TryGetValue(
                    character.Id.Entry,
                    out var config
                ))
            {
                _selectedCharacterConfig = null;
                SetCharacterSelectPanelVisible(screen, false);
                return;
            }

            _selectedCharacterConfig = config;
            if (!_selectedCharacterUnlocked)
            {
                SetCharacterSelectPanelVisible(screen, false);
                return;
            }

            RenderCharacterSelectAscension(screen, config);
        }

        /// <summary>
        /// Refreshes the open character-select panel after a live Ascension Down receipt.
        /// </summary>
        public static void RefreshCharacterSelectAscension(long characterOffset)
        {
            var screen = _activeCharacterSelectScreen;
            var config = _selectedCharacterConfig;
            if (screen == null
                || !GodotObject.IsInstanceValid(screen)
                || config == null
                || !_selectedCharacterUnlocked
                || config.CharOffset != characterOffset)
            {
                return;
            }

            RenderCharacterSelectAscension(screen, config);
        }

        private static void RenderCharacterSelectAscension(
            NCharacterSelectScreen screen,
            CharacterConfig config
        )
        {
            var panel = AscensionPanelRef(screen);
            var levelLabel = AscensionLevelLabelRef(panel);
            var infoLabel = AscensionInfoRef(panel);

            var ascensionManager = ArchipelagoClient.Progress.Ascensions;
            var configured = ascensionManager.GetConfiguredAscensions(config);
            var effective = ascensionManager.GetEffectiveAscensions(
                config,
                ArchipelagoClient.Progress.AllReceivedItems
            );
            var removed = configured.Except(effective);

            levelLabel.SetTextAutoSize(effective.Count.ToString());

            var lines = new List<string>
            {
                $"[gold]Active:[/gold] {FormatAscensionRanges(effective)}"
            };
            var removedText = FormatAscensionRanges(removed);
            if (removedText != "None")
            {
                lines.Add($"[gold]Removed:[/gold] {removedText}");
            }

            infoLabel.Text = string.Join("\n", lines);
            panel.Visible = true;
            LogUtility.Debug(
                $"Character-select ascension for {config.OfficialName}: "
                + $"active={FormatAscensionRanges(effective)}, removed={removedText}"
            );
        }

        private static void SetCharacterSelectPanelVisible(
            NCharacterSelectScreen screen,
            bool visible
        )
        {
            AscensionPanelRef(screen).Visible = visible;
        }

        private static bool IsActiveCharacterSelectPanel(NAscensionPanel panel)
        {
            var screen = _activeCharacterSelectScreen;
            return screen != null
                && GodotObject.IsInstanceValid(screen)
                && ReferenceEquals(AscensionPanelRef(screen), panel)
                && _selectedCharacterConfig != null;
        }

        private static string FormatAscensionRanges(IEnumerable<AscensionLevel> levels)
        {
            var values = levels
                .Select(level => (int)level)
                .Where(level => level >= 1 && level <= 10)
                .Distinct()
                .OrderBy(level => level)
                .ToList();
            if (values.Count == 0)
            {
                return "None";
            }

            var ranges = new List<string>();
            var rangeStart = values[0];
            var rangeEnd = values[0];
            for (var i = 1; i < values.Count; i++)
            {
                if (values[i] == rangeEnd + 1)
                {
                    rangeEnd = values[i];
                    continue;
                }

                ranges.Add(FormatAscensionRange(rangeStart, rangeEnd));
                rangeStart = values[i];
                rangeEnd = values[i];
            }

            ranges.Add(FormatAscensionRange(rangeStart, rangeEnd));
            return string.Join(", ", ranges);
        }

        private static string FormatAscensionRange(int start, int end)
        {
            return start == end ? $"A{start}" : $"A{start}-A{end}";
        }

        [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
        public static class ClearCharacterSelectAscensionState
        {
            [HarmonyPostfix]
            public static void Postfix(NCharacterSelectScreen __instance)
            {
                if (!ReferenceEquals(_activeCharacterSelectScreen, __instance))
                {
                    return;
                }

                _activeCharacterSelectScreen = null;
                _selectedCharacterConfig = null;
                _selectedCharacterUnlocked = false;
            }
        }

        /// <summary>
        /// Keeps vanilla callbacks and the panel's keyboard/controller hotkeys from
        /// replacing the AP-owned text or writing a preferred ascension to the base save.
        /// The lobby retains its own value; only this active AP panel is suppressed.
        /// </summary>
        [HarmonyPatch(typeof(NAscensionPanel), nameof(NAscensionPanel.SetAscensionLevel))]
        public static class PreserveCharacterSelectAscensionPresentation
        {
            [HarmonyPrefix]
            public static bool Prefix(NAscensionPanel __instance)
            {
                return !IsActiveCharacterSelectPanel(__instance);
            }
        }

        /// <summary>
        /// Hides the Ascension Arrows from the UI during Character Select
        /// </summary>
        [HarmonyPatch(typeof(NAscensionPanel))]
        public static class HideAscensionArrows
        {
            [HarmonyPatch("RefreshArrowVisibility")]
            [HarmonyPostfix]
            public static void Postfix(NAscensionPanel __instance)
            {
                LeftArrowRef(__instance).Visible = false;
                RightArrowRef(__instance).Visible = false;
            }
        }


        #endregion
    }
}
