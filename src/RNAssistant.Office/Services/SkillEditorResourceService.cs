using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class SkillEditorResourceService
    {
        internal const string Owner = "skill-editor";
        internal const int MaximumMutationBytes = 16 * 1024 * 1024;
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceDataPlaneService _data;
        private readonly SkillCatalogService _skills;

        internal SkillEditorResourceService(ResourceGatewayService gateway, ResourceDataPlaneService data, SkillCatalogService skills)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); _data = data ?? throw new ArgumentNullException(nameof(data));
          _skills = skills ?? throw new ArgumentNullException(nameof(skills)); }

        internal SkillSourceReadResponse Open(ChatSession session, SkillSourceReadRequest request, CancellationToken token)
        {
            string path = null;
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || request == null || request.ChatId != session.Id ||
                request.Type != SkillSourceReadRequest.ContractType || request.ContractVersion != SkillLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(request.SkillId) || string.IsNullOrWhiteSpace(request.ExpectedPackageRevision) ||
                request.Path == null || request.Path.Length != 0 && !SkillStore.TryNormalizeReferencePath(request.Path, out path))
                throw Error("RESOURCE_ACCESS_DENIED", "An exact addressed Skill Library source is required.");
            SkillSourceReadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, SkillStore.MaximumSkillReferenceBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var matches = _skills.GetVisibleSkills().Where(item => item.Id == request.SkillId).Take(2).ToArray();
                if (matches.Length != 1) throw Error("RESOURCE_NOT_FOUND", "The skill is not in this host's published catalog.");
                var skill = matches[0];
                var packageRevision = SkillRevision.Compute(skill);
                if (packageRevision != request.ExpectedPackageRevision)
                    throw Error("RESOURCE_REVISION_CHANGED", "The published package changed. Refresh the Skill Library.");
                SkillReferenceMetadata reference = null;
                if (path != null)
                {
                    var references = skill.References.Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if (references.Length != 1) throw Error("RESOURCE_NOT_FOUND", "The exact published reference is unavailable or ambiguous.");
                    reference = references[0];
                }
                var exact = CatalogResourceProvider.SkillResource(skill, reference?.Path);
                var read = _gateway.Read(session, new ResourceReadRequest { Reference = exact,
                    Representation = ResourceRepresentations.Text, MaxChars = 32000 }).Result;
                var payload = read?.CompleteViewPayload;
                if (read?.Resource?.Reference == null || read.Resource.Reference.Uri != exact.Uri || read.Resource.Reference.Revision != exact.Revision ||
                    read.Resource.Kind != (reference == null ? "skill" : "skill-reference") || read.Representation != ResourceRepresentations.Text ||
                    payload == null || payload.ByteLength > SkillStore.MaximumSkillReferenceBytes ||
                    read.TotalCharacters < 0 || read.TotalCharacters > SkillStore.MaximumSkillReferenceCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A complete bounded skill snapshot is required for editing.");
                var bytes = _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
                if (bytes == null || new UTF8Encoding(false, true).GetString(bytes).Length != read.TotalCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact reference snapshot is incomplete or invalid.");
                cancellation.ThrowIfCancellationRequested();
                response = new SkillSourceReadResponse { Type = SkillSourceReadResponse.ContractType,
                    ContractVersion = SkillLibraryResponse.CurrentContractVersion, ChatId = session.Id, SkillId = skill.Id,
                    PackageRevision = packageRevision, Path = reference?.Path ?? "", Resource = exact.Copy(), TotalCharacters = read.TotalCharacters,
                    Reference = reference == null ? null : new SkillReferenceDto { Path = reference.Path, Revision = reference.Revision, ByteLength = reference.ByteLength } };
                return new ResourceDownloadContent { Bytes = bytes, ContentType = "text/markdown; charset=utf-8" };
            }, token);
            try { token.ThrowIfCancellationRequested(); response.Data = lease; return response; }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        internal ResourceUploadOpenResponse BeginUpload(ChatSession session, SkillMutationUploadRequest request, CancellationToken token)
        {
            if (request == null) throw Error("RESOURCE_ACCESS_DENIED", "An explicit skill mutation upload is required.");
            var lease = _data.OpenUpload(session, new ResourceUploadOpenRequest { ChatId = request.ChatId,
                FileName = "skill-mutation.json", ContentType = "application/json; charset=utf-8", ByteLength = request.ByteLength },
                token, Owner, MaximumMutationBytes);
            try { token.ThrowIfCancellationRequested(); return lease; }
            catch { _data.CloseUpload(session.Id, lease.LeaseId, Owner); throw; }
        }

        internal IReadOnlyList<SkillLibraryCoreMutation> PrepareCoreMutations(ChatSession session, SkillMutationWriteRequest request, CancellationToken token)
        { return ValidateSkillLibraryPayload(ReadUploadedMutation<SkillLibraryMutationBatch>(session, request, token)); }

        internal SkillReferenceMutationBody PrepareReferenceMutation(ChatSession session, SkillMutationWriteRequest request, CancellationToken token)
        {
            var body = ReadUploadedMutation<SkillReferenceMutationBody>(session, request, token);
            string path;
            if (body == null || body.Type != SkillReferencePayload.ContractType || body.ContractVersion != SkillLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(body.SkillId) || string.IsNullOrWhiteSpace(body.ExpectedPackageRevision) ||
                !SkillStore.TryNormalizeReferencePath(body.Path, out path))
                throw Error("RESOURCE_UPLOAD_INVALID", "An exact typed reference mutation is required.");
            ValidateSource(body.Content); body.Path = path; return body;
        }

        private T ReadUploadedMutation<T>(ChatSession session, SkillMutationWriteRequest request, CancellationToken token)
        {
            if (request == null || session == null || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit addressed skill mutation upload is required.");
            return _data.ConsumeUpload(session, request.UploadLeaseId, Owner, (bytes, name, mime) =>
            {
                if (bytes.Length > MaximumMutationBytes || mime != "application/json; charset=utf-8" || request.Sha256 == null || request.Sha256.Length != 64)
                    throw Error("RESOURCE_UPLOAD_INVALID", "The skill mutation metadata is invalid.");
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != request.Sha256)
                        throw Error("RESOURCE_UPLOAD_INVALID", "The complete mutation does not match its byte hash.");
                try
                {
                    return JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(bytes),
                        new JsonSerializerSettings { MaxDepth = 16, CheckAdditionalContent = true, MissingMemberHandling = MissingMemberHandling.Error });
                }
                catch (DecoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "The skill mutation is not valid UTF-8."); }
                catch (JsonException) { throw Error("RESOURCE_UPLOAD_INVALID", "The skill mutation is not a complete typed JSON body."); }
            }, token);
        }

        private static void ValidateSource(string text)
        {
            if (text == null || text.Length > SkillStore.MaximumSkillReferenceCharacters)
                throw Error("RESOURCE_BATCH_TOO_LARGE", "A complete bounded skill body is required.");
            try
            {
                if (new UTF8Encoding(false, true).GetByteCount(text) > SkillStore.MaximumSkillReferenceBytes)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The skill body exceeds its byte limit.");
            }
            catch (EncoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "The skill body is not valid Unicode."); }
        }

        private static IReadOnlyList<SkillLibraryCoreMutation>
            ValidateSkillLibraryPayload(SkillLibraryMutationBatch payload)
        {
            if (payload == null ||
                !string.Equals(payload.Type,
                    SkillLibraryMutationBatch.ContractType,
                    StringComparison.Ordinal) ||
                payload.ContractVersion !=
                    SkillLibraryResponse.CurrentContractVersion)
            {
                throw new InvalidOperationException(
                    "Unsupported Skill Library mutation contract.");
            }
            var source = payload.Mutations;
            if (source == null) throw Error("RESOURCE_UPLOAD_INVALID", "An explicit mutation array is required.");
            if (source.Count > 256)
                throw new InvalidOperationException(
                    "Skill Library mutation limit exceeded: 256.");
            var baseIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var targetIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var result = new List<SkillLibraryCoreMutation>();
            foreach (var item in source)
            {
                if (item == null ||
                    !string.Equals(item.Kind, "upsert",
                        StringComparison.Ordinal) &&
                    !string.Equals(item.Kind, "delete",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Skill Library mutation kind is invalid.");
                }
                var baseId = item.BaseId ?? string.Empty;
                var expected = item.ExpectedRevision ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(baseId) &&
                    !baseIds.Add(baseId))
                {
                    throw new InvalidOperationException(
                        "Duplicate Skill Library base id: " + baseId);
                }
                if (string.Equals(item.Kind, "delete",
                    StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(baseId) ||
                        string.IsNullOrWhiteSpace(expected) || item.PreserveBody || item.BodyMarkdown != null)
                    {
                        throw new InvalidOperationException(
                            "Skill delete requires baseId and expectedRevision.");
                    }
                    result.Add(new SkillLibraryCoreMutation
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
                        "Skill upsert id is missing or duplicated: " +
                        (item.Id ?? string.Empty));
                }
                if (string.IsNullOrWhiteSpace(baseId) !=
                    string.IsNullOrWhiteSpace(expected))
                {
                    throw new InvalidOperationException(
                        "Existing skill upsert requires both baseId and expectedRevision; a new skill requires neither.");
                }
                if (item.PreserveBody)
                {
                    if (string.IsNullOrWhiteSpace(baseId) || item.BodyMarkdown != null)
                        throw Error("RESOURCE_UPLOAD_INVALID", "Body preservation requires an existing package and no replacement text.");
                }
                else ValidateSource(item.BodyMarkdown);
                result.Add(new SkillLibraryCoreMutation
                {
                    Kind = item.Kind,
                    BaseId = baseId,
                    ExpectedRevision = expected,
                    PreserveBody = item.PreserveBody,
                    Intended = new SkillDefinition
                    {
                        Id = item.Id,
                        Host = item.Host,
                        Name = item.Name,
                        Description = item.Description,
                        Version = item.Version,
                        BodyMarkdown = item.BodyMarkdown,
                        Enabled = item.Enabled,
                        BuiltIn = false
                    }
                });
            }
            return result;
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
