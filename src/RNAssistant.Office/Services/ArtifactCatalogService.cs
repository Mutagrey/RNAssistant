using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class ArtifactCatalogService
    {
        internal const string Owner = "artifact-transfer";
        private readonly DocumentAuthorityRegistry _documents;
        private readonly DocumentArtifactStore _artifacts;
        private readonly ArtifactWorkingSetService _links;
        private readonly ResourceAuthorityStore _authority;
        private readonly ChatBlobStore _blobs;
        internal ArtifactCatalogService(DocumentAuthorityRegistry documents, DocumentArtifactStore artifacts,
            ArtifactWorkingSetService links, ResourceAuthorityStore authority, ChatBlobStore blobs)
        { _documents = documents; _artifacts = artifacts; _links = links; _authority = authority; _blobs = blobs; }

        private IReadOnlyList<DocumentAuthorityEntry> Documents(ChatSession actor)
        {
            var entries = _documents.List().Where(d => d.AuthorityId != actor.DocumentAuthorityId).ToList();
            entries.Add(new DocumentAuthorityEntry { AuthorityId = actor.DocumentAuthorityId, Host = actor.Host, Locator = actor.DocumentTitle });
            return entries;
        }
        private static string Title(DocumentAuthorityEntry entry)
        { return string.IsNullOrEmpty(entry.Locator) ? entry.Host + " · " + entry.AuthorityId.Substring(0, Math.Min(8, entry.AuthorityId.Length)) : Path.GetFileName(entry.Locator); }
        private ChatSession Source(ChatSession actor, string documentId)
        {
            if (documentId == actor.DocumentAuthorityId) return actor;
            var entry = Documents(actor).SingleOrDefault(d => d.AuthorityId == documentId);
            if (entry == null) throw new InvalidOperationException("Документ отсутствует в локальном каталоге.");
            // Read-only context for exact saved content. Never passed to live Office tools.
            return new ChatSession { Id = actor.Id, DocumentAuthorityId = entry.AuthorityId, Host = entry.Host, DocumentTitle = Title(entry) };
        }
        internal ArtifactCatalogResponse List(ChatSession actor, ArtifactCatalogRequest request)
        {
            if (request.ChatId != actor.Id || request.Scope != "chat" && request.Scope != "document" && request.Scope != "all" || (request.Query?.Length ?? 0) > 200)
                throw new InvalidOperationException("Некорректный запрос каталога.");
            var documents = Documents(actor);
            var items = new List<ArtifactCatalogItem>();
            foreach (var doc in documents.Where(d => (request.Scope == "all" || d.AuthorityId == actor.DocumentAuthorityId) &&
                (string.IsNullOrEmpty(request.DocumentId) || d.AuthorityId == request.DocumentId)))
            {
                var source = Source(actor, doc.AuthorityId);
                foreach (var item in _links.CurrentItems(source).Where(a => a.Kind != DocumentArtifactStore.SharedContextKind &&
                    a.Kind != ChatArtifactKinds.ToolResult && a.Kind != ChatArtifactKinds.TaskList && a.Kind != ChatArtifactKinds.Compaction))
                {
                    var linked = doc.AuthorityId == actor.DocumentAuthorityId && ArtifactWorkingSet.IsLinked(actor, item);
                    if (request.Scope == "chat" && !linked || !string.IsNullOrEmpty(request.Kind) && request.Kind != item.Kind && !(request.Kind == "file" && item.Kind == ChatArtifactKinds.Attachment) ||
                        !string.IsNullOrEmpty(request.Query) && (item.Title ?? "").IndexOf(request.Query.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    items.Add(new ArtifactCatalogItem { DocumentId = doc.AuthorityId, DocumentTitle = Title(doc),
                        ResourceUri = ChatResourceUri.CreateArtifactRevisionUri(source, item), Title = item.Title ?? "Недоступный ресурс",
                        Kind = item.Kind, UpdatedUtc = item.CreatedUtc, Linked = linked,
                        Selected = linked && (item.Id == actor.ActiveHtmlArtifactId || item.Id == actor.ActivePlanDocumentArtifactId ||
                            MarkdownDocumentIdentity.LogicalId(item.Id) != null && actor.ArtifactLinks.Any(l => !l.Detached && l.Reference.Uri == ChatResourceUri.CreateArtifactRevisionUri(actor, item))), AvailabilityIssue = item.AvailabilityIssue,
                        CanTransfer = CanTransfer(item) });
                }
            }
            items = items.OrderByDescending(i => i.UpdatedUtc).ThenBy(i => i.ResourceUri, StringComparer.Ordinal).ToList();
            var stamp = TextPatternEngine.Sha256(JsonConvert.SerializeObject(new object[] { actor.Id, actor.Revision,
                request.Scope, request.Query, request.Kind, request.DocumentId, items }));
            var offset = 0;
            if (!string.IsNullOrEmpty(request.Cursor))
            {
                var parts = request.Cursor.Split('.');
                if (parts.Length != 2 || parts[0] != stamp || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset <= 0 || offset >= items.Count || offset % 50 != 0)
                    throw new InvalidOperationException("Каталог изменился. Обновите список.");
            }
            return new ArtifactCatalogResponse { ChatId = actor.Id, DocumentId = actor.DocumentAuthorityId, SessionRevision = actor.Revision,
                Items = items.Skip(offset).Take(50).ToArray(), NextCursor = items.Count > offset + 50 ? stamp + "." + (offset + 50) : null,
                Documents = documents.Select(d => new ArtifactCatalogDocument { Id = d.AuthorityId, Title = Title(d) }).ToArray() };
        }
        private static bool CanTransfer(ChatArtifact item)
        { return string.IsNullOrEmpty(item.AvailabilityIssue) && (item.Kind == ChatArtifactKinds.HtmlWorkspace || item.Kind == ChatArtifactKinds.Markdown || item.Kind == ChatArtifactKinds.File || item.OriginalAttachment != null); }

        internal ArtifactTransferContent Export(ChatSession actor, ArtifactTransferRequest request)
        {
            var source = Source(actor, request.DocumentId);
            var reference = ChatResourceUri.ArtifactSnapshot(new ResourceRef(request.ResourceUri));
            if (reference.Uri != request.ResourceUri) throw new InvalidDataException("Требуется точная ссылка артефакта.");
            var artifact = _artifacts.Read(source, reference);
            if (!CanTransfer(artifact) && !request.Preview) throw new InvalidOperationException("Перенос этого типа пока не поддерживается.");
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace)
            {
                var project = CaptureProject(source, artifact);
                project.Origin = new ArtifactTransferOrigin { ResourceUri = reference.Uri, ContentSha256 = artifact.ContentSha256 };
                return new ArtifactTransferContent { Kind = "html_workspace", FileName = SafeName(artifact.Title) + ".rna-html.zip",
                    ContentType = request.Preview ? "application/json" : "application/zip", Bytes = request.Preview ? Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Preview(project))) : project.Export() };
            }
            var bytes = _artifacts.ExportBytes(source, reference);
            return new ArtifactTransferContent { Kind = artifact.Kind, FileName = SafeName(artifact.OriginalAttachment?.FileName ?? artifact.Title) +
                (artifact.OriginalAttachment != null ? "" : artifact.Kind == ChatArtifactKinds.Markdown && !artifact.Title.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? ".md" : artifact.MimeType == "application/json" && !artifact.Title.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ".json" : ""),
                ContentType = artifact.MimeType ?? "application/octet-stream", Bytes = bytes,
                Origin = new ArtifactTransferOrigin { ResourceUri = reference.Uri, ContentSha256 = artifact.ContentSha256 } };
        }

        private ArtifactTransferPackage CaptureProject(ChatSession source, ChatArtifact artifact)
        {
            var snapshot = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText ?? "null");
            if (snapshot?.Files == null || snapshot.DataSources == null) throw new InvalidDataException("Исходники проекта недоступны.");
            var entry = snapshot.Files.SingleOrDefault(f => f.Id == snapshot.ActiveFileId && f.Kind == "html") ?? snapshot.Files.FirstOrDefault(f => f.Kind == "html");
            var project = new ArtifactTransferPackage { Title = artifact.Title, EntryPath = entry?.Path,
                Files = snapshot.Files.Select(f => new ArtifactTransferFile { Path = f.Path, Kind = f.Kind, Content = f.Content }).ToList() };
            var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(source.DocumentAuthorityId));
            var authority = _authority.Capture(scope);
            foreach (var data in snapshot.DataSources)
            {
                var binding = data.Binding;
                if (binding?.Resource == null) throw new InvalidDataException("Отсутствует привязка данных.");
                var exact = binding.Policy == "head" ? authority.GetHead(binding.Resource.Identity)?.Revision : binding.Resource;
                if (exact == null || !exact.IsExact) throw new InvalidDataException("Нет сохранённого снимка данных: " + data.Name);
                var json = ReadSavedJson(source, scope, exact);
                project.Data.Add(new ArtifactTransferData { Name = data.Name, Json = json, View = binding.View, ViewPath = binding.ViewPath,
                    SchemaJson = binding.Schema == null ? null : ReadSavedJson(source, scope, binding.Schema),
                    MappingJson = binding.Mapping == null ? null : ReadSavedJson(source, scope, binding.Mapping) });
            }
            if (_authority.Capture(scope).Generation != authority.Generation)
                throw new InvalidOperationException("Данные изменились во время экспорта. Повторите действие.");
            project.ExternalDependencies = project.Files.SelectMany(f => Regex.Matches(f.Content ?? "", @"https?://[^\s'""<>]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
                .Cast<Match>().Select(m => m.Value)).Distinct().Take(257).ToList();
            project.Validate();
            return project;
        }

        private string ReadSavedJson(ChatSession source, ResourceAuthorityScopeId scope, ResourceRef exact)
        {
            if (!exact.IsExact) throw new InvalidDataException("Зависимости требуют точной сохранённой версии.");
            string json;
            if (DocumentArtifactStore.Owns(source, exact))
                json = new UTF8Encoding(false, true).GetString(_artifacts.ExportBytes(source, exact));
            else {
                var head = _authority.GetHead(scope, exact.Identity);
                var view = _authority.GetView(scope, exact, "text");
                var payload = view?.Coverage?.Kind == ResourceCoverageKinds.Whole ? view.Payload : null;
                if (head == null || payload == null || payload.ByteLength > 8000000)
                    throw new InvalidDataException("Нет полного сохранённого снимка зависимости. Откройте исходный документ и сохраните данные проекта.");
                json = _blobs.ReadText(payload.ToBlobReference());
            }
            if (json == null) throw new InvalidDataException("Сохранённое содержимое зависимости недоступно.");
            JToken.Parse(json); return json;
        }

        private static ArtifactProjectPreview Preview(ArtifactTransferPackage project)
        {
            var result = new ArtifactProjectPreview { Project = new ArtifactTransferPackage { Title = project.Title,
                EntryPath = project.EntryPath, Files = project.Files, ExternalDependencies = project.ExternalDependencies,
                Data = project.Data.Select(d => new ArtifactTransferData { Name = d.Name, View = d.View, ViewPath = d.ViewPath }).ToList() } };
            foreach (var data in project.Data)
            {
                var view = new ArtifactPreviewData { Reference = new ResourceRef(ResourceUri.Create("preview", Guid.NewGuid().ToString("N")), "1"), Name = data.Name, View = data.View, Path = data.ViewPath ?? "$", Text = data.Json };
                if (data.View != "text")
                {
                    var root = JToken.Parse(data.Json);
                    var array = (view.Path == "$" ? root : root.SelectToken(view.Path)) as JArray;
                    if (array == null) throw new InvalidDataException("Данные предпросмотра не являются массивом записей.");
                    var keys = new List<string>();
                    view.Rows = new List<IDictionary<string, object>>();
                    foreach (var item in array)
                    {
                        var row = item as JObject ?? new JObject(((JArray)item).Select((cell, i) => new JProperty("c" + (i + 1), cell.DeepClone())));
                        foreach (var property in row.Properties()) if (!keys.Contains(property.Name)) keys.Add(property.Name);
                        view.Rows.Add(row.ToObject<Dictionary<string, object>>());
                    }
                    view.Columns = keys.Select(key => new ResourceTableColumn { Key = key, Label = key, Type = "json" }).ToArray();
                    view.Text = null;
                }
                result.Views.Add(view);
            }
            return result;
        }

        internal ChatArtifact Import(ChatSession actor, byte[] bytes, string fileName, string contentType, string operationId, ArtifactTransferOrigin origin = null)
        {
            if (bytes == null || bytes.Length > ArtifactTransferPackage.MaximumBytes) throw new InvalidDataException("Файл превышает 20 МиБ.");
            fileName = SafeName(fileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            ArtifactTransferPackage project = null;
            if (fileName.EndsWith(".rna-html.zip", StringComparison.OrdinalIgnoreCase)) project = ArtifactTransferPackage.Import(bytes);
            if (extension == ".html" || extension == ".htm")
                project = new ArtifactTransferPackage { Title = fileName, EntryPath = "index.html", Files = new List<ArtifactTransferFile> {
                    new ArtifactTransferFile { Path = "index.html", Kind = "html", Content = new UTF8Encoding(false, true).GetString(bytes) } } };
            if (project != null) return _artifacts.ImportAuthored(actor, project, project.Title, null, ChatArtifactKinds.HtmlWorkspace, operationId);
            if (extension == ".md" || extension == ".markdown" || extension == ".json")
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length > (extension == ".json" ? 2000000 : 200000)) throw new InvalidDataException("Текст превышает допустимый размер.");
                if (extension == ".json") JToken.Parse(text);
                return _artifacts.ImportAuthored(actor, null, fileName, text, extension == ".json" ? ChatArtifactKinds.File : ChatArtifactKinds.Markdown, operationId, origin);
            }
            return _artifacts.ImportOriginal(actor, fileName, contentType ?? "application/octet-stream", bytes, operationId, origin);
        }
        private static string SafeName(string name)
        {
            var result = new string((name ?? "artifact").Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).Take(180).ToArray());
            return string.IsNullOrWhiteSpace(result) ? "artifact" : result;
        }
    }
    internal sealed class ArtifactProjectPreview
    {
        public ArtifactTransferPackage Project { get; set; }
        public List<ArtifactPreviewData> Views { get; set; } = new List<ArtifactPreviewData>();
    }
    internal sealed class ArtifactPreviewData
    {
        public ResourceRef Reference { get; set; }
        public string Name { get; set; }
        public string View { get; set; }
        public string Path { get; set; }
        public string Text { get; set; }
        public List<IDictionary<string, object>> Rows { get; set; }
        public IReadOnlyList<ResourceTableColumn> Columns { get; set; }
    }
    internal sealed class ArtifactTransferContent
    {
        internal string FileName, ContentType, Kind;
        internal byte[] Bytes;
        internal ArtifactTransferOrigin Origin;
    }
}
