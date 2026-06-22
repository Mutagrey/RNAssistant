using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Office.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                var selection = TryGetSelection();
                var slide = TryGetSelectedSlide(selection) ?? TryGetActiveSlide();
                if (slide != null)
                {
                    context.ContainerName = "Slide " + slide.SlideIndex;
                }

                var shapeCount = TryGetSelectedShapeCount(selection);
                if (shapeCount > 0)
                {
                    context.SelectionAddress = shapeCount + " shape(s)";
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
                Skill("powerpoint.get_context", "Read-only: Return active presentation and slide context.", "{}"),
                Skill("powerpoint.get_selection", "Read-only: Read selected slide or shape context.", "{}"),
                Skill("powerpoint.read_slides", "Read-only: Read text from slides.", "{\"maxSlides\":20}"),
                Skill("powerpoint.read_slide", "Read-only: Read text and notes from one slide.", "{\"slideIndex\":1}"),
                Skill("powerpoint.list_slides", "Read-only: List slide titles and text previews.", "{}"),
                Skill("powerpoint.list_shapes", "Read-only: List shapes on one slide.", "{\"slideIndex\":1}"),
                Skill("powerpoint.read_speaker_notes", "Read-only: Read speaker notes from slides.", "{\"slideIndex\":0,\"maxSlides\":20}"),
                Skill("powerpoint.add_slide", "Mutates document: Add a text slide.", "{\"title\":\"Slide title\",\"body\":\"Slide body\"}", true, true),
                Skill("powerpoint.replace_selection_text", "Mutates document: Replace text in the selected shape.", "{\"text\":\"Replacement text\"}", true, true),
                Skill("powerpoint.set_speaker_notes", "Mutates document: Set speaker notes for one slide.", "{\"slideIndex\":1,\"notes\":\"Speaker notes\"}", true, true),
                Skill("powerpoint.add_text_box", "Mutates document: Add a text box to a slide.", "{\"slideIndex\":1,\"text\":\"Text\",\"left\":60,\"top\":120,\"width\":480,\"height\":120,\"fontSize\":18}", true, true),
                Skill("powerpoint.set_shape_text", "Mutates document: Set text for a named shape or selected shape.", "{\"slideIndex\":1,\"shapeName\":\"Title 1\",\"text\":\"Replacement text\"}", true, true),
                Skill("powerpoint.add_picture", "Mutates document: Add a local picture file to a slide.", "{\"slideIndex\":1,\"path\":\"C:\\\\Temp\\\\image.png\",\"left\":60,\"top\":120,\"width\":320,\"height\":180}", true, true),
                Skill("powerpoint.add_table", "Mutates document: Add a table to a slide.", "{\"slideIndex\":1,\"rows\":2,\"columns\":2,\"values\":[[\"Header\",\"Value\"],[\"A\",\"1\"]],\"left\":60,\"top\":120,\"width\":520,\"height\":160}", true, true),
                Skill("powerpoint.duplicate_slide", "Mutates document: Duplicate one slide.", "{\"slideIndex\":1}", true, true),
                Skill("powerpoint.move_slide", "Mutates document: Move a slide to a new position.", "{\"slideIndex\":2,\"toIndex\":1}", true, false),
                Skill("powerpoint.vba_read_project", "Read-only: Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("powerpoint.vba_read_module", "Read-only: Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("powerpoint.vba_replace_module", "Mutates document: Replace a VBA module source code and create a rollback backup.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}", true, false),
                Skill("powerpoint.insert_vba_module", "Mutates document: Insert a VBA module or return copyable code if trust access is blocked.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}", true, false),
                Skill("powerpoint.run_macro", "Mutates document: Run a PowerPoint VBA macro by name.", "{\"macroName\":\"Module1.Test\"}", true, false)
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
            var selection = TryGetSelection();
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
            else
            {
                slide = TryGetActiveSlide();
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
                    case "powerpoint.get_selection":
                        return GetSelection();
                    case "powerpoint.read_slides":
                        return ReadSlides(command);
                    case "powerpoint.read_slide":
                        return ReadSlide(command);
                    case "powerpoint.list_slides":
                        return ListSlides();
                    case "powerpoint.list_shapes":
                        return ListShapes(command);
                    case "powerpoint.read_speaker_notes":
                        return ReadSpeakerNotes(command);
                    case "powerpoint.add_slide":
                        return AddSlide(command);
                    case "powerpoint.replace_selection_text":
                        return ReplaceSelectionText(command);
                    case "powerpoint.set_speaker_notes":
                        return SetSpeakerNotes(command);
                    case "powerpoint.add_text_box":
                        return AddTextBox(command);
                    case "powerpoint.set_shape_text":
                        return SetShapeText(command);
                    case "powerpoint.add_picture":
                        return AddPicture(command);
                    case "powerpoint.add_table":
                        return AddTable(command);
                    case "powerpoint.duplicate_slide":
                        return DuplicateSlide(command);
                    case "powerpoint.move_slide":
                        return MoveSlide(command);
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

        private ToolResult GetSelection()
        {
            try
            {
                var selection = TryGetSelection();
                if (selection == null)
                {
                    return ToolResult.Ok("No PowerPoint selection.", "{}");
                }

                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes && selection.ShapeRange.Count > 0)
                {
                    var shape = selection.ShapeRange[1];
                    return ToolResult.Ok("Shape selection read.", JsonConvert.SerializeObject(new
                    {
                        type = "shape",
                        name = shape.Name,
                        text = ShapeText(shape),
                        left = shape.Left,
                        top = shape.Top,
                        width = shape.Width,
                        height = shape.Height
                    }));
                }

                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides && selection.SlideRange.Count > 0)
                {
                    var slide = selection.SlideRange[1];
                    return ToolResult.Ok("Slide selection read.", JsonConvert.SerializeObject(new
                    {
                        type = "slide",
                        index = slide.SlideIndex,
                        text = ReadSlideText(slide)
                    }));
                }

                return ToolResult.Ok("Selection read.", JsonConvert.SerializeObject(new { type = selection.Type.ToString() }));
            }
            catch
            {
                return ToolResult.Ok("No PowerPoint selection.", "{}");
            }
        }

        private ToolResult ReadSlide(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            return ToolResult.Ok("Slide read: " + slide.SlideIndex, JsonConvert.SerializeObject(new
            {
                index = slide.SlideIndex,
                text = ReadSlideText(slide),
                notes = ReadNotesText(slide)
            }));
        }

        private ToolResult ListSlides()
        {
            var presentation = RequirePresentation();
            var slides = new List<object>();
            for (var i = 1; i <= presentation.Slides.Count; i++)
            {
                var slide = presentation.Slides[i];
                slides.Add(new
                {
                    index = i,
                    title = SlideTitle(slide),
                    text = Trim(ReadSlideText(slide), 1000)
                });
            }

            return ToolResult.Ok("Slides listed: " + slides.Count, JsonConvert.SerializeObject(slides));
        }

        private ToolResult ListShapes(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var shapes = new List<object>();
            foreach (PowerPoint.Shape shape in slide.Shapes)
            {
                shapes.Add(new
                {
                    name = shape.Name,
                    type = shape.Type.ToString(),
                    text = ShapeText(shape),
                    left = shape.Left,
                    top = shape.Top,
                    width = shape.Width,
                    height = shape.Height
                });
            }

            return ToolResult.Ok("Shapes listed: " + shapes.Count, JsonConvert.SerializeObject(shapes));
        }

        private ToolResult ReadSpeakerNotes(ToolCommand command)
        {
            var presentation = RequirePresentation();
            var slideIndex = ToolArgumentReader.Int32(command.Arguments, "slideIndex", 0);
            var maxSlides = Math.Max(1, Math.Min(200, ToolArgumentReader.Int32(command.Arguments, "maxSlides", 20)));
            var notes = new List<object>();
            if (slideIndex > 0)
            {
                var slide = ResolveSlide(slideIndex);
                notes.Add(new { index = slide.SlideIndex, notes = ReadNotesText(slide) });
            }
            else
            {
                var count = Math.Min(presentation.Slides.Count, maxSlides);
                for (var i = 1; i <= count; i++)
                {
                    var slide = presentation.Slides[i];
                    notes.Add(new { index = i, notes = ReadNotesText(slide) });
                }
            }

            return ToolResult.Ok("Speaker notes read: " + notes.Count, JsonConvert.SerializeObject(notes));
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
            var selection = TryGetSelection();
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

        private ToolResult SetSpeakerNotes(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var notes = ToolArgumentReader.String(command.Arguments, "notes", string.Empty);
            var shape = ResolveNotesTextShape(slide);
            if (shape == null)
            {
                return ToolResult.Fail("Could not find speaker notes text shape.");
            }

            shape.TextFrame.TextRange.Text = notes;
            return ToolResult.Ok("Speaker notes set for slide " + slide.SlideIndex);
        }

        private ToolResult AddTextBox(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
            var left = ToolArgumentReader.Int32(command.Arguments, "left", 60);
            var top = ToolArgumentReader.Int32(command.Arguments, "top", 120);
            var width = ToolArgumentReader.Int32(command.Arguments, "width", 480);
            var height = ToolArgumentReader.Int32(command.Arguments, "height", 120);
            var fontSize = ToolArgumentReader.Int32(command.Arguments, "fontSize", 0);
            var shape = slide.Shapes.AddTextbox(MsoTextOrientation.msoTextOrientationHorizontal, left, top, width, height);
            shape.TextFrame.TextRange.Text = text;
            if (fontSize > 0)
            {
                shape.TextFrame.TextRange.Font.Size = fontSize;
            }

            return ToolResult.Ok("Text box added.", JsonConvert.SerializeObject(new { slide = slide.SlideIndex, shape = shape.Name }));
        }

        private ToolResult SetShapeText(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var shapeName = ToolArgumentReader.String(command.Arguments, "shapeName", string.Empty);
            var shape = string.IsNullOrWhiteSpace(shapeName) ? ResolveSelectedShape(slide) : ResolveShape(slide, shapeName);
            if (shape == null)
            {
                return ToolResult.Fail("Shape not found.");
            }
            if (shape.HasTextFrame != MsoTriState.msoTrue)
            {
                return ToolResult.Fail("Shape has no text frame.");
            }

            shape.TextFrame.TextRange.Text = ToolArgumentReader.String(command.Arguments, "text", string.Empty);
            return ToolResult.Ok("Shape text set: " + shape.Name);
        }

        private ToolResult AddPicture(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var path = ToolArgumentReader.String(command.Arguments, "path", string.Empty);
            if (string.IsNullOrWhiteSpace(path))
            {
                return ToolResult.Fail("path is required.");
            }

            var left = ToolArgumentReader.Int32(command.Arguments, "left", 60);
            var top = ToolArgumentReader.Int32(command.Arguments, "top", 120);
            var width = ToolArgumentReader.Int32(command.Arguments, "width", 320);
            var height = ToolArgumentReader.Int32(command.Arguments, "height", 180);
            var shape = slide.Shapes.AddPicture(path, MsoTriState.msoFalse, MsoTriState.msoTrue, left, top, width, height);
            return ToolResult.Ok("Picture added.", JsonConvert.SerializeObject(new { slide = slide.SlideIndex, shape = shape.Name }));
        }

        private ToolResult AddTable(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var rows = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "rows", 2));
            var columns = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "columns", 2));
            var left = ToolArgumentReader.Int32(command.Arguments, "left", 60);
            var top = ToolArgumentReader.Int32(command.Arguments, "top", 120);
            var width = ToolArgumentReader.Int32(command.Arguments, "width", 520);
            var height = ToolArgumentReader.Int32(command.Arguments, "height", 160);
            var shape = slide.Shapes.AddTable(rows, columns, left, top, width, height);
            var valuesJson = ToolArgumentReader.String(command.Arguments, "values", string.Empty);
            if (!string.IsNullOrWhiteSpace(valuesJson))
            {
                var values = JArray.Parse(valuesJson);
                for (var r = 1; r <= rows && r <= values.Count; r++)
                {
                    var row = values[r - 1] as JArray;
                    if (row == null)
                    {
                        continue;
                    }
                    for (var c = 1; c <= columns && c <= row.Count; c++)
                    {
                        shape.Table.Cell(r, c).Shape.TextFrame.TextRange.Text = Convert.ToString(row[c - 1]);
                    }
                }
            }

            return ToolResult.Ok("Table added.", JsonConvert.SerializeObject(new { slide = slide.SlideIndex, shape = shape.Name, rows = rows, columns = columns }));
        }

        private ToolResult DuplicateSlide(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var duplicated = slide.Duplicate();
            var duplicate = duplicated[1];
            return ToolResult.Ok("Slide duplicated.", JsonConvert.SerializeObject(new { sourceIndex = slide.SlideIndex, duplicateIndex = duplicate.SlideIndex }));
        }

        private ToolResult MoveSlide(ToolCommand command)
        {
            var slide = ResolveSlide(ToolArgumentReader.Int32(command.Arguments, "slideIndex", 1));
            var toIndex = Math.Max(1, ToolArgumentReader.Int32(command.Arguments, "toIndex", 1));
            slide.MoveTo(toIndex);
            return ToolResult.Ok("Slide moved to " + toIndex);
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

        private PowerPoint.Slide ResolveSlide(int slideIndex)
        {
            var presentation = RequirePresentation();
            if (presentation.Slides.Count <= 0)
            {
                throw new InvalidOperationException("Presentation has no slides.");
            }

            var index = Math.Max(1, Math.Min(presentation.Slides.Count, slideIndex));
            return presentation.Slides[index];
        }

        private static string SlideTitle(PowerPoint.Slide slide)
        {
            try
            {
                if (slide != null && slide.Shapes.HasTitle == MsoTriState.msoTrue)
                {
                    return slide.Shapes.Title.TextFrame.TextRange.Text;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ShapeText(PowerPoint.Shape shape)
        {
            try
            {
                return shape != null &&
                    shape.HasTextFrame == MsoTriState.msoTrue &&
                    shape.TextFrame.HasText == MsoTriState.msoTrue
                    ? shape.TextFrame.TextRange.Text
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadNotesText(PowerPoint.Slide slide)
        {
            var builder = new StringBuilder();
            try
            {
                foreach (PowerPoint.Shape shape in slide.NotesPage.Shapes)
                {
                    var text = ShapeText(shape);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.AppendLine(text);
                    }
                }
            }
            catch
            {
            }

            return builder.ToString();
        }

        private static PowerPoint.Shape ResolveNotesTextShape(PowerPoint.Slide slide)
        {
            try
            {
                var placeholders = slide.NotesPage.Shapes.Placeholders;
                if (placeholders.Count >= 2)
                {
                    var placeholder = placeholders[2];
                    if (placeholder.HasTextFrame == MsoTriState.msoTrue)
                    {
                        return placeholder;
                    }
                }
            }
            catch
            {
            }

            try
            {
                foreach (PowerPoint.Shape shape in slide.NotesPage.Shapes)
                {
                    if (shape.HasTextFrame == MsoTriState.msoTrue)
                    {
                        return shape;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private PowerPoint.Shape ResolveSelectedShape(PowerPoint.Slide slide)
        {
            try
            {
                var selection = TryGetSelection();
                if (selection != null &&
                    selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes &&
                    selection.ShapeRange.Count > 0)
                {
                    var shape = selection.ShapeRange[1];
                    return ShapeBelongsToPresentation(shape, slide.Parent as PowerPoint.Presentation) ? shape : null;
                }
            }
            catch
            {
            }

            return null;
        }

        private PowerPoint.DocumentWindow TryGetActiveWindow()
        {
            try { return _application.ActiveWindow; }
            catch { return null; }
        }

        private PowerPoint.Selection TryGetSelection()
        {
            try
            {
                var window = TryGetActiveWindow();
                return window == null ? null : window.Selection;
            }
            catch
            {
                return null;
            }
        }

        private PowerPoint.Slide TryGetActiveSlide()
        {
            try
            {
                var window = TryGetActiveWindow();
                if (window == null || window.View == null)
                {
                    return null;
                }

                return window.View.Slide as PowerPoint.Slide;
            }
            catch
            {
                return null;
            }
        }

        private PowerPoint.Slide TryGetSelectedSlide(PowerPoint.Selection selection)
        {
            if (selection == null)
            {
                return null;
            }

            try
            {
                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionSlides && selection.SlideRange.Count > 0)
                {
                    return selection.SlideRange[1];
                }

                if (selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes && selection.ShapeRange.Count > 0)
                {
                    var shape = selection.ShapeRange[1];
                    return shape == null ? null : shape.Parent as PowerPoint.Slide;
                }
            }
            catch
            {
            }

            return null;
        }

        private static int TryGetSelectedShapeCount(PowerPoint.Selection selection)
        {
            try
            {
                return selection != null && selection.Type == PowerPoint.PpSelectionType.ppSelectionShapes
                    ? selection.ShapeRange.Count
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static PowerPoint.Shape ResolveShape(PowerPoint.Slide slide, string shapeName)
        {
            if (slide == null || string.IsNullOrWhiteSpace(shapeName))
            {
                return null;
            }

            foreach (PowerPoint.Shape shape in slide.Shapes)
            {
                if (string.Equals(shape.Name, shapeName, StringComparison.OrdinalIgnoreCase))
                {
                    return shape;
                }
            }

            return null;
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
