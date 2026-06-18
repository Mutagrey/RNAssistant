using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed class VbaBackupStore
    {
        private readonly AppDataPaths _paths;
        private readonly JsonFileStore _json;

        public VbaBackupStore(AppDataPaths paths)
        {
            _paths = paths;
            _json = new JsonFileStore();
        }

        public VbaModuleBackup Save(string host, string documentKey, string documentTitle, string moduleName, string componentType, string code)
        {
            var backup = new VbaModuleBackup
            {
                BackupId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Host = host ?? string.Empty,
                DocumentKey = documentKey ?? string.Empty,
                DocumentTitle = documentTitle ?? string.Empty,
                ModuleName = moduleName ?? string.Empty,
                ComponentType = componentType ?? string.Empty,
                Code = code ?? string.Empty,
                CreatedUtc = DateTime.UtcNow
            };

            _json.Save(Path.Combine(DocumentDirectory(host, documentKey), backup.BackupId + ".json"), backup);
            return backup;
        }

        public List<VbaModuleBackup> List(string host, string documentKey)
        {
            var directory = DocumentDirectory(host, documentKey);
            if (!Directory.Exists(directory))
            {
                return new List<VbaModuleBackup>();
            }

            return Directory.GetFiles(directory, "*.json")
                .Select(path => _json.Load(path, (VbaModuleBackup)null))
                .Where(backup => backup != null)
                .OrderByDescending(backup => backup.CreatedUtc)
                .ToList();
        }

        public VbaModuleBackup Find(string host, string documentKey, string backupId, string moduleName)
        {
            var backups = List(host, documentKey);
            if (!string.IsNullOrWhiteSpace(backupId))
            {
                return backups.FirstOrDefault(b => string.Equals(b.BackupId, backupId, StringComparison.OrdinalIgnoreCase));
            }

            return backups.FirstOrDefault(b => string.Equals(b.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
        }

        private string DocumentDirectory(string host, string documentKey)
        {
            return Path.Combine(_paths.VbaBackupDirectory, AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)));
        }
    }
}
