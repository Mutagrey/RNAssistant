# Changelog

User-visible changes are recorded here. Internal stabilization work is tracked in
[PROGRESS.md](docs/stabilization/PROGRESS.md). A commit is not a release.
Completed product changes add an Unreleased note with their assigned development
version; dated version sections belong to explicit release preparation. See
[Versioning](docs/operations/VERSIONING.md) for bump rules and the transition from
the previous freeze. Unreleased notes do not claim published or qualified builds.

Historical baseline: `v16.0.4`. This file does not reconstruct historical release notes.

## [Unreleased]

### 16.3.2-dev

- A failed plugin source check on first opening now leaves a visible error instead
  of automatically opening the active published release. Source watching, manual
  retry and the existing preview after a failed refresh are preserved.

### 16.3.1-dev

- Context recovery preserves runtime unknown outcomes even when an opaque tool
  returns `ok`. The isolated new journal selects bounded context from its index,
  with compact unresolved-operation notices and addressed detail/replay-guard
  queries instead of a record-count limit. Production storage cutover is separate.

### 16.3.0-dev

- NativeHostCli gains integrated window controls, Compact/Companion modes, pin and
  saved window placement. Closing hides the window while retaining the session;
  opening from another Office window preserves the current chat and running work.
  Native Windows/Office qualification and visual acceptance remain open.

### 16.2.5-dev

- Saved HTML, raw HTML and the live editor use verified source capabilities.
  Foreign target responses cannot replace the preview; read-only sources remain
  viewable while editing and annotations are disabled.

### 16.2.4-dev

- Model context preparation reuses exact read-body metadata and hashes within each
  request, reducing repeated parsing while preserving body identity, omission and
  actual-delivery guards.

### 16.2.3-dev

- Active run progress/checkpoints and status reads no longer copy chat history.
  Catalogs use committed metadata; Office/Workspace Stop addresses the existing
  exact-scope queue without history hydration.

### 16.2.2-dev

- Model context snapshots, compiler copies and wire provenance share immutable text
  and evidence without JSON roundtrips. Mutable request/repair metadata remains
  detached, including nested confirmation previews and resource target outcomes.

### 16.2.1-dev

- Frozen skill catalogs reuse immutable instruction text without JSON roundtrips
  on each getter; mutable dependency/reference metadata remains isolated per request.

### 16.2.0-dev

- Records the accumulated unreleased UI, Workspace/Browser and agent capabilities
  after adoption of completed-change versioning; this is not a release or Office qualification.
- Context receipts retain all delivered-message provenance while limiting excluded
  history details to 16 examples plus reason counts in both hosts and dispatch manifests.
  Full-session runtime migration remains in progress.

Previous alpha `16.1.0-alpha.1` was prepared in source; it was not a published or qualified release.

### Tools exploration alpha — 2026-10-26

- Shared bounded literal/regex resource search across workspace and Office VBA/HTML,
  with semantic continuation and explicit incomplete-result reporting.
- Recursive workspace discovery with include/exclude globs.
- Correct active VBA tool display IDs and show search coordinates in result previews.
- Product version advances from `16.1.0-dev` to `16.1.0-alpha.1`; assembly binding
  remains `16.0.4.0`. Windows/Office qualification and signed release evidence are open.

### Added

- Qualification Center runs versioned in-app host checks with durable typed evidence;
  release admission now verifies a signed manifest for the exact candidate build.

### Changed

- Introduced the `16.1.0-dev` build identity during stabilization: commit SHA and
  `.dirty` markers distinguish builds without automatic build-time version bumps.

### Fixed

- Runtime now assigns tool-call IDs before acceptance and preserves them through
  confirmation and replay. The v4 model contract contains only tool names and
  arguments, removing ID-collision repairs that could regenerate useful HTML.
  Existing v3 chats require an explicit new chat/reset; saved prompts need review.
- Confirmation and normal runs now share runtime accounting; recorded effects survive
  chat replay, cancellation and failures while preparing the next model request.
- The configured format-attempt limit now includes the first response: 20 stops
  after 20 invalid responses. Transient timeout/network/server failures have a
  separate budget of two retries per model step; schema fallback also works during
  format repair. Cancellation prevents accepting a late response.
- Format repair now retains explicitly loaded media until the model step finishes,
  so a corrected response sees the same image/audio context as the first attempt.
- Runtime warnings now remain visible after failed or uncertain tool mutations,
  even when the model reports completion. Answers without writes do not certify
  applied changes.

### Removed

### Security
