using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    // Explicit inventory of retained registrations. This reader never changes
    // either registry; PluginStore owns the separate native import command.
    public sealed class LegacyPluginRecoveryService
    {
        private static readonly ResourceAuthorityScopeId Scope = new ResourceAuthorityScopeId("plugins", "local");
        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;
        public LegacyPluginRecoveryService(AppDataPaths paths, Func<StorageProtector> protectionProvider)
        { _paths = paths ?? throw new ArgumentNullException(nameof(paths)); _blobs = new ChatBlobStore(paths, protectionProvider); }

        public LegacyPluginRecoveryPage List(int offset = 0, int limit = 25, CancellationToken token = default(CancellationToken))
        {
            LegacyRecoveryFiles.RequirePage(offset, limit);
            using (var read = ReadAuthority(_paths, token))
            {
            var authority = read.Owner;
            var snapshot = authority.Capture(Scope);
            var heads = snapshot.Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                head.Revision.Uri.Contains("/registration/")).OrderBy(head => head.Revision.Uri, StringComparer.Ordinal).ToArray();
            if (offset > heads.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            var items = heads.Skip(offset).Take(limit).Select(head => ReadItem(authority, snapshot, head.Revision, token)).ToArray();
            return new LegacyPluginRecoveryPage { RootPath = _paths.ResourceAuthorityDirectory, Total = heads.Length, Items = items,
                NextOffset = offset + items.Length < heads.Length ? (int?)(offset + items.Length) : null };
            }
        }

        public LegacyPluginRecoveryItem ResolveSource(string pluginId, CancellationToken token = default(CancellationToken))
        {
            if (!PluginPackageFiles.ValidId(pluginId)) throw new ArgumentException("Выберите плагин из старого каталога.", nameof(pluginId));
            using (var read = ReadAuthority(_paths, token))
            {
            var authority = read.Owner;
            var snapshot = authority.Capture(Scope);
            var head = snapshot.GetHead(new ResourceIdentity(ResourceUri.Create("plugin", pluginId, "registration", "current")));
            if (head?.Knowledge != HeadKnowledge.Known) throw new InvalidOperationException("Старая регистрация плагина не найдена.");
            var item = ReadItem(authority, snapshot, head.Revision, token);
            if (!item.CanConnect) throw new InvalidOperationException(item.Error ?? "Исходники плагина недоступны.");
            return item;
            }
        }

        internal sealed class AuthorityRead : IDisposable
        {
            internal ResourceAuthorityStore Owner;
            internal readonly List<FileStream> Handles = new List<FileStream>();
            public void Dispose() { foreach (var stream in Handles) stream.Dispose(); }
        }

        internal static AuthorityRead ReadAuthority(AppDataPaths paths, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LegacyRecoveryFiles.RequireRegular(paths.ResourceAuthorityDirectory);
            LegacyRecoveryFiles.RequireRegular(paths.ChatBlobDirectory);
            var prefix = RNAssistant.Core.Tools.TextPatternEngine.Sha256(Scope.ToString());
            var read = new AuthorityRead();
            try
            {
                foreach (var suffix in new[] { ".authority.jsonl", ".revisions.jsonl", ".views.jsonl" })
                {
                    var path = Path.Combine(paths.ResourceAuthorityDirectory, prefix + suffix);
                    if (!File.Exists(path)) continue;
                    LegacyRecoveryFiles.RequireRegular(path);
                    read.Handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    LegacyRecoveryFiles.CheckJournal(path, token);
                }
                read.Owner = new ResourceAuthorityStore(paths); return read;
            }
            catch { read.Dispose(); throw; }
        }

        internal static string ImportRevision(ResourceAuthoritySnapshot snapshot, string pluginId)
        {
            var prefix = ResourceUri.Create("plugin", pluginId) + "/";
            return RNAssistant.Core.Tools.TextPatternEngine.Sha256(JsonConvert.SerializeObject(snapshot.Heads.Values
                .Where(head => head.Identity.Uri.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(head => head.Identity.Uri, StringComparer.Ordinal)));
        }

        private LegacyPluginRecoveryItem ReadItem(ResourceAuthorityStore authority, ResourceAuthoritySnapshot snapshot,
            ResourceRef reference, CancellationToken token)
        {
            var item = new LegacyPluginRecoveryItem { Status = "readable" };
            try
            {
                token.ThrowIfCancellationRequested();
                var address = ResourceUri.Parse(reference.Uri);
                if (address.Provider != "plugin" || address.Segments.Count != 3 || address.Segments[1] != "registration" || address.Segments[2] != "current")
                    throw new InvalidDataException("Старая регистрация имеет неверный адрес.");
                item.PluginId = address.Segments[0];
                var view = authority.GetView(Scope, reference, "plugin-registration");
                if (view?.Payload == null || view.Payload.ByteLength > 1024 * 1024)
                    throw new InvalidDataException("Метаданные старой регистрации отсутствуют или превышают 1 MiB.");
                LegacyRecoveryFiles.CheckFile(_blobs.PathFor(view.Payload.Sha256), 2 * 1024 * 1024);
                var json = _blobs.ReadText(view.Payload.ToBlobReference());
                if (json == null) throw new InvalidDataException("Метаданные не прочитаны: проверьте ключ защиты и сохранность CAS.");
                var registration = JsonConvert.DeserializeObject<PluginRegistration>(json);
                if (registration == null || registration.PluginId != item.PluginId || string.IsNullOrWhiteSpace(registration.SourceWorkspaceId))
                    throw new InvalidDataException("Идентичность старой регистрации не подтверждена.");
                item.Name = registration.Appearance?.Name ?? registration.Name;
                item.SourceRoot = registration.SourceRoot; item.SourceWorkspaceId = registration.SourceWorkspaceId;
                if (string.IsNullOrWhiteSpace(item.SourceRoot) || !Path.IsPathRooted(item.SourceRoot))
                    throw new LegacyRecoveryReadException("sourceChanged", "Старая регистрация не содержит полного пути исходников.");
                item.RegistrationRevision = ImportRevision(snapshot, item.PluginId);
                // Publication bytes can be recovered even while the editable source is offline.
                // The explicit import validates the complete retained graph before publishing.
                item.CanImport = true;
                if (!Directory.Exists(item.SourceRoot)) throw new LegacyRecoveryReadException("missingSource", "Папка исходников отсутствует по сохранённому пути.");
                LegacyRecoveryFiles.RequireRegular(item.SourceRoot);
                var manifestPath = Path.Combine(item.SourceRoot, "plugin.json");
                var workspacePath = Path.Combine(item.SourceRoot, ".rnassistant", "workspace.json");
                var manifest = PluginPackageFiles.ParseManifest(LegacyRecoveryFiles.ReadText(manifestPath));
                var workspace = JsonConvert.DeserializeObject<WorkspaceManifest>(LegacyRecoveryFiles.ReadText(workspacePath));
                if (manifest.Id != item.PluginId || workspace?.WorkspaceId != item.SourceWorkspaceId || workspace.SchemaVersion != 1)
                    throw new LegacyRecoveryReadException("sourceChanged", "plugin.json или workspace ID больше не соответствует старой регистрации.");
                item.CanConnect = true;
            }
            catch (Exception error) when (LegacyRecoveryFiles.ReadFailure(error))
            { item.Status = LegacyRecoveryFiles.Status(error); item.Error = LegacyRecoveryFiles.Error(error); }
            return item;
        }
    }

    internal sealed class LegacyRecoveryReadException : IOException
    {
        internal readonly string Code;
        internal LegacyRecoveryReadException(string code, string message) : base(message) { Code = code; }
    }

    internal static class LegacyRecoveryFiles
    {
        internal static void RequirePage(int offset, int limit)
        { if (offset < 0 || limit < 1 || limit > 50) throw new ArgumentOutOfRangeException("Use a nonnegative offset and a limit from 1 to 50."); }
        internal static void RequireRegular(string path)
        {
            var current = Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LegacyRecoveryReadException("linkedPath", "Архив или исходники содержат ссылку/reparse point: " + current);
                current = Path.GetDirectoryName(current);
            }
        }
        internal static void CheckFile(string path, long maximumBytes)
        {
            RequireRegular(path);
            if (new FileInfo(path).Length > maximumBytes)
                throw new LegacyRecoveryReadException("tooLarge", "Файл превышает предел явного чтения архива: " + maximumBytes + " байт.");
        }
        internal static void CheckJournal(string path, CancellationToken token)
        {
            CheckFile(path, 64L * 1024 * 1024);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[8192]; var line = 0; long total = 0; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    total += count;
                    if (total > 64L * 1024 * 1024) throw new LegacyRecoveryReadException("tooLarge", "Архив превышает 64 MiB.");
                    for (var index = 0; index < count; index++)
                    {
                        line = buffer[index] == 10 ? 0 : line + 1;
                        if (line > 8 * 1024 * 1024) throw new LegacyRecoveryReadException("tooLarge", "Запись архива превышает 8 MiB; файл сохранён без изменений.");
                    }
                }
                if (line != 0) throw new LegacyRecoveryReadException("incomplete", "У журнала незавершённый хвост; чтение не обрезает файл.");
            }
        }
        internal static string ReadText(string path)
        {
            CheckFile(path, 1024 * 1024);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
            { if (stream.Length > 1024 * 1024) throw new InvalidDataException("Source metadata exceeds 1 MiB."); return reader.ReadToEnd(); }
        }
        internal static bool ReadFailure(Exception error)
        { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is JsonException || error is FormatException ||
            error is InvalidOperationException || error is CryptographicException || error is ArgumentException; }
        internal static string Status(Exception error) { return (error as LegacyRecoveryReadException)?.Code ?? "unreadable"; }
        internal static string Error(Exception error) { var text = error.Message; return text.Length <= 600 ? text : text.Substring(0, 600); }
    }
}
