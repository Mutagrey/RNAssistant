# Conversation v8: phase-3 adapter and prompt candidate

2026-10-23. Owner: Core ModelProtocol guidance / Office prompt composer /
Workspace and CLI adapters. Branch: `conversation-v8`. Main remains v7.

The default Agent, Plan and Chat prompts now use the canonical v8 guidance
from `ModelProtocolWire`. Prompt schema version 52 archives and replaces saved
pre-v8 prompt layers through the existing settings migration. Office also pins
the complete v8 response contract when editable Agent or Plan text lacks it;
editable text cannot remove local schema/parser or runtime admission checks.
Workspace acceptance guidance names `finish` and sends indispensable questions
through `common.questions_ask`. The format-repair prompt shares the same
escaping guidance. The Office prompt composer now matches the execute branch
of the v8 root union before removing duplicated tool argument schemas from
strict-transport context; it retains the original context if contracts differ.

CLI JSONL reports `action=finish` for `model_finish`. The separate
`run.needs_input` and approval events remain runtime states, not model actions.
Unreachable `model_done/model_blocked/model_needs_input` projections were
removed from Office, Workspace, CLI and run-view state. The deterministic CLI
response-mode smoke now uses v8 execute/finish envelopes and terminal IDs.

## Host-neutral checks

- `dotnet run --project tests/RNAssistant.ProtocolTests/RNAssistant.ProtocolTests.csproj -- --export <empty-dir>`:
  153 cases / 2156 assertions passed.
- `sh tests/RNAssistant.V8KernelSmoke/run.sh`: source-linked production build
  and 21 passing focused checks, including default/custom prompts and strict
  v8 schema projection.
- `dotnet build src/RNAssistant.Cli/RNAssistant.Cli.csproj` and
  `python3 -B tests/cli/response_mode_smoke.py`: pass. The latter covers
  `json_schema`/`json_object`, user/developer/native tool-result roles, exact
  call/result pairing and approval inheritance through the actual CLI/HTTP
  boundary.

The full Harness still has 147 distinct v7 compile diagnostics in legacy test
fixtures. It needs a deliberate fixture/assertion migration, not production
aliases for removed actions. Process-level ChatStore replay, UI acceptance
projection, the phase-0 HTTP reproducer and real-model comparison remain open.
No Windows/Office/WebView2 qualification is claimed.
