# ADR-0014: Independent Data Templates application and file-owned data projects

Status: accepted for scoped implementation, 2026-10-16. M0 is partial.
Owner: Data Templates domain. Baseline reviewed: `c33da7df`.

Distribution manifest and root mounting are superseded by
[ADR-0015](ADR-0015-plugins.md) on 2026-10-19. The historical decision below uses the
former manifest name; current consumers use only `plugin.json` and the isolated plugin.

## Decision

Data Templates is an independent web application developed as a workspace project.
Its authoritative source now lives in `web/data-templates`: the same UI, plain-JS
engine, Worker, examples and portable tests are used inside and outside RNAssistant.
Co-location in this repository is a delivery choice, not a runtime dependency.
No Office, AgentKernel, ToolRuntime, Resource Fabric, CAS or journal is required
for the browser application. RNAssistant's adapter reuses those existing owners
when the agent develops/uses the application or the user grants native capabilities.
C# owns only host integration, exact source/session guards and native publication;
COM remains in OfficeHosts. Neither owns a second template interpreter.

The application project (HTML/CSS/JS, schemas, tests and docs) and user data projects
(sources, templates, dictionaries, mappings and results) are different scopes.
`application.json` is the distribution manifest; a data project's `project.json`
does not install executable code or confer native permission. Agent skills describe
development/use workflows; tools expose existing file/test operations and typed
application commands. Ordinary import must not depend on a conversation or agent run.

Data Templates consumes the workspace architecture in
[ADR-0013](ADR-0013-workspace-first-runtime.md). Workspace identity/session/file
ownership stays generic; `project.json` adds domain configuration inside that
scope. Excel is a separately bound source, not the project's owner. Implement the
necessary generic workspace capabilities before an Office-specific project API;
this does not require migrating all old authored artifacts first. The product
remains C# 7.3/.NET Framework 4.8. The existing .NET 8 CLI/Harness is a separate
development host, not a runtime requirement of Data Templates.

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

## Independent application clarification — 2026-10-16

The original proposal's early standalone/export requirement precedes further
native parser dispatch. The former M4-only export sequencing was too late.
Application UI, processing queue, parsing, recognition, template language, mapping,
validation and canonical export belong to this project; RNAssistant provides
optional file/Office/execution capabilities through adapters. Its existing native
queue prepares immutable sources only and must not become another business pipeline.

The first cut moves the shared loader/core and its tests into the standalone source
project and adds explicit folder export. It retains existing behavior; full data
project persistence, processing UI and dataset export are not completed by that move.
The root RNAssistant UI still loads only installed, shipped UI scripts. Editable
workspace JS needs a dedicated isolated viewer/executor, never root-script injection.

Trusted host transport must stay outside editable application code. Each future
native run captures and hashes an immutable application package, input templates,
dictionaries and granted source capabilities. App updates should change those
captured files without recompiling C# when the host protocol is unchanged.
The current compiled `TabularRuntimePin` remains a temporary conservative adapter;
do not remove its checks until scoped package admission, isolation and revocation
replace it. Owners/consumers/removal gates are in
[MIGRATION_MAP](../stabilization/MIGRATION_MAP.md#data-templates-application-adapters--2026-10-16).

Folder/ZIP is the primary portable distribution; a single HTML is a derived format.
The existing HTML Library may link/launch/export an application once its capability
contract is implemented. It must not become a second editable store or implicitly
include large private source files in an application package. Browser and native
adapters expose capability differences explicitly.

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
