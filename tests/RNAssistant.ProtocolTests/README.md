# Conversation v8 protocol checks

Production v8 codec checks; owner: Core ModelProtocol.
This small net8/C# 7.3 runner source-links the production codec, tool schemas,
local batch authority and message stream extractor. No Office, fake protocol
implementation or v7 compatibility codec is included.

```sh
dotnet run --project tests/RNAssistant.ProtocolTests/RNAssistant.ProtocolTests.csproj -- --export artifacts/protocol-v8
python3 -m venv artifacts/protocol-v8/venv
artifacts/protocol-v8/venv/bin/pip install -r tests/RNAssistant.ProtocolTests/requirements.txt
artifacts/protocol-v8/venv/bin/python tests/RNAssistant.ProtocolTests/validate_schema.py artifacts/protocol-v8
```

The independent JSON Schema validator consumes exported **production** schemas and
positive/negative logical envelopes wrapped explicitly as `{"response": ...}` for
strict transport. Duplicate evidence is deliberately enforced by the local parser,
not an unsupported provider keyword. JSON lexical/depth failures are checked by the C#
parser; duplicate keys must not be flattened by the Python validator. The existing
optional-argument transport projection is explicit: strict schema requires nullable
optional fields, while the same local parser also permits their omission under
`json_object`; accepted nulls are removed before runtime. Closed action/result
shapes, required arguments and batch constraints have no mode-specific exceptions.

Synthetic endpoint probes (local Ollama only; no tool is dispatched):

```sh
artifacts/protocol-v8/venv/bin/python tests/RNAssistant.ProtocolTests/probe_endpoint.py --export artifacts/protocol-v8 --output artifacts/protocol-v8/endpoint-new
```

Use a new output directory each time. The default locally installed profile is
`rna-gemma4-12b-32k:latest`; `--model` selects another installed model explicitly.
Requests use `/v1/chat/completions`, the same `response_format` envelope as LlmClient,
with temperature 0, top_p 1, max_tokens 768 and stream false. Each probe retains
exact request/response bodies, status, model digest, endpoint version, duration,
usage and validation result. Run `--validate response.json` on the C# runner to
validate a raw envelope against the probe's captured synthetic tool catalog.
These are format observations, not exhaustive decoder compliance or task quality.

The standalone runner does **not** compile ModelProtocolClient's full frozen
compiler/request loop, kernel, adapters or history reader. Repair content and
its typed error codes are checked here; frozen-context/retry/fallback integration
and no-replay remain required in the integrated Harness after phases 2–4.
Use Harness filter `v8 recovery:` for integrated strict/fallback, reflection depth,
output-limit correction and Office/workspace live-to-durable presentation. These
host-neutral checks do not qualify a real provider or Windows/Office/WebView2.
`--validate` checks a logical JSON-object response; `--validate-schema` requires
its strict transport wrapper. Endpoint probes select the matching guidance/parser.
