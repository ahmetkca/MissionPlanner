# Mission Planner ↔ MCP Bridge — Design

> **Status:** MVP / POC — read-only.
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

- **Writes** (`PARAM_SET`). Designed for, not enabled.
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
|  MissionPlanner.exe          |<---->|  Node 24)                     |<---->|  (Claude Code / Codex) |
|                              |      |                               |      |                        |
|  - In-process HTTP bridge    | HTTP |  - Translates HTTP -> MCP     |  MCP |  = MCP client          |
|    (HttpListener)            | JSON |  - Passes through metadata    | over |                        |
|  - Reads MAVLinkParamList    |      |    from MP bridge             | HTTP |                        |
|    under reader lock         |      |  - Validates bridge_api_      |      |                        |
|  - Reads MAV connection state|      |    version on startup         |      |                        |
|  - Reads pdef metadata via   |      |                               |      |                        |
|    ParameterMetaDataRepo     |      |                               |      |                        |
|                              |      |                               |      |                        |
|  Bound: 127.0.0.1:9999       |      |  Bound: 127.0.0.1:9990        |      |                        |
+------------------------------+      +-------------------------------+      +------------------------+
       (started by user)                    (started by user, separate         (started by user;
                                             process; survives MP restart)      configured w/ URL of
                                                                                MCP server)
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
- **Methods:** `GET` only in MVP.

### 3.2 Lifecycle (inside MP)

- **Start:** `MainV2.OnLoad` override, immediately after the existing `httpserver` startup (~line 3233). Follows the established pattern in the codebase. A startup failure (port in use, etc.) is **caught and logged** — MP must not abort.
- **Stop:** `MainV2_FormClosing`, alongside the existing `httpserver.Stop()` call (~line 2108). Graceful: stop accept loop, close listener.
- **Threading:** request handling runs on the .NET thread pool (`HttpListener.BeginGetContext` + callback). Handlers must **not** touch WinForms controls directly — only `MainV2.comPort` and the param list. Param list access takes the existing `ReaderWriterLock` on `MAVLinkParamList`.
- **Per-request budget:** target < 50 ms p99 even for full `/params` snapshot.

### 3.3 `bridge_api_version` field

Every `/status` response includes a `bridge_api_version` semver string. The MCP server validates it on startup and refuses to serve tools if incompatible.

- Patch bumps: bug fixes, no contract change.
- Minor bumps: additive (new fields, new endpoints).
- Major bumps: breaking — field removed/renamed, endpoint removed, response shape changed incompatibly.

