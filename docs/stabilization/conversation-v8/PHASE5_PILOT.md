# Conversation v8: isolated real-model pilot

2026-10-23. Owner: Core ModelProtocol / Workspace CLI. This is exploratory
phase-5 evidence, not the qualification gate in the v8.1 specification §G.

`tests/cli/conversation_v8_model_compare.py` ran the same three synthetic
workspace tasks against separate v7 and v8 CLI binaries. Every run used a new
workspace and `RNA_STATE_ROOT`; no user chat was reused. A local capture proxy
stored exact OpenAI request/response bodies and their SHA-256 hashes. The
independent grader compared resulting file bytes and unchanged inputs; runtime
read-back and acceptance are reported separately. Runs were interleaved to
balance order. Response mode was `json_schema`, instruction role `developer`,
tool-result role `user`, thinking off and context window 32,768 tokens.

- v7 source: `cc79fd75304989c4b8b16baf5ec5fe62042b2605`; CLI DLL SHA-256
  `c80331d8164e83dcdeb6e74401febff7682151f6f495198c4b8da9699233d5c6`.
  The archived build needed one build-only `YamlDotNet` reference in
  `RNAssistant.Core.Net8.csproj`; C# source was unchanged.
- v8 source: `a6d406df811f5e457c43f545d1674b033a67bcc4`; CLI DLL SHA-256
  `b9dc10dfd9752b4f17410e4164249c7f09244eb1301466e88756230230c63449`.
- Runner: `4b3fdf868dd7efb5bcc15824714bb65f4c4d37bb`.
  Local Ollama `0.35.0`, model `rna-gemma4-12b-32k:latest`, digest
  `cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`.

| Task | Variant | Exact output bytes | Required read-back | Run outcome | HTTP prompt / completion tokens | Seconds |
| --- | --- | --- | --- | --- | ---: | ---: |
| Create/read `report.txt` | v7 | yes | no | failed: tool step limit | 29,100 / 316 | 51.57 |
| Create/read `report.txt` | v8 | yes | yes | completed, acceptance passed | 18,579 / 317 | 47.60 |
| Uppercase `source.txt`, read result | v7 | no | no | exit 3: requested input, no terminal run | 14,146 / 118 | 41.08 |
| Uppercase `source.txt`, read result | v8 | yes | yes | completed, acceptance passed | 25,246 / 374 | 26.07 |
| Create/read two files | v7 | yes | no | failed: repeated tool without progress | 45,149 / 447 | 65.67 |
| Create/read two files | v8 | yes | yes | completed, acceptance passed | 25,339 / 426 | 63.18 |

The source comparison is not a protocol-only A/B test: the candidate branch
contains other changes. In earlier isolated exploratory runs, v8 also failed
read-back on the uppercase and two-file tasks. Their exact model responses
supplied both `startLine`/`lineCount` and `startChar`/`charCount`, which the
runtime correctly rejected. The workspace read schema now offers whole, line
and character variants separately (`a6d406df`). In the pinned pilot above,
both tasks used valid reads and passed. This supports that bounded schema fix;
one pass per task does not establish statistical reliability or the contribution
of v8 alone.

The [evidence archive](evidence/phase5-pilot-20261023.tar.gz) contains the
manifest, exact HTTP bodies and hashes, CLI JSONL, summaries and file artifacts
for all six runs. Archive SHA-256:
`f629845721c0792592744e5db4eb0438bf65db6c835637f6cce85990761eec88`.
It excludes disposable local state directories. The runner retains those in its
requested output directory when a deeper event inspection is needed.

The phase-5 gate remains open. It needs repeated representative tasks with an
isolated v8 no-reflection policy and an adaptive-reflection variant, field-order
and `reason` ablations, false/premature-success and invalid-evidence rates,
reflection loops/context failures, and analysis that separates protocol effects
from the workspace selector fix. These local CLI tasks do not qualify Windows
x64, Office x64 or WebView2 behavior.
