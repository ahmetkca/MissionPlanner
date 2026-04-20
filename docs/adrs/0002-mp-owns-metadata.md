# 0002. Mission Planner owns parameter metadata

- **Status:** Accepted
- **Date:** 2026-04-15

## Context

The MCP server needs to return rich metadata for each parameter: display name, description, units, allowed values (enum-style), bitmask definitions, numeric range, increment, user level (Standard / Advanced), reboot-required flag, read-only flag, volatile/calibration flags. This is what makes `get_param` and `search_params` useful to an AI agent — without it, a parameter is just a name and a float.

This metadata comes from ArduPilot's `apm.pdef.xml` files, one per vehicle type (ArduPlane, ArduCopter, Rover, ArduSub, AntennaTracker). Mission Planner already bundles these files, parses them at startup into `ParameterMetaDataRepositoryAPMpdef`, and caches the result as an in-memory tree keyed by `(vehicleType, paramName, metaKey)`. The singleton exposes `GetParameterMetaData(paramName, metaKey, vehicleType)` returning a `string`, and MP's own parameter UI (`ConfigRawParams`, `ParamCompareForm`, etc.) consumes exactly this surface.

When the MCP server was being designed, there were three places metadata could live:

- **MP's pdef cache, read through the bridge.** The bridge formats the raw strings into typed JSON (parsing `"min max"` into `{min, max}`, `"0:Foo,1:Bar"` into a `{key: value}` object) and returns them alongside the param value.
- **The MCP server's own pdef parser.** The server downloads (or bundles) `apm.pdef.xml` files and parses them itself in Node/TypeScript.
- **A shared third-party source.** Fetch metadata from the ArduPilot documentation site or a published pdef package at runtime.

## Decision

**Mission Planner is the authoritative source of parameter metadata.** The bridge exposes it; the MCP server does no XML parsing and bundles no pdef data.

Concretely:

- `GET /params/{name}` on the bridge calls `ParameterMetaDataRepositoryAPMpdef.GetParameterMetaData(paramName, metaKey, vehicleType)` once per metadata key.
- Vehicle type is derived from `MAVState.cs.firmware` (a `Firmwares` enum) via a fixed dictionary in the bridge (`ArduPlane`, `ArduCopter2 → "ArduCopter"`, `ArduRover → "Rover"`, `ArduSub`, `ArduTracker → "AntennaTracker"`). If vehicle type cannot be resolved, metadata is omitted and `metadata_available: false` is returned.
- The bridge parses the raw pdef string values into typed JSON at the edge: `range` becomes `{min: number, max: number}` parsed with invariant-culture float parsing; `values` and `bitmask` become `{string: string}` dictionaries split on `,` and `:`; other keys pass through as strings.
- `metadata_available` is a boolean: true if any of the metadata fields resolved to a non-null value, false otherwise. A null value on any individual field means "pdef did not define this for this parameter" rather than "lookup failed".
- The MCP server's `get_param` and `search_params` tools expose this metadata verbatim. They do not add fields, do not fabricate descriptions, and do not cross-reference external sources.

## Consequences

**Positive:**

- **Single source of truth.** Whatever ArduPilot version MP was built against is what the agent sees. When the MP fork is rebased onto a newer upstream with updated `apm.pdef.xml` files, the bridge picks that up for free. The MCP server is version-oblivious.
- **No duplicate parsing logic.** MP already handles pdef quirks (vehicle-specific overrides, inherited defaults, parameter family prefixes). Re-implementing that in TypeScript would be a significant ongoing maintenance burden — every pdef format change would require a second PR.
- **No stale cache on the server side.** The MCP server holds no state derived from pdef XML. Restarting MP on a new firmware version immediately changes what the server sees on the next `/params/{name}` call.
- **Correct handling of vehicle-specific metadata.** `apm.pdef.xml` differs across vehicle types — the same parameter name can have different allowed values on ArduPlane vs ArduCopter. MP's pdef repository already keys on vehicle type, so the bridge gets the right variant for free.

**Negative:**

- **Metadata is only available when MP is connected to a vehicle.** Vehicle type comes from `MAVState.cs.firmware`, which is populated only after MAVLink handshake completes. Before connection, `get_param` can still return name/value/type but `metadata_available` will be false. This is a consequence of keying on vehicle type and is acceptable because the entire use case requires a connected vehicle anyway.
- **MP is a hard dependency of the MCP server.** The server cannot serve metadata offline, cannot serve it without MP running, and cannot serve it for a vehicle type MP does not support. All of these are intended.
- **Bridge handler does parsing work on the listener thread.** Range/values/bitmask string parsing happens inside the HTTP request handler, inside the read lock. This is cheap (short strings, invariant-culture parse) and is not a measured bottleneck, but it is done on the listener thread rather than precomputed.

**Neutral:**

- The shape of the bridge's metadata response is the stable contract; the underlying pdef XML format is not. If MP upstream changes its pdef storage (new keys, renamed fields), the bridge absorbs the change and the MCP server stays unchanged — as long as the JSON shape on the wire is preserved.

## Alternatives considered

**MCP server parses pdef XML itself.** Rejected because it duplicates logic that already exists and is already correct in MP. The XML format has accumulated vehicle-specific overrides and inheritance rules that are non-trivial to re-implement. Shipping pdef files inside the npm package would also create a versioning problem: the server's bundled pdef could disagree with the MP install the user is actually running, which is exactly the drift this ADR is designed to prevent.

**Fetch pdef from a third-party source at runtime.** Rejected for two reasons. First, it introduces a network dependency the MCP server does not otherwise have — everything else is localhost-only. Second, third-party pdef sources lag behind what a user's MP install actually has, especially if the user runs a patched MP or a development ArduPilot build. The whole premise of this project is "expose what MP knows"; going around MP to fetch metadata from somewhere else defeats that.
