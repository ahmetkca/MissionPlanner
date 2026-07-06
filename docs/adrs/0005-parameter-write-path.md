# 0005. Parameter write path

- **Status:** Accepted
- **Date:** 2026-07-05

## Context

The MVP bridge is read-only (ADR-0001 through ADR-0004 all assume this). The top item on the post-MVP roadmap in `ARCHITECTURE.md` is parameter writes — letting an AI agent change a value on the connected vehicle, not just read it. This is a different risk class than anything shipped so far: a bad read returns wrong information; a bad write can change how a vehicle actually flies.

MP already has the mechanics for this. `MAVLinkInterface.setParamAsync` (`ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs:1638`) builds a `PARAM_SET` MAVLink message, subscribes to the matching `PARAM_VALUE` ack, retries up to 3 times over ~2.1s, and updates the in-memory `MAVLinkParamList` entry on success. `setParam` (`:1623`) is a synchronous wrapper (`setParamAsync(...).AwaitSync()`). None of this needed to be built — the question was entirely about what the bridge and the MCP server should do *around* this call.

Several sub-decisions had to be made together because they interact:

1. Should the bridge refuse writes while the vehicle is armed?
2. How is "a human confirmed this write" enforced — protocol-level, or convention?
3. Which parameters are writable — all of them, a curated subset, or everything except `read_only`?
4. Does a write block the bridge's single listener thread, given `setParamAsync` can take up to ~2.8s?
5. How does the system enforce that the agent actually knows the current value before writing — mirroring the "must Read before Edit" discipline this project's own tooling (Claude Code) already applies to file edits?
6. How does the agent find out if ArduPilot silently clamped the value instead of rejecting it?

## Decision

### `POST /params/{name}` — new bridge endpoint

Request body:

```json
{ "value": 0.15, "expected_current_value": 0.12 }
```

Success response (200):

```json
{
  "name": "ATC_RAT_PIT_P",
  "requested_value": 0.15,
  "applied_value": 0.15,
  "changed": true,
  "reboot_required": false
}
```

Error responses, all using the existing `{error, message}` envelope:

| Code | Error | Cause |
|---|---|---|
| 400 | `bad_request` | body missing/non-numeric `value` or `expected_current_value` |
| 403 | `read_only` | pdef `ReadOnly` flag is true for this parameter |
| 404 | `not_found` | parameter does not exist on the connected vehicle |
| 409 | `value_mismatch` | `expected_current_value` does not match the live value (see below) |
| 503 | `not_connected` | no vehicle connected |
| 504 | `timeout` | MP's internal retry loop exhausted without an ack — **ambiguous outcome** |
| 500 | `internal_error` | unexpected |

Handler sequence in `McpBridgeServer`: capture `comPort` → check connected (503) → look up param (404) → check `read_only` metadata (403) → parse body (400) → compare `expected_current_value` against the live value with a small float tolerance, since MAVLink parameters round-trip through `float32` and exact equality is unsafe (409 on mismatch, response includes the actual current value) → call `port.setParam(name, value)`, catching `TimeoutException` (504) → re-read the live value as `applied_value` → 200.

`bridge_api_version` bumps to `0.2.0` — a new endpoint is a minor bump under the existing rule in `MCP_BRIDGE_DESIGN.md §7`.

### No bridge-side armed gating

The bridge does **not** check `mav.cs.armed` before allowing a write, and does not refuse writes while armed. Most ArduPilot parameters require a reboot of the flight controller to take effect (`reboot_required` in pdef metadata already tells the agent this per-parameter), so "armed" is not a reliable proxy for "this write is dangerous right now" — a bridge-level block would be enforcing a safety property that doesn't match how ArduPilot parameters actually behave, while adding no protection for the parameters that *do* apply immediately.

