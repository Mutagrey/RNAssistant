using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    // Workspace association only. ResourceAuthorityStore owns content heads and ChatStore owns sessions.
    public sealed class WorkspaceStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly AppDataPaths _paths;

        private sealed class WorkspaceAssociation
        {
            public string WorkspaceId { get; set; }
            public string RootPath { get; set; }
            public string Name { get; set; }
            public bool ManifestStored { get; set; }
        }

        public WorkspaceStore(AppDataPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        }

        public WorkspaceDescriptor Open(string rootPath, bool createIfMissing = true, bool permitManifestWrite = true)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A workspace path is required.", nameof(rootPath));
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
            {
                if (!createIfMissing) throw new DirectoryNotFoundException(root);
                Directory.CreateDirectory(root);
            }
            if (StorageFileSystem.IsReparsePoint(root))
                throw new IOException("A workspace root cannot be a symlink or reparse point.");

            var control = Path.Combine(root, ".rnassistant");
            var manifestPath = Path.Combine(control, "workspace.json");
            if (Directory.Exists(control) && StorageFileSystem.IsReparsePoint(control))
                throw new IOException("Workspace control directory cannot be a link.");

            using (StorageFileSystem.AcquireWriteLock(Path.Combine(_paths.WorkspaceDirectory, "catalog.lck")))
            {
                var byPath = AssociationPath(root);
                var previous = ReadAssociation(byPath);
                WorkspaceManifest manifest = null;
                if (File.Exists(manifestPath))
                {
                    if (StorageFileSystem.IsReparsePoint(manifestPath))
                        throw new IOException("Workspace manifest cannot be a link.");
                    manifest = JsonConvert.DeserializeObject<WorkspaceManifest>(File.ReadAllText(manifestPath, Utf8));
                    Validate(manifest);
                }
                if (manifest != null && previous != null && previous.WorkspaceId != manifest.WorkspaceId)
                    throw new InvalidDataException("Workspace manifest conflicts with the saved path association.");

                var id = manifest?.WorkspaceId ?? previous?.WorkspaceId ?? NewId();
                var name = manifest?.Name ?? previous?.Name ?? new DirectoryInfo(root).Name;
                if (string.IsNullOrWhiteSpace(name)) name = "Workspace";
                var saved = ReadAssociation(IdPath(id));
                if (saved != null && !SamePath(saved.RootPath, root) && Directory.Exists(saved.RootPath))
                    throw new InvalidDataException("Workspace ID is already connected to another existing root. Resolve move or copy explicitly.");
                if (saved != null && !SamePath(saved.RootPath, root))
                    WorkspaceFileService.RelocateLocators(_paths, saved.RootPath, root);

                if (manifest == null && permitManifestWrite)
                {
                    StorageFileSystem.EnsureRegularDirectory(control);
                    manifest = new WorkspaceManifest { SchemaVersion = 1, WorkspaceId = id, Name = name, Root = "." };
                    using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, Utf8)) writer.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
                }

                var association = new WorkspaceAssociation
                {
                    WorkspaceId = id, RootPath = root, Name = name, ManifestStored = manifest != null
                };
                StorageFileSystem.WriteAllTextAtomic(IdPath(id), JsonConvert.SerializeObject(association), Utf8);
                StorageFileSystem.WriteAllTextAtomic(byPath, JsonConvert.SerializeObject(association), Utf8);
                return new WorkspaceDescriptor
                {
                    WorkspaceId = id, Name = name, RootPath = root, ReadOnly = !permitManifestWrite || manifest == null,
                    Mounts = new[] { new WorkspaceMountDescriptor
                    { Id = "root", Kind = "directory", RootPath = root, ReadOnly = !permitManifestWrite || manifest == null } }
                };
            }
        }

        public ChatSession CreateSession(ChatStore chats, WorkspaceDescriptor workspace, string title)
        {
            if (chats == null) throw new ArgumentNullException(nameof(chats));
            ValidateDescriptor(workspace);
            // The legacy storage partition fields are an internal routing adapter until M7.
            var session = chats.CreateTransient("Workspace", workspace.WorkspaceId, workspace.Name, title);
            session.WorkspaceId = workspace.WorkspaceId;
            session.DocumentAuthorityId = null;
            session.DocumentPath = null;
            chats.Save(session);
            return session;
        }

        public ChatSession LoadSession(ChatStore chats, WorkspaceDescriptor workspace, string sessionId)
        {
            if (chats == null) throw new ArgumentNullException(nameof(chats));
            ValidateDescriptor(workspace);
            var session = chats.Load("Workspace", workspace.WorkspaceId, sessionId);
            if (session != null && session.WorkspaceId != workspace.WorkspaceId)
                throw new InvalidDataException("The session is not associated with this workspace.");
            return session;
        }

        public ChatSessionHeader[] ListSessions(ChatStore chats, WorkspaceDescriptor workspace)
        {
            if (chats == null) throw new ArgumentNullException(nameof(chats));
            ValidateDescriptor(workspace);
            return chats.ListHeaders("Workspace", workspace.WorkspaceId, workspace.Name)
                .Where(item => item.WorkspaceId == workspace.WorkspaceId).ToArray();
        }

        private static void ValidateDescriptor(WorkspaceDescriptor value)
        {
            if (value == null || !ValidId(value.WorkspaceId) || string.IsNullOrWhiteSpace(value.RootPath))
                throw new ArgumentException("An opened workspace is required.", nameof(value));
        }

        private string IdPath(string id) { return Path.Combine(_paths.WorkspaceDirectory, id + ".json"); }
        private string AssociationPath(string root)
        {
            return Path.Combine(_paths.WorkspaceDirectory, "path-" + RNAssistant.Core.Tools.TextPatternEngine.Sha256(root) + ".json");
        }

        private static WorkspaceAssociation ReadAssociation(string path)
        {
            if (!File.Exists(path)) return null;
            if (StorageFileSystem.IsReparsePoint(path)) throw new IOException("Workspace association cannot be a link.");
            var value = JsonConvert.DeserializeObject<WorkspaceAssociation>(File.ReadAllText(path, Utf8));
            if (value == null || !ValidId(value.WorkspaceId) || string.IsNullOrWhiteSpace(value.RootPath))
                throw new InvalidDataException("Workspace association is invalid.");
            return value;
        }

        private static void Validate(WorkspaceManifest value)
        {
            if (value == null || value.SchemaVersion != 1 || !ValidId(value.WorkspaceId) ||
                string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 200 || value.Root != ".")
                throw new InvalidDataException("Workspace manifest is invalid or unsupported.");
        }

        private static bool ValidId(string id)
        {
            return id != null && id.Length == 35 && id.StartsWith("ws_", StringComparison.Ordinal) &&
                id.Skip(3).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }

        private static string NewId() { return "ws_" + Guid.NewGuid().ToString("N"); }
        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}
