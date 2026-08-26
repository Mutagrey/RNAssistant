using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ChatResourceUri
    {
        public const string ProviderName = "chat";

        public static ResourceRef CreateArtifactRevision(ChatSession session, ChatArtifact artifact)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || artifact == null ||
                string.IsNullOrWhiteSpace(artifact.Id))
            {
                throw new InvalidOperationException("A persisted chat and artifact are required to create a resource reference.");
            }
            var revision = Math.Max(1, artifact.Revision).ToString();
            return new ResourceRef(
                ResourceUri.Create(ProviderName, session.Id, "artifact", artifact.Id, "revision", revision),
                revision);
        }

        public static string CreateArtifactRevisionUri(ChatSession session, ChatArtifact artifact)
        {
            return CreateArtifactRevision(session, artifact).Uri;
        }

        public static ResourceRef ResolveArtifactRevision(ChatSession session, string artifactId)
        {
            var artifact = (session == null ? null : session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
            return artifact == null ? null : CreateArtifactRevision(session, artifact);
        }

        public static bool TryParseArtifactRevision(
            string sessionId,
            ResourceRef reference,
            out string artifactId,
            out int revision)
        {
            string actualSessionId;
            if (!TryParseArtifactRevision(reference, out actualSessionId, out artifactId, out revision) ||
                !string.Equals(actualSessionId, sessionId, StringComparison.Ordinal))
            {
                artifactId = null;
                revision = 0;
                return false;
            }
            return true;
        }

        public static bool TryParseArtifactRevision(
            ResourceRef reference,
            out string sessionId,
            out string artifactId,
            out int revision)
        {
            sessionId = null;
            artifactId = null;
            revision = 0;
            ResourceAddress address;
            if (reference == null || !ResourceUri.TryParse(reference.Uri, out address) ||
                !string.Equals(address.Provider, ProviderName, StringComparison.Ordinal) ||
                (address.Segments.Count != 5 &&
                 !(address.Segments.Count == 8 && string.Equals(address.Segments[5], "member", StringComparison.Ordinal))) ||
                !string.Equals(address.Segments[1], "artifact", StringComparison.Ordinal) ||
                !string.Equals(address.Segments[3], "revision", StringComparison.Ordinal) ||
                !int.TryParse(address.Segments[4], out revision) || revision < 1 ||
                (!string.IsNullOrWhiteSpace(reference.Revision) &&
                 !string.Equals(reference.Revision, address.Segments[4], StringComparison.Ordinal)))
            {
                sessionId = null;
                artifactId = null;
                revision = 0;
                return false;
            }
            sessionId = address.Segments[0];
            artifactId = address.Segments[2];
            return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(artifactId);
        }

        public static ResourceRef RebaseArtifactRevision(ResourceRef reference, string targetSessionId)
        {
            string ignoredSessionId;
            string artifactId;
            int revision;
            if (!TryParseArtifactRevision(reference, out ignoredSessionId, out artifactId, out revision) ||
                string.IsNullOrWhiteSpace(targetSessionId)) return null;
            var address = ResourceUri.Parse(reference.Uri);
            var segments = address.Segments.ToArray();
            segments[0] = targetSessionId;
            return new ResourceRef(ResourceUri.Create(ProviderName, segments), revision.ToString());
        }

        public static bool TryGetCurrentArtifactId(ChatSession session, ResourceRef reference, out string artifactId)
        {
            artifactId = null;
            int revision;
            if (session == null || !TryParseArtifactRevision(session.Id, reference, out artifactId, out revision))
            {
                return false;
            }
            var parsedArtifactId = artifactId;
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, parsedArtifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact != null && Math.Max(1, artifact.Revision) == revision) return true;
            artifactId = null;
            return false;
        }

        public static bool TryGetArtifactId(ChatSession session, ResourceRef reference, out string artifactId)
        {
            artifactId = null;
            string ignoredSessionId;
            int revision;
            if (session == null || !TryParseArtifactRevision(reference, out ignoredSessionId, out artifactId, out revision))
            {
                return false;
            }
            var parsedArtifactId = artifactId;
            var artifact = (session.Artifacts ?? new List<ChatArtifact>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, parsedArtifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact != null && Math.Max(1, artifact.Revision) == revision) return true;
            artifactId = null;
            return false;
        }

        public static List<string> CurrentArtifactIds(ChatSession session, IEnumerable<ResourceRef> references)
        {
            var result = new List<string>();
            foreach (var reference in references ?? new ResourceRef[0])
            {
                string artifactId;
                if (TryGetCurrentArtifactId(session, reference, out artifactId) &&
                    !result.Contains(artifactId, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(artifactId);
                }
            }
            return result;
        }
    }
}
