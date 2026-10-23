# Conversation v8: isolated real-model evidence

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

The phase-5 gate remained open after this pilot. It needed repeated representative tasks with an
isolated v8 no-reflection policy and an adaptive-reflection variant, field-order
and `reason` ablations, false/premature-success and invalid-evidence rates,
reflection loops/context failures, and analysis that separates protocol effects
from the workspace selector fix. These local CLI tasks do not qualify Windows
x64, Office x64 or WebView2 behavior.

## Repeated three-way local comparison — 2026-10-23

The same pinned model and three tasks were run twice per variant, interleaved
with fresh workspace/state per case. V8 used the source-linked CLI build from
`ec14d52d`; v7 used the earlier build above. The `v8_direct` policy removed
only the `reflection` branch from the model-facing JSON schema; for requests
without a schema it added a no-reflection instruction to v8 task guidance.
The v8 runtime and acceptance gate were unchanged. The capture stored both
the CLI request and the exact request sent upstream when the policy changed it.

| Variant | Accepted tasks | Exact file bytes | Required read-back | Repeated identical calls | Prompt / completion tokens | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v7 | 0/6 | 5/6 | 0/6 | 19 | 255,712 / 2,871 | 58.62 |
| v8 adaptive | 6/6 | 6/6 | 6/6 | 0 | 138,411 / 2,222 | 27.38 |
| v8 without reflection branch | 5/6 | 6/6 | 5/6 | 3 | 145,915 / 2,252 | 33.07 |

There were no completed runs missing the required artifact/read-back and no
finish citation absent from observed terminal tool results. The one failed
`v8_direct` run produced the correct uppercase file but made only one of two
required reads and stopped after repeated identical calls. V7 generally
produced the requested bytes but repeated tools without required read-back.
The adaptive v8 model chose **zero** reflections in these six runs, so the
6/6 versus 5/6 difference cannot be attributed to reflection. With
temperature 0.2 and two repetitions, it is also too small to establish a
reliable performance difference. V7/v8 sources differ beyond the protocol,
including the workspace read-selector correction, so this is not a
protocol-only A/B result. All tasks are synthetic local-file tasks.

The [three-way evidence archive](evidence/phase5-threeway-20261023.tar.gz)
contains the manifest, all 90 exact HTTP request/response pairs and hashes,
per-run summaries, CLI output, stderr and file artifacts; disposable state
directories are omitted. All pairs and the binary closure hashes were
rechecked after the run; no proxy HTTP 502 occurred. Archive SHA-256:
`fd3641485c22faad1ce283aabc4e32cef94168524073a7f6b164f2d9f273b1c8`.
Runner `1a2e744c` captured the series. The v7 repeated-call metric alone was
recomputed from those unchanged response bodies with runner `a7f328d9` and
recorded as postprocessing in the manifest.

The phase-5 gate remained open after this comparison for field-order and
`reason` ablations, broader representative tasks, additional repetitions and
target Windows/Office tests.

## Field-order ablation — 2026-10-23

The same v8 binary and pinned model ran the three tasks twice per variant in
interleaved order. A test proxy moved `message` to the first field of each
model-facing schema branch and required-field list, and changed the exact
field-order instruction. It did not change the local parser or runtime. All
24 model responses in that variant placed `message` first; the baseline's 22
responses placed `action` first.

| Field order | Accepted tasks | Exact bytes/read-back | Repeated calls | Prompt / completion tokens | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| `action` first | 6/6 | 6/6 | 0 | 138,363 / 2,220 | 53.17 |
| `message` first | 6/6 | 6/6 | 0 | 150,877 / 2,391 | 55.65 |

There were no invalid evidence IDs, premature completed runs or reflection
steps. The `message`-first variant used two more model responses and about 9%
more total tokens in this small sample. These results show that admission
remains correct under a field-order change; they do not establish a reliable
latency or quality effect. The [field-order evidence archive](evidence/phase5-fieldorder-20261023.tar.gz)
contains all 57 verified HTTP exchanges, CLI/file artifacts and binary closure
hashes without disposable state. SHA-256:
`afe07a6fa7e1e4e1b5df2f027eea42efa8f2b8b66681e61d804a8537a26387cd`.
Runner revision: `d82f0007`.

The phase-5 gate remained open after this field-order run for the `reason`
ablation, representative tasks, additional repetitions and target Windows/Office
tests.

## Model-facing `reason` ablation — 2026-10-23

The same v8 binary and pinned model ran each task twice with ordinary v8 and
with `reason` removed from the model-facing schema and root-order guidance.
The test proxy inserted the explicit neutral reason
`Benchmark transport placeholder; no model reason was requested.` into the
response delivered to the unchanged v8 runtime. It also added one benchmark
instruction so JSON-object fallback requests remained in this variant. Exact
upstream and delivered responses were both captured: all 28 translated
responses preserved action, tool calls/results and reported usage; the model
itself supplied **zero** `reason` fields. This is a model-facing ablation with
a test transport shim, not a product protocol alternative.

| Variant | Accepted tasks | Exact file bytes | Required read-back | Prompt / completion tokens | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Ordinary v8 | 6/6 | 6/6 | 6/6 | 138,562 / 2,261 | 53.64 |
| V8 without model `reason` | 4/6 | 6/6 | 4/6 | 176,151 / 2,320 | 55.15 |

Both failures were the `ready` task: the file bytes were correct, but the model
attempted finish repeatedly after one write without the required read-back.
The runtime rejected those finishes and stopped at the iteration limit. There
were no accepted premature finishes or invalid terminal-result citations.
`uppercase` and `two_files` passed in both repetitions without model `reason`.
The ordinary v8 baseline used one repeated identical call; the ablation used
none. Neither variant selected reflection. The result suggests a task-specific
quality risk, but two repetitions and a shim that also changes instruction and
saved reason text cannot isolate the cause or establish a general advantage for
`reason`.

The [reason-ablation evidence archive](evidence/phase5-noreason-20261023.tar.gz)
contains all 59 verified HTTP exchanges, upstream/delivered response bodies,
CLI/file artifacts and binary closure hashes without disposable state. No
proxy HTTP 502 occurred. SHA-256:
`938bad1cb1f31c57abda2b452f13b669b8f892a57006241d564ff9027d5a515f`.
Runner revision: `b3ae2e2c`.

The phase-5 gate remains open for representative Office tasks, more repetitions,
analysis separating protocol from other source changes, and Windows x64 + Office
x64 + WebView2 qualification. The exploratory CLI results do not justify a
default cutover.
