using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Word;
using RNAssistant.OfficeHosts.Identity;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    internal sealed class WordInteropBackend : IWordBackend
    {
        private readonly WordDocumentSession _session;

        internal WordInteropBackend(WordDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public WordTextSnapshot ReadText(WordTextReadRequest request)
        {
            request = request ?? new WordTextReadRequest();
            var document = Document();
            var source = request.Source ?? "document";
            Word.Range range;
            if (source == "selection")
                range = ResolveSelectionRange(document).Duplicate;
            else if (source == "range")
            {
                var content = document.Content;
                if (!request.HasEnd || request.Start < content.Start || request.End < request.Start || request.End > content.End)
                    throw new WordBackendException("The exact character range is outside the document.", "RESOURCE_TARGET_INVALID", false);
                range = document.Range(request.Start, request.End);
            }
            else range = document.Range();
            // Reject before Range.Text: trimming afterwards materializes the whole
            // document and can disguise an incomplete exact snapshot.
            if (request.MaxChars < 1 || (long)range.End - range.Start > request.MaxChars)
                throw new WordBackendException("Choose a narrower Word character range.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            return new WordTextSnapshot
            {
                Source = source,
                Start = range.Start,
                End = range.End,
                Text = range.Text ?? string.Empty
            };
        }

        public IReadOnlyList<WordStorySnapshot> ReadStories(
            WordStoryReadRequest request)
        {
            var ranges = StoryRanges(
                Document(), request == null ? null : request.Scope);
            return ranges.Select(item => new WordStorySnapshot
            {
                Id = item.Id,
                Kind = item.Kind,
                Start = item.Range.Start,
                End = item.Range.End,
                Text = item.Range.Text ?? string.Empty
            }).ToArray();
        }

        public WordInspectionSnapshot Inspect(WordInspectRequest request)
        {
            request = request ?? new WordInspectRequest();
            var document = Document();
            var result = new WordInspectionSnapshot { Kind = request.Kind };
            if (request.Kind == "headings")
            {
                var headings = new List<WordHeadingSnapshot>();
                foreach (Word.Paragraph paragraph in document.Paragraphs)
                {
                    var style = MemberText(paragraph.Range, "Style");
                    if (style.IndexOf("Heading", StringComparison.OrdinalIgnoreCase) < 0 &&
                        style.IndexOf("Заголовок", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    headings.Add(new WordHeadingSnapshot
                    {
                        Style = style,
                        Start = paragraph.Range.Start,
                        End = paragraph.Range.End,
                        Text = Trim(paragraph.Range.Text, 500)
                    });
                    if (headings.Count >= request.MaxResults) break;
                }
                result.Headings = headings;
            }
            else if (request.Kind == "tables")
            {
                var tables = new List<WordTableSnapshot>();
                for (var index = 1;
                    index <= document.Tables.Count &&
                    tables.Count < request.MaxTables;
                    index++)
                {
                    var table = document.Tables[index];
                    var values = new List<IReadOnlyList<string>>();
                    var rowLimit = Math.Min(table.Rows.Count, request.MaxRows);
                    for (var row = 1; row <= rowLimit; row++)
                    {
                        var cells = new List<string>();
                        for (var column = 1;
                            column <= table.Columns.Count; column++)
                            cells.Add(CleanCellText(
                                table.Cell(row, column).Range.Text));
                        values.Add(cells);
                    }
                    tables.Add(new WordTableSnapshot
                    {
                        Index = index,
                        Rows = table.Rows.Count,
                        Columns = table.Columns.Count,
                        Values = values
                    });
                }
                result.Tables = tables;
            }
            else if (request.Kind == "comments")
                result.Comments = Comments(document);
            else if (request.Kind == "stats")
                result.Statistics = new WordStatisticsSnapshot
                {
                    Characters = document.Characters.Count,
                    Words = document.Words.Count,
                    Paragraphs = document.Paragraphs.Count,
                    Tables = document.Tables.Count,
                    Comments = document.Comments.Count
                };
            return result;
        }

        public WordMutationBackendResult Write(
            WordWriteRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireMark(markDispatchPossible);
            var document = Document();
            var before = CreateWriteTarget(document, request);
            var inserted = request.Mode == "paragraph"
                ? (request.Text ?? string.Empty) + Environment.NewLine
                : request.Text ?? string.Empty;
            var expected = request.Mode == "paragraph"
                ? InsertAt(before.DocumentText, before.RelativeEnd, inserted)
                : ReplaceSlice(
                    before.DocumentText,
                    before.RelativeStart,
                    before.RelativeEnd,
                    inserted);
            if (SameWordText(expected, before.DocumentText))
                return Result(true, false, Token(expected));
            EnsureWriteTarget(document, request, before);
            markDispatchPossible();
            if (request.Mode == "paragraph")
                before.Range.InsertAfter(inserted);
            else if (request.Mode == "insert" &&
                IsLiveSelectionRange(before.Range, document))
            {
                document.Activate();
                document.Application.Selection.TypeText(inserted);
            }
            else before.Range.Text = inserted;
            var after = document.Content.Text ?? string.Empty;
            return Result(
                SameWordText(expected, after),
                !SameWordText(before.DocumentText, after),
                Token(after));
        }

        public void ApplyReplacement(
            WordReplaceApplyRequest request, Action markDispatchPossible)
        {
            if (request == null || request.Stories == null)
                throw Failure(
                    "Word replacement plan is missing.",
                    "word_replace_plan_invalid", false);
            RequireMark(markDispatchPossible);
            var current = StoryRanges(Document(), request.Scope);
            if (current.Count != request.Stories.Count)
                throw Failure(
                    "Word replacement scope changed before dispatch.",
                    "word_replace_target_changed", true);
            var ranges = current.ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var plan in request.Stories)
            {
                StoryRange item;
                if (plan == null ||
                    !ranges.TryGetValue(plan.StoryId ?? string.Empty, out item) ||
                    item.Range.Start != plan.Start ||
                    item.Range.End != plan.End ||
                    !string.Equals(item.Range.Text ?? string.Empty,
                        plan.ExpectedText ?? string.Empty,
                        StringComparison.Ordinal))
                    throw Failure(
                        "Word replacement scope changed before dispatch.",
                        "word_replace_target_changed", true);
            }
            var mutationCount = request.Stories.Sum(
                story => story == null || story.Replacements == null
                    ? 0 : story.Replacements.Count);
            if (mutationCount == 0) return;
            markDispatchPossible();
            for (var storyIndex = request.Stories.Count - 1;
                storyIndex >= 0; storyIndex--)
            {
                var plan = request.Stories[storyIndex];
                var replacements = plan.Replacements ??
                    new WordTextReplacement[0];
                for (var editIndex = replacements.Count - 1;
                    editIndex >= 0; editIndex--)
                {
                    var edit = replacements[editIndex];
                    if (edit == null || edit.Index < 0 || edit.Length < 0 ||
                        edit.Index + edit.Length >
                            (plan.ExpectedText ?? string.Empty).Length)
                        throw Failure(
                            "Word replacement edit is outside its story.",
                            "word_replace_plan_invalid", false);
                    var target = ranges[plan.StoryId].Range.Duplicate;
                    target.SetRange(
                        plan.Start + edit.Index,
                        plan.Start + edit.Index + edit.Length);
                    target.Text = edit.Text ?? string.Empty;
                }
            }
        }

        public WordMutationBackendResult Format(
            WordFormatRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireMark(markDispatchPossible);
            var document = Document();
            var range = FormatRange(document, request);
            var before = FormatState(range);
            if (FormattingSatisfied(before, request))
                return Result(true, false, before.Token);
            var current = FormatState(
                document.Range(before.Start, before.End));
            if (!string.Equals(before.Token, current.Token, StringComparison.Ordinal))
                throw Failure(
                    "Word formatting target changed before dispatch.",
                    "word_format_target_changed", true);
            markDispatchPossible();
            range = document.Range(before.Start, before.End);
            if (request.Kind == "style")
                range.GetType().InvokeMember(
                    "Style", BindingFlags.SetProperty,
                    null, range, new object[] { request.Style });
            else
            {
                if (request.HasBold) range.Font.Bold = request.Bold ? 1 : 0;
                if (request.HasItalic) range.Font.Italic = request.Italic ? 1 : 0;
                if (request.HasUnderline)
                    range.Font.Underline = request.Underline
                        ? Word.WdUnderline.wdUnderlineSingle
                        : Word.WdUnderline.wdUnderlineNone;
                if (request.HasFontSize) range.Font.Size = request.FontSize;
                if (request.HasFontName) range.Font.Name = request.FontName;
            }
            var after = FormatState(
                document.Range(before.Start, before.End));
            return Result(
                FormattingSatisfied(after, request),
                !string.Equals(before.Token, after.Token, StringComparison.Ordinal),
                after.Token);
        }

        public WordMutationBackendResult AddTable(
            WordTableRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireMark(markDispatchPossible);
            var document = Document();
            var range = ResolveInsertionRange(document, request.Location).Duplicate;
            var beforeTables = TableFingerprints(document);
            var beforeToken = TableTargetToken(document, range, beforeTables);
            var current = ResolveInsertionRange(document, request.Location).Duplicate;
            if (!string.Equals(
                beforeToken,
                TableTargetToken(document, current, TableFingerprints(document)),
                StringComparison.Ordinal))
                throw Failure(
                    "Word table target changed before dispatch.",
                    "word_table_target_changed", true);
            markDispatchPossible();
            var table = document.Tables.Add(current, request.Rows, request.Columns);
            if (request.Values != null)
            {
                for (var row = 1;
                    row <= request.Rows && row <= request.Values.Count; row++)
                {
                    var values = request.Values[row - 1];
                    if (values == null) continue;
                    for (var column = 1;
                        column <= request.Columns && column <= values.Count;
                        column++)
                        table.Cell(row, column).Range.Text =
                            Convert.ToString(values[column - 1],
                                CultureInfo.InvariantCulture);
                }
            }
            var afterTables = TableFingerprints(document);
            var added = TableFingerprint(table);
            var peers = new List<string>(afterTables);
            var removedAdded = peers.Remove(added);
            var verified = removedAdded &&
                document.Tables.Count == beforeTables.Count + 1 &&
                MultisetEquals(beforeTables, peers) &&
                TableMatches(table, request);
            return new WordMutationBackendResult
            {
                Verified = verified,
                Changed = true,
                Rows = table.Rows.Count,
                Columns = table.Columns.Count,
                StateToken = Token(string.Join("\n", afterTables.ToArray()))
            };
        }

        public WordMutationBackendResult InsertPageBreak(
            WordPageBreakRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var document = Document();
            var range = ResolveSelectionRange(document).Duplicate;
            var content = document.Content;
            var before = content.Text ?? string.Empty;
            var relativeStart = range.Start - content.Start;
            var relativeEnd = range.End - content.Start;
            var token = SelectionToken(before, range, content.Start);
            var current = ResolveSelectionRange(document).Duplicate;
            if (!string.Equals(
                token,
                SelectionToken(document.Content.Text ?? string.Empty,
                    current, document.Content.Start),
                StringComparison.Ordinal))
                throw Failure(
                    "Word page-break target changed before dispatch.",
                    "word_page_break_target_changed", true);
            var expected = ReplaceSlice(
                before, relativeStart, relativeEnd, "\f");
            if (SameWordText(before, expected))
                return Result(true, false, Token(before));
            markDispatchPossible();
            if (IsLiveSelectionRange(current, document))
            {
                document.Activate();
                document.Application.Selection.InsertBreak(
                    Word.WdBreakType.wdPageBreak);
            }
            else current.InsertBreak(Word.WdBreakType.wdPageBreak);
            var after = document.Content.Text ?? string.Empty;
            return Result(
                SameWordText(expected, after),
                !SameWordText(before, after),
                Token(after));
        }

        public WordMutationBackendResult AddComment(
            WordCommentRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireMark(markDispatchPossible);
            var document = Document();
            var range = ResolveSelectionRange(document).Duplicate;
            var beforeComments = CommentFingerprints(document);
            var beforeToken = CommentTargetToken(document, range, beforeComments);
            var current = ResolveSelectionRange(document).Duplicate;
            if (!string.Equals(
                beforeToken,
                CommentTargetToken(
                    document, current, CommentFingerprints(document)),
                StringComparison.Ordinal))
                throw Failure(
                    "Word comment target changed before dispatch.",
                    "word_comment_target_changed", true);
            var expectedScope = current.Text ?? string.Empty;
            markDispatchPossible();
            var comment = document.Comments.Add(current, request.Text ?? string.Empty);
            var afterComments = CommentFingerprints(document);
            var added = CommentFingerprint(comment);
            var peers = new List<string>(afterComments);
            var removedAdded = peers.Remove(added);
            var verified = removedAdded &&
                document.Comments.Count == beforeComments.Count + 1 &&
                MultisetEquals(beforeComments, peers) &&
                string.Equals(comment.Range.Text ?? string.Empty,
                    request.Text ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(comment.Scope.Text ?? string.Empty,
                    expectedScope, StringComparison.Ordinal);
            return Result(
                verified, true,
                Token(string.Join("\n", afterComments.ToArray())));
        }

        private Word.Document Document()
        {
            if (!_session.IsAlive)
                throw Failure(
                    "Target Word document is not open.",
                    "word_document_closed", false);
            var document = _session.BoundDocumentObject as Word.Document;
            if (document == null)
                throw Failure(
                    "The bound Word document is unavailable.",
                    "word_document_unavailable", false);
            return document;
        }

        private static List<StoryRange> StoryRanges(
            Word.Document document, string scope)
        {
            var normalized = string.IsNullOrWhiteSpace(scope)
                ? "main" : scope.Trim().ToLowerInvariant();
            var result = new List<StoryRange>();
            if (normalized == "selection")
            {
                result.Add(new StoryRange
                {
                    Id = "selection:0",
                    Kind = "selection",
                    Range = ResolveSelectionRange(document).Duplicate
                });
                return result;
            }
            if (normalized == "main")
            {
                result.Add(new StoryRange
                {
                    Id = "main:0",
                    Kind = "main",
                    Range = document.Content.Duplicate
                });
                return result;
            }
            foreach (Word.WdStoryType type in Enum.GetValues(
                typeof(Word.WdStoryType)))
            {
                Word.Range range;
                try { range = document.StoryRanges[type]; }
                catch { continue; }
                var ordinal = 0;
                while (range != null)
                {
                    result.Add(new StoryRange
                    {
                        Id = type + ":" + ordinal,
                        Kind = type.ToString(),
                        Range = range.Duplicate
                    });
                    ordinal++;
                    try { range = range.NextStoryRange; }
                    catch { range = null; }
                }
            }
            return result;
        }

        private static Word.Range ResolveSelectionRange(Word.Document document)
        {
            if (document == null)
                throw Failure(
                    "The bound Word document is unavailable.",
                    "word_document_unavailable", false);
            try
            {
                var selection = document.Application.Selection;
                var range = selection == null ? null : selection.Range;
                if (RangeBelongsToDocument(range, document)) return range;
            }
            catch
            {
            }
            try
            {
                var window = document.ActiveWindow;
                var selection = window == null ? null : window.Selection;
                var range = selection == null ? null : selection.Range;
                if (RangeBelongsToDocument(range, document)) return range;
            }
            catch
            {
            }
            try
            {
                if (document.Windows.Count > 0)
                {
                    var selection = document.Windows[1].Selection;
                    var range = selection == null ? null : selection.Range;
                    if (RangeBelongsToDocument(range, document)) return range;
                }
            }
            catch
            {
            }
            throw Failure(
                "Select Word text first in the bound document.",
                "word_selection_unavailable", true);
        }

        private static Word.Range ResolveInsertionRange(
            Word.Document document, string location)
        {
            if (string.Equals(location, "start", StringComparison.Ordinal))
                return document.Range(document.Content.Start, document.Content.Start);
            if (string.Equals(location, "end", StringComparison.Ordinal))
            {
                var end = Math.Max(
                    document.Content.Start, document.Content.End - 1);
                return document.Range(end, end);
            }
            return ResolveSelectionRange(document);
        }

        private static WriteTarget CreateWriteTarget(
            Word.Document document, WordWriteRequest request)
        {
            var content = document.Content;
            var range = request.Mode == "paragraph"
                ? ResolveInsertionRange(document, request.Location).Duplicate
                : ResolveSelectionRange(document).Duplicate;
            var text = content.Text ?? string.Empty;
            return new WriteTarget
            {
                Range = range,
                DocumentText = text,
                RelativeStart = range.Start - content.Start,
                RelativeEnd = range.End - content.Start,
                Token = SelectionToken(text, range, content.Start)
            };
        }

        private static void EnsureWriteTarget(
            Word.Document document,
            WordWriteRequest request,
            WriteTarget expected)
        {
            var current = CreateWriteTarget(document, request);
            if (!string.Equals(
                expected.Token, current.Token, StringComparison.Ordinal))
                throw Failure(
                    "Word write target changed before dispatch.",
                    "word_write_target_changed", true);
        }

        private static Word.Range FormatRange(
            Word.Document document, WordFormatRequest request)
        {
            return request.Kind == "style" && request.Target == "document"
                ? document.Range()
                : ResolveSelectionRange(document);
        }

        private static FormatSnapshot FormatState(Word.Range range)
        {
            var snapshot = new FormatSnapshot
            {
                Start = range.Start,
                End = range.End,
                Text = range.Text ?? string.Empty,
                Style = MemberText(range, "Style"),
                Bold = range.Font.Bold,
                Italic = range.Font.Italic,
                Underline = (int)range.Font.Underline,
                FontSize = range.Font.Size,
                FontName = range.Font.Name ?? string.Empty
            };
            snapshot.Token = Token(
                snapshot.Start + ":" + snapshot.End + "\n" +
                snapshot.Text + "\n" + snapshot.Style + "\n" +
                snapshot.Bold + ":" + snapshot.Italic + ":" +
                snapshot.Underline + ":" +
                snapshot.FontSize.ToString(CultureInfo.InvariantCulture) + ":" +
                snapshot.FontName);
            return snapshot;
        }

        private static bool FormattingSatisfied(
            FormatSnapshot state, WordFormatRequest request)
        {
            if (request.Kind == "style")
                return string.Equals(
                    state.Style, request.Style, StringComparison.OrdinalIgnoreCase);
            if (request.HasBold && (state.Bold != 0) != request.Bold) return false;
            if (request.HasItalic && (state.Italic != 0) != request.Italic) return false;
            if (request.HasUnderline &&
                (state.Underline != (int)Word.WdUnderline.wdUnderlineNone) !=
                    request.Underline) return false;
            if (request.HasFontSize &&
                Math.Abs(state.FontSize - request.FontSize) > 0.01f) return false;
            if (request.HasFontName && !string.Equals(
                state.FontName, request.FontName,
                StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static IReadOnlyList<WordCommentSnapshot> Comments(
            Word.Document document)
        {
            var result = new List<WordCommentSnapshot>();
            for (var index = 1; index <= document.Comments.Count; index++)
            {
                var comment = document.Comments[index];
                result.Add(new WordCommentSnapshot
                {
                    Index = index,
                    Author = SafeString(delegate { return comment.Author; }),
                    Text = Trim(comment.Range.Text, 1000),
                    Scope = Trim(comment.Scope.Text, 500)
                });
            }
            return result;
        }

        private static List<string> TableFingerprints(Word.Document document)
        {
            var result = new List<string>();
            for (var index = 1; index <= document.Tables.Count; index++)
                result.Add(TableFingerprint(document.Tables[index]));
            return result;
        }

        private static string TableFingerprint(Word.Table table)
        {
            return Token(
                table.Rows.Count + ":" + table.Columns.Count + "\n" +
                (table.Range.Text ?? string.Empty));
        }

        private static bool TableMatches(
            Word.Table table, WordTableRequest request)
        {
            if (table.Rows.Count != request.Rows ||
                table.Columns.Count != request.Columns) return false;
            for (var row = 1; row <= request.Rows; row++)
            {
                for (var column = 1; column <= request.Columns; column++)
                {
                    var expected = string.Empty;
                    if (request.Values != null && row <= request.Values.Count &&
                        request.Values[row - 1] != null &&
                        column <= request.Values[row - 1].Count)
                        expected = Convert.ToString(
                            request.Values[row - 1][column - 1],
                            CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.Equals(
                        CleanCellText(table.Cell(row, column).Range.Text),
                        expected.Trim(), StringComparison.Ordinal)) return false;
                }
            }
            return true;
        }

        private static string TableTargetToken(
            Word.Document document, Word.Range range,
            IEnumerable<string> tables)
        {
            return Token(
                (document.Content.Text ?? string.Empty) + "\n" +
                range.Start + ":" + range.End + ":" +
                (range.Text ?? string.Empty) + "\n" +
                string.Join("\n", tables.ToArray()));
        }

        private static List<string> CommentFingerprints(
            Word.Document document)
        {
            var result = new List<string>();
            for (var index = 1; index <= document.Comments.Count; index++)
                result.Add(CommentFingerprint(document.Comments[index]));
            return result;
        }

        private static string CommentFingerprint(Word.Comment comment)
        {
            return Token(
                SafeString(delegate { return comment.Author; }) + "\n" +
                (comment.Range.Text ?? string.Empty) + "\n" +
                comment.Scope.Start + ":" + comment.Scope.End + "\n" +
                (comment.Scope.Text ?? string.Empty));
        }

        private static string CommentTargetToken(
            Word.Document document, Word.Range range,
            IEnumerable<string> comments)
        {
            return Token(
                (document.Content.Text ?? string.Empty) + "\n" +
                range.Start + ":" + range.End + ":" +
                (range.Text ?? string.Empty) + "\n" +
                string.Join("\n", comments.ToArray()));
        }

        private static bool MultisetEquals(
            IEnumerable<string> left, IEnumerable<string> right)
        {
            var first = (left ?? new string[0]).OrderBy(
                value => value, StringComparer.Ordinal).ToArray();
            var second = (right ?? new string[0]).OrderBy(
                value => value, StringComparer.Ordinal).ToArray();
            return first.SequenceEqual(second, StringComparer.Ordinal);
        }

        private static string SelectionToken(
            string documentText, Word.Range range, int contentStart)
        {
            return Token(
                (documentText ?? string.Empty) + "\n" +
                (range.Start - contentStart) + ":" +
                (range.End - contentStart) + ":" +
                (range.Text ?? string.Empty));
        }

        private static string ReplaceSlice(
            string source, int start, int end, string value)
        {
            source = source ?? string.Empty;
            start = Math.Max(0, Math.Min(source.Length, start));
            end = Math.Max(start, Math.Min(source.Length, end));
            return source.Substring(0, start) + (value ?? string.Empty) +
                source.Substring(end);
        }

        private static string InsertAt(string source, int index, string value)
        {
            source = source ?? string.Empty;
            index = Math.Max(0, Math.Min(source.Length, index));
            return source.Substring(0, index) + (value ?? string.Empty) +
                source.Substring(index);
        }

        private static bool SameWordText(string left, string right)
        {
            return string.Equals(
                CanonicalWordText(left), CanonicalWordText(right),
                StringComparison.Ordinal);
        }

        private static string CanonicalWordText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\r")
                .Replace("\n", "\r");
        }

        private static bool IsLiveSelectionRange(
            Word.Range range, Word.Document document)
        {
            try
            {
                var selection = document.Application.Selection;
                var current = selection == null ? null : selection.Range;
                return RangeBelongsToDocument(current, document) &&
                    current.Start == range.Start && current.End == range.End;
            }
            catch { return false; }
        }

        private static bool RangeBelongsToDocument(
            Word.Range range, Word.Document document)
        {
            if (range == null || document == null) return false;
            try
            {
                return string.Equals(
                    DocumentIdentity.RuntimeKey("Word", range.Document),
                    DocumentIdentity.RuntimeKey("Word", document),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string MemberText(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
                return string.Empty;
            try
            {
                var value = instance.GetType().InvokeMember(
                    memberName, BindingFlags.GetProperty,
                    null, instance, null);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        private static string CleanCellText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\a", string.Empty)
                .Replace("\a", string.Empty)
                .Trim();
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }

        private static string Token(string value)
        {
            return TextPatternEngine.Sha256(value ?? string.Empty);
        }

        private static void RequireMark(Action markDispatchPossible)
        {
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
        }

        private static WordMutationBackendResult Result(
            bool verified, bool changed, string token)
        {
            return new WordMutationBackendResult
            {
                Verified = verified,
                Changed = changed,
                StateToken = token
            };
        }

        private static WordBackendException Failure(
            string message, string code, bool retryable)
        {
            return new WordBackendException(message, code, retryable);
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private sealed class StoryRange
        {
            internal string Id { get; set; }
            internal string Kind { get; set; }
            internal Word.Range Range { get; set; }
        }

        private sealed class WriteTarget
        {
            internal Word.Range Range { get; set; }
            internal string DocumentText { get; set; }
            internal int RelativeStart { get; set; }
            internal int RelativeEnd { get; set; }
            internal string Token { get; set; }
        }

        private sealed class FormatSnapshot
        {
            internal int Start { get; set; }
            internal int End { get; set; }
            internal string Text { get; set; }
            internal string Style { get; set; }
            internal int Bold { get; set; }
            internal int Italic { get; set; }
            internal int Underline { get; set; }
            internal float FontSize { get; set; }
            internal string FontName { get; set; }
            internal string Token { get; set; }
        }
    }
}
