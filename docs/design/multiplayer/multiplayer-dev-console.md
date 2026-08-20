# Multiplayer developer-console requirements

- **Status:** Existing read-only probes predate the accepted host-progress model;
  corrected providers and runtime validation pending
- **Last updated:** 2026-08-20

## 1. Purpose

Provide fast, repeatable ways to exercise the AP multiplayer grant pipeline and
inspect its state without requiring an AP server command for every test. The
console is a diagnostic entry point, not a second implementation of grant or
networking behavior.

## 2. Required behavior

The first implementation intentionally covers only the read-only provider registry and keeps
the existing server-command passthrough. Its supported commands are:

```text
ap !command
ap state
ap state lobby
ap state run
ap state ledger
ap state receipts
ap state progress
ap state shared-checks
ap state grants
ap state assignments
ap state multiplayer
ap state grant <AP-slot:received-index>
```

`ledger` describes a legacy scaffold probe and is not part of the accepted
target persistence design. Synthetic receipts, JSON/file output, and the
corrected provider names below are future work. This narrower implementation
does not introduce a second grant path.

- Preserve the existing `ap !command` shorthand for sending server commands.
- Allow representative AP receipts to be simulated without an AP connection.
- Pass simulated receipts through the production callback, assignment, routing,
  synchronization, host-progress, and floor-checkpoint boundaries.
- Allocate simulated receipts from a reserved synthetic index range so they
  cannot collide with real AP received-item indexes.
- Never write a simulated receipt acknowledgment to the AP server or AP
  DataStorage.
- Keep the console command itself local. If a simulated operation changes
  replicated state, the production pipeline must perform the synchronization.
- Refuse any mutating multiplayer command that cannot use the production
  synchronization path.
- Do not provide a raw "run this executor only on my process" escape hatch.
- Print stable, useful identifiers including AP slot ID, received-item index,
  STS owner Net ID, grant kind, assignment domain, and claimable/applied/blocked state.
- Never print AP credentials or authentication material.

## 3. Example command surface

The final syntax may change during implementation. These examples define the
capabilities and intended ergonomics:

```text
# Existing AP server passthrough; requires an AP connection.
ap !hint Ironclad Relic
ap !getitem Ironclad Relic

# Synthetic receipt; does not require AP for locally reproducible grants.
ap grant "Ironclad Relic"
ap grant "50 Gold"
ap grant "Strength Buff"
ap grant "Strength Buff" --count 5

# Human-readable state.
ap state
ap state lobby
ap state grants
ap state buffs
ap state assignments
ap state rng
ap state connection
ap state multiplayer

# Stable machine-readable output for bug reports and automated harnesses.
ap state grants --json
ap state grant 3:127 --json
```

`ap grant` must report whether the synthetic receipt was queued, which route was
selected, and its synthetic `ApGrantId`. It must not report success merely
because a RitsuLib managed-action request was issued.

## 4. Extensible state inspection

State output should use a provider registry rather than one growing switch
statement. A provider owns one named section:

```csharp
public interface IApDevStateProvider
{
    string Name { get; }
    object Capture(ApDevStateContext context);
    string FormatHumanReadable(object snapshot);
}
```

Initial provider names are:

| Provider | Minimum content |
|---|---|
| `summary` | Run, local player, AP slot, connection and component versions, claimable/applied/blocked and error counts |
| `lobby` | Derived host Net ID, contribution visibility, `RunId`, frozen settings, each player's Own AP Slot/AP Guest/Vanilla Guest mode, AP source, receipt readiness, blocker, and recomputed host validation |
| `run` | Canonical committed `RunId`, derived host Net ID, participant/AP-source mapping, host Ascension set, and shared check scope |
| `ledger` | Legacy implemented scaffold only; must not be presented as target persistence |
| `receipts` | Source identity, catalog revision/high-watermark, snapshot/delta readiness, and received indices without credentials |
| `progress` | Per-Net-ID consumed indices, aggregate cursors, assignment counts, pending buffs/checks, and last host update/checkpoint evidence |
| `shared-checks` | Frozen scope, participating characters, resolved/deduplicated location IDs, host AP connectivity, and last submission |
| `grants` | Claimable/applied/blocked IDs, route, owner, last attempt, acknowledgment state |
| `buffs` | Per-owner FIFO, next buff, last combat attempt |
| `assignments` | Grant ID to concrete cached assignment and domain |
| `rng` | Registered RitsuLib stream names and assignment-domain versions, never mutable RNG internals unless safe |
| `connection` | AP connectivity and history-processing readiness without credentials |
| `multiplayer` | Frozen and currently connected Net IDs, host/client role, permanent-invalidation state, managed-action registration and last execution status |

