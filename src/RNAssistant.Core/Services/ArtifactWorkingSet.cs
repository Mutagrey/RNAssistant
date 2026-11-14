using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // Chat-local membership is independent of immutable source-message references.
    public static class ArtifactWorkingSet
    {
        public static void Validate(ChatSession session)
        {
            Validate(ConversationMetadata.From(session), session.ArtifactLinks);
        }

        public static void Validate(ConversationMetadata metadata, IEnumerable<ChatArtifactLink> artifactLinks)
        {
            var links = artifactLinks ?? Enumerable.Empty<ChatArtifactLink>();
            if (links.Any(link => link?.Identity == null || !DocumentArtifactStore.Owns(metadata, link.Reference)) ||
                links.GroupBy(link => link.Identity).Any(group => group.Count() != 1))
                throw new InvalidDataException("The chat artifact links are invalid or ambiguous.");
        }

        public static ResourceIdentity Identity(ChatSession session, ChatArtifact artifact)
        {
            return Identity(session?.Id, session?.DocumentAuthorityId, session?.WorkspaceId, artifact);
        }

        public static ResourceIdentity Identity(ConversationMetadata session, ChatArtifact artifact)
        {
            return Identity(session?.Id, session?.DocumentAuthorityId, session?.WorkspaceId, artifact);
        }

        public static ResourceIdentity Identity(ConversationMetadata session, ResourceRef reference)
        {
            string owner, id; int revision;
            if (reference?.IsExact != true || !DocumentArtifactStore.Owns(session, reference) ||
                !ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) ||
                ChatResourceUri.ArtifactSnapshot(reference).Uri != reference.Uri)
                throw new InvalidDataException("An exact owned artifact snapshot is required.");
            return Identity(session, new ChatArtifact { Id = id, Revision = revision,
                DocumentAuthorityId = session.DocumentAuthorityId, WorkspaceId = session.WorkspaceId,
                Kind = id.StartsWith("plan_doc_", StringComparison.Ordinal) ? ChatArtifactKinds.PlanDocument :
                    session.WorkspaceId != null ? ChatArtifactKinds.HtmlProjectVersion :
                    HtmlWorkspaceIdentity.LogicalId(id) != null ? ChatArtifactKinds.HtmlWorkspace : ChatArtifactKinds.File });
        }

        private static ResourceIdentity Identity(string chatId, string documentAuthorityId,
            string workspaceId, ChatArtifact artifact)
        {
            if (artifact?.Kind == ChatArtifactKinds.PlanDocument &&
                DocumentArtifactStore.Owns(documentAuthorityId, workspaceId, ChatResourceUri.CreateArtifactRevision(chatId, artifact)))
                return DocumentArtifactStore.PlanIdentity(documentAuthorityId, workspaceId, DocumentArtifactStore.PlanIdFromArtifact(artifact));
            if (artifact?.Kind == ChatArtifactKinds.HtmlProjectVersion && !string.IsNullOrEmpty(workspaceId) &&
                artifact.WorkspaceId == workspaceId && DocumentArtifactStore.Owns(documentAuthorityId, workspaceId, ChatResourceUri.CreateArtifactRevision(chatId, artifact)))
                return ChatResourceUri.CreateArtifactRevision(chatId, artifact).Identity;
            if (artifact == null || string.IsNullOrWhiteSpace(documentAuthorityId) || artifact.DocumentAuthorityId != documentAuthorityId)
                throw new InvalidDataException("The artifact is not owned by this document.");
            if (MarkdownDocumentIdentity.LogicalId(artifact.Id) != null)
                return MarkdownDocumentIdentity.Identity(documentAuthorityId, MarkdownDocumentIdentity.LogicalId(artifact.Id));
            if (DocumentArtifactStore.ContextLogicalId(artifact.Id) != null)
                return new ResourceIdentity(ResourceUri.Create("state", "document", documentAuthorityId, DocumentArtifactStore.ContextLogicalId(artifact.Id)));
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
                return HtmlWorkspaceIdentity.Identity(documentAuthorityId, HtmlWorkspaceIdentity.LogicalId(artifact.Id));
            if (!artifact.Id.StartsWith("attachment_", StringComparison.Ordinal) && !artifact.Id.StartsWith("artifact_", StringComparison.Ordinal))
                throw new InvalidDataException("This artifact has no document working-set owner yet.");
            return ChatResourceUri.CreateArtifactRevision(chatId, artifact).Identity;
        }

        public static string PlanId(ChatArtifact artifact)
        {
            return (string)JObject.Parse(artifact.MetadataJson ?? "{}")["planId"];
        }

        public static IEnumerable<string> LinkedArtifactIds(ChatSession session)
        {
            foreach (var link in session.ArtifactLinks ?? new List<ChatArtifactLink>())
            {
                string owner, id;
                int revision;
                if (!link.Detached && ChatResourceUri.TryParseArtifactRevision(link.Reference, out owner, out id, out revision) &&
                    DocumentArtifactStore.Owns(session, link.Reference)) yield return id;
            }
        }

        public static bool IsLinked(ChatSession session, ChatArtifact artifact)
        {
            if (artifact.RemovedFromDocument) return false;
            var identity = Identity(session, artifact);
            var decision = (session.ArtifactLinks ?? new List<ChatArtifactLink>()).SingleOrDefault(link => identity.Equals(link.Identity));
            if (decision != null) return !decision.Detached;
            return (session.Artifacts ?? new List<ChatArtifact>()).Any(item =>
                DocumentArtifactStore.Owns(session, ChatResourceUri.CreateArtifactRevision(session, item)) && identity.Equals(Identity(session, item)));
        }

        public static bool IsDetached(ChatSession session, ChatArtifact artifact)
        {
            return IsDetached(session?.Id, session?.DocumentAuthorityId, session?.WorkspaceId, session?.ArtifactLinks, artifact);
        }

        public static bool IsDetached(ConversationMetadata metadata, IEnumerable<ChatArtifactLink> links, ChatArtifact artifact)
        {
            return IsDetached(metadata?.Id, metadata?.DocumentAuthorityId, metadata?.WorkspaceId, links, artifact);
        }

        private static bool IsDetached(string chatId, string documentAuthorityId, string workspaceId,
            IEnumerable<ChatArtifactLink> links, ChatArtifact artifact)
        {
            if (artifact?.RemovedFromDocument == true) return true;
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId) && string.IsNullOrWhiteSpace(artifact.WorkspaceId)) return false;
            var identity = Identity(chatId, documentAuthorityId, workspaceId, artifact);
            return (links ?? Enumerable.Empty<ChatArtifactLink>()).Any(link => link.Detached && identity.Equals(link.Identity));
        }

        public static bool IsDetachedReference(ChatSession session, ResourceRef reference)
        {
            string owner, id;
            int revision;
            if (session == null || !ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) || !DocumentArtifactStore.Owns(session, reference)) return false;
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item => item.Id == id && item.Revision == revision);
            return artifact != null && IsDetached(session, artifact);
        }

        public static void Set(ChatSession session, ChatArtifact artifact, bool detached)
        {
            var identity = Identity(session, artifact);
            session.ArtifactLinks = session.ArtifactLinks ?? new List<ChatArtifactLink>();
            session.ArtifactLinks.RemoveAll(link => identity.Equals(link.Identity));
            session.ArtifactLinks.Add(new ChatArtifactLink { Identity = identity,
                Reference = ChatResourceUri.CreateArtifactRevision(session, artifact), Detached = detached });
            if (detached && artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
            {
                var selected = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item => item.Id == session.ActiveHtmlArtifactId);
                if (selected != null && identity.Equals(Identity(session, selected)))
                { session.ActiveHtmlArtifactId = null; session.HtmlWorkspace = new HtmlWorkspace(); session.HtmlWorkspaceRecovery = new HtmlWorkspaceRecoveryState(); }
            }
            if (detached && artifact.Kind == ChatArtifactKinds.PlanDocument)
            {
                var selected = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item => item.Id == session.ActivePlanDocumentArtifactId);
                if (selected != null && identity.Equals(Identity(session, selected))) session.ActivePlanDocumentArtifactId = null;
            }
        }
    }
}
