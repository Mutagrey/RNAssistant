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
                throw Unavailable("RESOURCE_SNAPSHOT_INVALID_REFERENCE", "Exact resource identity is missing or not revision-pinned.");
            var metadata = revisions.GetRevision(snapshot.ScopeId, exact);
            if (metadata == null)
                throw Unavailable("RESOURCE_SNAPSHOT_METADATA_MISSING", "Exact resource revision metadata is not registered.");
            if (PublicationOrder(snapshot, exact) == null)
                throw Unavailable("RESOURCE_SNAPSHOT_UNPUBLISHED", "Exact resource revision has not crossed its publication barrier.");
            return metadata;
        }

        public static string ReadPayload(ChatBlobStore payloads, PayloadRef payload, long maximumBytes)
        {
            if (payloads == null || payload == null)
                throw Unavailable("RESOURCE_SNAPSHOT_OWNER_MISSING", "Exact snapshot payload owner or reference is missing.");
            if (payload.ByteLength > maximumBytes)
                throw Unavailable("RESOURCE_SNAPSHOT_BUDGET", "Exact snapshot payload exceeds the bounded read budget.");
            try
            {
                var reference = payload.ToBlobReference();
                if (!payloads.MatchesCurrentProtection(reference))
                    throw Unavailable("RESOURCE_SNAPSHOT_PROTECTION", "Snapshot belongs to another storage protection context.");
                using (var body = payloads.OpenVerifiedRead(reference, Math.Max(1, maximumBytes)))
                using (var reader = new StreamReader(body, new System.Text.UTF8Encoding(false, true), false))
                    return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            { throw Unavailable("RESOURCE_SNAPSHOT_MISSING", "Exact snapshot payload is missing."); }
            catch (DirectoryNotFoundException)
            { throw Unavailable("RESOURCE_SNAPSHOT_MISSING", "Exact snapshot payload is missing."); }
            catch (CryptographicException)
            { throw Unavailable("RESOURCE_SNAPSHOT_PROTECTION", "Exact snapshot payload failed storage-protection verification."); }
            catch (InvalidDataException)
            { throw Unavailable("RESOURCE_SNAPSHOT_INTEGRITY", "Exact snapshot payload failed integrity validation."); }
            catch (System.Text.DecoderFallbackException)
            { throw Unavailable("RESOURCE_SNAPSHOT_ENCODING", "Exact snapshot payload has invalid text encoding."); }
            catch (UnauthorizedAccessException)
            { throw Unavailable("RESOURCE_SNAPSHOT_PROTECTION", "Access to the exact snapshot payload was denied."); }
            catch (IOException)
            { throw Unavailable("RESOURCE_SNAPSHOT_IO", "Exact snapshot payload could not be read from storage."); }
        }

        private static ResourceRequestException Unavailable(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
