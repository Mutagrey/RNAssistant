using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private DocumentArtifactStore ResourceStore()
        {
            var resources = new ResourceJournal(_paths, _protector);
            return new DocumentArtifactStore(resources, resources, _bodies);
        }

        private static bool IsPlanSnapshot(ConversationMetadata metadata, ResourceRef reference)
        {
            string owner, id; int revision;
            if (reference?.IsExact != true || !ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) ||
                owner != (metadata.DocumentAuthorityId ?? metadata.WorkspaceId) || !id.StartsWith("plan_doc_", StringComparison.Ordinal) ||
                ChatResourceUri.ArtifactSnapshot(reference).Uri != reference.Uri) return false;
            DocumentArtifactStore.PlanIdFromSnapshot(reference); // Validate the runtime snapshot grammar.
            return true;
        }

        private static bool IsStoredSnapshot(ConversationMetadata metadata, ResourceRef reference)
        { return IsOriginal(metadata, reference) || IsPlanSnapshot(metadata, reference) || IsTaskSnapshot(metadata, reference); }

        private static ResourceIdentity WorkingSetIdentity(ConversationMetadata metadata, ResourceRef reference)
        {
            return IsPlanSnapshot(metadata, reference) ? DocumentArtifactStore.PlanIdentity(metadata.DocumentAuthorityId,
                metadata.WorkspaceId, DocumentArtifactStore.PlanIdFromSnapshot(reference)) : reference.Identity;
        }

        private ConversationResourceSet ReadResourceSet(PayloadRef reference, ConversationMetadata metadata)
        {
            var state = reference == null ? new ConversationResourceSet(new ResourceRef[0], new ChatArtifactLink[0]) :
                JsonConvert.DeserializeObject<ConversationResourceSet>(ReadChatBody(reference));
            if (state?.References == null || state.Links == null || state.References.Any(item => !IsStoredSnapshot(metadata, item)) ||
                state.References.Select(item => item.Uri).Distinct(StringComparer.Ordinal).Count() != state.References.Count ||
                state.Links.Any(item => item == null || !IsStoredSnapshot(metadata, item.Reference) ||
                    !WorkingSetIdentity(metadata, item.Reference).Equals(item.Identity) || IsTaskSnapshot(metadata, item.Reference)) ||
                state.ActiveTask != null && (!IsTaskSnapshot(metadata, state.ActiveTask) ||
                    !state.References.Any(item => SameRunValue(item, state.ActiveTask))) ||
                state.ActivePlan != null && (!IsPlanSnapshot(metadata, state.ActivePlan) ||
                    !state.References.Any(item => SameRunValue(item, state.ActivePlan)) ||
                    state.Links.Any(item => item.Detached && item.Identity.Equals(WorkingSetIdentity(metadata, state.ActivePlan)))))
                throw new InvalidDataException("Invalid resource working set or Plan selection.");
            ArtifactWorkingSet.Validate(metadata, state.Links);
            return state;
        }

        private PayloadRef PrepareResourceSet(ChatSession session)
        {
            if (session.Artifacts.Any(item => item == null || item.AvailabilityIssue == null &&
                item.Kind != ChatArtifactKinds.Attachment && item.Kind != ChatArtifactKinds.Image &&
                item.Kind != ChatArtifactKinds.PlanDocument && item.Kind != ChatArtifactKinds.TaskList)) throw TextStoreScope();
            var metadata = ConversationMetadata.From(session);
            var references = session.Artifacts.Select(item => ChatResourceUri.CreateArtifactRevision(session, item)).ToArray();
            if (references.Any(item => !IsStoredSnapshot(metadata, item)) || session.ArtifactLinks.Any(item => !IsStoredSnapshot(metadata, item?.Reference) || IsTaskSnapshot(metadata, item.Reference)))
                throw TextStoreScope();
            ArtifactWorkingSet.Validate(session);
            var active = session.ActivePlanDocumentArtifactId == null ? null :
                DocumentArtifactStore.PlanSelectionReference(session, session.ActivePlanDocumentArtifactId);
            var task = session.ActiveTaskListArtifactId == null ? null : references.SingleOrDefault(item =>
                IsTaskSnapshot(metadata, item) && ResourceUri.Parse(item.Uri).Segments[2] == session.ActiveTaskListArtifactId);
            if (session.ActiveTaskListArtifactId != null && task == null) throw new InvalidDataException("Active task-list membership is missing.");
            if (references.Length == 0 && session.ArtifactLinks.Count == 0 && active == null && task == null) return null;
            return StoreChatJson(new ConversationResourceSet(references, session.ArtifactLinks, active, task));
        }

        // Called outside the conversation transaction: document authority uses its
        // own existing journal. No chat projection may become authority for it.
        private void ValidatePublishedArtifacts(ChatSession session, IEnumerable<ConversationTextEntry> messages)
        {
            var metadata = ConversationMetadata.From(session);
            var published = session.Artifacts.Where(item => item.AvailabilityIssue == null &&
                DocumentArtifactStore.Owns(metadata, ChatResourceUri.CreateArtifactRevision(session, item))).ToArray();
            if (published.Length == 0 && (session.DocumentAuthorityId == null ||
                !messages.Any(item => item.Header.Attachments?.Count > 0))) return;
            var originals = ResourceStore();
            foreach (var artifact in published)
            {
                var retained = originals.Read(metadata, ChatResourceUri.CreateArtifactRevision(session, artifact), false);
                RequireArtifactProjection(artifact, retained);
                if (artifact.Id == session.ActivePlanDocumentArtifactId && PlanDocumentService.IsTombstone(retained))
                    throw new InvalidDataException("A removed Plan cannot be selected as active.");
            }
            if (session.DocumentAuthorityId == null) return;
            foreach (var message in messages.Select(item => item.Header))
            foreach (var attachment in message.Attachments ?? new List<ChatAttachment>())
            {
                var retained = originals.Read(metadata, AttachmentReference(metadata, message, attachment), false);
                if (!SameRunValue(attachment, retained.OriginalAttachment))
                    throw new InvalidDataException("Message attachment disagrees with its document original.");
            }
        }

        private static void RequireArtifactProjection(ChatArtifact actual, ChatArtifact expected)
        {
            // Local creation time was presentation metadata in the retiring Store.
            // It is now derived from the upload, never a second persisted artifact.
            var copy = JsonConvert.DeserializeObject<ChatArtifact>(JsonConvert.SerializeObject(actual));
            if (expected.Kind == ChatArtifactKinds.PlanDocument)
            {
                if (copy.InlineText != null && (Tools.TextPatternEngine.Sha256(copy.InlineText) != expected.ContentSha256 ||
                    System.Text.Encoding.UTF8.GetByteCount(copy.InlineText) != expected.ContentByteLength))
                    throw new InvalidDataException("Plan text differs from its published snapshot.");
                // Message links are retained on the message, not rewritten into resource provenance.
                copy.InlineText = null; copy.SourceMessageId = expected.SourceMessageId; copy.RunId = expected.RunId;
            }
            else copy.CreatedUtc = expected.CreatedUtc;
            if (!SameRunValue(copy, expected)) throw new InvalidDataException("Artifact projection differs from its immutable resource.");
        }

        private ChatArtifact[] ReadArtifacts(ConversationMetadata metadata, IEnumerable<ResourceRef> references)
        {
            var selected = references.Where(item => IsStoredSnapshot(metadata, item))
                .GroupBy(item => item.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (selected.Length == 0) return new ChatArtifact[0];
            var result = new Dictionary<string, ChatArtifact>(StringComparer.Ordinal);
            var published = selected.Where(item => DocumentArtifactStore.Owns(metadata, item)).ToArray();
            if (published.Length != 0)
            {
                var resources = ResourceStore();
                foreach (var item in published) result.Add(item.Uri, resources.InspectMetadata(metadata, item));
            }
            var local = selected.Where(item => !result.ContainsKey(item.Uri)).ToArray();
            if (local.Length != 0) Access((db, cursor) => {
                    RequireChatRevision(metadata, TextChat(db, metadata.Id));
                    foreach (var item in local) result.Add(item.Uri, IsTaskSnapshot(metadata, item)
                        ? ReadTaskSnapshot(db, metadata, item) : ReadLocalOriginal(db, metadata, item));
                    return true;
                }, CancellationToken.None);
            return selected.Select(item => result[item.Uri]).ToArray();
        }

        private ChatArtifact[] ReadPageArtifacts(ConversationMetadata metadata, ConversationResourceSet state,
            IEnumerable<ResourceRef> references)
        {
            var page = references.Where(item => IsStoredSnapshot(metadata, item)).ToArray();
            var plans = new HashSet<string>(page.Where(item => IsPlanSnapshot(metadata, item))
                .Select(DocumentArtifactStore.PlanIdFromSnapshot), StringComparer.Ordinal);
            // An off-page tombstone must still hide old cards. Address only the
            // latest retained metadata for the Plan families actually on this page.
            var heads = state.References.Where(item => IsPlanSnapshot(metadata, item) &&
                plans.Contains(DocumentArtifactStore.PlanIdFromSnapshot(item)))
                .GroupBy(DocumentArtifactStore.PlanIdFromSnapshot)
                .Select(group => group.OrderByDescending(item => int.Parse(item.Revision,
                    System.Globalization.CultureInfo.InvariantCulture)).First());
            return ReadArtifacts(metadata, page.Concat(heads));
        }
    }

    // The manifest contains only membership/selection. Snapshot metadata and bodies
    // retain their owner; local Task snapshots are canonical conversation facts.
    internal sealed class ConversationResourceSet
    {
        public IReadOnlyList<ResourceRef> References { get; private set; }
        public IReadOnlyList<ChatArtifactLink> Links { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ResourceRef ActivePlan { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ResourceRef ActiveTask { get; private set; }
        [JsonConstructor] internal ConversationResourceSet(IReadOnlyList<ResourceRef> references, IReadOnlyList<ChatArtifactLink> links,
            ResourceRef activePlan = null, ResourceRef activeTask = null)
        { References = references; Links = links; ActivePlan = activePlan; ActiveTask = activeTask; }
    }
}
