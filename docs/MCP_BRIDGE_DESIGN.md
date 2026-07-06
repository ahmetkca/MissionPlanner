# Mission Planner ↔ MCP Bridge — Design

> **Status:** MVP / POC — read + single-parameter write.
> **Audience:** developers implementing the bridge on either side.
> **Scope:** the HTTP contract Mission Planner exposes, and the MCP tool/resource surface a separate TypeScript MCP server presents to AI coding agents (Claude Code, Codex, etc.).

---

## 1. Goals and non-goals

### Goals (MVP)

- Allow an AI coding agent on the **same machine** as a running Mission Planner to:
  - Ask whether MP is connected to a MAVLink COM port.
  - List the full parameter set of the currently-selected vehicle (lightweight values + types).
  - Look up a single parameter by name with full metadata (description, units, range, enum values, bitmask, reboot-required, etc.).
  - Search/filter parameters by name or text.
- Keep MP and the MCP server **decoupled** so each can evolve and release independently.
- Survive MP restarts cleanly: the bridge port is stable; the MCP server can reconnect.
- Never destabilize MP — bridge failures must not crash the GUI or the MAVLink stack.

### Non-goals (MVP)

- **Multiple MAVs.** Currently-selected vehicle only.
- **Authentication / TLS.** `127.0.0.1` only, trusted local environment.
- **Live update streams.** Polling is fine for POC.
- **Plugin packaging.** The bridge is built into MP source on a fork branch.
- **Cross-machine access.** Same-host only; no LAN exposure.

---

## 2. Architecture overview

```
+------------------------------+      +-------------------------------+      +------------------------+
|  Mission Planner (C#, net472)|      |  MCP server (TypeScript,      |      |  AI coding agent       |
|  MissionPlanner.exe          |<---->|  Node 20+)                    |<---->|  (Claude Code / Codex) |
|                              |      |                               |      |                        |
|  - In-process HTTP bridge    | HTTP |  - Translates HTTP -> MCP     | stdio|  = MCP client          |
|    (HttpListener)            | JSON |  - Passes through metadata    |  or  |                        |
|  - Reads MAVLinkParamList    |      |    from MP bridge             | HTTP |                        |
|    under reader lock         |      |  - Validates bridge_api_      |      |                        |
|  - Reads MAV connection state|      |    version on startup         |      |                        |
|  - Reads pdef metadata via   |      |                               |      |                        |
|    ParameterMetaDataRepo     |      |                               |      |                        |
|                              |      |                               |      |                        |
|  Bound: 127.0.0.1:9999       |      |  stdio (default) OR           |      |                        |
|                              |      |  Streamable HTTP on :9990     |      |                        |
+------------------------------+      +-------------------------------+      +------------------------+
       (started by user)                    (stdio: agent spawns via          (spawns MCP server via
                                             `npx`, one instance per           `cmd /c npx ...` for
                                             agent session.                    stdio mode, or config-
                                             HTTP: user runs manually,         ured with URL for HTTP
                                             shared across agents.)            mode.)
```

**Key design choice:** the HTTP API exposed by MP is **not MCP**. It is a small, MP-private JSON contract designed for what's convenient to emit from `MainV2.comPort.MAV.param`. The MCP server is the adapter that maps that contract onto MCP tools and resources. This means:

- Adding a new MCP tool that's a different cut of the same data does not require a MP rebuild.
- Renaming an MCP tool does not affect MP.
- Bumping MCP SDK versions does not affect MP.
- The HTTP contract changes only when we actually need new data from MP.

---

## 3. HTTP bridge contract (lives in Mission Planner)

### 3.1 Transport

- **Library:** `System.Net.HttpListener` (built into `net472`, zero NuGet dependencies).
- **Bind address:** `http://127.0.0.1:9999/` (fixed default for MVP).
- **Why 9999:** unassigned by IANA, well above the ephemeral range, easy to remember. Configurable later via `Settings.Instance` (e.g., `MCPBridgePort`) — not in MVP.
- **Encoding:** `application/json; charset=utf-8`. JSON only. UTF-8 only.
- **CORS:** none. Localhost trust model.
- **Methods:** `GET` for all read endpoints; `POST` for `/params/{name}` (writes, added in `0.2.0` — see §3.4).

### 3.2 Lifecycle (inside MP)

