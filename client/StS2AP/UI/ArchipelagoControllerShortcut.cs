using Godot;
using StS2AP.Utils;

namespace StS2AP.UI;

/// <summary>
/// Converts the otherwise-unused R3 button into a dedicated AP Loot Menu action.
/// </summary>
internal sealed partial class ArchipelagoControllerShortcut : Node
{
    internal const string ActionName = "archipelago_open_ap_loot";
    internal const string NodeName = "ArchipelagoControllerShortcut";

    public override void _EnterTree()
    {
        SetProcessInput(true);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventJoypadButton
            {
                ButtonIndex: JoyButton.RightStick,
            } controllerEvent)
        {
            return;
        }

        if (!GameUtility.IsInRun)
            return;

        // RuntimeHotkeyService consumes action events rather than raw joypad events. Synthesizing
        // this AP-owned action keeps RitsuLib's focus/console suppression and Steam Input path.
        using var actionEvent = new InputEventAction
        {
            Action = new StringName(ActionName),
            Pressed = controllerEvent.Pressed,
            Strength = controllerEvent.Pressed ? 1.0f : 0.0f,
        };
        Input.ParseInputEvent(actionEvent);
        GetViewport()?.SetInputAsHandled();
    }
}
