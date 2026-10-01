# Host Fabric and launch profiles

## Status and scope

This is a deferred product target contract, not part of the working baseline or a
prerequisite for ordinary bug fixes. It requires an explicit user scope and owner;
the design does not qualify existing Office/COM behavior.

The user-facing cross-host workflow already exists: Desktop, VSTO and the
in-process NativeHostCli panel can create document-bound chats for Excel, Word,
PowerPoint and Outlook and switch the current pane to an existing chat's open
target. The accepted run stays pinned to its exact document session. See
[Desktop runtime](desktop-runtime.md#runtime-path).

Current selection uses `OfficeHostChatCoordinator` and
`OfficeComAdapterProvider`: they discover or activate an Office target, bind a new
COM adapter/controller, verify the selected document/chat, then rebind the pane.
NativeHostCli is still a DLL loaded into one Office process; its panel starts at
the supplied host/HWND but can switch to a different host/document adapter.
Desktop has a cross-host picker. Discovery/attach uses Office COM/ROT and, for
Excel, an HWND path; multi-instance enumeration remains best-effort. The adapter
retains a bound document session, but cross-process work is not routed through an
endpoint running in the selected document's owning Office process.

Host Fabric is the deferred replacement for that discovery/routing boundary. Its
goal is an exact registry of live host endpoints and typed requests executed on
the selected owner's UI STA. It keeps the existing document chats, panel workflow,
`AgentKernel`, `ToolRuntime`, `HostRuntime` and durable event/CAS ownership.

## Target ownership

Every running add-in instance publishes an ephemeral `HostEndpoint`:

- `HostInstanceId`, host kind, process id, bitness, Windows session/user identity;
- endpoint nonce/protocol version and heartbeat lease;
- document/window descriptors with `DocumentSessionId`, stable/runtime identity,
  title, path when available, HWND and liveness;
- current capability-pack revision and busy/running state.

Descriptors contain no COM references and no document contents. A per-user,
access-controlled local transport routes typed requests to the endpoint that owns
the document. The owner marshals Office work to its UI STA and retains the existing
`DocumentSession`, `HostRuntime`, document gate, preparation, confirmation and
read-back boundaries.

```text
window in any host
    -> Host Registry (descriptors and leases only)
        -> selected owning HostEndpoint
            -> AgentKernel / ToolRuntime / HostRuntime
                -> owner UI STA -> owner Office object model
```

The registry is not a second chat store, capability authority or COM broker. Chat
events/CAS remain authoritative. A tool pack comes from the selected endpoint and
is pinned before a run.

## Selection and run rules

- The picker may list every live target, filter by host and activate its Office
  window. Document content and selection are read only on demand from the owner.
- `Auto follow` may change the selected UI target between turns. An accepted run is
  pinned to exact `HostInstanceId + DocumentSessionId + runtime identity`; focus or
  dropdown changes never retarget an in-flight run.
- Closing/restarting an endpoint makes the target unavailable. There is no silent
  ROT fallback to another instance. Existing event recovery decides whether an
  unfinished effect is `interrupted_unknown`.
- Save As may change stable identity only through the existing document-identity
  migration. A stale descriptor cannot authorize a call.
- Remote activation, selection reads and mutations are separate typed operations;
  the UI cannot send arbitrary COM or reflection requests.

## Transport profiles

Two implementations may satisfy the same `IHostRegistry`/`IHostEndpoint` contracts:

1. **Approved broker (recommended).** A signed, IT-deployed per-user broker owns the
   registry and routing. It has no Office COM authority; commands still execute in
   the owning add-in process. This gives the clearest lifecycle, audit and update
   boundary.
2. **Office-only peer rendezvous.** When a separate custom process is forbidden,
   signed add-ins publish same-user named-pipe endpoints through an ephemeral,
   ACL-protected rendezvous lease. Any RNAssistant window can connect directly.
   All target hosts must have the add-in loaded; stale leases fail closed. This
   removes a standalone RNAssistant EXE but is less resilient than an approved
   broker.

Direct cross-process COM from the window-owning Office process is not the target:
it recreates ROT ambiguity, wrong-instance risk and STA/lifetime coupling.

## Office-hosted launcher

A visible top-level RNAssistant form may live inside a dedicated Excel process. A
command such as `excel.exe /x "...\RNAssistantLauncher.xlsx"` can create a separate
Excel process; after the signed add-in recognizes the exact launcher document and
the agent form is ready, it may hide only that launcher Office instance. Microsoft
documents `/x` as a new Excel process and `/e` as suppressing the startup UI, but
also states that multiple Office switches in one launch are unsupported. Therefore
the design must not depend on `/x /e` together.

This profile is an interactive launcher, not security isolation or unattended Office
automation. Office prompts, recovery, add-in disablement, crashes and policy still
affect it. The form must be unowned by the hidden Office HWND, shown in the taskbar,
and close the launcher process only after active runs have reached a durable
boundary. The existing NativeHost form defaults (`OwnerWindow`, no taskbar entry)
do not yet meet that contract.

There is no process-free desktop window: Office itself and WebView2 runtimes are
processes. A VSTO/native add-in also ships executable DLL code. This profile must be
signed and approved; using Office, `rundll32`, macros or another system binary to
bypass application-control policy is explicitly out of scope.

An Office Web Add-in is a possible centrally deployed UI profile, but its task-pane
and dialog runtimes do not provide unrestricted local files/process execution. It
would still need an approved native companion for the Local Automation Agent.

Microsoft references:

- [Command-line switches for Microsoft Office products](https://support.microsoft.com/en-us/office/lifecycle/command-line-switches-for-microsoft-office-products)
- [VSTO custom task panes and window ownership](https://learn.microsoft.com/en-us/visualstudio/vsto/custom-task-panes?view=visualstudio)
- [Runtimes in Office Add-ins](https://learn.microsoft.com/en-us/office/dev/add-ins/testing/runtimes)
- [Considerations for unattended automation of Office](https://learn.microsoft.com/en-us/office/client-developer/integration/considerations-unattended-automation-office-microsoft-365-for-unattended-rpa)

## Deferred implementation slices (historical Phase 11 plan)

1. Contracts only: endpoint/target/lease DTOs, protocol versioning and fail-closed
   target pinning; no transport or UI switch.
2. One-process inventory: multiple windows/documents in one Excel instance with
   exact selection and active-run pinning.
3. Cross-process Excel through the approved broker profile, then peer rendezvous if
   required; no ROT fallback.
4. Word, PowerPoint and Outlook each complete their local DocumentSession,
   resources/tools/effect-evidence and qualification vertical before its endpoint
   adapter is admitted. Tool availability shown by the window comes from that
   selected endpoint's exact catalog revision; see [Tool Library](tool-library.md).
5. Unified picker, activation and auto-follow UX.
6. Office-hosted launcher profile as a separate optional slice.

Windows x64 + Office x64 gates must cover parallel instances, mixed host processes,
modal/busy Office, Save As, close/reopen, endpoint crash, stale/replayed descriptors,
bitness mismatch, add-in unload and an in-flight run while the visible window changes
target. No host is called supported before its endpoint pack passes those gates.
