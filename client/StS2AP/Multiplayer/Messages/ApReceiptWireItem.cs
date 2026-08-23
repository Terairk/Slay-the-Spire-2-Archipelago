namespace StS2AP.Multiplayer.Messages;

/// <summary>One stable entry in the host AP slot's received-item catalog.</summary>
public sealed class ApReceiptWireItem
{
    public int Index { get; set; }
    public string SerializedItem { get; set; } = string.Empty;
}