Instead, `GET /status` gains an `armed: bool | null` field (sourced from `mav.cs.armed` — `ExtLibs/ArduPilot/CurrentState.cs:1884`, already updated by MP from the heartbeat's `SAFETY_ARMED` bit; the same field the existing bridge code already reads for `vehicle_type` via `mav.cs.firmware`). `mp_status` on the MCP side surfaces this, and `set_param`'s tool description instructs the agent to check it and treat "armed" as a reason to pause and confirm with extra care — a prompting-layer responsibility, not a bridge-enforced rule. This keeps the bridge a mechanical layer: it does what it's told, subject only to checks that are true regardless of context (`read_only`, existence, value match). Judgment about *when* it's safe to act lives in the agent, informed by data the bridge now exposes.

### Read-before-write, enforced via `expected_current_value`

We want the same discipline this very harness enforces for file edits — Claude Code's own `Edit` tool refuses to run until `Read` has been called on that file at least once in the session. We considered mirroring that literally: have the MCP server track, in memory, which parameter names have had `get_param` called on them, and refuse `set_param` for any name not in that set.

That approach breaks under the MCP server's own supported topology. ADR-0003 established that Streamable HTTP mode lets **one server process serve multiple concurrent agent sessions**. Session-name tracking would be process-global unless scoped per MCP session, and even then adds real statefulness to a server that is otherwise completely stateless (every existing tool is a pure passthrough to a bridge call, no memory between requests).

Instead, `expected_current_value` becomes a **required** field on `set_param`. The bridge compares it against the live value at write time and rejects on mismatch. This achieves the same practical goal — an agent cannot call `set_param` without having a real number in hand, which in practice means it called `get_param` first — without any server-side state at all. It also buys something session-tracking would not: genuine optimistic-concurrency protection. If the value changed between the agent's read and this write — from MP's own UI, from another agent, from the vehicle itself — the write is rejected instead of silently overwriting a value the agent never actually saw.

### Post-write verification is an independent second read, not a trust of the response

`set_param`'s response includes `applied_value` — the bridge's own re-read immediately after the MAVLink round-trip. But the tool description also instructs the agent to call `get_param` again afterward, as a fully separate HTTP round-trip, specifically to catch ArduPilot's silent-clamping behavior (the flight controller can accept an out-of-range `PARAM_SET` and ack with a clamped value rather than rejecting it). This is deliberately redundant with `applied_value`: the project currently ships with zero automated tests (an acknowledged gap — see `MCP_BRIDGE_DESIGN.md §9`), so an independent, separately-fetched confirmation is the closest thing to a safety net available for this change, and it also guards against a bug in the write handler's own self-reported value.

### Confirmation stays soft, but names the mechanism

Confirmation is enforced at the prompting layer only — `set_param`'s description tells the agent to propose the exact parameter, current value, and new value to the user, and to use Claude Code's `AskUserQuestion` tool specifically to gather explicit confirmation before calling `set_param`, rather than proceeding on an inferred "the user probably wants this." There is no protocol-level confirmation field (MCP has no first-class concept of "a human already approved this"), so this is a convention encoded in text, not code — consistent with how the read tools already rely on tool descriptions to shape agent behavior.

### Writes stay single-threaded

`McpBridgeServer`'s accept loop remains a single dedicated thread blocking on `HttpListener.GetContext()`. A write blocks that thread for up to ~2.8s (three retries at 700ms) while MP waits for a MAVLink ack. We chose not to move writes to the .NET thread pool. Writes are expected to be rare relative to reads, and the existing architecture (ADR-0001) already accepts sequential handling as the cost of a stateless dedicated-thread accept loop; a slow write serializing a few reads behind it for under three seconds is an acceptable cost, not a new one — the alternative introduces real complexity (a second thread touching `MAVLinkParamList` concurrently with the accept thread) for a problem that hasn't been observed.

## Consequences

**Positive:**

- The bridge remains "dumb" in the same sense as ADR-0001 originally established — it enforces only facts (`read_only`, existence, value match), never judgment calls (armed state, whether this is a good idea). All judgment stays in the agent, which is where the actual reasoning capability lives.
- `expected_current_value` solves read-before-write enforcement and optimistic concurrency with a single mechanism, at zero additional state, and works identically in stdio and HTTP transport modes.
- Silent clamping — a real ArduPilot behavior that would otherwise produce a confidently wrong "success" report from the agent — is caught by the mandatory independent re-read.
- No architecture changes: no new threads, no new locks beyond the existing `ReaderWriterLock` already used for reads, no new MP-side concurrency to reason about.

**Negative:**

- `expected_current_value` adds friction: an agent literally cannot call `set_param` after a stale read (e.g., minutes/tool-calls later) without re-fetching, even in the common case where nothing actually changed. This is accepted as the cost of both concurrency safety and read-before-write enforcement.
- Confirmation-via-prompting is unenforceable in any strict sense — an agent (or a differently-behaved MCP client) can ignore the tool description's instruction to confirm with the user. This system has no mechanism to prevent that; it can only ask clearly.
- A write's ~2.8s worst-case blocks all other bridge traffic for that duration. Under concurrent agents (HTTP transport mode) this could be noticeable, though not incorrect.
- `armed` is informational only. Nothing stops an agent from writing a parameter while armed if its own judgment (or a user's explicit override) says to proceed. The system deliberately does not protect against this at the bridge level.

**Neutral:**

- If real-world usage shows agents routinely ignoring the armed/confirm guidance in tool descriptions, that's the signal to revisit bridge-side enforcement — this ADR's position is a starting point, not a permanent commitment. Re-open as its own ADR if that happens.

## Alternatives considered

**Hard `confirm: true` parameter on `set_param`.** Rejected because it doesn't actually verify a human saw anything — an agent can pass `confirm: true` without having asked the user at all, exactly as easily as it can skip a prose instruction. It adds schema friction without adding real enforcement, which is worse than no enforcement plus a clear instruction: it creates a false sense of a safety gate that isn't one.

**Two-phase `propose_param_change` / `commit_param_change` with a token.** Attractive on paper — it creates a natural place to show range/reboot_required/current-vs-proposed before committing, and a token-based commit step feels more "real" than a boolean flag. Rejected for MVP because it doubles the tool surface for a guarantee it doesn't actually provide: nothing stops an agent from calling `propose` then immediately `commit` without any human in the loop, same as the `confirm:true` case. It also adds real implementation cost (token generation, token expiry, matching commit to a specific proposal) for a UX improvement that a good tool description can approximate today. Revisit if usage shows the extra structure would meaningfully change agent behavior, not just add ceremony.

**Thread-pool writes (`BeginGetContext` or offloading writes to `Task.Run`).** Rejected because it introduces a second thread touching `MAVLinkParamList` and MP's MAVLink send/receive plumbing concurrently with the accept-loop thread, for a problem (a few seconds of serialized reads during an infrequent write) that isn't a measured issue. Simpler is better until proven otherwise.

**Session-tracked read-before-write.** Rejected because the MCP server can serve multiple concurrent sessions in HTTP transport mode (ADR-0003), and any "have I read this name" tracking either leaks across sessions (process-global) or requires building real per-session state management the server has never needed before. `expected_current_value` achieves the same practical goal statelessly.

**Bridge-side armed-state gating (refuse writes while `armed`).** Rejected because it doesn't match ArduPilot's actual risk model — most parameters require a reboot to take effect regardless of arm state, so blocking on `armed` would create a false sense of safety for the parameters that need it least, while doing nothing extra for the ones that apply immediately (those are already covered by `reboot_required: false` being visible to the agent via `get_param`). Moved to the prompting layer: `armed` is exposed as data, not enforced as a rule.

**Curated allowlist of "safe" parameter families for the first version.** Rejected — the pdef `read_only` flag already exists and is the correct, ArduPilot-maintained signal for "this must not be written directly." A hand-maintained allowlist would duplicate that signal with a second, bridge-fork-owned list that inevitably drifts from upstream pdef changes (we've already seen ArduPilot rename and restructure parameters across firmware versions in the upstream merge — see the 4.7 `PSC_POSZ_P` → `PSC_D_POS_P` rename). Trusting `read_only` keeps the bridge's notion of "writable" in sync with whatever ArduPilot firmware the connected vehicle actually runs.