MCP server declares a compatible range in its package (e.g., `"^0.1.0"` for "any 0.1.x" since we're pre-1.0). Mismatch → MCP server logs a clear error and reports it via `mp_status` to the agent.

**Initial value:** `0.1.0`.

### 3.4 Endpoints

#### `GET /status`

Always reachable (whether or not MAVLink is connected). The single endpoint the MCP server is allowed to call before the handshake completes.

**Response (200) — connected:**

```json
{
  "bridge_api_version": "0.1.0",
  "mission_planner_version": "1.3.83.0",
  "connected": true,
  "port": "COM3",
  "vehicle_type": "ArduCopter",
  "vehicle_firmware": "ArduCopter V4.5.7",
  "selected_sysid": 1,
  "selected_compid": 1,
  "params_loaded": 1247,
  "params_total": 1247
}
```

**Response (200) — not connected:**

```json
{
  "bridge_api_version": "0.1.0",
  "mission_planner_version": "1.3.83.0",
  "connected": false,
  "port": null,
  "vehicle_type": null,
  "vehicle_firmware": null,
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

### 3.5 Error envelope

All non-2xx responses use:

```json
{ "error": "<machine-readable code>", "message": "<human-readable explanation>" }
```

Codes used in MVP:
- `not_connected` — 503
- `not_found` — 404
- `internal_error` — 500 (anything we didn't anticipate; logged via log4net with full stack)
- `bad_request` — 400 (malformed path, invalid name characters)

### 3.6 What's intentionally not in MVP

- `POST /params/{name}` — write. Reserved for v0.2.x.
- `GET /params?prefix=...&changed=true` — server-side filtering. The MCP server handles all filtering.
- `GET /vehicles` — multi-MAV enumeration. MVP is single-vehicle.
- Server-Sent Events / WebSocket for live param updates. MVP is poll-only.
- `/health` distinct from `/status` — `/status` is the health check.

---

## 4. MCP server surface (lives in TypeScript MCP server)

### 4.1 Transport to the agent

- **Streamable HTTP** on `127.0.0.1:9990` (separate from the MP bridge port).
- The agent is configured with the URL — neither MP nor the agent spawn the MCP server.

### 4.2 Tools

#### `mp_status()`

Returns the same payload as `GET /status` from MP, plus a `bridge_compatible: bool` field computed by the MCP server from its own version range check.

**Example tool result:**

```json
{
  "bridge_compatible": true,
  "bridge_api_version": "0.1.0",
  "mission_planner_version": "1.3.83.0",
  "connected": true,
  "port": "COM3",
  "vehicle_type": "ArduCopter",
  "vehicle_firmware": "ArduCopter V4.5.7",
  "selected_sysid": 1,
  "selected_compid": 1,
  "params_loaded": 1247,
  "params_total": 1247
}
```

If the MCP server cannot reach MP at all, returns:

```json
{ "bridge_compatible": false, "error": "mp_unreachable", "message": "..." }
```

#### `list_params(prefix?, changed_from_default?, limit?, cursor?)`

Structural browse over the full param set.

**Schema (JSON Schema-style):**

```json
{
  "prefix": { "type": "string", "description": "Filter by name prefix (e.g., 'GPS_')" },
  "changed_from_default": { "type": "boolean", "description": "Only return params whose value differs from default" },
  "limit": { "type": "integer", "minimum": 1, "maximum": 500, "default": 100 },
  "cursor": { "type": "string", "description": "Opaque cursor from the previous response" }
}
```

**Returns:** `{ params: [{ name, value, type, default? }], next_cursor: string | null }`

- `default` is included when known from metadata; otherwise omitted.
- Filtering and pagination happen entirely in the MCP server (the bridge always returns the full set).

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

### 4.3 Resources

- **`params://current`** — single resource. Body is the full result of `list_params()` with no filter. Useful for "load the entire param set into context" agent flows.
- **`param://{name}`** — resource template. Resolving it returns the same payload as `get_param(name)`. Agents can construct URIs themselves; no enumeration needed.

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
- **MCP server:** `https://github.com/ahmetkca/missionplanner-mcp-server` (TBD). Independently semver-tagged.

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

### MCP server compatibility check

On startup the MCP server:

1. `GET /status` from MP.
2. Parses `bridge_api_version`.
3. Compares against its declared range (e.g., `"^0.1.0"` while pre-1.0 — note that npm/semver treats `^0.x.y` as caret-locked to `0.x.*`, so this means "any 0.1.x").
4. If incompatible:
   - Logs a clear error including both versions.
   - Continues to serve `mp_status` (which surfaces `bridge_compatible: false`).
   - **Refuses** to serve `list_params`, `get_param`, `search_params` — those tools return an error result instructing the agent to update one side.

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
5. **MCP transport:** Streamable HTTP. Will verify Claude Code config syntax when we reach TS integration.
6. **Lifecycle hooks:** `OnLoad` for startup (established pattern — existing `httpserver` starts there at ~line 3228). `FormClosing` for shutdown.
7. **JSON serialization:** `Newtonsoft.Json` 13.0.3 already in `MissionPlanner.csproj` and used across the codebase. No new dependencies.
8. **Implementation order:** C# HTTP bridge first (MP side), then TS MCP server. Bridge must exist for the MCP server to test against.

### Remaining (to address during implementation)

- Exact `vehicle_firmware` field sourcing — need to locate where MP stores the `AUTOPILOT_VERSION` response text. May simplify to `null` for MVP if it's hard to reach.
- `BaseStream.PortName` availability — confirm this property exists on all `ICommsSerial` implementations MP uses (TCP, UDP serial don't have a COM port name).

---

## 10. Quick reference

### Bridge endpoints

| Method | Path | Purpose | When it works |
|---|---|---|---|
| `GET` | `/status` | Health + connection state + version handshake | always |
| `GET` | `/params` | Full param dump (lightweight) | only when connected |
| `GET` | `/params/{name}` | One param + full pdef metadata | only when connected |

### MCP tools

| Tool | Purpose |
|---|---|
| `mp_status` | Connection + version + bridge compatibility |
| `list_params` | Browse params with structural filters |
| `get_param` | Single param with full metadata (passed through from MP bridge) |
| `search_params` | Text search across name + description |

### MCP resources

| URI | Shape |
|---|---|
| `params://current` | Full param list |
| `param://{name}` | One param + metadata |

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

### HttpListener accept loop pattern (decided)

- **Blocking `GetContext()`** on a dedicated `Thread` with `IsBackground = true`
- **Shutdown:** set `_running = false` → `Stop()` → `Close()` → `Join(2000)`
- Catch `HttpListenerException` code 995 (interrupted) + `ObjectDisposedException` in accept loop
- All handler exceptions caught → return HTTP 500 → never crash MP
- Full skeleton: see `docs/HTTPLISTENER_FEASIBILITY.md`

### Files to create/modify for implementation

| Action | File | What |
|---|---|---|
| **Create** | `McpBridge/McpBridgeServer.cs` | HttpListener server + route handlers |
| **Modify** | `ExtLibs/Mavlink/MAVLinkParamList.cs` | Add `Snapshot()` method (~8 lines) |
| **Modify** | `MainV2.cs` | Add field, start in `OnLoad` (~line 3234), stop in `FormClosing` (~line 2108) |
