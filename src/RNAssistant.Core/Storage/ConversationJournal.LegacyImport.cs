using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed class ConversationLegacyImport
    {
        public string SourceId { get; set; }
        public long SourceRevision { get; set; }
        public PayloadRef Snapshot { get; set; }
        public PayloadRef Manifest { get; set; }
    }
    internal sealed class ConversationLegacyManifest
    {
        public IReadOnlyList<PayloadRef> Chunks { get; set; }
        public IReadOnlyList<PayloadRef> References { get; set; }
        public IReadOnlyList<ChatArtifact> Snapshots { get; set; }
        public long ToolSequence { get; set; }
        public long EvidenceSequence { get; set; }
    }

    internal sealed partial class ConversationJournal
    {
        private const long LegacyMaximumBytes = 64L * 1024 * 1024;
        private static string LegacyImportOperation(string chat, string source, long revision)
        { return "legacy-chat-import:" + TextPatternEngine.Sha256(chat + "\n" + source + "\n" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        internal LegacyChatImportResult ReadLegacyImport(string chat, string source, long revision, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(source) || revision < 1) throw new ArgumentException("Exact import identity is required.");
            return Access((db, cursor) => {
                var retained = FindChatWrite(db, LegacyImportOperation(chat, source, revision), chat);
                if (retained == null) return null;
                return LegacyImportResult(retained, source, revision, true, CurrentChat(db, chat) == null);
            }, token);
        }

        private static LegacyChatImportResult LegacyImportResult(ConversationChatReceipt receipt, string source,
            long revision, bool duplicate, bool deleted)
        {
            var proof = receipt.Fact.LegacyImport;
            if (proof == null || proof.SourceId != source || proof.SourceRevision != revision)
                throw new InvalidDataException("Import receipt does not match its exact source.");
            var address = receipt.Fact.Card.Address;
            return new LegacyChatImportResult { Status = duplicate ? "alreadyImported" : "imported", SourceId = source,
                SourceRevision = revision, ChatId = address.Id, Host = address.Host, DocumentKey = address.DocumentKey,
                WorkspaceId = address.WorkspaceId, DocumentAuthorityId = address.DocumentAuthorityId,
                MessageCount = receipt.Fact.MessageCount, Revision = receipt.Position.Sequence, TargetDeleted = deleted };
        }

        internal LegacyChatImportResult ImportLegacyChat(ChatSession source, JObject snapshot, string sourceId,
            long sourceRevision, CancellationToken token)
        {
            var operation = LegacyImportOperation(source.Id, sourceId, sourceRevision);
            var accepted = ReadLegacyImport(source.Id, sourceId, sourceRevision, token);
            if (accepted != null) return accepted;
            Access((db, cursor) => { RequireUnusedLegacyTarget(db, source.Id); return true; }, token);
            ConversationChatCommit fact;
            try
            {
                var json = snapshot.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(json) > LegacyMaximumBytes) throw new InvalidDataException("Снимок истории превышает 64 MiB.");
                if (source.Messages.Any(item => item == null || item.LegacyHistory != null) ||
                    source.Messages.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != source.Messages.Count)
                    throw new InvalidDataException("Повторяющиеся или неподдерживаемые сообщения старой истории.");
                var references = LegacySnapshotReferences(snapshot, token).ToList();
                var address = new ConversationMetadata(source.Id, 0, source.Host, source.DocumentKey, source.WorkspaceId, source.DocumentAuthorityId);
                var policy = new ConversationRunPolicy(source.Mode == ChatModes.Plan ? ChatModes.Plan : ChatModes.Agent, source.MutationPolicy);
                var card = new ConversationCard(source.Id, source.Title, policy, source.CreatedUtc,
                    address: address, documentTitle: source.DocumentTitle, documentPath: source.DocumentPath, selectedModel: source.Model,
                    reasoningEnabled: source.ReasoningEnabled, titleAssignedByAgent: source.TitleAssignedByAgent,
                    previousDocumentKeys: source.PreviousDocumentKeys);
                ValidateChatAddress(card);
                var entries = source.Messages.Select(message => PublishLegacyText(message, sourceId, sourceRevision)).ToArray();
                // The old conversation intentionally omitted document-owned
                // artifact metadata. Reconstruct membership from the exact refs
                // after their native immutable resource publication, before the
                // native chat working-set manifest is prepared.
                var importedResources = ImportLegacyResources(source, entries, token);
                var artifacts = ResourceStore();
                foreach (var reference in importedResources)
                {
                    token.ThrowIfCancellationRequested();
                    var artifact = artifacts.InspectMetadata(address, reference);
                    if (artifact.AvailabilityIssue != null) throw new InvalidDataException("Метаданные перенесённого оригинала недоступны.");
                    if (!source.Artifacts.Any(item => item.Id == artifact.Id)) source.Artifacts.Add(artifact);
                }
                var chunks = new List<PayloadRef>();
                for (var offset = 0; offset < entries.Length; offset += 64)
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = entries.Skip(offset).Take(64).ToArray();
                    var text = JsonConvert.SerializeObject(chunk);
                    if (Encoding.UTF8.GetByteCount(text) > LegacyMaximumBytes) throw new InvalidDataException("Слишком большой блок метаданных истории.");
                    chunks.Add(PayloadRef.FromBlob(_bodies.StoreText(text, "application/json")));
                    references.AddRange(chunk.SelectMany(item => new[] { item.Body, item.Reasoning, item.Analysis }).Where(item => item != null));
                }
                // Retain user-owned instructions/data; runtime observations and
                // compaction/continuation are archived in Snapshot, never resumed.
                source.Context = source.Context ?? new DocumentContext();
                source.Context.Notes = (source.Context.Notes ?? new List<ContextNote>()).Where(note =>
                    note != null && (note.Role == ContextNoteRole.UserInstruction || note.Role == ContextNoteRole.SuppliedData)).ToList();
                source.LastRun = null; source.LastContextReceipt = null; source.ActiveContextCheckpointId = null;
                var context = PrepareContextNotes(source);
                var resources = PrepareResourceSet(source);
                var local = PrepareSnapshotCandidates(source);
                var sourceBody = PayloadRef.FromBlob(_bodies.StoreText(json, "application/json"));
                references.Add(sourceBody); references.AddRange(chunks);
                references.AddRange(local.Select(SnapshotBody));
                var manifest = new ConversationLegacyManifest { Chunks = chunks, Snapshots = local,
                    References = references.GroupBy(ReferenceKey, StringComparer.Ordinal).Select(group => group.First()).ToArray(),
                    ToolSequence = Math.Max(source.ToolCallReferenceSequence, source.Messages.Select(message => message.ToolCallReference?.Ordinal ?? 0).DefaultIfEmpty(0).Max()),
                    EvidenceSequence = Math.Max(source.EvidenceReferenceSequence, source.Messages.Select(message => message.EvidenceReference?.Ordinal ?? 0).DefaultIfEmpty(0).Max()) };
                var manifestText = JsonConvert.SerializeObject(manifest);
                if (Encoding.UTF8.GetByteCount(manifestText) > LegacyMaximumBytes) throw new InvalidDataException("Слишком большой каталог ссылок истории.");
                fact = new ConversationChatCommit(card, 0, entries.Length, new ConversationTextEntry[0], context, source.UpdatedUtc,
                    resources: resources, legacyImport: new ConversationLegacyImport { SourceId = sourceId, SourceRevision = sourceRevision,
                        Snapshot = sourceBody, Manifest = PayloadRef.FromBlob(_bodies.StoreText(manifestText, "application/json")) });
                ReadLegacyManifest(fact);
                ValidatePublishedArtifacts(source, entries);
            }
            catch (LegacyChatImportException) { throw; }
            catch (Exception error) when (LegacyRecoveryFiles.ReadFailure(error) || error is NotSupportedException)
            { throw ChatStore.LegacyImportFailure("unavailable", "История не перенесена: " + LegacyRecoveryFiles.Error(error)); }
            var record = DeliveryRecord(operation, "chat.imported", fact, ChatFingerprint(fact));
            // Verify the complete closure before append. A missing embedded CAS
            // reference must not become a valid-looking, partially restored chat.
            foreach (var reference in LegacyImportReferences(fact))
            {
                token.ThrowIfCancellationRequested();
                if (!_bodies.HasVerifiedReference(reference.ToBlobReference()))
                    throw ChatStore.LegacyImportFailure("unavailable", "Байты истории или вложения недоступны; чат не опубликован.");
            }
            return Access((db, cursor) => {
                var prior = FindChatWrite(db, operation, source.Id);
                if (prior != null) return LegacyImportResult(prior, sourceId, sourceRevision, true, CurrentChat(db, source.Id) == null);
                RequireUnusedLegacyTarget(db, source.Id);
                ValidateLegacyImport(db, record, fact);
                token.ThrowIfCancellationRequested();
                var position = Journal.Append(cursor, operation, record.Fingerprint, record.Kind, record.Data);
                // Once append has started, failures are unknown to the caller and
                // can only be reconciled by the read-only import receipt query.
                Fault?.Invoke("after_legacy_import_append");
                ApplyLegacyImport(db, record, position);
                return LegacyImportResult(new ConversationChatReceipt(fact, operation, position), sourceId, sourceRevision, false, false);
            }, token);
        }

        private static void RequireUnusedLegacyTarget(SqliteConnection db, string chat)
        {
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT 1 FROM chat_card WHERE chat=$chat LIMIT 1";
                command.Parameters.AddWithValue("$chat", chat);
                if (command.ExecuteScalar() != null)
                    throw ChatStore.LegacyImportFailure("target_exists", "Этот ID уже занят текущим чатом или его удалением. Перенос не заменяет существующую историю.");
            }
        }

        private IEnumerable<PayloadRef> LegacySnapshotReferences(JObject snapshot, CancellationToken token)
        {
            var scan = new CasReachabilityScan(); scan.AddTokenReferences(snapshot, "legacy-chat-import", _scope, "snapshot");
            if (scan.Issues.Count != 0) throw new InvalidDataException("Неполная ссылка на сохранённые байты истории.");
            long total = 0;
            foreach (var group in scan.References.GroupBy(item => item.Reference.Sha256, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var reference = group.First().Reference;
                if (group.Any(item => item.Reference.ByteLength != reference.ByteLength)) throw new InvalidDataException("Размеры CAS-ссылок истории не совпадают.");
                total += reference.ByteLength;
                if (reference.ByteLength > 256L * 1024 * 1024 || total > 1024L * 1024 * 1024)
                    throw new InvalidDataException("Вложения истории превышают предел переноса: 256 MiB на объект, 1 GiB суммарно.");
                LegacyRecoveryFiles.RequireRegular(_bodies.PathFor(reference.Sha256));
                if (!_bodies.HasVerifiedReference(reference)) throw new InvalidDataException("Сохранённые байты отсутствуют, повреждены или недоступны с текущим ключом.");
                yield return PayloadRef.FromBlob(reference);
            }
        }

        private ConversationTextEntry PublishLegacyText(ChatMessage message, string source, long revision)
        {
            var header = message.CopyForContext();
            header.LegacyHistory = new LegacyChatHistoryProvenance { SourceId = source, SourceRevision = revision };
            header.ExcludeFromModelContext = header.ExcludeFromModelContext || header.Role != "user" && header.Role != "assistant" || header.ProtocolMessage || header.RuntimeProjection ||
                header.Activity != null || header.ToolCallId != null || header.ToolCalls?.Count > 0 || header.AgentCommunication != null ||
                header.SyntheticResourceObservation || header.CompletedOperation != null || header.ContextClaims?.Count > 0 ||
                header.ResourceEvidence?.Count > 0 || header.RunViewState != null || header.ToolCallReference != null ||
                header.EvidenceReference != null || header.ResourceModelReferences?.Count > 0;
            header.Content = null; header.ReasoningContent = null;
            header.Attachments = AttachmentHeaders(message.Attachments);
            var analysis = message.AttachmentAnalysis == null ? null : PayloadRef.FromBlob(_bodies.StoreText(message.AttachmentAnalysis.Content ?? string.Empty, "text/plain"));
            if (header.AttachmentAnalysis != null) header.AttachmentAnalysis.Content = null;
            return new ConversationTextEntry(header, PayloadRef.FromBlob(_bodies.StoreText(message.Content ?? string.Empty, "text/plain")),
                message.ReasoningContent == null ? null : PayloadRef.FromBlob(_bodies.StoreText(message.ReasoningContent, "text/plain")), analysis);
        }

        private ConversationLegacyManifest ReadLegacyManifest(ConversationChatCommit fact)
        {
            var proof = fact.LegacyImport;
            if (proof?.Manifest == null || proof.Snapshot == null || proof.SourceRevision < 1 || string.IsNullOrWhiteSpace(proof.SourceId) ||
                proof.Manifest.ByteLength > LegacyMaximumBytes || proof.Snapshot.ByteLength > LegacyMaximumBytes)
                throw new InvalidDataException("Invalid import provenance.");
            var value = JsonConvert.DeserializeObject<ConversationLegacyManifest>(ReadChatBody(proof.Manifest));
            if (value?.Chunks == null || value.References == null || value.Snapshots == null || value.Chunks.Count > 3125 ||
                value.References.Count > 100000 || value.ToolSequence < 0 || value.EvidenceSequence < 0 ||
                value.ToolSequence == long.MaxValue || value.EvidenceSequence == long.MaxValue ||
                value.Chunks.Any(item => item == null || item.ByteLength > LegacyMaximumBytes) || value.References.Any(item => item == null) ||
                !value.References.Any(item => ReferenceKey(item) == ReferenceKey(proof.Snapshot)) ||
                value.Chunks.Any(chunk => !value.References.Any(item => ReferenceKey(item) == ReferenceKey(chunk))))
                throw new InvalidDataException("Incomplete import manifest.");
            return value;
        }

        private IEnumerable<PayloadRef> LegacyImportReferences(ConversationChatCommit fact)
        {
            var manifest = ReadLegacyManifest(fact);
            return new[] { fact.Context, fact.Resources, fact.LegacyImport.Manifest }.Where(item => item != null).Concat(manifest.References);
        }

        private ConversationTextEntry[] ReadLegacyChunk(ConversationChatCommit fact, PayloadRef chunk)
        {
            var entries = JsonConvert.DeserializeObject<ConversationTextEntry[]>(ReadChatBody(chunk));
            if (entries == null || entries.Length < 1 || entries.Length > 64 || entries.Any(item => item?.Header.LegacyHistory?.SourceId != fact.LegacyImport.SourceId ||
                item.Header.LegacyHistory.SourceRevision != fact.LegacyImport.SourceRevision))
                throw new InvalidDataException("Invalid imported occurrence chunk.");
            return entries;
        }

        private IEnumerable<PayloadRef> ValidateLegacyImport(SqliteConnection db, JournalRecord record, ConversationChatCommit fact)
        {
            ValidateChatAddress(fact.Card);
            if (record.Kind != "chat.imported" || fact.ExpectedRevision != 0 || fact.Messages.Count != 0 || fact.Run != null ||
                fact.RunState != null || fact.Checkpoint != null || fact.Tools != null || fact.Control != null || fact.Input != null ||
                fact.Fork != null || fact.Card.AgentCreation != null || fact.ModelEvent != null || fact.ContextReceipt != null ||
                fact.History != null || fact.Snapshots != null || fact.References != null || OperationExists(db, record.Operation))
                throw new InvalidDataException("An import is historical presentation, never native execution evidence.");
            RequireUnusedLegacyTarget(db, fact.Card.ChatId);
            var manifest = ReadLegacyManifest(fact);
            if (record.Operation != LegacyImportOperation(fact.Card.ChatId, fact.LegacyImport.SourceId, fact.LegacyImport.SourceRevision))
                throw new InvalidDataException("Import operation does not identify its source.");
            var ids = new HashSet<string>(StringComparer.Ordinal); var attachments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in manifest.Chunks)
            foreach (var entry in ReadLegacyChunk(fact, chunk))
            {
                if (!ids.Add(entry.Header.Id)) throw new InvalidDataException("Duplicate imported occurrence.");
                foreach (var attachment in entry.Header.Attachments)
                    if (!attachments.Add(attachment.Id)) throw new InvalidDataException("Duplicate imported attachment identity.");
            }
            if (ids.Count != fact.MessageCount) throw new InvalidDataException("Import omitted messages.");
            ReadResourceSet(fact.Resources, fact.Card.Address);
            foreach (var artifact in manifest.Snapshots) RequireSnapshotHeader(fact.Card.Address, artifact);
            return LegacyImportReferences(fact);
        }

        private void ApplyLegacyImport(SqliteConnection db, JournalRecord record, JournalCursor position)
        {
            var fact = DecodeChatCommit(record); ValidateLegacyImport(db, record, fact);
            var manifest = ReadLegacyManifest(fact); var chunks = manifest.Chunks.Select(chunk => ReadLegacyChunk(fact, chunk)).ToArray();
            var activity = chunks.SelectMany(chunk => chunk).Select(item => item.Header.CreatedUtc.Ticks).DefaultIfEmpty(fact.Card.CreatedUtc.Ticks).Max();
            using (var tx = db.BeginTransaction())
            {
                RegisterOperation(db, tx, record.Operation, record.Kind); SaveChatMetadata(db, tx, record, position, fact, activity);
                var ordinal = 0;
                for (var index = 0; index < chunks.Length; index++)
                foreach (var entry in chunks[index])
                {
                    using (var command = Command(db, "INSERT INTO chat_message VALUES($stream,$chat,$id,$ordinal,$operation); INSERT INTO chat_import_message VALUES($stream,$chat,$id,$chunk)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", entry.Header.Id);
                        command.Parameters.AddWithValue("$ordinal", ordinal++); command.Parameters.AddWithValue("$operation", record.Operation);
                        command.Parameters.AddWithValue("$chunk", index); command.ExecuteNonQuery();
                    }
                    foreach (var attachment in entry.Header.Attachments)
                    using (var command = Command(db, "INSERT INTO chat_attachment VALUES($stream,$chat,$id,$message,$operation)", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", attachment.Id);
                        command.Parameters.AddWithValue("$message", entry.Header.Id); command.Parameters.AddWithValue("$operation", record.Operation); command.ExecuteNonQuery();
                    }
                }
                foreach (var artifact in manifest.Snapshots)
                using (var command = Command(db, "INSERT INTO chat_snapshot VALUES($stream,$chat,$id,$operation)", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", artifact.Id);
                    command.Parameters.AddWithValue("$operation", record.Operation); command.ExecuteNonQuery();
                }
                using (var command = Command(db, "INSERT INTO chat_sequence VALUES($stream,$chat,$e,$t)", tx))
                {
                    command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$e", checked(manifest.EvidenceSequence + 1));
                    command.Parameters.AddWithValue("$t", checked(manifest.ToolSequence + 1)); command.ExecuteNonQuery();
                }
                Fault?.Invoke("before_legacy_import_index_commit"); SaveCursor(db, position, tx, record); tx.Commit();
            }
        }

        private ConversationTextEntry SavedChatEntry(SqliteConnection db, ConversationChatReceipt write, string id,
            Dictionary<string, ConversationTextEntry[]> chunks = null)
        {
            if (write.Fact.LegacyImport == null) return write.Fact.Messages.Single(item => item.Header.Id == id);
            int index;
            using (var command = Command(db, "SELECT chunk FROM chat_import_message WHERE stream=$stream AND chat=$chat AND id=$id"))
            {
                command.Parameters.AddWithValue("$chat", write.Fact.Card.ChatId); command.Parameters.AddWithValue("$id", id);
                var value = command.ExecuteScalar(); if (value == null) throw new InvalidDataException("Imported occurrence locator is missing.");
                index = Convert.ToInt32(value);
            }
            var key = write.Operation + ":" + index; ConversationTextEntry[] entries;
            if (chunks == null || !chunks.TryGetValue(key, out entries))
            {
                var manifest = ReadLegacyManifest(write.Fact);
                if (index < 0 || index >= manifest.Chunks.Count) throw new InvalidDataException("Imported chunk locator is invalid.");
                entries = ReadLegacyChunk(write.Fact, manifest.Chunks[index]);
                if (chunks != null) chunks[key] = entries;
            }
            return entries.Single(item => item.Header.Id == id);
        }
    }
}
