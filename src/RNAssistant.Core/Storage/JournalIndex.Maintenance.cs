using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal static partial class JournalIndex
    {
        // Called only under the existing exclusive maintenance/quiescence gate.
        // A private rebuilt projection validates the same reducers and complete
        // cursor set; neither the production index nor its negative answers are
        // used as authority. Journals are strictly read-only in this operation.
        internal static IReadOnlyList<JournalStream> ScanCanonical(AppDataPaths paths, StorageProtector protector,
            CasReachabilityScan references, CancellationToken token = default(CancellationToken))
        {
            var streams = StorageJournal.Inventory(paths);
            if (streams.Count == 0) return streams;
            var scratch = Path.Combine(paths.Root, "index.audit." + Guid.NewGuid().ToString("N") + ".sqlite");
            var contextBodies = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using (var db = Open(paths, scratch)) { }
                foreach (var stream in streams)
                {
                    token.ThrowIfCancellationRequested();
                    var conversation = stream.Resources ? null : new ConversationJournal(paths, stream.Identity, protector);
                    var journal = conversation?.Journal ?? new ResourceJournal(paths, protector).Journal;
                    var cursor = journal.Scan((record, position) => {
                        foreach (var reference in stream.Resources ? ResourceJournal.CanonicalReferences(record) : conversation.CanonicalReferences(record))
                            references.AddReference(reference.ToBlobReference(), "storage-journal", stream.Identity, record.Sequence.ToString());
                        if (conversation != null) conversation.ScanChatContextReferences(record, references, contextBodies);
                        if (record.Kind == "run.checkpoint" || record.Kind == "chat.committed" && ConversationJournal.HasCheckpoint(record))
                        {
                            if (conversation == null) throw new InvalidDataException("Run checkpoint in resource stream.");
                            conversation.ValidateRunClosure(record);
                        }
                    }, token);
                    if (stream.Resources) new ResourceJournal(paths, protector).ReplayInto(scratch, token, recover: false);
                    else conversation.ReplayInto(scratch, token, recover: false);
                    using (var db = Open(paths, scratch))
                    {
                        var projected = LoadCursor(db, stream.Identity);
                        if (projected == null || projected.Sequence != cursor.Sequence || projected.Hash != cursor.Hash ||
                            projected.End != cursor.End || projected.Segment != cursor.Segment)
                            throw new InvalidDataException("Canonical inventory and rebuilt projection cursors differ.");
                    }
                }
                if (references.Issues.Count != 0) throw new InvalidDataException("Canonical reference closure is incomplete.");
                return streams;
            }
            finally
            {
                foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
                    if (File.Exists(scratch + suffix)) File.Delete(scratch + suffix);
            }
        }
    }
}
