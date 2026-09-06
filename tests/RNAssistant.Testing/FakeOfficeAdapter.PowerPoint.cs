using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.PowerPoint;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter
    {
        internal const string PowerPointReadSlidesOperation =
            "powerpoint.read_slides.direct";
        internal const string PowerPointListOperation =
            "powerpoint.list.direct";
        internal const string PowerPointReadTextOperation =
            "powerpoint.read_text.direct";
        internal const string PowerPointAddSlideOperation =
            "powerpoint.add_slide.direct";
        internal const string PowerPointSetTextOperation =
            "powerpoint.set_text.direct";
        internal const string PowerPointReplaceOperation =
            "powerpoint.replace.direct";
        internal const string PowerPointAddObjectOperation =
            "powerpoint.add_object.direct";
        internal const string PowerPointDuplicateOperation =
            "powerpoint.duplicate.direct";
        internal const string PowerPointMoveOperation =
            "powerpoint.move.direct";

        public int PowerPointSourceMaterializationCount { get; private set; }

        public PowerPointSlideReadSnapshot ReadSlides(
            PowerPointReadSlidesRequest request)
        {
            BeginPowerPointBackendCall(PowerPointReadSlidesOperation);
            request = request ?? new PowerPointReadSlidesRequest();
            IEnumerable<FakeSlide> slides = _slides;
            if (request.HasSlideIndex)
                slides = new[] { PowerPointSlide(request.SlideIndex) };
            else if (_slides.Count > request.MaxSlides)
                throw new PowerPointBackendException("Choose one slide.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            long characters = 0;
            var selected = slides.ToArray();
            foreach (var slide in selected)
            {
                if (2 + slide.Shapes.Count > request.MaxShapesPerSlide)
                    throw new PowerPointBackendException("Too many shapes.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
                characters += (slide.Notes ?? string.Empty).Length;
                foreach (var value in new[] { slide.Title, slide.Body }.Concat(slide.Shapes.Select(shape => shape.Text)))
                    if (!string.IsNullOrEmpty(value)) characters += (long)value.Length + Environment.NewLine.Length;
            }
            if (characters > request.MaxCharacters)
                throw new PowerPointBackendException("Choose a smaller source.", "RESOURCE_SNAPSHOT_TOO_LARGE", false);
            PowerPointSourceMaterializationCount++;
            var captured = selected.Select(PowerPointContent).ToArray();
            return new PowerPointSlideReadSnapshot
            {
                TotalSlides = _slides.Count,
                Slides = captured
            };
        }

        public PowerPointListSnapshot List(PowerPointListRequest request)
        {
            BeginPowerPointBackendCall(PowerPointListOperation);
            request = request ?? new PowerPointListRequest();
            if (string.Equals(request.Kind, "slides", StringComparison.Ordinal))
                return new PowerPointListSnapshot
                {
                    Kind = request.Kind,
                    Slides = _slides.Select((slide, index) =>
                        new PowerPointSlideSummarySnapshot
                        {
                            SlideId = slide.Id,
                            Index = index + 1,
                            Title = slide.Title ?? string.Empty,
                            Text = PowerPointSlideText(slide)
                        }).ToArray(),
                    Shapes = new PowerPointShapeSnapshot[0]
                };
            var target = request.HasSlideIndex
                ? PowerPointSlide(request.SlideIndex) : LastOrNewSlide();
            var targetIndex = _slides.IndexOf(target) + 1;
            var shapes = new List<PowerPointShapeSnapshot>
            {
                PowerPointShapeSnapshot(target, targetIndex,
                    PowerPointTitleId(target), "Title 1", "msoPlaceholder",
                    target.Title, 0, 0, 640, 60),
                PowerPointShapeSnapshot(target, targetIndex,
                    PowerPointBodyId(target), "Content Placeholder 2",
                    "msoPlaceholder", target.Body, 0, 70, 640, 300)
            };
            shapes.AddRange((target.Shapes ?? new List<FakePowerPointShape>())
                .Select(shape => PowerPointShapeSnapshot(
                    target, targetIndex, shape.Id, shape.Name, shape.Kind,
                    shape.Text, shape.Left, shape.Top, shape.Width, shape.Height)));
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
            BeginPowerPointBackendCall(PowerPointReadTextOperation);
            return PowerPointTextTargets(request);
        }

        public PowerPointMutationBackendResult AddSlide(
            PowerPointAddSlideRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointAddSlideOperation);
            markDispatchPossible();
            var slide = new FakeSlide
            {
                Id = _nextPowerPointSlideId++,
                Title = request == null ? string.Empty : request.Title ?? string.Empty,
                Body = request == null ? string.Empty : request.Body ?? string.Empty,
                Shapes = new List<FakePowerPointShape>()
            };
            _slides.Add(slide);
            ThrowAfterPowerPointMutation();
            return PowerPointMutationResult(true, _slides.Count);
        }

        public PowerPointMutationBackendResult SetText(
            PowerPointSetTextRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointSetTextOperation);
            request = request ?? new PowerPointSetTextRequest();
            var slide = request.HasSlideIndex
                ? PowerPointSlide(request.SlideIndex) : LastOrNewSlide();
            var desired = request.Text ?? string.Empty;
            string before;
            Action apply;
            var shapeName = request.ShapeName;
            if (string.Equals(request.Target, "notes", StringComparison.Ordinal))
            {
                before = slide.Notes ?? string.Empty;
                shapeName = "Notes Placeholder 2";
                apply = delegate { slide.Notes = desired; };
            }
            else if (string.Equals(shapeName, "Title 1",
                StringComparison.OrdinalIgnoreCase))
            {
                before = slide.Title ?? string.Empty;
                apply = delegate { slide.Title = desired; };
            }
            else
            {
                var shape = (slide.Shapes ?? new List<FakePowerPointShape>())
                    .FirstOrDefault(item => string.Equals(
                        item.Name, shapeName, StringComparison.OrdinalIgnoreCase));
                if (shape != null)
                {
                    before = shape.Text ?? string.Empty;
                    shapeName = shape.Name;
                    apply = delegate { shape.Text = desired; };
                }
                else
                {
                    before = slide.Body ?? string.Empty;
                    shapeName = "Content Placeholder 2";
                    apply = delegate { slide.Body = desired; };
                }
            }
            if (string.Equals(before, desired, StringComparison.Ordinal))
                return new PowerPointMutationBackendResult
                {
                    Verified = true,
                    Changed = false,
                    SlideIndex = _slides.IndexOf(slide) + 1,
                    ShapeName = shapeName,
                    StateToken = PowerPointStateToken()
                };
            markDispatchPossible();
            apply();
            ThrowAfterPowerPointMutation();
            return new PowerPointMutationBackendResult
            {
                Verified = true,
                Changed = true,
                SlideIndex = _slides.IndexOf(slide) + 1,
                ShapeName = shapeName,
                StateToken = PowerPointStateToken()
            };
        }

        public IReadOnlyList<PowerPointTextTargetSnapshot> ApplyReplacement(
            PowerPointReplaceApplyRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointReplaceOperation);
            if (request == null || request.Scope == null || request.Targets == null)
                throw new PowerPointBackendException(
                    "fake PowerPoint replacement plan missing",
                    "powerpoint_replace_plan_invalid", false);
            var current = PowerPointTextTargets(request.Scope);
            if (current.Count != request.Targets.Count)
                throw new PowerPointBackendException(
                    "fake PowerPoint replacement target changed",
                    "powerpoint_replace_target_changed", true);
            for (var index = 0; index < current.Count; index++)
                if (!string.Equals(current[index].TargetId,
                        request.Targets[index].TargetId, StringComparison.Ordinal) ||
                    !string.Equals(current[index].Text,
                        request.Targets[index].ExpectedText, StringComparison.Ordinal))
                    throw new PowerPointBackendException(
                        "fake PowerPoint replacement target changed",
                        "powerpoint_replace_target_changed", true);
            if (!request.Targets.Any(plan => plan != null &&
                plan.Replacements != null && plan.Replacements.Count > 0))
                return current;
            markDispatchPossible();
            foreach (var plan in request.Targets)
            {
                if (plan == null || plan.Replacements == null ||
                    plan.Replacements.Count == 0) continue;
                SetPowerPointTarget(plan, plan.ResultText ?? string.Empty);
            }
            ThrowAfterPowerPointMutation();
            return PowerPointTextTargets(request.Scope);
        }

        public PowerPointMutationBackendResult AddObject(
            PowerPointAddObjectRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointAddObjectOperation);
            request = request ?? new PowerPointAddObjectRequest();
            var slide = request.HasSlideIndex
                ? PowerPointSlide(request.SlideIndex) : LastOrNewSlide();
            if (slide.Shapes == null) slide.Shapes = new List<FakePowerPointShape>();
            markDispatchPossible();
            var shape = new FakePowerPointShape
            {
                Id = _nextPowerPointShapeId++,
                Name = "Shape " + _nextPowerPointShapeId,
                Kind = request.Kind,
                Text = request.Text ?? string.Empty,
                Left = request.Left,
                Top = request.Top,
                Width = request.Width,
                Height = request.Height,
                Rows = request.Rows,
                Columns = request.Columns,
                Values = request.Values
            };
            slide.Shapes.Add(shape);
            ThrowAfterPowerPointMutation();
            return new PowerPointMutationBackendResult
            {
                Verified = true,
                Changed = true,
                SlideIndex = _slides.IndexOf(slide) + 1,
                ShapeName = shape.Name,
                Rows = request.Kind == "table" ? request.Rows : 0,
                Columns = request.Kind == "table" ? request.Columns : 0,
                StateToken = PowerPointStateToken()
            };
        }

        public PowerPointMutationBackendResult DuplicateSlide(
            PowerPointDuplicateSlideRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointDuplicateOperation);
            var source = PowerPointSlide(request.SlideIndex);
            markDispatchPossible();
            var duplicate = new FakeSlide
            {
                Id = _nextPowerPointSlideId++,
                Title = source.Title,
                Body = source.Body,
                Notes = source.Notes,
                Shapes = (source.Shapes ?? new List<FakePowerPointShape>())
                    .Select(ClonePowerPointShape).ToList()
            };
            _slides.Insert(request.SlideIndex, duplicate);
            ThrowAfterPowerPointMutation();
            return new PowerPointMutationBackendResult
            {
                Verified = true,
                Changed = true,
                SourceIndex = request.SlideIndex,
                DuplicateIndex = request.SlideIndex + 1,
                StateToken = PowerPointStateToken()
            };
        }

        public PowerPointMutationBackendResult MoveSlide(
            PowerPointMoveSlideRequest request, Action markDispatchPossible)
        {
            BeginPowerPointBackendCall(PowerPointMoveOperation);
            var slide = PowerPointSlide(request.SlideIndex);
            if (request.ToIndex < 1 || request.ToIndex > _slides.Count)
                throw new PowerPointBackendException(
                    "fake PowerPoint destination is invalid",
                    "powerpoint_slide_index_invalid", false);
            if (request.SlideIndex == request.ToIndex)
                return PowerPointMutationResult(false, request.ToIndex);
            markDispatchPossible();
            _slides.Remove(slide);
            _slides.Insert(request.ToIndex - 1, slide);
            ThrowAfterPowerPointMutation();
            return PowerPointMutationResult(true, request.ToIndex);
        }

        private IReadOnlyList<PowerPointTextTargetSnapshot> PowerPointTextTargets(
            PowerPointTextScopeRequest request)
        {
            request = request ?? new PowerPointTextScopeRequest();
            IEnumerable<FakeSlide> slides = request.SlideIndex > 0
                ? new[] { PowerPointSlide(request.SlideIndex) }
                : _slides.Take(request.MaxSlides);
            var result = new List<PowerPointTextTargetSnapshot>();
            foreach (var slide in slides)
            {
                var index = _slides.IndexOf(slide) + 1;
                AddPowerPointTarget(result, slide, index,
                    PowerPointTitleId(slide), "Title 1", "shape", slide.Title);
                AddPowerPointTarget(result, slide, index,
                    PowerPointBodyId(slide), "Content Placeholder 2",
                    "shape", slide.Body);
                foreach (var shape in slide.Shapes ??
                    new List<FakePowerPointShape>())
                    AddPowerPointTarget(result, slide, index,
                        shape.Id, shape.Name, "shape", shape.Text);
                if (request.IncludeNotes)
                    AddPowerPointTarget(result, slide, index,
                        PowerPointNotesId(slide), "Notes Placeholder 2",
                        "notes", slide.Notes);
            }
            return result;
        }

        private static void AddPowerPointTarget(
            ICollection<PowerPointTextTargetSnapshot> result,
            FakeSlide slide, int slideIndex, int shapeId,
            string shapeName, string kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            result.Add(new PowerPointTextTargetSnapshot
            {
                TargetId = slide.Id + ":" + kind + ":" + shapeId,
                SlideId = slide.Id,
                SlideIndex = slideIndex,
                ShapeId = shapeId,
                ShapeName = shapeName,
                Kind = kind,
                Text = text
            });
        }

        private void SetPowerPointTarget(
            PowerPointTextReplacementPlan plan, string value)
        {
            var slide = _slides.FirstOrDefault(item => item.Id == plan.SlideId);
            if (slide == null) throw new PowerPointBackendException(
                "fake PowerPoint replacement target changed",
                "powerpoint_replace_target_changed", true);
            if (plan.ShapeId == PowerPointTitleId(slide)) slide.Title = value;
            else if (plan.ShapeId == PowerPointBodyId(slide)) slide.Body = value;
            else if (plan.ShapeId == PowerPointNotesId(slide)) slide.Notes = value;
            else
            {
                var shape = (slide.Shapes ?? new List<FakePowerPointShape>())
                    .FirstOrDefault(item => item.Id == plan.ShapeId);
                if (shape == null) throw new PowerPointBackendException(
                    "fake PowerPoint replacement target changed",
                    "powerpoint_replace_target_changed", true);
                shape.Text = value;
            }
        }

        private FakeSlide PowerPointSlide(int index)
        {
            if (index < 1 || index > _slides.Count)
                throw new PowerPointBackendException(
                    "slideIndex is outside the fake presentation: " + index + ".",
                    "powerpoint_slide_index_invalid", false);
            return _slides[index - 1];
        }

        private PowerPointSlideContentSnapshot PowerPointContent(FakeSlide slide)
        {
            return new PowerPointSlideContentSnapshot
            {
                SlideId = slide.Id,
                Index = _slides.IndexOf(slide) + 1,
                Text = PowerPointSlideText(slide),
                Notes = slide.Notes ?? string.Empty
            };
        }

        private static string PowerPointSlideText(FakeSlide slide)
        {
            var values = new List<string>();
            if (!string.IsNullOrEmpty(slide.Title)) values.Add(slide.Title);
            if (!string.IsNullOrEmpty(slide.Body)) values.Add(slide.Body);
            values.AddRange((slide.Shapes ?? new List<FakePowerPointShape>())
                .Where(shape => !string.IsNullOrEmpty(shape.Text))
                .Select(shape => shape.Text));
            return values.Count == 0 ? string.Empty :
                string.Join(Environment.NewLine, values.ToArray()) +
                Environment.NewLine;
        }

        private static PowerPointShapeSnapshot PowerPointShapeSnapshot(
            FakeSlide slide, int slideIndex, int shapeId,
            string name, string kind, string text,
            int left, int top, int width, int height)
        {
            return new PowerPointShapeSnapshot
            {
                SlideId = slide.Id,
                SlideIndex = slideIndex,
                ShapeId = shapeId,
                Name = name,
                Type = kind,
                Text = text ?? string.Empty,
                Left = left,
                Top = top,
                Width = width,
                Height = height
            };
        }

        private FakePowerPointShape ClonePowerPointShape(FakePowerPointShape source)
        {
            return new FakePowerPointShape
            {
                Id = _nextPowerPointShapeId++,
                Name = source.Name,
                Kind = source.Kind,
                Text = source.Text,
                Left = source.Left,
                Top = source.Top,
                Width = source.Width,
                Height = source.Height,
                Rows = source.Rows,
                Columns = source.Columns,
                Values = source.Values
            };
        }

        private PowerPointMutationBackendResult PowerPointMutationResult(
            bool changed, int slideIndex)
        {
            return new PowerPointMutationBackendResult
            {
                Verified = true,
                Changed = changed,
                SlideIndex = slideIndex,
                StateToken = PowerPointStateToken()
            };
        }

        private string PowerPointStateToken()
        {
            return TextPatternEngine.Sha256(string.Join("\n", _slides.Select(
                slide => slide.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                    (slide.Title ?? string.Empty) + ":" +
                    (slide.Body ?? string.Empty) + ":" +
                    (slide.Notes ?? string.Empty)).ToArray()));
        }

        private void BeginPowerPointBackendCall(string operation)
        {
            PowerPointBackendCalls.Add(operation);
        }

        private void ThrowAfterPowerPointMutation()
        {
            if (!PowerPointThrowAfterMutation) return;
            PowerPointThrowAfterMutation = false;
            throw new InvalidOperationException(
                "scripted failure after PowerPoint mutation");
        }

        private static int PowerPointTitleId(FakeSlide slide)
        {
            return -(slide.Id * 10 + 1);
        }

        private static int PowerPointBodyId(FakeSlide slide)
        {
            return -(slide.Id * 10 + 2);
        }

        private static int PowerPointNotesId(FakeSlide slide)
        {
            return -(slide.Id * 10 + 3);
        }
    }
}
