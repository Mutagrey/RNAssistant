# ADR-0014: Portable Data Templates core and file-owned projects

Status: accepted for scoped implementation, 2026-10-16. M0 is partial.
Owner: Data Templates domain. Baseline reviewed: `c33da7df`.

## Decision

Data Templates is a file-backed tabular transformation project using the existing
AgentKernel, ToolRuntime, Resource Fabric, workspace file owner, CAS and journal.
One portable plain-JavaScript core owns selector/template semantics. C# will own
typed application integration, exact source/session guards and publication; COM
remains in OfficeHosts. It will not implement another template interpreter.

Data Templates consumes the workspace architecture in
[ADR-0013](ADR-0013-workspace-first-runtime.md). Workspace identity/session/file
ownership stays generic; `project.json` adds domain configuration inside that
scope. Excel is a separately bound source, not the project's owner. Implement the
necessary shared workspace application slice before an Office-specific project
API; this does not require migrating all old authored artifacts first. Current
.NET 8 Runtime and .NET Framework 4.8 Office composition need a compatible shared
boundary, not a direct assembly reference or duplicated application runtime.

Templates, dictionaries and mappings are data. Executable JS changes are a
separate developer workflow with exact module capture and publication. Neither
opening a project nor reading its manifest grants native permissions. Project
files are editable authority; retained snapshots are immutable evidence, not a
second mutable HTML aggregate. Existing HTML projects are not migrated implicitly.

Use separate classic scripts and explicit file selection for portable basic
mode. Worker execution belongs to an application service independently of viewer
lifetime. Only the bounded shipped core may use explicit cooperative execution;
unavailable Worker execution is an error, never a hidden switch to another engine
or `RNAssistant.JsWorker.exe`. Arbitrary extensions require a separately qualified
interruptible realm and cannot run inside the privileged root UI.

## Language/core clarification — 2026-10-16

The original proposal's §6.4 is an invariant: parameter, period, unit, dimension
and value roles do not belong to physical rows/columns. §6.7 defers richer layout
constructions, not this invariant. This completes the accepted universal scope;
it is not an additional feature track.

The initial orientation-bound matrix profile is replaced by language revision 2
with explicit semantic bindings over matrix, record/field and JSON-path models.
One typed compiler and observation executor own all three. Old language 1 is
explicitly refused; no fallback, second interpreter or automatic user-file migration
is introduced. The existing selectors, dictionary/value semantics, XLSX reader,
Worker, file authority and preparation queue are retained. Canonical source locations
stay native; CSV/JSON never acquire fabricated Excel coordinates.

Source preparation, immutable execution plans, record validation and provisional
result sinks are distinct from publication. Sink completion finalizes staging only;
abort revokes it. Native parser dispatch must consume the existing queue's exact
source pins, not reconstruct authority from UI metadata. The working foundation,
examples, checks and limits are owned by [Data Templates](../data-templates.md#language-and-execution-core).
CSV/JSON production file readers, other Excel families including XLSB, full output
schemas and native integration remain open. A normalized-source proof does not
qualify those readers or the 80 MB/Office gates.

## Initial boundary and consequences

The first implemented proof covers normalized disjoint capture, bounded selectors,
immutable typed cells, cancellation/deadline, lifecycle and offline parity. It
introduces no native tool, project store, approval path or production capture
claim. The input envelope is a diagnostic probe, not an alternative template DSL.

Office file composition, live capture consistency and field availability, exact
module hashes, WebView2 isolation, project publication and export closure remain
explicit integration gates. A working browser proof cannot close those gates.
Do not increase existing HTML/transfer limits to avoid implementing file-backed
delivery. The full implementation proposal is design input, not proof of shipped
features. Current behavior and targeted checks are maintained only in
[Data Templates](../data-templates.md).
