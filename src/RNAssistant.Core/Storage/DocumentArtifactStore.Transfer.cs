using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class DocumentArtifactStore
    {
        public byte[] ExportBytes(ChatSession session, ResourceRef reference)
        {
            var item = Read(session, reference, false);
            if (!item.ContentByteLength.HasValue || item.ContentByteLength > ArtifactTransferPackage.MaximumBytes)
                throw new InvalidDataException("Содержимое недоступно или превышает 20 МиБ.");
            var bytes = _payloads.ReadBytes(new ChatBlobReference { Sha256 = item.ContentSha256,
                ByteLength = item.ContentByteLength.Value, ContentType = item.MimeType });
            if (bytes == null || bytes.LongLength != item.ContentByteLength.Value)
                throw new InvalidDataException("Полное содержимое артефакта недоступно.");
            return bytes;
        }

        // One derived publication exposes the entire imported aggregate. CAS writes made
        // before the commit are not catalog entries; no existing head is overwritten.
        public ChatArtifact ImportAuthored(ChatSession session, ArtifactTransferPackage project,
            string title, string text, string kind, string operationId, ArtifactTransferOrigin origin = null)
        {
            Guid parsed;
            if (!Guid.TryParseExact(operationId, "N", out parsed)) throw new InvalidDataException("Требуется идентификатор переноса.");
            project?.Validate();
            var scope = Scope(session);
            var hash = TextPatternEngine.Sha256(session.Id + ":" + operationId);
            var logicalId = project != null ? "html_ws_" + hash : kind == ChatArtifactKinds.Markdown ? "artifact_md_" + hash : "artifact_" + hash;
            var root = new ChatArtifact { Id = logicalId + "_r1_" + hash.Substring(0, 8), Revision = 1,
                DocumentAuthorityId = scope.Id, Title = title, Kind = kind, InlineText = text,
                MimeType = kind == ChatArtifactKinds.Markdown ? "text/markdown; charset=utf-8" : "application/json", MetadataJson = "{}" };
            var before = _authority.Capture(scope);
            var rootRef = ChatResourceUri.CreateArtifactRevision(session, root);
            if (before.GetHead(rootRef.Identity) != null) throw new InvalidOperationException("Этот перенос уже опубликован. Найдите копию в каталоге.");
            var retained = new List<ResourceMutationReadBack>();
            var dependencies = new List<ResourceDependency>();
            if (project != null)
            {
                var snapshot = new HtmlWorkspaceSnapshot { Label = project.Title, ActiveFileId = project.EntryPath.ToLowerInvariant(),
                    Files = project.Files.Select(f => new HtmlWorkspaceFile { Id = f.Path.ToLowerInvariant(), Path = f.Path, Kind = f.Kind, Content = f.Content }).ToList() };
                for (var i = 0; i < project.Data.Count; i++)
                {
                    var data = project.Data[i];
                    var member = new ChatArtifact { Id = "artifact_" + TextPatternEngine.Sha256(hash + ":" + i), Revision = 1,
                        DocumentAuthorityId = scope.Id, Title = data.Name + ".json", Kind = ChatArtifactKinds.File,
                        MimeType = "application/json", InlineText = data.Json, MetadataJson = "{}" };
                    var saved = RetainAuthoredSnapshot(session, member, operationId, operationId);
                    retained.Add(saved);
                    dependencies.Add(new ResourceDependency(saved.Revision, data.View, kind: "html-binding"));
                    root.RelatedArtifactIds.Add(member.Id);
                    snapshot.DataSources.Add(new HtmlWorkspaceDataSource { Id = data.Name.ToLowerInvariant(), Name = data.Name,
                        Binding = new HtmlWorkspaceDataBinding { Resource = saved.Revision, Policy = "exact", View = data.View, ViewPath = data.ViewPath,
                            Schema = ImportAuxiliary(session, data.SchemaJson, hash + ":schema:" + i, operationId, retained, dependencies, root),
                            Mapping = ImportAuxiliary(session, data.MappingJson, hash + ":mapping:" + i, operationId, retained, dependencies, root) } });
                }
                root.Kind = ChatArtifactKinds.HtmlWorkspace;
                root.MimeType = "application/vnd.rnassistant.html-workspace+json";
                root.InlineText = JsonConvert.SerializeObject(snapshot);
            }
            origin = project?.Origin ?? origin;
            root.MetadataJson = JsonConvert.SerializeObject(new ArtifactImportMetadata {
                ImportedFromUri = origin?.ResourceUri, ImportedSourceContentSha256 = origin?.ContentSha256,
                ActiveFileId = project?.EntryPath?.ToLowerInvariant(), FileCount = project?.Files.Count ?? 0, DataSourceCount = project?.Data.Count ?? 0 });
            var savedRoot = RetainAuthoredSnapshot(session, root, operationId, operationId, dependencies: dependencies);
            retained.Add(savedRoot);
            var changes = retained.Select(r => new ResourceHeadChange(r.Identity, null,
                ResourceHeadState.Known(r.Revision, before.Generation + 1, "artifact-import"))).ToList();
            if (project != null || kind == ChatArtifactKinds.Markdown)
            {
                var identity = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, logicalId));
                var link = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(savedRoot.Revision), "application/json"));
                var logical = new ResourceRef(identity.Uri, "r_" + operationId);
                _revisions.RegisterRevision(scope, new ResourceRevisionMetadata(logical, link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(savedRoot.Revision, kind: "immutable-snapshot") }));
                changes.Add(new ResourceHeadChange(identity, null, ResourceHeadState.Known(logical, before.Generation + 1, "artifact-import")));
            }
            _authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null, changes, AuthorityCommitReason.DerivedPublication));
            return Read(session, savedRoot.Revision);
        }

        private ResourceRef ImportAuxiliary(ChatSession session, string json, string seed, string operationId,
            List<ResourceMutationReadBack> retained, List<ResourceDependency> dependencies, ChatArtifact root)
        {
            if (json == null) return null;
            var artifact = new ChatArtifact { Id = "artifact_" + TextPatternEngine.Sha256(seed), Revision = 1,
                DocumentAuthorityId = session.DocumentAuthorityId, Kind = ChatArtifactKinds.File, Title = "Project dependency.json",
                MimeType = "application/json", InlineText = json, MetadataJson = "{}" };
            var saved = RetainAuthoredSnapshot(session, artifact, operationId, operationId);
            retained.Add(saved); dependencies.Add(new ResourceDependency(saved.Revision, "text", kind: "html-binding"));
            root.RelatedArtifactIds.Add(artifact.Id); return saved.Revision;
        }

        public ChatArtifact ImportOriginal(ChatSession session, string fileName, string contentType, byte[] bytes, string operationId, ArtifactTransferOrigin origin = null)
        {
            Guid parsed;
            if (!Guid.TryParseExact(operationId, "N", out parsed) || bytes == null || bytes.Length > ArtifactTransferPackage.MaximumBytes)
                throw new InvalidDataException("Некорректный импорт файла.");
            var raw = _payloads.StoreBytes(bytes, contentType);
            var original = new ChatAttachment { Id = TextPatternEngine.Sha256(session.Id + ":" + operationId), FileName = fileName,
                ContentType = contentType, Size = bytes.Length, Kind = contentType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "file",
                ContentSha256 = raw.Sha256, ContentByteLength = raw.ByteLength };
            if (contentType.StartsWith("text/", StringComparison.Ordinal))
            {
                try {
                    var text = new UTF8Encoding(false, true).GetString(bytes);
                    original.ExtractedTextSha256 = raw.Sha256; original.ExtractedTextByteLength = raw.ByteLength; original.ExtractedCharCount = text.Length;
                }
                catch (DecoderFallbackException) { original.ExtractionWarning = "Текст не UTF-8; исходные байты сохранены без преобразования."; }
            }
            var artifact = new ChatArtifact { Id = "attachment_" + original.Id, Revision = 1, Title = fileName,
                Kind = original.Kind == "image" ? ChatArtifactKinds.Image : ChatArtifactKinds.Attachment,
                MetadataJson = JsonConvert.SerializeObject(new ArtifactImportMetadata { ImportedFromUri = origin?.ResourceUri, ImportedSourceContentSha256 = origin?.ContentSha256 }),
                MimeType = contentType, ContentSha256 = raw.Sha256, ContentByteLength = raw.ByteLength, DocumentAuthorityId = session.DocumentAuthorityId };
            if (_authority.GetHead(Scope(session), ChatResourceUri.CreateArtifactRevision(session, artifact).Identity) != null)
                throw new InvalidOperationException("Этот перенос уже опубликован. Найдите файл в каталоге.");
            return PublishOriginal(session, artifact, original);
        }
    }
}
