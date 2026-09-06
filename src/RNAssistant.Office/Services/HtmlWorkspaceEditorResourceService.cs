using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Transport/preparation only. Writes retain the existing HTML domain and commit owner.
    internal sealed class HtmlWorkspaceEditorResourceService
    {
        internal const string Owner = "html-editor";
        internal const int MaximumSourceBytes = 4 * HtmlWorkspaceToolService.MaxHtmlChars;
        private const string ContentType = "text/plain; charset=utf-8";
        private readonly OfficeToolExecutor _executor;
        private readonly ResourceDataPlaneService _data;

        internal HtmlWorkspaceEditorResourceService(OfficeToolExecutor executor, ResourceDataPlaneService data)
        { _executor = executor; _data = data; }

        internal ResourceUploadOpenResponse BeginUpload(ChatSession session, HtmlWorkspaceMutationUploadRequest request, CancellationToken token)
        {
            if (request == null) throw Error("RESOURCE_ACCESS_DENIED", "An addressed HTML upload is required.");
            var lease = _data.OpenUpload(session, new ResourceUploadOpenRequest { ChatId = request.ChatId,
                FileName = "html-workspace-source.txt", ContentType = ContentType, ByteLength = request.ByteLength },
                token, Owner, MaximumSourceBytes, allowEmpty: true);
            try { token.ThrowIfCancellationRequested(); return lease; }
            catch { _data.CloseUpload(session.Id, lease.LeaseId, Owner); throw; }
        }

        // The caller reserves and reloads the exact chat. No gate is held during upload.
        internal void SaveFile(ChatSession session, HtmlWorkspaceFilePayload request, CancellationToken token)
        {
            var content = ReadSource(session, request, token);
            var expected = ValidateCurrent(session, request, token);
            _executor.MutateLocalResources(session, "common.html_workspace_write_file", new Dictionary<string, object> {
                ["path"] = request.Path, ["kind"] = request.Kind, ["content"] = content, ["setActive"] = request.SetActive != false,
                ["expectedActiveHtmlArtifactId"] = request.ExpectedActiveHtmlArtifactId, ["expectedRevision"] = expected },
                () => HtmlWorkspaceToolService.UpsertFile(session, request.Path, request.Kind, content, request.SetActive != false),
                validateBeforeDispatch: () => {
                    if (ValidateCurrent(session, request, token) != expected) throw Stale();
                    HtmlWorkspaceToolService.ValidateFileWrite(session, request.Path, request.Kind, content);
                });
        }

        internal void SaveData(ChatSession session, HtmlWorkspaceDataPayload request, CancellationToken token)
        {
            var json = ReadSource(session, request, token);
            var expected = ValidateCurrent(session, request, token);
            _executor.MutateLocalResources(session, "common.html_data_write", new Dictionary<string, object> {
                ["name"] = request.Name, ["json"] = json, ["expectedActiveHtmlArtifactId"] = request.ExpectedActiveHtmlArtifactId,
                ["expectedRevision"] = expected },
                () => HtmlWorkspaceToolService.UpsertDataSource(session, request.Name, json),
                validateBeforeDispatch: () => {
                    if (ValidateCurrent(session, request, token) != expected) throw Stale();
                    HtmlWorkspaceToolService.ValidateDataWrite(session, request.Name, json);
                });
        }

        private string ReadSource(ChatSession session, HtmlWorkspaceMutationPayload request, CancellationToken token)
        {
            if (session == null || request == null || string.IsNullOrWhiteSpace(session.Id) || request.ChatId != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "An addressed HTML save is required.");
            return _data.ConsumeUpload(session, request.UploadLeaseId, Owner, (bytes, name, mime) =>
            {
                if (bytes.Length > MaximumSourceBytes || mime != ContentType || request.Sha256 == null || request.Sha256.Length != 64)
                    throw Error("RESOURCE_UPLOAD_INVALID", "Invalid HTML upload metadata.");
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != request.Sha256)
                        throw Error("RESOURCE_UPLOAD_INVALID", "The complete HTML upload does not match its hash.");
                try { return new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw Error("RESOURCE_UPLOAD_INVALID", "Invalid UTF-8 HTML upload."); }
            }, token);
        }

        private string ValidateCurrent(ChatSession session, HtmlWorkspaceMutationPayload request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.ExpectedActiveHtmlArtifactId == null || request.ExpectedActiveHtmlArtifactId != (session.ActiveHtmlArtifactId ?? ""))
                throw Stale();
            var authority = _executor.ResourceAuthority;
            var scope = authority.Scope(session, false);
            var head = authority.CaptureMany(new[] { scope }).Get(scope).GetHead(ResourceStateProvider.Identity(scope, "html-workspace"));
            if (string.IsNullOrEmpty(session.ActiveHtmlArtifactId) && (head == null || head.Knowledge == HeadKnowledge.Unavailable)) return null;
            if (head?.Knowledge != HeadKnowledge.Known)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The current HTML workspace must have a known publication.");
            var revision = ((IResourceRevisionStore)authority.Store).GetRevision(scope, head.Revision);
            var artifact = session.Artifacts.SingleOrDefault(item => item.Id == session.ActiveHtmlArtifactId);
            var exact = artifact == null ? null : ChatResourceUri.CreateArtifactRevision(session, artifact);
            if (exact == null || revision == null || !revision.Dependencies.Any(item => item.Kind == "immutable-snapshot" &&
                item.Resource.Uri == exact.Uri && item.Resource.Revision == exact.Revision))
                throw Error("RESOURCE_REVISION_CHANGED", "The displayed HTML revision no longer matches its publication.");
            return head.Revision.Revision;
        }

        private static ResourceRequestException Stale()
        { return Error("RESOURCE_REVISION_CHANGED", "The HTML draft is stale. Refresh before saving."); }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
