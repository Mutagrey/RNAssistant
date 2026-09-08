using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Existing retained activity data supplies content; only source owners supply effects.
    internal sealed class ToolResultPresentationService
    {
        private const int MaxSource = 512 * 1024, MaxRows = 200, MaxText = 12000;
        private readonly Func<string, RunChangesDto> _changes;
        internal ToolResultPresentationService(Func<string, RunChangesDto> changes) { _changes = changes; }

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
            var result = new ToolResultPresentationDto { ChatId = session.Id, RunId = runId, ToolCallId = toolCallId };
            var effect = activity.ExecutionEvidence?.Effect;
            var changes = effect == ToolEffectEvidence.VerifiedChange || effect == ToolEffectEvidence.VerifiedNoChange ||
                effect == ToolEffectEvidence.Unknown ? _changes?.Invoke(toolCallId) : null;
            if (changes != null && (changes.EvidenceFound || !changes.Complete))
                result.Blocks.Add(new ToolChangesBlockDto { Title = "Изменения этого шага", Complete = changes.Complete, Changes = changes });
            else if (activity.ExecutionEvidence?.Effect == ToolEffectEvidence.Unknown ||
                activity.ExecutionEvidence?.Effect == ToolEffectEvidence.VerifiedChange)
                result.Blocks.Add(new ToolTextBlockDto { Title = "Сравнение", Text = "Сохранённое сравнение для этого вызова недоступно." });
            AppendContent(result.Blocks, activity.DataJson);
            return result;
        }

        // Recognize existing content shapes through a typed input projection. No before/after heuristic.
        internal static void AppendContent(List<ToolResultBlockDto> blocks, string source)
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
                    else if (reader.TokenType == JsonToken.StartObject) data = serializer.Deserialize<PreviewSource>(reader);
                    else return;
                    if (reader.Read()) throw new JsonSerializationException("Trailing data.");
                }
                if (data == null) return;
                var complete = data.Complete != false && !data.Partial && !data.Truncated && !data.Externalized && string.IsNullOrEmpty(data.NextCursor);
                var table = data.Table ?? (data.Columns != null && data.Rows != null ? new ResourceTableBatch { Columns = data.Columns, Rows = data.Rows, TotalRows = data.TotalRows } : null);
                if (table?.Columns != null && table.Rows != null)
                {
                    var columns = table.Columns.Take(12).ToList();
                    if (columns.Any(c => c == null || string.IsNullOrEmpty(c.Key))) throw new JsonSerializationException("Invalid columns.");
                    var block = new ToolTableBlockDto { Title = "Таблица", Complete = complete && table.Rows.Count <= MaxRows &&
                        table.Columns.Count <= 12 && table.TotalRows <= table.Rows.Count };
                    block.Columns = columns.Select(c => Clip(c.Label ?? c.Key, 160, block)).ToList();
                    foreach (var row in table.Rows.Take(MaxRows))
                        block.Rows.Add(columns.Select(c => { object value; return Clip(row != null && row.TryGetValue(c.Key, out value) ? Scalar(value) : "", 500, block); }).ToList());
                    blocks.Add(block);
                }
                else if (data.Items != null)
                {
                    var block = new ToolListBlockDto { Title = "Список", Complete = complete && data.Items.Count <= MaxRows };
                    foreach (var item in data.Items.Take(MaxRows))
                    {
                        if (item == null) { block.Complete = false; continue; }
                        block.Items.Add(new ToolListItemDto {
                            Title = Clip(item.Title ?? item.Name ?? item.Label ?? item.Target ?? "Результат", 240, block),
                            Detail = Clip(string.Join("\n", new[] { item.Target, item.Description, item.Snippet, item.Text, item.Type ?? item.Kind, item.Scope }
                                .Where(t => !string.IsNullOrEmpty(t)).Distinct()), 2000, block) });
                    }
                    blocks.Add(block);
                }
                var text = data.Text ?? data.Content;
                if (text != null)
                {
                    var block = new ToolTextBlockDto { Title = "Содержимое", Complete = complete };
                    block.Text = Clip(text, MaxText, block); blocks.Add(block);
                }
                if (!complete && data.Items == null && table == null && text == null) Notice(blocks);
            }
            catch (JsonException) { Notice(blocks); }
            catch (OverflowException) { Notice(blocks); }
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
        { blocks.Add(new ToolTextBlockDto { Title = "Представление", Text = "Компактный просмотр недоступен. Данные — в JSON ниже." }); }

        private sealed class PreviewSource
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
