using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Data;
using StS2AP.Utils;
using STS2RitsuLib.Networking.ManagedActions;

namespace StS2AP.Multiplayer;

/// <summary>
/// Gives every replica the same AP progress for building campfire and Ancient options.
///
/// MegaCrit builds each player's campfire options and Ancient event on every replica, then sends
/// only the chosen index, so every replica must build the same list. Live progress reaches the
/// replicas at different times: the owner first, then the host, then everyone else, on RitsuLib's
/// sidecar channel, which is not ordered with the game's own messages. Reading it directly could
/// give a player a Smith option, or a different Ancient offer, on one machine but not another.
///
/// Instead, the host sends its view of everyone's progress as a managed action in its own action
/// queue, at two points:
/// <list type="bullet">
/// <item>just before it requests a map move into a rest site, so the inputs execute before the
/// move does;</item>
/// <item>just before its own vote to move to the next act. The act changes when the last vote
/// executes, and the game executes actions in host-assigned order, so the inputs are in place
/// before the next act's Ancient on every replica.</item>
/// </list>
/// Progress the host has not accepted by then applies from the next such room.
/// </summary>
public static class RoomInputsMultiplayer
{
    private const string ActionKey = "room_inputs";

    private static readonly RitsuLibManagedNetActionDescriptor<ApRoomInputsActionMessage>
        ActionDescriptor = new(
            ModuleId: ModEntry.ModId,
            ActionKey: ActionKey,
            Serialize: static message => JsonSerializer.SerializeToUtf8Bytes(message),
            Deserialize: DeserializeMessage,
            Execute: ExecuteAction,
            ActionType: GameActionType.NonCombat
        );

