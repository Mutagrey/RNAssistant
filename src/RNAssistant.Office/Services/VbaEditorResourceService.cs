using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    internal sealed class VbaEditorResourceService
    {
        internal const string Owner = "vba-editor";
        internal const int MaximumCharacters = 1000000;
        internal const int MaximumBytes = 4 * MaximumCharacters;
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceDataPlaneService _data;

        internal VbaEditorResourceService(ResourceGatewayService gateway, ResourceDataPlaneService data)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); _data = data ?? throw new ArgumentNullException(nameof(data)); }

        internal ResourceUploadOpenResponse BeginUpload(ChatSession session, VbaEditorUploadRequest request, CancellationToken token)
        {
            if (request == null) throw Error("RESOURCE_ACCESS_DENIED", "An explicit source upload request is required.");
            return _data.OpenUpload(session, new ResourceUploadOpenRequest { ChatId = request.ChatId,
                FileName = "vba-source.txt", ContentType = "text/plain; charset=utf-8", ByteLength = request.ByteLength },
                token, Owner, MaximumBytes, true);
        }

        internal string ReadUploadedSource(ChatSession session, VbaEditorWriteRequest request, CancellationToken token)
        {
            if (request == null || session == null || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit addressed source upload is required.");
            // Consume once before mutation preparation. Uploaded data is not a VBA revision,
            // model observation or permission to write; mutation guards and publication remain with their existing owner.
            return _data.ConsumeUpload(session, request.UploadLeaseId, Owner, (bytes, fileName, contentType) =>
            {
                var save = request as VbaModulePayload;
                if (save != null && (save.ExpectedCodeSha256 == null || save.ExpectedCodeSha256.Length != 64 ||
                    save.ExpectedCodeSha256.Any(character => !Uri.IsHexDigit(character))))
                    throw Error("vba_editor_guard_required", "Saving requires the exact source hash from the editor read.");
                if (bytes.Length > MaximumBytes || contentType != "text/plain; charset=utf-8" ||
                    string.IsNullOrWhiteSpace(request.ModuleName) || request.SourceSha256 == null || request.SourceSha256.Length != 64)
                    throw Error("RESOURCE_UPLOAD_INVALID", "The VBA source metadata is invalid.");
                using (var sha = SHA256.Create())
                {
                    var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(hash, request.SourceSha256, StringComparison.Ordinal))
                        throw Error("RESOURCE_UPLOAD_INVALID", "The uploaded source does not match its byte hash.");
                }
                string code;
                try { code = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "The VBA source is not valid UTF-8."); }
                if (code.Length > MaximumCharacters)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The VBA source exceeds the editor character limit.");
                return code;
            }, token);
        }

        internal VbaEditorReadResponse Open(ChatSession session, string moduleName, CancellationToken token)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.DocumentAuthorityId) ||
                string.IsNullOrWhiteSpace(moduleName)) throw Error("RESOURCE_ACCESS_DENIED", "An exact document/chat and module are required.");
            VbaEditorReadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, MaximumBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var identity = VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, moduleName);
                ResourceReadResult read;
                using (DocumentAccessGate.BeginOperation())
                    read = _gateway.Read(session, new ResourceReadRequest { Reference = new ResourceRef(identity.Uri),
                        Representation = ResourceRepresentations.Source, MaxChars = 32000 }).Result;
                cancellation.ThrowIfCancellationRequested();
                var resource = read?.Resource;
                var payload = read?.CompleteViewPayload;
                if (resource?.Reference?.IsExact != true || resource.Reference.Identity.Uri != identity.Uri ||
                    resource.Kind != VbaResourceProvider.ComponentKind || read.Representation != ResourceRepresentations.Source ||
                    !string.Equals(resource.Title, moduleName, StringComparison.OrdinalIgnoreCase))
                    throw Error("vba_editor_read_invalid", "The Gateway did not capture the requested VBA component.");
                if (payload == null || read.TotalCharacters < 0 || read.TotalCharacters > MaximumCharacters || payload.ByteLength > MaximumBytes)
                    throw Error("vba_editor_source_truncated", "The complete VBA source is unavailable within the editor limit. Saving partial code is blocked.");
                var bytes = _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
                if (bytes == null) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact VBA source snapshot is unavailable.");
                var code = new UTF8Encoding(false, true).GetString(bytes);
                if (code.Length != read.TotalCharacters || !string.Equals(VbaTextCanonicalizer.LiveCodeSha256(code),
                    read.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw Error("vba_editor_read_invalid", "The complete source does not match its write guard.");
                string componentType, lines;
                int lineCount;
                if (!resource.Metadata.TryGetValue("componentType", out componentType) || string.IsNullOrWhiteSpace(componentType) ||
                    !resource.Metadata.TryGetValue("lineCount", out lines) ||
                    !int.TryParse(lines, NumberStyles.None, CultureInfo.InvariantCulture, out lineCount) || lineCount < 0)
                    throw Error("vba_editor_read_invalid", "The source metadata is incomplete.");
                cancellation.ThrowIfCancellationRequested();
                response = new VbaEditorReadResponse { ChatId = session.Id, ModuleName = resource.Title,
                    ComponentType = componentType, LineCount = lineCount, TotalCharacters = code.Length,
                    CodeSha256 = read.ContentSha256, Resource = resource.Reference.Copy() };
                return new ResourceDownloadContent { Bytes = bytes, ContentType = "text/plain; charset=utf-8" };
            }, token);
            try { token.ThrowIfCancellationRequested(); response.Data = lease; return response; }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
