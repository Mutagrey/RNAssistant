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

namespace RNAssistant.Office.Services
{
    internal sealed class PromptEditorResourceService
    {
        internal const string Owner = "prompt-editor";
        internal const int MaximumSourceBytes = 4 * PromptSettingsService.MaximumPromptCharacters;
        internal const int MaximumMutationBytes = 8 * 1024 * 1024;
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceDataPlaneService _data;

        internal PromptEditorResourceService(ResourceGatewayService gateway, ResourceDataPlaneService data)
        { _gateway = gateway; _data = data; }

        internal static PromptLibraryResponse Metadata(ResourceRef publication)
        {
            return new PromptLibraryResponse { Type = PromptLibraryResponse.ContractType, ContractVersion = 1,
                Publication = publication.Copy(), Items = PromptSettingsService.TemplateKeys.Where(key => key != "systemPromptRole")
                    .Select(key => new PromptMetadataDto { Key = key,
                        Resource = new ResourceRef(ResourceUri.Create("catalog", "prompts", key), publication.Revision) }).ToArray() };
        }

        private static string Key(ResourceRef resource)
        {
            if (resource == null || !resource.IsExact) throw Error("RESOURCE_ACCESS_DENIED", "An exact prompt resource is required.");
            var key = PromptSettingsService.TemplateKeys.FirstOrDefault(item => item != "systemPromptRole" &&
                resource.Uri == ResourceUri.Create("catalog", "prompts", item));
            if (key == null) throw Error("RESOURCE_ACCESS_DENIED", "Only published editable prompt bodies are supported.");
            return key;
        }

        internal PromptSourceReadResponse Open(ChatSession session, PromptSourceReadRequest request, CancellationToken token)
        {
            if (session == null || request == null || string.IsNullOrWhiteSpace(session.Id) || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit addressed prompt read is required.");
            Key(request.Resource);
            var exact = request.Resource.Copy();
            PromptSourceReadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, MaximumSourceBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var read = _gateway.Read(session, new ResourceReadRequest { Reference = exact,
                    Representation = ResourceRepresentations.Text, MaxChars = 32000 }).Result;
                var payload = read?.CompleteViewPayload;
                if (read?.Resource?.Reference == null || read.Resource.Reference.Uri != exact.Uri || read.Resource.Reference.Revision != exact.Revision ||
                    read.Resource.Kind != "prompt" || read.Representation != ResourceRepresentations.Text || payload == null ||
                    payload.ByteLength > MaximumSourceBytes || read.TotalCharacters < 0 || read.TotalCharacters > PromptSettingsService.MaximumPromptCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A complete bounded prompt snapshot is required.");
                var bytes = _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
                if (bytes == null || new UTF8Encoding(false, true).GetString(bytes).Length != read.TotalCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact prompt snapshot is incomplete.");
                cancellation.ThrowIfCancellationRequested();
                response = new PromptSourceReadResponse { Type = PromptSourceReadResponse.ContractType, ContractVersion = 1,
                    ChatId = session.Id, Resource = exact, TotalCharacters = read.TotalCharacters };
                return new ResourceDownloadContent { Bytes = bytes, ContentType = "text/markdown; charset=utf-8" };
            }, token);
            try { token.ThrowIfCancellationRequested(); response.Data = lease; return response; }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        internal ResourceUploadOpenResponse BeginUpload(ChatSession session, PromptMutationUploadRequest request, CancellationToken token)
        {
            if (request == null) throw Error("RESOURCE_ACCESS_DENIED", "An explicit prompt upload is required.");
            var lease = _data.OpenUpload(session, new ResourceUploadOpenRequest { ChatId = request.ChatId,
                FileName = "prompt-mutation.json", ContentType = "application/json; charset=utf-8", ByteLength = request.ByteLength },
                token, Owner, MaximumMutationBytes);
            try { token.ThrowIfCancellationRequested(); return lease; }
            catch { _data.CloseUpload(session.Id, lease.LeaseId, Owner); throw; }
        }

        internal PromptMutationBatch ReadMutation(ChatSession session, SaveSettingsPayload request, CancellationToken token)
        {
            if (session == null || request == null || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An addressed settings save is required.");
            if (request.UploadLeaseId == null && request.Sha256 == null) return null;
            return _data.ConsumeUpload(session, request.UploadLeaseId, Owner, (bytes, name, mime) =>
            {
                if (bytes.Length > MaximumMutationBytes || mime != "application/json; charset=utf-8" || request.Sha256 == null || request.Sha256.Length != 64)
                    throw Error("RESOURCE_UPLOAD_INVALID", "Invalid prompt upload metadata.");
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != request.Sha256)
                        throw Error("RESOURCE_UPLOAD_INVALID", "The complete prompt upload does not match its hash.");
                try
                {
                    var batch = JsonConvert.DeserializeObject<PromptMutationBatch>(new UTF8Encoding(false, true).GetString(bytes),
                        new JsonSerializerSettings { MaxDepth = 8, CheckAdditionalContent = true, MissingMemberHandling = MissingMemberHandling.Error });
                    Validate(batch); return batch;
                }
                catch (DecoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "Invalid UTF-8 prompt upload."); }
                catch (JsonException) { throw Error("RESOURCE_UPLOAD_INVALID", "A complete typed prompt mutation is required."); }
            }, token);
        }

        private static void Validate(PromptMutationBatch batch)
        {
            if (batch == null || batch.Type != PromptMutationBatch.ContractType || batch.ContractVersion != 1 ||
                batch.Changes == null || batch.Changes.Count < 1 || batch.Changes.Count > 8)
                throw Error("RESOURCE_UPLOAD_INVALID", "A bounded explicit prompt change list is required.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in batch.Changes)
            {
                if (change == null || !keys.Add(Key(change.Resource)) || change.Value == null || change.Value.Length > PromptSettingsService.MaximumPromptCharacters)
                    throw Error("RESOURCE_UPLOAD_INVALID", "Prompt changes must be unique, complete and bounded.");
                try { new UTF8Encoding(false, true).GetByteCount(change.Value); }
                catch (EncoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "Prompt text must be valid Unicode."); }
            }
        }

        internal static AppSettings Prepare(AppSettings source, SettingsControlsDto controls, ResourceRef publication,
            string publishedTemplates, PromptMutationBatch batch)
        {
            if (controls == null || publication == null || !publication.IsExact || publication.Uri != "rna://catalog/prompts")
                throw Error("RESOURCE_ACCESS_DENIED", "Settings controls and their exact prompt publication are required.");
            var intended = controls.ApplyTo(PromptSettingsService.ApplyPublishedTemplates(source, publishedTemplates));
            if (batch == null) return intended;
            Validate(batch);
            foreach (var change in batch.Changes)
            {
                if (change.Resource.Revision != publication.Revision)
                    throw Error("RESOURCE_REVISION_CHANGED", "The prompt draft is stale. Refresh before saving.");
                PromptSettingsService.SetValue(intended, Key(change.Resource), change.Value);
            }
            return intended;
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