- **Start:** `MainV2.OnLoad` override, immediately after the existing `httpserver` startup (~line 3233). Follows the established pattern in the codebase. A startup failure (port in use, etc.) is **caught and logged** — MP must not abort.
- **Stop:** `MainV2_FormClosing`, alongside the existing `httpserver.Stop()` call (~line 2108). Graceful: stop accept loop, close listener.
- **Threading:** request handling runs on a single dedicated background thread blocking on `HttpListener.GetContext()` (see §10, "HttpListener accept loop pattern") — not the .NET thread pool. Handlers must **not** touch WinForms controls directly — only `MainV2.comPort` and the param list. Param list access takes the existing `ReaderWriterLock` on `MAVLinkParamList`. A `POST /params/{name}` write can block this thread for up to ~2.8s while MP waits for a MAVLink ack (`setParamAsync`'s 3×700ms retry); see `docs/adrs/0005-parameter-write-path.md` for why this is accepted rather than moved to the thread pool.
- **Per-request budget:** target < 50 ms p99 for read endpoints. Writes are the deliberate exception (above).

### 3.3 `bridge_api_version` field

Every `/status` response includes a `bridge_api_version` semver string. The MCP server validates it on startup and refuses to serve tools if incompatible.

- Patch bumps: bug fixes, no contract change.
- Minor bumps: additive (new fields, new endpoints).
- Major bumps: breaking — field removed/renamed, endpoint removed, response shape changed incompatibly.

MCP server declares a compatible range in its package (e.g., `"^0.1.0"` for "any 0.1.x" since we're pre-1.0). Mismatch → MCP server logs a clear error and reports it via `mp_status` to the agent.

**Initial value:** `0.1.0`. **Current value:** `0.2.0`, bumped for the `POST /params/{name}` write endpoint and the `armed` field on `/status` — see `docs/adrs/0005-parameter-write-path.md`.

### 3.4 Endpoints

#### `GET /status`

Always reachable (whether or not MAVLink is connected). The single endpoint the MCP server is allowed to call before the handshake completes.

**Response (200) — connected:**

```json
{
  "bridge_api_version": "0.2.0",
  "mission_planner_version": "1.3.83.0",
  "connected": true,
  "port": "COM3",
  "vehicle_type": "ArduCopter",
  "vehicle_firmware": "ArduCopter V4.5.7",
  "armed": false,
  "selected_sysid": 1,
  "selected_compid": 1,
  "params_loaded": 1247,
  "params_total": 1247
}
```

**Response (200) — not connected:**

```json
{
  "bridge_api_version": "0.2.0",
  "mission_planner_version": "1.3.83.0",
  "connected": false,
  "port": null,
  "vehicle_type": null,
  "vehicle_firmware": null,
  "armed": null,
  "selected_sysid": null,
  "selected_compid": null,
  "params_loaded": 0,
  "params_total": 0
}
```

**Field semantics:**

| Field | Source |
|---|---|
| `bridge_api_version` | hardcoded constant in the C# bridge module |
| `mission_planner_version` | `Application.ProductVersion` |
| `connected` | `MainV2.comPort.BaseStream.IsOpen` |
| `port` | `MainV2.comPort.BaseStream.PortName` (when applicable) |
| `vehicle_type` | derived from `MainV2.comPort.MAV.cs.firmware` |
| `vehicle_firmware` | from `AUTOPILOT_VERSION` / `STATUSTEXT` if available; null otherwise |
| `armed` | `MainV2.comPort.MAV.cs.armed` (`CurrentState.cs:1884`, from the heartbeat's `SAFETY_ARMED` bit); `null` when not connected. **Informational only — the bridge does not gate writes on this.** See `docs/adrs/0005-parameter-write-path.md`. |
| `selected_sysid` / `selected_compid` | `MainV2.comPort.sysidcurrent` / `compidcurrent` |
| `params_loaded` | `MainV2.comPort.MAV.param.TotalReceived` |
| `params_total` | `MainV2.comPort.MAV.param.TotalReported` |

`params_loaded < params_total` indicates the agent should retry shortly — parameters are still being downloaded from the vehicle.

#### `GET /params`

Lightweight dump of every parameter known to MP for the currently-selected MAV.

**Response (200) — connected:**

```json
{
  "vehicle_type": "ArduCopter",
  "params_loaded": 1247,
  "params_total": 1247,
  "params": [
    { "name": "ACRO_BAL_PITCH", "value": 1.0, "type": "MAV_PARAM_TYPE_REAL32" },
    { "name": "ACRO_BAL_ROLL",  "value": 1.0, "type": "MAV_PARAM_TYPE_REAL32" },
    { "name": "AHRS_ORIENTATION", "value": 0, "type": "MAV_PARAM_TYPE_INT8" }
  ]
}
```

**Response (503) — not connected:**

```json
{ "error": "not_connected", "message": "Mission Planner is not connected to a MAVLink COM port" }
```

**Field semantics:**

| Field | Source |
|---|---|
| `vehicle_type` | as above |
| `params_loaded` / `params_total` | as above |
| `params[].name` | `MAVLinkParam.Name` |
| `params[].value` | `MAVLinkParam.Value` (double; numeric in JSON) |
| `params[].type` | string form of `MAVLinkParam.Type` (`MAV_PARAM_TYPE_*`) |

**Notes:**

- **Intentionally lightweight** — no metadata, no description, no defaults. Use `GET /params/{name}` for full metadata on a specific parameter.
- Snapshot taken under the reader lock on `MAVLinkParamList`, then released before serialization. See §5.
- Order: insertion order (which is roughly the order params were received).

#### `GET /params/{name}`

Single-parameter live snapshot **with full metadata** from MP's pdef cache.

**Response (200) — found, metadata available:**

```json
{
  "vehicle_type": "ArduPlane",
  "name": "SERVO1_FUNCTION",
  "value": 4.0,
  "type": "INT16",
  "metadata_available": true,
  "display_name": "Servo output function",
  "description": "Function assigned to this servo. Setting this to Disabled(0) will setup this output for control by auto missions or MAVLink servo set commands. any other value will enable the corresponding function",
  "units": null,
  "unit_text": null,
  "range": null,
  "values": { "-1": "GPIO", "0": "Disabled", "4": "Aileron", "6": "MountPan" },
  "increment": null,
  "user": "Standard",
  "bitmask": null,
  "reboot_required": "True",
  "read_only": null,
  "volatile": null,
  "calibration": null
}
```

**Response (200) — found, metadata not available:**

When the parameter exists on the vehicle but has no pdef entry (e.g., custom/vendor params), all metadata fields are `null` and `metadata_available` is `false`. The value and type are still returned.

```json
{
  "vehicle_type": "ArduPlane",
  "name": "CUSTOM_PARAM",
  "value": 42.0,
  "type": "REAL32",
  "metadata_available": false,
  "display_name": null,
  "description": null,
  "units": null,
  "unit_text": null,
  "range": null,
  "values": null,
  "increment": null,
  "user": null,
  "bitmask": null,
  "reboot_required": null,
  "read_only": null,
  "volatile": null,
  "calibration": null
}
```

**Response (404) — not found:**

```json
{ "error": "not_found", "message": "Parameter 'XYZ' is not known to the connected vehicle" }
```

**Response (503) — not connected:** same shape as `/params`.

**Metadata field semantics:**

| Field | Type | Source | Example |
|---|---|---|---|
| `metadata_available` | `bool` | `true` if any metadata field is non-null | `true` |
| `display_name` | `string?` | pdef `humanName` attribute | `"Servo output function"` |
| `description` | `string?` | pdef `documentation` attribute | long text |
| `units` | `string?` | pdef `Units` field (short code) | `"m/s"`, `"degC"` |
| `unit_text` | `string?` | pdef `UnitText` field (human-readable) | `"meters per second"` |
| `range` | `{ min: number, max: number }?` | pdef `Range` field, parsed to numeric | `{ "min": 0.0, "max": 30.0 }` |
| `values` | `{ [code]: label }?` | pdef `<values>` children, parsed to map | `{ "0": "Disabled", "4": "Aileron" }` |
| `increment` | `string?` | pdef `Increment` field | `"0.1"` |
| `user` | `string?` | pdef `user` attribute | `"Standard"` or `"Advanced"` |
| `bitmask` | `{ [bit]: label }?` | pdef `Bitmask` field, parsed to map | `{ "0": "Roll", "1": "Pitch" }` |
| `reboot_required` | `string?` | pdef `RebootRequired` field | `"True"` |
| `read_only` | `string?` | pdef `ReadOnly` field | `"True"` |
| `volatile` | `string?` | pdef `Volatile` field | `"True"` |
| `calibration` | `string?` | pdef `Calibration` field/attribute | `"1"` |

**Notes:**

- Metadata is sourced from `ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData()` — MP's existing pdef XML cache. The bridge does **not** download or parse pdef files itself; it uses what MP already has loaded.
- `range.min` and `range.max` are always numeric (doubles), never strings.
- `values` and `bitmask` are key-value maps where keys are the numeric codes as strings and values are human-readable labels.
- The name path segment is matched **case-sensitive** against `MAVLinkParam.Name`. ArduPilot param names are conventionally upper-snake; agents should preserve case.
- Single-key indexer on `MAVLinkParamList` is already lock-protected (`ExtLibs/Mavlink/MAVLinkParamList.cs:20-67`). No extra locking needed for this endpoint.

#### `POST /params/{name}`

Write a single parameter value. Added in `0.2.0` — see `docs/adrs/0005-parameter-write-path.md` for the full design rationale.

**Request body:**

```json
{ "value": 0.15, "expected_current_value": 0.12 }
```

Both fields are required. `expected_current_value` must be the value most recently read for this parameter (via `GET /params/{name}`) — the bridge compares it against the live value and rejects the write on mismatch. This is the mechanism that enforces "read before write" without any server-side session state (see the ADR for why session tracking was rejected).

**Response (200) — success:**

```json
{
  "name": "ATC_RAT_PIT_P",
  "requested_value": 0.15,
  "applied_value": 0.15,
  "changed": true,
  "reboot_required": false
}
```

`applied_value` is re-read from the live `MAVLinkParamList` entry immediately after the MAVLink round-trip completes. It can differ from `requested_value` — ArduPilot sometimes clamps an out-of-range `PARAM_SET` to a valid value and acks with the clamped value rather than rejecting the write outright. Callers must not assume `applied_value == requested_value`.

**Error responses**, all using the standard envelope (§3.5) except 409, which adds a `current_value` field:

| Code | Error | Cause |
|---|---|---|
| 400 | `bad_request` | body missing, not valid JSON, or `value`/`expected_current_value` not numeric |
| 403 | `read_only` | pdef `ReadOnly` flag is `true` for this parameter — checked before any MAVLink traffic |
| 404 | `not_found` | parameter does not exist on the connected vehicle |
| 409 | `value_mismatch` | `expected_current_value` does not match the live value; response includes `current_value` (the actual live value) so the caller can re-fetch and retry |
| 503 | `not_connected` | no vehicle connected |
| 504 | `timeout` | `MAVLinkInterface.setParam`'s internal 3×700ms retry loop exhausted without an ack — **the outcome is ambiguous**, the write may have been applied with only the ack lost. Callers must re-fetch via `GET /params/{name}` rather than assume failure. |

**Field semantics:**

| Field | Source |
|---|---|
| `name` | echoed request parameter name |
| `requested_value` | echoed request `value` |
| `applied_value` | live `MAVLinkParam.Value` re-read after `port.setParam(...)` returns |
| `changed` | `applied_value` differs from the pre-write live value (float-tolerance compare, not exact equality — see below) |
| `reboot_required` | pdef `RebootRequired` metadata, coerced to `bool` (note: this is a `bool` here vs. a nullable `string` on `GET /params/{name}`'s `reboot_required` field — same underlying pdef fact, different wire representation between the two endpoints; not a bug) |

**Notes:**

- **No armed-state gating.** The bridge does not check `armed` before writing. Most ArduPilot parameters require a flight-controller reboot to take effect (`reboot_required` tells the agent this per-parameter), so `armed` is not a reliable signal for "this write is dangerous right now." See ADR-0005 for the full reasoning — this is a deliberate, not accidental, omission.
- **Only `read_only` restricts writability.** No curated allowlist of "safe" parameters exists; any parameter not flagged `read_only` in pdef can be written. If the vehicle type can't be resolved (no pdef data to check), the bridge fails open and allows the write.
- **Float comparison tolerance.** MAVLink `REAL32` parameters round-trip through 32-bit floats (`MAVLinkParam.GetValue()` rounds to 7 significant digits), so both the `expected_current_value` check and the `changed` computation use a small relative+absolute tolerance (`1e-6`) rather than exact `double` equality.
- **Blocks the bridge's single listener thread** for the duration of the MAVLink round-trip (up to ~2.8s worst case). See §3.2.
- **Written by `MAVLinkInterface.setParam(name, value)`** (`ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs:1623`), the existing synchronous wrapper around `setParamAsync`. Not new MP-side machinery — the bridge is the only new caller.

### 3.5 Error envelope

All non-2xx responses use:

```json
{ "error": "<machine-readable code>", "message": "<human-readable explanation>" }
```

Codes used:
- `not_connected` — 503
- `not_found` — 404
- `internal_error` — 500 (anything we didn't anticipate; logged via log4net with full stack)
- `bad_request` — 400 (malformed path/body, invalid name characters, non-numeric write fields)
- `read_only` — 403 (write attempted on a pdef-flagged read-only parameter; §3.4)
- `value_mismatch` — 409 (write's `expected_current_value` doesn't match the live value; §3.4)
- `timeout` — 504 (write's MAVLink ack never arrived; §3.4)

### 3.6 What's intentionally not in MVP

- `GET /params?prefix=...&changed=true` — server-side filtering. The MCP server handles all filtering.
- `GET /vehicles` — multi-MAV enumeration. MVP is single-vehicle.
- Server-Sent Events / WebSocket for live param updates. MVP is poll-only.
- `/health` distinct from `/status` — `/status` is the health check.

---

## 4. MCP server surface (lives in TypeScript MCP server)

### 4.1 Transport to the agent

The MCP server supports **two transports**, selected via a CLI flag:

- **`--transport stdio`** (default) — JSON-RPC over stdin/stdout. The agent (e.g., Claude Code) spawns the MCP server as a child process per agent session. This is the default everywhere MCP is documented.
- **`--transport http --port 9990`** — Streamable HTTP on `127.0.0.1:9990` (separate from the MP bridge port). The user starts the server manually; one server instance can be shared across multiple agent sessions and survives if the agent restarts. Useful for debugging with tools like `mcp-inspector`.

Both transports speak the same MCP surface. Only the framing differs.

**Why support both:**
- stdio is the zero-config default — the agent config just names the command, no port management, no orphaned processes.
- HTTP is the debug/dev-loop path, and an escape hatch for scenarios where multiple agents want to share a warmed-up process (bridge connection already validated, no startup latency).

**Distribution.** The MCP server is published to npm as `@ahmetkca/missionplanner-mcp-server` (see §7). The typical agent config invokes it via `npx`, e.g. for Claude Code on Windows:

```json
{
  "mcpServers": {
    "missionplanner": {
      "command": "cmd",
      "args": ["/c", "npx", "--yes", "@ahmetkca/missionplanner-mcp-server"]
    }
  }
}
```

**Windows gotchas** (observed during implementation, worth preserving for contributors):
- The entry point (`dist/index.js`) must have `#!/usr/bin/env node` as its first line, otherwise npm on Windows cannot generate a functional `.cmd` shim and the launcher fails with `'missionplanner-mcp-server' is not recognized as an internal or external command`.
- The agent must invoke `cmd /c npx ...` rather than `npx` directly — Claude Code on Windows does not resolve `.cmd` shims through PATHEXT without the `cmd /c` wrapper.
- `npx --yes` is used to auto-accept the first-install confirmation prompt, which would otherwise write to stdout and corrupt the JSON-RPC stream.

**Startup compatibility check.** On startup, the MCP server calls `GET /status` once, parses `bridge_api_version`, and compares against its declared range (`>=0.1.0 <1.0.0` pre-1.0). On mismatch **or** if the bridge is unreachable, the server **logs a warning to stderr and still starts** — individual tool calls then fail at invocation time with the specific error. This is softer than refusing to serve; the rationale is that an agent should be able to introspect `mp_status` to learn what's wrong, even when other tools aren't viable. See follow-up notes in §9.

### 4.2 Tools

#### `mp_status()`

Returns the same payload as `GET /status` from MP, plus a `bridge_compatible: bool` field computed by the MCP server from its own version range check.

**Example tool result:**

```json
{
  "bridge_compatible": true,
  "bridge_api_version": "0.2.0",
  "mission_planner_version": "1.3.83.0",
  "connected": true,
  "port": "COM3",
  "vehicle_type": "ArduCopter",
  "vehicle_firmware": "ArduCopter V4.5.7",
  "armed": false,
  "selected_sysid": 1,
  "selected_compid": 1,
  "params_loaded": 1247,
  "params_total": 1247
}
```

Agents should check `armed` before proposing a `set_param` call — see below.

If the MCP server cannot reach MP at all, returns:

```json
{ "bridge_compatible": false, "error": "mp_unreachable", "message": "..." }
```

#### `list_params(prefix?, limit?, cursor?)`

Structural browse over the full param set.

**Schema (JSON Schema-style):**

```json
{
  "prefix": { "type": "string", "description": "Filter by name prefix, case-sensitive (e.g., 'GPS_', 'SERVO1')" },
  "limit": { "type": "integer", "minimum": 1, "maximum": 500, "default": 100 },
  "cursor": { "type": "string", "description": "Opaque (base64-encoded index) cursor from the previous response's next_cursor" }
}
```

**Returns:**

```json
{
  "vehicle_type": "ArduPlane",
  "params_loaded": 1132,
  "params_total": 1132,
  "filtered_count": 18,
  "params": [
    { "name": "ARSPD_PRIMARY", "value": 0, "type": "INT8" },
    { "name": "ARSPD_OPTIONS", "value": 11, "type": "INT32" }
  ],
  "next_cursor": "NQ=="
}
```

- `filtered_count` is the total number of parameters matching `prefix` (before pagination).
- `next_cursor` is `null` when the page is the last.
- Filtering and pagination happen entirely in the MCP server (the bridge always returns the full set).
- **No `default` field** — ArduPilot defaults are not available in pdef XML or via any reliable published source, so `changed_from_default` and `default` were dropped for MVP. See `docs/adrs/0004-drop-changed-from-default.md`.

#### `get_param(name)`

Full metadata for one parameter. The MCP server fetches `GET /params/{name}` from the MP bridge, which already includes all metadata from MP's pdef cache. The MCP server passes this through directly — no client-side metadata merging needed.

**Returns:** the same shape as the bridge's `GET /params/{name}` response (see §3.4 for full field list).

```json
{
  "vehicle_type": "ArduPlane",
  "name": "SERVO1_FUNCTION",
  "value": 4.0,
  "type": "INT16",
  "metadata_available": true,
  "display_name": "Servo output function",
  "description": "Function assigned to this servo...",
  "units": null,
  "unit_text": null,
  "range": null,
  "values": { "-1": "GPIO", "0": "Disabled", "4": "Aileron" },
  "increment": null,
  "user": "Standard",
  "bitmask": null,
  "reboot_required": "True",
  "read_only": null,
  "volatile": null,
  "calibration": null
}
```

#### `search_params(query)`

Text search across `name`, `display_name`, `description`, and `units` of all known parameters. The MCP server fetches `/params` for the full list, then fetches `/params/{name}` for each candidate to get metadata. Case-insensitive substring match.

**Returns:** same shape as `list_params`, but ranked: name matches first, then description matches.

#### `set_param(name, value, expected_current_value)`

Writes a single parameter via `POST /params/{name}`. All three arguments are required — `expected_current_value` must be the value most recently returned by `get_param` for this name, enforcing read-before-write without any server-side session state (see `docs/adrs/0005-parameter-write-path.md`).

The tool's description (not the protocol) instructs the calling agent to: call `get_param` first; check `mp_status`'s `armed` field and treat "armed" as a reason for extra caution (the bridge itself does not gate on this); propose the exact change to the user and get explicit confirmation — using `AskUserQuestion` when running in Claude Code — before calling; and, after a successful call, independently call `get_param` again to verify what was actually written, since ArduPilot can silently clamp an out-of-range value rather than reject it. None of this is protocol-enforced; it is convention encoded in the tool description, consistent with how the read tools already shape agent behavior through description text rather than schema constraints.

**Returns:** the same shape as the bridge's `POST /params/{name}` response (§3.4), plus an inline note appended to the text when `applied_value !== requested_value` (clamping detected).

```json
{
  "name": "ATC_RAT_PIT_P",
  "requested_value": 0.15,
  "applied_value": 0.15,
  "changed": true,
  "reboot_required": false
}
```

On `value_mismatch` (409) or `timeout` (504), the tool returns `isError: true` with a message telling the agent to re-fetch via `get_param` before retrying — it does not retry automatically.

### 4.3 Resources

All resources use a custom URI scheme `ardupilot-missionplanner://` so the origin is unambiguous in agent UIs that list resources from many servers.

- **`ardupilot-missionplanner://vehicle/params`** — single static resource. Body is the full lightweight param list (same shape as `GET /params` from the bridge, unpaginated). Useful for "load the entire param set into context" agent flows.
- **`ardupilot-missionplanner://vehicle/params/{name}`** — resource template. Resolving it returns the same payload as `get_param(name)` (full pdef metadata). Agents can construct URIs themselves; no enumeration needed.

The template resource also registers two discovery callbacks:
- **`list`** — enumerates all known parameters as resources (name, current value, type) so agents can browse without calling a tool first.
- **`complete` on the `{name}` parameter** — prefix autocomplete over known param names (top 20 matches, uppercase prefix). Lets agents drive a picker UX.

These are not the only way to read params — they are an alternative entry point for clients that prefer resource browsing over tool invocation.

---

## 5. Threading model in MP

### 5.1 Critical: `MainV2.comPort` can be reassigned

`MainV2.comPort` (`MainV2.cs:401`) is a **static property with a backing field that can be reassigned** during connect/disconnect (`:3018`, `:3753`). There is no synchronization around the setter. The HTTP bridge runs on a background thread and must **never** access `MainV2.comPort` more than once per request — capture it to a local variable immediately.

### 5.2 Thread-safety matrix (audited fields)

| Field | Thread-safe? | Risk | Access pattern |
|---|---|---|---|
| `MainV2.comPort` | **NO** — can be reassigned | NullRef if reassigned mid-request | Capture to local, null-check |
| `comPort.BaseStream` | **NO** — property can be null/swapped | NullRef if swapped mid-read | Capture to local after comPort, null-check |
| `BaseStream.IsOpen` | MEDIUM — depends on impl | Stale bool | Read once to local after null-check |
| `BaseStream.PortName` | MEDIUM — string ref | Stale string | Copy to local after null-check |
| `comPort.MAV` | PARTIAL — MAVList indexer locks internally | Wrong MAVState if sysid changes | Capture sysid/compid first, index once |
| `comPort.MAV.cs.firmware` | **YES** — enum, atomic on x86 | Stale value (acceptable) | Direct read from captured MAV |
| `comPort.MAV.VersionString` | **NO** — string ref can change | Stale string (acceptable) | Copy to local from captured MAV |
| `comPort.sysidcurrent/compidcurrent` | PARTIAL — atomic int, but changes anytime | Inconsistent if read separately | Capture both together before MAVList index |
| `comPort.MAV.param` | PARTIAL — has `ReaderWriterLock` | Concurrent mutation during enumerate | Use `Snapshot()` method (§5.3) |

### 5.3 Required defensive access pattern for every handler

```csharp
// 1. Capture comPort to local — never access the static property again
var port = MainV2.comPort;
if (port == null) { /* return 503 */ }

// 2. Capture BaseStream, check it's open
var bs = port.BaseStream;
bool connected = bs != null && bs.IsOpen;
// For /status: return connected=false. For /params, /params/{name}: return 503.

// 3. Capture sysid/compid TOGETHER (both can change independently)
int sysid = port.sysidcurrent;
int compid = port.compidcurrent;

// 4. Index MAVList once with captured IDs (indexer locks internally)
var mav = port.MAVlist[sysid, compid];
if (mav == null) { /* return 503 */ }

// 5. Copy string fields to locals
string versionStr = mav.VersionString ?? "";
string portName = bs?.PortName ?? "";
var firmware = mav.cs.firmware; // enum, atomic

// 6. For /params: snapshot the param list under reader lock
var paramsSnapshot = mav.param.Snapshot(); // new method, see below
```

### 5.4 `MAVLinkParamList.Snapshot()` method

Add a public method to `ExtLibs/Mavlink/MAVLinkParamList.cs` that returns a locked copy:

```csharp
public MAVLinkParam[] Snapshot()
{
    try
    {
        locker.AcquireReaderLock(1000);
        return this.ToArray();
    }
    finally
    {
        if (locker.IsReaderLockHeld)
            locker.ReleaseReaderLock();
    }
}
```

Mirrors the pattern used by the existing implicit `Dictionary` operator (`:175-193`). `GET /params/{name}` uses the existing string indexer, which is already lock-protected — no extra locking needed.

---

## 6. Lifecycle and failure handling in MP

### Startup

```csharp
// in MainV2.cs, OnLoad — right after existing httpserver startup (~line 3233)
try
{
    _mcpBridge = new McpBridgeServer("http://127.0.0.1:9999/");
    _mcpBridge.Start();
    log.Info("MCP bridge listening on http://127.0.0.1:9999/");
}
catch (Exception ex)
{
    log.Error("MCP bridge failed to start; continuing without bridge", ex);
    // do NOT rethrow — MP must remain usable
}
```

### Shutdown

```csharp
// in MainV2.cs, MainV2_FormClosing — alongside httpserver.Stop() (~line 2108)
try { _mcpBridge?.Stop(); } catch (Exception ex) { log.Warn("MCP bridge shutdown error", ex); }
```

### Per-request

- All exceptions caught, logged, returned as `{ error: "internal_error", message: "..." }` with HTTP 500.
- Never let a request handler exception propagate into the `HttpListener` accept loop.

### Port already in use

- Catch `HttpListenerException` (Windows error code 183 / 32). Log clear message: "Port 9999 is in use; MCP bridge disabled this session." Do not retry, do not pick another port (would defeat the "fixed port" contract).

### MAVLink not connected

- All endpoints except `/status` return 503 `{ error: "not_connected" }`.
- `/status` always succeeds (returns `connected: false`).

---

## 7. Versioning and the cross-repo handshake

### Repos

- **MissionPlanner fork:** `https://github.com/ahmetkca/MissionPlanner` — branch `feat/mcp-bridge`. Tagged with semver as we ship.
- **MCP server:** `https://github.com/ahmetkca/missionplanner-mcp-server`. Independently semver-tagged. Published to npm as `@ahmetkca/missionplanner-mcp-server` (current: `0.1.1`).

### What's versioned

- **`bridge_api_version`** — the contract in §3. This is the only field the MCP server checks at runtime.
- The two repos' own package versions (MP `Application.ProductVersion`, MCP server `package.json` version) are reported in `mp_status()` for human debugging but are NOT used for compatibility decisions.

### Bump rules

| Change | `bridge_api_version` bump |
|---|---|
| New optional field added to a response | minor (0.1.0 → 0.2.0 while pre-1.0; minor after 1.0) |
| New endpoint added | minor |
| Field removed | major |
| Field type changed | major |
| Endpoint removed or path changed | major |
| Bug fix, no contract change | patch |

### Version history

- **`0.1.0`** — initial read-only MVP (`GET /status`, `/params`, `/params/{name}`).
- **`0.2.0`** — adds `POST /params/{name}` (parameter writes) and the `armed` field on `GET /status`. New endpoint + new optional field, both additive → minor bump per the table above. See `docs/adrs/0005-parameter-write-path.md`.

### MCP server compatibility check

On startup the MCP server:

1. `GET /status` from MP.
2. Parses `bridge_api_version`.
3. Compares against its declared range (currently `>=0.1.0 <1.0.0`, i.e. "any 0.1.x pre-1.0; will tighten to caret-lock post-1.0").
4. On mismatch **or** bridge unreachable:
   - Logs a warning to stderr (both versions included).
   - **Continues to start** — the server does not refuse to register its tools.
   - `mp_status` surfaces the specific failure via `bridge_compatible: false` + `reason`.
   - Other tools (`list_params`, `get_param`, `search_params`) then fail at invocation time with the underlying HTTP/fetch error.

> **Known follow-up:** the DESIGN originally specified that incompatible tools should be pre-emptively refused with a clean "update one side" message rather than failing at call time. That stricter behavior is intentionally deferred — the current behavior is chosen so an agent can always introspect `mp_status` and learn what's wrong. Will revisit when we have real-world incompatibility scenarios to test against.

---

## 8. Metadata sourcing

### MP owns metadata

The MP bridge serves parameter metadata directly from MP's existing pdef cache (`ParameterMetaDataRepositoryAPMpdef`). MP downloads and caches `apm.pdef.xml.gz` files from `https://autotest.ardupilot.org/Parameters/{vehicle}/` on startup and refreshes them weekly. The bridge reads from this cache via `GetParameterMetaData()`.

**The MCP server does not download, parse, or cache pdef files.** It passes through whatever the bridge returns.

### Why MP owns it

- **Single source of truth** — MP already has the correct pdef for the connected vehicle and firmware version. No risk of version mismatch between MP and a separately-fetched pdef.
- **No duplicate work** — both processes don't need to download and parse the same XML.
- **Simpler MCP server** — the TS side is a thin translation layer, not a metadata engine.

### Data flow

`get_param(name)` flow:

```
agent
  → MCP server: get_param("SERVO1_FUNCTION")
  → MCP server: GET http://127.0.0.1:9999/params/SERVO1_FUNCTION
  ← MP bridge: { name, value, type, metadata_available, display_name, description, ... }
  ← agent: full record (passed through)
```

### Metadata fields

All 13 metadata fields from the pdef XML are exposed (see §3.4 for the full list):
`display_name`, `description`, `units`, `unit_text`, `range`, `values`, `increment`, `user`, `bitmask`, `reboot_required`, `read_only`, `volatile`, `calibration`.

### When metadata is unavailable

If the parameter exists on the vehicle but has no pdef entry (custom/vendor params, or pdef not yet downloaded), all metadata fields are `null` and `metadata_available` is `false`. The value and type are still returned — the bridge never fails a request due to missing metadata.

---

## 9. Resolved decisions and remaining notes

### Resolved

1. **C# bridge module location:** `McpBridge/` folder at repo root (mirrors `Plugin/`). SDK-style csproj auto-includes new files — no csproj edits needed.
2. **Thread-safe snapshot:** add a public `Snapshot()` method to `MAVLinkParamList` that acquires the reader lock, copies, and returns. Keeps locking encapsulated.
3. **Vehicle-type derivation:** static `Dictionary<Firmwares, string>` in the bridge module. Enum values from `ExtLibs/ArduPilot/Firmwares.cs`: `ArduPlane`→`"ArduPlane"`, `ArduCopter2`→`"ArduCopter"`, `ArduRover`→`"Rover"`, `ArduSub`→`"ArduSub"`, `ArduTracker`→`"AntennaTracker"`. Others → `null`.
4. **Port collision:** log and disable for MVP. No fallback ports, no discovery file.
5. **MCP transport:** Dual — stdio (default, agent-spawned) + Streamable HTTP (optional, user-run). See §4.1 and `docs/adrs/0003-dual-transport-stdio-http.md`.
6. **Lifecycle hooks:** `OnLoad` for startup (established pattern — existing `httpserver` starts there at ~line 3228). `FormClosing` for shutdown.
7. **JSON serialization:** `Newtonsoft.Json` 13.0.3 already in `MissionPlanner.csproj` and used across the codebase. No new dependencies.
8. **Implementation order:** C# HTTP bridge first (MP side), then TS MCP server. Bridge must exist for the MCP server to test against.
9. **`vehicle_firmware` sourcing:** `MAVState.VersionString` (already populated from `AUTOPILOT_VERSION` / `STATUSTEXT` during handshake). Verified against a live ArduPlane V4.6.3 connection.
10. **`BaseStream.PortName` availability:** confirmed present on all `ICommsSerial` implementations used by MP (CommsSerial, CommsFile, CommsTCP, CommsUDP). Non-COM implementations return a synthetic name (e.g., `"TCP"`) rather than null.
11. **Shutdown signaling:** `ManualResetEvent` + `WaitOne(100)` + `Application.DoEvents()` in `Stop()`, `_shutdownEvent.Set()` in accept-loop `finally`. Aligns with MP's codebase convention rather than a naked `Thread.Join(2000)`.
12. **Metadata exposure:** all 13 pdef fields surfaced (`display_name`, `description`, `units`, `unit_text`, `range`, `values`, `increment`, `user`, `bitmask`, `reboot_required`, `read_only`, `volatile`, `calibration`). `range.min/max` always numeric doubles. See §3.4.
13. **Defaults:** dropped from MVP — ArduPilot defaults are not in pdef XML, `defaults.parm` URLs don't exist on the ArduPilot CDN, and computing defaults from firmware source at runtime is out of scope. See `docs/adrs/0004-drop-changed-from-default.md`.
14. **Parameter writes:** shipped in `0.2.0` as `POST /params/{name}`. No bridge-side armed-state gating (moved to the prompting layer — `armed` is exposed on `/status` but not enforced); read-before-write enforced via a required `expected_current_value` field compared against the live value, rather than server-side session tracking (which would leak across concurrent sessions in HTTP transport mode); confirmation stays a soft, tool-description convention (`AskUserQuestion` named explicitly for Claude Code); writes remain single-threaded, blocking the bridge's one listener thread for up to ~2.8s. See `docs/adrs/0005-parameter-write-path.md` for the full reasoning and rejected alternatives.

### Remaining (post-MVP follow-ups)

- **Resource pagination.** The `ardupilot-missionplanner://vehicle/params` static resource currently returns all ~1132 params in one JSON body. Consider chunked resources or filtering.
- **Stricter compatibility gating.** See §7.
- **Test coverage.** The C# bridge has zero unit tests; the MCP server has zero unit tests. Need at least smoke tests against a mock bridge.
- **Multi-vehicle.** Everything assumes `MainV2.comPort.MAV` (single currently-selected vehicle). Expanding to `MAVlist[sysid,compid]` enumeration is a contract-breaking change.
- **Live updates.** Polling is fine for params (they change rarely); telemetry will need SSE / WebSocket.

---

## 10. Quick reference

### Bridge endpoints

| Method | Path | Purpose | When it works |
|---|---|---|---|
| `GET` | `/status` | Health + connection state + version handshake | always |
| `GET` | `/params` | Full param dump (lightweight) | only when connected |
| `GET` | `/params/{name}` | One param + full pdef metadata | only when connected |
| `POST` | `/params/{name}` | Write one parameter value | only when connected |

### MCP tools

| Tool | Purpose |
|---|---|
| `mp_status` | Connection + version + bridge compatibility + `armed` |
| `list_params` | Browse params with structural filters |
| `get_param` | Single param with full metadata (passed through from MP bridge) |
| `search_params` | Text search across name + description |
| `set_param` | Write one parameter value, guarded by `expected_current_value` |

### MCP resources

| URI | Shape |
|---|---|
| `ardupilot-missionplanner://vehicle/params` | Full lightweight param list (name, value, type) |
| `ardupilot-missionplanner://vehicle/params/{name}` | One param + full pdef metadata (template with `list` + `complete` callbacks) |

### Versions in play

| Component | Versioned how |
|---|---|
| `bridge_api_version` | Hardcoded constant in MP, validated by MCP server. Drives compatibility. |
| MP `Application.ProductVersion` | Reported in `/status` for debugging. Not used for compatibility. |
| MCP server `package.json` version | Reported by the MCP server. Not used for compatibility. |

### Implementation reference (key locations in MP codebase)

| What | Where |
|---|---|
| `comPort` static property | `MainV2.cs:401` — backing field `_comPort`, can be reassigned |
| Existing httpserver start | `MainV2.cs:~3228` inside `OnLoad` — follow same pattern for bridge |
| Existing httpserver stop | `MainV2.cs:~2108` inside `FormClosing` — add bridge stop alongside |
| `MAVLinkParamList` class | `ExtLibs/Mavlink/MAVLinkParamList.cs` — add `Snapshot()` method |
| `Firmwares` enum | `ExtLibs/ArduPilot/Firmwares.cs:6-18` |
| `MAVState.VersionString` | `ExtLibs/ArduPilot/Mavlink/MAVState.cs:123` (auto-property) |
| `MAVLinkInterface.sysidcurrent` | `ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs:289-315` |
| `Newtonsoft.Json` PackageRef | `MissionPlanner.csproj:230` (already present, v13.0.3) |
| `PortName` on all ICommsSerial | Verified: exists on all impls (CommsFile, CommsTCP, CommsUDP, etc.) |

### Firmwares enum → vehicle type string mapping

```
ArduPlane    → "ArduPlane"
ArduCopter2  → "ArduCopter"
ArduRover    → "Rover"
ArduSub      → "ArduSub"
ArduTracker  → "AntennaTracker"
Ateryx       → null
Gimbal       → null
PX4          → null
Other        → null
AP_Periph    → null
```

### HttpListener accept loop pattern (shipped)

- **Blocking `GetContext()`** on a dedicated `Thread` with `IsBackground = true`.
- **Shutdown:** `_running = false` → `_listener.Stop()` / `Close()` → `_shutdownEvent.WaitOne(100)` loop with `Application.DoEvents()` → `Thread.Join()`. The `ManualResetEvent` is set in the accept loop's `finally` block. Rationale: matches MP's codebase convention for cooperative background-thread shutdown, keeps the UI responsive during teardown.
- Catches `HttpListenerException` code 995 (interrupted) + `ObjectDisposedException` in the accept loop.
- All handler exceptions caught → return HTTP 500 → never crash MP.
- Full skeleton: see `docs/HTTPLISTENER_FEASIBILITY.md`.

### Files created/modified (MVP, shipped on branch `feat/mcp-bridge`)

| Action | File | What |
|---|---|---|
| **Created** | `McpBridge/McpBridgeServer.cs` | HttpListener server + three route handlers + metadata enrichment via `ParameterMetaDataRepositoryAPMpdef` |
| **Modified** | `ExtLibs/Mavlink/MAVLinkParamList.cs` | Added `Snapshot()` method |
| **Modified** | `MainV2.cs` | Start bridge in `OnLoad` alongside existing httpserver, stop in `FormClosing` |

### Files created/modified (`0.2.0` — parameter writes)

| Action | File | What |
|---|---|---|
| **Modified** | `McpBridge/McpBridgeServer.cs` | `POST /params/{name}` handler, `armed` field on `/status`, `BridgeApiVersion` bump to `0.2.0` |

### MCP server files (shipped in `ahmetkca/missionplanner-mcp-server`, published as `@ahmetkca/missionplanner-mcp-server@0.1.1`)

| File | What |
|---|---|
| `src/index.ts` | Entry point — CLI parsing, dual-transport startup (stdio / Streamable HTTP), graceful shutdown |
| `src/server.ts` | `McpServer` factory — wires tools + resources to the `BridgeClient` |
| `src/bridge-client.ts` | Typed HTTP client for the MP bridge — `getStatus`, `getParams`, `getParam`, `checkCompatibility` |
| `src/semver.ts` | Minimal semver parse + range check (no runtime dependency) |
| `src/tools/mp-status.ts` | `mp_status` tool |
| `src/tools/list-params.ts` | `list_params` tool (prefix filter + base64-cursor pagination) |
| `src/tools/get-param.ts` | `get_param` tool (passthrough to bridge) |
| `src/tools/search-params.ts` | `search_params` tool (name substring → metadata fetch → description search) |
| `src/tools/set-param.ts` | `set_param` tool — writes one parameter, guarded by `expected_current_value` (added `0.2.0`) |
| `src/resources/params-current.ts` | Static resource `ardupilot-missionplanner://vehicle/params` |
| `src/resources/param-by-name.ts` | Resource template `ardupilot-missionplanner://vehicle/params/{name}` + `list` + `complete` callbacks |
