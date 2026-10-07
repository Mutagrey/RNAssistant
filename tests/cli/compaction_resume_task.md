# Complete the local release notes

Work only in this workspace. This task can be interrupted and resumed; continue
the existing work and preserve any edits made outside the agent.

1. Create `receipt.txt` with exactly `RELEASE_NOTES_STARTED` and a final newline.
   Create it once. If it already exists on continuation, retain it unchanged.
2. Read `manifest.txt`. Process its eight note files in the listed order. For each
   file, read its current complete contents, change only the header line
   `status: draft` to `status: ready`, and read back the changed file before moving
   to the next note. Already-ready notes need no repeated write.
3. After all eight notes are ready, reread `notes/01.md` and change only its header
   `review: pending` to `review: complete`. Read the result again. Preserve every
   other line, including the current `operator_note` value; never restore an older
   value from memory or a summary.
4. Read `receipt.txt` and verify the requested final files. Finish only when all
   eight current notes have been read after their last change, the first note's
   review is complete, and the receipt and manifest remain intact.

The numbered detail lines are data to preserve byte for byte. Do not rewrite or
summarize them in the files. Use the workspace tools and concise progress messages.
No browser, Office, external services or extra output files are needed.
