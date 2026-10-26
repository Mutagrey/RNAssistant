using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Existing retained activity data supplies content; only source owners supply effects.
    internal sealed class ToolResultPresentationService
    {
        private const int MaxSource = 512 * 1024, MaxRows = 200, MaxText = 12000;
        private readonly Func<string, RunChangesDto> _changes;
        private readonly ChatBlobStore _payloads;
        internal ToolResultPresentationService(Func<string, RunChangesDto> changes, ChatBlobStore payloads = null)
        { _changes = changes; _payloads = payloads; }

        internal ToolResultPresentationDto Read(ChatSession session, string runId, string toolCallId)
        {
            if (session == null || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(toolCallId))
                throw new ArgumentException("An exact chat/run/call is required.");
            var candidates = (session.Messages ?? new List<ChatMessage>()).Where(m => m != null &&
                string.Equals(m.RunId, runId, StringComparison.Ordinal) && m.Activity != null &&
                string.Equals(m.Activity.ToolCallId, toolCallId, StringComparison.Ordinal)).ToList();
            if (candidates.Count != 1) throw new InvalidOperationException("The exact activity is unavailable or ambiguous.");
            var activity = candidates[0].Activity;
            if (!string.IsNullOrEmpty(activity.RunId) && !string.Equals(activity.RunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException("Activity run does not match its message.");
            var result = new ToolResultPresentationDto { ChatId = session.Id, RunId = runId, ToolCallId = toolCallId,
                Action = activity.Display?.Action ?? activity.ToolId, Target = activity.Subtitle };
            var terminals = session.Messages.Where(m => m != null && m.RunId == runId && m.ToolCallId == toolCallId &&
                m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion && m.Activity == null && m.AcceptedCallOrigin == null).ToArray();
            var terminal = terminals.Length == 1 && terminals[0].ToolName == activity.ToolId ? terminals[0] : null;
            result.ExecutionReference = terminal?.ExecutionReference?.Kind == ModelReferenceKind.Execution
                ? terminal.ExecutionReference.Alias : null;
            var effect = activity.ExecutionEvidence?.Effect;
            var changes = effect == ToolEffectEvidence.VerifiedChange || effect == ToolEffectEvidence.VerifiedNoChange ||
                effect == ToolEffectEvidence.Unknown ? _changes?.Invoke(toolCallId) : null;
            if (changes != null && (changes.EvidenceFound || !changes.Complete))
                result.Blocks.Add(new ToolChangesBlockDto { Title = "Изменения этого шага", Complete = changes.Complete, Changes = changes });
            else if (activity.ExecutionEvidence?.Effect == ToolEffectEvidence.Unknown ||
                activity.ExecutionEvidence?.Effect == ToolEffectEvidence.VerifiedChange ||
                activity.ExecutionEvidence?.Effect == ToolEffectEvidence.VerifiedNoChange)
                result.Blocks.Add(new ToolTextBlockDto { Title = "Сравнение", Text =
                    (effect == ToolEffectEvidence.Unknown ? "Результат действия не подтверждён. " :
                     effect == ToolEffectEvidence.VerifiedNoChange ? "Подтверждено отсутствие изменений. " : "Изменения подтверждены. ") +
                    "Сохранённое сравнение для этого вызова недоступно." });
            var questions = activity.ToolId == UserQuestionToolCatalog.AskToolId;
            if (questions) result.QuestionsUnavailableReason = "Сохранённые вопросы недоступны. Повторите загрузку; при повторной ошибке сообщите об этом в чат.";
            // Canonical results/CAS are shared by document and workspace activities.
            // References cannot be rebound to an unrelated activity preview.
            if (terminal != null)
            {
                try
                {
                    var read = terminal;
                    if (terminal.ResultPayload != null)
                    {
                        if (terminal.ResultPayload.ByteLength > MaxSource * 4L) { Notice(result.Blocks); return result; }
                        read = RNAssistant.Core.Llm.HistoricalContextProjector.Project(terminal);
                        read.Content = _payloads?.ReadText(terminal.ResultPayload.ToBlobReference());
                    }
                    ToolResultWireReadResult wire; string error;
                    if (ToolResultHistoryReader.TryRead(read, out wire, out error))
                    {
                        result.RawJson = wire.Result.DataJson?.Length <= MaxSource ? wire.Result.DataJson : null;
                        if (questions) AppendActivityContent(result, wire.Result.DataJson, true, activity.ToolId);
                        else AppendContent(result.Blocks, wire.Result.DataJson, activity.ToolId,
                            wire.Result.Status == RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok ? terminal : null);
                    }
                    else Unavailable(result.Blocks);
                }
                catch (Exception ex) when (ex is IOException || ex is System.Security.Cryptography.CryptographicException || ex is UnauthorizedAccessException)
                { Unavailable(result.Blocks); }
                return result;
            }
            if (activity.ResultPayload == null) AppendActivityContent(result, activity.DataJson, questions, activity.ToolId);
            else if (activity.ResultPayload.ByteLength > MaxSource * 4L) Notice(result.Blocks);
            else
            {
                try
                {
                    var content = _payloads?.ReadText(activity.ResultPayload.ToBlobReference());
                    if (content == null) Unavailable(result.Blocks);
                    else AppendActivityContent(result, content, questions, activity.ToolId);
                }
                catch (Exception ex) when (ex is IOException || ex is System.Security.Cryptography.CryptographicException ||
                    ex is UnauthorizedAccessException)
                { Unavailable(result.Blocks); }
            }
            return result;
        }

        private static void AppendActivityContent(ToolResultPresentationDto result, string source, bool questions, string toolId)
        {
            result.RawJson = source?.Length <= MaxSource ? source : null;
            if (!questions) { AppendContent(result.Blocks, source, toolId); return; }
            if (string.IsNullOrWhiteSpace(source) || source.Length > MaxSource) return;
            try
            {
                var data = JsonConvert.DeserializeObject<UserQuestionSet>(source, new JsonSerializerSettings {
                    MaxDepth = 16, CheckAdditionalContent = true, TypeNameHandling = TypeNameHandling.None });
                if (data?.Type != "rnassistant.questions" || string.IsNullOrWhiteSpace(data.QuestionSetId) ||
                    data.Questions == null || data.Questions.Count < 1 || data.Questions.Count > 3 ||
                    data.Questions.Any(q => q == null || string.IsNullOrWhiteSpace(q.Id) ||
                        string.IsNullOrWhiteSpace(q.Prompt) || (q.Selection != "single" && q.Selection != "multiple") ||
                        q.Options == null || q.Options.Count < 2 || q.Options.Count > 4 ||
                        q.Options.Any(o => o == null || string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Label)))) return;
                result.Questions = data;
                result.QuestionsUnavailableReason = null;
            }
            catch (JsonException) { } // The typed unavailable reason remains visible.
        }

        private static void Unavailable(List<ToolResultBlockDto> blocks)
        { blocks.Add(new ToolTextBlockDto { Title = "Содержимое", Complete = false,
            Text = "Сохранённое содержимое результата недоступно. Статус выполненного действия не изменён." }); }

        // Recognize existing content shapes through a typed input projection. No before/after heuristic.
        internal static void AppendContent(List<ToolResultBlockDto> blocks, string source, string toolId = null, ChatMessage terminal = null)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            if (source.Length > MaxSource) { Notice(blocks); return; }
            try
            {
                PreviewSource data;
                using (var reader = new JsonTextReader(new StringReader(source)) { MaxDepth = 32,
                    DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
                {
                    var serializer = new JsonSerializer { MaxDepth = 32, TypeNameHandling = TypeNameHandling.None };
                    if (!reader.Read()) return;
                    if (reader.TokenType == JsonToken.String) data = new PreviewSource { Text = (string)reader.Value };
                    else if (reader.TokenType == JsonToken.StartArray) data = new PreviewSource { Items = serializer.Deserialize<List<PreviewItem>>(reader) };
                    else if (reader.TokenType == JsonToken.StartObject) data = toolId == ResourceToolCatalog.ReadToolId
                        ? serializer.Deserialize<ResourceReadPreviewSource>(reader) : serializer.Deserialize<PreviewSource>(reader);
                    else return;
                    if (reader.Read()) throw new JsonSerializationException("Trailing data.");
                }
                if (data == null) return;
                var readData = data as ResourceReadPreviewSource;
                var sourceView = readData?.Kind == "resource-read"
                    ? Source(data.Target, data.Text, readData.Representation, readData.SourceCoverage ?? readData.Coverage) : null;
                if (sourceView != null) sourceView.PageIndex = readData.PageIndex;
                if (sourceView != null && readData.Representation != ResourceRepresentations.Media &&
                    (data.Text != null || data.Table != null || readData.Tree != null))
                    sourceView.Reference = ModelReferenceProjection.BoundSourceReference(terminal, readData.Representation, readData.Coverage)?.Alias;
                if (data.Matches != null)
                {
                    var block = new ToolListBlockDto { Title = "Совпадения", Complete = data.ScanComplete == true &&
                        data.SourceComplete == true && data.MatchesComplete == true && data.Matches.Count <= MaxRows };
                    foreach (var match in data.Matches.Take(MaxRows))
                    {
                        if (match == null) { block.Complete = false; continue; }
                        block.Items.Add(new ToolListItemDto { Title = Clip(match.Target ?? data.Target ?? "Ресурс", 240, block),
                            Source = toolId == ResourceToolCatalog.SearchToolId
                                ? Source(match.Target ?? data.Target, match.Snippet, "text", null, match.StartLine, match.StartColumn) : null,
                            Detail = Clip((match.StartLine.HasValue ? "Строка " + match.StartLine + ", столбец " + match.StartColumn + "\n" :
                                match.PartNumber.HasValue ? "Часть " + match.PartNumber + "\n" : "") + match.Snippet, 2000, block) });
                    }
                    blocks.Add(block);
                    if (!block.Complete) blocks.Add(new ToolTextBlockDto { Title = "Полнота поиска", Complete = false,
                        Text = "Поиск или выдача неполны. " + string.Join(", ", data.IncompleteReasons ?? new List<string>()) });
                }
                var complete = (readData?.Kind == "resource-read" ? readData.SelectionComplete ?? data.Complete : data.Complete) != false &&
                    !data.Partial && !data.Truncated && !data.Externalized && string.IsNullOrEmpty(data.NextCursor);
                var note = sourceView == null || terminal == null ? null : ReadNote(readData);
                var table = data.Table ?? (data.Columns != null && data.Rows != null ? new ResourceTableBatch { Columns = data.Columns, Rows = data.Rows, TotalRows = data.TotalRows } : null);
                if (table?.Columns != null && table.Rows != null)
                {
                    var columns = table.Columns.Take(12).ToList();
                    if (columns.Any(c => c == null || string.IsNullOrEmpty(c.Key))) throw new JsonSerializationException("Invalid columns.");
                    var block = new ToolTableBlockDto { Title = "Таблица", Source = sourceView, Note = note, Complete = complete && table.Rows.Count <= MaxRows &&
                        table.Columns.Count <= 12 && table.TotalRows <= table.Rows.Count };
                    block.Columns = columns.Select(c => Clip(c.Label ?? c.Key, 160, block)).ToList();
                    foreach (var row in table.Rows.Take(MaxRows))
                        block.Rows.Add(columns.Select(c => { object value; return Clip(row != null && row.TryGetValue(c.Key, out value) ? Scalar(value) : "", 500, block); }).ToList());
                    blocks.Add(block);
                }
                if (data.Items != null)
                {
                    var block = new ToolListBlockDto { Title = "Список", Complete = complete && data.Items.Count <= MaxRows };
                    foreach (var item in data.Items.Take(MaxRows))
                    {
                        if (item == null) { block.Complete = false; continue; }
                        block.Items.Add(new ToolListItemDto {
                            Title = Clip(item.Title ?? item.Name ?? item.Label ?? item.Target ?? "Результат", 240, block),
                            Source = toolId == ResourceToolCatalog.FindToolId ? Source(item.Target, item.Snippet, null, null) : null,
                            Detail = Clip(string.Join("\n", new[] { item.Target, item.Description, item.Snippet, item.Text, item.Type ?? item.Kind, item.Scope }
                                .Where(t => !string.IsNullOrEmpty(t)).Distinct()), 2000, block) });
                    }
                    blocks.Add(block);
                }
                var text = data.Text ?? data.Content;
                if (text != null)
                {
                    var block = new ToolTextBlockDto { Title = "Содержимое", Source = sourceView, Note = note, Complete = complete };
                    block.Text = Clip(text, MaxText, block); blocks.Add(block);
                }
                if (readData?.Tree?.Nodes != null)
                {
                    var block = new ToolListBlockDto { Title = "Структура", Source = sourceView, Note = note, Complete = complete &&
                        readData.Tree.NextOffset == null && readData.Tree.Nodes.Count <= MaxRows };
                    foreach (var item in readData.Tree.Nodes.Take(MaxRows)) {
                        if (item == null) { block.Complete = false; continue; }
                        block.Items.Add(new ToolListItemDto { Title = item.Pointer,
                            Detail = Clip(item.Type + (item.Value == null ? "" : ": " + Scalar(item.Value)), 2000, block) });
                    }
                    blocks.Add(block);
                }
                if (sourceView != null && readData.Representation == ResourceRepresentations.Media)
                {
                    // A media receipt is not the binary body or evidence of HTTP delivery.
                    var receipt = Source(data.Target, null, readData.Representation, readData.SourceCoverage ?? readData.Coverage);
                    blocks.Add(new ToolTextBlockDto { Title = "Медиа", Source = receipt,
                        Text = terminal != null && readData.HydratedForNextModelStep
                            ? "Медиа подготовлено для следующего запроса модели. Фактическую отправку показывает диагностика запроса."
                            : "Сохранены сведения о медиа; подтверждение подготовки для следующего запроса модели отсутствует.",
                        Note = "Бинарное содержимое не включено в эту карточку. Просмотр доступен в ресурсах чата." });
                }
                if (!complete && data.Items == null && table == null && text == null &&
                    readData?.Representation != ResourceRepresentations.Media) Notice(blocks);
            }
            catch (JsonException) { Notice(blocks); }
            catch (OverflowException) { Notice(blocks); }
        }
        private static string ReadNote(ResourceReadPreviewSource data)
        {
            var notes = new List<string>();
            if (data.SelectionComplete == true && (data.Complete == false || data.SourceComplete == false))
                notes.Add("Выбранный фрагмент получен полностью; весь источник не прочитан.");
            else if (data.SourceComplete == false)
                notes.Add("Получена часть источника.");
            if (data.PageIndex.HasValue || data.StartPage.HasValue)
                notes.Add("Текст извлечён из PDF; это не просмотр изображений и не подтверждение OCR.");
            if (data.NextPageIndex.HasValue)
                notes.Add("Продолжение — со страницы " + ((long)data.NextPageIndex.Value + 1).ToString(CultureInfo.InvariantCulture) + ".");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
        private static ToolSourcePreviewDto Source(string target, string text, string view, ResourceCoverage coverage,
            int? line = null, int? column = null)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            var length = Math.Min(160, text?.Length ?? 0);
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return new ToolSourcePreviewDto { Target = target, View = view, Coverage = coverage, Line = line, Column = column,
                Excerpt = text == null ? null : text.Substring(0, length), ExcerptTruncated = text != null && length < text.Length };
        }
        private static string Scalar(object value)
        {
            if (value == null) return "";
            if (value is string) return (string)value;
            if (value is bool) return (bool)value ? "true" : "false";
            var formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : "Сложное значение — в JSON ниже";
        }
        private static string Clip(string text, int limit, ToolResultBlockDto block)
        {
            if (text == null || text.Length <= limit) return text ?? "";
            block.Complete = false;
            if (char.IsHighSurrogate(text[limit - 1])) limit--;
            return text.Substring(0, limit) + "…";
        }
        private static void Notice(List<ToolResultBlockDto> blocks)
        { blocks.Add(new ToolTextBlockDto { Title = "Представление", Text = "Компактный просмотр недоступен. Подробности доступны в диагностике результата." }); }

        private sealed class ResourceReadPreviewSource : PreviewSource {
            public string Kind { get; set; }
            public string Representation { get; set; }
            public ResourceCoverage Coverage { get; set; }
            public ResourceCoverage SourceCoverage { get; set; }
            public ResourceTree Tree { get; set; }
            public bool? SelectionComplete { get; set; }
            public int? PageIndex { get; set; }
            public int? StartPage { get; set; }
            public int? NextPageIndex { get; set; }
            public bool HydratedForNextModelStep { get; set; }
        }
        private class PreviewSource
        {
            public bool? Complete { get; set; }
            public bool Partial { get; set; }
            public bool Truncated { get; set; }
            public bool Externalized { get; set; }
            public string NextCursor { get; set; }
            public string Text { get; set; }
            public string Content { get; set; }
            public ResourceTableBatch Table { get; set; }
            public int TotalRows { get; set; }
            public List<ResourceTableColumn> Columns { get; set; }
            public List<IDictionary<string, object>> Rows { get; set; }
            public List<PreviewItem> Items { get; set; }
            public string Target { get; set; }
            public bool? ScanComplete { get; set; }
            public bool? SourceComplete { get; set; }
            public bool? MatchesComplete { get; set; }
            public List<ResourceWithinSearchItem> Matches { get; set; }
            public List<string> IncompleteReasons { get; set; }
        }
        private sealed class PreviewItem
        {
            public string Title { get; set; }
            public string Name { get; set; }
            public string Label { get; set; }
            public string Target { get; set; }
            public string Description { get; set; }
            public string Snippet { get; set; }
            public string Text { get; set; }
            public string Kind { get; set; }
            public string Type { get; set; }
            public string Scope { get; set; }
        }
    }
}
