using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    public sealed class VbaJournalStore
    {
        private const string JournalFileName = "mutations.events.jsonl";
        private static readonly object PersistenceSync = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

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

        internal void ScanCasReferences(CasReachabilityScan scan)
        {
            if (scan == null) throw new ArgumentNullException("scan");
            string[] paths;
            try
            {
                paths = Directory.Exists(_paths.VbaJournalDirectory)
                    ? Directory.GetFiles(_paths.VbaJournalDirectory, JournalFileName, SearchOption.AllDirectories)
                    : new string[0];
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                scan.AddSourceIssue(
                    CasHealthIssueKinds.SourceUnreadable,
                    "vba",
                    "vba-journals",
                    "VBA mutation journals could not be enumerated: " + ex.Message);
                return;
            }

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
                            var identity = ReadFirstEvent(path);
                            if (identity == null)
                            {
                                scan.AddSourceIssue(CasHealthIssueKinds.SourceInvalid, "vba", sourceId,
                                    "The VBA mutation journal is empty or invalid.");
                                continue;
                            }
                            var log = ReadEventLog(path, identity.Host, identity.DocumentKey);
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

        private void Append(
            string host,
            string documentKey,
            string type,
            string mutationId,
            string runId,
            string turnId,
            string stepId,
            string toolCallId,
            JToken data)
        {
            lock (PersistenceSync)
            {
                using (AcquireLock(host, documentKey))
                {
                    var path = JournalPath(host, documentKey);
                    AppendLocked(path, host, documentKey, ReadEventLog(path, host, documentKey), type,
                        mutationId, runId, turnId, stepId, toolCallId, data);
                }
            }
        }

        private void AppendLocked(
            string path,
            string host,
            string documentKey,
            JournalReadResult log,
            string type,
            string mutationId,
            string runId,
            string turnId,
            string stepId,
            string toolCallId,
            JToken data)
        {
            if (log != null && log.HasIncompleteTail) RewriteValidEvents(path, log.Events);
            var previous = log == null || log.Events.Count == 0 ? null : log.Events[log.Events.Count - 1];
            var journalEvent = new VbaJournalEvent
            {
                Host = host ?? string.Empty,
                DocumentKey = documentKey ?? string.Empty,
                Sequence = previous == null ? 1 : previous.Sequence + 1,
                Type = type,
                MutationId = mutationId,
                RunId = runId,
                TurnId = turnId,
                StepId = stepId,
                ToolCallId = toolCallId,
                PreviousHash = previous == null ? null : previous.Hash,
                Data = data == null ? null : data.DeepClone()
            };
            var protector = Protection();
            journalEvent.HashAlgorithm = protector.CurrentHashAlgorithm;
            journalEvent.ProtectionKeyId = protector.UsesHmac || protector.Encrypts ? protector.KeyId : null;
            ProtectEventData(journalEvent, protector);
            journalEvent.Hash = ComputeHash(journalEvent, protector);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, Utf8))
            {
                writer.WriteLine(JsonConvert.SerializeObject(journalEvent, Formatting.None));
                writer.Flush();
                stream.Flush(true);
            }
        }

        private JournalReadResult ReadEventLog(string path, string host, string documentKey)
        {
            if (!File.Exists(path)) return null;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Utf8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new VbaJournalException("The VBA mutation journal could not be read.", ex);
            }

            var result = new JournalReadResult();
            var protector = Protection();
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index])) continue;
                VbaJournalEvent journalEvent;
                try
                {
                    journalEvent = JsonConvert.DeserializeObject<VbaJournalEvent>(lines[index]);
                }
                catch (JsonException ex)
                {
                    if (index == lines.Length - 1)
                    {
                        result.HasIncompleteTail = true;
                        break;
                    }
                    throw new VbaJournalException("The VBA mutation journal contains an invalid record.", ex);
                }
                ValidateEvent(result.Events, journalEvent, host, documentKey, protector);
                HydrateEventData(journalEvent, protector);
                result.Events.Add(journalEvent);
            }
            return result;
        }

        private static VbaJournalEvent ReadFirstEvent(string path)
        {
            foreach (var line in File.ReadLines(path, Utf8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                return JsonConvert.DeserializeObject<VbaJournalEvent>(line);
            }
            return null;
        }

        private static void ValidateEvent(
            IReadOnlyList<VbaJournalEvent> previousEvents,
            VbaJournalEvent journalEvent,
            string host,
            string documentKey,
            StorageProtector protector)
        {
            if (journalEvent == null || journalEvent.SchemaVersion != VbaJournalEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(journalEvent.EventId) || string.IsNullOrWhiteSpace(journalEvent.Type) ||
                !ValidType(journalEvent.Type) || !ValidHashAlgorithm(journalEvent.HashAlgorithm) ||
                ((string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal)) &&
                    string.IsNullOrWhiteSpace(journalEvent.MutationId)) ||
                !string.IsNullOrWhiteSpace(journalEvent.EncryptedData) && journalEvent.Data != null ||
                !ProtectionMatches(journalEvent, protector) ||
                !string.Equals(journalEvent.Host ?? string.Empty, host ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey ?? string.Empty, documentKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                throw new VbaJournalException("The VBA mutation journal contains an unsupported record.");
            }
            var previous = previousEvents.Count == 0 ? null : previousEvents[previousEvents.Count - 1];
            if (journalEvent.Sequence != (previous == null ? 1 : previous.Sequence + 1) ||
                !string.Equals(journalEvent.PreviousHash ?? string.Empty, previous == null ? string.Empty : previous.Hash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Hash, ComputeHash(journalEvent, protector), StringComparison.OrdinalIgnoreCase))
            {
                throw new VbaJournalException("The VBA mutation journal integrity check failed.");
            }
        }

        private static IReadOnlyList<VbaMutationRecord> ProjectMutations(IEnumerable<VbaJournalEvent> events)
        {
            var records = new List<VbaMutationRecord>();
            var byId = new Dictionary<string, VbaMutationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var journalEvent in events ?? new List<VbaJournalEvent>())
            {
                if (journalEvent.Data == null) continue;
                if (string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal))
                {
                    var prepared = journalEvent.Data.ToObject<VbaMutationPreparation>();
                    if (!ValidPreparation(journalEvent, prepared) || byId.ContainsKey(prepared.MutationId))
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid preparation.");
                    }
                    var record = new VbaMutationRecord { Prepared = prepared };
                    byId.Add(prepared.MutationId, record);
                    records.Add(record);
                }
                else if (string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal))
                {
                    var terminal = journalEvent.Data.ToObject<VbaMutationTerminal>();
                    VbaMutationRecord record;
                    if (terminal == null || string.IsNullOrWhiteSpace(terminal.MutationId) ||
                        !string.Equals(journalEvent.MutationId, terminal.MutationId, StringComparison.OrdinalIgnoreCase) ||
                        !VbaMutationStatuses.IsTerminal(terminal.Status) ||
                        !byId.TryGetValue(terminal.MutationId, out record) || record.Terminal != null)
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid terminal record.");
                    }
                    if (!SameCorrelation(journalEvent, record.Prepared))
                    {
                        throw new VbaJournalException("The VBA mutation journal terminal correlation is invalid.");
                    }
                    record.Terminal = terminal;
                }
            }
            return records;
        }

        private static IReadOnlyList<VbaPackageMutationRecord> ProjectPackageMutations(IEnumerable<VbaJournalEvent> events)
        {
            var records = new List<VbaPackageMutationRecord>();
            var byId = new Dictionary<string, VbaPackageMutationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var journalEvent in events ?? new List<VbaJournalEvent>())
            {
                if (journalEvent.Data == null) continue;
                if (string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal))
                {
                    var prepared = journalEvent.Data.ToObject<VbaPackageMutationPreparation>();
                    if (!ValidPackagePreparation(journalEvent, prepared) || byId.ContainsKey(prepared.MutationId))
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid package preparation.");
                    }
                    var record = new VbaPackageMutationRecord { Prepared = prepared };
                    byId.Add(prepared.MutationId, record);
                    records.Add(record);
                }
                else if (string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal))
                {
                    var terminal = journalEvent.Data.ToObject<VbaPackageMutationTerminal>();
                    VbaPackageMutationRecord record;
                    if (terminal == null || string.IsNullOrWhiteSpace(terminal.MutationId) ||
                        !string.Equals(journalEvent.MutationId, terminal.MutationId, StringComparison.OrdinalIgnoreCase) ||
                        !VbaMutationStatuses.IsTerminal(terminal.Status) || terminal.Components == null ||
                        !byId.TryGetValue(terminal.MutationId, out record) || record.Terminal != null)
                    {
                        throw new VbaJournalException("The VBA mutation journal contains an invalid package terminal record.");
                    }
                    if (!SameCorrelation(journalEvent, record.Prepared) ||
                        terminal.Components.Count != record.Prepared.Components.Count ||
                        terminal.Components.Any(item => item == null || string.IsNullOrWhiteSpace(item.ModuleName)) ||
                        terminal.Components.GroupBy(item => item.ModuleName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                        terminal.Components.Any(item => !record.Prepared.Components.Any(component =>
                            string.Equals(component.ModuleName, item.ModuleName, StringComparison.OrdinalIgnoreCase))))
                    {
                        throw new VbaJournalException("The VBA mutation journal package terminal correlation is invalid.");
                    }
                    record.Terminal = terminal;
                }
            }
            return records;
        }

        private static void AddBackup(IDictionary<string, VbaModuleBackup> backups, VbaModuleBackup backup)
        {
            if (backup == null || string.IsNullOrWhiteSpace(backup.BackupId) || backup.CodeReference == null) return;
            if (!backups.ContainsKey(backup.BackupId)) backups.Add(backup.BackupId, backup);
        }

        private static bool ValidBackup(VbaJournalEvent journalEvent, VbaModuleBackup backup)
        {
            return journalEvent != null && backup != null &&
                string.IsNullOrWhiteSpace(journalEvent.MutationId) &&
                !string.IsNullOrWhiteSpace(backup.BackupId) &&
                !string.IsNullOrWhiteSpace(backup.ModuleName) &&
                string.Equals(journalEvent.Host, backup.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.DocumentKey, backup.DocumentKey, StringComparison.OrdinalIgnoreCase) &&
                ValidReference(backup.CodeReference) &&
                string.Equals(backup.CodeSha256, backup.CodeReference.Sha256, StringComparison.OrdinalIgnoreCase) &&
                backup.CodeByteLength == backup.CodeReference.ByteLength;
        }

        private static bool ValidPreparation(VbaJournalEvent journalEvent, VbaMutationPreparation prepared)
        {
            if (journalEvent == null || prepared == null || string.IsNullOrWhiteSpace(prepared.MutationId) ||
                string.IsNullOrWhiteSpace(prepared.Operation) || string.IsNullOrWhiteSpace(prepared.ModuleName) ||
                !string.Equals(journalEvent.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Host, prepared.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey, prepared.DocumentKey, StringComparison.OrdinalIgnoreCase) ||
                !SameCorrelation(journalEvent, prepared)) return false;
            if (prepared.BeforeExists != (prepared.BeforeCodeReference != null) ||
                prepared.BeforeExists != !string.IsNullOrWhiteSpace(prepared.BackupId) ||
                prepared.IntendedAfterExists != (prepared.IntendedAfterCodeReference != null)) return false;
            if (prepared.BeforeExists && (!ValidReference(prepared.BeforeCodeReference) ||
                !string.Equals(prepared.BeforeCodeSha256, prepared.BeforeCodeReference.Sha256, StringComparison.OrdinalIgnoreCase))) return false;
            return !prepared.IntendedAfterExists || ValidReference(prepared.IntendedAfterCodeReference) &&
                string.Equals(prepared.IntendedAfterCodeSha256, prepared.IntendedAfterCodeReference.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidPackagePreparation(VbaJournalEvent journalEvent, VbaPackageMutationPreparation prepared)
        {
            if (journalEvent == null || prepared == null || string.IsNullOrWhiteSpace(prepared.MutationId) ||
                string.IsNullOrWhiteSpace(prepared.Operation) || string.IsNullOrWhiteSpace(prepared.PackageId) ||
                prepared.Components == null || prepared.Components.Count == 0 ||
                !string.Equals(journalEvent.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Host, prepared.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey, prepared.DocumentKey, StringComparison.OrdinalIgnoreCase) ||
                !SameCorrelation(journalEvent, prepared)) return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in prepared.Components)
            {
                if (component == null || string.IsNullOrWhiteSpace(component.ModuleName) ||
                    component.BeforeExists && string.IsNullOrWhiteSpace(component.BeforeComponentType) ||
                    !component.BeforeExists && !string.IsNullOrWhiteSpace(component.BeforeComponentType) ||
                    component.IntendedAfterExists && string.IsNullOrWhiteSpace(component.IntendedAfterComponentType) ||
                    !component.IntendedAfterExists && !string.IsNullOrWhiteSpace(component.IntendedAfterComponentType) ||
                    !names.Add(component.ModuleName) ||
                    component.BeforeExists != (component.BeforeCodeReference != null) ||
                    component.IntendedAfterExists != (component.IntendedAfterCodeReference != null) ||
                    prepared.RetainBackups && component.BeforeExists != !string.IsNullOrWhiteSpace(component.BackupId) ||
                    !prepared.RetainBackups && !string.IsNullOrWhiteSpace(component.BackupId) ||
                    component.BeforeExists && (!ValidReference(component.BeforeCodeReference) || !ValidSha256(component.BeforeCodeSha256)) ||
                    component.IntendedAfterExists && (!ValidReference(component.IntendedAfterCodeReference) || !ValidSha256(component.IntendedAfterCodeSha256)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameCorrelation(VbaJournalEvent journalEvent, VbaMutationPreparation prepared)
        {
            return journalEvent != null && prepared != null &&
                string.Equals(journalEvent.RunId ?? string.Empty, prepared.RunId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.TurnId ?? string.Empty, prepared.TurnId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.StepId ?? string.Empty, prepared.StepId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.ToolCallId ?? string.Empty, prepared.ToolCallId ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool SameCorrelation(VbaJournalEvent journalEvent, VbaPackageMutationPreparation prepared)
        {
            return journalEvent != null && prepared != null &&
                string.Equals(journalEvent.RunId ?? string.Empty, prepared.RunId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.TurnId ?? string.Empty, prepared.TurnId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.StepId ?? string.Empty, prepared.StepId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(journalEvent.ToolCallId ?? string.Empty, prepared.ToolCallId ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool ValidSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        private static bool ValidReference(ChatBlobReference reference)
        {
            if (reference == null || reference.ByteLength < 0 || string.IsNullOrWhiteSpace(reference.Sha256) || reference.Sha256.Length != 64)
            {
                return false;
            }
            for (var index = 0; index < reference.Sha256.Length; index++)
            {
                var character = reference.Sha256[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        private static string ComputeHash(VbaJournalEvent journalEvent, StorageProtector protector)
        {
            var canonical = new JObject
            {
                ["SchemaVersion"] = journalEvent.SchemaVersion,
                ["Host"] = journalEvent.Host,
                ["DocumentKey"] = journalEvent.DocumentKey,
                ["Sequence"] = journalEvent.Sequence,
                ["EventId"] = journalEvent.EventId,
                ["CreatedUtc"] = journalEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = journalEvent.Type,
                ["MutationId"] = NullString(journalEvent.MutationId),
                ["RunId"] = NullString(journalEvent.RunId),
                ["TurnId"] = NullString(journalEvent.TurnId),
                ["StepId"] = NullString(journalEvent.StepId),
                ["ToolCallId"] = NullString(journalEvent.ToolCallId),
                ["PreviousHash"] = NullString(journalEvent.PreviousHash),
                ["HashAlgorithm"] = journalEvent.HashAlgorithm,
                ["ProtectionKeyId"] = NullString(journalEvent.ProtectionKeyId),
                ["Data"] = string.IsNullOrWhiteSpace(journalEvent.EncryptedData) && journalEvent.Data != null
                    ? journalEvent.Data.DeepClone()
                    : JValue.CreateNull(),
                ["EncryptedData"] = string.IsNullOrWhiteSpace(journalEvent.EncryptedData)
                    ? JValue.CreateNull()
                    : new JValue(journalEvent.EncryptedData)
            };
            try
            {
                return (protector ?? StorageProtector.None).ComputeEventHash(
                    Utf8.GetBytes(canonical.ToString(Formatting.None)),
                    journalEvent.HashAlgorithm,
                    journalEvent.ProtectionKeyId);
            }
            catch (CryptographicException ex)
            {
                throw new VbaJournalException("The VBA mutation journal protection key is unavailable or invalid.", ex);
            }
        }

        private static void ProtectEventData(VbaJournalEvent journalEvent, StorageProtector protector)
        {
            if (journalEvent == null || protector == null || !protector.Encrypts) return;
            var plaintext = Utf8.GetBytes(journalEvent.Data == null ? "null" : journalEvent.Data.ToString(Formatting.None));
            journalEvent.EncryptedData = Convert.ToBase64String(
                protector.Protect(plaintext, EventProtectionPurpose(journalEvent)));
            journalEvent.Data = null;
        }

        private static void HydrateEventData(VbaJournalEvent journalEvent, StorageProtector protector)
        {
            if (journalEvent == null || string.IsNullOrWhiteSpace(journalEvent.EncryptedData)) return;
            try
            {
                var stored = Convert.FromBase64String(journalEvent.EncryptedData);
                var plaintext = (protector ?? StorageProtector.None).Unprotect(stored, EventProtectionPurpose(journalEvent));
                var parsed = JToken.Parse(Utf8.GetString(plaintext));
                journalEvent.Data = parsed.Type == JTokenType.Null ? null : parsed;
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is JsonException)
            {
                throw new VbaJournalException("The encrypted VBA mutation event could not be authenticated.", ex);
            }
        }

        private static string EventProtectionPurpose(VbaJournalEvent journalEvent)
        {
            return "vba-journal|" + new JObject
            {
                ["SchemaVersion"] = journalEvent.SchemaVersion,
                ["Host"] = journalEvent.Host,
                ["DocumentKey"] = journalEvent.DocumentKey,
                ["Sequence"] = journalEvent.Sequence,
                ["EventId"] = journalEvent.EventId,
                ["CreatedUtc"] = journalEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = journalEvent.Type,
                ["MutationId"] = NullString(journalEvent.MutationId),
                ["RunId"] = NullString(journalEvent.RunId),
                ["TurnId"] = NullString(journalEvent.TurnId),
                ["StepId"] = NullString(journalEvent.StepId),
                ["ToolCallId"] = NullString(journalEvent.ToolCallId),
                ["PreviousHash"] = NullString(journalEvent.PreviousHash),
                ["HashAlgorithm"] = journalEvent.HashAlgorithm,
                ["ProtectionKeyId"] = NullString(journalEvent.ProtectionKeyId)
            }.ToString(Formatting.None);
        }

        private IDisposable AcquireLock(string host, string documentKey)
        {
            return AcquireJournalPathLock(JournalPath(host, documentKey));
        }

        private IDisposable AcquireJournalPathLock(string journalPath)
        {
            var directory = Path.Combine(_paths.Root, "locks");
            Directory.CreateDirectory(directory);
            var lockPath = Path.Combine(directory, "vba_" + AppDataPaths.SafeFileName(Path.GetFullPath(journalPath)) + ".lck");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new VbaJournalException("Timed out waiting for another RNAssistant instance to finish the VBA journal.", ex);
                    }
                    Thread.Sleep(25);
                }
            }
        }

        private string JournalPath(string host, string documentKey)
        {
            return Path.Combine(
                _paths.VbaJournalDirectory,
                AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)),
                JournalFileName);
        }

        private StorageProtector Protection()
        {
            return _protectionProvider() ?? StorageProtector.None;
        }

        private static void RewriteValidEvents(string path, IEnumerable<VbaJournalEvent> events)
        {
            var content = string.Join("\n", (events ?? new List<VbaJournalEvent>())
                .Select(item => JsonConvert.SerializeObject(item, Formatting.None)));
            if (content.Length > 0) content += "\n";
            StorageFileSystem.WriteAllTextAtomic(path, content, Utf8);
        }

        private static bool ValidType(string value)
        {
            return string.Equals(value, VbaJournalEventTypes.BackupCreated, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal) ||
                string.Equals(value, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal);
        }

        private static bool ValidHashAlgorithm(string value)
        {
            return string.Equals(value, HistoryIntegrityModes.Sha256, StringComparison.Ordinal) ||
                string.Equals(value, HistoryIntegrityModes.HmacSha256, StringComparison.Ordinal);
        }

        private static bool ProtectionMatches(VbaJournalEvent journalEvent, StorageProtector protector)
        {
            protector = protector ?? StorageProtector.None;
            if (!string.Equals(journalEvent.HashAlgorithm, protector.CurrentHashAlgorithm, StringComparison.Ordinal)) return false;
            if (protector.Encrypts != !string.IsNullOrWhiteSpace(journalEvent.EncryptedData)) return false;
            if (protector.UsesHmac || protector.Encrypts)
            {
                return !string.IsNullOrWhiteSpace(journalEvent.ProtectionKeyId) &&
                    string.Equals(journalEvent.ProtectionKeyId, protector.KeyId, StringComparison.OrdinalIgnoreCase);
            }
            return string.IsNullOrWhiteSpace(journalEvent.ProtectionKeyId);
        }

        private static JToken NullString(string value)
        {
            return value == null ? JValue.CreateNull() : new JValue(value);
        }

        private static string NewId(string prefix)
        {
            return prefix + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
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
