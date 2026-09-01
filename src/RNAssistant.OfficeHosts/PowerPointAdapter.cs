using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Office.Core;
using Newtonsoft.Json;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Tools;
using RNAssistant.OfficeHosts.Identity;

namespace RNAssistant.OfficeHosts
{
    public sealed class PowerPointAdapter : IOfficeApplicationAdapter,
        IOfficeContextProvider, IOfficeBuiltInSkillProvider,
        IOfficeDocumentCatalog, IOfficeDocumentSessionProvider,
        IOfficeDispatcherProvider, IPowerPointBackendProvider,
        IVbaHostBackendProvider
    {
        private readonly PowerPoint.Application _application;
        private readonly PowerPoint.Presentation _targetPresentation;
        private readonly PowerPoint.DocumentWindow _targetWindow;
        private readonly PowerPointDocumentSession _documentSession;
        private readonly PowerPointInteropBackend _powerPointBackend;
        private readonly VbaInteropBackend _vbaHostBackend;

        public PowerPointAdapter(
            PowerPoint.Application application,
            PowerPoint.Presentation targetPresentation,
            PowerPoint.DocumentWindow targetWindow,
            IOfficeStaDispatcher dispatcher)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _targetPresentation = targetPresentation ??
                throw new ArgumentNullException(nameof(targetPresentation));
            _targetWindow = targetWindow;
            var runtimeDocumentId = DocumentIdentity.RuntimeKey(
                HostName, _targetPresentation);
            _documentSession = new PowerPointDocumentSession(
                _targetPresentation, _targetWindow,
                runtimeDocumentId, dispatcher);
            _powerPointBackend = new PowerPointInteropBackend(_documentSession);
            _vbaHostBackend = new VbaInteropBackend(
                _documentSession, _application);
        }

