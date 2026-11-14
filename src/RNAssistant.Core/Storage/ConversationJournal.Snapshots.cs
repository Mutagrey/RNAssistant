using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private static bool IsTaskSnapshot(ConversationMetadata metadata, ResourceRef reference)
        {
            string owner, id; int revision;
            return reference?.IsExact == true && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                owner == metadata.Id && System.Text.RegularExpressions.Regex.IsMatch(id,
                    "^tasks_[0-9a-f]{32}_r" + revision + "_[0-9a-f]{8}$") &&
                ChatResourceUri.ArtifactSnapshot(reference).Uri == reference.Uri;
        }

        private static PayloadRef TaskBody(ChatArtifact artifact)
        {
            var body = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType);
            if (body.ByteLength < 1 || body.ByteLength > TaskListPublication.MaxBodyBytes)
                throw new InvalidDataException("Task-list body exceeds its bounded contract.");
            return body;
        }

        private static TaskListRevisionMetadata TaskMetadata(ChatArtifact artifact)
        {
            var metadata = JsonConvert.DeserializeObject<TaskListRevisionMetadata>(artifact.MetadataJson ?? "null");
            if (metadata == null || !artifact.Id.StartsWith(metadata.TaskListId + "_r", StringComparison.Ordinal) ||
                !new[] { "active", "blocked", "completed", "cancelled", "superseded" }.Contains(metadata.Status))
                throw new InvalidDataException("Invalid task-list revision metadata.");
            return metadata;
        }

        private static void RequireTaskHeader(ConversationMetadata owner, ChatArtifact artifact)
        {
            if (artifact == null || artifact.Kind != ChatArtifactKinds.TaskList || artifact.Revision < 1 ||
                artifact.DocumentAuthorityId != null || artifact.WorkspaceId != null || artifact.InlineText != null ||
                artifact.RelativePath != null || artifact.SourceMessageId != null || artifact.RunId != null ||
                artifact.CreatedUtc.Kind != DateTimeKind.Utc || artifact.MimeType != "application/vnd.rnassistant.task-list+json" ||
                !IsTaskSnapshot(owner, ChatResourceUri.CreateArtifactRevision(owner.Id, artifact)))
                throw new InvalidDataException("Invalid conversation-owned task-list snapshot.");
            TaskBody(artifact); TaskMetadata(artifact);
        }

        private ChatArtifact[] PrepareSnapshotCandidates(ChatSession session)
        {
            return session.Artifacts.Where(item => item.Kind == ChatArtifactKinds.TaskList || item.Kind == ChatArtifactKinds.Compaction).Select(artifact => {
                if (artifact.InlineText != null)
                {
                    if (artifact.Kind == ChatArtifactKinds.TaskList && Encoding.UTF8.GetByteCount(artifact.InlineText) > TaskListPublication.MaxBodyBytes)
                        throw new InvalidDataException("Task-list body exceeds its bounded contract.");
                    var body = _bodies.StoreText(artifact.InlineText, artifact.MimeType);
                    if (artifact.ContentSha256 != null && (artifact.ContentSha256 != body.Sha256 || artifact.ContentByteLength != body.ByteLength))
                        throw new InvalidDataException("Task-list body differs from its immutable snapshot.");
                    artifact.ContentSha256 = body.Sha256; artifact.ContentByteLength = body.ByteLength;
                }
                var header = JsonConvert.DeserializeObject<ChatArtifact>(JsonConvert.SerializeObject(artifact));
                // Message references own presentation links; immutable snapshots do not get rewritten.
                header.InlineText = null;
                if (header.Kind == ChatArtifactKinds.TaskList) { header.SourceMessageId = null; header.RunId = null; }
                RequireSnapshotHeader(ConversationMetadata.From(session), header);
                if (header.Kind == ChatArtifactKinds.Compaction && header.Id == session.ActiveContextCheckpointId)
                {
                    var selected = session.ContextCheckpoints.Where(item => item?.Id == header.Id).ToArray();
                    if (selected.Length != 1 || Tools.TextPatternEngine.Sha256(JsonConvert.SerializeObject(selected[0])) != header.ContentSha256)
                        throw new InvalidDataException("Active checkpoint projection differs from its immutable body.");
                }
                return header;
            }).ToArray();
        }

        private ChatArtifact LocalSnapshot(SqliteConnection db, string chat, string id)
        {
            string operation;
            using (var command = Command(db, "SELECT operation FROM chat_snapshot WHERE stream=$stream AND chat=$chat AND id=$id"))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$id", id);
                operation = command.ExecuteScalar() as string;
            }
            if (operation == null) return null;
            var receipt = FindChatWrite(db, operation);
            var artifact = receipt?.Fact.Snapshots?.SingleOrDefault(item => item.Id == id);
            if (artifact == null || !InheritedFact(db, chat, receipt.Fact.Card.ChatId, receipt.Position.Sequence))
                throw new InvalidDataException("Snapshot locator disagrees with its canonical snapshot.");
            return artifact;
        }

        private IReadOnlyList<ChatArtifact> PrepareSnapshotDelta(SqliteConnection db, string chat, ChatArtifact[] candidates, ConversationChatReceipt prior,
            ConversationFork fork = null)
        {
            var added = new List<ChatArtifact>();
            foreach (var artifact in candidates)
            {
                var retained = LocalSnapshot(db, chat, artifact.Id) ?? (fork == null ? null : LocalSnapshot(db, fork.Parent.Id, artifact.Id));
                if (fork != null && retained == null) throw new InvalidOperationException("A fork cannot invent a local snapshot.");
                if (retained != null && !SameRunValue(artifact, retained))
                    throw new InvalidOperationException("A local snapshot cannot be rebound to different evidence.");
                if (retained == null && artifact.Kind == ChatArtifactKinds.TaskList)
                {
                    var task = JsonConvert.DeserializeObject<ChatTaskList>(TaskListPublication.ReadBody(artifact, _bodies));
                    var metadata = TaskMetadata(artifact);
                    if (task == null || task.Id != metadata.TaskListId || task.Status != metadata.Status || task.Goal != artifact.Title)
                        throw new InvalidDataException("Task-list body disagrees with its metadata.");
                }
                if (retained == null && artifact.Kind == ChatArtifactKinds.Compaction) ReadCheckpoint(artifact);
                if (retained == null || prior?.Fact.Snapshots?.Any(item => item.Id == artifact.Id) == true) added.Add(artifact);
            }
            return added.Count == 0 ? null : added;
        }

        private void ValidateLocalSnapshots(SqliteConnection db, ConversationChatCommit fact, ConversationResourceSet state)
        {
            var added = fact.Snapshots ?? new ChatArtifact[0];
            if (added.Count > 128 || added.Select(item => item?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != added.Count)
                throw new InvalidDataException("Snapshot delta is ambiguous or unbounded.");
            var fork = fact.ExpectedRevision == 0 ? fact.Fork : null;
            var membership = fork == null ? null : new HashSet<string>(state.References.Select(item => item.Uri), StringComparer.Ordinal);
            Func<string, ChatArtifact> resolve = id => added.SingleOrDefault(item => item?.Id == id) ?? LocalSnapshot(db, fact.Card.ChatId, id) ??
                (fork == null ? null : LocalSnapshot(db, fork.Parent.Id, id));
            foreach (var artifact in added)
            {
                RequireSnapshotHeader(fact.Card.Address, artifact);
                if (LocalSnapshot(db, fact.Card.ChatId, artifact.Id) != null || !state.References.Any(item =>
                    SameRunValue(item, ChatResourceUri.CreateArtifactRevision(fact.Card.ChatId, artifact))))
                    throw new InvalidDataException("Local snapshot is repeated or absent from its working set.");
                var parent = artifact.ParentArtifactId == null ? null : resolve(artifact.ParentArtifactId);
                if (artifact.Revision == 1 ? artifact.ParentArtifactId != null : parent == null ||
                    parent.Kind != artifact.Kind || parent.Revision != artifact.Revision - 1 ||
                    artifact.Kind == ChatArtifactKinds.TaskList && TaskMetadata(parent).TaskListId != TaskMetadata(artifact).TaskListId)
                    throw new InvalidDataException("Local snapshot lacks its exact parent.");
            }
            foreach (var reference in state.References.Where(item => IsLocalSnapshot(fact.Card.Address, item)))
            {
                var artifact = resolve(ResourceUri.Parse(reference.Uri).Segments[2]);
                if (artifact == null || !SameRunValue(reference, ChatResourceUri.CreateArtifactRevision(fact.Card.ChatId, artifact)))
                    throw new InvalidDataException("Local snapshot membership lacks its exact snapshot.");
                if (fork != null && artifact.ParentArtifactId != null)
                {
                    var parent = resolve(artifact.ParentArtifactId);
                    if (parent == null || !membership.Contains(ChatResourceUri.CreateArtifactRevision(fact.Card.ChatId, parent).Uri))
                        throw new InvalidDataException("Fork omitted an inherited snapshot parent.");
                }
                if (SameRunValue(reference, state.ActiveTask) && TaskMetadata(artifact).Status != "active" && TaskMetadata(artifact).Status != "blocked")
                    throw new InvalidDataException("A closed task list cannot be selected as active.");
            }
            ValidateContextSelection(db, fact, state, resolve);
        }

        private void ApplySnapshotRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record, ConversationChatCommit fact)
        {
            if (fact.ExpectedRevision == 0 && fact.Fork != null)
                foreach (var reference in ReadResourceSet(fact.Resources, fact.Card.Address).References.Where(item => IsCompactionSnapshot(fact.Card.Address, item)))
                    using (var command = Command(db, "INSERT INTO chat_snapshot SELECT stream,$chat,id,operation FROM chat_snapshot WHERE stream=$stream AND chat=$parent AND id=$id", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$parent", fact.Fork.Parent.Id);
                        command.Parameters.AddWithValue("$id", ResourceUri.Parse(reference.Uri).Segments[2]);
                        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Inherited snapshot locator is unavailable.");
                    }
            foreach (var artifact in fact.Snapshots ?? new ChatArtifact[0])
            using (var command = Command(db, "INSERT INTO chat_snapshot VALUES($stream,$chat,$id,$operation)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", artifact.Id);
                command.Parameters.AddWithValue("$operation", record.Operation); command.ExecuteNonQuery();
            }
        }

        private ChatArtifact ReadLocalSnapshot(SqliteConnection db, ConversationMetadata metadata, ResourceRef reference)
        {
            var artifact = LocalSnapshot(db, metadata.Id, ResourceUri.Parse(reference.Uri).Segments[2]);
            if (artifact == null || !SameRunValue(reference, ChatResourceUri.CreateArtifactRevision(metadata.Id, artifact)))
                throw new InvalidDataException("Local snapshot is unavailable.");
            return artifact;
        }
    }
}
