# Architecture

> **Audience:** contributors new to the project, and future-us after the details have faded.
> **Scope:** the full system — what we're building, why, how the parts fit, and where things are going.
> **This doc is a hub.** It explains the shape; linked docs explain the contracts.

---

## What this is

This fork of Mission Planner adds a narrow HTTP bridge that lets AI coding agents (Claude Code, Codex, etc.) read live vehicle state directly from MP's process memory — starting with the full parameter set and its pdef metadata.

The bridge is not a general-purpose API. It exists to serve one use case: an AI agent running on the same machine as MP that needs structured, accurate context about the connected vehicle in order to help a developer tune, diagnose, or extend an ArduPilot-based system.

---

## System diagram

```
┌──────────────────────────────────────────┐
│  Mission Planner  (this fork)            │
│                                          │
│  UI thread (WinForms)                    │
│  MAVLink receive thread                  │
│    └─ writes MAVLinkParamList            │
│       (guarded by ReaderWriterLock)      │
│                                          │
│  MCP bridge thread  ←──────────────────┐ │
│    HttpListener on 127.0.0.1:9999      │ │
│    ├─ GET /status                      │ │
│    ├─ GET /params                      │ │
│    └─ GET /params/{name}               │ │
│                                        │ │
│  pdef cache  ──────────────────────────┘ │
│    ParameterMetaDataRepositoryAPMpdef    │
│    (keyed by vehicle type + param name)  │
└──────────────────────────────────────────┘
          ▲  HTTP/JSON (loopback only)
          │
┌─────────┴────────────────────────────────┐
│  @ahmetkca/missionplanner-mcp-server     │
│  (separate repo, published to npm)       │
│                                          │
│  Thin adapter: bridge HTTP → MCP         │
│  Transports:                             │
│    stdio  (default, agent-spawned)       │
│    Streamable HTTP on :9990 (optional)   │
│                                          │
│  Tools:  mp_status, list_params,         │
│          get_param, search_params        │
│  Resources: ardupilot-missionplanner://  │
└──────────────────────────────────────────┘
          ▲  MCP (stdio or Streamable HTTP)
          │
┌─────────┴────────────────────────────────┐
│  AI coding agent                         │
│  (Claude Code, Codex, etc.)              │
└──────────────────────────────────────────┘
```

**Key design choice:** the HTTP API is *not MCP*. It is a minimal MP-private JSON contract. The MCP server is the adapter. This means:

- New MCP tools that are different cuts of the same data don't require rebuilding MP.
- Bumping the MCP SDK version doesn't affect MP.
- The HTTP contract only changes when we need genuinely new data from MP's process.

---

## The two repos

