using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Presentation of saved facts only. Never reparses/adopts a model proposal.
    internal static class ModelContextRequestProjection
    {
        internal static ModelContextSummaryDto Summary(IReadOnlyList<SessionEvent> all,
            SessionEvent request, IReadOnlyList<ModelContextEventDto> exchange, CancellationToken token)
        {
            var trace = request.Data?.ToObject<LlmTraceRecord>() ?? new LlmTraceRecord();
            var rejected = exchange.LastOrDefault(item => item.Type == SessionEventTypes.AgentResponseRejected);
            var accepted = exchange.LastOrDefault(item => item.Type == SessionEventTypes.ModelResponseAccepted);
            var response = exchange.LastOrDefault(item => item.Type == SessionEventTypes.LlmResponse);
            var failure = exchange.LastOrDefault(item => item.Type == SessionEventTypes.LlmFailure);
            var summary = new ModelContextSummaryDto {
                Parser = rejected != null ? ModelContextAcceptance.Rejected : accepted != null
                    ? ModelContextAcceptance.Accepted : ModelContextAcceptance.NotRecorded,
                Runtime = ModelContextAcceptance.NotRecorded,
                Reason = rejected?.Trace?.Error ?? failure?.Trace?.Error,
                ResponseEventId = response?.EventId, RejectedEventId = rejected?.EventId,
                RequestBytes = request.Payload?.ByteLength, RequestSha256 = request.Payload?.Sha256,
                ResponseComplete = response?.Trace?.Completed,
                CaptureTruncated = exchange.Any(item => item.Trace?.CaptureTruncated == true),
                PreviousRequestEventId = all.LastOrDefault(item => item.Sequence < request.Sequence &&
                    item.Type == SessionEventTypes.LlmRequest && item.RunId == request.RunId &&
                    !string.IsNullOrEmpty(request.RunId))?.EventId
            };
            if (ModelContextEvidenceProjection.AcceptedMessages(all, request, trace, token).Any())
                summary.Runtime = ModelContextAcceptance.Accepted;
            return summary;
        }

        private const int MaxBytes = 8 * 1024 * 1024;
        private const int MaxMessages = 8000;
        private const int MaxChanges = 200;

        internal static ModelContextComparisonDto Compare(SessionEvent before, SessionEvent after,
            ChatBlobStore blobs, CancellationToken token)
        {
            var result = new ModelContextComparisonDto { BeforeEventId = before.EventId, AfterEventId = after.EventId,
                BeforeBytes = before.Payload?.ByteLength, AfterBytes = after.Payload?.ByteLength,
                BeforeSha256 = before.Payload?.Sha256, AfterSha256 = after.Payload?.Sha256,
                SettingsChanged = new string[0], Changes = new ModelContextMessageChangeDto[0] };
            if (before.Payload == null || after.Payload == null)
            { result.Reason = "Тело одного из запросов не сохранено."; return result; }
            if (before.Payload.ByteLength > MaxBytes || after.Payload.ByteLength > MaxBytes)
            { result.Reason = "Сравнение ограничено 8 МБ на запрос. Доступны точные исходники."; return result; }
            try
            {
                var a = Read(before, blobs, token); var b = Read(after, blobs, token);
                var left = a["messages"] as JArray; var right = b["messages"] as JArray;
                if (left == null || right == null || left.Count > MaxMessages || right.Count > MaxMessages ||
                    left.Any(item => !ReadableMessage(item)) || right.Any(item => !ReadableMessage(item)))
                { result.Reason = "Нужны массивы messages до 8000 объектов сообщений со строковым полем role."; return result; }
                result.BeforeMessages = left.Count; result.AfterMessages = right.Count;
                var prefix = 0; var suffix = 0;
                while (prefix < Math.Min(left.Count, right.Count) && Equal(left[prefix], right[prefix], token)) prefix++;
                while (suffix < Math.Min(left.Count, right.Count) - prefix &&
                    Equal(left[left.Count - suffix - 1], right[right.Count - suffix - 1], token)) suffix++;
                result.UnchangedPrefix = prefix; result.UnchangedSuffix = suffix;
                var leftMiddle = left.Count - prefix - suffix; var rightMiddle = right.Count - prefix - suffix;
                var changes = new List<ModelContextMessageChangeDto>();
                for (var index = 0; index < Math.Max(leftMiddle, rightMiddle); index++)
                {
                    token.ThrowIfCancellationRequested();
                    var old = index < leftMiddle ? left[prefix + index] as JObject : null;
                    var next = index < rightMiddle ? right[prefix + index] as JObject : null;
                    if (old != null && next != null && JToken.DeepEquals(old, next)) continue;
                    if (changes.Count == MaxChanges) { result.Truncated = true; break; }
                    var fields = ChangedFields(old, next, null);
                    if (fields.Count > 40) result.Truncated = true;
                    changes.Add(new ModelContextMessageChangeDto { Kind = index >= leftMiddle ? "added" : index >= rightMiddle ? "removed" : "changed",
                        BeforeIndex = index < leftMiddle ? (int?)(prefix + index) : null,
                        AfterIndex = index < rightMiddle ? (int?)(prefix + index) : null,
                        BeforeRole = (string)old?["role"], AfterRole = (string)next?["role"],
                        Fields = fields.Take(40).ToArray() });
                }
                result.Changes = changes;
                var settings = ChangedFields(a, b, "messages");
                if (settings.Count > 40) result.Truncated = true;
                result.SettingsChanged = settings.Take(40).ToArray();
                result.Available = true;
                result.Reason = "Сравнена разобранная структура сохранённых JSON; байты проверяются по SHA-256. Общие начало и конец сохранены; середина сопоставлена по позиции. Строки остаются строками.";
            }
            catch (Exception error) when (error is IOException || error is JsonException ||
                error is UnauthorizedAccessException || error is DecoderFallbackException || error is System.Security.Cryptography.CryptographicException)
            { result.Reason = "Сохранённое тело недоступно или не является JSON: " + error.Message; }
            return result;
        }

        private static JObject Read(SessionEvent item, ChatBlobStore blobs, CancellationToken token)
        {
            using (var stream = blobs.OpenVerifiedRead(item.Payload, MaxBytes, token))
            using (var text = new StreamReader(stream, new UTF8Encoding(false, true)))
            using (var json = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 })
            {
                token.ThrowIfCancellationRequested();
                var result = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (json.Read()) throw new JsonReaderException("После объекта запроса есть дополнительные данные.");
                token.ThrowIfCancellationRequested();
                return result;
            }
        }
        private static bool Equal(JToken left, JToken right, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return JToken.DeepEquals(left, right); }
        private static bool ReadableMessage(JToken item)
        { return item is JObject && (item["role"] == null || item["role"].Type == JTokenType.String); }
        private static IReadOnlyList<string> ChangedFields(JObject left, JObject right, string except)
        {
            return (left?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>())
                .Concat(right?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>()).Distinct()
                .Where(name => name != except && !JToken.DeepEquals(left?[name], right?[name]))
                .Take(41).ToArray();
        }
    }
}
