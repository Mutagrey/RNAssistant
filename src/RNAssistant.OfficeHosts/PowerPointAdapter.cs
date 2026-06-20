using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Office.Core;
using Newtonsoft.Json;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class PowerPointAdapter : IOfficeApplicationAdapter, IOfficeContextProvider
    {
        private readonly PowerPoint.Application _application;
        private readonly OfficeTargetDescriptor _target;

        public PowerPointAdapter(PowerPoint.Application application)
            : this(application, null)
        {
        }

        public PowerPointAdapter(PowerPoint.Application application, OfficeTargetDescriptor target)
        {
            _application = application;
            _target = target;
        }

        public string HostName { get { return "PowerPoint"; } }

        public string DocumentKey
        {
            get
            {
                var presentation = ActivePresentation();
                if (presentation == null)
                {
                    return "PowerPoint:NoPresentation";
                }

                return DocumentIdentity.ForOfficeDocument(
                    HostName,
                    presentation.Path,
                    RuntimeDocumentKey,
                    () => presentation.CustomDocumentProperties);
            }
        }

        public string RuntimeDocumentKey
        {
            get
            {
                var presentation = ActivePresentation();
                return presentation == null ? "PowerPoint:NoPresentation" : "PowerPoint:Runtime:" + presentation.GetHashCode().ToString("x");
            }
        }

        public string LegacyDocumentKey
        {
            get
            {
                var presentation = ActivePresentation();
                if (presentation == null)
                {
                    return "PowerPoint:NoPresentation";
                }

                return string.IsNullOrWhiteSpace(presentation.FullName) ? RuntimeDocumentKey : presentation.FullName;
            }
        }

        public string DocumentTitle
        {
            get
            {
                var presentation = ActivePresentation();
                return presentation == null ? "No presentation" : presentation.Name;
            }
        }

        public OfficeContext GetOfficeContext()
        {
            var context = new OfficeContext { Host = HostName };
            try
            {
                var hwnd = NativeWindowInfo.ReadLongMemberPath(_application, "HWND");
                context.AppHwnd = new IntPtr(hwnd);
                context.ProcessId = NativeWindowInfo.GetProcessId(hwnd);
            }
            catch
            {
            }

            var presentation = ActivePresentation();
            if (presentation != null)
            {
                context.DocumentPath = SafeString(delegate { return presentation.FullName; });
                context.DocumentTitle = SafeString(delegate { return presentation.Name; });
            }

            try
            {
                var window = _application.ActiveWindow;
                var selection = window == null ? null : window.Selection;
                if (selection != null && selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides)
                {
                    context.ContainerName = selection.SlideRange.Count > 0 ? "Slide " + selection.SlideRange[1].SlideIndex : null;
                }
                if (string.IsNullOrWhiteSpace(context.ContainerName) && window != null && window.View != null && window.View.Slide != null)
                {
                    context.ContainerName = "Slide " + window.View.Slide.SlideIndex;
                }

                if (selection != null && selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes && selection.ShapeRange.Count > 0)
                {
                    context.SelectionAddress = selection.ShapeRange.Count + " shape(s)";
                }
            }
            catch
            {
            }

            return context;
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Skill("powerpoint.get_context", "Return active PowerPoint presentation and slide context.", "{}"),
                Skill("powerpoint.read_slides", "Read text from slides.", "{\"maxSlides\":20}"),
                Skill("powerpoint.add_slide", "Add a text slide.", "{\"title\":\"Slide title\",\"body\":\"Slide body\"}", true, true),
                Skill("powerpoint.replace_selection_text", "Replace text in selected shape.", "{\"text\":\"Replacement text\"}", true, true),
                Skill("powerpoint.vba_read_project", "Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("powerpoint.vba_read_module", "Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("powerpoint.vba_replace_module", "Replace a VBA module source code; RNAssistant stores rollback backups before replacement.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}", true, false),
                Skill("powerpoint.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}", true, false),
                Skill("powerpoint.run_macro", "Run a PowerPoint VBA macro by name.", "{\"macroName\":\"Module1.Test\"}", true, false)
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var presentation = ActivePresentation();
            if (presentation == null)
            {
                return "No active presentation.";
            }

            return Trim(ReadSlidesText(presentation, 20), maxChars);
        }

        public string GetVbaSnapshot(int maxChars)
        {
            var presentation = ActivePresentation();
            if (presentation == null)
            {
                return "No active presentation.";
            }

            return VbaProjectSupport.GetSnapshot(presentation, presentation.Name, maxChars);
        }

        public void PrepareForContextCapture()
        {
            try
            {
                var presentation = ActivePresentation();
                if (presentation != null)
                {
                    presentation.Application.Activate();
                    return;
                }

                _application.Activate();
            }
            catch
            {
            }
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var presentation = RequirePresentation();
            var selection = _application.ActiveWindow == null ? null : _application.ActiveWindow.Selection;
            if (selection == null)
            {
                throw new InvalidOperationException("Select a PowerPoint slide or shape first.");
            }

            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            PowerPoint.Slide slide = null;
            PowerPoint.Shape shape = null;
            var text = string.Empty;

            if (selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes && selection.ShapeRange.Count > 0)
            {
                shape = selection.ShapeRange[1];
                slide = shape.Parent as PowerPoint.Slide;
                if (shape.HasTextFrame == MsoTriState.msoTrue && shape.TextFrame.HasText == MsoTriState.msoTrue)
                {
                    text = shape.TextFrame.TextRange.Text;
                }
            }
            else if (selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides && selection.SlideRange.Count > 0)
            {
                slide = selection.SlideRange[1];
                text = ReadSlideText(slide);
            }
            else if (_application.ActiveWindow.View != null)
            {
                slide = _application.ActiveWindow.View.Slide as PowerPoint.Slide;
                if (slide != null)
                {
                    text = ReadSlideText(slide);
                }
            }

            if (slide == null)
            {
                throw new InvalidOperationException("Select a PowerPoint slide or shape first.");
            }
            if (!SlideBelongsToPresentation(slide, presentation))
            {
                throw new InvalidOperationException("Selected PowerPoint object is not in the target presentation.");
            }

            var reference = "Slide " + slide.SlideIndex + (shape == null ? string.Empty : " / " + shape.Name);
            if (referenceOnly)
            {
                text = "Reference only. Use PowerPoint tools with this slide/shape if exact content is needed.";
            }
            else if (string.IsNullOrWhiteSpace(text))
            {
                text = "Selected PowerPoint object has no readable text. Use this reference for layout/object tasks.";
            }

            text = Trim(text, maxChars);
            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "slide-reference" : (shape == null ? "slide" : "shape"),
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

        public ToolResult ExecuteTool(ToolCommand command)
        {
            try
            {
                switch (command.ToolId)
                {
                    case "powerpoint.get_context":
                        return ToolResult.Ok("PowerPoint context collected.", JsonConvert.SerializeObject(GetOfficeContext()));
                    case "powerpoint.read_slides":
                        return ReadSlides(command);
                    case "powerpoint.add_slide":
                        return AddSlide(command);
                    case "powerpoint.replace_selection_text":
                        return ReplaceSelectionText(command);
                    case "powerpoint.vba_read_project":
                        return ReadVbaProject(command);
                    case "powerpoint.vba_read_module":
                        return ReadVbaModule(command);
                    case "powerpoint.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "powerpoint.insert_vba_module":
                        return InsertVbaModule(command);
                    case "powerpoint.run_macro":
                        return RunMacro(command);
                    default:
                        return ToolResult.Fail("Unsupported PowerPoint tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }

        private ToolResult ReadSlides(ToolCommand command)
        {
            var maxSlides = ToolArgumentReader.Int32(command.Arguments, "maxSlides", 20);
            return ToolResult.Ok("Slides read.", JsonConvert.SerializeObject(new { text = ReadSlidesText(RequirePresentation(), maxSlides) }));
        }

        private ToolResult AddSlide(ToolCommand command)
        {
            var presentation = RequirePresentation();
            var title = ToolArgumentReader.String(command.Arguments, "title", "AI slide");
            var body = ToolArgumentReader.String(command.Arguments, "body", string.Empty);
            var slide = presentation.Slides.Add(presentation.Slides.Count + 1, PowerPoint.PpSlideLayout.ppLayoutText);
            slide.Shapes.Title.TextFrame.TextRange.Text = title;
            if (slide.Shapes.Count >= 2)
            {
                slide.Shapes[2].TextFrame.TextRange.Text = body;
            }
            return ToolResult.Ok("Slide added: " + title);
        }

        private ToolResult ReplaceSelectionText(ToolCommand command)
        {
            var presentation = RequirePresentation();
            var text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
            var selection = _application.ActiveWindow.Selection;
            if (selection == null || selection.Type != PowerPoint.PpSelectionType.ppSelectionShapes)
            {
                return ToolResult.Fail("Select a text shape first.");
            }

            var shape = selection.ShapeRange[1];
            if (!ShapeBelongsToPresentation(shape, presentation))
            {
                return ToolResult.Fail("Selected shape is not in the target presentation.");
            }
            if (shape.HasTextFrame != MsoTriState.msoTrue)
            {
                return ToolResult.Fail("Selected shape has no text frame.");
            }

            shape.TextFrame.TextRange.Text = text;
            return ToolResult.Ok("Selected shape text replaced.");
        }

        private ToolResult ReadVbaProject(ToolCommand command)
        {
            var presentation = RequirePresentation();
            return VbaProjectSupport.ReadProject(presentation, presentation.Name, ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            return VbaProjectSupport.ReadModule(
                RequirePresentation(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private ToolResult ReplaceVbaModule(ToolCommand command)
        {
            return VbaProjectSupport.ReplaceModule(
                RequirePresentation(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.String(command.Arguments, "code", string.Empty),
                ToolArgumentReader.Boolean(command.Arguments, "createIfMissing", true));
        }

        private ToolResult InsertVbaModule(ToolCommand command)
        {
            var moduleName = ToolArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = ToolArgumentReader.String(command.Arguments, "code", string.Empty);
            try
            {
                return VbaProjectSupport.InsertModule(RequirePresentation(), moduleName, code);
            }
            catch (Exception ex)
            {
                return ToolResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private ToolResult RunMacro(ToolCommand command)
        {
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("No macroName provided.");
            }

            _application.Run(macroName);
            return ToolResult.Ok("Macro ran: " + macroName);
        }

        private string ReadSlidesText(PowerPoint.Presentation presentation, int maxSlides)
        {
            var builder = new StringBuilder();
            var count = Math.Min(presentation.Slides.Count, Math.Max(1, maxSlides));
            for (var i = 1; i <= count; i++)
            {
                var slide = presentation.Slides[i];
                builder.AppendLine("Slide " + i + ":");
                builder.Append(ReadSlideText(slide));
            }
            return builder.ToString();
        }

        private string ReadSlideText(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            foreach (PowerPoint.Shape shape in slide.Shapes)
            {
                if (shape.HasTextFrame == MsoTriState.msoTrue && shape.TextFrame.HasText == MsoTriState.msoTrue)
                {
                    builder.AppendLine(shape.TextFrame.TextRange.Text);
                }
            }
            return builder.ToString();
        }

        private PowerPoint.Presentation ActivePresentation()
        {
            if (HasTargetDocument())
            {
                return TargetPresentation();
            }

            try { return _application.ActivePresentation; }
            catch { return null; }
        }

        private PowerPoint.Presentation TargetPresentation()
        {
            if (!HasTargetDocument())
            {
                return null;
            }

            foreach (PowerPoint.Presentation presentation in _application.Presentations)
            {
                if (MatchesPresentation(presentation))
                {
                    return presentation;
                }
            }

            return null;
        }

        private bool HasTargetDocument()
        {
            return _target != null && _target.HasDocumentIdentity;
        }

        private bool MatchesPresentation(PowerPoint.Presentation presentation)
        {
            if (presentation == null)
            {
                return false;
            }

            var fullName = SafeString(delegate { return presentation.FullName; });
            if (!string.IsNullOrWhiteSpace(_target.FullName) && SamePath(fullName, _target.FullName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_target.Path) && SamePath(fullName, _target.Path))
            {
                return true;
            }

            var name = SafeString(delegate { return presentation.Name; });
            return string.IsNullOrWhiteSpace(_target.FullName)
                && string.IsNullOrWhiteSpace(_target.Path)
                && !string.IsNullOrWhiteSpace(_target.Name)
                && string.Equals(name, _target.Name, StringComparison.OrdinalIgnoreCase);
        }

        private PowerPoint.Presentation RequirePresentation()
        {
            var presentation = ActivePresentation();
            if (presentation == null)
            {
                throw new InvalidOperationException(_target == null || !_target.HasDocumentIdentity
                    ? "No active presentation."
                    : "Target PowerPoint presentation is not open.");
            }
            return presentation;
        }

        private static bool ShapeBelongsToPresentation(PowerPoint.Shape shape, PowerPoint.Presentation presentation)
        {
            if (shape == null)
            {
                return false;
            }

            try
            {
                return SlideBelongsToPresentation(shape.Parent as PowerPoint.Slide, presentation);
            }
            catch
            {
                return false;
            }
        }

        private static bool SlideBelongsToPresentation(PowerPoint.Slide slide, PowerPoint.Presentation presentation)
        {
            if (slide == null || presentation == null)
            {
                return false;
            }

            try
            {
                var parent = slide.Parent as PowerPoint.Presentation;
                return SamePath(SafeString(delegate { return parent.FullName; }), SafeString(delegate { return presentation.FullName; }))
                    || string.Equals(SafeString(delegate { return parent.Name; }), SafeString(delegate { return presentation.Name; }), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static bool SamePath(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static ToolDefinition Skill(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true)
        {
            return new ToolDefinition { Id = id, Host = "PowerPoint", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun };
        }

        private static string Trim(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