| Repo | What lives there |
|---|---|
| [`ahmetkca/MissionPlanner`](https://github.com/ahmetkca/MissionPlanner) (`feat/mcp-bridge`) | C# bridge inside MP: `McpBridge/McpBridgeServer.cs`, lifecycle hooks in `MainV2.cs`, `Snapshot()` on `MAVLinkParamList`. Also: all docs (this file, `MCP_BRIDGE_DESIGN.md`, ADRs). |
| [`ahmetkca/missionplanner-mcp-server`](https://github.com/ahmetkca/missionplanner-mcp-server) | TypeScript MCP server, published as `@ahmetkca/missionplanner-mcp-server` on npm. Thin adapter — no pdef parsing, no param storage, no state. |

The repos are intentionally separate. MP is a fork of a large upstream project; keeping the MCP server in its own repo lets it version, release, and be installed independently. The hub for architectural documentation is this repo (the MP fork) — the MCP server README links here.

---

## Bridge module (MP side)

```
McpBridge/
└─ McpBridgeServer.cs          ← HttpListener + three route handlers
                                  + metadata enrichment via ParameterMetaDataRepositoryAPMpdef
```

MP changes are surgical and additive:

| File | Change |
|---|---|
| `McpBridge/McpBridgeServer.cs` | New file — the entire bridge |
| `ExtLibs/Mavlink/MAVLinkParamList.cs` | Added `Snapshot()` — locked copy of the param list |
| `MainV2.cs` | `_mcpBridge.Start()` in `OnLoad`, `_mcpBridge.Stop()` in `FormClosing` |

The bridge has zero new NuGet dependencies. `Newtonsoft.Json` and `HttpListener` are already present in the net472 MP codebase. See `MCP_BRIDGE_DESIGN.md §10` for exact line references.

### Threading model (brief)

The bridge runs a dedicated background thread that blocks on `HttpListener.GetContext()`. Each request handler must:

1. Capture `MainV2.comPort` to a local once — it is a static field that can be reassigned during connect/disconnect with no synchronization.
2. Read `MAVLinkParamList` via `Snapshot()` (full list) or the string indexer (single param) — both take the existing `ReaderWriterLock` for reading.
3. Never touch WinForms controls.

The full threading rationale and the defensively-correct access pattern are in `MCP_BRIDGE_DESIGN.md §5`.

---

## Contract between the two repos

The HTTP contract — endpoint paths, request/response shapes, error codes, the `bridge_api_version` semver field, and the versioning bump rules — is specified in `MCP_BRIDGE_DESIGN.md §3–§7`.

The short version:

- `bridge_api_version` is the only version field the MCP server checks at runtime. It is a semver string hardcoded in `McpBridgeServer.BridgeApiVersion`. Patch bumps are bug-fixes; minor bumps are additive; major bumps are breaking.
- A mismatched or unreachable bridge doesn't prevent the MCP server from starting. It degrades gracefully: `mp_status` reports the failure; other tools fail at call time with a clear message.

**If you are changing either side's contract:** read `docs/adrs/README.md` to determine whether your change warrants an ADR, and read `MCP_BRIDGE_DESIGN.md §7` for the bump rules.

---

## Load-bearing decisions

The ADRs in `docs/adrs/` document the four choices that are hardest to reverse. Read them before proposing changes that touch the HTTP contract, the bridge module structure, or the MCP tool surface.

| ADR | Decision |
|---|---|
| [0001](adrs/0001-in-process-http-bridge.md) | The bridge runs in-process inside MP (not a sidecar or plugin). Rationale: direct memory access to pdef cache + param list, no IPC, no drift. |
| [0002](adrs/0002-mp-owns-metadata.md) | MP's pdef cache is the authoritative source of parameter metadata. The MCP server does no XML parsing. Rationale: single source of truth, correct vehicle-type keying, no duplicate pipeline. |
| [0003](adrs/0003-dual-transport-stdio-http.md) | The MCP server ships both stdio and Streamable HTTP. Rationale: stdio is the zero-config agent default; HTTP is the debug/dev-loop path. |
| [0004](adrs/0004-drop-changed-from-default.md) | `default` and `changed_from_default` are not in the MVP contract. Rationale: ArduPilot parameter defaults depend on frame class and compile flags — pdef XML values are unreliable. A wrong `default` field is worse than no field. |

---

## Data flow

### `get_param("ATC_RAT_PIT_P")`

```
AI agent
  →  MCP server: invoke get_param tool
  →  GET http://127.0.0.1:9999/params/ATC_RAT_PIT_P
     ├─ bridge reads mav.param["ATC_RAT_PIT_P"] under reader lock
     └─ bridge calls ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(
            "ATC_RAT_PIT_P", <each_key>, "ArduCopter")
            for 13 metadata fields
  ←  HTTP 200 JSON: {name, value, type, metadata_available,
                      display_name, description, units, range,
                      values, bitmask, reboot_required, ...}
  ←  MCP server passes through to agent verbatim
  ←  AI agent receives full parameter context
```

### `list_params(prefix: "ATC_", limit: 50)`

```
AI agent
  →  MCP server: invoke list_params tool
  →  GET http://127.0.0.1:9999/params
     └─ bridge calls mav.param.Snapshot() under reader lock
        returns all N params as a flat array
  ←  HTTP 200 JSON: [{name, value, type}, ...]
  ←  MCP server filters by prefix "ATC_", paginates to 50,
     computes base64 cursor for next page
  ←  AI agent receives page of matching params
```

---

## Running the system

Both processes must be running on the same machine.

**Mission Planner** — build from source on this branch:

```
docs/BUILD_WINDOWS.md    ← prerequisites, MSBuild command, known gotchas
```

The bridge starts automatically when MP opens and stops when MP closes. No extra configuration.

**MCP server** — see the MCP server README for install, agent configuration (Claude Code / other clients), and run modes. The short version for Claude Code on Windows:

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

---

## Roadmap

### Near-term (concrete, scoped)

- **Parameter writes.** `POST /params/{name}` → `PARAM_SET` MAVLink message → wait for `PARAM_VALUE` acknowledgement → return new value. Contract designed in `MCP_BRIDGE_DESIGN.md §3.6`; not enabled in MVP. Requires the MCP server to add a `set_param` tool.
- **Reliable test coverage.** Zero tests today on either side. Minimum target: the bridge handlers against a stub MP (via `ICommsSerial` mock), and the MCP server tools against a stub HTTP bridge.
- **Stricter compatibility gating.** Currently the MCP server warns but still starts on bridge mismatch. Post-MVP: pre-emptively refuse incompatible tools with a "update MP / update the server" message before the agent wastes a turn calling them.

### Medium-term (design needed)

- **Live telemetry snapshot.** Attitude, battery, GPS fix, flight mode, armed state. Likely `GET /telemetry` returning a point-in-time snapshot; see `MAVState.cs` fields.
- **Missions / waypoints.** `GET /mission` → waypoint list. Write path (`POST /mission`) is the hard part — needs round-trip confirmation via `MISSION_ACK`.
- **Resource pagination.** The `ardupilot-missionplanner://vehicle/params` static resource currently returns all params in one body (~1100 entries). Needs chunked resources or a `list` cursor protocol.

### Long-term (directional)

- **Logs / dataflash.** Listing and retrieval of `.bin` logs stored on the SD card or accessible via MAVLink log download.
- **Plugin packaging.** Distributing the bridge as an MP plugin (`.dll` in `plugins/`) would let users who don't build MP from source install it. Rejected for MVP due to plugin ABI instability — revisit when the plugin surface stabilises upstream (see `docs/adrs/0001-in-process-http-bridge.md` §Alternatives).
- **Multi-vehicle.** Everything today assumes the single currently-selected vehicle. Expanding to enumerate all active sysids in `MAVlist` would be a contract-breaking change (`bridge_api_version` major bump).

---

## Contributing

Before opening a PR that changes the HTTP contract or the MCP tool surface:

1. Check whether the change needs an ADR — see `docs/adrs/README.md`.
2. Check the `bridge_api_version` bump rules — see `MCP_BRIDGE_DESIGN.md §7`.
3. Both repos need to agree on any contract change. Changes to the bridge API should be accompanied by a matching MCP server PR (or vice versa), and both should reference the same ADR / design discussion.

For purely additive changes (new endpoints, new optional fields, new MCP tools that don't require bridge changes), the bar is lower — new endpoint + tests + doc update + appropriate version bump.

Bug fixes, refactors that don't change external behavior, and documentation improvements don't need ADRs.
