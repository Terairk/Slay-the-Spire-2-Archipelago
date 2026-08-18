# Multiplayer developer-console requirements

- **Status:** Requirements only; implementation deferred
- **Last updated:** 2026-08-18

## 1. Purpose

Provide fast, repeatable ways to exercise the AP multiplayer grant pipeline and
inspect its state without requiring an AP server command for every test. The
console is a diagnostic entry point, not a second implementation of grant or
networking behavior.

## 2. Required behavior

- Preserve the existing `ap !command` shorthand for sending server commands.
- Allow representative AP receipts to be simulated without an AP connection.
- Pass simulated receipts through the production callback, assignment, routing,
  synchronization, ledger, and persistence boundaries.
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
  STS owner Net ID, grant kind, assignment domain, and applied/pending state.
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
| `summary` | Run, local player, AP slot, connection, protocol, pending/error counts |
| `lobby` | Contributions, Ready blockers, host Ascension set, client mismatch diagnostics |
| `grants` | Pending/applied IDs, route, owner, last transition, acknowledgment state |
| `buffs` | Per-owner FIFO, next buff, last combat attempt |
| `assignments` | Grant ID to concrete cached assignment and domain |
| `rng` | Registered RitsuLib stream names and assignment-domain versions, never mutable RNG internals unless safe |
| `connection` | AP connectivity and history-processing readiness without credentials |
| `multiplayer` | Net ID mapping, host/client role, managed-action registration and last execution status |

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
| `ap !...` | Yes | Yes | Existing owner-local AP server operation. |
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
