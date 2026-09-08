using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // Chat-local membership is independent of immutable source-message references.
    public static class ArtifactWorkingSet
    {
        public static void Validate(ChatSession session)
        {
            var links = session.ArtifactLinks ?? new List<ChatArtifactLink>();
            if (links.Any(link => link?.Identity == null || !DocumentArtifactStore.Owns(session, link.Reference)) ||
                links.GroupBy(link => link.Identity).Any(group => group.Count() != 1))
                throw new InvalidDataException("The chat artifact links are invalid or ambiguous.");
        }

        public static ResourceIdentity Identity(ChatSession session, ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(session?.DocumentAuthorityId) || artifact.DocumentAuthorityId != session.DocumentAuthorityId)
                throw new InvalidDataException("The artifact is not owned by this document.");
            if (artifact.Kind == ChatArtifactKinds.PlanDocument)
            {
                return DocumentArtifactStore.PlanIdentity(session,
                    DocumentArtifactStore.PlanIdFromSnapshot(ChatResourceUri.CreateArtifactRevision(session, artifact)));
            }
            if (!artifact.Id.StartsWith("attachment_", StringComparison.Ordinal))
                throw new InvalidDataException("This artifact has no document working-set owner yet.");
            return ChatResourceUri.CreateArtifactRevision(session, artifact).Identity;
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
                    owner == session.DocumentAuthorityId) yield return id;
            }
        }

        public static bool IsLinked(ChatSession session, ChatArtifact artifact)
        {
            var identity = Identity(session, artifact);
            var decision = (session.ArtifactLinks ?? new List<ChatArtifactLink>()).SingleOrDefault(link => identity.Equals(link.Identity));
            if (decision != null) return !decision.Detached;
            return (session.Artifacts ?? new List<ChatArtifact>()).Any(item =>
                item.DocumentAuthorityId == session.DocumentAuthorityId && identity.Equals(Identity(session, item)));
        }

        public static bool IsDetached(ChatSession session, ChatArtifact artifact)
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.DocumentAuthorityId)) return false;
            var identity = Identity(session, artifact);
            return (session.ArtifactLinks ?? new List<ChatArtifactLink>()).Any(link => link.Detached && identity.Equals(link.Identity));
        }

        public static bool IsDetachedReference(ChatSession session, ResourceRef reference)
        {
            string owner, id;
            int revision;
            if (session == null || !ChatResourceUri.TryParseArtifactRevision(reference, out owner, out id, out revision) || owner != session.DocumentAuthorityId) return false;
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
            if (detached && artifact.Kind == ChatArtifactKinds.PlanDocument)
            {
                var selected = (session.Artifacts ?? new List<ChatArtifact>()).SingleOrDefault(item => item.Id == session.ActivePlanDocumentArtifactId);
                if (selected != null && identity.Equals(Identity(session, selected))) session.ActivePlanDocumentArtifactId = null;
            }
        }
    }
}
