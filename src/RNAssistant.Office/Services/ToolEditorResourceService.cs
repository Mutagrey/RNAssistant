using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    internal sealed class ToolEditorResourceService
    {
        internal const string Owner = "tool-editor";
        internal const int MaximumMutationBytes = 16 * 1024 * 1024;
        private readonly ResourceDataPlaneService _data;
        private readonly ResourceGatewayService _gateway;
        private readonly ToolCatalogService _tools;

        internal ToolEditorResourceService(ResourceGatewayService gateway, ResourceDataPlaneService data, ToolCatalogService tools)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); _data = data ?? throw new ArgumentNullException(nameof(data));
          _tools = tools ?? throw new ArgumentNullException(nameof(tools)); }

        internal ToolSourceReadResponse Open(ChatSession session, ToolSourceReadRequest request, CancellationToken token)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || request == null || request.ChatId != session.Id ||
                request.Type != ToolSourceReadRequest.ContractType || request.ContractVersion != ToolLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ExpectedRevision))
                throw Error("RESOURCE_ACCESS_DENIED", "An exact addressed Tool Library source is required.");
            ToolSourceReadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, MaximumMutationBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                using (DocumentAccessGate.BeginOperation())
                {
                    var matches = _tools.GetVisibleTools().Where(item => item.Id == request.ToolId).Take(2).ToArray();
                    if (matches.Length != 1) throw Error("RESOURCE_NOT_FOUND", "The tool is not in this host's catalog.");
                    var tool = matches[0];
                    if (ToolAuthoringService.LibraryRevision(tool) != request.ExpectedRevision)
                        throw Error("RESOURCE_REVISION_CHANGED", "The tool changed. Refresh the Tool Library.");
                    var sources = new List<ResourceRef>();
                    byte[] bytes;
                    if (tool.Scope == "document")
                    {
                        if (string.IsNullOrWhiteSpace(session.DocumentAuthorityId) || tool.Components == null || tool.Components.Count == 0)
                            throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact document components are unavailable.");
                        try
                        {
                            foreach (var component in tool.Components)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                var identity = VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, component.Name);
                                var read = _gateway.Read(session, new ResourceReadRequest { Reference = new ResourceRef(identity.Uri),
                                    Representation = ResourceRepresentations.Source, MaxChars = 32000 }).Result;
                                var captured = ReadComplete(read, identity.Uri, VbaResourceProvider.ComponentKind, ResourceRepresentations.Source);
                                string componentType;
                                // Exact source text proves the cached manifest/component snapshot, not a canonicalized hash.
                                if (!read.Resource.Metadata.TryGetValue("componentType", out componentType) ||
                                    componentType != component.Type || new UTF8Encoding(false, true).GetString(captured) != component.Code)
                                    throw Error("RESOURCE_REVISION_CHANGED", "The document tool changed. Refresh the Tool Library.");
                                sources.Add(read.Resource.Reference.Copy());
                            }
                        }
                        catch { _tools.InvalidateDocumentVbaTools(); throw; }
                        bytes = ToolSourceBodyDto.Bytes(tool);
                    }
                    else
                    {
                        var uri = ResourceUri.Create("catalog", tool.BuiltIn ? "builtin-tools-" + _tools.HostName.ToLowerInvariant() : "tools", tool.Id, "source");
                        var read = _gateway.Read(session, new ResourceReadRequest { Reference = new ResourceRef(uri),
                            Representation = ResourceRepresentations.Text, MaxChars = 32000 }).Result;
                        bytes = ReadComplete(read, uri, "tool-source", ResourceRepresentations.Text);
                        var expected = ToolSourceMetadataDto.From(tool);
                        using (var sha = SHA256.Create())
                            if (bytes.LongLength != expected.ByteLength || BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != expected.Sha256)
                                throw Error("RESOURCE_REVISION_CHANGED", "The published tool source changed. Refresh the Tool Library.");
                        sources.Add(read.Resource.Reference.Copy());
                    }
                    cancellation.ThrowIfCancellationRequested();
                    response = new ToolSourceReadResponse { Type = ToolSourceReadResponse.ContractType,
                        ContractVersion = ToolLibraryResponse.CurrentContractVersion, ChatId = session.Id, ToolId = tool.Id,
                        Revision = request.ExpectedRevision, Sources = sources };
                    return new ResourceDownloadContent { Bytes = bytes, ContentType = "application/json; charset=utf-8" };
                }
            }, token);
            try { token.ThrowIfCancellationRequested(); response.Data = lease; return response; }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        private byte[] ReadComplete(ResourceReadResult read, string uri, string kind, string representation)
        {
            var payload = read?.CompleteViewPayload;
            if (read?.Resource?.Reference?.IsExact != true || read.Resource.Reference.Uri != uri || read.Resource.Kind != kind ||
                read.Representation != representation || payload == null || payload.ByteLength > MaximumMutationBytes ||
                read.TotalCharacters < 0 || read.TotalCharacters > MaximumMutationBytes)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A complete bounded tool source snapshot is required.");
            var bytes = _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
            if (bytes == null || new UTF8Encoding(false, true).GetString(bytes).Length != read.TotalCharacters)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The tool source snapshot is incomplete or invalid.");
            return bytes;
        }

        internal ResourceUploadOpenResponse BeginUpload(ChatSession session, ToolMutationUploadRequest request, CancellationToken token)
        {
            if (request == null) throw Error("RESOURCE_ACCESS_DENIED", "An explicit tool mutation upload is required.");
            var lease = _data.OpenUpload(session, new ResourceUploadOpenRequest { ChatId = request.ChatId,
                FileName = "tool-mutation.json", ContentType = "application/json; charset=utf-8", ByteLength = request.ByteLength },
                token, Owner, MaximumMutationBytes);
            try { token.ThrowIfCancellationRequested(); return lease; }
            catch { _data.CloseUpload(session.Id, lease.LeaseId, Owner); throw; }
        }

        internal IReadOnlyList<ToolLibraryCoreMutation> PrepareMutations(ChatSession session, ToolMutationWriteRequest request, CancellationToken token)
        {
            if (request == null || session == null || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit addressed tool mutation upload is required.");
            var body = _data.ConsumeUpload(session, request.UploadLeaseId, Owner, (bytes, name, mime) =>
            {
                if (bytes.Length > MaximumMutationBytes || mime != "application/json; charset=utf-8" || request.Sha256 == null || request.Sha256.Length != 64)
                    throw Error("RESOURCE_UPLOAD_INVALID", "The tool mutation metadata is invalid.");
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != request.Sha256)
                        throw Error("RESOURCE_UPLOAD_INVALID", "The complete mutation does not match its byte hash.");
                try
                {
                    return JsonConvert.DeserializeObject<ToolLibraryMutationBatch>(new UTF8Encoding(false, true).GetString(bytes),
                        new JsonSerializerSettings { MaxDepth = 16, CheckAdditionalContent = true, MissingMemberHandling = MissingMemberHandling.Error });
                }
                catch (DecoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "The tool mutation is not valid UTF-8."); }
                catch (JsonException) { throw Error("RESOURCE_UPLOAD_INVALID", "The tool mutation is not a complete typed JSON body."); }
            }, token);
            var mutations = ValidateToolLibraryPayload(body);
            token.ThrowIfCancellationRequested();
            return mutations;
        }

        private static IReadOnlyList<ToolLibraryCoreMutation>
            ValidateToolLibraryPayload(ToolLibraryMutationBatch payload)
        {
            if (payload == null || !string.Equals(payload.Type,
                    ToolLibraryMutationBatch.ContractType,
                    StringComparison.Ordinal) ||
                payload.ContractVersion !=
                    ToolLibraryResponse.CurrentContractVersion)
            {
                throw new InvalidOperationException(
                    "Unsupported Tool Library mutation contract.");
            }
            var source = payload.Mutations;
            if (source == null) throw Error("RESOURCE_UPLOAD_INVALID", "An explicit mutation array is required.");
            if (source.Count > 256)
                throw new InvalidOperationException(
                    "Tool Library mutation limit exceeded: 256.");
            var baseIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var targetIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var result = new List<ToolLibraryCoreMutation>();
            foreach (var item in source)
            {
                if (item == null ||
                    !string.Equals(item.Kind, "upsert",
                        StringComparison.Ordinal) &&
                    !string.Equals(item.Kind, "delete",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Tool Library mutation kind is invalid.");
                }
                var baseId = item.BaseId ?? string.Empty;
                var expected = item.ExpectedRevision ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(baseId) &&
                    !baseIds.Add(baseId))
                {
                    throw new InvalidOperationException(
                        "Duplicate Tool Library base id: " + baseId);
                }
                if (string.Equals(item.Kind, "delete",
                    StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(baseId) ||
                        string.IsNullOrWhiteSpace(expected))
                    {
                        throw new InvalidOperationException(
                            "Tool delete requires baseId and expectedRevision.");
                    }
                    result.Add(new ToolLibraryCoreMutation
                    {
                        Kind = item.Kind,
                        BaseId = baseId,
                        ExpectedRevision = expected
                    });
                    continue;
                }
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    !targetIds.Add(item.Id))
                {
                    throw new InvalidOperationException(
                        "Tool upsert id is missing or duplicated: " +
                        (item.Id ?? string.Empty));
                }
                if (string.IsNullOrWhiteSpace(baseId) !=
                    string.IsNullOrWhiteSpace(expected))
                {
                    throw new InvalidOperationException(
                        "Existing tool upsert requires both baseId and expectedRevision; a new tool requires neither.");
                }
                result.Add(new ToolLibraryCoreMutation
                {
                    Kind = item.Kind,
                    BaseId = baseId,
                    ExpectedRevision = expected,
                    Intended = item.ToCatalogEntry()
                });
            }
            return result;
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
