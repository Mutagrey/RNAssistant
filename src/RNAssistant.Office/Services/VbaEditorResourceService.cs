using System;
using System.Globalization;
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
