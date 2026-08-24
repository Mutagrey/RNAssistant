# Cleanup baseline

The agent runtime now has one direct flow:

```text
request + full tool catalog + compact skill catalog
    -> model JSON
        -> zero or more sequential local tools
            -> one TOOL_RESULT JSON per call id
                -> next model turn
```

Removed layers include offline mode, automatic mode selection, task routing, hidden planner/phase state, tool catalog slicing, progressive skill activation, observations, repair state machines, persistent batch orchestration, automatic tool retry, and separate mutation verification. Optional visible plans are explicit versioned chat artifacts controlled by model-selected CRUD tools. Format recovery is a bounded 1–20 stateless retry loop; each attempt uses the same clean accepted prompt plus one current error. Rejected output never enters model replay or visible chat history, but remains a log-only trajectory event for diagnosis. The only transport fallback is the explicit request-local `json_schema` → `json_object` compatibility option.

The remaining runtime responsibilities are intentionally small:

- prompt/context assembly;
- one JSON parser;
- schema and safety validation at execution time;
- confirmation and resource limits;
- local execution and result serialization;
- canonical event persistence with explicit turn/step/stream boundaries, replay projections, and optional context compaction.

Future changes should prefer extending the editable prompt, skill text, native-like tool descriptions, or tool-result JSON. Add a new runtime state machine only when a local safety or consistency invariant cannot be expressed or enforced at the tool boundary.

Known trade-offs of this simpler design:

- the full runnable tool and enabled-skill catalogs consume context and can outgrow a small model window; the run then stops with an explicit budget diagnostic, while skill bodies consume context only after `common.skills_read`;
- independent multi-tool calls reduce model round trips, but result-dependent calls still require another model turn;
- strict tool schemas improve selection and validation, but their descriptions, defaults, enums, and required fields must stay synchronized with executor behavior;
- the selected endpoint should support the configured `json_object` or `json_schema` format and result role; repeated malformed responses stop after the configured 1–20 correction attempts;
- without a separate verifier stage, correctness depends on explicit tool results and tool-internal checks; VBA mutations perform deterministic backup/strict-live-hash/stale-state and post-write read-back verification, while package equivalence uses a separate export-aware hash;
- media attachments use request-scoped priority routing and fail explicitly when no configured model declares all required capabilities.

The fast validation path is `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj`. COM/VSTO behavior still requires Windows x64 + Office + VS 2022 smoke testing.
