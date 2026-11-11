using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private void AssignChatReferences(SqliteConnection db, ConversationChatCommit fact, ConversationChatCommit retry)
        {
            var chat = fact.Card.ChatId;
            var nextCall = Convert.ToInt64(Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", chat) ?? 1L);
            var nextResource = Convert.ToInt64(Scalar(db, "SELECT MAX(ordinal) FROM chat_reference WHERE stream=$stream AND chat=$chat", chat) is long value ? value : 0L);
            var added = new List<ModelResourceReference>();
            var terminal = fact.Tools?.Terminal;
            if (terminal != null)
            {
                var prior = retry?.Tools?.Terminal;
                if (prior != null) { terminal.Result.Id = prior.Result.Id; terminal.Result.CreatedUtc = prior.Result.CreatedUtc; }
                var call = FindCall(db, chat, terminal.Result.RunId, terminal.Result.ToolCallId);
                if (call == null) throw new InvalidOperationException("Kernel terminal has no accepted call.");
                terminal.Result.ToolCallReference = call.Call.ToolCallReference;
                terminal.Result.EvidenceReference = prior?.Result.EvidenceReference ??
                    (terminal.Result.ExecutionProgress.EvidenceEligibility == RNAssistant.Core.Tools.EvidenceEligibility.Eligible
                        ? ModelReference.Create(ModelReferenceKind.Evidence, Convert.ToInt64(Scalar(db,
                            "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", chat))) : null);
            }
            foreach (var entry in fact.Messages)
            {
                var message = entry.Header;
                var callId = message.ToolCallId ?? message.Activity?.ToolCallId;
                if (callId == null) continue;
                var accepted = FindCall(db, chat, message.RunId, callId);
                if (message.AcceptedCallOrigin != null && accepted == null)
                    message.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, nextCall++);
                else
                {
                    if (accepted == null) throw new InvalidOperationException("Runtime presentation has no accepted call.");
                    message.ToolCallReference = accepted.Call.ToolCallReference;
                    var result = terminal?.Result.ToolCallId == callId ? terminal :
                        Find(db, TerminalOperation(db, chat, message.RunId, callId) as string)?.Fact;
                    message.EvidenceReference = message.AcceptedCallOrigin == null ? result?.Result.EvidenceReference : null;
                }
            }
            Func<ResourceAuthorityScopeId, ResourceRef, ModelResourceReference> observe = (scope, resource) => {
                var key = new ModelResourceReference(scope, resource, ModelReference.Create(ModelReferenceKind.Resource, 1), 1);
                var canonical = added.FirstOrDefault(item => item.ExactKey == key.ExactKey) ?? ReadReference(db, chat, scope, resource);
                if (canonical != null) return canonical;
                var previous = added.LastOrDefault(item => item.LogicalKey == key.LogicalKey) ?? ReadReference(db, chat, scope, resource, true);
                var reference = new ModelResourceReference(scope, resource,
                    previous?.Reference ?? ModelReference.Create(ModelReferenceKind.Resource, ++nextResource), (previous?.Version ?? 0) + 1);
                added.Add(reference); return reference;
            };
            foreach (var message in fact.Messages.Select(item => item.Header).Concat(terminal == null ? new ChatMessage[0] : new[] { terminal.Result }))
            {
                message.ResourceModelReferences = message.ResourceEvidence.Select(evidence => observe(evidence.ScopeId, evidence.Resource))
                    .Concat(message.ResourceModelReferences.Select(reference => observe(reference.Scope, reference.Resource)))
                    .GroupBy(reference => reference.ExactKey).Select(group => group.First()).ToList();
            }
            fact.References = retry?.References ?? (added.Count == 0 ? null : added);
        }

        private ModelResourceReference ReadReference(SqliteConnection db, string chat, ResourceAuthorityScopeId scope, ResourceRef resource, bool latest = false)
        {
            using (var command = ReferenceCommand(db, "SELECT revision,ordinal,version,operation FROM chat_reference WHERE stream=$stream AND chat=$chat AND scope_kind=$kind AND scope_id=$scope AND uri=$uri" +
                (latest ? " ORDER BY version DESC LIMIT 1" : " AND revision=$revision"), chat, scope, resource))
            {
                string revision, operation; long ordinal, version;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    revision = reader.GetString(0); ordinal = reader.GetInt64(1); version = reader.GetInt64(2); operation = reader.GetString(3);
                }
                var canonical = FindChatWrite(db, operation)?.Fact.References?.SingleOrDefault(item =>
                    item.Scope.Equals(scope) && item.Resource.Uri == resource.Uri && item.Resource.Revision == revision);
                if (canonical == null || canonical.Reference.Ordinal != ordinal || canonical.Version != version)
                    throw new InvalidDataException("Resource reference locator mismatch.");
                return canonical;
            }
        }

        private void ValidateChatReferences(SqliteConnection db, ConversationChatCommit fact)
        {
            var accepted = new List<ModelResourceReference>(); var chat = fact.Card.ChatId;
            var high = Scalar(db, "SELECT MAX(ordinal) FROM chat_reference WHERE stream=$stream AND chat=$chat", chat);
            var next = high is long value ? value : 0L;
            foreach (var reference in fact.References ?? new ModelResourceReference[0])
            {
                if (accepted.Any(item => item.ExactKey == reference.ExactKey) || ReadReference(db, chat, reference.Scope, reference.Resource) != null)
                    throw new InvalidDataException("Duplicate resource reference allocation.");
                var prior = accepted.LastOrDefault(item => item.LogicalKey == reference.LogicalKey) ?? ReadReference(db, chat, reference.Scope, reference.Resource, true);
                if (reference.Reference.Ordinal != (prior?.Reference.Ordinal ?? ++next) || reference.Version != (prior?.Version ?? 0) + 1)
                    throw new InvalidDataException("Resource reference sequence mismatch.");
                accepted.Add(reference);
            }
            foreach (var reference in fact.Messages.SelectMany(item => item.Header.ResourceModelReferences)
                .Concat(fact.Tools?.Terminal?.Result.ResourceModelReferences ?? new List<ModelResourceReference>()))
            {
                var canonical = accepted.FirstOrDefault(item => item.ExactKey == reference.ExactKey) ?? ReadReference(db, chat, reference.Scope, reference.Resource);
                if (canonical?.Alias != reference.Alias) throw new InvalidDataException("Foreign resource reference binding.");
            }
        }

        private SqliteCommand ReferenceCommand(SqliteConnection db, string sql, string chat, ResourceAuthorityScopeId scope, ResourceRef resource, SqliteTransaction tx = null)
        {
            var command = Command(db, sql, tx); command.Parameters.AddWithValue("$chat", chat);
            command.Parameters.AddWithValue("$kind", scope.Kind); command.Parameters.AddWithValue("$scope", scope.Id);
            command.Parameters.AddWithValue("$uri", resource.Uri); command.Parameters.AddWithValue("$revision", resource.Revision); return command;
        }

        private void LoadChatReferences(ChatSession session)
        {
            Access((db, cursor) => {
                RequireChatRevision(RNAssistant.Core.Persistence.ConversationMetadata.From(session), TextChat(db, session.Id));
                var coordinates = new List<Tuple<ResourceAuthorityScopeId, ResourceRef>>();
                using (var command = Command(db, "SELECT scope_kind,scope_id,uri,revision FROM chat_reference WHERE stream=$stream AND chat=$chat ORDER BY ordinal,version"))
                {
                    command.Parameters.AddWithValue("$chat", session.Id);
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                        coordinates.Add(Tuple.Create(new ResourceAuthorityScopeId(reader.GetString(0), reader.GetString(1)), new ResourceRef(reader.GetString(2), reader.GetString(3))));
                }
                session.ResourceReferenceIndex = coordinates.Select(item => ReadReference(db, session.Id, item.Item1, item.Item2)).ToList();
                session.ToolCallReferenceSequence = Convert.ToInt64(Scalar(db, "SELECT next_t FROM chat_sequence WHERE stream=$stream AND chat=$chat", session.Id) ?? 1L) - 1;
                session.EvidenceReferenceSequence = Convert.ToInt64(Scalar(db, "SELECT next_e FROM chat_sequence WHERE stream=$stream AND chat=$chat", session.Id) ?? 1L) - 1;
                return true;
            }, CancellationToken.None);
        }
    }
}
