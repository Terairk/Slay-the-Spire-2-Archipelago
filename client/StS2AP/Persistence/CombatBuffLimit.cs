namespace StS2AP.Persistence;

// Room IDs restart at each map point; nested event combats have distinct room IDs.
public sealed record BuffCombatKey(int ActIndex, int Floor, int RoomId);

/// <summary>
/// Per-player combat allowance, saved with the run and advanced by the same action on
/// every peer. The transient reservation prevents overlapping asynchronous applications.
/// </summary>
public sealed class CombatBuffLimit
{
    public BuffCombatKey? LastConsumedCombat { get; set; }
    private BuffCombatKey? _applying;

    public bool CanConsume(BuffCombatKey combat) =>
        _applying == null && LastConsumedCombat != combat;

    public bool TryBegin(BuffCombatKey combat)
    {
        if (!CanConsume(combat))
            return false;
        _applying = combat;
        return true;
    }

    public void Complete(BuffCombatKey combat)
    {
        if (_applying != combat)
            throw new InvalidOperationException("No buff application is reserved for this combat.");
        LastConsumedCombat = combat;
        _applying = null;
    }

    public void Cancel() => _applying = null;
}
