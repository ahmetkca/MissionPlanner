# 0001. In-process HTTP bridge inside Mission Planner

- **Status:** Accepted
- **Date:** 2026-04-15

## Context

We need a way for an external process — the MCP server, and through it AI coding agents — to read Mission Planner's live in-memory state. The MVP scope is the full parameter list for the currently connected vehicle plus its pdef metadata (description, units, allowed values, bitmask definitions, range, reboot-required flag, and so on).

The data we want is not on disk. It lives in MP's process memory, specifically:

- `MainV2.comPort.MAV.param`, a `MAVLinkParamList` holding the live parameter snapshot.
- The singleton `ParameterMetaDataRepositoryAPMpdef` pdef cache, built from XML files shipped with MP and keyed by `(paramName, metaKey, vehicleType)`.
- Connection state hanging off `MainV2.comPort.BaseStream` (`.IsOpen`, `.PortName`) and `MAVState` records on `MainV2.comPort.MAVlist[sysid, compid]` (firmware family, version string, received/reported counts).

Threading is non-trivial. MAVLink traffic is received on a dedicated background thread; that thread writes to `MAVLinkParamList` under an internal `ReaderWriterLock`. WinForms UI code reads the same structure from the UI thread. Any new reader has to cooperate with the existing lock rather than inventing its own synchronization. MP itself already demonstrates this by running an internal `httpserver` (unrelated feature, different port) started from `MainV2.OnLoad` on a background thread.

Three shapes of bridge were plausible:

- **In-process HTTP server compiled into MP.** Runs on a background thread inside MP's process, reads `MainV2.comPort.MAV.param` directly while holding `MAVLinkParamList`'s reader lock, reads pdef metadata directly from `ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(...)`.
- **Out-of-process bridge.** A separate .NET process that receives MAVLink traffic over UDP forwarded from MP (MP already supports "UDP out" forwarding), reconstructs its own param list by parsing `PARAM_VALUE` messages, and serves HTTP to the MCP server.
- **Mission Planner plugin.** Packaged as a `.dll` dropped into MP's `plugins/` directory, loaded at runtime via MP's `IPlugin` interface.

## Decision

Build the bridge as an **in-process HTTP server compiled into MP itself**, living under `McpBridge/` on the `feat/mcp-bridge` branch of the fork.

Concretely:

- A single class `McpBridgeServer` (`McpBridge/McpBridgeServer.cs`) owns a `System.Net.HttpListener` bound to `http://127.0.0.1:9999/`. Listening on the loopback address only — not on `+` or `0.0.0.0` — means no firewall prompt on Windows and no exposure beyond the local machine.
- The listener runs on a dedicated background `Thread` (`IsBackground = true`, named `"MCP Bridge listener"`) that blocks on `HttpListener.GetContext()` in a while-loop. We chose a dedicated blocking thread over `BeginGetContext` because the total request volume is tiny (one MCP agent, infrequent tool calls) and the synchronous accept loop is easier to reason about for shutdown.
- Request handlers run synchronously on the listener thread. Parameter reads go through `MAVLinkParamList.Snapshot()` and the string indexer, both of which take the list's `ReaderWriterLock` for reading. That means we cooperate with the existing lock rather than inventing new synchronization.
- Startup is called from `MainV2.OnLoad` wrapped in `try/catch`, so a bridge failure never prevents MP from booting. Shutdown is called from `MainV2_FormClosing` and uses a `ManualResetEvent` the accept loop sets in its `finally` block, so `Stop()` can deterministically wait for the listener thread to exit before MP disposes its own state.
- The bridge catches `HttpListenerException.ErrorCode == 995` (`ERROR_OPERATION_ABORTED`) and `ObjectDisposedException` as normal shutdown signals; all other exceptions in the accept loop are logged and the loop continues after a 100ms backoff.

## Consequences

**Positive:**

