using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Office.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.OfficeHosts
{
    public sealed class PowerPointAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog
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
                return presentation == null ? "PowerPoint:NoPresentation" : DocumentIdentity.RuntimeKey(HostName, presentation);
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

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            PowerPoint.Presentation active;
            try { active = _application.ActivePresentation; }
            catch { active = null; }
            var result = new List<OpenOfficeDocumentDto>();
            foreach (PowerPoint.Presentation presentation in _application.Presentations)
            {
                var path = SafeString(delegate { return presentation.Path; });
                result.Add(new OpenOfficeDocumentDto
                {
                    Host = HostName,
                    DocumentKey = KeyForPresentation(presentation),
                    Title = SafeString(delegate { return presentation.Name; }),
                    Path = string.IsNullOrWhiteSpace(path) ? string.Empty : SafeString(delegate { return presentation.FullName; }),
                    IsActive = active != null && string.Equals(KeyForPresentation(active), KeyForPresentation(presentation), StringComparison.OrdinalIgnoreCase)
                });
            }
            return result;
        }

        public bool ActivateDocument(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey))
            {
                return false;
            }

            foreach (PowerPoint.Presentation presentation in _application.Presentations)
            {
                if (!string.Equals(KeyForPresentation(presentation), documentKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (presentation.Windows != null && presentation.Windows.Count > 0)
                {
                    presentation.Windows[1].Activate();
                }
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "HWND"));
                return true;
            }
            return false;
        }

        public bool OpenDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var presentation = _application.Presentations.Open(path);
                if (presentation == null)
                {
                    return false;
                }
                if (presentation.Windows != null && presentation.Windows.Count > 0)
                {
                    presentation.Windows[1].Activate();
                }
                NativeWindowInfo.BringToForeground(NativeWindowInfo.ReadLongMemberPath(_application, "HWND"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string KeyForPresentation(PowerPoint.Presentation presentation)
        {
            if (presentation == null)
            {
                return "PowerPoint:NoPresentation";
            }

            var runtimeKey = DocumentIdentity.RuntimeKey(HostName, presentation);
            return DocumentIdentity.ForOfficeDocument(
                HostName,
                SafeString(delegate { return presentation.Path; }),
                runtimeKey,
                () => presentation.CustomDocumentProperties);
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return new[]
            {
                Tool("powerpoint.get_context", "Read-only: Return active presentation and slide context.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.get_selection", "Read-only: Read selected slide or shape context.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.read_slides", "Read-only: Read text from slides.", "{\"type\":\"object\",\"properties\":{\"maxSlides\":{\"type\":\"integer\",\"description\":\"Maximum number of slides returned.\",\"default\":20}},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.read_slide", "Read-only: Read text and notes from one slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1}},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.list_slides", "Read-only: List slide titles and text previews.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.list_shapes", "Read-only: List shapes on one slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1}},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.search_text", "Read-only: Find literal or regex text in slide shapes and notes with stable coordinates/hash.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Non-empty literal or regular-expression search query.\",\"minLength\":1},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"deck\",\"enum\":[\"deck\",\"slide\"]},\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based target slide when scope is slide; 0 searches the deck.\",\"default\":0},\"includeNotes\":{\"type\":\"boolean\",\"description\":\"Whether speaker notes are included.\",\"default\":true},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"maxResults\":{\"type\":\"integer\",\"description\":\"Maximum number of matches returned.\",\"default\":50},\"contextChars\":{\"type\":\"integer\",\"description\":\"Maximum context characters returned around each match.\",\"default\":80}},\"required\":[\"query\"],\"additionalProperties\":false}"),
                Tool("powerpoint.read_speaker_notes", "Read-only: Read speaker notes from slides.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide to read; 0 reads up to maxSlides.\",\"default\":0},\"maxSlides\":{\"type\":\"integer\",\"description\":\"Maximum number of slides returned.\",\"default\":20}},\"required\":[],\"additionalProperties\":false}"),
                Tool("powerpoint.add_slide", "Mutates document: Add a text slide.", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"description\":\"Human-readable title.\",\"default\":\"AI slide\"},\"body\":{\"type\":\"string\",\"description\":\"Body text for the item being created or updated.\"}},\"required\":[],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.replace_selection_text", "Mutates document: Replace text in the selected shape.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert, replace, or assign.\"}},\"required\":[\"text\"],\"additionalProperties\":false}", true, true, 2),
                Tool("powerpoint.set_speaker_notes", "Mutates document: Set speaker notes for one slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1},\"notes\":{\"type\":\"string\",\"description\":\"Complete speaker-notes text.\"}},\"required\":[\"notes\"],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.add_text_box", "Mutates document: Add a text box to a slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1},\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert, replace, or assign.\"},\"left\":{\"type\":\"integer\",\"description\":\"Horizontal position in points from the slide or sheet origin.\",\"default\":60},\"top\":{\"type\":\"integer\",\"description\":\"Vertical position in points from the slide or sheet origin.\",\"default\":120},\"width\":{\"type\":\"integer\",\"description\":\"Width in points.\",\"default\":480},\"height\":{\"type\":\"integer\",\"description\":\"Height in points.\",\"default\":120},\"fontSize\":{\"type\":\"integer\",\"description\":\"Font size in points.\",\"default\":0}},\"required\":[\"text\"],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.set_shape_text", "Mutates document: Set text for a named shape or selected shape.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1},\"shapeName\":{\"type\":\"string\",\"description\":\"Exact PowerPoint shape name.\"},\"text\":{\"type\":\"string\",\"description\":\"Complete text to insert, replace, or assign.\"}},\"required\":[\"text\"],\"additionalProperties\":false}", true, true, 2),
                Tool("powerpoint.replace_text", "Mutates document: Replace literal or regex text after a matching search preview.", "{\"type\":\"object\",\"properties\":{\"find\":{\"type\":\"string\",\"description\":\"Literal or regular-expression text to find.\",\"minLength\":1},\"replace\":{\"type\":\"string\",\"description\":\"Replacement text; regex capture groups are allowed only in regex mode.\"},\"scope\":{\"type\":\"string\",\"description\":\"Search or operation scope supported by the tool.\",\"default\":\"deck\",\"enum\":[\"deck\",\"slide\"]},\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based target slide when scope is slide; 0 searches the deck.\",\"default\":0},\"includeNotes\":{\"type\":\"boolean\",\"description\":\"Whether speaker notes are included.\",\"default\":true},\"mode\":{\"type\":\"string\",\"description\":\"Text matching mode: literal or regex.\",\"default\":\"literal\",\"enum\":[\"literal\",\"regex\"]},\"matchCase\":{\"type\":\"boolean\",\"description\":\"Whether matching is case-sensitive.\",\"default\":false},\"wholeWord\":{\"type\":\"boolean\",\"description\":\"Whether only whole-word matches are accepted.\",\"default\":false},\"replaceAll\":{\"type\":\"boolean\",\"description\":\"Whether all matches in scope may be replaced.\",\"default\":true},\"expectedMatches\":{\"type\":\"integer\",\"description\":\"Exact match count returned by the preceding search.\"},\"expectedScopeSha256\":{\"type\":\"string\",\"description\":\"Exact scope SHA-256 hash returned by the preceding search.\"},\"maxReplacements\":{\"type\":\"integer\",\"description\":\"Safety limit for replacements.\",\"default\":500}},\"required\":[\"find\",\"expectedMatches\",\"expectedScopeSha256\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("powerpoint.add_picture", "Mutates document: Add a local picture file to a slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1},\"path\":{\"type\":\"string\",\"description\":\"Workspace-relative file path or absolute local source path, as required by the tool.\"},\"left\":{\"type\":\"integer\",\"description\":\"Horizontal position in points from the slide or sheet origin.\",\"default\":60},\"top\":{\"type\":\"integer\",\"description\":\"Vertical position in points from the slide or sheet origin.\",\"default\":120},\"width\":{\"type\":\"integer\",\"description\":\"Width in points.\",\"default\":320},\"height\":{\"type\":\"integer\",\"description\":\"Height in points.\",\"default\":180}},\"required\":[\"path\"],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.add_table", "Mutates document: Add a table to a slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\",\"default\":1},\"rows\":{\"type\":\"integer\",\"description\":\"Number of table rows.\",\"default\":2},\"columns\":{\"type\":\"integer\",\"description\":\"Number of table columns.\",\"default\":2},\"values\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}},\"description\":\"Two-dimensional JSON array of row arrays.\"},\"left\":{\"type\":\"integer\",\"description\":\"Horizontal position in points from the slide or sheet origin.\",\"default\":60},\"top\":{\"type\":\"integer\",\"description\":\"Vertical position in points from the slide or sheet origin.\",\"default\":120},\"width\":{\"type\":\"integer\",\"description\":\"Width in points.\",\"default\":520},\"height\":{\"type\":\"integer\",\"description\":\"Height in points.\",\"default\":160}},\"required\":[],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.duplicate_slide", "Mutates document: Duplicate one slide.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\"}},\"required\":[\"slideIndex\"],\"additionalProperties\":false}", true, true, 1),
                Tool("powerpoint.move_slide", "Mutates document: Move a slide to a new position.", "{\"type\":\"object\",\"properties\":{\"slideIndex\":{\"type\":\"integer\",\"description\":\"One-based slide index.\"},\"toIndex\":{\"type\":\"integer\",\"description\":\"One-based destination slide index.\"}},\"required\":[\"slideIndex\",\"toIndex\"],\"additionalProperties\":false}", true, true, 2, true),
                Tool("powerpoint.vba_read_module", "Read-only: Read one VBA component by exact name from vba_list_modules; returns source and full code hash.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum number of text characters returned.\",\"default\":30000,\"minimum\":1,\"maximum\":1000000}},\"required\":[\"moduleName\"],\"additionalProperties\":false}"),
                Tool("powerpoint.vba_read_lines", "Read-only: Read an exact one-based line range from a VBA component; returns the full-module code hash.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"startLine\":{\"type\":\"integer\",\"description\":\"One-based first line.\",\"default\":1,\"minimum\":1},\"lineCount\":{\"type\":\"integer\",\"description\":\"Maximum consecutive lines returned.\",\"default\":200,\"minimum\":1,\"maximum\":500}},\"required\":[\"moduleName\"],\"additionalProperties\":false}"),
                Tool("powerpoint.vba_replace_module", "Mutates document: Replace a VBA module source code and create a rollback backup.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\"},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code.\"},\"createIfMissing\":{\"type\":\"boolean\",\"description\":\"Whether a missing VBA standard module may be created.\",\"default\":true}},\"required\":[\"moduleName\",\"code\"],\"additionalProperties\":false}", true, false, 3),
                Tool("powerpoint.insert_vba_module", "Mutates document: Insert a VBA module or return copyable code if trust access is blocked.", "{\"type\":\"object\",\"properties\":{\"moduleName\":{\"type\":\"string\",\"description\":\"Exact VBA component name.\",\"default\":\"RNAssistantModule\"},\"code\":{\"type\":\"string\",\"description\":\"Complete VBA source code.\"}},\"required\":[\"code\"],\"additionalProperties\":false}", true, false, 3),
                Tool("powerpoint.run_macro", "Mutates document: Run a PowerPoint VBA macro by name.", "{\"type\":\"object\",\"properties\":{\"macroName\":{\"type\":\"string\",\"description\":\"Exact public VBA macro name.\"}},\"required\":[\"macroName\"],\"additionalProperties\":false}", true, false, 3)
            };
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
            var presentation = ActivePresentation();
            if (presentation == null)
            {
                return "No active presentation.";
            }

            return Trim(ReadSlidesText(presentation, 20), maxChars);
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
                    case "powerpoint.search_text":
                        return SearchText(command);
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
                    case "powerpoint.replace_text":
                        return ReplaceText(command);
                    case "powerpoint.add_picture":
                        return AddPicture(command);
                    case "powerpoint.add_table":
                        return AddTable(command);
                    case "powerpoint.duplicate_slide":
                        return DuplicateSlide(command);
                    case "powerpoint.move_slide":
                        return MoveSlide(command);
                    case "powerpoint.vba_list_project_components_internal":
                        return ListVbaProjectComponents();
                    case "powerpoint.vba_read_module":
                        return ReadVbaModule(command);
                    case "powerpoint.vba_read_lines":
                        return ReadVbaLines(command);
                    case "powerpoint.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "powerpoint.insert_vba_module":
                        return InsertVbaModule(command);
                    case "powerpoint.run_macro":
                        return RunMacro(command);
                    case "powerpoint.vba_install_package_internal":
                        return VbaProjectSupport.InstallPackage(RequirePresentation(), ToolArgumentReader.String(command.Arguments, "componentsJson", "[]"), ToolArgumentReader.String(command.Arguments, "marker", string.Empty));
                    case "powerpoint.vba_remove_package_internal":
                        return VbaProjectSupport.RemovePackage(RequirePresentation(), ToolArgumentReader.String(command.Arguments, "expectedComponentsJson", "{}"), ToolArgumentReader.String(command.Arguments, "expectedMarker", string.Empty));
                    case "powerpoint.vba_create_module_internal":
                        return VbaProjectSupport.CreateModule(RequirePresentation(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty), ToolArgumentReader.String(command.Arguments, "componentType", "StdModule"), ToolArgumentReader.String(command.Arguments, "code", string.Empty));
                    case "powerpoint.vba_delete_module_internal":
                        return VbaProjectSupport.DeleteModule(RequirePresentation(), ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty));
                    default:
                        return ToolResult.Fail("Unsupported PowerPoint tool: " + command.ToolId);
                }
            }
            catch (Exception ex)
            {
                var isVba = (command == null ? string.Empty : command.ToolId ?? string.Empty)
                    .IndexOf(".vba_", StringComparison.OrdinalIgnoreCase) >= 0;
                return ToolResult.Fail(ex.Message, null, isVba ? "vba_access_error" : "office_tool_error", !isVba);
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

        private ToolResult SearchText(ToolCommand command)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty);
            if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required.");
            var maxResults = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxResults", 50)));
            var contextChars = Math.Max(0, Math.Min(1000, ToolArgumentReader.Int32(command.Arguments, "contextChars", 80)));
            var matches = new List<object>();
            var hash = new StringBuilder();
            var total = 0;
            try
            {
                foreach (var target in TextTargets(command))
                {
                    var text = ShapeText(target.Shape);
                    hash.Append(target.SlideIndex).Append(':').Append(target.Kind).Append(':').Append(target.Shape.Name).Append('\n').Append(text).Append('\n');
                    var found = TextPatternEngine.Find(text, query, PatternOptions(command), Math.Max(1, maxResults - matches.Count), contextChars);
                    total += found.MatchCount;
                    foreach (var match in found.Matches)
                    {
                        if (matches.Count >= maxResults) break;
                        matches.Add(new { slideIndex = target.SlideIndex, shapeName = target.Shape.Name, kind = target.Kind, start = match.Index, end = match.Index + match.Length, preview = match.Preview });
                    }
                }
                var scopeHash = TextPatternEngine.Sha256(hash.ToString());
                return ToolResult.Ok("PowerPoint text matches found: " + total, JsonConvert.SerializeObject(new { matchCount = total, returnedCount = matches.Count, truncated = total > matches.Count, scopeSha256 = scopeHash, contentSha256 = scopeHash, matches = matches }));
            }
            catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
        }

        private ToolResult ReplaceText(ToolCommand command)
        {
            var find = ToolArgumentReader.String(command.Arguments, "find", string.Empty);
            if (string.IsNullOrWhiteSpace(find)) return ToolResult.Fail("find is required.");
            var replacement = ToolArgumentReader.String(command.Arguments, "replace", string.Empty);
            var expectedMatches = ToolArgumentReader.Int32(command.Arguments, "expectedMatches", -1);
            var expectedHash = ToolArgumentReader.String(command.Arguments, "expectedScopeSha256", string.Empty);
            var replaceAll = ToolArgumentReader.Boolean(command.Arguments, "replaceAll", true);
            var maxReplacements = Math.Max(1, Math.Min(500, ToolArgumentReader.Int32(command.Arguments, "maxReplacements", 500)));
            if (expectedMatches < 0 || string.IsNullOrWhiteSpace(expectedHash)) return ToolResult.Fail("expectedMatches and expectedScopeSha256 from powerpoint.search_text are required.", null, "search_precondition_required", true);
            var targets = new List<PptTextTarget>(TextTargets(command));
            var hash = new StringBuilder();
            var plans = new List<PptReplacementPlan>();
            var options = PatternOptions(command);
            var observedMatches = 0;
            var replacementPlanned = false;
            try
            {
                foreach (var target in targets)
                {
                    var text = ShapeText(target.Shape);
                    hash.Append(target.SlideIndex).Append(':').Append(target.Kind).Append(':').Append(target.Shape.Name).Append('\n').Append(text).Append('\n');
                    var found = TextPatternEngine.Find(text, find, options, 1, 0);
                    observedMatches += found.MatchCount;
                    if (found.MatchCount > 0 && (replaceAll || !replacementPlanned))
                    {
                        var edits = TextPatternEngine.PlanReplacements(text, find, replacement, options, replaceAll, maxReplacements);
                        if (edits.Count > 0)
                        {
                            plans.Add(new PptReplacementPlan { Target = target, Edits = edits });
                            replacementPlanned = true;
                        }
                    }
                }
                var replacements = 0;
                foreach (var plan in plans) replacements += plan.Edits.Count;
                if (!string.Equals(expectedHash, TextPatternEngine.Sha256(hash.ToString()), StringComparison.OrdinalIgnoreCase) || observedMatches != expectedMatches)
                    return ToolResult.Fail("PowerPoint search scope changed after preview.", null, "stale_search_scope", true);
                if (replacements > maxReplacements) return ToolResult.Fail("Replacement count exceeds maxReplacements=" + maxReplacements + ".", null, "replacement_limit_exceeded", false);
                for (var p = plans.Count - 1; p >= 0; p--)
                {
                    var plan = plans[p];
                    for (var e = plan.Edits.Count - 1; e >= 0; e--)
                    {
                        var edit = plan.Edits[e];
                        plan.Target.Shape.TextFrame.TextRange.Characters(edit.Index + 1, edit.Length).Text = edit.Text;
                    }
                }
                var verify = SearchCommand(command, find);
                var post = SearchText(verify);
                if (!post.Success) return post;
                var postHash = Convert.ToString(JObject.Parse(post.DataJson ?? "{}")["scopeSha256"]);
                return ToolResult.Ok("PowerPoint replacements completed: " + replacements + ".", JsonConvert.SerializeObject(new { replacements = replacements, scopeSha256 = postHash }));
            }
            catch (TextPatternException ex) { return ToolResult.Fail(ex.Message, null, ex.ErrorCode, false); }
        }

        private ToolCommand SearchCommand(ToolCommand source, string query)
        {
            var command = new ToolCommand { ToolId = "powerpoint.search_text" };
            command.Arguments["query"] = query;
            foreach (var name in new[] { "scope", "slideIndex", "includeNotes", "mode", "matchCase", "wholeWord" })
                if (source.Arguments.ContainsKey(name)) command.Arguments[name] = source.Arguments[name];
            command.Arguments["maxResults"] = 500; command.Arguments["contextChars"] = 80;
            return command;
        }

        private TextPatternOptions PatternOptions(ToolCommand command)
        {
            return new TextPatternOptions { Mode = ToolArgumentReader.String(command.Arguments, "mode", "literal"), MatchCase = ToolArgumentReader.Boolean(command.Arguments, "matchCase", false), WholeWord = ToolArgumentReader.Boolean(command.Arguments, "wholeWord", false) };
        }

        private IEnumerable<PptTextTarget> TextTargets(ToolCommand command)
        {
            var presentation = RequirePresentation();
            var scope = ToolArgumentReader.String(command.Arguments, "scope", "deck");
            var slideIndex = ToolArgumentReader.Int32(command.Arguments, "slideIndex", 0);
            var includeNotes = ToolArgumentReader.Boolean(command.Arguments, "includeNotes", true);
            if (slideIndex < 0 || slideIndex > presentation.Slides.Count)
            {
                throw new InvalidOperationException("slideIndex is outside the presentation: " + slideIndex + ".");
            }
            if (string.Equals(scope, "selection", StringComparison.OrdinalIgnoreCase))
            {
                var activeSlide = TryGetActiveSlide();
                var slide = ResolveSlide(slideIndex <= 0 ? (activeSlide == null ? 1 : activeSlide.SlideIndex) : slideIndex);
                var shape = ResolveSelectedShape(slide);
                if (shape != null) yield return new PptTextTarget { SlideIndex = slide.SlideIndex, Kind = "shape", Shape = shape };
                yield break;
            }
            foreach (PowerPoint.Slide slide in presentation.Slides)
            {
                if (slideIndex > 0 && slide.SlideIndex != slideIndex) continue;
                foreach (PowerPoint.Shape shape in slide.Shapes)
                    if (!string.IsNullOrEmpty(ShapeText(shape))) yield return new PptTextTarget { SlideIndex = slide.SlideIndex, Kind = "shape", Shape = shape };
                if (includeNotes)
                {
                    foreach (PowerPoint.Shape shape in slide.NotesPage.Shapes)
                        if (!string.IsNullOrEmpty(ShapeText(shape))) yield return new PptTextTarget { SlideIndex = slide.SlideIndex, Kind = "notes", Shape = shape };
                }
            }
        }

        private sealed class PptTextTarget { public int SlideIndex { get; set; } public string Kind { get; set; } public PowerPoint.Shape Shape { get; set; } }
        private sealed class PptReplacementPlan { public PptTextTarget Target { get; set; } public List<TextPatternReplacement> Edits { get; set; } }

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
            if (selection == null ||
                selection.Type != PowerPoint.PpSelectionType.ppSelectionShapes ||
                TryGetSelectedShapeCount(selection) <= 0)
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
            var toIndex = ToolArgumentReader.Int32(command.Arguments, "toIndex", 1);
            var slideCount = RequirePresentation().Slides.Count;
            if (toIndex < 1 || toIndex > slideCount)
            {
                throw new InvalidOperationException("toIndex is outside the presentation: " + toIndex + ".");
            }
            slide.MoveTo(toIndex);
            return ToolResult.Ok("Slide moved to " + toIndex);
        }

        private ToolResult ListVbaProjectComponents()
        {
            var presentation = RequirePresentation();
            return VbaProjectSupport.ListProjectComponents(presentation, presentation.Name);
        }

        private ToolResult ReadVbaModule(ToolCommand command)
        {
            return VbaProjectSupport.ReadModule(
                RequirePresentation(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private ToolResult ReadVbaLines(ToolCommand command)
        {
            return VbaProjectSupport.ReadModuleLines(
                RequirePresentation(),
                ToolArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                ToolArgumentReader.Int32(command.Arguments, "startLine", 1),
                ToolArgumentReader.Int32(command.Arguments, "lineCount", 200));
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
                return ToolResult.Fail("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }), "vba_access_error", false);
            }
        }

        private ToolResult RunMacro(ToolCommand command)
        {
            var macroName = ToolArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return ToolResult.Fail("No macroName provided.");
            }

            var output = VbaProjectSupport.RunStringFunction(_application, macroName, ToolArgumentReader.String(command.Arguments, "argumentsJson", "[]"));
            return ToolResult.Ok("Macro ran: " + macroName, JsonConvert.SerializeObject(new { output = output }));
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

            if (slideIndex < 1 || slideIndex > presentation.Slides.Count)
            {
                throw new InvalidOperationException("slideIndex is outside the presentation: " + slideIndex + ".");
            }

            return presentation.Slides[slideIndex];
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
                return string.Equals(
                    DocumentIdentity.RuntimeKey("PowerPoint", parent),
                    DocumentIdentity.RuntimeKey("PowerPoint", presentation),
                    StringComparison.OrdinalIgnoreCase);
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

        private static ToolDefinition Tool(string id, string description, string schema, bool mutatesDocument = false, bool agentCanRun = true, int riskLevel = 0, bool requiresConfirmation = false)
        {
            return new ToolDefinition { Id = id, Host = "PowerPoint", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true, MutatesDocument = mutatesDocument, AgentCanRun = agentCanRun, RiskLevel = riskLevel, RequiresConfirmation = requiresConfirmation };
        }

        private static string Trim(string text, int maxChars)
        {
            maxChars = Math.Max(0, maxChars);
            if (maxChars == 0) return string.Empty;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
