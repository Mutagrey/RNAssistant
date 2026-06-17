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

                return string.IsNullOrWhiteSpace(presentation.FullName) ? presentation.Name : presentation.FullName;
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
                Skill("powerpoint.replace_selection_text", "Replace text in selected shape.", "{\"text\":\"Replacement text\"}")
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

        private string ReadSlidesText(PowerPoint.Presentation presentation, int maxSlides)
        {
            var builder = new StringBuilder();
            var count = Math.Min(presentation.Slides.Count, Math.Max(1, maxSlides));
            for (var i = 1; i <= count; i++)
            {
                var slide = presentation.Slides[i];
                builder.AppendLine("Slide " + i + ":");
                foreach (PowerPoint.Shape shape in slide.Shapes)
                {
                    if (shape.HasTextFrame == MsoTriState.msoTrue && shape.TextFrame.HasText == MsoTriState.msoTrue)
                    {
                        builder.AppendLine(shape.TextFrame.TextRange.Text);
                    }
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