        public string HostName { get { return "PowerPoint"; } }
        public IOfficeDocumentSession DocumentSession { get { return _documentSession; } }
        public IOfficeStaDispatcher StaDispatcher { get { return _documentSession.StaDispatcher; } }
        public IPowerPointBackend PowerPointBackend { get { return _powerPointBackend; } }
        public IVbaHostBackend VbaHostBackend { get { return _vbaHostBackend; } }
        public string DocumentKey { get { return _documentSession.StableDocumentId; } }
        public string RuntimeDocumentKey { get { return _documentSession.RuntimeDocumentId; } }
        public string DocumentTitle { get { return RequirePresentation().Name; } }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            try
            {
                var hwnd = _targetWindow == null
                    ? NativeWindowInfo.ReadLongMemberPath(_application, "HWND")
                    : NativeWindowInfo.ReadLongMemberPath(_targetWindow, "HWND");
                context.AppHwnd = new IntPtr(hwnd);
                context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);
            }
            catch { }

            var presentation = RequirePresentation();
            context.DocumentPath = PersistentPath(presentation);
            context.DocumentTitle = SafeString(delegate { return presentation.Name; });
            try
            {
                var selection = TryGetSelection();
                var slide = TryGetSelectedSlide(selection) ?? TryGetActiveSlide();
                if (slide != null)
                    context.ContainerName = "Slide " + slide.SlideIndex;
                var shapeCount = TryGetSelectedShapeCount(selection);
                if (shapeCount > 0)
                    context.SelectionAddress = shapeCount + " shape(s)";
            }
            catch { }
            return context;
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            var result = new List<OpenOfficeDocumentDto>();
            foreach (PowerPoint.Presentation presentation in _application.Presentations)
            {
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForPresentation(presentation),
                    Title = SafeString(delegate { return presentation.Name; }),
                    Path = PersistentPath(presentation),
                    IsActive = SamePresentation(_targetPresentation, presentation)
                });
            }
            return result;
        }

        public bool ActivateDocument(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey)) return false;
            foreach (PowerPoint.Presentation presentation in _application.Presentations)
            {
                if (!string.Equals(
                    KeyForPresentation(presentation), documentKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (presentation.Windows != null && presentation.Windows.Count > 0)
                    presentation.Windows[1].Activate();
                NativeWindowInfo.BringToForeground(
                    NativeWindowInfo.ReadLongMemberPath(_application, "HWND"));
                return true;
            }
            return false;
        }

        public bool OpenDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var presentation = _application.Presentations.Open(path);
                if (presentation == null) return false;
                if (presentation.Windows != null && presentation.Windows.Count > 0)
                    presentation.Windows[1].Activate();
                NativeWindowInfo.BringToForeground(
                    NativeWindowInfo.ReadLongMemberPath(_application, "HWND"));
                return true;
            }
            catch { return false; }
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                new SkillDefinition
                {
                    Id = "powerpoint.deck_building",
                    Host = "PowerPoint",
                    Name = "PowerPoint deck building",
                    Description = "Create and improve slide structure, content, and speaker notes.",
                    BodyMarkdown = "# PowerPoint Deck Building\n\nUse this skill for slide creation and cleanup.\n\n- Create one clear idea per slide.\n- Use short titles and concise body bullets.\n- Keep slide order logical: context, evidence, recommendation, next steps.\n- Add speaker notes only when useful.\n- Do not overload slides with long paragraphs.",
                    Enabled = true,
                    BuiltIn = true
                }
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            return Trim(ReadSlidesText(RequirePresentation(), 20), maxChars);
        }

        public void PrepareForContextCapture()
        {
            try
            {
                if (_targetWindow != null) _targetWindow.Activate();
                else _targetPresentation.Application.Activate();
            }
            catch { }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var presentation = RequirePresentation();
            var selection = TryGetSelection();
            if (selection == null)
                throw new InvalidOperationException(
                    "Select a PowerPoint slide or shape first in the bound presentation.");
            var referenceOnly = string.Equals(
                mode, "reference", StringComparison.OrdinalIgnoreCase);
            PowerPoint.Slide slide = null;
            PowerPoint.Shape shape = null;
            var text = string.Empty;
            if (selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes &&
                selection.ShapeRange.Count > 0)
            {
                shape = selection.ShapeRange[1];
                slide = shape.Parent as PowerPoint.Slide;
                text = ShapeText(shape);
            }
            else if (selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides &&
                selection.SlideRange.Count > 0)
            {
                slide = selection.SlideRange[1];
                text = ReadSlideText(slide);
            }
            else
            {
                slide = TryGetActiveSlide();
                if (slide != null) text = ReadSlideText(slide);
            }
            if (slide == null || !SlideBelongsToPresentation(slide, presentation))
                throw new InvalidOperationException(
                    "Selected PowerPoint object is not in the bound presentation.");
            var reference = "Slide " + slide.SlideIndex +
                (shape == null ? string.Empty : " / " + shape.Name);
            if (referenceOnly)
                text = "Reference only. Use PowerPoint tools with this slide/shape if exact content is needed.";
            else if (string.IsNullOrWhiteSpace(text))
                text = "Selected PowerPoint object has no readable text. Use this reference for layout/object tasks.";
            text = Trim(text, maxChars);
            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "slide-reference" :
                    (shape == null ? "slide" : "shape"),
                Title = "PowerPoint " + reference,
                Reference = reference,
                Source = presentation.Name + " / " + reference,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    presentation = presentation.Name,
                    slide = slide.SlideIndex,
                    shape = shape == null ? string.Empty : shape.Name,
                    shapeType = shape == null ? string.Empty : shape.Type.ToString(),
                    mode = referenceOnly ? "reference" : "text"
                })
            };
        }

        private PowerPoint.Presentation RequirePresentation()
        {
            if (!_documentSession.IsAlive)
                throw new InvalidOperationException(
                    "Target PowerPoint presentation is not open.");
            return _targetPresentation;
        }

        private PowerPoint.Selection TryGetSelection()
        {
            try { return _targetWindow == null ? null : _targetWindow.Selection; }
            catch { return null; }
        }

        private PowerPoint.Slide TryGetActiveSlide()
        {
            try
            {
                if (_targetWindow == null || _targetWindow.View == null) return null;
                var slide = _targetWindow.View.Slide as PowerPoint.Slide;
                return SlideBelongsToPresentation(slide, _targetPresentation)
                    ? slide : null;
            }
            catch { return null; }
        }

        private static PowerPoint.Slide TryGetSelectedSlide(
            PowerPoint.Selection selection)
        {
            if (selection == null) return null;
            try
            {
                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides &&
                    selection.SlideRange.Count > 0)
                    return selection.SlideRange[1];
                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes &&
                    selection.ShapeRange.Count > 0)
                    return selection.ShapeRange[1].Parent as PowerPoint.Slide;
            }
            catch { }
            return null;
        }

        private static int TryGetSelectedShapeCount(
            PowerPoint.Selection selection)
        {
            try
            {
                return selection != null &&
                    selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes
                    ? selection.ShapeRange.Count : 0;
            }
            catch { return 0; }
        }

        private string KeyForPresentation(PowerPoint.Presentation presentation)
        {
            return PowerPointDocumentSession.StableKey(
                presentation,
                presentation == null ? "PowerPoint:Runtime:none" :
                    DocumentIdentity.RuntimeKey(HostName, presentation));
        }

        private static string PersistentPath(
            PowerPoint.Presentation presentation)
        {
            if (presentation == null || string.IsNullOrWhiteSpace(
                SafeString(delegate { return presentation.Path; })))
                return string.Empty;
            return SafeString(delegate { return presentation.FullName; });
        }

        private static string ReadSlidesText(
            PowerPoint.Presentation presentation, int maxSlides)
        {
            var builder = new StringBuilder();
            var count = Math.Min(
                presentation.Slides.Count, Math.Max(1, maxSlides));
            for (var index = 1; index <= count; index++)
            {
                builder.AppendLine("Slide " + index + ":");
                builder.Append(ReadSlideText(presentation.Slides[index]));
            }
            return builder.ToString();
        }

        private static string ReadSlideText(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in slide.Shapes)
            {
                var text = ShapeText(shape);
                if (!string.IsNullOrEmpty(text)) builder.AppendLine(text);
            }
            return builder.ToString();
        }

        private static string ShapeText(PowerPoint.Shape shape)
        {
            try
            {
                return shape != null &&
                    shape.HasTextFrame == MsoTriState.msoTrue &&
                    shape.TextFrame.HasText == MsoTriState.msoTrue
                    ? shape.TextFrame.TextRange.Text : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool SamePresentation(
            PowerPoint.Presentation left, PowerPoint.Presentation right)
        {
            if (left == null || right == null) return false;
            try
            {
                return string.Equals(
                    DocumentIdentity.RuntimeKey("PowerPoint", left),
                    DocumentIdentity.RuntimeKey("PowerPoint", right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SlideBelongsToPresentation(
            PowerPoint.Slide slide, PowerPoint.Presentation presentation)
        {
            if (slide == null || presentation == null) return false;
            try
            {
                return SamePresentation(
                    slide.Parent as PowerPoint.Presentation, presentation);
            }
            catch { return false; }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
