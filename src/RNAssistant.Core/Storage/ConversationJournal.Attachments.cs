using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        internal static List<ChatAttachment> AttachmentHeaders(IEnumerable<ChatAttachment> attachments)
        {
            var result = new List<ChatAttachment>();
            foreach (var attachment in attachments ?? Enumerable.Empty<ChatAttachment>())
            {
                if (attachment == null || string.IsNullOrWhiteSpace(attachment.Id) ||
                    attachment.CreatedUtc.Kind != DateTimeKind.Utc || attachment.Size < 1 ||
                    attachment.ContentByteLength != attachment.Size || attachment.DraftChatId != null ||
                    attachment.RelativePath != null || attachment.ExtractedTextPath != null ||
                    attachment.Status != "ready" && attachment.Status != "error" ||
                    attachment.VisualSelectionSource != null && !attachment.VisualSelectionSource.IsExact)
                    throw new InvalidDataException("An attachment requires an exact committed original.");
                // Validate both halves of each CAS address without opening its body.
                AttachmentReferences(new[] { attachment }).ToArray();
                var header = attachment.CopyForContext(); header.ExtractedText = null;
                result.Add(header);
            }
            if (result.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
                throw new InvalidDataException("Attachment identities are ambiguous.");
            return result;
        }

        private static IEnumerable<PayloadRef> AttachmentReferences(IEnumerable<ChatAttachment> attachments)
        {
            foreach (var attachment in attachments ?? Enumerable.Empty<ChatAttachment>())
            {
                if (attachment == null) throw new InvalidDataException("Missing attachment metadata.");
                yield return new PayloadRef(attachment.ContentSha256, attachment.ContentByteLength ?? -1, attachment.ContentType);
                if (attachment.ExtractedTextSha256 != null || attachment.ExtractedTextByteLength.HasValue)
                    yield return new PayloadRef(attachment.ExtractedTextSha256, attachment.ExtractedTextByteLength ?? -1, "text/plain; charset=utf-8");
                else if (!string.IsNullOrEmpty(attachment.ExtractedText))
                    throw new InvalidDataException("Extracted text has no durable CAS address.");
            }
        }

        private static ResourceRef AttachmentReference(ConversationMetadata metadata, ChatMessage message, ChatAttachment attachment)
        {
            var artifact = ChatMessageAttachmentService.CreateArtifact(message, attachment);
            artifact.DocumentAuthorityId = metadata.DocumentAuthorityId;
            return ChatResourceUri.CreateArtifactRevision(metadata.Id, artifact);
        }

        private static bool IsOriginal(ConversationMetadata metadata, ResourceRef reference)
        {
            string owner, id; int revision;
            return reference?.IsExact == true && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                owner == (metadata.DocumentAuthorityId ?? metadata.Id) && revision == 1 &&
                id.StartsWith("attachment_", StringComparison.Ordinal) && ChatResourceUri.ArtifactSnapshot(reference).Uri == reference.Uri;
        }

        private static bool IsWorkspaceOriginal(ConversationMetadata metadata, ResourceRef reference)
        { return metadata.WorkspaceId != null && metadata.DocumentAuthorityId == null && IsOriginal(metadata, reference); }

        private ConversationTextEntry AttachmentEntry(SqliteConnection db, string chat, string id)
        {
            string message, operation;
            using (var command = Command(db, "SELECT message,operation FROM chat_attachment WHERE stream=$stream AND chat=$chat AND id=$id"))
            {
                command.Parameters.AddWithValue("$chat", chat); command.Parameters.AddWithValue("$id", id);
                using (var reader = command.ExecuteReader())
                { if (!reader.Read()) return null; message = reader.GetString(0); operation = reader.GetString(1); }
            }
            var receipt = FindChatWrite(db, operation);
            var entry = receipt?.Fact.Messages.SingleOrDefault(item => item.Header.Id == message);
            if (entry == null || !InheritedFact(db, chat, receipt.Fact.Card.ChatId, receipt.Position.Sequence) ||
                entry.Header.Attachments.Count(item => item.Id == id) != 1)
                throw new InvalidDataException("Attachment locator disagrees with its canonical message.");
            return entry;
        }

        private void ValidateMessageAttachments(SqliteConnection db, ConversationChatCommit fact)
        {
            if (!fact.Messages.Any(item => item.Header.Attachments?.Count > 0)) return;
            var metadata = fact.Card.Address;
            var removed = fact.History == null ? new HashSet<string>() : new HashSet<string>(ReadHistory(fact.History).Removed);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in fact.Messages.Select(item => item.Header))
            foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
            {
                var reference = AttachmentReference(metadata, message, attachment);
                if ((message.ResourceRefs ?? new List<ResourceRef>()).Count(item => SameRunValue(item, reference)) != 1)
                    throw new InvalidDataException("Attachment lacks its exact message reference.");
                var prior = AttachmentEntry(db, metadata.Id, attachment.Id);
                if (prior != null && !SameRunValue(prior.Header.Attachments.Single(item => item.Id == attachment.Id), attachment))
                    throw new InvalidOperationException("An upload cannot be rebound to different immutable evidence.");
                if (metadata.WorkspaceId != null && (sources.ContainsKey(attachment.Id) ||
                    prior != null && prior.Header.Id != message.Id && !removed.Contains(prior.Header.Id) &&
                    ChatMessageOperation(db, metadata.Id, prior.Header.Id) != null))
                    throw new InvalidOperationException("A workspace original has more than one visible source message.");
                sources[attachment.Id] = message.Id;
            }
        }

        private void ApplyAttachmentRows(SqliteConnection db, SqliteTransaction tx, JournalRecord record, ConversationChatCommit fact)
        {
            if (fact.ExpectedRevision == 0 && fact.Fork != null)
                foreach (var reference in ReadResourceSet(fact.Resources, fact.Card.Address).References.Where(item => IsWorkspaceOriginal(fact.Card.Address, item)))
                    using (var command = Command(db, "INSERT INTO chat_attachment SELECT stream,$chat,id,message,operation FROM chat_attachment WHERE stream=$stream AND chat=$parent AND id=$id", tx))
                    {
                        command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$parent", fact.Fork.Parent.Id);
                        command.Parameters.AddWithValue("$id", ResourceUri.Parse(reference.Uri).Segments[2].Substring("attachment_".Length));
                        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Fork original locator is unavailable.");
                    }
            foreach (var message in fact.Messages)
            foreach (var attachment in message.Header.Attachments ?? new List<ChatAttachment>())
            using (var command = Command(db, "INSERT OR REPLACE INTO chat_attachment VALUES($stream,$chat,$id,$message,$operation)", tx))
            {
                command.Parameters.AddWithValue("$chat", fact.Card.ChatId); command.Parameters.AddWithValue("$id", attachment.Id);
                command.Parameters.AddWithValue("$message", message.Header.Id); command.Parameters.AddWithValue("$operation", record.Operation);
                command.ExecuteNonQuery();
            }
        }

        private static ChatArtifact LocalOriginal(ChatMessage message, ChatAttachment attachment)
        {
            var artifact = ChatMessageAttachmentService.CreateArtifact(message, attachment);
            artifact.CreatedUtc = attachment.CreatedUtc;
            return artifact;
        }

        private void ValidateLocalOriginals(SqliteConnection db, ChatSession session, ConversationTextEntry[] messages)
        {
            // An unavailable projection can retain an existing reference, never
            // introduce an unverified original. Independent text remains usable.
            var unavailable = session.Artifacts.Where(item => item.AvailabilityIssue != null).ToArray();
            if (unavailable.Length != 0)
            {
                var current = TextChat(db, session.Id);
                var retained = ReadResourceSet(current?.Fact.Resources, ConversationMetadata.From(session));
                if (unavailable.Any(item => !retained.References.Any(reference =>
                    SameRunValue(reference, ChatResourceUri.CreateArtifactRevision(session, item)))))
                    throw new InvalidDataException("Unavailable original has no prior accepted membership.");
            }
            if (session.WorkspaceId == null) return;
            foreach (var artifact in session.Artifacts.Where(item => IsOriginal(ConversationMetadata.From(session),
                ChatResourceUri.CreateArtifactRevision(session, item))))
            {
                var id = artifact.Id.Substring("attachment_".Length);
                var source = messages.FirstOrDefault(item => item.Header.Id == artifact.SourceMessageId &&
                    item.Header.Attachments.Any(attachment => attachment.Id == id)) ?? AttachmentEntry(db, session.Id, id);
                // Initial fork preparation may reference an original whose source
                // occurrence is hidden. PrepareFork fences the parent in this transaction.
                if (source == null && session.Revision == 0 && session.ParentSessionId != null)
                    source = AttachmentEntry(db, session.ParentSessionId, id);
                if (source == null) throw new InvalidDataException("Local original has no accepted source message.");
                RequireArtifactProjection(artifact, LocalOriginal(source.Header, source.Header.Attachments.Single(item => item.Id == id)));
            }
        }

        private ChatArtifact ReadLocalOriginal(SqliteConnection db, ConversationMetadata metadata, ResourceRef reference)
        {
            string owner, id; int revision;
            if (!IsOriginal(metadata, reference) || metadata.DocumentAuthorityId != null ||
                !ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision))
                throw new InvalidDataException("Foreign local original.");
            var entry = AttachmentEntry(db, metadata.Id, id.Substring("attachment_".Length));
            if (entry == null) throw new InvalidDataException("Local original is unavailable.");
            return LocalOriginal(entry.Header, entry.Header.Attachments.Single(item => "attachment_" + item.Id == id));
        }


    }

}
