using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal static class ChatResourceReferenceService
    {
        internal const string MessageAttachmentPrefix = ChatMessageAttachmentService.TargetPrefix;

        internal static string MessageAttachmentTarget(ChatMessage message, ChatAttachment attachment)
        { return ChatMessageAttachmentService.Target(message, attachment); }

        internal sealed class MessageAttachmentBinding
        {
            internal ChatMessage Message;
            internal ChatAttachment Attachment;
            internal string Target;
            internal ResourceRef Reference;
        }

        private static IEnumerable<MessageAttachmentBinding> MessageAttachments(ChatSession session)
        {
            foreach (var message in (session?.Messages ?? new List<ChatMessage>()).AsEnumerable().Reverse())
            {
                if (message == null || message.ProtocolMessage || !string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
                    if (attachment != null)
                        yield return new MessageAttachmentBinding { Message = message, Attachment = attachment,
                            Target = MessageAttachmentTarget(message, attachment) };
            }
        }

        internal static Dictionary<string, string> MessageAttachmentTargets(ChatSession session)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in MessageAttachments(session).GroupBy(item => item.Target, StringComparer.Ordinal))
            {
                if (group.Count() != 1) continue;
                var binding = group.Single();
                try { BindMessageAttachment(session, binding); }
                catch (ResourceRequestException) { continue; }
                if (!targets.ContainsKey(binding.Reference.Uri)) targets.Add(binding.Reference.Uri, binding.Target);
            }
            return targets;
        }

        internal static MessageAttachmentBinding ResolveMessageAttachment(ChatSession session, string target)
        {
            var matches = MessageAttachments(session).Where(item => item.Target == target).Take(2).ToArray();
            if (matches.Length == 0)
                throw new ResourceRequestException("The selected attachment is not linked to a user message in this chat. Use a supplied message attachment target.",
                    "resource_attachment_not_found", false);
            if (matches.Length != 1)
                throw new ResourceRequestException("Multiple message attachments have this target. Select an unambiguous attachment; no original was chosen.",
                    "resource_target_ambiguous", false);
            BindMessageAttachment(session, matches[0]);
            return matches[0];
        }

        private static void BindMessageAttachment(ChatSession session, MessageAttachmentBinding binding)
        {
            var message = binding.Message;
            var attachment = binding.Attachment;
            if (string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) || string.IsNullOrWhiteSpace(message.Id) ||
                session.Messages.Count(item => item != null && item.Id == message.Id) != 1 ||
                string.IsNullOrWhiteSpace(attachment.Id) || !string.IsNullOrWhiteSpace(attachment.DraftChatId) ||
                string.IsNullOrWhiteSpace(attachment.ContentSha256) || !attachment.ContentByteLength.HasValue ||
                message.Attachments.Count(item => item != null && item.Id == attachment.Id) != 1)
                throw new ResourceRequestException("The selected message attachment has no unambiguous committed original.",
                    "resource_attachment_binding_invalid", false);
            var references = (message.ResourceRefs ?? new List<ResourceRef>()).Where(reference => {
                string owner, id; int revision;
                return reference?.IsExact == true && ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) &&
                    owner == session.DocumentAuthorityId && id == "attachment_" + attachment.Id && revision == 1 &&
                    reference.Revision == "1" && reference.Uri == ChatResourceUri.ArtifactSnapshot(reference).Uri;
            }).GroupBy(reference => reference.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (references.Length != 1)
                throw new ResourceRequestException("The selected message attachment is missing its exact document resource link.",
                    "resource_attachment_binding_invalid", false);
            binding.Reference = references[0].Copy();
        }

        public static void LinkMessageResources(ChatSession session, int startIndex)
        {
            if (session == null || session.Messages == null) return;
            session.Artifacts = session.Artifacts ?? new List<ChatArtifact>();
            for (var index = Math.Max(0, startIndex); index < session.Messages.Count; index++)
            {
                var message = session.Messages[index];
                if (message == null) continue;
                message.ResourceRefs = message.ResourceRefs ?? new List<ResourceRef>();
                RebaseReferences(session, message);
                if (message.ProtocolMessage) continue;
                ChatMessageAttachmentService.Link(session, message);
                LinkHtmlWorkspace(session, message);
                LinkTaskList(session, message);
                LinkPlanDocument(session, message);
            }
        }

        public static List<ChatArtifact> ReachableForMessages(
            IEnumerable<ChatArtifact> artifacts,
            IEnumerable<ChatMessage> messages,
            IEnumerable<string> additionalArtifactIds = null)
        {
            var artifactList = (artifacts ?? new ChatArtifact[0])
                .Where(artifact => artifact != null && !string.IsNullOrWhiteSpace(artifact.Id))
                .ToList();
            if (artifactList.GroupBy(artifact => artifact.Id, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
            {
                // Reachability must never choose one side of an ambiguous immutable identity.
                // Preserve the whole set so pruning/forking cannot silently discard evidence.
                return artifactList;
            }
            var byId = artifactList
                .GroupBy(artifact => artifact.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message == null) continue;
                foreach (var id in ReferencedArtifactIds(message, artifactList)) AddRequired(required, id);
                AddRequired(required, ReferencedArtifactId(message.HtmlWorkspaceCheckpoint, artifactList));
            }
            foreach (var id in additionalArtifactIds ?? new string[0]) AddRequired(required, id);

            var pending = new Queue<string>(required);
            while (pending.Count > 0)
            {
                ChatArtifact artifact;
                if (!byId.TryGetValue(pending.Dequeue(), out artifact)) continue;
                EnqueueRequired(required, pending, artifact.ParentArtifactId);
                foreach (var relatedId in artifact.RelatedArtifactIds ?? new List<string>())
                {
                    EnqueueRequired(required, pending, relatedId);
                }
            }
            return artifactList.Where(artifact => required.Contains(artifact.Id)).ToList();
        }

        public static void PruneUnreachable(ChatSession session)
        {
            if (session == null) return;
            var removedModelTombstoneSource = (session.Artifacts ?? new List<ChatArtifact>()).Any(item =>
                PlanDocumentService.IsTombstone(item) &&
                !string.IsNullOrWhiteSpace(item.SourceMessageId) &&
                !PlanDocumentService.IsApplicableTombstone(session, item));
            var activeArtifactIds = ArtifactWorkingSet.LinkedArtifactIds(session).ToList();
            if (!string.IsNullOrWhiteSpace(session.ActiveHtmlArtifactId)) activeArtifactIds.Add(session.ActiveHtmlArtifactId);
            if (!string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId)) activeArtifactIds.Add(session.ActiveTaskListArtifactId);
            if (!string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId)) activeArtifactIds.Add(session.ActivePlanDocumentArtifactId);
            activeArtifactIds.AddRange((session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => PlanDocumentService.IsApplicableTombstone(session, item))
                .Select(item => item.Id));
            session.Artifacts = ReachableForMessages(session.Artifacts, session.Messages, activeArtifactIds);
            if (!string.IsNullOrWhiteSpace(session.ActiveTaskListArtifactId) &&
                FindUniqueArtifact(session, session.ActiveTaskListArtifactId, ChatArtifactKinds.TaskList) == null)
            {
                session.ActiveTaskListArtifactId = null;
            }
            if (!string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId) &&
                FindUniqueArtifact(session, session.ActivePlanDocumentArtifactId, ChatArtifactKinds.PlanDocument) == null)
            {
                session.ActivePlanDocumentArtifactId = null;
            }
            if (removedModelTombstoneSource && string.IsNullOrWhiteSpace(session.ActivePlanDocumentArtifactId))
            {
                RestoreActivePlanDocumentFromMessages(session);
            }
        }

        public static void RestoreActiveTaskListFromMessages(ChatSession session)
        {
            if (session == null)
            {
                return;
            }
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(item.Kind, ChatArtifactKinds.TaskList, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            session.ActiveTaskListArtifactId = null;
            for (var messageIndex = (session.Messages ?? new List<ChatMessage>()).Count - 1; messageIndex >= 0; messageIndex--)
            {
                var ids = session.Messages[messageIndex] == null
                    ? new List<string>()
                    : ReferencedArtifactIds(session.Messages[messageIndex], artifacts.Values);
                for (var idIndex = (ids ?? new List<string>()).Count - 1; idIndex >= 0; idIndex--)
                {
                    if (!artifacts.ContainsKey(ids[idIndex])) continue;
                    ChatTaskList taskList;
                    try { taskList = JsonConvert.DeserializeObject<ChatTaskList>(artifacts[ids[idIndex]].InlineText); }
                    catch (JsonException) { continue; }
                    if (taskList == null) continue;
                    if (!string.Equals(taskList.Status, "active", StringComparison.OrdinalIgnoreCase)) return;
                    session.ActiveTaskListArtifactId = ids[idIndex];
                    return;
                }
            }
        }

        public static void RestoreActivePlanDocumentFromMessages(ChatSession session)
        {
            if (session == null) return;
            var selected = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item =>
                item.Id == session.ActivePlanDocumentArtifactId && item.Kind == ChatArtifactKinds.PlanDocument);
            // An explicit document selection is independent of the edited message range.
            if (selected != null && !string.IsNullOrWhiteSpace(selected.DocumentAuthorityId) &&
                !PlanDocumentService.IsRemoved(session, selected) &&
                (session.ArtifactLinks ?? new List<ChatArtifactLink>()).Any(link => !link.Detached &&
                    link.Identity.Equals(ArtifactWorkingSet.Identity(session, selected)))) return;
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(item.Kind, ChatArtifactKinds.PlanDocument, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            session.ActivePlanDocumentArtifactId = null;
            for (var messageIndex = (session.Messages ?? new List<ChatMessage>()).Count - 1; messageIndex >= 0; messageIndex--)
            {
                var ids = session.Messages[messageIndex] == null
                    ? new List<string>()
                    : ReferencedArtifactIds(session.Messages[messageIndex], artifacts.Values);
                for (var idIndex = ids.Count - 1; idIndex >= 0; idIndex--)
                {
                    ChatArtifact artifact;
                    if (!artifacts.TryGetValue(ids[idIndex], out artifact) ||
                        PlanDocumentService.IsRemoved(session, artifact) || ArtifactWorkingSet.IsDetached(session, artifact)) continue;
                    session.ActivePlanDocumentArtifactId = ids[idIndex];
                    return;
                }
            }
        }

        private static void LinkHtmlWorkspace(ChatSession session, ChatMessage message)
        {
            // Publication owns attribution. Preserve exact runtime references and
            // add only links backed by that retained origin, even after archival.
            if (message.Activity == null || string.IsNullOrWhiteSpace(message.RunId)) return;
            foreach (var artifact in session.Artifacts.Where(item => item != null &&
                item.Kind == ChatArtifactKinds.HtmlWorkspace && item.RunId == message.RunId &&
                item.SourceMessageId == message.Id))
                if (FindUniqueArtifact(session, artifact.Id, ChatArtifactKinds.HtmlWorkspace) == artifact)
                    AddReference(session, message, artifact);
        }

        private static void LinkTaskList(ChatSession session, ChatMessage message)
        {
            var activity = message.Activity;
            if (activity == null || string.IsNullOrWhiteSpace(activity.ToolId) ||
                !activity.ToolId.StartsWith("common.task_list_", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(activity.DataJson))
            {
                return;
            }
            try
            {
                var artifactId = (string)JObject.Parse(activity.DataJson)["artifactId"];
                if (string.IsNullOrWhiteSpace(artifactId)) return;
                var artifact = FindUniqueArtifact(session, artifactId, ChatArtifactKinds.TaskList);
                if (artifact == null) return;
                artifact.SourceMessageId = string.IsNullOrWhiteSpace(artifact.SourceMessageId) ? message.Id : artifact.SourceMessageId;
                if (!string.Equals(artifact.SourceMessageId, message.Id, StringComparison.OrdinalIgnoreCase)) return;
                artifact.RunId = string.IsNullOrWhiteSpace(artifact.RunId) ? message.RunId : artifact.RunId;
                AddReference(session, message, artifact);
            }
            catch (JsonException)
            {
            }
        }

        private static void LinkPlanDocument(ChatSession session, ChatMessage message)
        {
            var activity = message.Activity;
            if (activity == null || string.IsNullOrWhiteSpace(activity.ToolId) ||
                !activity.ToolId.StartsWith("common.plan_doc_", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(activity.DataJson)) return;
            try
            {
                var artifactId = (string)JObject.Parse(activity.DataJson)["artifactId"];
                if (string.IsNullOrWhiteSpace(artifactId)) return;
                var artifact = FindUniqueArtifact(session, artifactId, ChatArtifactKinds.PlanDocument);
                if (artifact == null) return;
                artifact.SourceMessageId = string.IsNullOrWhiteSpace(artifact.SourceMessageId) ? message.Id : artifact.SourceMessageId;
                if (!string.Equals(artifact.SourceMessageId, message.Id, StringComparison.OrdinalIgnoreCase)) return;
                artifact.RunId = string.IsNullOrWhiteSpace(artifact.RunId) ? message.RunId : artifact.RunId;
                AddReference(session, message, artifact);
            }
            catch (JsonException)
            {
            }
        }

        private static List<string> ReferencedArtifactIds(ChatMessage message, IEnumerable<ChatArtifact> artifacts)
        {
            if (message == null) return new List<string>();
            var artifactList = (artifacts ?? new ChatArtifact[0]).Where(item => item != null).ToList();
            return (message.ResourceRefs ?? new List<ResourceRef>())
                .Select(reference => ReferencedArtifactId(reference, artifactList))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ReferencedArtifactId(ResourceRef reference, IEnumerable<ChatArtifact> artifacts)
        {
            string ignoredSessionId;
            string artifactId;
            int revision;
            if (!ChatResourceUri.TryParseArtifactRevision(reference, out ignoredSessionId, out artifactId, out revision)) return null;
            var matches = (artifacts ?? new ChatArtifact[0]).Where(item => item != null &&
                string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return matches.Count == 1 && Math.Max(1, matches[0].Revision) == revision ? artifactId : null;
        }

        private static ChatArtifact FindUniqueArtifact(ChatSession session, string artifactId, string kind)
        {
            if (session == null || string.IsNullOrWhiteSpace(artifactId)) return null;
            var matches = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    artifactId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return matches.Count == 1 && string.Equals(
                matches[0].Kind,
                kind,
                StringComparison.OrdinalIgnoreCase)
                    ? matches[0]
                    : null;
        }

        private static void AddReference(ChatSession session, ChatMessage message, ChatArtifact artifact)
        {
            if (message == null || artifact == null) return;
            message.ResourceRefs = message.ResourceRefs ?? new List<ResourceRef>();
            var reference = ChatResourceUri.CreateArtifactRevision(session, artifact);
            if (message.ResourceRefs.Any(item => item != null && string.Equals(item.Uri, reference.Uri, StringComparison.Ordinal) &&
                string.Equals(item.Revision ?? string.Empty, reference.Revision ?? string.Empty, StringComparison.Ordinal))) return;
            message.ResourceRefs.Add(reference);
        }

        private static void RebaseReferences(ChatSession session, ChatMessage message)
        {
            var artifacts = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            var rebased = new List<ResourceRef>();
            foreach (var reference in message.ResourceRefs ?? new List<ResourceRef>())
            {
                if (RNAssistant.Core.Storage.DocumentArtifactStore.Owns(session, reference))
                {
                    rebased.Add(reference.Copy());
                    continue;
                }
                string ignoredSessionId;
                string artifactId;
                int revision;
                if (!ChatResourceUri.TryParseArtifactRevision(reference, out ignoredSessionId, out artifactId, out revision))
                {
                    if (reference != null) rebased.Add(new ResourceRef(reference.Uri, reference.Revision));
                    continue;
                }
                ChatArtifact artifact;
                if (!artifacts.TryGetValue(artifactId, out artifact) || Math.Max(1, artifact.Revision) != revision) continue;
                var current = ChatResourceUri.RebaseArtifactRevision(reference, session.Id);
                if (current != null) rebased.Add(current);
            }
            message.ResourceRefs = rebased
                .GroupBy(reference => reference.Uri + "\n" + (reference.Revision ?? string.Empty), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
            {
                var source = attachment?.VisualSelectionSource;
                if (source == null || RNAssistant.Core.Storage.DocumentArtifactStore.Owns(session, source)) continue;
                var current = ChatResourceUri.RebaseArtifactRevision(source, session.Id);
                string artifactId;
                if (current != null && ChatResourceUri.TryGetCurrentArtifactId(session, current, out artifactId))
                    attachment.VisualSelectionSource = current;
            }

            var resultPrefix = string.Equals(message.Role, ToolResultRoles.Tool, StringComparison.OrdinalIgnoreCase) &&
                message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion ? null : "TOOL_RESULT:";
            message.Content = RebaseJsonText(session, message.Content, resultPrefix);
            foreach (var call in message.ToolCalls ?? new List<RNAssistant.Core.Llm.LlmToolCall>())
            {
                if (call != null) call.ArgumentsJson = RebaseJsonText(session, call.ArgumentsJson, null);
            }
            if (message.Activity != null)
            {
                message.Activity.ArgumentsJson = RebaseJsonText(session, message.Activity.ArgumentsJson, null);
                message.Activity.DataJson = RebaseJsonText(session, message.Activity.DataJson, null);
            }

            if (message.HtmlWorkspaceCheckpoint != null &&
                !RNAssistant.Core.Storage.DocumentArtifactStore.Owns(session, message.HtmlWorkspaceCheckpoint))
            {
                var checkpoint = ChatResourceUri.RebaseArtifactRevision(message.HtmlWorkspaceCheckpoint, session.Id);
                string checkpointId;
                if (checkpoint == null || !ChatResourceUri.TryGetCurrentArtifactId(session, checkpoint, out checkpointId))
                {
                    message.HtmlWorkspaceCheckpoint = null;
                }
                else
                {
                    message.HtmlWorkspaceCheckpoint = checkpoint;
                }
            }
        }

        private static string RebaseJsonText(ChatSession session, string value, string requiredPrefix)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var prefixLength = 0;
            if (!string.IsNullOrWhiteSpace(requiredPrefix))
            {
                if (!value.StartsWith(requiredPrefix, StringComparison.Ordinal)) return value;
                prefixLength = requiredPrefix.Length;
            }
            var json = value.Substring(prefixLength).TrimStart();
            if (!(json.StartsWith("{", StringComparison.Ordinal) || json.StartsWith("[", StringComparison.Ordinal))) return value;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                    var token = JToken.Load(reader);
                    while (reader.Read()) { }
                    if (!RebaseJsonResourceUris(session, token)) return value;
                    return (prefixLength == 0 ? string.Empty : requiredPrefix + "\n") + token.ToString(Formatting.None);
                }
            }
            catch (JsonException)
            {
                return value;
            }
        }

        private static bool RebaseJsonResourceUris(ChatSession session, JToken token)
        {
            if (token == null) return false;
            var value = token as JValue;
            if (value != null)
            {
                if (value.Type != JTokenType.String) return false;
                if (RNAssistant.Core.Storage.DocumentArtifactStore.Owns(session, new ResourceRef((string)value))) return false;
                var reference = ChatResourceUri.RebaseArtifactRevision(
                    new ResourceRef((string)value), session == null ? null : session.Id);
                string artifactId;
                if (reference != null && ChatResourceUri.TryGetCurrentArtifactId(session, reference, out artifactId))
                {
                    if (!string.Equals((string)value, reference.Uri, StringComparison.Ordinal))
                    {
                        value.Value = reference.Uri;
                        return true;
                    }
                }
                return false;
            }
            var changed = false;
            foreach (var child in token.Children().ToArray()) changed |= RebaseJsonResourceUris(session, child);
            return changed;
        }

        private static void AddRequired(ISet<string> required, string id)
        {
            if (required != null && !string.IsNullOrWhiteSpace(id)) required.Add(id);
        }

        private static void EnqueueRequired(ISet<string> required, Queue<string> pending, string id)
        {
            if (required != null && pending != null && !string.IsNullOrWhiteSpace(id) && required.Add(id)) pending.Enqueue(id);
        }

    }
}
