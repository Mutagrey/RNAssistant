using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    public sealed class VbaJournalException : IOException
    {
        public VbaJournalException(string message)
            : base(message)
        {
        }

        public VbaJournalException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Document-scoped append-only source of truth for VBA rollback snapshots and mutations.
    /// Source bodies live in the shared SHA-256 CAS; backup lists and mutation state are projections.
    /// </summary>
    public sealed partial class VbaJournalStore
    {
        private const string JournalFileName = "mutations.events.jsonl";
        private const int MaxMutationPageSize = 200;
        private const int MaxMutationSearchChars = 512;
        private const string MutationCursorPrefix = "vba:";
        private static readonly object PersistenceSync = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> JournalEventProperties = new HashSet<string>(
            new[]
            {
                "SchemaVersion", "Host", "DocumentKey", "Sequence", "EventId", "CreatedUtc", "Type",
                "MutationId", "RunId", "TurnId", "StepId", "ToolCallId", "PreviousHash",
                "HashAlgorithm", "ProtectionKeyId", "Hash", "Data", "EncryptedData"
            },
            StringComparer.Ordinal);

        private readonly AppDataPaths _paths;
        private readonly ChatBlobStore _blobs;
        private readonly Func<StorageProtector> _protectionProvider;

        public VbaJournalStore(AppDataPaths paths)
            : this(paths, null)
        {
        }

        public VbaJournalStore(AppDataPaths paths, Func<StorageProtector> protectionProvider)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _protectionProvider = protectionProvider ?? (() => StorageProtector.None);
            _blobs = new ChatBlobStore(paths, _protectionProvider);
        }

        public VbaModuleBackup Save(string host, string documentKey, string documentTitle, string moduleName, string componentType, string code)
        {
            var reference = _blobs.StoreText(code ?? string.Empty, "text/x-vba; charset=utf-8");
            var backup = new VbaModuleBackup
            {
                BackupId = NewId("backup"),
                Host = host ?? string.Empty,
                DocumentKey = documentKey ?? string.Empty,
                DocumentTitle = documentTitle ?? string.Empty,
                ModuleName = moduleName ?? string.Empty,
                ComponentType = componentType ?? string.Empty,
                CodeSha256 = reference.Sha256,
                CodeByteLength = reference.ByteLength,
                CodeReference = reference,
                Code = code ?? string.Empty,
                CreatedUtc = DateTime.UtcNow
            };
            Append(host, documentKey, VbaJournalEventTypes.BackupCreated, null, null, null, null, null, JObject.FromObject(backup));
            return backup;
        }

        public VbaMutationPreparation PrepareMutation(
            VbaMutationPreparation preparation,
            string beforeCode,
            string intendedAfterCode)
        {
            if (preparation == null) throw new ArgumentNullException("preparation");
            if (string.IsNullOrWhiteSpace(preparation.Host) || string.IsNullOrWhiteSpace(preparation.DocumentKey) ||
                string.IsNullOrWhiteSpace(preparation.ModuleName) || string.IsNullOrWhiteSpace(preparation.Operation))
            {
                throw new VbaJournalException("VBA mutation identity, module, and operation are required.");
            }

            preparation.MutationId = NewId("mutation");
            preparation.CreatedUtc = preparation.CreatedUtc == default(DateTime)
                ? DateTime.UtcNow
                : preparation.CreatedUtc.ToUniversalTime();
            if (preparation.BeforeExists)
            {
                preparation.BeforeCodeReference = _blobs.StoreText(beforeCode ?? string.Empty, "text/x-vba; charset=utf-8");
                preparation.BeforeCodeSha256 = preparation.BeforeCodeReference.Sha256;
                preparation.BackupId = string.IsNullOrWhiteSpace(preparation.BackupId) ? NewId("backup") : preparation.BackupId;
            }
            else
            {
                preparation.BeforeCodeReference = null;
                preparation.BeforeCodeSha256 = null;
                preparation.BeforeComparableCodeSha256 = null;
                preparation.BackupId = null;
            }
            if (preparation.IntendedAfterExists)
            {
                preparation.IntendedAfterCodeReference = _blobs.StoreText(intendedAfterCode ?? string.Empty, "text/x-vba; charset=utf-8");
                preparation.IntendedAfterCodeSha256 = preparation.IntendedAfterCodeReference.Sha256;
            }
            else
            {
                preparation.IntendedAfterCodeReference = null;
                preparation.IntendedAfterCodeSha256 = null;
                preparation.IntendedAfterComparableCodeSha256 = null;
            }

            Append(
                preparation.Host,
                preparation.DocumentKey,
                VbaJournalEventTypes.MutationPrepared,
                preparation.MutationId,
                preparation.RunId,
                preparation.TurnId,
                preparation.StepId,
                preparation.ToolCallId,
                JObject.FromObject(preparation));
            return preparation;
        }

        public VbaMutationTerminal CompleteMutation(
            string host,
            string documentKey,
            string mutationId,
            string status,
            bool? actualExists,
            string actualCodeSha256,
            string actualComparableCodeSha256,
            string errorCode,
            string message)
        {
            if (!VbaMutationStatuses.IsTerminal(status))
            {
                throw new ArgumentException("Unsupported VBA mutation terminal status: " + status, "status");
            }
            lock (PersistenceSync)
            {
                using (AcquireLock(host, documentKey))
                {
                    var path = JournalPath(host, documentKey);
                    var log = ReadEventLog(path, host, documentKey);
                    var records = ProjectMutations(log == null ? null : log.Events);
                    var record = records.FirstOrDefault(item => item.Prepared != null &&
                        string.Equals(item.Prepared.MutationId, mutationId, StringComparison.OrdinalIgnoreCase));
                    if (record == null)
                    {
                        throw new VbaJournalException("VBA mutation preparation was not found: " + mutationId + ".");
                    }
                    if (record.Terminal != null) return record.Terminal;

                    var terminal = new VbaMutationTerminal
                    {
                        MutationId = mutationId,
                        Status = status,
                        ActualExists = actualExists,
                        ActualCodeSha256 = actualCodeSha256,
                        ActualComparableCodeSha256 = actualComparableCodeSha256,
                        ErrorCode = errorCode,
                        Message = message,
                        CreatedUtc = DateTime.UtcNow
                    };
                    AppendLocked(
                        path,
                        host,
                        documentKey,
                        log,
                        VbaJournalEventTypes.MutationTerminal,
                        mutationId,
                        record.Prepared.RunId,
                        record.Prepared.TurnId,
                        record.Prepared.StepId,
                        record.Prepared.ToolCallId,
                        JObject.FromObject(terminal));
                    return terminal;
                }
            }
        }

        public VbaPackageMutationPreparation PreparePackageMutation(VbaPackageMutationPreparation preparation)
        {
            if (preparation == null) throw new ArgumentNullException("preparation");
            if (string.IsNullOrWhiteSpace(preparation.Host) || string.IsNullOrWhiteSpace(preparation.DocumentKey) ||
                string.IsNullOrWhiteSpace(preparation.PackageId) || string.IsNullOrWhiteSpace(preparation.Operation) ||
                preparation.Components == null || preparation.Components.Count == 0)
            {
                throw new VbaJournalException("VBA package mutation identity, components, and operation are required.");
            }
            var duplicate = preparation.Components
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ModuleName))
                .GroupBy(item => item.ModuleName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (preparation.Components.Any(item => item == null || string.IsNullOrWhiteSpace(item.ModuleName) ||
                item.BeforeExists && string.IsNullOrWhiteSpace(item.BeforeComponentType) ||
                item.IntendedAfterExists && string.IsNullOrWhiteSpace(item.IntendedAfterComponentType)) || duplicate != null)
            {
                throw new VbaJournalException("VBA package mutation components must have unique names and explicit types.");
            }

            preparation.MutationId = NewId("package_mutation");
            preparation.CreatedUtc = preparation.CreatedUtc == default(DateTime)
                ? DateTime.UtcNow
                : preparation.CreatedUtc.ToUniversalTime();
            foreach (var component in preparation.Components)
            {
                if (component.BeforeExists)
                {
                    component.BeforeCodeReference = _blobs.StoreText(component.BeforeCode ?? string.Empty, "text/x-vba; charset=utf-8");
                    component.BeforeCodeSha256 = VbaToolManifestParser.CodeSha256(component.BeforeCode);
                    component.BackupId = preparation.RetainBackups
                        ? string.IsNullOrWhiteSpace(component.BackupId) ? NewId("backup") : component.BackupId
                        : null;
                }
                else
                {
                    component.BeforeComponentType = null;
                    component.BeforeCodeReference = null;
                    component.BeforeCodeSha256 = null;
                    component.BackupId = null;
                }
                if (component.IntendedAfterExists)
                {
                    component.IntendedAfterCodeReference = _blobs.StoreText(component.IntendedAfterCode ?? string.Empty, "text/x-vba; charset=utf-8");
                    component.IntendedAfterCodeSha256 = VbaToolManifestParser.CodeSha256(component.IntendedAfterCode);
                }
                else
                {
                    component.IntendedAfterComponentType = null;
                    component.IntendedAfterCodeReference = null;
                    component.IntendedAfterCodeSha256 = null;
                }
            }

            Append(
                preparation.Host,
                preparation.DocumentKey,
                VbaJournalEventTypes.PackageMutationPrepared,
                preparation.MutationId,
                preparation.RunId,
                preparation.TurnId,
                preparation.StepId,
                preparation.ToolCallId,
                JObject.FromObject(preparation));
            return preparation;
        }

        public VbaPackageMutationTerminal CompletePackageMutation(
            string host,
            string documentKey,
            string mutationId,
            string status,
            IEnumerable<VbaPackageMutationComponentAssessment> components,
            string errorCode,
            string message)
        {
            if (!VbaMutationStatuses.IsTerminal(status))
            {
                throw new ArgumentException("Unsupported VBA package mutation terminal status: " + status, "status");
            }
            lock (PersistenceSync)
            {
                using (AcquireLock(host, documentKey))
                {
                    var path = JournalPath(host, documentKey);
                    var log = ReadEventLog(path, host, documentKey);
                    var records = ProjectPackageMutations(log == null ? null : log.Events);
                    var record = records.FirstOrDefault(item => item.Prepared != null &&
                        string.Equals(item.Prepared.MutationId, mutationId, StringComparison.OrdinalIgnoreCase));
                    if (record == null)
                    {
                        throw new VbaJournalException("VBA package mutation preparation was not found: " + mutationId + ".");
                    }
                    if (record.Terminal != null) return record.Terminal;
                    var componentAssessments = (components ?? new VbaPackageMutationComponentAssessment[0]).ToList();
                    if (componentAssessments.Count != record.Prepared.Components.Count ||
                        componentAssessments.Any(item => item == null || string.IsNullOrWhiteSpace(item.ModuleName)) ||
                        componentAssessments.GroupBy(item => item.ModuleName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                        componentAssessments.Any(item => !record.Prepared.Components.Any(component =>
                            string.Equals(component.ModuleName, item.ModuleName, StringComparison.OrdinalIgnoreCase))))
                    {
                        throw new VbaJournalException("VBA package mutation terminal must assess every prepared component exactly once.");
                    }

                    var terminal = new VbaPackageMutationTerminal
                    {
                        MutationId = mutationId,
                        Status = status,
                        Components = componentAssessments,
                        ErrorCode = errorCode,
                        Message = message,
                        CreatedUtc = DateTime.UtcNow
                    };
                    AppendLocked(
                        path,
                        host,
                        documentKey,
                        log,
                        VbaJournalEventTypes.PackageMutationTerminal,
                        mutationId,
                        record.Prepared.RunId,
                        record.Prepared.TurnId,
                        record.Prepared.StepId,
                        record.Prepared.ToolCallId,
                        JObject.FromObject(terminal));
                    return terminal;
                }
            }
        }

        public List<VbaModuleBackup> List(string host, string documentKey)
        {
            var events = ReadEvents(host, documentKey);
            ProjectMutations(events);
            ProjectPackageMutations(events);
            var backups = new Dictionary<string, VbaModuleBackup>(StringComparer.OrdinalIgnoreCase);
            foreach (var journalEvent in events)
            {
                if (journalEvent.Data == null) continue;
                if (string.Equals(journalEvent.Type, VbaJournalEventTypes.BackupCreated, StringComparison.Ordinal))
                {
                    var backup = journalEvent.Data.ToObject<VbaModuleBackup>();
                    if (!ValidBackup(journalEvent, backup))
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid backup record.");
                    }
                    AddBackup(backups, backup);
                    continue;
                }
                if (!string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal)) continue;
                var prepared = journalEvent.Data.ToObject<VbaMutationPreparation>();
                if (prepared == null || !prepared.BeforeExists || prepared.BeforeCodeReference == null ||
                    string.IsNullOrWhiteSpace(prepared.BackupId)) continue;
                AddBackup(backups, new VbaModuleBackup
                {
                    BackupId = prepared.BackupId,
                    MutationId = prepared.MutationId,
                    Host = prepared.Host,
                    DocumentKey = prepared.DocumentKey,
                    DocumentTitle = prepared.DocumentTitle,
                    ModuleName = prepared.ModuleName,
                    ComponentType = prepared.ComponentType,
                    CodeSha256 = prepared.BeforeCodeSha256,
                    CodeByteLength = prepared.BeforeCodeReference.ByteLength,
                    CodeReference = prepared.BeforeCodeReference,
                    CreatedUtc = prepared.CreatedUtc
                });
            }
            foreach (var packageEvent in events.Where(item =>
                string.Equals(item.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal)))
            {
                var prepared = packageEvent.Data == null
                    ? null
                    : packageEvent.Data.ToObject<VbaPackageMutationPreparation>();
                foreach (var component in prepared == null
                    ? new List<VbaPackageMutationComponent>()
                    : prepared.Components ?? new List<VbaPackageMutationComponent>())
                {
                    if (component == null || !component.BeforeExists || component.BeforeCodeReference == null ||
                        string.IsNullOrWhiteSpace(component.BackupId)) continue;
                    AddBackup(backups, new VbaModuleBackup
                    {
                        BackupId = component.BackupId,
                        MutationId = prepared.MutationId,
                        Host = prepared.Host,
                        DocumentKey = prepared.DocumentKey,
                        DocumentTitle = prepared.DocumentTitle,
                        ModuleName = component.ModuleName,
                        ComponentType = component.BeforeComponentType,
                        CodeSha256 = component.BeforeCodeSha256,
                        CodeByteLength = component.BeforeCodeReference.ByteLength,
                        CodeReference = component.BeforeCodeReference,
                        CreatedUtc = prepared.CreatedUtc
                    });
                }
            }
            return backups.Values.OrderByDescending(item => item.CreatedUtc).ToList();
        }

        public VbaModuleBackup Find(string host, string documentKey, string backupId, string moduleName)
        {
            var backups = List(host, documentKey);
            var backup = !string.IsNullOrWhiteSpace(backupId)
                ? backups.FirstOrDefault(item => string.Equals(item.BackupId, backupId, StringComparison.OrdinalIgnoreCase))
                : backups.FirstOrDefault(item => string.Equals(item.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
            if (backup == null) return null;
            backup.Code = _blobs.ReadText(backup.CodeReference);
            if (backup.Code == null)
            {
                throw new VbaJournalException("VBA backup content is missing, corrupt, or protected with another key: " + backup.BackupId + ".");
            }
            return backup;
        }

        public IReadOnlyList<VbaMutationRecord> ListMutations(string host, string documentKey)
        {
            return ProjectMutations(ReadEvents(host, documentKey));
        }

        public IReadOnlyList<VbaMutationRecord> ListOpenMutations(string host, string documentKey)
        {
            return ListMutations(host, documentKey)
                .Where(item => item.Prepared != null && item.Terminal == null)
                .ToList();
        }

        public IReadOnlyList<VbaPackageMutationRecord> ListPackageMutations(string host, string documentKey)
        {
            return ProjectPackageMutations(ReadEvents(host, documentKey));
        }

        public IReadOnlyList<VbaPackageMutationRecord> ListOpenPackageMutations(string host, string documentKey)
        {
            return ListPackageMutations(host, documentKey)
                .Where(item => item.Prepared != null && item.Terminal == null)
                .ToList();
        }

        public IReadOnlyList<VbaJournalEvent> ReadEvents(string host, string documentKey)
        {
            lock (PersistenceSync)
            {
                using (AcquireLock(host, documentKey))
                {
                    var log = ReadEventLog(JournalPath(host, documentKey), host, documentKey);
                    return log == null ? new List<VbaJournalEvent>() : log.Events;
                }
            }
        }

        public bool MoveDocument(
            string oldHost,
            string oldDocumentKey,
            string newHost,
            string newDocumentKey,
            string runtimeDocumentKey,
            string documentTitle)
        {
            var oldPath = JournalPath(oldHost, oldDocumentKey);
            var newPath = JournalPath(newHost, newDocumentKey);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;

            lock (PersistenceSync)
            {
                using (AcquireTwoJournalLocks(oldPath, newPath))
                {
                    JournalReadResult log;
                    if (File.Exists(oldPath))
                    {
                        if (File.Exists(newPath))
                        {
                            throw new VbaJournalException("The destination already contains a VBA mutation journal.");
                        }
                        log = ReadEventLog(oldPath, oldHost, oldDocumentKey);
                        EnsureJournalHasIdentity(log);
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                        File.Move(oldPath, newPath);
                    }
                    else if (File.Exists(newPath))
                    {
                        log = ReadEventLogUnbound(newPath);
                        var current = LastEvent(log);
                        if (SameIdentity(current, newHost, newDocumentKey)) return false;
                        if (!SameIdentity(current, oldHost, oldDocumentKey))
                        {
                            throw new VbaJournalException("The VBA mutation journal has an unexpected document identity.");
                        }
                        EnsureJournalHasIdentity(log);
                    }
                    else
                    {
                        return false;
                    }

                    var change = new VbaDocumentIdentityChange
                    {
                        PreviousHost = oldHost ?? string.Empty,
                        PreviousDocumentKey = oldDocumentKey ?? string.Empty,
                        Host = newHost ?? string.Empty,
                        DocumentKey = newDocumentKey ?? string.Empty,
                        RuntimeDocumentKey = runtimeDocumentKey ?? string.Empty,
                        DocumentTitle = documentTitle ?? string.Empty,
                        CreatedUtc = DateTime.UtcNow
                    };
                    AppendLocked(
                        newPath,
                        newHost,
                        newDocumentKey,
                        log,
                        VbaJournalEventTypes.DocumentIdentityChanged,
                        null,
                        null,
                        null,
                        null,
                        null,
                        JObject.FromObject(change));
                    return true;
                }
            }
        }

        internal void ScanCasReferences(CasReachabilityScan scan)
        {
            if (scan == null) throw new ArgumentNullException("scan");
            var paths = StorageFileSystem.GetFilesRecursive(
                _paths.VbaJournalDirectory,
                JournalFileName,
                (path, message) => scan.AddSourceIssue(
                    CasHealthIssueKinds.SourceUnreadable,
                    "vba",
                    CasMaintenanceService.RelativePath(_paths.VbaJournalDirectory, path),
                    message)).ToArray();

            foreach (var path in paths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                scan.VbaJournalCount += 1;
                var sourceId = CasMaintenanceService.RelativePath(_paths.VbaJournalDirectory, path);
                try
                {
                    lock (PersistenceSync)
                    {
                        using (AcquireJournalPathLock(path))
                        {
                            var log = ReadEventLogUnbound(path);
                            if (log == null || log.Events.Count == 0)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "vba", sourceId,
                                    "The VBA mutation journal is empty or invalid.");
                                continue;
                            }
                            foreach (var journalEvent in log.Events)
                            {
                                scan.AddTokenReferences(journalEvent.Data, "vba", sourceId,
                                    "event#" + journalEvent.Sequence + ".Data");
                            }
                            if (log.HasIncompleteTail)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.IncompleteTail, "vba", sourceId,
                                    "The VBA mutation journal has an incomplete final record.");
                            }

                            ProjectMutations(log.Events);
                            ProjectPackageMutations(log.Events);
                            foreach (var backupEvent in log.Events.Where(item =>
                                string.Equals(item.Type, VbaJournalEventTypes.BackupCreated, StringComparison.Ordinal)))
                            {
                                var backup = backupEvent.Data == null ? null : backupEvent.Data.ToObject<VbaModuleBackup>();
                                if (!ValidBackup(backupEvent, backup))
                                {
                                    throw new VbaJournalException("The VBA mutation journal contains an invalid backup record.");
                                }
                            }

                            var identity = log.Events[log.Events.Count - 1];
                            var canonicalPath = JournalPath(identity.Host, identity.DocumentKey);
                            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(canonicalPath), StringComparison.OrdinalIgnoreCase))
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "vba", sourceId,
                                    "The VBA mutation journal is outside its canonical document path.");
                            }
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is IOException || ex is UnauthorizedAccessException || ex is JsonException ||
                    ex is InvalidOperationException || ex is ArgumentException || ex is CryptographicException ||
                    ex is DecoderFallbackException)
                {
                    scan.AddSourceIssue(CasHealthIssueKinds.SourceUnreadable, "vba", sourceId,
                        "The VBA mutation journal could not be validated: " + ex.Message);
                }
            }
        }


        private static bool ValidType(string value)
        {
            return string.Equals(value, VbaJournalEventTypes.BackupCreated, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.DocumentIdentityChanged, StringComparison.Ordinal);
        }

        private static JToken NullString(string value)
        {
            return value == null ? JValue.CreateNull() : new JValue(value);
        }

        private static string NewId(string prefix)
        {
            return prefix + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static VbaJournalEvent LastEvent(JournalReadResult log)
        {
            return log == null || log.Events.Count == 0 ? null : log.Events[log.Events.Count - 1];
        }

        private static bool SameIdentity(VbaJournalEvent journalEvent, string host, string documentKey)
        {
            return journalEvent != null &&
                string.Equals(journalEvent.Host ?? string.Empty, host ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.DocumentKey ?? string.Empty, documentKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureJournalHasIdentity(JournalReadResult log)
        {
            if (log == null || log.Events.Count == 0)
            {
                throw new VbaJournalException("The VBA mutation journal is empty and cannot change document identity.");
            }
        }

        private sealed class JournalReadResult
        {
            public List<VbaJournalEvent> Events { get; private set; }
            public bool HasIncompleteTail { get; set; }

            public JournalReadResult()
            {
                Events = new List<VbaJournalEvent>();
            }
        }

    }
}
