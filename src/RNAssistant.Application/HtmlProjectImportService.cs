using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Application
{
    public sealed class HtmlProjectImportException : IOException
    {
        public BrowserRefusalCode Reason { get; private set; }
        public HtmlProjectImportException(BrowserRefusalCode reason, string message) : base(message) { Reason = reason; }
    }
    // Copies exact source into the existing file/workspace authority. No browser/session store.
    public sealed class HtmlProjectImportService
    {
        public const int MaximumSourceBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex HostReference = new Regex(@"\bRN\b|rnassistant|rn-vendor:|rna?://|https?://",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
        private static readonly Lazy<Regex[]> VendorReferences = new Lazy<Regex[]>(ReadVendorReferences);
        private readonly AppDataPaths _paths;
        private readonly WorkspaceStore _workspaces;
        private readonly WorkspaceFileService _files;

        public HtmlProjectImportService(AppDataPaths paths, WorkspaceFileService files = null)
        { _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _workspaces = new WorkspaceStore(paths);
            _files = files ?? new WorkspaceFileService(paths); }

        // Exact provider bytes only. No directory discovery, download or encoding conversion.
        public static ArtifactTransferPackage FromHtmlResource(byte[] bytes, string title, ArtifactTransferOrigin origin)
        {
            string hash = null;
            if (bytes != null && bytes.Length <= MaximumSourceBytes)
                using (var digest = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (bytes == null || bytes.Length > MaximumSourceBytes || origin == null || string.IsNullOrWhiteSpace(origin.ResourceUri) ||
                origin.ContentSha256 != hash)
                throw new HtmlProjectImportException(BrowserRefusalCode.InvalidContent, "Точный HTML-источник недоступен или превышает лимит 16 МиБ.");
            string text;
            try { text = Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new HtmlProjectImportException(BrowserRefusalCode.UnsupportedEncoding, "Поддерживается только исходный UTF-8 HTML без преобразования кодировки."); }
            if (!Utf8.GetBytes(text).SequenceEqual(bytes) || text.IndexOf('\0') >= 0 ||
                Regex.IsMatch(text, @"\bcharset\s*=\s*['""\s]*(?!utf-8\b|utf8\b)[a-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300)))
                throw new HtmlProjectImportException(BrowserRefusalCode.UnsupportedEncoding, "HTML объявляет неподдерживаемую кодировку.");
            var decoded = System.Net.WebUtility.HtmlDecode(text);
            // Conservative source admission. Runtime network isolation remains a
            // separate gate; this check never declares arbitrary script portable.
            var dependency = Regex.IsMatch(decoded, @"@import\b|\bsrcset\s*=|\b(?:import|require|fetch|XMLHttpRequest|WebSocket|EventSource|Worker|SharedWorker|serviceWorker)\b|<base\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
            var references = Regex.Matches(decoded, @"\b(?:src|href|poster|action|data)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))|url\s*\(\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s)]+))\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
            foreach (Match reference in references)
            {
                var value = reference.Groups["value"].Value.Trim();
                dependency |= value.Length != 0 && !value.StartsWith("#", StringComparison.Ordinal) && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            }
            if (dependency || HostReference.IsMatch(text))
                throw new HtmlProjectImportException(BrowserRefusalCode.UnsupportedDependency, "HTML использует внешние или необъявленные зависимости. Они не будут скачаны или взяты из соседней папки.");
            return new ArtifactTransferPackage { Title = title, EntryPath = "index.html", Origin = origin,
                Files = new List<ArtifactTransferFile> { new ArtifactTransferFile { Path = "index.html", Kind = "html", Content = text } } };
        }

        public HtmlProjectImportResult Import(ArtifactTransferPackage project, CancellationToken token)
        {
            List<PreparedFile> prepared;
            try { prepared = Prepare(project); token.ThrowIfCancellationRequested(); }
            catch (Exception error) { return new HtmlProjectImportResult {
                Status = HtmlProjectImportStatus.Rejected, Backing = HtmlProjectBacking.Managed, Message = error.Message,
                Reason = (error as HtmlProjectImportException)?.Reason ?? BrowserRefusalCode.InvalidContent }; }
            var root = Path.Combine(_paths.Root, "html-projects", Guid.NewGuid().ToString("N"));
            var result = new HtmlProjectImportResult { Status = HtmlProjectImportStatus.Unknown,
                Backing = HtmlProjectBacking.Managed, RootPath = root, Name = project.Title, EntryPath = project.EntryPath };
            try
            {
                // The workspace stays hidden during writes, including after interruption.
                StorageFileSystem.EnsureRegularDirectory(Path.GetDirectoryName(root));
                var workspace = _workspaces.CreateHiddenHtmlProject(root, result.Name, result.EntryPath, prepared.Select(file => file.Path).ToArray());
                result.WorkspaceId = workspace.WorkspaceId;
                foreach (var file in prepared)
                {
                    token.ThrowIfCancellationRequested();
                    _files.CreateText(workspace, file.Path, file.Content);
                }
                foreach (var file in prepared)
                {
                    token.ThrowIfCancellationRequested();
                    var observed = _files.CaptureFile(workspace, file.Path, MaximumSourceBytes, token);
                    if (observed.ContentSha256 != file.Sha256)
                        throw new IOException("Содержимое записанного файла изменилось: " + file.Path);
                }
                token.ThrowIfCancellationRequested();
                var published = _workspaces.ConnectProject(root, false);
                if (published.WorkspaceId != workspace.WorkspaceId || published.HtmlEntryPath != result.EntryPath)
                    throw new IOException("Идентичность созданного проекта изменилась.");
                result.Status = HtmlProjectImportStatus.Created;
                return result;
            }
            catch (Exception error)
            {
                // File mutation owners retain their prepared/terminal evidence. Never delete or replay here.
                result.Message = "Создание проекта не подтверждено. Проверьте папку " + root +
                    " и список проектов перед повторным созданием. " + error.Message;
                return result;
            }
        }

        private sealed class PreparedFile
        { internal string Path, Content, Sha256; }

        private static List<PreparedFile> Prepare(ArtifactTransferPackage project)
        {
            if (project == null || project.Data == null || project.ExternalDependencies == null)
                throw new InvalidDataException("Сохранённый HTML-проект недоступен.");
            if (project.Data.Count != 0 || project.ExternalDependencies.Count != 0)
                throw new InvalidDataException("Перенос данных документа и внешних зависимостей пока не поддерживается. Исходный проект сохранён.");
            project.Validate();
            if (!project.EntryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !project.EntryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл запуска должен иметь расширение HTML.");
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fileNames = new HashSet<string>(project.Files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            var prepared = new List<PreparedFile>(); long total = 0;
            foreach (var file in project.Files)
            {
                WorkspaceFileService.ValidateWritePath(file.Path);
                if (!PluginPackageFiles.ValidPath(file.Path) || file.Path.IndexOfAny(new[] { '<', '>', '"', '|', '*' }) >= 0 ||
                    !file.Path.IsNormalized(NormalizationForm.FormC))
                    throw new InvalidDataException("Непереносимый путь файла: " + file.Path);
                var prefix = ""; var parts = file.Path.Split('/');
                for (var index = 0; index < parts.Length; index++)
                {
                    prefix = prefix.Length == 0 ? parts[index] : prefix + "/" + parts[index];
                    if (paths.TryGetValue(prefix, out var existing) && existing != prefix ||
                        index < parts.Length - 1 && fileNames.Contains(prefix))
                        throw new InvalidDataException("Файлы или каталоги конфликтуют: " + file.Path);
                    paths[prefix] = prefix;
                }
                total += Utf8.GetByteCount(file.Content);
                if (total > MaximumSourceBytes) throw new InvalidDataException("Проект превышает лимит переноса 16 МиБ.");
                if (HostReference.IsMatch(file.Content) || file.Kind != "css" && VendorReferences.Value.Any(pattern => pattern.IsMatch(file.Content)))
                    throw new InvalidDataException("Проект использует SDK или встроенные библиотеки RNAssistant. Сначала нужен перенос зависимостей.");
                prepared.Add(new PreparedFile { Path = file.Path, Content = file.Content, Sha256 = TextPatternEngine.Sha256(file.Content) });
            }
            return prepared;
        }

        // Same manifest as the HTML renderer; no second handwritten library catalog.
        private sealed class VendorManifest { public List<VendorPackage> Packages { get; set; } }
        private sealed class VendorPackage { public HtmlVendor HtmlWorkspace { get; set; } }
        private sealed class HtmlVendor { public string Global { get; set; } public string Activation { get; set; } }
        private static Regex[] ReadVendorReferences()
        {
            using (var stream = typeof(HtmlProjectImportService).Assembly.GetManifestResourceStream("RNAssistant.HtmlVendorManifest"))
            {
                if (stream == null) throw new InvalidDataException("Каталог зависимостей HTML недоступен.");
                using (var reader = new StreamReader(stream, Utf8))
                {
                    var manifest = JsonConvert.DeserializeObject<VendorManifest>(reader.ReadToEnd());
                    if (manifest?.Packages == null) throw new InvalidDataException("Каталог зависимостей HTML повреждён.");
                    return manifest.Packages.Where(item => item.HtmlWorkspace != null).Select(item => new Regex(
                        @"\b" + Regex.Escape(item.HtmlWorkspace.Activation ?? item.HtmlWorkspace.Global).Replace(@"\.", @"\s*\.\s*") + @"\b",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300))).ToArray();
                }
            }
        }
    }
}
