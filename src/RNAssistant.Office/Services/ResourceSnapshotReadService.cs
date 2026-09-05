using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // One retained text-view reader for logical resources. Only whole
    // captured views or the canonical revision payload can supply a complete body.
    internal sealed class ResourceSnapshotReadService
    {
        private readonly IResourceRevisionStore _revisions;
        private readonly ResourceAuthorityService _authority;
        private readonly ChatBlobStore _payloads;
        internal ResourceSnapshotReadService(ResourceAuthorityService authority, ChatBlobStore payloads)
        { _authority = authority; _revisions = (IResourceRevisionStore)authority.Store; _payloads = payloads; }

        internal ResourceReadSelection Read(ChatSession session, ResourceAuthorityScopeId scope, ResourceDescriptor descriptor, ResourceReadRequest request)
        {
            var exact = request.Reference.IsExact ? request.Reference : descriptor.Reference;
            if (exact?.IsExact != true) throw Error("RESOURCE_HEAD_UNKNOWN", "The logical resource has no known current revision.");
            var snapshot = _authority.CaptureMany(new[] { scope }).Get(scope);
            var metadata = _authority.RequirePublished(snapshot, exact, session);
            var view = string.IsNullOrWhiteSpace(request.Representation) || request.Representation == "auto" ? "text" : request.Representation;
            var captured = _revisions.GetView(scope, exact, view);
            var whole = captured?.Coverage?.Kind == ResourceCoverageKinds.Whole ? captured : null;
            var payload = whole?.Payload ?? (view == "text" ? metadata.Payload : null);
            if (payload == null) throw Error("RESOURCE_VIEW_UNAVAILABLE", "This exact revision has no complete payload for the requested view.");
            if (payload.ByteLength > 8L * 1024 * 1024) throw Error("RESOURCE_BATCH_TOO_LARGE", "The retained view exceeds its materialization bound.");
            var binding = ResourceReadCursor.ReadBinding(exact.Uri, view);
            var position = ResourceReadCursor.ParseExact(request, binding);
            var text = ReadPayload(_payloads, payload);
            if (position.Offset > text.Length) throw Error("RESOURCE_CURSOR_INVALID", "The cursor is outside this exact view.");
            var count = Math.Min(text.Length - position.Offset, Math.Max(1, Math.Min(32000, request.MaxChars <= 0 ? 32000 : request.MaxChars)));
            var next = position.Offset + count;
            var hash = whole?.ContentSha256 ?? metadata.ContentSha256 ?? payload.Sha256;
            var coverage = position.Offset == 0 && next == text.Length ? ResourceCoverage.Whole() :
                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: position.Offset, end: next);
            descriptor.Reference = exact.Copy(); descriptor.Payload = payload; descriptor.MimeType = payload.ContentType;
            descriptor.ContentSha256 = hash; descriptor.ByteLength = payload.ByteLength;
            descriptor.Coverage = coverage;
            descriptor.Dependencies = metadata.Dependencies.ToList();
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor, Representation = view,
                Text = text.Substring(position.Offset, count), ContentSha256 = hash, Offset = position.Offset, Coverage = coverage,
                ReturnedCharacters = count, TotalCharacters = text.Length, Complete = next == text.Length, Truncated = next < text.Length,
                CompleteViewPayload = payload, AuthorityGeneration = snapshot.Generation,
                NextCursor = next < text.Length ? ResourceReadCursor.CreateRevisionBound(next, exact.Revision, binding) : null },
                ResourceRefs = new[] { exact.Copy() } };
        }

        // Callers negotiate their domain-specific size bound before hydration.
        internal static string ReadPayload(ChatBlobStore payloads, PayloadRef payload)
        {
            if (payloads == null || payload == null) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact snapshot payload is unavailable.");
            string text;
            try { text = payloads.ReadText(payload.ToBlobReference()); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is CryptographicException || error is System.Text.DecoderFallbackException)
            { throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact snapshot payload is unavailable or corrupt."); }
            if (text == null) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact snapshot payload is unavailable.");
            return text;
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
