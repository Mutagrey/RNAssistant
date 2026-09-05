using System;
using System.Linq;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class SkillReferenceResourceService
    {
        internal const string Owner = "skill-reference-editor";
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceDataPlaneService _data;
        private readonly SkillCatalogService _skills;

        internal SkillReferenceResourceService(ResourceGatewayService gateway, ResourceDataPlaneService data, SkillCatalogService skills)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); _data = data ?? throw new ArgumentNullException(nameof(data));
          _skills = skills ?? throw new ArgumentNullException(nameof(skills)); }

        internal SkillReferenceReadResponse Open(ChatSession session, SkillReferenceReadRequest request, CancellationToken token)
        {
            string path;
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || request == null || request.ChatId != session.Id ||
                request.Type != SkillReferencePayload.ContractType || request.ContractVersion != SkillLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(request.SkillId) || string.IsNullOrWhiteSpace(request.ExpectedPackageRevision) ||
                !SkillStore.TryNormalizeReferencePath(request.Path, out path))
                throw Error("RESOURCE_ACCESS_DENIED", "An exact addressed Skill Library reference is required.");
            SkillReferenceReadResponse response = null;
            var lease = _data.OpenDownload(session, Owner, SkillStore.MaximumSkillReferenceBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var matches = _skills.GetVisibleSkills().Where(item => !item.BuiltIn && item.Id == request.SkillId).Take(2).ToArray();
                if (matches.Length != 1) throw Error("RESOURCE_NOT_FOUND", "The custom skill is not in this host's published catalog.");
                var skill = matches[0];
                var packageRevision = SkillRevision.Compute(skill);
                if (packageRevision != request.ExpectedPackageRevision)
                    throw Error("RESOURCE_REVISION_CHANGED", "The published package changed. Refresh the Skill Library.");
                var references = skill.References.Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (references.Length != 1) throw Error("RESOURCE_NOT_FOUND", "The exact published reference is unavailable or ambiguous.");
                var reference = references[0];
                var exact = CatalogResourceProvider.SkillResource(skill, reference.Path);
                var read = _gateway.Read(session, new ResourceReadRequest { Reference = exact,
                    Representation = ResourceRepresentations.Text, MaxChars = 32000 }).Result;
                var payload = read?.CompleteViewPayload;
                if (read?.Resource?.Reference == null || read.Resource.Reference.Uri != exact.Uri || read.Resource.Reference.Revision != exact.Revision ||
                    read.Resource.Kind != "skill-reference" || read.Representation != ResourceRepresentations.Text ||
                    payload == null || payload.ByteLength > SkillStore.MaximumSkillReferenceBytes ||
                    read.TotalCharacters < 0 || read.TotalCharacters > SkillStore.MaximumSkillReferenceCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A complete bounded reference snapshot is required for editing.");
                var bytes = _gateway.Authority.Payloads.ReadBytes(payload.ToBlobReference());
                if (bytes == null || new UTF8Encoding(false, true).GetString(bytes).Length != read.TotalCharacters)
                    throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact reference snapshot is incomplete or invalid.");
                cancellation.ThrowIfCancellationRequested();
                response = new SkillReferenceReadResponse { Type = SkillReferenceReadResponse.ContractType,
                    ContractVersion = SkillLibraryResponse.CurrentContractVersion, ChatId = session.Id, SkillId = skill.Id,
                    PackageRevision = packageRevision, Resource = exact.Copy(), TotalCharacters = read.TotalCharacters,
                    Reference = new SkillReferenceDto { Path = reference.Path, Revision = reference.Revision, ByteLength = reference.ByteLength } };
                return new ResourceDownloadContent { Bytes = bytes, ContentType = "text/markdown; charset=utf-8" };
            }, token);
            try { token.ThrowIfCancellationRequested(); response.Data = lease; return response; }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
