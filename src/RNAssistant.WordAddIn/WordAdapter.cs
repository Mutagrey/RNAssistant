using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Word = Microsoft.Office.Interop.Word;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Skills;

namespace RNAssistant.WordAddIn
{
    public sealed class WordAdapter : IOfficeApplicationAdapter
    {
        private readonly Word.Application _application;

        public WordAdapter(Word.Application application)
        {
            _application = application;
        }

        public string HostName { get { return "Word"; } }

        public string DocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                if (doc == null)
                {
                    return "Word:NoDocument";
                }

                return DocumentIdentity.ForOfficeDocument(
                    HostName,
                    doc.Path,
                    RuntimeDocumentKey,
                    () => doc.CustomDocumentProperties);
            }
        }

        public string RuntimeDocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                return doc == null ? "Word:NoDocument" : "Word:Runtime:" + doc.GetHashCode().ToString("x");
            }
        }

        public string LegacyDocumentKey
        {
            get
            {
                var doc = ActiveDocument();
                if (doc == null)
                {
                    return "Word:NoDocument";
                }

                return string.IsNullOrWhiteSpace(doc.FullName) ? RuntimeDocumentKey : doc.FullName;
            }
        }

        public string DocumentTitle
        {
            get
            {
                var doc = ActiveDocument();
                return doc == null ? "No document" : doc.Name;
            }
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                Skill("word.read_document", "Read current document text.", "{\"maxChars\":12000}"),
                Skill("word.read_selection", "Read current Word selection.", "{}"),
                Skill("word.insert_text", "Insert text at current cursor position.", "{\"text\":\"Text to insert\"}"),
                Skill("word.replace_selection", "Replace selected text.", "{\"text\":\"Replacement text\"}"),
                Skill("word.add_comment", "Add a comment to the current selection.", "{\"text\":\"Comment text\"}"),
                Skill("word.vba_read_project", "Read VBA project modules and source code when Trust Access to VBA project is enabled.", "{\"maxChars\":30000}"),
                Skill("word.vba_read_module", "Read one VBA module by name.", "{\"moduleName\":\"Module1\",\"maxChars\":30000}"),
                Skill("word.vba_replace_module", "Replace a VBA module source code; RNAssistant stores rollback backups before replacement.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\",\"createIfMissing\":true}"),
                Skill("word.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}"),
                Skill("word.run_macro", "Run a Word VBA macro by name.", "{\"macroName\":\"Module1.Test\"}")
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                return "No active Word document.";
            }

            return Trim(doc.Range().Text, maxChars);
        }

        public string GetVbaSnapshot(int maxChars)
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                return "No active Word document.";
            }

            return VbaProjectSupport.GetSnapshot(doc, doc.Name, maxChars);
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            var doc = RequireDocument();
            if (_application.Selection == null || _application.Selection.Range == null)
            {
                throw new InvalidOperationException("Select Word text first.");
            }

            var range = _application.Selection.Range;
            var referenceOnly = string.Equals(mode, "reference", StringComparison.OrdinalIgnoreCase);
            var reference = doc.Name + " chars " + range.Start + "-" + range.End;
            var selectedText = Trim(range.Text, maxChars);
            if (string.IsNullOrWhiteSpace(selectedText) && !referenceOnly)
            {
                throw new InvalidOperationException("Select Word text first.");
            }

            var text = referenceOnly
                ? "Reference only. Use Word tools with the current selection/document if exact text is needed."
                : selectedText;

            return new ContextNote
            {
                Host = HostName,
                Kind = referenceOnly ? "text-reference" : "text-selection",
                Title = "Word selection",
                Reference = reference,
                Source = reference,
                Text = text,
                Preview = Trim(text, 360),
                DetailsJson = JsonConvert.SerializeObject(new
                {
                    document = doc.Name,
                    start = range.Start,
                    end = range.End,
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
                    case "word.read_document":
                        return ReadDocument(command);
                    case "word.read_selection":
                        return SkillResult.Ok("Selection read.", JsonConvert.SerializeObject(new { text = SelectionText() }));
                    case "word.insert_text":
                        _application.Selection.TypeText(SkillArgumentReader.String(command.Arguments, "text", string.Empty));
                        return SkillResult.Ok("Text inserted.");
                    case "word.replace_selection":
                        _application.Selection.Range.Text = SkillArgumentReader.String(command.Arguments, "text", string.Empty);
                        return SkillResult.Ok("Selection replaced.");
                    case "word.add_comment":
                        _application.ActiveDocument.Comments.Add(_application.Selection.Range, SkillArgumentReader.String(command.Arguments, "text", string.Empty));
                        return SkillResult.Ok("Comment added.");
                    case "word.vba_read_project":
                        return ReadVbaProject(command);
                    case "word.vba_read_module":
                        return ReadVbaModule(command);
                    case "word.vba_replace_module":
                        return ReplaceVbaModule(command);
                    case "word.insert_vba_module":
                        return InsertVbaModule(command);
                    case "word.run_macro":
                        return RunMacro(command);
                    default:
                        return SkillResult.Fail("Unsupported Word skill: " + command.SkillId);
                }
            }
            catch (Exception ex)
            {
                return SkillResult.Fail(ex.Message);
            }
        }

        private SkillResult ReadDocument(SkillCommand command)
        {
            var maxChars = SkillArgumentReader.Int32(command.Arguments, "maxChars", 12000);
            var doc = RequireDocument();
            return SkillResult.Ok("Document read.", JsonConvert.SerializeObject(new { text = Trim(doc.Range().Text, maxChars) }));
        }

        private SkillResult ReadVbaProject(SkillCommand command)
        {
            var doc = RequireDocument();
            return VbaProjectSupport.ReadProject(doc, doc.Name, SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private SkillResult ReadVbaModule(SkillCommand command)
        {
            return VbaProjectSupport.ReadModule(
                RequireDocument(),
                SkillArgumentReader.String(command.Arguments, "moduleName", string.Empty),
                SkillArgumentReader.Int32(command.Arguments, "maxChars", 30000));
        }

        private SkillResult ReplaceVbaModule(SkillCommand command)
        {
            return VbaProjectSupport.ReplaceModule(
                RequireDocument(),
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
                return VbaProjectSupport.InsertModule(RequireDocument(), moduleName, code);
            }
            catch (Exception ex)
            {
                return SkillResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private SkillResult RunMacro(SkillCommand command)
        {
            object macroName = SkillArgumentReader.String(command.Arguments, "macroName", string.Empty);
            if (string.IsNullOrWhiteSpace((string)macroName))
            {
                return SkillResult.Fail("No macroName provided.");
            }

            _application.Run(ref macroName);
            return SkillResult.Ok("Macro ran: " + macroName);
        }

        private string SelectionText()
        {
            try
            {
                return _application.Selection == null ? string.Empty : _application.Selection.Text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private Word.Document ActiveDocument()
        {
            try { return _application.ActiveDocument; }
            catch { return null; }
        }

        private Word.Document RequireDocument()
        {
            var doc = ActiveDocument();
            if (doc == null)
            {
                throw new InvalidOperationException("No active Word document.");
            }
            return doc;
        }

        private static SkillDefinition Skill(string id, string description, string schema)
        {
            return new SkillDefinition { Id = id, Host = "Word", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true };
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
