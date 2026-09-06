using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Office.Core;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.OfficeHosts.Identity;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace RNAssistant.OfficeHosts
{
    internal sealed class PowerPointInteropBackend : IPowerPointBackend
    {
        private readonly PowerPointDocumentSession _session;

        internal PowerPointInteropBackend(PowerPointDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public PowerPointSlideReadSnapshot ReadSlides(
            PowerPointReadSlidesRequest request)
        {
            request = request ?? new PowerPointReadSlidesRequest();
            var presentation = Presentation();
            if (!request.HasSlideIndex && presentation.Slides.Count > request.MaxSlides)
                throw Failure("Choose one slide from this presentation.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            var remaining = request.MaxCharacters;
            var slides = new List<PowerPointSlideContentSnapshot>();
            if (request.HasSlideIndex)
            {
                slides.Add(Content(ResolveSlide(presentation, request.SlideIndex), request.MaxShapesPerSlide, ref remaining));
            }
            else
            {
                var count = presentation.Slides.Count;
                for (var index = 1; index <= count; index++)
                    slides.Add(Content(presentation.Slides[index], request.MaxShapesPerSlide, ref remaining));
            }
            return new PowerPointSlideReadSnapshot { Slides = slides, TotalSlides = presentation.Slides.Count };
        }

        public PowerPointListSnapshot List(PowerPointListRequest request)
        {
            request = request ?? new PowerPointListRequest();
            var presentation = Presentation();
            if (string.Equals(request.Kind, "slides", StringComparison.Ordinal))
            {
                if (presentation.Slides.Count > request.MaxSlides)
                    throw Failure(
                        "PowerPoint presentation exceeds the bounded slide limit.",
                        "powerpoint_slide_limit_exceeded", false);
                var slides = new List<PowerPointSlideSummarySnapshot>();
                foreach (PowerPoint.Slide slide in presentation.Slides)
                    slides.Add(new PowerPointSlideSummarySnapshot
                    {
                        SlideId = slide.SlideID,
                        Index = slide.SlideIndex,
                        Title = SlideTitle(slide),
                        Text = SlideText(slide)
                    });
                return new PowerPointListSnapshot
                {
                    Kind = request.Kind,
                    Slides = slides,
                    Shapes = new PowerPointShapeSnapshot[0]
                };
            }

            var target = ResolveTargetSlide(
                presentation, request.HasSlideIndex, request.SlideIndex);
            if (target.Shapes.Count > request.MaxShapes)
                throw Failure(
                    "PowerPoint slide exceeds the bounded shape limit.",
                    "powerpoint_shape_limit_exceeded", false);
            var shapes = new List<PowerPointShapeSnapshot>();
            foreach (PowerPoint.Shape shape in target.Shapes)
                shapes.Add(ShapeSnapshot(target, shape));
            return new PowerPointListSnapshot
            {
                Kind = request.Kind,
                Slides = new PowerPointSlideSummarySnapshot[0],
                Shapes = shapes
            };
        }

        public IReadOnlyList<PowerPointTextTargetSnapshot> ReadTextTargets(
            PowerPointTextScopeRequest request)
        {
            request = request ?? new PowerPointTextScopeRequest();
            var presentation = Presentation();
            var targets = new List<PowerPointTextTargetSnapshot>();
            var remaining = request.MaxCharacters;
            var slides = SlidesForScope(presentation, request);
            foreach (var slide in slides)
            {
                if (slide.Shapes.Count > request.MaxShapesPerSlide)
                    throw Failure(
                        "PowerPoint slide exceeds the bounded shape limit.",
                        "powerpoint_shape_limit_exceeded", false);
                foreach (PowerPoint.Shape shape in slide.Shapes)
                    AddTextTarget(targets, slide, shape, "shape", request.MaxTargets, request.MaxCharacters > 0, ref remaining);
                if (!request.IncludeNotes) continue;
                var notes = slide.NotesPage.Shapes;
                if (notes.Count > request.MaxShapesPerSlide)
                    throw Failure(
                        "PowerPoint notes exceed the bounded shape limit.",
                        "powerpoint_shape_limit_exceeded", false);
                foreach (PowerPoint.Shape shape in notes)
                    AddTextTarget(targets, slide, shape, "notes", request.MaxTargets, request.MaxCharacters > 0, ref remaining);
            }
            return targets;
        }

        public PowerPointMutationBackendResult AddSlide(
            PowerPointAddSlideRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var presentation = Presentation();
            var before = Deck(presentation);
            EnsureDeck(presentation, before, "powerpoint_slide_target_changed");
            markDispatchPossible();
            var added = presentation.Slides.Add(
                presentation.Slides.Count + 1,
                PowerPoint.PpSlideLayout.ppLayoutText);
            if (added.Shapes.HasTitle == MsoTriState.msoTrue)
                added.Shapes.Title.TextFrame.TextRange.Text = request.Title ?? string.Empty;
            if (added.Shapes.Count >= 2 &&
                added.Shapes[2].HasTextFrame == MsoTriState.msoTrue)
                added.Shapes[2].TextFrame.TextRange.Text = request.Body ?? string.Empty;
            var after = Deck(presentation);
            var verified = after.Ids.Count == before.Ids.Count + 1 &&
                after.Ids.Take(before.Ids.Count).SequenceEqual(before.Ids) &&
                after.Ids[after.Ids.Count - 1] == added.SlideID &&
                PeersUnchanged(before, after) &&
                string.Equals(SlideTitle(added), request.Title ?? string.Empty,
                    StringComparison.Ordinal) &&
                string.Equals(SlideBody(added), request.Body ?? string.Empty,
                    StringComparison.Ordinal);
            return Result(verified, true, after.Token, added.SlideIndex);
        }

        public PowerPointMutationBackendResult SetText(
            PowerPointSetTextRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var presentation = Presentation();
            var target = ResolveTextTarget(presentation, request);
            if (string.Equals(target.Text, request.Text ?? string.Empty,
                    StringComparison.Ordinal))
                return new PowerPointMutationBackendResult
                {
                    Verified = true,
                    Changed = false,
                    SlideIndex = target.SlideIndex,
                    ShapeName = target.Shape.Name,
                    StateToken = target.Token
                };
            var current = ResolveTextTarget(
                presentation, target.SlideId, target.ShapeId, target.Kind);
            if (!string.Equals(target.Token, current.Token,
                    StringComparison.Ordinal))
                throw Failure(
                    "PowerPoint text target changed before dispatch.",
                    "powerpoint_text_target_changed", true);
            markDispatchPossible();
            current.Shape.TextFrame.TextRange.Text = request.Text ?? string.Empty;
            var after = ResolveTextTarget(
                presentation, target.SlideId, target.ShapeId, target.Kind);
            return new PowerPointMutationBackendResult
            {
                Verified = string.Equals(
                    after.Text, request.Text ?? string.Empty,
                    StringComparison.Ordinal),
                Changed = !string.Equals(
                    target.Text, after.Text, StringComparison.Ordinal),
                SlideIndex = after.SlideIndex,
                ShapeName = after.Shape.Name,
                StateToken = after.Token
            };
        }

        public IReadOnlyList<PowerPointTextTargetSnapshot> ApplyReplacement(
            PowerPointReplaceApplyRequest request, Action markDispatchPossible)
        {
            if (request == null || request.Scope == null || request.Targets == null)
                throw Failure(
                    "PowerPoint replacement plan is missing.",
                    "powerpoint_replace_plan_invalid", false);
            RequireMark(markDispatchPossible);
            var current = ReadTextTargets(request.Scope);
            EnsureReplacementScope(current, request.Targets);
            var mutationCount = request.Targets.Sum(plan =>
                plan == null || plan.Replacements == null
                    ? 0 : plan.Replacements.Count);
            if (mutationCount == 0) return current;
            markDispatchPossible();
            var presentation = Presentation();
            foreach (var plan in request.Targets)
            {
                if (plan == null || plan.Replacements == null ||
                    plan.Replacements.Count == 0) continue;
                var target = ResolveTextTarget(
                    presentation, plan.SlideId, plan.ShapeId, plan.Kind);
                for (var index = plan.Replacements.Count - 1; index >= 0; index--)
                {
                    var edit = plan.Replacements[index];
                    if (edit == null || edit.Index < 0 || edit.Length < 0 ||
                        edit.Index + edit.Length >
                            (plan.ExpectedText ?? string.Empty).Length)
                        throw Failure(
                            "PowerPoint replacement edit is outside its target.",
                            "powerpoint_replace_plan_invalid", false);
                    target.Shape.TextFrame.TextRange.Characters(
                        edit.Index + 1, edit.Length).Text = edit.Text ?? string.Empty;
                }
            }
            return ReadTextTargets(request.Scope);
        }

        public PowerPointMutationBackendResult AddObject(
            PowerPointAddObjectRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var presentation = Presentation();
            var slide = ResolveTargetSlide(
                presentation, request.HasSlideIndex, request.SlideIndex);
            var before = SlideShapes(slide);
            var current = ResolveSlideById(presentation, before.SlideId);
            if (!string.Equals(before.Token, SlideShapes(current).Token,
                    StringComparison.Ordinal))
                throw Failure(
                    "PowerPoint object target changed before dispatch.",
                    "powerpoint_object_target_changed", true);
            markDispatchPossible();
            PowerPoint.Shape added;
            if (string.Equals(request.Kind, "textbox", StringComparison.Ordinal))
            {
                added = current.Shapes.AddTextbox(
                    MsoTextOrientation.msoTextOrientationHorizontal,
                    request.Left, request.Top, request.Width, request.Height);
                added.TextFrame.TextRange.Text = request.Text ?? string.Empty;
                if (request.HasFontSize)
                    added.TextFrame.TextRange.Font.Size = request.FontSize;
            }
            else if (string.Equals(request.Kind, "picture", StringComparison.Ordinal))
            {
                added = current.Shapes.AddPicture(
                    request.Path, MsoTriState.msoFalse, MsoTriState.msoTrue,
                    request.Left, request.Top, request.Width, request.Height);
            }
            else
            {
                added = current.Shapes.AddTable(
                    request.Rows, request.Columns,
                    request.Left, request.Top, request.Width, request.Height);
                WriteTable(added, request);
            }
            var after = SlideShapes(current);
            var peers = new Dictionary<int, string>(after.Fingerprints);
            var removed = peers.Remove(added.Id);
            var verified = removed &&
                after.Fingerprints.Count == before.Fingerprints.Count + 1 &&
                DictionaryEqual(before.Fingerprints, peers) &&
                ObjectMatches(added, request);
            return new PowerPointMutationBackendResult
            {
                Verified = verified,
                Changed = true,
                SlideIndex = current.SlideIndex,
                ShapeName = added.Name,
                Rows = request.Kind == "table" ? request.Rows : 0,
                Columns = request.Kind == "table" ? request.Columns : 0,
                StateToken = after.Token
            };
        }

        public PowerPointMutationBackendResult DuplicateSlide(
            PowerPointDuplicateSlideRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var presentation = Presentation();
            var source = ResolveSlide(presentation, request.SlideIndex);
            var sourceId = source.SlideID;
            var sourceContent = SlideContentToken(source);
            var before = Deck(presentation);
            EnsureDeck(presentation, before, "powerpoint_slide_target_changed");
            markDispatchPossible();
            var duplicated = source.Duplicate();
            var duplicate = duplicated[1];
            var after = Deck(presentation);
            var expected = new List<int>(before.Ids);
            expected.Insert(request.SlideIndex, duplicate.SlideID);
            var peers = new Dictionary<int, string>(after.Fingerprints);
            peers.Remove(duplicate.SlideID);
            var verified = after.Ids.SequenceEqual(expected) &&
                DictionaryEqual(before.Fingerprints, peers) &&
                string.Equals(sourceContent, SlideContentToken(duplicate),
                    StringComparison.Ordinal);
            return new PowerPointMutationBackendResult
            {
                Verified = verified,
                Changed = true,
                SourceIndex = IndexOfSlide(after.Ids, sourceId) + 1,
                DuplicateIndex = IndexOfSlide(after.Ids, duplicate.SlideID) + 1,
                StateToken = after.Token
            };
        }

        public PowerPointMutationBackendResult MoveSlide(
            PowerPointMoveSlideRequest request, Action markDispatchPossible)
        {
            RequireMark(markDispatchPossible);
            var presentation = Presentation();
            var source = ResolveSlide(presentation, request.SlideIndex);
            if (request.ToIndex < 1 || request.ToIndex > presentation.Slides.Count)
                throw Failure(
                    "toIndex is outside the presentation: " + request.ToIndex + ".",
                    "powerpoint_slide_index_invalid", false);
            var before = Deck(presentation);
            if (request.SlideIndex == request.ToIndex)
                return Result(true, false, before.Token, request.ToIndex);
            EnsureDeck(presentation, before, "powerpoint_slide_target_changed");
            var expected = new List<int>(before.Ids);
            var movedId = expected[request.SlideIndex - 1];
            expected.RemoveAt(request.SlideIndex - 1);
            expected.Insert(request.ToIndex - 1, movedId);
            markDispatchPossible();
            source.MoveTo(request.ToIndex);
            var after = Deck(presentation);
            return Result(
                after.Ids.SequenceEqual(expected) && PeersUnchanged(before, after),
                true, after.Token, request.ToIndex);
        }

        private PowerPoint.Presentation Presentation()
        {
            if (!_session.IsAlive)
                throw Failure(
                    "Target PowerPoint presentation is not open.",
                    "powerpoint_presentation_closed", false);
            return _session.Presentation;
        }

        private PowerPoint.Slide ResolveTargetSlide(
            PowerPoint.Presentation presentation, bool hasIndex, int slideIndex)
        {
            if (hasIndex) return ResolveSlide(presentation, slideIndex);
            var active = ActiveSlide(presentation);
            return active ?? ResolveSlide(presentation, 1);
        }

        private PowerPoint.Slide ActiveSlide(
            PowerPoint.Presentation presentation)
        {
            try
            {
                var window = _session.Window;
                if (window == null || window.View == null) return null;
                var slide = window.View.Slide as PowerPoint.Slide;
                return BelongsTo(slide, presentation) ? slide : null;
            }
            catch { return null; }
        }

        private PowerPoint.Shape SelectedShape(
            PowerPoint.Presentation presentation)
        {
            try
            {
                var window = _session.Window;
                var selection = window == null ? null : window.Selection;
                if (selection == null ||
                    selection.Type != PowerPoint.PpSelectionType.ppSelectionShapes ||
                    selection.ShapeRange.Count < 1) return null;
                var shape = selection.ShapeRange[1];
                return BelongsTo(shape == null ? null : shape.Parent as PowerPoint.Slide,
                    presentation) ? shape : null;
            }
            catch { return null; }
        }

        private static PowerPoint.Slide ResolveSlide(
            PowerPoint.Presentation presentation, int slideIndex)
        {
            if (presentation.Slides.Count == 0)
                throw Failure(
                    "Presentation has no slides.",
                    "powerpoint_presentation_empty", false);
            if (slideIndex < 1 || slideIndex > presentation.Slides.Count)
                throw Failure(
                    "slideIndex is outside the presentation: " + slideIndex + ".",
                    "powerpoint_slide_index_invalid", false);
            return presentation.Slides[slideIndex];
        }

        private static PowerPoint.Slide ResolveSlideById(
            PowerPoint.Presentation presentation, int slideId)
        {
            foreach (PowerPoint.Slide slide in presentation.Slides)
                if (slide.SlideID == slideId) return slide;
            throw Failure(
                "PowerPoint slide changed before dispatch.",
                "powerpoint_slide_target_changed", true);
        }

        private TextTarget ResolveTextTarget(
            PowerPoint.Presentation presentation, PowerPointSetTextRequest request)
        {
            PowerPoint.Slide slide;
            PowerPoint.Shape shape;
            var kind = request.Target;
            if (string.Equals(kind, "notes", StringComparison.Ordinal))
            {
                slide = ResolveTargetSlide(
                    presentation, request.HasSlideIndex, request.SlideIndex);
                shape = ResolveNotesTextShape(slide);
                if (shape == null)
                    throw Failure(
                        "Could not find speaker notes text shape.",
                        "powerpoint_notes_shape_missing", false);
            }
            else if (!string.IsNullOrWhiteSpace(request.ShapeName))
            {
                slide = ResolveTargetSlide(
                    presentation, request.HasSlideIndex, request.SlideIndex);
                shape = ResolveShape(slide, request.ShapeName);
                if (shape == null)
                    throw Failure(
                        "Shape not found.", "powerpoint_shape_missing", false);
            }
            else
            {
                shape = SelectedShape(presentation);
                if (shape == null)
                    throw Failure(
                        "Shape not found.", "powerpoint_shape_missing", false);
                slide = shape.Parent as PowerPoint.Slide;
            }
            if (shape.HasTextFrame != MsoTriState.msoTrue)
                throw Failure(
                    "Shape has no text frame.",
                    "powerpoint_shape_has_no_text", false);
            return CreateTextTarget(slide, shape, kind);
        }

        private static TextTarget ResolveTextTarget(
            PowerPoint.Presentation presentation,
            int slideId, int shapeId, string kind)
        {
            var slide = ResolveSlideById(presentation, slideId);
            var shapes = string.Equals(kind, "notes", StringComparison.Ordinal)
                ? slide.NotesPage.Shapes : slide.Shapes;
            foreach (PowerPoint.Shape shape in shapes)
                if (shape.Id == shapeId)
                    return CreateTextTarget(slide, shape, kind);
            throw Failure(
                "PowerPoint text target changed before dispatch.",
                "powerpoint_text_target_changed", true);
        }

        private static TextTarget CreateTextTarget(
            PowerPoint.Slide slide, PowerPoint.Shape shape, string kind)
        {
            var text = ShapeText(shape);
            return new TextTarget
            {
                SlideId = slide.SlideID,
                SlideIndex = slide.SlideIndex,
                ShapeId = shape.Id,
                Kind = kind,
                Shape = shape,
                Text = text,
                Token = Token(slide.SlideID + ":" + slide.SlideIndex + ":" +
                    kind + ":" + shape.Id + ":" + shape.Name + "\n" + text)
            };
        }

        private static IReadOnlyList<PowerPoint.Slide> SlidesForScope(
            PowerPoint.Presentation presentation, PowerPointTextScopeRequest request)
        {
            if (request.SlideIndex > 0)
                return new[] { ResolveSlide(presentation, request.SlideIndex) };
            if (presentation.Slides.Count > request.MaxSlides)
                throw Failure(
                    "PowerPoint presentation exceeds the bounded slide limit.",
                    "powerpoint_slide_limit_exceeded", false);
            var slides = new List<PowerPoint.Slide>();
            foreach (PowerPoint.Slide slide in presentation.Slides) slides.Add(slide);
            return slides;
        }

        private static void AddTextTarget(
            ICollection<PowerPointTextTargetSnapshot> targets,
            PowerPoint.Slide slide, PowerPoint.Shape shape,
            string kind, int maxTargets, bool boundedCapture, ref int remaining)
        {
            string text;
            if (boundedCapture)
            {
                // Exact search capture must not suppress COM failures or materialize
                // an unbounded Text value before checking the native range extent.
                if (shape.HasTextFrame != MsoTriState.msoTrue || shape.TextFrame.HasText != MsoTriState.msoTrue) return;
                if (targets.Count >= maxTargets)
                    throw Failure("PowerPoint text scope exceeds the target limit.", "powerpoint_text_target_limit_exceeded", false);
                var range = shape.TextFrame.TextRange;
                var length = range.Length;
                if (length < 0 || length > remaining)
                    throw Failure("Choose a smaller PowerPoint search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                text = range.Text;
                if (text == null || text.Length > remaining)
                    throw Failure("PowerPoint source changed during capture.", "powerpoint_read_snapshot_invalid", false);
                remaining -= text.Length;
            }
            else text = ShapeText(shape);
            if (string.IsNullOrEmpty(text)) return;
            if (targets.Count >= maxTargets)
                throw Failure(
                    "PowerPoint text scope exceeds the bounded target limit.",
                    "powerpoint_text_target_limit_exceeded", false);
            targets.Add(new PowerPointTextTargetSnapshot
            {
                TargetId = TargetId(slide.SlideID, kind, shape.Id),
                SlideId = slide.SlideID,
                SlideIndex = slide.SlideIndex,
                ShapeId = shape.Id,
                ShapeName = shape.Name,
                Kind = kind,
                Text = text
            });
        }

        private static void EnsureReplacementScope(
            IReadOnlyList<PowerPointTextTargetSnapshot> current,
            IReadOnlyList<PowerPointTextReplacementPlan> plans)
        {
            if (current == null || current.Count != plans.Count)
                throw Failure(
                    "PowerPoint replacement scope changed before dispatch.",
                    "powerpoint_replace_target_changed", true);
            for (var index = 0; index < current.Count; index++)
            {
                var target = current[index];
                var plan = plans[index];
                if (plan == null ||
                    !string.Equals(target.TargetId, plan.TargetId,
                        StringComparison.Ordinal) ||
                    target.SlideId != plan.SlideId ||
                    target.SlideIndex != plan.SlideIndex ||
                    target.ShapeId != plan.ShapeId ||
                    !string.Equals(target.ShapeName, plan.ShapeName,
                        StringComparison.Ordinal) ||
                    !string.Equals(target.Kind, plan.Kind, StringComparison.Ordinal) ||
                    !string.Equals(target.Text, plan.ExpectedText,
                        StringComparison.Ordinal))
                    throw Failure(
                        "PowerPoint replacement scope changed before dispatch.",
                        "powerpoint_replace_target_changed", true);
            }
        }

        private static PowerPointSlideContentSnapshot Content(
            PowerPoint.Slide slide, int maxShapes, ref int remaining)
        {
            return new PowerPointSlideContentSnapshot
            {
                SlideId = slide.SlideID,
                Index = slide.SlideIndex,
                Text = CaptureShapeText(slide.Shapes, maxShapes, ref remaining),
                Notes = CaptureShapeText(slide.NotesPage.Shapes, maxShapes, ref remaining)
            };
        }

        private static string CaptureShapeText(PowerPoint.Shapes shapes, int maxShapes, ref int remaining)
        {
            if (shapes.Count > maxShapes)
                throw Failure("PowerPoint source exceeds the shape limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in shapes)
            {
                if (shape.HasTextFrame != MsoTriState.msoTrue ||
                    shape.TextFrame.HasText != MsoTriState.msoTrue) continue;
                var range = shape.TextFrame.TextRange;
                var length = range.Length;
                if (length < 0 || (long)length + Environment.NewLine.Length > remaining)
                    throw Failure("PowerPoint source exceeds the character limit.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                var text = range.Text;
                if (text == null || (long)text.Length + Environment.NewLine.Length > remaining)
                    throw Failure("PowerPoint source changed during capture.", "powerpoint_read_snapshot_invalid", false);
                builder.AppendLine(text);
                remaining -= text.Length + Environment.NewLine.Length;
            }
            return builder.ToString();
        }

        private static PowerPointShapeSnapshot ShapeSnapshot(
            PowerPoint.Slide slide, PowerPoint.Shape shape)
        {
            return new PowerPointShapeSnapshot
            {
                SlideId = slide.SlideID,
                SlideIndex = slide.SlideIndex,
                ShapeId = shape.Id,
                Name = shape.Name,
                Type = shape.Type.ToString(),
                Text = ShapeText(shape),
                Left = shape.Left,
                Top = shape.Top,
                Width = shape.Width,
                Height = shape.Height
            };
        }

        private static string SlideText(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in slide.Shapes)
            {
                var text = ShapeText(shape);
                if (!string.IsNullOrEmpty(text)) builder.AppendLine(text);
            }
            return builder.ToString();
        }

        private static string NotesText(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in slide.NotesPage.Shapes)
            {
                var text = ShapeText(shape);
                if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text);
            }
            return builder.ToString();
        }

        private static string SlideTitle(PowerPoint.Slide slide)
        {
            try
            {
                return slide.Shapes.HasTitle == MsoTriState.msoTrue
                    ? ShapeText(slide.Shapes.Title) : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string SlideBody(PowerPoint.Slide slide)
        {
            try
            {
                return slide.Shapes.Count >= 2
                    ? ShapeText(slide.Shapes[2]) : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ShapeText(PowerPoint.Shape shape)
        {
            try
            {
                return shape != null &&
                    shape.HasTextFrame == MsoTriState.msoTrue &&
                    shape.TextFrame.HasText == MsoTriState.msoTrue
                    ? shape.TextFrame.TextRange.Text ?? string.Empty
                    : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static PowerPoint.Shape ResolveNotesTextShape(
            PowerPoint.Slide slide)
        {
            try
            {
                var placeholders = slide.NotesPage.Shapes.Placeholders;
                if (placeholders.Count >= 2 &&
                    placeholders[2].HasTextFrame == MsoTriState.msoTrue)
                    return placeholders[2];
            }
            catch { }
            foreach (PowerPoint.Shape shape in slide.NotesPage.Shapes)
                if (shape.HasTextFrame == MsoTriState.msoTrue) return shape;
            return null;
        }

        private static PowerPoint.Shape ResolveShape(
            PowerPoint.Slide slide, string name)
        {
            foreach (PowerPoint.Shape shape in slide.Shapes)
                if (string.Equals(shape.Name, name,
                    StringComparison.OrdinalIgnoreCase)) return shape;
            return null;
        }

        private static bool BelongsTo(
            PowerPoint.Slide slide, PowerPoint.Presentation presentation)
        {
            if (slide == null || presentation == null) return false;
            try
            {
                var parent = slide.Parent as PowerPoint.Presentation;
                return parent != null && string.Equals(
                    DocumentIdentity.RuntimeKey("PowerPoint", parent),
                    DocumentIdentity.RuntimeKey("PowerPoint", presentation),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static DeckState Deck(PowerPoint.Presentation presentation)
        {
            var ids = new List<int>();
            var fingerprints = new Dictionary<int, string>();
            foreach (PowerPoint.Slide slide in presentation.Slides)
            {
                ids.Add(slide.SlideID);
                fingerprints.Add(slide.SlideID, SlideFingerprint(slide));
            }
            return new DeckState
            {
                Ids = ids,
                Fingerprints = fingerprints,
                Token = Token(string.Join(",", ids.Select(
                    id => id.ToString(CultureInfo.InvariantCulture)).ToArray()) +
                    "\n" + string.Join("\n", ids.Select(
                        id => fingerprints[id]).ToArray()))
            };
        }

        private static SlideShapeState SlideShapes(PowerPoint.Slide slide)
        {
            var fingerprints = new Dictionary<int, string>();
            foreach (PowerPoint.Shape shape in slide.Shapes)
                fingerprints.Add(shape.Id, ShapeFingerprint(shape));
            return new SlideShapeState
            {
                SlideId = slide.SlideID,
                Fingerprints = fingerprints,
                Token = Token(slide.SlideID + "\n" + string.Join("\n",
                    fingerprints.OrderBy(pair => pair.Key).Select(pair =>
                        pair.Key + ":" + pair.Value).ToArray()))
            };
        }

        private static void EnsureDeck(
            PowerPoint.Presentation presentation, DeckState expected,
            string errorCode)
        {
            var current = Deck(presentation);
            if (!string.Equals(expected.Token, current.Token,
                    StringComparison.Ordinal))
                throw Failure(
                    "PowerPoint presentation changed before dispatch.",
                    errorCode, true);
        }

        private static bool PeersUnchanged(DeckState before, DeckState after)
        {
            foreach (var pair in before.Fingerprints)
            {
                string current;
                if (!after.Fingerprints.TryGetValue(pair.Key, out current) ||
                    !string.Equals(pair.Value, current, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static string SlideFingerprint(PowerPoint.Slide slide)
        {
            return Token(slide.SlideID + "\n" + SlideContentToken(slide));
        }

        private static string SlideContentToken(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in slide.Shapes)
                builder.Append(ShapeContentFingerprint(shape)).Append('\n');
            builder.Append("notes\n").Append(NotesText(slide));
            return Token(builder.ToString());
        }

        private static string ShapeFingerprint(PowerPoint.Shape shape)
        {
            return Token(shape.Id + ":" + ShapeContentFingerprint(shape));
        }

        private static string ShapeContentFingerprint(PowerPoint.Shape shape)
        {
            return Token(shape.Name + ":" + shape.Type + ":" +
                shape.Left.ToString(CultureInfo.InvariantCulture) + ":" +
                shape.Top.ToString(CultureInfo.InvariantCulture) + ":" +
                shape.Width.ToString(CultureInfo.InvariantCulture) + ":" +
                shape.Height.ToString(CultureInfo.InvariantCulture) + "\n" +
                ShapeText(shape));
        }

        private static void WriteTable(
            PowerPoint.Shape shape, PowerPointAddObjectRequest request)
        {
            if (request.Values == null) return;
            for (var row = 1;
                row <= request.Rows && row <= request.Values.Count; row++)
            {
                var values = request.Values[row - 1];
                if (values == null) continue;
                for (var column = 1;
                    column <= request.Columns && column <= values.Count; column++)
                    shape.Table.Cell(row, column).Shape.TextFrame.TextRange.Text =
                        Convert.ToString(values[column - 1],
                            CultureInfo.InvariantCulture);
            }
        }

        private static bool ObjectMatches(
            PowerPoint.Shape shape, PowerPointAddObjectRequest request)
        {
            if (shape == null || !Near(shape.Left, request.Left) ||
                !Near(shape.Top, request.Top) || !Near(shape.Width, request.Width) ||
                !Near(shape.Height, request.Height)) return false;
            if (request.Kind == "textbox")
                return shape.HasTextFrame == MsoTriState.msoTrue &&
                    string.Equals(ShapeText(shape), request.Text ?? string.Empty,
                        StringComparison.Ordinal) &&
                    (!request.HasFontSize ||
                     Near(shape.TextFrame.TextRange.Font.Size, request.FontSize));
            if (request.Kind == "picture")
                return shape.Type == MsoShapeType.msoPicture ||
                    shape.Type == MsoShapeType.msoLinkedPicture;
            if (shape.HasTable != MsoTriState.msoTrue ||
                shape.Table.Rows.Count != request.Rows ||
                shape.Table.Columns.Count != request.Columns) return false;
            if (request.Values == null) return true;
            for (var row = 1; row <= request.Values.Count; row++)
            {
                var values = request.Values[row - 1];
                if (values == null) continue;
                for (var column = 1; column <= values.Count; column++)
                    if (!string.Equals(
                        ShapeText(shape.Table.Cell(row, column).Shape),
                        Convert.ToString(values[column - 1],
                            CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool Near(double actual, double expected)
        {
            return Math.Abs(actual - expected) <= 0.25;
        }

        private static bool DictionaryEqual(
            IDictionary<int, string> expected,
            IDictionary<int, string> actual)
        {
            if (expected.Count != actual.Count) return false;
            foreach (var pair in expected)
            {
                string value;
                if (!actual.TryGetValue(pair.Key, out value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static int IndexOfSlide(IReadOnlyList<int> ids, int slideId)
        {
            for (var index = 0; index < ids.Count; index++)
                if (ids[index] == slideId) return index;
            return -1;
        }

        private static string TargetId(int slideId, string kind, int shapeId)
        {
            return slideId.ToString(CultureInfo.InvariantCulture) + ":" +
                kind + ":" + shapeId.ToString(CultureInfo.InvariantCulture);
        }

        private static string Token(string value)
        {
            return TextPatternEngine.Sha256(value ?? string.Empty);
        }

        private static PowerPointMutationBackendResult Result(
            bool verified, bool changed, string token, int slideIndex)
        {
            return new PowerPointMutationBackendResult
            {
                Verified = verified,
                Changed = changed,
                SlideIndex = slideIndex,
                StateToken = token
            };
        }

        private static void RequireMark(Action markDispatchPossible)
        {
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
        }

        private static PowerPointBackendException Failure(
            string message, string code, bool retryable)
        {
            return new PowerPointBackendException(message, code, retryable);
        }

        private sealed class TextTarget
        {
            public int SlideId { get; set; }
            public int SlideIndex { get; set; }
            public int ShapeId { get; set; }
            public string Kind { get; set; }
            public PowerPoint.Shape Shape { get; set; }
            public string Text { get; set; }
            public string Token { get; set; }
        }

        private sealed class DeckState
        {
            public List<int> Ids { get; set; }
            public Dictionary<int, string> Fingerprints { get; set; }
            public string Token { get; set; }
        }

        private sealed class SlideShapeState
        {
            public int SlideId { get; set; }
            public Dictionary<int, string> Fingerprints { get; set; }
            public string Token { get; set; }
        }
    }
}
