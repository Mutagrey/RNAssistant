# ADR-0012: Derived SQLite projection for large chats

Date: 2026-10-02
Status: Accepted for host-neutral implementation; Windows delivery evidence open.

## Context

Chats of 30–50 MiB required a complete JSONL scan and replay after process restart.
The in-memory projection cache helped repeated reads, but could not remove cold
read cost or supply an older message page without materializing the full aggregate.
The bridge already limits the initial transcript to 80 messages, so sending the
entire JSONL to WebView was not the measured cause. Full stream validation on
every navigation also competed with other UI work.

## Decision

- Keep append-only JSONL events and immutable CAS bodies as the only chat authority.
  SQLite is a per-chat, versioned, disposable read projection. It contains root
  metadata, a header reducer checkpoint, normalized message/artifact rows and an
  exact `(session, sequence, hash, byte length, next offset, tail offset, mtime,
  protection key)` cursor.
- On first use of an older chat, validate and index its complete stream in the
  bridge worker. Show navigation progress in the chat list. A new chat gets an
  index from its first append. This work may take time proportional to the source.
- On routine reads, validate the indexed tail record. If the file grew, validate
  and apply only the suffix. A shorter, replaced, incompatible or incomplete
  stream causes a full rebuild. Page reads select only requested message rows;
  active conversation execution still materializes the complete aggregate.
- Flush JSONL before updating SQLite in one local transaction. A failed index
  update leaves the source intact; a later read catches up or rebuilds. The index
  may be deleted at any time. No dual write or automatic source migration occurs.
- Use the managed SQLite provider against Windows `winsqlite3.dll`. Do not bundle
  a native SQLite binary. The harness uses the platform `sqlite3` provider.
- Encrypt SQLite payload blobs with the configured history protector. Row IDs,
  counts and cursor metadata remain visible as file metadata, so the sidecar is
  deleted with its chat and moved with the event file.
- Treat the cursor as a trusted checkpoint for ordinary reads. A modified prefix
  that preserves the exact tail and file metadata can remain undetected until a
  full audit. CAS maintenance audit therefore validates every event, compares
  the canonical projection with SQLite and rebuilds a divergent index. Raw
  trajectory/export and CAS reachability continue to read canonical events.

## Consequences

Steady reads avoid repeated 30–50 MiB JSONL scans, and history pages avoid reading
unrequested message bodies. Full aggregate saves still compare messages except for
the direct scalar metadata operations. The first index build is bounded by source
size and must be measured on the reported Windows history. The index is not
execution authority, read-back evidence or a second store for Office/HTML data.
Exact Windows x64/Office x64/WebView2 packaging, responsiveness, corruption and
recovery qualification remain open.
