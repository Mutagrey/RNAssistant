using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Word;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        internal const string WordReadTextOperation = "word.read_text.direct";
        internal const string WordReadStoriesOperation = "word.read_stories.direct";
        internal const string WordInspectOperation = "word.inspect.direct";
        internal const string WordWriteOperation = "word.write.direct";
        internal const string WordReplaceOperation = "word.replace.direct";
        internal const string WordFormatOperation = "word.format.direct";
        internal const string WordTableOperation = "word.table.direct";
        internal const string WordPageBreakOperation = "word.page_break.direct";
        internal const string WordCommentOperation = "word.comment.direct";

        public WordTextSnapshot ReadText(WordTextReadRequest request)
        {
            BeginWordBackendCall(WordReadTextOperation);
            request = request ?? new WordTextReadRequest();
            var source = request.Source ?? "document";
            var start = source == "range"
                ? Math.Max(0, Math.Min(_wordText.Length, request.Start))
                : 0;
            var end = source == "range"
                ? Math.Max(start, Math.Min(_wordText.Length,
                    request.HasEnd ? request.End : start + 12000))
                : _wordText.Length;
            var text = _wordText.Substring(start, end - start);
            if (request.MaxChars == 0) text = string.Empty;
            else if (request.MaxChars > 0 && text.Length > request.MaxChars)
                text = text.Substring(0, request.MaxChars) + "\n...[truncated]";
            return new WordTextSnapshot
            {
                Source = source,
                Start = start,
                End = end,
                Text = text
            };
        }

        public IReadOnlyList<WordStorySnapshot> ReadStories(
            WordStoryReadRequest request)
        {
            BeginWordBackendCall(WordReadStoriesOperation);
            var scope = request == null || string.IsNullOrWhiteSpace(request.Scope)
                ? "main" : request.Scope;
            return new[]
            {
                new WordStorySnapshot
                {
                    Id = scope == "selection" ? "selection:0" :
                        scope == "all" ? "wdMainTextStory:0" : "main:0",
                    Kind = scope == "selection" ? "selection" :
                        scope == "all" ? "wdMainTextStory" : "main",
                    Start = 0,
                    End = _wordText.Length,
                    Text = _wordText
                }
            };
        }

        public WordInspectionSnapshot Inspect(WordInspectRequest request)
        {
            BeginWordBackendCall(WordInspectOperation);
            request = request ?? new WordInspectRequest();
            var result = new WordInspectionSnapshot { Kind = request.Kind };
            if (request.Kind == "headings")
                result.Headings = new WordHeadingSnapshot[0];
            else if (request.Kind == "tables")
                result.Tables = _wordTables.Take(request.MaxTables).ToArray();
            else if (request.Kind == "comments")
                result.Comments = _wordComments.Select((text, index) =>
                    new WordCommentSnapshot
                    {
                        Index = index + 1,
                        Author = "Mock User",
                        Text = text,
                        Scope = _wordText
                    }).ToArray();
            else result.Statistics = new WordStatisticsSnapshot
            {
                Characters = _wordText.Length,
                Words = _wordText.Split(new[] { ' ', '\r', '\n', '\t' },
                    StringSplitOptions.RemoveEmptyEntries).Length,
                Paragraphs = Math.Max(1, _wordText.Split('\n').Length),
                Tables = _wordTables.Count,
                Comments = _wordComments.Count
            };
            return result;
        }

        public WordMutationBackendResult Write(
            WordWriteRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordWriteOperation);
            var before = _wordText;
            var mode = request == null ? string.Empty : request.Mode;
            var text = request == null ? string.Empty : request.Text ?? string.Empty;
            string after;
            if (mode == "replaceselection") after = text;
            else if (mode == "paragraph")
            {
                var paragraph = text + Environment.NewLine;
                after = request.Location == "start"
                    ? paragraph + before
                    : before + paragraph;
            }
            else after = before + text;
            if (string.Equals(before, after, StringComparison.Ordinal))
                return WordMutationResult(false);
            markDispatchPossible();
            _wordText = after;
            ThrowAfterWordMutation();
            return WordMutationResult(true);
        }

        public void ApplyReplacement(
            WordReplaceApplyRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordReplaceOperation);
            if (request == null || request.Stories == null ||
                request.Stories.Count != 1 ||
                !string.Equals(request.Stories[0].ExpectedText, _wordText,
                    StringComparison.Ordinal))
                throw new WordBackendException(
                    "fake Word replacement target changed",
                    "word_replace_target_changed", true);
            var replacements = request.Stories[0].Replacements ??
                new WordTextReplacement[0];
            if (replacements.Count == 0) return;
            markDispatchPossible();
            var text = _wordText;
            for (var index = replacements.Count - 1; index >= 0; index--)
            {
                var edit = replacements[index];
                text = text.Substring(0, edit.Index) +
                    (edit.Text ?? string.Empty) +
                    text.Substring(edit.Index + edit.Length);
            }
            _wordText = text;
            ThrowAfterWordMutation();
        }

        public WordMutationBackendResult Format(
            WordFormatRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordFormatOperation);
            var token = FormatToken(request);
            if (string.Equals(token, _wordFormatToken, StringComparison.Ordinal))
                return WordMutationResult(false);
            markDispatchPossible();
            _wordFormatToken = token;
            ThrowAfterWordMutation();
            return WordMutationResult(true);
        }

        public WordMutationBackendResult AddTable(
            WordTableRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordTableOperation);
            markDispatchPossible();
            var rows = new List<IReadOnlyList<string>>();
            for (var row = 0; row < request.Rows; row++)
            {
                var values = new List<string>();
                for (var column = 0; column < request.Columns; column++)
                {
                    object value = null;
                    if (request.Values != null && row < request.Values.Count &&
                        request.Values[row] != null &&
                        column < request.Values[row].Count)
                        value = request.Values[row][column];
                    values.Add(Convert.ToString(value) ?? string.Empty);
                }
                rows.Add(values);
            }
            _wordTables.Add(new WordTableSnapshot
            {
                Index = _wordTables.Count + 1,
                Rows = request.Rows,
                Columns = request.Columns,
                Values = rows
            });
            ThrowAfterWordMutation();
            return new WordMutationBackendResult
            {
                Verified = true,
                Changed = true,
                Rows = request.Rows,
                Columns = request.Columns,
                StateToken = TextPatternEngine.Sha256(
                    _wordTables.Count.ToString())
            };
        }

        public WordMutationBackendResult InsertPageBreak(
            WordPageBreakRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordPageBreakOperation);
            markDispatchPossible();
            _wordText += "\f";
            ThrowAfterWordMutation();
            return WordMutationResult(true);
        }

        public WordMutationBackendResult AddComment(
            WordCommentRequest request, Action markDispatchPossible)
        {
            BeginWordBackendCall(WordCommentOperation);
            markDispatchPossible();
            _wordComments.Add(request == null ? string.Empty : request.Text ?? string.Empty);
            ThrowAfterWordMutation();
            return WordMutationResult(true);
        }

        private void BeginWordBackendCall(string operation)
        {
            WordBackendCalls.Add(operation);
        }

        private void ThrowAfterWordMutation()
        {
            if (!WordThrowAfterMutation) return;
            WordThrowAfterMutation = false;
            throw new InvalidOperationException(
                "scripted failure after Word mutation");
        }

        private WordMutationBackendResult WordMutationResult(bool changed)
        {
            return new WordMutationBackendResult
            {
                Verified = true,
                Changed = changed,
                StateToken = TextPatternEngine.Sha256(
                    _wordText + "\n" + _wordComments.Count + "\n" +
                    _wordTables.Count + "\n" + _wordFormatToken)
            };
        }

        private static string FormatToken(WordFormatRequest request)
        {
            if (request == null) return string.Empty;
            return string.Join("|", new[]
            {
                request.Kind, request.Style, request.Target,
                request.HasBold ? request.Bold.ToString() : string.Empty,
                request.HasItalic ? request.Italic.ToString() : string.Empty,
                request.HasUnderline ? request.Underline.ToString() : string.Empty,
                request.HasFontSize ? request.FontSize.ToString() : string.Empty,
                request.HasFontName ? request.FontName : string.Empty
            });
        }
    }
}
