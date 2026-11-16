using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        // Explicit archive reads reuse the legacy validators/reducers. They never
        // repair SQLite, hydrate a continuation, append a fact or publish a resource.
        public LegacyChatRecoveryPage ReadLegacyRecoveryChats(int offset = 0, int limit = 25,
            CancellationToken token = default(CancellationToken))
        {
            LegacyRecoveryFiles.RequirePage(offset, limit);
            var paths = LegacyRecoveryChatFiles(token).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (offset > paths.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            var items = new List<LegacyChatRecoveryItem>();
            foreach (var path in paths.Skip(offset).Take(limit))
            {
                token.ThrowIfCancellationRequested();
                var item = new LegacyChatRecoveryItem { SourceId = RecoverySourceId(path), Status = "readable" };
                try
                {
                    var saved = ReadLegacyRecovery(path, false, token).Item1;
                    item.ChatId = saved.Id; item.Title = saved.Title; item.Host = saved.Host;
                    item.DocumentTitle = saved.DocumentTitle; item.DocumentPath = saved.DocumentPath;
                    item.Revision = saved.Revision; item.MessageCount = saved.MessageCount;
                    item.CanImport = true; // The write revalidates exact source, identity, payloads and target.
                }
                catch (Exception error) when (LegacyRecoveryFiles.ReadFailure(error))
                { item.Status = LegacyRecoveryFiles.Status(error); item.Error = LegacyRecoveryFiles.Error(error); }
                items.Add(item);
            }
            return new LegacyChatRecoveryPage { RootPath = _paths.ChatDirectory, Total = paths.Length, Items = items,
                NextOffset = offset + items.Count < paths.Length ? (int?)(offset + items.Count) : null };
        }

        public LegacyChatTranscriptPage ReadLegacyRecoveryTranscript(string sourceId, long expectedRevision,
            int messageIndex = 0, int characterOffset = 0, int maxCharacters = 32000,
            CancellationToken token = default(CancellationToken))
        {
            if (expectedRevision < 1 || messageIndex < 0 || characterOffset < 0 || maxCharacters < 1024 || maxCharacters > 65536)
                throw new ArgumentOutOfRangeException("An exact revision and bounded transcript cursor are required.");
            var saved = ReadLegacyRecovery(ResolveRecoverySource(sourceId), true, token);
            if (saved.Item1.Revision != expectedRevision) throw new ChatConcurrencyException("Архив изменился. Обновите список перед чтением.");
            var messages = (saved.Item2["Messages"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            if (messageIndex > messages.Length || messageIndex == messages.Length && characterOffset != 0)
                throw new ArgumentOutOfRangeException(nameof(messageIndex));
            var output = new StringBuilder(); var index = messageIndex; var position = characterOffset;
            while (index < messages.Length && output.Length < maxCharacters && index - messageIndex < 100)
            {
                token.ThrowIfCancellationRequested();
                var text = RecoveryMessageText(messages[index], index);
                if (position >= text.Length) throw new ArgumentOutOfRangeException(nameof(characterOffset));
                var take = Math.Min(text.Length - position, maxCharacters - output.Length);
                if (take > 0 && position + take < text.Length && char.IsHighSurrogate(text[position + take - 1])) take--;
                if (take == 0) break;
                output.Append(text, position, take); position += take;
                if (position == text.Length) { index++; position = 0; }
            }
            return new LegacyChatTranscriptPage { SourceId = sourceId, ChatId = saved.Item1.Id, Title = saved.Item1.Title,
                Revision = expectedRevision, MessageStartIndex = messageIndex, TotalMessages = messages.Length,
                NextMessageIndex = index < messages.Length ? (int?)index : null, NextCharacterOffset = position, Text = output.ToString() };
        }

        private IEnumerable<string> LegacyRecoveryChatFiles(CancellationToken token)
        {
            LegacyRecoveryFiles.RequireRegular(_paths.ChatDirectory);
            foreach (var directory in Directory.EnumerateDirectories(_paths.ChatDirectory))
            {
                token.ThrowIfCancellationRequested(); LegacyRecoveryFiles.RequireRegular(directory);
                foreach (var path in Directory.EnumerateFiles(directory, "*" + EventFileSuffix)) yield return path;
            }
        }

        private string RecoverySourceId(string path)
        { return Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path); }

        private string ResolveRecoverySource(string sourceId)
        {
            if (sourceId == null || !Regex.IsMatch(sourceId, "\\A[a-f0-9]{64}/[a-f0-9]{64}\\.events\\.jsonl\\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("Выберите точную запись из каталога архива.", nameof(sourceId));
            var path = Path.Combine(_paths.ChatDirectory, sourceId.Replace('/', Path.DirectorySeparatorChar));
            LegacyRecoveryFiles.RequireRegular(path); return path;
        }

        private Tuple<ChatSessionHeader, JObject> ReadLegacyRecovery(string path, bool transcript, CancellationToken token)
        {
            ResolveRecoverySource(RecoverySourceId(path));
            using (AcquirePersistenceRead())
            using (AcquireDocumentPathLockForRead(path))
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                LegacyRecoveryFiles.CheckJournal(path, token);
                var before = CaptureStorageFileState(path); var protector = Protection();
                var header = new ChatHeaderReducer(_blobs);
                var projection = transcript ? new ProjectionReplayCursor(null) : null;
                SessionEvent last = null; var count = 0;
                var summary = JsonlRecordReader.Read(path, 0, text => {
                    if (text.Length > 8 * 1024 * 1024) throw new LegacyRecoveryReadException("tooLarge", "Запись архива превышает 8 MiB.");
                    return ParseSessionEvent(text);
                }, (item, line) => {
                    if (line.NextOffset > 64L * 1024 * 1024) throw new LegacyRecoveryReadException("tooLarge", "Архив превышает 64 MiB.");
                    if (++count > 200000) throw new LegacyRecoveryReadException("tooLarge", "В архиве более 200000 событий; файл сохранён без изменений.");
                    ValidateEvent(last, item, protector); item.StorageByteOffset = line.Offset;
                    if (IsProjectionEvent(item)) { HydrateEventData(item, protector); header.Apply(item); projection?.Apply(item); }
                    last = item;
                }, cancellationToken: token);
                if (summary.HasIncompleteTail) throw new LegacyRecoveryReadException("incomplete", "У архива незавершённый хвост. Восстановление не изменяет и не обрезает его.");
                var after = CaptureStorageFileState(path);
                if (before == null || after == null || before.ByteLength != after.ByteLength ||
                    before.LastWriteUtcTicks != after.LastWriteUtcTicks || summary.ByteLength != before.ByteLength)
                    throw new ChatConcurrencyException("Архив изменился во время чтения.");
                if (last == null || !header.IsValid) throw new InvalidDataException("Формат архива не читается существующим legacy reader.");
                var value = header.CreateHeader(null, last.Sequence, summary.ByteLength, null, null, null);
                if (GetSessionPath(value.Host, value.DocumentKey, value.Id) != path)
                    throw new InvalidDataException("Идентичность архива не соответствует его пути.");
                var root = projection?.Materialize();
                if (transcript && root == null) throw new InvalidDataException("Не удалось прочитать сообщения архива.");
                return Tuple.Create(value, root);
            }
        }

        private static string RecoveryMessageText(JObject message, int index)
        {
            var text = new StringBuilder().Append("[" + (index + 1) + "] ").Append((string)message["Role"] ?? "message");
            if ((bool?)message["ProtocolMessage"] == true) text.Append(" (сохранённый протокол)");
            text.AppendLine().AppendLine((string)message["Content"] ?? string.Empty);
            var reasoning = (string)message["ReasoningContent"];
            if (!string.IsNullOrEmpty(reasoning)) text.AppendLine("Сохранённое рассуждение:").AppendLine(reasoning);
            if (message["ToolCalls"] is JArray calls && calls.Count > 0) text.AppendLine("Сохранённые вызовы (не выполняются):").AppendLine(calls.ToString(Formatting.None));
            if (message["Attachments"] is JArray attachments && attachments.Count > 0)
                text.AppendLine("Вложения: " + string.Join(", ", attachments.Select(item => (string)item["FileName"] ?? (string)item["Name"] ?? "вложение")));
            if (message["ResultBody"] != null && message["ResultBody"].Type != JTokenType.Null)
                text.AppendLine("[Полное тело tool-result хранится отдельно в CAS; эта страница содержит текст сообщения.]");
            return text.AppendLine().ToString();
        }
    }
}
