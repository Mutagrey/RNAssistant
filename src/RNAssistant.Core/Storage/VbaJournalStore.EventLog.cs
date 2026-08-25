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

namespace RNAssistant.Core.Storage
{
    public sealed partial class VbaJournalStore
    {
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
            if (log != null && log.HasIncompleteTail) JsonlRecordWriter.RewriteAll(path, log.Events, Utf8);
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
            var result = ReadEventLogUnbound(path);
            var current = LastEvent(result);
            if (current != null && !SameIdentity(current, host, documentKey))
            {
                throw new VbaJournalException("The VBA mutation journal has an unexpected document identity.");
            }
            return result;
        }

        private JournalReadResult ReadEventLogUnbound(string path)
        {
            if (!File.Exists(path)) return null;
            var result = new JournalReadResult();
            var protector = Protection();
            try
            {
                var summary = JsonlRecordReader.Read(
                    path,
                    0,
                    ParseJournalEvent,
                    (journalEvent, line) =>
                    {
                        ValidateEvent(result.Events, journalEvent, protector);
                        HydrateEventData(journalEvent, protector);
                        ValidateIdentityChange(result.Events, journalEvent);
                        result.Events.Add(journalEvent);
                    });
                result.HasIncompleteTail = summary.HasIncompleteTail;
            }
            catch (JsonlRecordException ex)
            {
                throw new VbaJournalException(ex.Kind == JsonlRecordErrorKind.BlankRecord
                    ? "The VBA mutation journal contains a blank record."
                    : "The VBA mutation journal contains an invalid record.", ex.InnerException ?? ex);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException)
            {
                throw new VbaJournalException("The VBA mutation journal could not be read.", ex);
            }
            return result;
        }

        private static VbaJournalEvent ParseJournalEvent(string text)
        {
            var root = JObject.Parse(text, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            var unknown = root.Properties().FirstOrDefault(property => !JournalEventProperties.Contains(property.Name));
            if (unknown != null)
            {
                throw new JsonSerializationException("Unsupported VBA journal event property: " + unknown.Name + ".");
            }
            return root.ToObject<VbaJournalEvent>();
        }

        private static void ValidateEvent(
            IReadOnlyList<VbaJournalEvent> previousEvents,
            VbaJournalEvent journalEvent,
            StorageProtector protector)
        {
            if (journalEvent == null || journalEvent.SchemaVersion != VbaJournalEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(journalEvent.EventId) || string.IsNullOrWhiteSpace(journalEvent.Type) ||
                !ValidType(journalEvent.Type) || !EventProtectionSupport.IsSupportedHashAlgorithm(journalEvent.HashAlgorithm) ||
                ((string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationPrepared, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.MutationTerminal, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal) ||
                  string.Equals(journalEvent.Type, VbaJournalEventTypes.PackageMutationTerminal, StringComparison.Ordinal)) &&
                    string.IsNullOrWhiteSpace(journalEvent.MutationId)) ||
                !string.IsNullOrWhiteSpace(journalEvent.EncryptedData) && journalEvent.Data != null ||
                !EventProtectionSupport.Matches(
                    protector,
                    journalEvent.HashAlgorithm,
                    journalEvent.ProtectionKeyId,
                    journalEvent.EncryptedData))
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

        private static void ValidateIdentityChange(
            IReadOnlyList<VbaJournalEvent> previousEvents,
            VbaJournalEvent journalEvent)
        {
            var previous = previousEvents.Count == 0 ? null : previousEvents[previousEvents.Count - 1];
            var isChange = string.Equals(
                journalEvent.Type,
                VbaJournalEventTypes.DocumentIdentityChanged,
                StringComparison.Ordinal);
            if (previous == null)
            {
                if (isChange) throw new VbaJournalException("The VBA mutation journal starts with an invalid identity change.");
                return;
            }

            var sameIdentity = SameIdentity(previous, journalEvent.Host, journalEvent.DocumentKey);
            if (!isChange)
            {
                if (!sameIdentity)
                {
                    throw new VbaJournalException("The VBA mutation journal changes document identity without a migration event.");
                }
                return;
            }

            var change = journalEvent.Data == null
                ? null
                : journalEvent.Data.ToObject<VbaDocumentIdentityChange>();
            if (sameIdentity || change == null || change.CreatedUtc == default(DateTime) ||
                !string.Equals(previous.Host ?? string.Empty, change.PreviousHost ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.DocumentKey ?? string.Empty, change.PreviousDocumentKey ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.Host ?? string.Empty, change.Host ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(journalEvent.DocumentKey ?? string.Empty, change.DocumentKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                throw new VbaJournalException("The VBA mutation journal contains an invalid identity change.");
            }
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
            journalEvent.EncryptedData = EventProtectionSupport.ProtectPayload(
                journalEvent.Data,
                protector,
                EventProtectionPurpose(journalEvent),
                Utf8);
            journalEvent.Data = null;
        }

        private static void HydrateEventData(VbaJournalEvent journalEvent, StorageProtector protector)
        {
            if (journalEvent == null || string.IsNullOrWhiteSpace(journalEvent.EncryptedData)) return;
            try
            {
                journalEvent.Data = EventProtectionSupport.UnprotectPayload(
                    journalEvent.EncryptedData,
                    protector,
                    EventProtectionPurpose(journalEvent),
                    Utf8);
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

        private IDisposable AcquireTwoJournalLocks(string firstPath, string secondPath)
        {
            if (string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase))
            {
                return AcquireJournalPathLock(firstPath);
            }
            return string.Compare(firstPath, secondPath, StringComparison.OrdinalIgnoreCase) < 0
                ? DisposablePair.Acquire(
                    () => AcquireJournalPathLock(firstPath),
                    () => AcquireJournalPathLock(secondPath))
                : DisposablePair.Acquire(
                    () => AcquireJournalPathLock(secondPath),
                    () => AcquireJournalPathLock(firstPath));
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
    }
}
