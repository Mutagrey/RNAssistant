# Changelog

User-visible changes are recorded here. Internal stabilization work is tracked in
[PROGRESS.md](docs/stabilization/PROGRESS.md). A commit is not a release.

Historical baseline: `v16.0.4`. This file does not reconstruct historical release notes.

## [Unreleased]

Alpha `16.1.0-alpha.1` is prepared in source; it is not yet a published or qualified release.

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

- Development builds target `16.1.0-dev` and include commit identity; local changes
  are marked `.dirty` without incrementing the product version.

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
