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
            if (exact?.IsExact != true)
                throw Unavailable("Exact resource identity is missing or not revision-pinned.");
            var metadata = revisions.GetRevision(snapshot.ScopeId, exact);
            if (metadata == null)
                throw Unavailable("Exact resource revision metadata is not registered.");
            if (PublicationOrder(snapshot, exact) == null)
                throw Unavailable("Exact resource revision has not crossed its publication barrier.");
            return metadata;
        }

        public static string ReadPayload(ChatBlobStore payloads, PayloadRef payload, long maximumBytes)
        {
            if (payloads == null || payload == null)
                throw Unavailable("Exact snapshot payload owner or reference is missing.");
            if (payload.ByteLength > maximumBytes)
                throw Unavailable("Exact snapshot payload exceeds the bounded read budget.");
            string text;
            try { text = payloads.ReadText(payload.ToBlobReference()); }
            catch (CryptographicException)
            { throw Unavailable("Exact snapshot payload failed storage-protection verification."); }
            catch (InvalidDataException)
            { throw Unavailable("Exact snapshot payload failed integrity validation."); }
            catch (System.Text.DecoderFallbackException)
            { throw Unavailable("Exact snapshot payload has invalid text encoding."); }
            catch (IOException)
            { throw Unavailable("Exact snapshot payload could not be read from storage."); }
            if (text == null) throw Unavailable("Exact snapshot payload is missing from storage.");
            return text;
        }

        private static ResourceRequestException Unavailable(string message)
        { return new ResourceRequestException(message, "RESOURCE_SNAPSHOT_UNAVAILABLE", false); }
    }
}
