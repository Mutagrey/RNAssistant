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
    public sealed partial class ChatStore
    {
        private EventLogReadResult ReadEventLog(string path)
        {
            return ReadEventLog(path, 0, null);
        }

        private EventLogReadResult ReadEventLog(string path, long startByteOffset, SessionEvent previousEvent)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var result = new EventLogReadResult();
            var protector = Protection();
            var before = CaptureStorageFileState(path);
            try
            {
                var summary = JsonlRecordReader.Read(
                    path,
                    startByteOffset,
                    ParseSessionEvent,
                    (sessionEvent, line) =>
                    {
                        ValidateEvent(previousEvent, sessionEvent, protector);
                        HydrateEventData(sessionEvent, protector);
                        sessionEvent.StorageByteOffset = line.Offset;
                        result.Events.Add(sessionEvent);
                        previousEvent = sessionEvent;
                    });
                result.ByteLength = summary.ByteLength;
                result.TailNextByteOffset = summary.TailNextByteOffset;
                result.HasIncompleteTail = summary.HasIncompleteTail;
                var after = CaptureStorageFileState(path);
                result.IsStableSnapshot = before != null && after != null &&
                    before.ByteLength == result.ByteLength && after.ByteLength == result.ByteLength &&
                    before.LastWriteUtcTicks == after.LastWriteUtcTicks;
                result.LastWriteUtcTicks = result.IsStableSnapshot ? after.LastWriteUtcTicks : 0;
            }
            catch (JsonlRecordException ex)
            {
                throw new ChatConcurrencyException(ex.Kind == JsonlRecordErrorKind.BlankRecord
                    ? "The chat event log contains a blank record."
                    : "The chat event log contains an invalid record.");
            }
            catch (DecoderFallbackException)
            {
                throw new ChatConcurrencyException("The chat event log contains invalid UTF-8.");
            }
            return result;
        }

        private static void ValidateEvent(
            SessionEvent previous,
            SessionEvent sessionEvent,
            StorageProtector protector)
        {
            if (sessionEvent == null || sessionEvent.SchemaVersion != SessionEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(sessionEvent.SessionId) || string.IsNullOrWhiteSpace(sessionEvent.Type) ||
                !EventProtectionSupport.IsSupportedHashAlgorithm(sessionEvent.HashAlgorithm) ||
                !string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null ||
                !EventProtectionSupport.Matches(
                    protector,
                    sessionEvent.HashAlgorithm,
                    sessionEvent.ProtectionKeyId,
                    sessionEvent.EncryptedData))
            {
                throw new ChatConcurrencyException("The chat event log uses an unsupported contract. Resource cutover requires schema 4; open a new chat. Existing files were not migrated or deleted.");
            }
            var expectedSequence = previous == null ? 1 : previous.Sequence + 1;
            var expectedPreviousHash = previous == null ? null : previous.Hash;
            if (sessionEvent.Sequence != expectedSequence ||
                previous != null && !string.Equals(sessionEvent.SessionId, previous.SessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sessionEvent.PreviousHash ?? string.Empty, expectedPreviousHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sessionEvent.Hash, ComputeHash(sessionEvent, protector), StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatConcurrencyException("The chat event log integrity check failed.");
            }
        }

        private static SessionEvent ParseSessionEvent(string text)
        {
            var root = JObject.Parse(text, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            var unknown = root.Properties().FirstOrDefault(property => !SessionEventProperties.Contains(property.Name));
            if (unknown != null)
            {
                throw new JsonSerializationException("Unsupported session event property: " + unknown.Name + ".");
            }
            return root.ToObject<SessionEvent>();
        }

        private static string ComputeHash(SessionEvent sessionEvent, StorageProtector protector)
        {
            var canonical = new JObject
            {
                ["SchemaVersion"] = sessionEvent.SchemaVersion,
                ["SessionId"] = sessionEvent.SessionId,
                ["Sequence"] = sessionEvent.Sequence,
                ["EventId"] = sessionEvent.EventId,
                ["CreatedUtc"] = sessionEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = sessionEvent.Type,
                ["RunId"] = sessionEvent.RunId == null ? JValue.CreateNull() : new JValue(sessionEvent.RunId),
                ["TurnId"] = sessionEvent.TurnId == null ? JValue.CreateNull() : new JValue(sessionEvent.TurnId),
                ["StepId"] = sessionEvent.StepId == null ? JValue.CreateNull() : new JValue(sessionEvent.StepId),
                ["PreviousHash"] = sessionEvent.PreviousHash == null ? JValue.CreateNull() : new JValue(sessionEvent.PreviousHash),
                ["HashAlgorithm"] = sessionEvent.HashAlgorithm,
                ["ProtectionKeyId"] = sessionEvent.ProtectionKeyId == null ? JValue.CreateNull() : new JValue(sessionEvent.ProtectionKeyId),
                ["Data"] = string.IsNullOrWhiteSpace(sessionEvent.EncryptedData) && sessionEvent.Data != null
                    ? sessionEvent.Data.DeepClone()
                    : JValue.CreateNull(),
                ["EncryptedData"] = string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)
                    ? JValue.CreateNull()
                    : new JValue(sessionEvent.EncryptedData),
                ["Payload"] = sessionEvent.Payload == null ? JValue.CreateNull() : JToken.FromObject(sessionEvent.Payload)
            };
            try
            {
                var bytes = Utf8.GetBytes(canonical.ToString(Formatting.None));
                return (protector ?? StorageProtector.None).ComputeEventHash(
                    bytes,
                    sessionEvent.HashAlgorithm,
                    sessionEvent.ProtectionKeyId);
            }
            catch (CryptographicException ex)
            {
                throw new ChatConcurrencyException("The chat event log protection key is unavailable or invalid: " + ex.Message);
            }
        }

        private static void ProtectEventData(SessionEvent sessionEvent, StorageProtector protector)
        {
            if (sessionEvent == null || protector == null || !protector.Encrypts) return;
            sessionEvent.EncryptedData = EventProtectionSupport.ProtectPayload(
                sessionEvent.Data,
                protector,
                EventProtectionPurpose(sessionEvent),
                Utf8);
            sessionEvent.Data = null;
        }

        private static void HydrateEventData(SessionEvent sessionEvent, StorageProtector protector)
        {
            if (sessionEvent == null || string.IsNullOrWhiteSpace(sessionEvent.EncryptedData)) return;
            try
            {
                sessionEvent.Data = EventProtectionSupport.UnprotectPayload(
                    sessionEvent.EncryptedData,
                    protector,
                    EventProtectionPurpose(sessionEvent),
                    Utf8);
            }
            catch (FormatException ex)
            {
                throw new ChatConcurrencyException("The encrypted chat event is invalid: " + ex.Message);
            }
            catch (CryptographicException ex)
            {
                throw new ChatConcurrencyException("The encrypted chat event could not be authenticated: " + ex.Message);
            }
            catch (JsonException ex)
            {
                throw new ChatConcurrencyException("The decrypted chat event is invalid: " + ex.Message);
            }
        }

        private static string EventProtectionPurpose(SessionEvent sessionEvent)
        {
            return new JObject
            {
                ["SchemaVersion"] = sessionEvent.SchemaVersion,
                ["SessionId"] = sessionEvent.SessionId,
                ["Sequence"] = sessionEvent.Sequence,
                ["EventId"] = sessionEvent.EventId,
                ["CreatedUtc"] = sessionEvent.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["Type"] = sessionEvent.Type,
                ["RunId"] = sessionEvent.RunId == null ? JValue.CreateNull() : new JValue(sessionEvent.RunId),
                ["TurnId"] = sessionEvent.TurnId == null ? JValue.CreateNull() : new JValue(sessionEvent.TurnId),
                ["StepId"] = sessionEvent.StepId == null ? JValue.CreateNull() : new JValue(sessionEvent.StepId),
                ["PreviousHash"] = sessionEvent.PreviousHash == null ? JValue.CreateNull() : new JValue(sessionEvent.PreviousHash),
                ["HashAlgorithm"] = sessionEvent.HashAlgorithm,
                ["ProtectionKeyId"] = sessionEvent.ProtectionKeyId,
                ["Payload"] = sessionEvent.Payload == null ? JValue.CreateNull() : JToken.FromObject(sessionEvent.Payload)
            }.ToString(Formatting.None);
        }

        private StorageProtector Protection()
        {
            return _protectionProvider() ?? StorageProtector.None;
        }

        private IDisposable AcquirePathLock(string targetPath)
        {
            var directory = Path.Combine(_paths.Root, "locks");
            Directory.CreateDirectory(directory);
            var normalized = Path.GetFullPath(targetPath ?? _paths.Root);
            var lockPath = Path.Combine(directory, "chat_" + AppDataPaths.SafeFileName(normalized) + ".lck");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new ChatConcurrencyException("Timed out waiting for another RNAssistant instance to finish saving this chat.");
                    }
                    Thread.Sleep(25);
                }
            }
        }

        private IDisposable AcquireDocumentLock(string host, string documentKey)
        {
            return AcquireDocumentDirectoryLock(GetDocumentDirectory(host, documentKey));
        }

        private IDisposable AcquireDocumentPathLock(string path)
        {
            return AcquireDocumentDirectoryLock(Path.GetDirectoryName(path ?? string.Empty));
        }

        private IDisposable AcquireDocumentDirectoryLock(string directory)
        {
            return AcquirePathLock((directory ?? _paths.ChatDirectory) + ".document");
        }

        private IDisposable AcquireTwoDocumentLocks(string firstHost, string firstKey, string secondHost, string secondKey)
        {
            var firstDirectory = GetDocumentDirectory(firstHost, firstKey);
            var secondDirectory = GetDocumentDirectory(secondHost, secondKey);
            if (string.Equals(firstDirectory, secondDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return AcquireDocumentDirectoryLock(firstDirectory);
            }
            return string.Compare(firstDirectory, secondDirectory, StringComparison.OrdinalIgnoreCase) < 0
                ? DisposablePair.Acquire(
                    () => AcquireDocumentDirectoryLock(firstDirectory),
                    () => AcquireDocumentDirectoryLock(secondDirectory))
                : DisposablePair.Acquire(
                    () => AcquireDocumentDirectoryLock(secondDirectory),
                    () => AcquireDocumentDirectoryLock(firstDirectory));
        }

        private string GetDocumentDirectory(string host, string documentKey)
        {
            return Path.Combine(_paths.ChatDirectory,
                AppDataPaths.SafeFileName((host ?? string.Empty) + "|" + (documentKey ?? string.Empty)));
        }

        private string GetSessionPath(string host, string documentKey, string sessionId)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey),
                AppDataPaths.SafeFileName(sessionId ?? string.Empty) + EventFileSuffix);
        }

        private string GetActivePath(string host, string documentKey)
        {
            return Path.Combine(GetDocumentDirectory(host, documentKey), "active.txt");
        }

        private static IEnumerable<string> SafeGetDirectories(string directory)
        {
            return StorageFileSystem.GetDirectories(directory);
        }

        private static IEnumerable<string> SafeGetSessionFiles(string directory)
        {
            return StorageFileSystem.GetFiles(directory, "*" + EventFileSuffix);
        }

        private IEnumerable<string> SafeFindSessionFiles(string sessionId)
        {
            if (!Directory.Exists(_paths.ChatDirectory)) return new string[0];
            var fileName = AppDataPaths.SafeFileName(sessionId ?? string.Empty) + EventFileSuffix;
            return StorageFileSystem.GetFilesRecursive(_paths.ChatDirectory, fileName);
        }
    }
}
