using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ToolEditorResourceService
    {
        internal const string Owner = "tool-editor";
        internal const int MaximumMutationBytes = 16 * 1024 * 1024;
        private readonly ResourceDataPlaneService _data;

        internal ToolEditorResourceService(ResourceDataPlaneService data)
        { _data = data ?? throw new ArgumentNullException(nameof(data)); }

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
