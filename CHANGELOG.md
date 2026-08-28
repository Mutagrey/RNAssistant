# Changelog

User-visible changes are recorded here. Internal stabilization work is tracked in
[PROGRESS.md](docs/stabilization/PROGRESS.md). A commit is not a release.

Historical baseline: `v16.0.4`. This file does not reconstruct historical release notes.

## [Unreleased]

### Added

### Changed

- Development builds target `16.1.0-dev` and include commit identity; local changes
  are marked `.dirty` without incrementing the product version.

### Fixed

- Format repair now retains explicitly loaded media until the model step finishes,
  so a corrected response sees the same image/audio context as the first attempt.
- Runtime warnings now remain visible after failed or uncertain tool mutations,
  even when the model reports completion. Answers without writes do not certify
  applied changes.

### Removed

### Security
