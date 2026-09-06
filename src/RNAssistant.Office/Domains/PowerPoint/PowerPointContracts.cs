using System;
using System.Collections.Generic;

namespace RNAssistant.Office.Domains.PowerPoint
{
    public sealed class PowerPointReadSlidesRequest
    {
        public bool HasSlideIndex { get; set; }
        public int SlideIndex { get; set; }
        public int MaxSlides { get; set; }
        public int MaxCharacters { get; set; }
        public int MaxShapesPerSlide { get; set; }
    }

    public sealed class PowerPointSlideContentSnapshot
    {
        public int SlideId { get; set; }
        public int Index { get; set; }
        public string Text { get; set; }
        public string Notes { get; set; }
    }

    public sealed class PowerPointSlideReadSnapshot
    {
        public int TotalSlides { get; set; }
        public IReadOnlyList<PowerPointSlideContentSnapshot> Slides { get; set; }
    }

    public sealed class PowerPointListRequest
    {
        public string Kind { get; set; }
        public bool HasSlideIndex { get; set; }
        public int SlideIndex { get; set; }
        public int MaxSlides { get; set; }
        public int MaxShapes { get; set; }
    }

    public sealed class PowerPointSlideSummarySnapshot
    {
        public int SlideId { get; set; }
        public int Index { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    public sealed class PowerPointShapeSnapshot
    {
        public int SlideId { get; set; }
        public int SlideIndex { get; set; }
        public int ShapeId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Text { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public sealed class PowerPointListSnapshot
    {
        public string Kind { get; set; }
        public IReadOnlyList<PowerPointSlideSummarySnapshot> Slides { get; set; }
        public IReadOnlyList<PowerPointShapeSnapshot> Shapes { get; set; }
    }

    public sealed class PowerPointTextScopeRequest
    {
        public string Scope { get; set; }
        public int SlideIndex { get; set; }
        public bool IncludeNotes { get; set; }
        public int MaxSlides { get; set; }
        public int MaxShapesPerSlide { get; set; }
        public int MaxTargets { get; set; }
    }

    public sealed class PowerPointTextTargetSnapshot
    {
        public string TargetId { get; set; }
        public int SlideId { get; set; }
        public int SlideIndex { get; set; }
        public int ShapeId { get; set; }
        public string ShapeName { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
    }

    public sealed class PowerPointAddSlideRequest
    {
        public string Title { get; set; }
        public string Body { get; set; }
    }

    public sealed class PowerPointSetTextRequest
    {
        public string Target { get; set; }
        public bool HasSlideIndex { get; set; }
        public int SlideIndex { get; set; }
        public string ShapeName { get; set; }
        public string Text { get; set; }
    }

    public sealed class PowerPointReplaceRequest
    {
        public string Find { get; set; }
        public string Replacement { get; set; }
        public string Scope { get; set; }
        public int SlideIndex { get; set; }
        public bool IncludeNotes { get; set; }
        public string Mode { get; set; }
        public bool MatchCase { get; set; }
        public bool WholeWord { get; set; }
        public bool ReplaceAll { get; set; }
        public int MaxReplacements { get; set; }
    }

    public sealed class PowerPointTextReplacement
    {
        public int Index { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
    }

    public sealed class PowerPointTextReplacementPlan
    {
        public string TargetId { get; set; }
        public int SlideId { get; set; }
        public int SlideIndex { get; set; }
        public int ShapeId { get; set; }
        public string ShapeName { get; set; }
        public string Kind { get; set; }
        public string ExpectedText { get; set; }
        public string ResultText { get; set; }
        public IReadOnlyList<PowerPointTextReplacement> Replacements { get; set; }
    }

    public sealed class PowerPointReplaceApplyRequest
    {
        public PowerPointTextScopeRequest Scope { get; set; }
        public IReadOnlyList<PowerPointTextReplacementPlan> Targets { get; set; }
    }

    public sealed class PowerPointAddObjectRequest
    {
        public string Kind { get; set; }
        public bool HasSlideIndex { get; set; }
        public int SlideIndex { get; set; }
        public bool HasText { get; set; }
        public string Text { get; set; }
        public string Path { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public IReadOnlyList<IReadOnlyList<object>> Values { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasFontSize { get; set; }
        public int FontSize { get; set; }
    }

    public sealed class PowerPointDuplicateSlideRequest
    {
        public int SlideIndex { get; set; }
    }

    public sealed class PowerPointMoveSlideRequest
    {
        public int SlideIndex { get; set; }
        public int ToIndex { get; set; }
    }

    public sealed class PowerPointMutationBackendResult
    {
        public bool Verified { get; set; }
        public bool Changed { get; set; }
        public int SlideIndex { get; set; }
        public string ShapeName { get; set; }
        public int SourceIndex { get; set; }
        public int DuplicateIndex { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public string StateToken { get; set; }
    }

    public interface IPowerPointBackend
    {
        PowerPointSlideReadSnapshot ReadSlides(PowerPointReadSlidesRequest request);
        PowerPointListSnapshot List(PowerPointListRequest request);
        IReadOnlyList<PowerPointTextTargetSnapshot> ReadTextTargets(
            PowerPointTextScopeRequest request);
        PowerPointMutationBackendResult AddSlide(
            PowerPointAddSlideRequest request, Action markDispatchPossible);
        PowerPointMutationBackendResult SetText(
            PowerPointSetTextRequest request, Action markDispatchPossible);
        IReadOnlyList<PowerPointTextTargetSnapshot> ApplyReplacement(
            PowerPointReplaceApplyRequest request, Action markDispatchPossible);
        PowerPointMutationBackendResult AddObject(
            PowerPointAddObjectRequest request, Action markDispatchPossible);
        PowerPointMutationBackendResult DuplicateSlide(
            PowerPointDuplicateSlideRequest request, Action markDispatchPossible);
        PowerPointMutationBackendResult MoveSlide(
            PowerPointMoveSlideRequest request, Action markDispatchPossible);
    }

    public sealed class PowerPointBackendException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public string DetailsJson { get; private set; }

        public PowerPointBackendException(
            string message, string errorCode, bool retryable,
            string detailsJson = null)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "powerpoint_backend_failed" : errorCode;
            Retryable = retryable;
            DetailsJson = detailsJson;
        }
    }

    public enum PowerPointOutcomeStatus { Ok, Error, Unknown }
    public enum PowerPointEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    public sealed class PowerPointOutcome
    {
        public PowerPointOutcomeStatus Status { get; private set; }
        public PowerPointEffect Effect { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public static PowerPointOutcome Ok(
            string message, string dataJson, PowerPointEffect effect)
        {
            return new PowerPointOutcome
            {
                Status = PowerPointOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        public static PowerPointOutcome Error(
            string message, string dataJson, string errorCode, bool retryable)
        {
            return new PowerPointOutcome
            {
                Status = PowerPointOutcomeStatus.Error,
                Effect = PowerPointEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "powerpoint_tool_failed" : errorCode,
                Retryable = retryable
            };
        }

        public static PowerPointOutcome Unknown(
            string message, string dataJson, string errorCode)
        {
            return new PowerPointOutcome
            {
                Status = PowerPointOutcomeStatus.Unknown,
                Effect = PowerPointEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "powerpoint_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }
}
