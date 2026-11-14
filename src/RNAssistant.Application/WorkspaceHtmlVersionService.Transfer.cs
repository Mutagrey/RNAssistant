using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Application
{
    // Ephemeral projection owned by one existing data-plane lease, never a second store.
    public sealed class HtmlProjectExport : IDisposable
    {
        public Stream Stream { get; set; }
        public PayloadRef Payload { get; set; }
        public string FileName { get; set; }
        public void Dispose() { Stream?.Dispose(); Stream = null; }
    }

    public sealed partial class WorkspaceHtmlVersionService
    {
        public const long MaximumExportBytes = WorkspaceFileService.MaximumCaptureSetBytes + 1024 * 1024;
        public bool TransferOwnerAvailable(BrowserOwnerScope owner, string workspaceId)
        { try { RequireOwner(owner, workspaceId); return true; } catch { return false; } }
        private BrowserTargetDescriptor TransferTarget(BrowserTargetDescriptor requested)
        {
            var target = Target(requested);
            if (target.State != BrowserTargetState.Ready || string.IsNullOrEmpty(target.SavedVersionId))
                throw new InvalidOperationException("Выберите доступную сохранённую версию проекта.");
            return target;
        }
        public HtmlProjectImportResult CopySavedProject(BrowserTargetDescriptor requested, CancellationToken token)
        {
            var target = TransferTarget(requested);
            var snapshot = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, target.SavedVersionId);
            var entry = _artifacts.WorkspaceHtmlVersion(target.WorkspaceId, target.SavedVersionId);
            return new HtmlProjectImportService(_paths, _files).ImportSnapshot(target.Title, snapshot,
                new ArtifactTransferOrigin { ResourceUri = entry.ContentReference.Uri, ContentSha256 = entry.ContentFingerprint },
                path => _artifacts.OpenWorkspaceHtmlMember(target.WorkspaceId, target.SavedVersionId, path, token), token);
        }
        public HtmlProjectExport ExportSavedProject(BrowserTargetDescriptor requested, CancellationToken token)
        {
            var target = TransferTarget(requested);
            var snapshot = _artifacts.ReadWorkspaceHtml(target.WorkspaceId, target.SavedVersionId);
            var file = new FileStream(Path.Combine(Path.GetTempPath(), "rna-html-export-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.DeleteOnClose);
            try
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, true))
                    foreach (var member in snapshot.Members.OrderBy(m => m.Path, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        using (var input = _artifacts.OpenWorkspaceHtmlMember(target.WorkspaceId, target.SavedVersionId, member.Path, token))
                        using (var output = archive.CreateEntry(member.Path, CompressionLevel.Optimal).Open())
                            HtmlProjectImportService.CopyExact(input, output, member.ByteLength, token);
                        if (file.Length > MaximumExportBytes) throw new InvalidDataException("Архив превышает лимит переноса.");
                    }
                if (file.Length > MaximumExportBytes) throw new InvalidDataException("Архив превышает лимит переноса.");
                token.ThrowIfCancellationRequested(); file.Position = 0;
                string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                token.ThrowIfCancellationRequested(); file.Position = 0;
                var name = new string((target.Title ?? "HTML project").Take(120).Where(c => !char.IsControl(c) && c != '/' && c != '\\' && !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
                return new HtmlProjectExport { Stream = file, Payload = new PayloadRef(hash, file.Length, "application/zip"),
                    FileName = (string.IsNullOrWhiteSpace(name) ? "HTML project" : name) + ".zip" };
            }
            catch { file.Dispose(); throw; }
        }
    }
}
