using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.Word
{
    public sealed class WordTextReadRequest
    {
        public string Source { get; set; }
        public int Start { get; set; }
        public bool HasEnd { get; set; }
        public int End { get; set; }
        public int MaxChars { get; set; }
    }

    public sealed class WordTextSnapshot
    {
        public string Source { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public string Text { get; set; }
    }

    public sealed class WordStoryReadRequest
    {
        public string Scope { get; set; }
    }

    public sealed class WordStorySnapshot
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public string Text { get; set; }
    }

    public sealed class WordInspectRequest
    {
        public string Kind { get; set; }
        public int MaxResults { get; set; }
        public int MaxTables { get; set; }
        public int MaxRows { get; set; }
    }

    public sealed class WordHeadingSnapshot
    {
        public string Style { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public string Text { get; set; }
    }

    public sealed class WordTableSnapshot
    {
        public int Index { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public IReadOnlyList<IReadOnlyList<string>> Values { get; set; }
    }

    public sealed class WordCommentSnapshot
    {
        public int Index { get; set; }
        public string Author { get; set; }
        public string Text { get; set; }
        public string Scope { get; set; }
    }

    public sealed class WordStatisticsSnapshot
    {
        public int Characters { get; set; }
        public int Words { get; set; }
        public int Paragraphs { get; set; }
        public int Tables { get; set; }
        public int Comments { get; set; }
    }

    public sealed class WordInspectionSnapshot
    {
        public string Kind { get; set; }
        public IReadOnlyList<WordHeadingSnapshot> Headings { get; set; }
        public IReadOnlyList<WordTableSnapshot> Tables { get; set; }
        public IReadOnlyList<WordCommentSnapshot> Comments { get; set; }
        public WordStatisticsSnapshot Statistics { get; set; }
    }

    public sealed class WordWriteRequest
    {
        public string Mode { get; set; }
        public string Text { get; set; }
        public string Location { get; set; }
    }

    public sealed class WordReplaceRequest
    {
        public string Find { get; set; }
        public string Replacement { get; set; }
        public string Scope { get; set; }
        public string Mode { get; set; }
        public bool ReplaceAll { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public int MaxReplacements { get; set; }
    }

    public sealed class WordTextReplacement
    {
        public int Index { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
    }

    public sealed class WordStoryReplacementPlan
    {
        public string StoryId { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public string ExpectedText { get; set; }
        public string ResultText { get; set; }
        public IReadOnlyList<WordTextReplacement> Replacements { get; set; }
    }

    public sealed class WordReplaceApplyRequest
    {
        public string Scope { get; set; }
        public IReadOnlyList<WordStoryReplacementPlan> Stories { get; set; }
    }

    public sealed class WordFormatRequest
    {
        public string Kind { get; set; }
        public string Style { get; set; }
        public string Target { get; set; }
        public bool HasBold { get; set; }
        public bool Bold { get; set; }
        public bool HasItalic { get; set; }
        public bool Italic { get; set; }
        public bool HasUnderline { get; set; }
        public bool Underline { get; set; }
        public bool HasFontSize { get; set; }
        public int FontSize { get; set; }
        public bool HasFontName { get; set; }
        public string FontName { get; set; }
    }

    public sealed class WordTableRequest
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
        public string Location { get; set; }
    }

    public sealed class WordPageBreakRequest
    {
    }

    public sealed class WordCommentRequest
    {
        public string Text { get; set; }
    }

    public sealed class WordMutationBackendResult
    {
        public bool Verified { get; set; }
        public bool Changed { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public string StateToken { get; set; }
    }

    public interface IWordBackend
    {
        WordTextSnapshot ReadText(WordTextReadRequest request);
        IReadOnlyList<WordStorySnapshot> ReadStories(WordStoryReadRequest request);
        WordInspectionSnapshot Inspect(WordInspectRequest request);
        WordMutationBackendResult Write(
            WordWriteRequest request, Action markDispatchPossible);
        void ApplyReplacement(
            WordReplaceApplyRequest request, Action markDispatchPossible);
        WordMutationBackendResult Format(
            WordFormatRequest request, Action markDispatchPossible);
        WordMutationBackendResult AddTable(
            WordTableRequest request, Action markDispatchPossible);
        WordMutationBackendResult InsertPageBreak(
            WordPageBreakRequest request, Action markDispatchPossible);
        WordMutationBackendResult AddComment(
            WordCommentRequest request, Action markDispatchPossible);
    }

    public sealed class WordBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public WordBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "word_backend_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum WordOutcomeStatus { Ok, Error, Unknown }
    public enum WordEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    public sealed class WordOutcome
    {
        public WordOutcomeStatus Status { get; private set; }
        public WordEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static WordOutcome Ok(
            string message, string dataJson, WordEffect effect)
        {
            return new WordOutcome
            {
                Status = WordOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static WordOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new WordOutcome
            {
                Status = WordOutcomeStatus.Error,
                Effect = WordEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "word_tool_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static WordOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new WordOutcome
            {
                Status = WordOutcomeStatus.Unknown,
                Effect = WordEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "word_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