`lobby`, `run`, and legacy `ledger` are implemented read-only probes. The lobby output
states its visibility explicitly: host output contains the merged peer
contributions and is authoritative for launch validation; client output may
contain only its local contribution. Therefore, checking whether Bob's
`ApHistoryComplete` reached Alice must be done with `ap state lobby` on Alice's
host process.

JSON mode should serialize a versioned envelope so tooling can distinguish
schema changes:

```json
{
  "schemaVersion": 1,
  "section": "grants",
  "capturedAt": "2026-08-18T00:00:00Z",
  "data": {}
}
```

Providers should capture a coherent snapshot before formatting so a live AP
callback cannot produce internally inconsistent output.

## 5. Safety and multiplayer policy

| Command category | AP connection required? | Allowed in multiplayer? | Rule |
|---|---:|---:|---|
| `ap !...` | Yes | Yes | Allowed only on a process with its own AP connection; AP Guests have none. |
| `ap state ...` | No | Yes | Read-only and credentials-redacted. |
| `ap grant ...` | Usually no | Yes, only through production sync | Must exercise the normal grant router and managed/native transport. |
| Future local mutation shortcut | No | No | Refuse to run; do not bypass replication. |

The initial implementation should favor clear refusal messages over hidden
fallback behavior. Features that inherently require AP server state may still
require a live connection and should explain that requirement in their result.

## 6. Deferred implementation questions

Implementation is deliberately deferred until the multiplayer grant pipeline
exists. At that point decide:

1. The reserved synthetic received-item index format and its save/reset policy.
2. Whether `ap grant` accepts canonical AP item names only or also registered
   aliases.
3. How asynchronous completion is reported after a managed request is queued.
4. Which state snapshots are safe to capture off the main thread.
5. Whether JSON snapshots should optionally be written to a diagnostic file.

No part of this document authorizes direct executor calls or an unsynchronized
multiplayer mutation command.

## 7. Explicit foundation test cases

Run these on the supported two-process game environment. They are runtime
tests; source inspection alone does not satisfy them.

| Case | Host command/evidence | Client command/evidence |
|---|---|---|
| Host identity | `ap state lobby` reports `hostNetId` equal to `localNetId` | `ap state lobby` reports the same host ID, different from the client's local ID |
| Own-slot contribution | Host output contains the AP client's Net ID with complete room/team/slot and direct receipt readiness | Client output contains its own complete contribution; absence of the host contribution here is allowed |
| AP Guest contribution | Host output binds the client Net ID to the fixed host AP source and host receipt revision | Client reports no AP socket and a ready host-relayed catalog |
| Vanilla Guest contribution | Host output contains the client Net ID with `participation=vanilla-guest` and no AP blocker | Client has no AP progress and retains native rewards |
| AP disconnect in lobby | Host shows the affected direct or shared receipt source incomplete, becomes unready if necessary, and disables Ready | Frozen participation does not change |
| Client-last Ready race | Keep the host Ready, then make the client's latest record incomplete before the client readies | Host refuses the all-ready launch, unreaddies itself, and reports the blocking Net ID |
| Committed launch mapping | `ap state run` reports a nonempty `RunId`, the same host ID, and all participants | Client reports the same `RunId`, host ID, and participant mapping |
| Shared receipt delta | `ap state receipts` advances only after host AP callback | AP Guest receives the same revision and never gets ahead of host |
| Immediate consumption | `ap state progress` shows the claimant index consumed before a floor save | Claimant UI immediately removes/blocks the receipt |
| Checkpoint restore | Save/continue retains consumption, cursor, and stable assignment with native state | Rejoined client rebuilds from host progress plus its receipt source |
| Shared checks | `ap state shared-checks` shows host-only submission and deduplication for the frozen scope | AP Guest shows no AP check send |
| Active-run peer rejoin | While the client is absent, `ap state multiplayer` reports its ID as `missing`; after rejoin it reports `participantConnection=complete` without permanent invalidation | Rejoin with the same Steam account or ENet `clientId`; verify the snapshot includes grants completed during the absence and do not bind the AP slot to a replacement Net ID |

The source implementation now uses the same derived validation for host Ready
presentation and the final all-ready launch guard. Runtime confirmation of the
ordering and automatic-unready behavior remains required.
