# Trajectory export bundle

Trajectory export is an on-demand, disposable projection for offline analysis and future regression fixtures. It is built only from a complete validated chat `*.events.jsonl` stream and verified `chat-blobs`; it is never saved as RNAssistant state and cannot become a second source of truth.

## Contents

- `events.jsonl`: selected source events in an explicit export schema, retaining source sequence, event id, correlations, and source-chain hashes.
- `views/<view>.json`: selected derived rows when a non-raw view is exported, including complete `sourceEventSeqs` and source event ids.
- `manifest.json`: source head evidence, filters, redaction notice, CAS-reference locations, inclusion status, and hashes/sizes of preceding files.
- `checksums.sha256`: hashes of all preceding files, including `manifest.json`.
- `cas/<sha256>.blob`: optional verified plaintext payload bodies, available only in full no-redaction mode.

The bridge also reports the complete ZIP SHA-256. Exports are capped at 5,000 source events, 2,000 derived rows, 32 MiB uncompressed, and 24 MiB compressed; a larger request must be narrowed.

## Delivery and lifetime

`TrajectoryExportDownloadService` connects the existing Core exporter to the shared
`ResourceDataPlaneService`/`ResourceDataRouter`. The typed bridge returns only
metadata and a download capability; there is no base64 body or legacy fallback.
The ZIP is a transient owned buffer identified by its exact payload SHA-256, not a
new published resource/head, artifact, CAS retention root or durable export store.
Its source manifest remains pinned to the one completely validated event snapshot.
Later appends cannot change an open download or trigger another source capture.

Capacity is reserved before complete stream validation and ZIP construction, which
run off the UI thread. At most two downloads share the data plane's 64-lease/four-open
limits and 50 MiB transfer-buffer budget with attachment uploads. Binary GET requests
to `/v1/download/<opaque-lease>` consume exact sequential byte ranges up to 256 KiB,
with one in-flight operation per lease. Expiry is ten minutes. Close, cancellation,
chat deletion and runtime disposal release buffers; an in-flight operation retains
its reservation until it actually exits. A failed read is not retried automatically.

The exporter checks cancellation during selection, serialization, CAS inclusion and
compression. JSON/ZIP output is byte-limited while written, and oversized CAS refs
are rejected before hydration. Complete cold stream validation is still required;
this does not close the separate bounded-history/checkpoint optimization gate.

The UI bounds each response and the complete archive, verifies the full SHA-256
before creating a download, and closes the lease on success or failure. A cancelled
or late response for a changed diagnostic context cannot download or rebind to the
active chat. Actual Windows/WebView2 binary delivery, cancellation and downloaded-ZIP
qualification remain open.

## Redaction modes

- `metadata` (default): removes event/row data and content-derived row titles, hides the search phrase, and excludes CAS bodies. Identifiers, times, event types, correlations, source hashes, usage totals, and CAS reference metadata remain.
- `secrets`: recursively replaces values whose property names identify common credentials. It excludes CAS bodies, but prompts, document text, tool arguments/results, filenames, and titles may remain sensitive. This is field redaction, not content classification.
- `none`: includes decrypted event/row data. The user may additionally include CAS bodies; every body is decrypted, length/hash verified, and then written as plaintext into the ZIP.

RNAssistant never adds its configured API key, custom history secret, derived protection keys, authorization headers, or protection key ids to this bundle. Full export must still be handled as sensitive because arbitrary provider/document content already present in canonical history—including credential-looking user data—is preserved.

## Integrity semantics

Canonical event validation happens before selection. An incomplete/corrupt/unreadable stream fails the whole export, and a requested missing/corrupt/unreadable CAS body also fails it. Source hashes remain in exported records and the source head remains in the manifest as evidence.

Metadata and secret redaction change canonical event data, so the exported rows cannot reproduce the original event-chain hashes and are not a replacement history. Bundle checksums protect transport of the projection itself; HMAC-authenticated canonical history still requires its original secret to authenticate independently.
