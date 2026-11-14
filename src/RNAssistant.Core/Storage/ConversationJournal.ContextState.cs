using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private static bool IsCompactionSnapshot(ConversationMetadata metadata, ResourceRef reference)
        {
            string owner, id; int revision; Guid parsed;
            return reference?.IsExact == true && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                owner == metadata.Id && Guid.TryParseExact(id, "N", out parsed) &&
                ChatResourceUri.ArtifactSnapshot(reference).Uri == reference.Uri;
        }

        private static bool IsLocalSnapshot(ConversationMetadata metadata, ResourceRef reference)
        { return IsTaskSnapshot(metadata, reference) || IsCompactionSnapshot(metadata, reference); }

        private static PayloadRef SnapshotBody(ChatArtifact artifact)
        { return artifact.Kind == ChatArtifactKinds.TaskList ? TaskBody(artifact) :
            new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength ?? -1, artifact.MimeType); }

        private static ContextCheckpointMetadata CheckpointMetadata(ChatArtifact artifact)
        {
            var metadata = JsonConvert.DeserializeObject<ContextCheckpointMetadata>(artifact.MetadataJson ?? "null");
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.ThroughMessageId) ||
                metadata.SourceMessageCount < 1 || metadata.SourceTokens < 0 || metadata.SummaryTokens < 0)
                throw new InvalidDataException("Invalid compaction boundary metadata.");
            return metadata;
        }

        private static void RequireSnapshotHeader(ConversationMetadata owner, ChatArtifact artifact)
        {
            if (artifact?.Kind == ChatArtifactKinds.TaskList) { RequireTaskHeader(owner, artifact); return; }
            if (artifact?.Kind != ChatArtifactKinds.Compaction || artifact.Revision < 1 ||
                artifact.DocumentAuthorityId != null || artifact.WorkspaceId != null || artifact.InlineText != null ||
                artifact.RelativePath != null || string.IsNullOrWhiteSpace(artifact.SourceMessageId) || artifact.RunId != null ||
                artifact.CreatedUtc.Kind != DateTimeKind.Utc || artifact.MimeType != "application/json" ||
                !IsCompactionSnapshot(owner, ChatResourceUri.CreateArtifactRevision(owner.Id, artifact)))
                throw new InvalidDataException("Invalid conversation-owned compaction snapshot.");
            SnapshotBody(artifact); CheckpointMetadata(artifact);
        }

        private ContextCheckpoint ReadCheckpoint(ChatArtifact artifact)
        {
            var checkpoint = JsonConvert.DeserializeObject<ContextCheckpoint>(ReadChatBody(SnapshotBody(artifact)));
            var metadata = CheckpointMetadata(artifact);
            if (checkpoint == null || checkpoint.Id != artifact.Id ||
                checkpoint.PromptVersion != ContextCheckpoint.CurrentPromptVersion ||
                checkpoint.ThroughMessageId != metadata.ThroughMessageId ||
                checkpoint.SourceMessageCount != metadata.SourceMessageCount || checkpoint.SourceTokens != metadata.SourceTokens ||
                checkpoint.SummaryTokens != metadata.SummaryTokens || checkpoint.Claims == null || checkpoint.Claims.Count == 0 ||
                checkpoint.Claims.Count > 64 || checkpoint.Claims.Any(item => item == null || !item.HasTypedProvenance()) ||
                checkpoint.SummaryMarkdown != string.Join("\n", checkpoint.Claims.Select(item => item.Text)) ||
                !SameRunValue(checkpoint.Claims, JsonConvert.DeserializeObject<List<StructuredContextClaim>>(checkpoint.SummaryJson ?? "null")))
                throw new InvalidDataException("Compaction body disagrees with its typed claims or boundary.");
            return checkpoint;
        }

        private static bool IsCheckpointMessage(ChatMessage message, IEnumerable<ChatArtifact> artifacts)
        {
            return message.Role == "assistant" && message.ExcludeFromModelContext && !message.ProtocolMessage && !message.RuntimeProjection &&
                message.ToolCallId == null && message.Activity?.Kind == "compaction" &&
                artifacts.Any(item => item.Kind == ChatArtifactKinds.Compaction && item.SourceMessageId == message.Id);
        }

        private void ValidateContextSelection(SqliteConnection db, ConversationChatCommit fact, ConversationResourceSet state,
            Func<string, ChatArtifact> resolve)
        {
            foreach (var artifact in (fact.Snapshots ?? new ChatArtifact[0]).Where(item => item.Kind == ChatArtifactKinds.Compaction))
            {
                var source = fact.Messages.SingleOrDefault(item => item.Header.Id == artifact.SourceMessageId);
                if (source == null || !IsCheckpointMessage(source.Header, new[] { artifact }) ||
                    !source.Header.ResourceRefs.Any(item => SameRunValue(item, ChatResourceUri.CreateArtifactRevision(fact.Card.ChatId, artifact))))
                    throw new InvalidDataException("Compaction must be committed with its exact source message.");
            }
            if (state.ActiveContext == null) return;
            if (fact.History != null) throw new InvalidDataException("A history rewrite must invalidate its context checkpoint.");
            var selected = resolve(ResourceUri.Parse(state.ActiveContext.Uri).Segments[2]);
            if (selected?.Kind != ChatArtifactKinds.Compaction) throw new InvalidDataException("Selected checkpoint is unavailable.");
            var boundary = CheckpointMetadata(selected).ThroughMessageId;
            if (!fact.Messages.Any(item => item.Header.Id == boundary))
                using (var command = Command(db, "SELECT 1 FROM chat_message WHERE stream=$stream AND chat=$chat AND id=$id AND ordinal>=0"))
                {
                    command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", boundary);
                    if (command.ExecuteScalar() == null)
                        throw new InvalidDataException("Selected checkpoint boundary is not in visible history.");
                }
        }

        private void RestoreCheckpointProjection(ChatSession session)
        {
            session.ContextCheckpoints.Clear();
            if (session.ActiveContextCheckpointId == null) return;
            var artifact = session.Artifacts.Single(item => item.Id == session.ActiveContextCheckpointId);
            // Missing/invalid summary is explicit; never silently replace the selected
            // context or discard retained execution obligations.
            var checkpoint = ReadCheckpoint(artifact);
            session.ContextCheckpoints.Add(checkpoint);
        }

        private PayloadRef PrepareContextNotes(ChatSession session)
        {
            var notes = session.Context.Notes;
            if (notes.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id)) ||
                notes.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != notes.Count)
                throw new InvalidDataException("Context notes require unique identities.");
            foreach (var note in notes)
            {
                var instruction = note.Role == ContextNoteRole.UserInstruction;
                var data = note.Role == ContextNoteRole.SuppliedData;
                if (instruction ? note.InstructionPayload == null || note.Evidence != null :
                    !data && note.Role != ContextNoteRole.OfficeObservation || note.InstructionPayload != null ||
                    note.Evidence?.Payload == null || note.Evidence.Immutable != data ||
                    note.Evidence.ScopeId.Kind != (data ? "conversation" : "document") ||
                    note.Evidence.ScopeId.Id != (data ? session.Id : session.DocumentAuthorityId))
                    throw new InvalidDataException("Context note must retain its exact typed role and source.");
                var payload = instruction ? note.InstructionPayload : note.Evidence.Payload;
                if (!_bodies.HasVerifiedReference(payload.ToBlobReference()))
                    throw new InvalidDataException("Context note payload is unavailable.");
            }
            return StoreChatJson(session.Context);
        }

        // Maintenance follows the typed CAS containers themselves. Copying an
        // unbounded closure into every chat header would duplicate durable state.
        internal void ScanChatContextReferences(JournalRecord record, CasReachabilityScan scan, HashSet<string> visited)
        {
            if (record.Kind != "chat.committed" && record.Kind != "agent.spawned") return;
            if (record.Kind == "agent.spawned" && DecodeSpawn(record).Chat == null) return;
            var fact = DecodeChatCommit(record);
            if (visited.Add(fact.Context.Sha256))
            {
                var context = JsonConvert.DeserializeObject<DocumentContext>(ReadChatBody(fact.Context));
                if (context?.Notes == null) throw new InvalidDataException("Retained context notes are unavailable.");
                scan.AddTokenReferences(JObject.FromObject(context), "storage-journal", _scope, "context:" + fact.Card.ChatId);
            }
            foreach (var artifact in (fact.Snapshots ?? new ChatArtifact[0]).Where(item => item.Kind == ChatArtifactKinds.Compaction))
                if (visited.Add(artifact.ContentSha256))
                    scan.AddTokenReferences(JObject.FromObject(ReadCheckpoint(artifact)), "storage-journal", _scope, "checkpoint:" + artifact.Id);
        }
    }
}
