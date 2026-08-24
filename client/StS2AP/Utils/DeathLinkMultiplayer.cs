using System.Text.Json;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.ManagedActions;

namespace StS2AP.Utils;

/// <summary>
/// Owns multiplayer DeathLink action synchronization, replicated HP mutation, and
/// individual-death authority. An own-slot process requests a RitsuLib managed action owned by
/// its local player. The native action queue orders and executes that action on every replica.
/// </summary>
public static class DeathLinkMultiplayer
{
    private const int SchemaVersion = 1;
    private const string DamageActionKey = "death_link_damage_v1";
    private static readonly TimeSpan EchoFallbackWindow = TimeSpan.FromSeconds(6);
    private static readonly object StateLock = new();
    private static readonly HashSet<ulong> ActiveInboundDeaths = new();
    private static readonly Dictionary<ulong, DateTime> RecentInboundLethalDamage = new();

    private static readonly RitsuLibManagedNetActionDescriptor<DeathLinkActionMessage>
        DamageActionDescriptor = new(
            ModuleId: ModEntry.ModId,
            ActionKey: DamageActionKey,
            Serialize: static message => JsonSerializer.SerializeToUtf8Bytes(message),
            Deserialize: DeserializeActionMessage,
            Execute: ExecuteDamageAction,
            ActionType: GameActionType.Any
        );

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        RitsuLibManagedNetActions.Register(DamageActionDescriptor);
        _initialized = true;
    }

    public static void EndRun()
    {
        lock (StateLock)
        {
            ActiveInboundDeaths.Clear();
            RecentInboundLethalDamage.Clear();
        }
    }

    /// <summary>
    /// Routes one AP SDK DeathLink callback into an owner-authored managed action. Only an own-slot
    /// process has an AP SDK callback; AP Guests are added when the host slot's action executes.
    /// </summary>
    public static void Receive(DeathLink info)
    {
        string source = info.Source ?? string.Empty;
        string? cause = info.Cause;
        Callable.From(() => RequestDamageAction(source, cause)).CallDeferred();
    }

    /// <summary>Handles an actual, death-prevention-approved player death on every replica.</summary>
    public static void PlayerDied(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.DeathLink)
            || !ArchipelagoClient.IsConnected
            || player.RunState is not RunState runState
            || runState.CurrentRoom?.IsVictoryRoom == true
            || !IsDeathLinkWriter(player, runState, out ApParticipationKind participation)
            || !ApPlayerContextResolver.TryGetRewardSettings(
                player,
                out ArchipelagoSettings settings
            )
            || !settings.IsDeathLinkEnabled)
        {
            return;
        }

        if (ShouldSuppressOutgoing(player.NetId, out string reason))
        {
            LogUtility.Info(
                $"Suppressing outgoing DeathLink for player {player.NetId}: {reason}."
            );
            return;
        }

        string floorCause = $"Act {runState.CurrentActIndex + 1} Floor {runState.ActFloor}";
        string characterName = player.Character.Id.Entry;
        string cause = participation == ApParticipationKind.ApGuest
            ? $"{ArchipelagoClient.PlayerName}'s AP Guest ({characterName}) was Slain on {floorCause}"
            : $"{ArchipelagoClient.PlayerName} ({characterName}) was Slain on {floorCause}";

        ArchipelagoClient.DeathLinkController.SendDeathLink(
            new DeathLink(ArchipelagoClient.PlayerName, cause)
        );
        LogUtility.Info(
            $"Sent individual-player DeathLink for {player.NetId} ({participation})."
        );
    }

    private static void RequestDamageAction(string source, string? cause)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsLocalOwnApSlot
            || source.Length > 1024
            || cause?.Length > 2048
            || GameUtility.CurrentPlayer is not Player owner
            || !MultiplayerLocationChecks.IsLocalProgressOwner(owner)
            || owner.RunState is not RunState runState
            || !ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId == Guid.Empty
            || !ApRunData.TryGetPlayerState(runState, owner.NetId, out ApPlayerRunState ownerState)
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.SlotSettings is not ArchipelagoSettings ownerSettings
            || !ownerSettings.IsDeathLinkEnabled)
        {
            LogUtility.Warn("Ignored multiplayer DeathLink without a local own-slot run owner.");
            return;
        }

        var message = new DeathLinkActionMessage
        {
            RunId = shared.RunId,
            EventId = Guid.NewGuid(),
            Source = source,
            Cause = cause,
        };

        if (!RitsuLibManagedNetActions.Request(
                RunManager.Instance,
                DamageActionDescriptor,
                message,
                owner.NetId
            ))
        {
            LogUtility.Error($"Could not enqueue managed DeathLink {message.EventId}.");
            NotificationUtility.ShowRawText("Could not synchronize the received DeathLink.");
            return;
        }

        LogUtility.Info(
            $"Requested managed DeathLink {message.EventId} for AP owner {owner.NetId}."
        );
    }

    private static DeathLinkActionMessage DeserializeActionMessage(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<DeathLinkActionMessage>(bytes) ?? new();
        }
        catch (JsonException ex)
        {
            LogUtility.Warn($"Could not deserialize managed DeathLink payload: {ex.Message}");
            return new DeathLinkActionMessage();
        }
    }

    private static async Task ExecuteDamageAction(
        RitsuLibManagedNetActionContext<DeathLinkActionMessage> context)
    {
        DeathLinkActionMessage message = context.Message;
        Player owner = context.Player;
        if (!TryValidateAction(message, owner, out RunState runState, out ArchipelagoSettings settings))
        {
            LogUtility.Warn(
                $"Ignored invalid managed DeathLink {message.EventId} owned by {owner.NetId}."
            );
            return;
        }

        IReadOnlyList<ulong> targetNetIds = GetExpectedTargets(runState, owner.NetId);
        var plans = new List<(Player Target, int NewHp)>();
        foreach (ulong targetNetId in targetNetIds.Order())
        {
            Player target = runState.GetPlayer(targetNetId)
                ?? throw new InvalidOperationException(
                    $"DeathLink target {targetNetId} was absent from the run."
                );
            if (target.Creature.IsDead)
                continue;

            if (LocalContext.IsMe(target))
            {
                string cause = message.Cause ?? $"{message.Source} died";
                NotificationUtility.ShowDeathLink(new DeathLink(message.Source, cause));
            }

            int damage = Mathf.RoundToInt(
                target.Creature.MaxHp * (settings.DeathLinkDamagePercent / 100.0f)
            );
            int newHp = Math.Max(0, target.Creature.CurrentHp - damage);
            plans.Add((target, newHp));
        }

        // Mark the complete AP-slot event as causal before applying its first target. A death hook
        // from one target may affect another target before the sequential recipe reaches it, and
        // that secondary death must not echo the same incoming DeathLink.
        lock (StateLock)
        {
            foreach ((Player target, int newHp) in plans)
            {
                ActiveInboundDeaths.Add(target.NetId);
                if (newHp == 0)
                    RecentInboundLethalDamage[target.NetId] = DateTime.UtcNow;
            }
        }

        try
        {
            foreach ((Player target, int newHp) in plans)
            {
                LogUtility.Info(
                    $"Applying managed DeathLink {message.EventId} to {target.NetId}: "
                        + $"{target.Creature.CurrentHp}->{newHp} HP."
                );
                await CreatureCmd.SetCurrentHp(target.Creature, newHp);
            }
        }
        finally
        {
            lock (StateLock)
            {
                foreach ((Player target, _) in plans)
                {
                    ActiveInboundDeaths.Remove(target.NetId);
                    if (!target.Creature.IsDead)
                        RecentInboundLethalDamage.Remove(target.NetId);
                }
            }
        }
    }

    private static bool TryValidateAction(
        DeathLinkActionMessage message,
        Player owner,
        out RunState runState,
        out ArchipelagoSettings settings)
    {
        runState = null!;
        settings = null!;
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.DeathLink)
            || message.SchemaVersion != SchemaVersion
            || message.RunId == Guid.Empty
            || message.EventId == Guid.Empty
            || message.Source is null
            || message.Source.Length > 1024
            || message.Cause?.Length > 2048
            || RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != message.RunId
            || current.GetPlayer(owner.NetId) is not Player currentOwner
            || !ApRunData.TryGetPlayerState(
                current,
                currentOwner.NetId,
                out ApPlayerRunState ownerState
            )
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.SlotSettings is not ArchipelagoSettings ownerSettings
            || !ownerSettings.IsDeathLinkEnabled
            || ownerSettings.DeathLinkDamagePercent is < 0 or > 100)
        {
            return false;
        }

        runState = current;
        settings = ownerSettings;
        return true;
    }

    private static IReadOnlyList<ulong> GetExpectedTargets(RunState runState, ulong ownerNetId)
    {
        bool ownerIsHost = BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId
            )
            && ownerNetId == hostNetId;
        if (!ownerIsHost)
            return new[] { ownerNetId };

        return runState.Players
            .Where(player =>
                player.NetId == ownerNetId
                || ApRunData.TryGetPlayerState(
                    runState,
                    player.NetId,
                    out ApPlayerRunState state
                ) && state.Participation == ApParticipationKind.ApGuest
            )
            .Select(player => player.NetId)
            .Order()
            .ToArray();
    }

    private static bool IsDeathLinkWriter(
        Player player,
        RunState runState,
        out ApParticipationKind participation)
    {
        participation = ApParticipationKind.VanillaGuest;
        if (!ApRunData.TryGetPlayerState(
                runState,
                player.NetId,
                out ApPlayerRunState state
            ))
        {
            return false;
        }

        participation = state.Participation;
        if (participation == ApParticipationKind.OwnApSlot)
        {
            return MultiplayerSupport.IsLocalOwnApSlot
                && MultiplayerLocationChecks.IsLocalProgressOwner(player);
        }

        return participation == ApParticipationKind.ApGuest
            && MultiplayerSupport.IsLocalOwnApSlot
            && RunManager.Instance.NetService.Type == NetGameType.Host;
    }

    private static bool ShouldSuppressOutgoing(ulong playerNetId, out string reason)
    {
        lock (StateLock)
        {
            if (ActiveInboundDeaths.Contains(playerNetId))
            {
                RecentInboundLethalDamage.Remove(playerNetId);
                reason = "the death is being applied by an incoming DeathLink";
                return true;
            }

            if (RecentInboundLethalDamage.Remove(playerNetId, out DateTime receivedAt))
            {
                TimeSpan elapsed = DateTime.UtcNow - receivedAt;
                if (elapsed <= EchoFallbackWindow)
                {
                    reason = $"incoming lethal damage was received {elapsed.TotalSeconds:F2}s ago";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }
}
