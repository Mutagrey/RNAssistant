# Cleanup baseline

The agent runtime now has one direct flow:

```text
request + full tool/skill context
    -> model JSON
        -> zero or more sequential local tools
            -> one TOOL_RESULT JSON per call id
                -> next model turn
```

Removed layers include offline mode, automatic mode selection, task routing, phase state, tool catalog slicing, progressive skill activation, plans, observations, format repair, transport fallback, persistent batch orchestration, automatic tool retry, and separate mutation verification.

The remaining runtime responsibilities are intentionally small:

- prompt/context assembly;
- one JSON parser;
- schema and safety validation at execution time;
- confirmation and resource limits;
- local execution and result serialization;
- transcript persistence and optional context compaction.

Future changes should prefer extending the editable prompt, skill text, native-like tool descriptions, or tool-result JSON. Add a new runtime state machine only when a local safety or consistency invariant cannot be expressed or enforced at the tool boundary.

Known trade-offs of this simpler design:

- the full enabled tool/skill catalog consumes context and can outgrow a small model window;
- independent multi-tool calls reduce model round trips, but result-dependent calls still require another model turn;
- built-in tool schemas are formal but still need richer per-argument descriptions, enums, defaults, and required-field coverage;
- the selected endpoint must reliably support `json_object`, because invalid JSON is not repaired;
- without a separate verifier, correctness depends on explicit tool results and tool-internal hash/backup/stale-state checks;
- attachments use the selected model and fail explicitly when its declared media capabilities are insufficient.

The fast validation path is `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj`. COM/VSTO behavior still requires Windows x64 + Office + VS 2022 smoke testing.
