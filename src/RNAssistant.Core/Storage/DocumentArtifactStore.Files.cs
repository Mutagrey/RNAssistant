using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        private const string FileTextView = "artifact-file-text-v1";

        public ResourceMutationReadBack RetainFileSnapshot(ChatSession session, ChatArtifact artifact,
            byte[] bytes, string attemptId, string operationKey, ResourceRef source = null)
        {
            if (artifact == null || artifact.Kind != ChatArtifactKinds.File || bytes == null ||
                bytes.LongLength > ArtifactTransferPackage.MaximumBytes)
                throw new InvalidDataException("A bounded authored file is required.");
            string encoding;
            DecodeFileText(bytes, out encoding);
            if (FileEncoding(artifact) != encoding)
                throw new InvalidDataException("The file encoding does not match its retained bytes.");
            var body = PayloadRef.FromBlob(_payloads.StoreBytes(bytes, artifact.MimeType));
            return RetainRecordPayload(session, artifact, body, AuthoredRecordView,
                "application/vnd.rnassistant.artifact-record+json", attemptId, operationKey,
                dependencies: source == null ? null : new[] { new ResourceDependency(source, kind: "source-original") },
                resultView: ResourceRepresentations.Raw);
        }

        public static string FileEncoding(ChatArtifact artifact)
        {
            if (artifact?.Kind != ChatArtifactKinds.File || string.IsNullOrWhiteSpace(artifact.MetadataJson)) return null;
            try { return JsonConvert.DeserializeObject<FileEncodingRecord>(artifact.MetadataJson)?.FileEncoding; }
            catch (JsonException) { return null; }
        }

        public static string DecodeFileText(byte[] bytes, out string encoding)
        {
            if (bytes == null || bytes.LongLength > ArtifactTransferPackage.MaximumBytes)
                throw new InvalidDataException("The text file exceeds 20 MiB.");
            Encoding decoder;
            var offset = 0;
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            { encoding = "utf32-le-bom"; decoder = new UTF32Encoding(false, true, true); offset = 4; }
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            { encoding = "utf32-be-bom"; decoder = new UTF32Encoding(true, true, true); offset = 4; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            { encoding = "utf16-le-bom"; decoder = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            { encoding = "utf16-be-bom"; decoder = new UnicodeEncoding(true, true, true); offset = 2; }
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            { encoding = "utf8-bom"; decoder = new UTF8Encoding(false, true); offset = 3; }
            else { encoding = "utf8"; decoder = new UTF8Encoding(false, true); }
            try
            {
                var text = decoder.GetString(bytes, offset, bytes.Length - offset);
                if (text.IndexOf('\0') >= 0)
                    throw new InvalidDataException("The file is not lossless supported text.");
                return text;
            }
            catch (DecoderFallbackException error)
            { throw new InvalidDataException("The file encoding is unsupported or invalid.", error); }
        }

        public static byte[] EncodeFileText(string text, string encoding)
        {
            if (text == null) throw new InvalidDataException("The file text is unavailable.");
            Encoding codec;
            byte[] bom;
            switch (encoding)
            {
                case "utf8": codec = new UTF8Encoding(false, true); bom = new byte[0]; break;
                case "utf8-bom": codec = new UTF8Encoding(false, true); bom = new byte[] { 0xef, 0xbb, 0xbf }; break;
                case "utf16-le-bom": codec = new UnicodeEncoding(false, true, true); bom = new byte[] { 0xff, 0xfe }; break;
                case "utf16-be-bom": codec = new UnicodeEncoding(true, true, true); bom = new byte[] { 0xfe, 0xff }; break;
                case "utf32-le-bom": codec = new UTF32Encoding(false, true, true); bom = new byte[] { 0xff, 0xfe, 0, 0 }; break;
                case "utf32-be-bom": codec = new UTF32Encoding(true, true, true); bom = new byte[] { 0, 0, 0xfe, 0xff }; break;
                default: throw new InvalidDataException("The file encoding is unsupported.");
            }
            try
            {
                var content = codec.GetBytes(text);
                if ((long)content.Length + bom.Length > ArtifactTransferPackage.MaximumBytes)
                    throw new InvalidDataException("The edited file exceeds 20 MiB.");
                var result = new byte[bom.Length + content.Length];
                Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
                Buffer.BlockCopy(content, 0, result, bom.Length, content.Length);
                return result;
            }
            catch (EncoderFallbackException error)
            { throw new InvalidDataException("The edit cannot be encoded without loss.", error); }
        }

        private PayloadRef FileTextPayload(ChatSession session, ResourceRef reference,
            ChatArtifact artifact, PayloadRef raw)
        {
            if (FileEncoding(artifact) == null) return raw;
            var scope = Scope(session);
            var view = _revisions.GetView(scope, reference, FileTextView);
            if (view != null && view.ContentSha256 == raw.Sha256 && view.Payload != null &&
                view.Payload.ByteLength <= 20 * 1024 * 1024 &&
                _payloads.HasVerifiedReference(raw.ToBlobReference()) &&
                _payloads.HasVerifiedReference(view.Payload.ToBlobReference()))
                return view.Payload;
            var bytes = _payloads.ReadBytes(raw.ToBlobReference());
            if (bytes == null) throw new InvalidDataException("The exact file bytes are unavailable.");
            string encoding;
            var text = DecodeFileText(bytes, out encoding);
            if (encoding != FileEncoding(artifact)) throw new InvalidDataException("The file encoding changed.");
            if (Encoding.UTF8.GetByteCount(text) > 20 * 1024 * 1024)
                throw new InvalidDataException("The decoded file text exceeds the index bound.");
            var payload = PayloadRef.FromBlob(_payloads.StoreText(text, "text/plain; charset=utf-8"));
            if (view == null)
                _revisions.RegisterView(scope, new ResourceRevisionView(reference, FileTextView,
                    raw.Sha256, payload, ResourceCoverage.Whole(), new[] { raw }));
            else if (view.ContentSha256 != raw.Sha256 || view.Payload?.Sha256 != payload.Sha256 ||
                view.Payload.ByteLength != payload.ByteLength)
                throw new InvalidDataException("The retained file text does not match its bytes.");
            return payload;
        }

        private sealed class FileEncodingRecord
        { [JsonProperty("fileEncoding")] public string FileEncoding { get; set; } }
    }
}