- **Zero IPC complexity.** The bridge reads the authoritative in-memory structures directly. No MAVLink re-parsing, no UDP forwarding setup, no state-sync drift between MP's and a sidecar's view of the parameter list.
- **Correct by construction.** Whatever MP knows, the bridge knows — vehicle type (`Firmwares` enum → string), firmware version (`MAVState.VersionString`), and pdef metadata cached in MP's pdef repository. There is no second source of truth to keep in sync.
- **Threading is one problem, not two.** `MAVLinkParamList`'s existing `ReaderWriterLock` covers us. We did not introduce a new concurrency model, a new queue, or a new synchronization primitive for the parameter data itself. The only bridge-specific synchronization is the `ManualResetEvent` used to join the listener thread on shutdown.
- **Follows an existing convention.** MP already starts an internal HTTP server from `MainV2.OnLoad` (different port, different purpose). A contributor reading `OnLoad` sees `_mcpBridge = new McpBridgeServer(); _mcpBridge.Start();` right next to the existing pattern and recognizes what it's doing.
- **Debuggable without any tooling.** `curl http://127.0.0.1:9999/status` works from any shell while MP is running. No protocol decoder, no MCP client, no simulator needed to verify the bridge is alive.

**Negative:**

- **Bridge ships with MP.** Bumping the HTTP contract (`bridge_api_version`) requires rebuilding MP from source. There is no hot-swap path; a contributor who wants to iterate on the bridge edits C# and restarts MP on every change.
- **Lifecycle tied to MP.** If MP is killed mid-request the request fails with a connection reset; if MP is not running there is nothing for the MCP server to talk to. There is no queue-and-retry on the bridge side. The MCP server handles this by surfacing a clear `mp_status` result rather than hanging.
- **Process isolation lost.** A bug in the bridge could in principle destabilize MP. We mitigate this with catch-all exception handling in every handler (`HandleRequest`'s outer try/catch always writes a 500 and closes the response stream) and by wrapping bridge startup in `MainV2.OnLoad` so a listener failure never blocks MP from booting.
- **Harder to test in isolation.** Running the bridge without MP means stubbing `MainV2.comPort`, `MAVState`, and `ParameterMetaDataRepositoryAPMpdef` — all of which are tightly coupled to MP internals. The MCP server side addresses this by mocking at the HTTP level (stub `/status`, `/params`, `/params/{name}`) rather than at the C# level.

**Neutral:**

- Future promotion to an MP plugin remains possible. The bridge module is self-contained under `McpBridge/`, touches MP only through the documented public surface (`MainV2.comPort`, `MAVLinkParamList.Snapshot()`, the pdef repository), and could be repackaged as an `IPlugin` implementation if upstream MP accepts it. The migration is mechanical — no internal contract to redesign.

## Alternatives considered

**Out-of-process bridge via UDP forwarding.** Rejected for two reasons. First, pdef metadata is the feature, not a nice-to-have: MP's pdef cache is built from XML shipped inside MP and keyed by vehicle type, and a sidecar receiving only `PARAM_VALUE` MAVLink messages has no access to it. Duplicating the pdef pipeline in a second process means shipping the XML twice and re-implementing `ParameterMetaDataRepositoryAPMpdef` in whatever language the sidecar is written in. Second, a sidecar reconstructing the param list from UDP creates a drift problem — when MP and the sidecar disagree about a parameter value (dropped packet, race on reconnect), which view is authoritative? Operator-visible truth lives in MP's UI, so the bridge must too. The setup friction (user has to configure UDP forwarding in MP before anything works) was a secondary reason.

**MP plugin (`.dll` in `plugins/`).** Rejected for MVP because it adds real friction to the "clone, build, run" loop: producing the `.dll`, copying it into a working MP install, and versioning against MP's plugin ABI. The `IPlugin` surface is also not formally stable — its contract lives in source and has been reshuffled in past MP releases — so shipping the bridge as a plugin would couple our release cadence to upstream MP's internals in a way that a compiled-in fork does not. We revisit this post-MVP if we want to distribute the bridge as a drop-in to users who don't build MP from source.
