# 0004. Drop `changed_from_default` / `default` from MVP

- **Status:** Accepted
- **Date:** 2026-04-15

## Context

An early draft of the bridge contract included two features tied to parameter defaults:

- A `default: number | null` field on each entry returned by `GET /params` and `GET /params/{name}`, representing the factory default value for the parameter on the current vehicle.
- A `changed_from_default=true` query flag on `GET /params`, filtering the returned list to parameters whose current value differs from their default.

The intent was to help agents quickly answer "what has the operator customized on this vehicle?" — a useful question for tuning audits and bug triage. Both features assume the bridge can reliably produce a default value for every parameter.

In practice this assumption does not hold. ArduPilot parameter defaults are a layered construct: `apm.pdef.xml` carries a nominal `<field name="Default">` value for many parameters, but the actual default at runtime depends on vehicle frame, frame class, compile-time feature flags, and (for some parameters) firmware version. MP's own parameter-compare UI acknowledges this by making "compare against defaults" an explicit operator-driven workflow rather than a one-liner.

There were three ways to handle this:

- **Ship defaults from pdef anyway.** Return whatever `<Default>` appears in the XML; accept that it is sometimes wrong.
- **Ship defaults from MP's internal resolver.** Track down whatever logic MP uses to compute per-vehicle defaults and expose it through the bridge.
- **Drop both features from MVP.** Keep the response shape simpler, and add them back later if and when default resolution is reliable.

## Decision

**Both features are removed from the MVP contract.** `GET /params` does not accept `changed_from_default`; neither endpoint returns a `default` field.

Concretely:

- The bridge's `GET /params` handler accepts no query parameters beyond what is needed for the basic listing (currently none). Adding `changed_from_default` later is a compatible extension — it would be an optional query param, absent by default.
- The bridge's `GET /params/{name}` response includes `name`, `value`, `type`, `metadata_available`, and the pdef metadata fields, but no `default` field.
- The MCP server's `list_params` tool accepts `prefix`, `limit`, and `cursor` — no `changed_from_default`. The filter guidance in its tool description points agents at `prefix`-based filtering instead.
- The MCP server's `get_param` and `search_params` return the metadata the bridge provides verbatim. There is no "default" field in their output either.
- `MCP_BRIDGE_DESIGN.md` §4 documents the dropped fields explicitly so contributors reviewing the contract understand this is a deliberate omission, not a regression.

## Consequences

**Positive:**

- **No field in the contract is a lie.** Every field we return either carries accurate data or is explicitly marked unavailable (`metadata_available: false`, `vehicle_type: null`). Adding a `default` field whose value is "sometimes right" would poison the contract — agents would treat it as authoritative and make bad suggestions based on it.
- **Smaller surface to support.** One less query parameter to implement, one less field to document, one less thing to regress. The MVP is tighter.
- **Clear extension path.** If and when we build a reliable default resolver, re-introducing the field is a purely additive change: new key on existing responses, new query flag on existing endpoint. No breaking change, no version bump beyond `bridge_api_version` bumping for the new capability.
- **Pushes the "what changed?" question into the agent.** An agent that wants to audit customizations can ask "list all params, then group by family, then compare against what I know about typical ArduCopter settings." That's a prompting / reasoning task the LLM can actually do well; the bridge providing a half-correct boolean would short-circuit it with bad data.

**Negative:**

- **Loses a convenient tool surface.** "Show me parameters that have been changed from default" is a natural question an operator would ask an agent. For MVP the answer is "the bridge doesn't expose defaults, so I can't tell you directly" — less satisfying than "here are the 12 params you've tuned."
- **Agents may hallucinate defaults.** Without a trustworthy default field, an agent may pattern-match on parameter names and confidently claim a default value that does not match the vehicle's actual default. This is a known LLM failure mode; the only mitigation is a clear tool description ("this tool does not expose defaults") which agents sometimes ignore.
- **Means the roadmap has unfinished-sounding bullets.** "Parameter writes" and "default comparison" both appear in the roadmap without a clear ETA. That's honest but feels incomplete to a first-time reader.

**Neutral:**

- Re-adding the feature later depends on a prior decision about where default resolution lives — MP's existing logic if we can extract it, a new in-bridge resolver if not, or upstream ArduPilot if they publish a stable per-vehicle-config default snapshot. That decision will be its own ADR when the work is scheduled.

## Alternatives considered

**Ship pdef defaults as-is.** Rejected because the failure mode is silent and bad. An agent told the default for `ATC_RAT_PIT_P` is `0.135` will confidently discuss whether the operator is over- or under-tuned against that baseline, even when the actual default for this vehicle's frame class is different. "Correct most of the time but wrong in the cases operators actually want to investigate" is the worst of both worlds — we'd be better off having no field.

**Resolve defaults inside MP and expose via the bridge.** Rejected for MVP because it requires spelunking through MP's parameter-download and parameter-compare code to understand where defaults actually come from, and that code is non-trivial. The scope creep would delay the MVP for a feature that is useful but not on the critical path to "let an agent read params at all." Revisit when the MVP has landed and there is user signal on whether the missing feature is actually blocking workflows.

**Keep the feature but flag every value as `default_confidence: "pdef-declared"` or similar.** Rejected because agents ignore confidence flags in practice. A numeric field next to a qualitative flag becomes the numeric field in whatever summary the agent produces. Better to omit the field entirely than to add a hedge the downstream consumer will strip away.
