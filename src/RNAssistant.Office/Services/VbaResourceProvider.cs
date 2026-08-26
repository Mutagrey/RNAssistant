using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class VbaResourceProvider : ILiveOfficeResourceProvider
    {
        public const string ProviderName = "vba";
        public const string ProjectKind = "vba-project";
        public const string ComponentKind = "vba-component";
        public const string BackupKind = "vba-backup";
        private const int MaximumItems = 50;

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly IVbaResourceSource _source;
        private readonly VbaJournalStore _journal;
        private readonly LiveOfficeResourceScope _scope;

        public VbaResourceProvider(
            IOfficeApplicationAdapter adapter,
            IVbaResourceSource source,
            VbaJournalStore journal)
        {
            _adapter = adapter ?? throw new ArgumentNullException("adapter");
            _source = source ?? throw new ArgumentNullException("source");
            _journal = journal;
            _scope = new LiveOfficeResourceScope(adapter);
        }

        public string Id { get { return ProviderName; } }

        public static bool SupportsHost(string host)
        {
            return string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase);
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            return _scope.Read(session, delegate
            {
                limit = Math.Max(1, Math.Min(MaximumItems, limit <= 0 ? 20 : limit));
                var offset = ParseCursor(cursor);
                List<ResourceDescriptor> items;
                if (string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(kind, ProjectKind, StringComparison.OrdinalIgnoreCase))
                {
                    items = new List<ResourceDescriptor> { DescribeProject(session) };
                }
                else if (string.Equals(kind, ComponentKind, StringComparison.OrdinalIgnoreCase))
                {
                    items = LoadModules().Select(module => DescribeComponent(session, module, null)).ToList();
                }
                else if (string.Equals(kind, BackupKind, StringComparison.OrdinalIgnoreCase))
                {
                    items = LoadBackups().Select(backup => DescribeBackup(session, backup)).ToList();
                }
                else
                {
                    items = new List<ResourceDescriptor>();
                }
                var selected = items.Skip(offset).Take(limit).ToList();
                var next = offset + selected.Count;
                return new ResourceListPage
                {
                    Items = selected,
                    Total = items.Count,
                    Cursor = offset.ToString(),
                    NextCursor = next < items.Count ? next.ToString() : null,
                    Truncated = next < items.Count
                };
            });
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            return _scope.Read(session, delegate
            {
                VbaResourceTarget target;
                if (!TryResolveTarget(session, resourceUri, out target))
                {
                    throw new KeyNotFoundException("VBA resource was not found: " + resourceUri);
                }
                if (target.Project) return DescribeProject(session);
                if (target.Module != null) return DescribeComponent(session, target.Module, null);
                return DescribeBackup(session, target.Backup);
            });
        }

        private ResourceDescriptor DescribeProject(ChatSession session)
        {
            var descriptor = new ResourceDescriptor
            {
                Reference = new ResourceRef(ProjectUri(session)),
                Provider = ProviderName,
                Kind = ProjectKind,
                Title = (_adapter.DocumentTitle ?? "Office document") + " VBA project",
                MimeType = "application/json",
                Mutable = true
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Structure);
            descriptor.Metadata["host"] = _adapter.HostName ?? string.Empty;
            descriptor.Metadata["live"] = "true";
            descriptor.Metadata["childKinds"] = ComponentKind + "," + BackupKind;
            descriptor.Metadata["childDiscovery"] = "List this provider with one exact child kind.";
            return descriptor;
        }

        private ResourceDescriptor DescribeComponent(ChatSession session, VbaResourceModule module, string codeSha256)
        {
            var descriptor = new ResourceDescriptor
            {
                Reference = new ResourceRef(ComponentUri(session, module.Name), codeSha256),
                Provider = ProviderName,
                Kind = ComponentKind,
                Title = module.Name,
                MimeType = "text/x-vba; charset=utf-8",
                Mutable = true,
                ContentSha256 = codeSha256,
                Parent = new ResourceRef(ProjectUri(session))
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Source);
            descriptor.Metadata["name"] = module.Name;
            descriptor.Metadata["componentType"] = module.ComponentType;
            descriptor.Metadata["lineCount"] = module.LineCount.ToString();
            descriptor.Metadata["live"] = "true";
            return descriptor;
        }

        private ResourceDescriptor DescribeBackup(ChatSession session, VbaModuleBackup backup)
        {
            var descriptor = new ResourceDescriptor
            {
                Reference = new ResourceRef(BackupUri(session, backup.BackupId), backup.CodeSha256),
                Provider = ProviderName,
                Kind = BackupKind,
                Title = backup.ModuleName + " backup " + backup.CreatedUtc.ToString("u"),
                MimeType = "text/x-vba; charset=utf-8",
                Mutable = false,
                ByteLength = backup.CodeByteLength,
                CreatedUtc = backup.CreatedUtc,
                ContentSha256 = backup.CodeSha256,
                Parent = new ResourceRef(ProjectUri(session))
            };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Source);
            descriptor.Metadata["backupId"] = backup.BackupId ?? string.Empty;
            descriptor.Metadata["moduleName"] = backup.ModuleName ?? string.Empty;
            descriptor.Metadata["componentType"] = backup.ComponentType ?? string.Empty;
            descriptor.Metadata["mutationId"] = backup.MutationId ?? string.Empty;
            return descriptor;
        }

        private List<VbaResourceModule> LoadModules()
        {
            var result = _source.ListResourceModules();
            EnsureSuccess(result, "VBA project metadata could not be read.");
            try
            {
                return (JObject.Parse(result.DataJson ?? "{}")["modules"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(module => new VbaResourceModule
                    {
                        Name = ((string)module["name"] ?? string.Empty).Trim(),
                        ComponentType = (string)module["type"] ?? string.Empty,
                        LineCount = Math.Max(0, (int?)module["lineCount"] ?? 0)
                    })
                    .Where(module => module.Name.Length > 0)
                    .GroupBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (JsonException ex)
            {
                throw new ResourceRequestException(
                    "VBA project metadata is invalid: " + ex.Message,
                    "vba_read_invalid",
                    true);
            }
        }

        private List<VbaModuleBackup> LoadBackups()
        {
            if (_journal == null) return new List<VbaModuleBackup>();
            try
            {
                return _journal.List(_adapter.HostName, _adapter.DocumentKey);
            }
            catch (VbaJournalException ex)
            {
                throw new ResourceRequestException(ex.Message, "vba_backup_unavailable", false);
            }
        }

        private bool TryResolveTarget(ChatSession session, string resourceUri, out VbaResourceTarget target)
        {
            target = null;
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address) ||
                !string.Equals(address.Provider, ProviderName, StringComparison.Ordinal) ||
                address.Segments.Count < 2 ||
                !string.Equals(address.Segments[0], _scope.DocumentToken(session), StringComparison.Ordinal))
            {
                return false;
            }
            if (address.Segments.Count == 2 && string.Equals(address.Segments[1], "project", StringComparison.Ordinal))
            {
                target = new VbaResourceTarget { Project = true };
                return true;
            }
            if (address.Segments.Count != 3) return false;
            if (string.Equals(address.Segments[1], "component", StringComparison.Ordinal))
            {
                var module = LoadModules().FirstOrDefault(item => string.Equals(
                    ComponentKey(item.Name),
                    address.Segments[2],
                    StringComparison.Ordinal));
                if (module == null) return false;
                target = new VbaResourceTarget { Module = module };
                return true;
            }
            if (string.Equals(address.Segments[1], "backup", StringComparison.Ordinal))
            {
                var backup = LoadBackups().FirstOrDefault(item => string.Equals(
                    item.BackupId,
                    address.Segments[2],
                    StringComparison.OrdinalIgnoreCase));
                if (backup == null) return false;
                target = new VbaResourceTarget { Backup = backup };
                return true;
            }
            return false;
        }

        private string ProjectUri(ChatSession session)
        {
            return ResourceUri.Create(ProviderName, _scope.DocumentToken(session), "project");
        }

        private string ComponentUri(ChatSession session, string moduleName)
        {
            return ResourceUri.Create(
                ProviderName,
                _scope.DocumentToken(session),
                "component",
                ComponentKey(moduleName));
        }

        private string BackupUri(ChatSession session, string backupId)
        {
            return ResourceUri.Create(
                ProviderName,
                _scope.DocumentToken(session),
                "backup",
                backupId);
        }

        private static string ComponentKey(string moduleName)
        {
            return TextPatternEngine.Sha256((moduleName ?? string.Empty).Trim().ToLowerInvariant());
        }

        private static void EnsureSuccess(ToolResult result, string fallback)
        {
            if (result != null && result.Success) return;
            throw new ResourceRequestException(
                result == null || string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message,
                result == null || string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? "vba_read_failed"
                    : result.ErrorCode,
                result != null && result.Retryable == true);
        }

        private static int ParseCursor(string cursor)
        {
            int offset;
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (!int.TryParse(cursor, out offset) || offset < 0)
            {
                throw new ResourceRequestException(
                    "VBA resource cursor is invalid.",
                    "resource_cursor_invalid",
                    true);
            }
            return offset;
        }

        private sealed class VbaResourceTarget
        {
            public bool Project { get; set; }
            public VbaResourceModule Module { get; set; }
            public VbaModuleBackup Backup { get; set; }
        }

        private sealed class VbaResourceModule
        {
            public string Name { get; set; }
            public string ComponentType { get; set; }
            public int LineCount { get; set; }
        }
    }
}
