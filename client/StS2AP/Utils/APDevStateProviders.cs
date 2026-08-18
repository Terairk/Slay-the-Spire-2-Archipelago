using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;

namespace StS2AP.Utils;

/// <summary>
/// One coherent, credentials-redacted capture used by all AP developer-console sections.
/// Providers format this snapshot; they never mutate the run or grant pipeline.
/// </summary>
public sealed record ApDevStateContext(
    RunState? RunState,
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
            var context = new ApDevStateContext(
                runState,
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
        string Protocol,
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
                $"protocol={state.Protocol}",
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
            return new MultiplayerSnapshot(
                netType.ToString(),
                RunManager.Instance.NetService.NetId.ToString(),
                MultiplayerSupport.PreparedApSlotId?.ToString() ?? "none",
                context.RunState?.Players.Select(player =>
                    $"netId={player.NetId}, character={player.Character.Id.Entry}").ToArray()
                    ?? Array.Empty<string>(),
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
                $"grantTransport={state.GrantTransport}",
                $"rewardSelection={state.RewardSelection}",
            });
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

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string YesNo(bool value) => value ? "yes" : "no";
}
