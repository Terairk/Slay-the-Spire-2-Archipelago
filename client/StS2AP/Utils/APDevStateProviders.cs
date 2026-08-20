using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;

namespace StS2AP.Utils;

/// <summary>
/// One coherent, credentials-redacted capture used by all AP developer-console sections.
/// Providers format this snapshot; they never mutate the run or grant pipeline.
/// </summary>
public sealed record ApDevStateContext(
    RunState? RunState,
    StartRunLobby? StartLobby,
    Player? LocalPlayer,
    IReadOnlyList<ApGrantSnapshot> Grants,
    IReadOnlyList<string> Arguments
);

public interface IApDevStateProvider
{
    string Name { get; }

    object Capture(ApDevStateContext context);

    string FormatHumanReadable(object snapshot);
}

public static class ApDevStateProviders
{
    private static readonly IReadOnlyDictionary<string, IApDevStateProvider> Providers =
        new IApDevStateProvider[]
        {
            new SummaryProvider(),
            new GrantsProvider(),
            new AssignmentsProvider(),
            new MultiplayerProvider(),
            new LobbyProvider(),
            new RunDataProvider(),
            new LedgerProvider(),
            new GrantProvider(),
        }.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names => Providers.Keys.ToArray();

    public static bool TryCapture(
        string name,
        IReadOnlyList<string> arguments,
        out string output,
        out string error)
    {
        output = string.Empty;
        error = string.Empty;
        if (!Providers.TryGetValue(name, out IApDevStateProvider? provider))
        {
            error = $"Unknown AP state section '{name}'. Available: {string.Join(", ", Names.Order())}.";
            return false;
        }

        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            MultiplayerSupport.TryGetObservedStartLobby(out StartRunLobby startLobby);
            var context = new ApDevStateContext(
                runState,
                startLobby,
                GameUtility.CurrentPlayer,
                ApMirroredRewardDispatcher.CaptureGrantSnapshots().ToArray(),
                arguments.ToArray()
            );
            output = provider.FormatHumanReadable(provider.Capture(context));
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not capture AP state '{name}': {ex.GetBaseException().Message}";
            return false;
        }
    }

    private sealed record SummarySnapshot(
        string Run,
        string LocalPlayer,
        string ApSlot,
        string Connection,
        string Versions,
        bool InitialItemsLoaded,
        string RewardClaims,
        int Total,
        int Claimable,
        int Applied,
        int Blocked,
        int UnsupportedReceipts
    );

    private sealed class SummaryProvider : IApDevStateProvider
    {
        public string Name => "summary";

        public object Capture(ApDevStateContext context)
        {
            string rewardClaims;
            if (!MultiplayerSupport.IsRealMultiplayerRun)
            {
                rewardClaims = "singleplayer";
            }
            else if (MultiplayerSupport.CanClaimReceivedReward(
                ApMirroredRewardKind.Card,
                out string reason))
            {
                rewardClaims = "enabled";
            }
            else
            {
                rewardClaims = $"blocked ({reason})";
            }

            string localPlayer = context.LocalPlayer == null
                ? "none"
                : $"netId={context.LocalPlayer.NetId}, character={context.LocalPlayer.Character.Id.Entry}";
            return new SummarySnapshot(
                ApMirroredRewardDispatcher.ActiveRunIdentity ?? "none",
                localPlayer,
                MultiplayerSupport.PreparedApSlotId?.ToString() ?? "none",
                ArchipelagoClient.State.ToString(),
                $"AP/{ArchipelagoClient.APVersion}; mirroredRewardSpec/1",
                MultiplayerSupport.InitialItemsLoaded,
                rewardClaims,
                context.Grants.Count,
                context.Grants.Count(grant => grant.State == ApGrantState.Claimable),
                context.Grants.Count(grant => grant.State == ApGrantState.Applied),
                context.Grants.Count(grant => grant.State == ApGrantState.Blocked),
                MultiplayerSupport.PendingUnsupportedItems.Count
            );
        }

        public string FormatHumanReadable(object snapshot)
        {
            var state = (SummarySnapshot)snapshot;
            return string.Join(Environment.NewLine, new[]
            {
                "AP state summary",
                $"run={state.Run}",
                $"localPlayer={state.LocalPlayer}",
                $"apSlot={state.ApSlot}",
                $"connection={state.Connection}",
                $"versions={state.Versions}",
                $"initialItemsLoaded={YesNo(state.InitialItemsLoaded)}",
                $"rewardClaims={state.RewardClaims}",
                $"grants total={state.Total} claimable={state.Claimable} applied={state.Applied} blocked={state.Blocked}",
                $"unsupportedReceipts={state.UnsupportedReceipts}",
            });
        }
    }

    private sealed class GrantsProvider : IApDevStateProvider
    {
        public string Name => "grants";

        public object Capture(ApDevStateContext context) => context.Grants.ToArray();

        public string FormatHumanReadable(object snapshot)
        {
            var grants = (IReadOnlyList<ApGrantSnapshot>)snapshot;
            if (grants.Count == 0)
                return "AP grants: none";
            return "AP grants" + Environment.NewLine
                + string.Join(Environment.NewLine, grants.Select(FormatGrant));
        }
    }

    private sealed class AssignmentsProvider : IApDevStateProvider
    {
        public string Name => "assignments";

        public object Capture(ApDevStateContext context) => context.Grants
            .Where(grant => grant.Assignment != "<unassigned>")
            .ToArray();

        public string FormatHumanReadable(object snapshot)
        {
            var grants = (IReadOnlyList<ApGrantSnapshot>)snapshot;
            if (grants.Count == 0)
                return "AP assignments: none";
            return "AP assignments" + Environment.NewLine
                + string.Join(Environment.NewLine, grants.Select(grant =>
                    $"{grant.GrantId} {grant.Kind} ownerNetId={grant.OwnerNetId} assignment={Quote(grant.Assignment)}"));
        }
    }

    private sealed record MultiplayerSnapshot(
        string NetType,
        string LocalNetId,
        string ApSlot,
        IReadOnlyList<string> Players,
        IReadOnlyList<string> ConnectedNetIds,
        string ParticipantConnection,
        bool Experimental,
        bool ClaimsInvalidated,
        string GrantTransport,
        string RewardSelection
    );

    private sealed class MultiplayerProvider : IApDevStateProvider
    {
        public string Name => "multiplayer";

        public object Capture(ApDevStateContext context)
        {
            NetGameType netType = RunManager.Instance.NetService.Type;
            (IReadOnlyList<string> connectedNetIds, string participantConnection) =
                CaptureParticipantConnection(context.RunState);
            return new MultiplayerSnapshot(
                netType.ToString(),
                RunManager.Instance.NetService.NetId.ToString(),
                MultiplayerSupport.PreparedApSlotId?.ToString() ?? "none",
                context.RunState?.Players.Select(player =>
                    $"netId={player.NetId}, character={player.Character.Id.Entry}").ToArray()
                    ?? Array.Empty<string>(),
                connectedNetIds,
                participantConnection,
                MultiplayerSupport.IsExperimentalMultiplayerRun,
                MultiplayerSupport.ClaimsInvalidated,
                "Ritsu Sidecar required/reliable",
                "MegaCrit native; Ancient=Ritsu LinkedRewardSet"
            );
        }

        public string FormatHumanReadable(object snapshot)
        {
            var state = (MultiplayerSnapshot)snapshot;
            return string.Join(Environment.NewLine, new[]
            {
                "AP multiplayer state",
                $"role={state.NetType} localNetId={state.LocalNetId} apSlot={state.ApSlot}",
                $"experimental={YesNo(state.Experimental)} claimsInvalidated={YesNo(state.ClaimsInvalidated)}",
                $"players=[{string.Join("; ", state.Players)}]",
                $"connectedNetIds=[{string.Join(",", state.ConnectedNetIds)}] participantConnection={state.ParticipantConnection}",
                $"grantTransport={state.GrantTransport}",
                $"rewardSelection={state.RewardSelection}",
            });
        }

        private static (IReadOnlyList<string> ConnectedNetIds, string Status)
            CaptureParticipantConnection(RunState? runState)
        {
            RunLobby? runLobby = RunManager.Instance.RunLobby;
            if (runState == null || runLobby == null)
                return (Array.Empty<string>(), "native-run-lobby-unavailable");

            try
            {
                ulong[] expected = runState.Players.Select(player => player.NetId).ToArray();
                ulong[] connected = BetaMainCompatibility
                    .GetConnectedRunPlayerNetIds(runLobby)
                    .Order()
                    .ToArray();
                var expectedSet = expected.ToHashSet();
                var connectedSet = connected.ToHashSet();
                string status;
                if (expectedSet.Count != expected.Length || connectedSet.Count != connected.Length)
                {
                    status = "duplicate-net-id";
                }
                else
                {
                    ulong[] unexpected = connectedSet.Except(expectedSet).Order().ToArray();
                    ulong[] missing = expectedSet.Except(connectedSet).Order().ToArray();
                    status = unexpected.Length > 0
                        ? $"unexpected:{string.Join(",", unexpected)}"
                        : missing.Length > 0
                            ? $"missing:{string.Join(",", missing)}"
                            : "complete";
                }

                return (connected.Select(id => id.ToString()).ToArray(), status);
            }
            catch (Exception ex)
            {
                return (Array.Empty<string>(), $"unavailable:{ex.GetBaseException().Message}");
            }
        }
    }

    private sealed record LobbySnapshot(
        string Role,
        string LocalNetId,
        string HostNetId,
        string Visibility,
        string ContributionValidation,
        string RunId,
        string HostSettingsFrozen,
        IReadOnlyList<string> Players
    );

    /// <summary>
    /// Makes the RitsuLib lobby-contribution path observable. In particular, running this on
    /// the host proves whether each client's receipt-source contribution reached the host;
    /// client output is intentionally not treated as an authoritative view of other players.
    /// </summary>
    private sealed class LobbyProvider : IApDevStateProvider
    {
        public string Name => "lobby";

        public object Capture(ApDevStateContext context)
        {
            StartRunLobby lobby = context.StartLobby
                ?? throw new InvalidOperationException(
                    "No AP multiplayer start lobby is currently displayed."
                );
            INetGameService netService = lobby.NetService;
            string hostNetId = BetaMainCompatibility.TryGetHostNetId(netService, out ulong hostId)
                ? hostId.ToString()
                : "unavailable";
            string runId = ApRunData.TryGetLobbySharedState(lobby, out ApRunSharedState shared)
                ? FormatRunId(shared.RunId)
                : "missing";
            string hostSettingsFrozen = YesNo(shared?.HostSettings != null);

            var players = new List<string>();
            foreach (ulong netId in BetaMainCompatibility.GetLobbyPlayerNetIds(lobby))
            {
                if (!ApRunData.TryGetLobbyPlayerState(lobby, netId, out ApPlayerRunState state))
                {
                    players.Add($"netId={netId} contribution=missing readyBlocker=missing-contribution");
                    continue;
                }

                string identity = state.Participation switch
                {
                    ApParticipationKind.VanillaGuest => "vanilla-guest",
                    ApParticipationKind.ApGuest => "host-slot",
                    _ => state.ApRoomSeed == null || state.ApTeamId == null || state.ApSlotId == null
                        ? "incomplete"
                        : $"room={Quote(state.ApRoomSeed)} team={state.ApTeamId} slot={state.ApSlotId}",
                };
                string blocker = ApRunData.GetLobbyContributionBlocker(state) ?? "none";
                players.Add(
                    $"netId={netId} participation={state.Participation} identity={identity} "
                        + $"receiptSourceReady={YesNo(state.ReceiptSourceReady)} readyBlocker={blocker}"
                );
            }

            string visibility = netService.Type == NetGameType.Host
                ? "authoritative host staging; merged peer contributions"
                : "local client staging; other-player contributions may be absent";
            string contributionValidation;
            if (netService.Type != NetGameType.Host)
            {
                contributionValidation = "not-authoritative-on-client";
            }
            else if (ApRunData.TryValidateHostLobbyContributions(lobby, out string reason))
            {
                contributionValidation = "ready";
            }
            else
            {
                contributionValidation = $"blocked ({reason})";
            }
            return new LobbySnapshot(
                netService.Type.ToString(),
                netService.NetId.ToString(),
                hostNetId,
                visibility,
                contributionValidation,
                runId,
                hostSettingsFrozen,
                players
            );
        }

        public string FormatHumanReadable(object snapshot)
        {
            var state = (LobbySnapshot)snapshot;
            return string.Join(Environment.NewLine, new[]
            {
                "AP lobby run data",
                $"role={state.Role} localNetId={state.LocalNetId} hostNetId={state.HostNetId}",
                $"visibility={state.Visibility}",
                $"contributionValidation={state.ContributionValidation}",
                $"runId={state.RunId} hostSettingsFrozen={state.HostSettingsFrozen}",
                $"players=[{string.Join("; ", state.Players)}]",
            });
        }
    }

    private sealed record RunDataSnapshot(
        string Role,
        string LocalNetId,
        string HostNetId,
        string RunId,
        string HostSettingsFrozen,
        string SharedSlotCheckScope,
        IReadOnlyList<string> Players
    );

    private sealed class RunDataProvider : IApDevStateProvider
    {
        public string Name => "run";

        public object Capture(ApDevStateContext context)
        {
            RunState runState = context.RunState
                ?? throw new InvalidOperationException("No active run exists.");
            INetGameService netService = RunManager.Instance.NetService;
            string hostNetId = BetaMainCompatibility.TryGetHostNetId(netService, out ulong hostId)
                ? hostId.ToString()
                : "unavailable";
            if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared))
                throw new InvalidOperationException("The active run has no ap_run saved-data slot.");

            var players = new List<string>();
            foreach (Player player in runState.Players)
            {
                if (!ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state))
                {
                    players.Add($"netId={player.NetId} contribution=missing");
                    continue;
                }

                string identity = state.Participation switch
                {
                    ApParticipationKind.VanillaGuest => "vanilla-guest",
                    ApParticipationKind.ApGuest => "host-slot",
                    _ => state.ApRoomSeed == null || state.ApTeamId == null || state.ApSlotId == null
                        ? "incomplete"
                        : $"room={Quote(state.ApRoomSeed)} team={state.ApTeamId} slot={state.ApSlotId}",
                };
                players.Add(
                    $"netId={player.NetId} participation={state.Participation} identity={identity} "
                        + $"revision={state.ProgressRevision} used={state.Progress.UsedItems.Count}"
                );
            }

            return new RunDataSnapshot(
                netService.Type.ToString(),
                netService.NetId.ToString(),
                hostNetId,
                FormatRunId(shared.RunId),
                YesNo(shared.HostSettings != null),
                shared.SharedSlotCheckScope.ToString(),
                players
            );
        }

        public string FormatHumanReadable(object snapshot)
        {
            var state = (RunDataSnapshot)snapshot;
            return string.Join(Environment.NewLine, new[]
            {
                "AP canonical run data",
                $"role={state.Role} localNetId={state.LocalNetId} hostNetId={state.HostNetId}",
                $"runId={state.RunId} hostSettingsFrozen={state.HostSettingsFrozen}",
                $"sharedSlotCheckScope={state.SharedSlotCheckScope}",
                $"players=[{string.Join("; ", state.Players)}]",
            });
        }
    }

    private sealed class LedgerProvider : IApDevStateProvider
    {
        public string Name => "ledger";

        public object Capture(ApDevStateContext context)
        {
            RunState runState = context.RunState
                ?? throw new InvalidOperationException("No active run exists.");
            return runState.Players
                .Select(player => ApRunData.TryGetPlayerState(
                    runState,
                    player.NetId,
                    out ApPlayerRunState state
                )
                    ? $"netId={player.NetId} used=[{string.Join(",", state.Progress.UsedItems.Order())}]"
                    : $"netId={player.NetId} missing")
                .ToArray();
        }

        public string FormatHumanReadable(object snapshot)
        {
            var receipts = (IReadOnlyList<string>)snapshot;
            return receipts.Count == 0
                ? "AP receipt-consumption ledger: empty"
                : "AP receipt-consumption ledger" + Environment.NewLine
                    + string.Join(Environment.NewLine, receipts);
        }
    }

    private sealed class GrantProvider : IApDevStateProvider
    {
        public string Name => "grant";

        public object Capture(ApDevStateContext context)
        {
            if (context.Arguments.Count != 1
                || !TryParseGrantId(context.Arguments[0], out ApGrantId grantId))
            {
                throw new ArgumentException("Usage: ap state grant <AP-slot:received-index>");
            }

            return context.Grants.FirstOrDefault(grant => grant.GrantId == grantId)
                ?? throw new KeyNotFoundException($"AP grant {grantId} was not found among supported receipts.");
        }

        public string FormatHumanReadable(object snapshot) =>
            "AP grant" + Environment.NewLine + FormatGrant((ApGrantSnapshot)snapshot);
    }

    private static bool TryParseGrantId(string value, out ApGrantId grantId)
    {
        grantId = default;
        string[] pieces = value.Split(':', StringSplitOptions.TrimEntries);
        if (pieces.Length != 2
            || !int.TryParse(pieces[0], out int slot)
            || !int.TryParse(pieces[1], out int index)
            || slot < 0
            || index < 1)
        {
            return false;
        }
        grantId = new ApGrantId(slot, index);
        return true;
    }

    private static string FormatGrant(ApGrantSnapshot grant)
    {
        string line = $"{grant.GrantId} {grant.Kind} state={grant.State.ToString().ToLowerInvariant()} "
            + $"ownerNetId={grant.OwnerNetId} item={Quote(grant.ItemName)} "
            + $"assignment={Quote(grant.Assignment)}";
        if (!string.IsNullOrWhiteSpace(grant.LastAttempt))
            line += $" lastAttempt={Quote(grant.LastAttempt)}";
        if (!string.IsNullOrWhiteSpace(grant.BlockedReason))
            line += $" blockedReason={Quote(grant.BlockedReason)}";
        return line;
    }

    private static string FormatRunId(Guid runId) =>
        runId == Guid.Empty ? "missing" : runId.ToString("D");

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string YesNo(bool value) => value ? "yes" : "no";
}