    private static readonly Dictionary<ulong, ApPlayerRoomInputs> InputsByPlayer = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        RitsuLibManagedNetActions.Register(ActionDescriptor);
        _initialized = true;
    }

    /// <summary>
    /// Takes the starting inputs from the run data every replica just launched or loaded. They
    /// cover the first Ancient and a run resumed inside a rest site, which no host action precedes.
    /// </summary>
    public static void BindRun(RunState runState)
    {
        EndRun();
        if (!MultiplayerSupport.IsRealMultiplayerRun)
            return;

        foreach (Player player in runState.Players)
        {
            if (TryReadRunDataInputs(player) is ApPlayerRoomInputs inputs)
                InputsByPlayer[player.NetId] = inputs;
        }
    }

    public static void EndRun()
    {
        InputsByPlayer.Clear();
    }

    /// <summary>
    /// Called on every RequestEnqueue; acts only on the host, ahead of its own move into a rest
    /// site or its own vote for the next act.
    /// </summary>
    public static void BeforeHostRequestsAction(GameAction action)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || RunManager.Instance.NetService.Type != NetGameType.Host
            || RunManager.Instance.DebugOnlyGetState() is not RunState runState)
        {
            return;
        }

        string reason;
        switch (action)
        {
            case MoveToMapCoordAction move
                when MultiplayerSupport.ShouldRunReplicatedConstruction(
                        MultiplayerFeature.RestSites)
                    && runState.Map.GetPoint(move._destination)?.PointType
                        == MapPointType.RestSite:
                reason = $"rest site {move._destination}";
                break;
            case VoteToMoveToNextActAction
                when MultiplayerSupport.ShouldRunReplicatedConstruction(
                    MultiplayerFeature.Ancients):
                reason = $"vote to leave act {runState.CurrentActIndex + 1}";
                break;
            default:
                return;
        }

        RequestInputs(runState, reason);
    }

    public static bool TryGetCampfireInputs(
        Player player,
        out int restLevel,
        out int smithLevel,
        out IReadOnlySet<long> checkedCampfireLocationIds,
        out string reason)
    {
        restLevel = 0;
        smithLevel = 0;
        checkedCampfireLocationIds = new HashSet<long>();
        reason = string.Empty;
        if (!InputsByPlayer.TryGetValue(player.NetId, out ApPlayerRoomInputs? inputs)
            || !inputs.HasCampfireInputs)
        {
            reason = $"no agreed campfire inputs exist for player {player.NetId}";
            return false;
        }

        restLevel = inputs.RestLevel;
        smithLevel = inputs.SmithLevel;
        checkedCampfireLocationIds = new HashSet<long>(inputs.CheckedCampfireLocationIds);
        return true;
    }

    /// <summary>Returns a copy of the agreed Progressive Ancient counts per character offset.</summary>
    public static bool TryGetProgressiveAncients(
        ulong netId,
        out Dictionary<long, int> countsByCharacter)
    {
        countsByCharacter = new Dictionary<long, int>();
        if (!InputsByPlayer.TryGetValue(netId, out ApPlayerRoomInputs? inputs))
            return false;

        countsByCharacter = new Dictionary<long, int>(inputs.ProgressiveAncients);
        return true;
    }

    /// <summary>
    /// Host only. Snapshots the host's view of every AP player's progress and queues it as a
    /// managed action. Called from inside RequestEnqueue for the move or vote that triggered it,
    /// so the snapshot takes the action ID just before that action and runs first on every
    /// replica. The host stores it only when the action executes, like everyone else.
    /// </summary>
    private static void RequestInputs(RunState runState, string reason)
    {
        if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared))
        {
            LogUtility.Error($"Cannot send room inputs ({reason}): the shared AP run data is missing.");
            return;
        }

        var message = new ApRoomInputsActionMessage
        {
            RunId = shared.RunId,
            ActIndex = runState.CurrentActIndex,
            Reason = reason,
        };
        foreach (Player player in runState.Players)
        {
            if (TryReadRunDataInputs(player) is ApPlayerRoomInputs inputs)
                message.Players.Add(inputs);
        }

        // No deferral: the inputs only help ahead of the action that triggered them. If the request
        // fails, every replica keeps the previous inputs, which may be stale but are still the same.
        if (!RitsuLibManagedNetActions.Request(RunManager.Instance, ActionDescriptor, message))
        {
            LogUtility.Error(
                $"Could not queue room inputs ({reason}); every player keeps the previous inputs."
            );
        }
    }

    private static Task ExecuteAction(
        RitsuLibManagedNetActionContext<ApRoomInputsActionMessage> context)
    {
        ApRoomInputsActionMessage message = context.Message;
        if (RunManager.Instance.DebugOnlyGetState() is not RunState runState
            || !ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId != message.RunId)
        {
            LogUtility.Warn("Ignored room inputs from another run.");
            return Task.CompletedTask;
        }

        // Replace the whole set: a player the host left out has no inputs on any replica.
        InputsByPlayer.Clear();
        foreach (ApPlayerRoomInputs inputs in message.Players)
            InputsByPlayer[inputs.NetId] = inputs;

        LogUtility.Info(
            $"Stored room inputs for {message.Reason}: "
                + string.Join(
                    ", ",
                    message.Players.Select(p =>
                        $"{p.NetId} rest={p.RestLevel} smith={p.SmithLevel} "
                            + $"campfiresChecked={p.CheckedCampfireLocationIds.Count} "
                            + $"ancients=[{string.Join(" ", p.ProgressiveAncients.Select(a => $"{a.Key}:{a.Value}"))}]")
                )
        );
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads one AP player's inputs from this replica's copy of the run data. On the host that is
    /// the latest progress it has accepted from each owner.
    /// </summary>
    private static ApPlayerRoomInputs? TryReadRunDataInputs(Player player)
    {
        if (!ApPlayerContextResolver.TryGetRewardProgressSource(
                player,
                out ApPlayerRunState source,
                out _
            ))
        {
            return null;
        }

        ApRunProgressState progress = source.Progress;
        var inputs = new ApPlayerRoomInputs { NetId = player.NetId };

        // The lobby's Ancient counts stand in until the owner's progress arrives, and never lower it.
        if (progress.Initialized)
        {
            foreach ((long characterOffset, int count) in progress.ProgressiveAncients)
                inputs.ProgressiveAncients[characterOffset] = count;
        }
        foreach ((long characterOffset, int initialCount) in
            source.InitialProgressiveAncientsByCharacter)
        {
            inputs.ProgressiveAncients.TryGetValue(characterOffset, out int count);
            inputs.ProgressiveAncients[characterOffset] = Math.Max(count, initialCount);
        }

        if (progress.Initialized
            && ApPlayerContextResolver.TryGetCharacterConfig(player, out CharacterConfig config))
        {
            progress.ProgressiveRests.TryGetValue(config.CharOffset, out int restLevel);
            progress.ProgressiveSmiths.TryGetValue(config.CharOffset, out int smithLevel);
            inputs.HasCampfireInputs = true;
            inputs.RestLevel = restLevel;
            inputs.SmithLevel = smithLevel;
            inputs.CheckedCampfireLocationIds =
                progress.CheckedCampfireLocationIds.Order().ToList();
        }

        return inputs;
    }

    private static ApRoomInputsActionMessage DeserializeMessage(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ApRoomInputsActionMessage>(bytes) ?? new();
        }
        catch (JsonException ex)
        {
            LogUtility.Warn($"Could not deserialize room inputs: {ex.Message}");
            return new ApRoomInputsActionMessage();
        }
    }
}
