using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    // Pure publication check plus bounded CAS hydration. A registered revision
    // alone is not a published resource; authoring files are never a fallback.
    public static class ResourcePublicationReader
    {
        public static long[] PublicationOrder(ResourceAuthoritySnapshot snapshot, ResourceRef exact)
        {
            if (exact?.IsExact != true) return null;
            var head = snapshot.GetHead(exact.Identity);
            if (head?.Knowledge == HeadKnowledge.Known && head.Revision.Uri == exact.Uri && head.Revision.Revision == exact.Revision)
                return new[] { head.AuthorityGeneration };
            var publication = snapshot.Commits.LastOrDefault(commit => commit.HeadChanges.Any(change =>
                change.After.Knowledge == HeadKnowledge.Known && change.After.Revision.Uri == exact.Uri &&
                change.After.Revision.Revision == exact.Revision));
            return publication == null ? null : new[] { publication.NewGeneration };
        }

        public static ResourceRevisionMetadata RequirePublished(IResourceRevisionStore revisions,
            ResourceAuthoritySnapshot snapshot, ResourceRef exact)
        {
            var metadata = exact?.IsExact == true ? revisions.GetRevision(snapshot.ScopeId, exact) : null;
            if (metadata == null || PublicationOrder(snapshot, exact) == null)
                throw Unavailable("The exact resource revision has not crossed its publication barrier or is unavailable.");
            return metadata;
        }

        public static string ReadPayload(ChatBlobStore payloads, PayloadRef payload, long maximumBytes)
        {
            if (payloads == null || payload == null || payload.ByteLength > maximumBytes)
                throw Unavailable("The exact snapshot payload is unavailable or exceeds its bound.");
            string text;
            try { text = payloads.ReadText(payload.ToBlobReference()); }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                error is CryptographicException || error is System.Text.DecoderFallbackException)
            { throw Unavailable("The exact snapshot payload is unavailable or corrupt."); }
            if (text == null) throw Unavailable("The exact snapshot payload is unavailable.");
            return text;
        }

        private static ResourceRequestException Unavailable(string message)
        { return new ResourceRequestException(message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
    }
}
