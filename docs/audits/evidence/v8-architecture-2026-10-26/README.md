# V8 stabilization evidence — 2026-10-26

Owner: Core/Application + Harness/CLI/Web fixtures. Baseline
`ba44ad1b573aa4a5f6daf0f79c0b6c3c35a5a98a`; local macOS, .NET 8
host-neutral Harness/CLI/MockDemo, Python 3, Node and Chrome headless.
Production target remains C# 7.3/.NET Framework 4.8. Windows/Office/VSTO
validation was deliberately not run on this machine.

- [Harness](harness.json): command is `dotnet tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll <filter>`; exact filters/stdout included. 94 checks across selected filters pass. This is not full-suite coverage.
- [CLI](cli.json): 12 scripts, all pass; controlled local HTTP with scripted v8 responses. Includes all 11 backlog scripts plus shared mode/questions consumer. No real model comparison.
- [Web](web.json): 7 scripts pass. Browser scripts use `NODE_PATH` pointing to the existing bundled Playwright and `BROWSER_EXECUTABLE=/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`; no npm install/build.
- [Build and MockDemo](build-and-demo.json): Harness build has 2 existing PDF platform warnings, CLI zero, MockDemo 7 existing fake/platform warnings; all builds succeed. MockDemo resource workflow passes.
- [Dependency inventory](dependencies.json): regenerate with `python3 tools/audit-v8-dependencies.py docs/audits/evidence/v8-architecture-2026-10-26/dependencies.json`. 18 project/import files, 147 focused sources. No project-reference cycle detected. Lexical candidates are not semantic resolution or dead-code evidence; conditions and dynamic routes remain explicit limitations.
- [Scope](scope.json), [tracked diffstat](diffstat.txt): excludes concurrent UI work observed during implementation. New reports/inventory are listed separately; no commits created. Source hashes pin the inventory to worktree bytes, not a fabricated final commit.

Before/after findings and remaining crash/layout/legacy/performance/host gates:
[audit](../../v8-architecture-drift-2026-10-26.md).
Fault injection checks exception boundaries and stale authority, not OS power loss.
Perf measurements were not run; no throughput/RSS improvement is claimed.
The evidence is historical after acceptance and does not qualify later edits.
