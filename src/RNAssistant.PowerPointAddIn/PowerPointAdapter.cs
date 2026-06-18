using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Office.Core;
using Newtonsoft.Json;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Skills;

namespace RNAssistant.PowerPointAddIn
{
    public sealed class PowerPointAdapter : IOfficeApplicationAdapter
    {
        private readonly PowerPoint.Application _application;

        public PowerPointAdapter(PowerPoint.Application application)
        {
            _application = application;
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

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                Skill("powerpoint.read_slides", "Read text from slides.", "{\"maxSlides\":20}"),
                Skill("powerpoint.add_slide", "Add a text slide.", "{\"title\":\"Slide title\",\"body\":\"Slide body\"}"),
                Skill("powerpoint.replace_selection_text", "Replace text in selected shape.", "{\"text\":\"Replacement text\"}"),
                Skill("powerpoint.vba_read_project", "Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("powerpoint.vba_read_module", "Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("powerpoint.vba_replace_module", "Replace a VBA module source code; RNAssistant stores rollback backups before replacement.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}"),
                Skill("powerpoint.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}"),
                Skill("powerpoint.run_macro", "Run a PowerPoint VBA macro by name.", "{\"macroName\":\"Module1.Test\"}")
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

        public SkillResult ExecuteSkill(SkillCommand command)
        {
            try
            {
                switch (command.SkillId)
                {
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
                        return SkillResult.Fail("Unsupported PowerPoint skill: " + command.SkillId);
                }
            }
            catch (Exception ex)
            {
                return SkillResult.Fail(ex.Message);
            }
        }

        private SkillResult ReadSlides(SkillCommand command)
        {
            var maxSlides = SkillArgumentReader.Int32(command.Arguments, "maxSlides", 20);
            return SkillResult.Ok("Slides read.", JsonConvert.SerializeObject(new { text = ReadSlidesText(RequirePresentation(), maxSlides) }));
        }

        private SkillResult AddSlide(SkillCommand command)
        {
            var presentation = RequirePresentation();
            var title = SkillArgumentReader.String(command.Arguments, "title", "AI slide");
            var body = SkillArgumentReader.String(command.Arguments, "body", string.Empty);
            var slide = presentation.Slides.Add(presentation.Slides.Count + 1, PowerPoint.PpSlideLayout.ppLayoutText);
            slide.Shapes.Title.TextFrame.TextRange.Text = title;
            if (slide.Shapes.Count >= 2)
            {
                slide.Shapes[2].TextFrame.TextRange.Text = body;
            }
            return SkillResult.Ok("Slide added: " + title);
        }

        private SkillResult ReplaceSelectionText(SkillCommand command)
        {
            var text = SkillArgumentReader.String(command.Arguments, "text", string.Empty);
            var selection = _application.ActiveWindow.Selection;
            if (selection == null || selection.Type != PowerPoint.PpSelectionType.ppSelectionShapes)
            {
                return SkillResult.Fail("Select a text shape first.");
            }

            var shape = selection.ShapeRange[1];
            if (shape.HasTextFrame != MsoTriState.msoTrue)
            {
                return SkillResult.Fail("Selected shape has no text frame.");
            }

            shape.TextFrame.TextRange.Text = text;
            return SkillResult.Ok("Selected shape text replaced.");
        }

        private SkillResult ReadVbaProject(SkillCommand command)
        {
            var presentation = RequirePresentation();
            return VbaProjectSupport.ReadProject(presentation, presentation.Name, SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private SkillResult ReadVbaModule(SkillCommand command)
        {
            return VbaProjectSupport.ReadModule(
                RequirePresentation(),
                SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private SkillResult ReplaceVbaModule(SkillCommand command)
        {
            return VbaProjectSupport.ReplaceModule(
                RequirePresentation(),
                SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                SkillArgumentReader.String(command.Arguments, "code", string.Empty),
                SkillArgumentReader.Boolean(command.Arguments, "createIfMissing", true));
        }

        private SkillResult InsertVbaModule(SkillCommand command)
        {
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = SkillArgumentReader.String(command.Arguments, "code", string.Empty);
            try
            {
                return VbaProjectSupport.InsertModule(RequirePresentation(), moduleName, code);
            }
            catch (Exception ex)
            {
                return SkillResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private SkillResult RunMacro(SkillCommand command)
        {
            var macroName = SkillArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace(macroName))
            {
                return SkillResult.Fail("No macroName provided.");
            }

            _application.Run(macroName);
            return SkillResult.Ok("Macro ran: " + macroName);
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
            try { return _application.ActivePresentation; }
            catch { return null; }
        }

        private PowerPoint.Presentation RequirePresentation()
        {
            var presentation = ActivePresentation();
            if (presentation == null)
            {
                throw new InvalidOperationException("No active presentation.");
            }
            return presentation;
        }

        private static SkillDefinition Skill(string id, string description, string schema)
        {
            return new SkillDefinition { Id = id, Host = "PowerPoint", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true };
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
