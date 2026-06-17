using System;
using System.Collections.Generic;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;
using VBIDE = Microsoft.Vbe.Interop;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office;
using RNAssistant.Office.Skills;

namespace RNAssistant.ExcelAddIn
{
    public sealed class ExcelAdapter : IOfficeApplicationAdapter
    {
        private readonly Excel.Application _application;

        public ExcelAdapter(Excel.Application application)
        {
            _application = application;
        }

        public string HostName { get { return "Excel"; } }

        public string DocumentKey
        {
            get
            {
                var workbook = ActiveWorkbook();
                if (workbook == null)
                {
                    return "Excel:NoWorkbook";
                }

                return string.IsNullOrWhiteSpace(workbook.FullName) ? workbook.Name : workbook.FullName;
            }
        }

        public string DocumentTitle
        {
            get
            {
                var workbook = ActiveWorkbook();
                return workbook == null ? "No workbook" : workbook.Name;
            }
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return new[]
            {
                Skill("excel.workbook_summary", "Return workbook metadata, sheets and used ranges.", "{}"),
                Skill("excel.list_sheets", "List workbook sheet names.", "{}"),
                Skill("excel.read_range", "Read a worksheet range.", "{\"sheet\":\"optional\",\"address\":\"A1:D20\"}"),
                Skill("excel.write_range", "Write a scalar value to a worksheet range.", "{\"sheet\":\"optional\",\"address\":\"A1\",\"value\":\"text\"}"),
                Skill("excel.add_sheet", "Add a new worksheet.", "{\"name\":\"Sheet name\"}"),
                Skill("excel.insert_vba_module", "Insert VBA module when Trust Access to VBA project is enabled; otherwise returns copyable code.", "{\"moduleName\":\"Module1\",\"code\":\"Sub Test()\\nEnd Sub\"}")
            };
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                return "No active workbook.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Workbook: " + workbook.Name);
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                builder.AppendLine("Sheet: " + sheet.Name);
                var used = sheet.UsedRange;
                builder.AppendLine("UsedRange: " + used.Address[false, false]);
                AppendRangeValues(builder, used, maxChars);
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            return Trim(builder.ToString(), maxChars);
        }

        public SkillResult ExecuteSkill(SkillCommand command)
        {
            try
            {
                switch (command.SkillId)
                {
                    case "excel.workbook_summary":
                        return WorkbookSummary();
                    case "excel.list_sheets":
                        return ListSheets();
                    case "excel.read_range":
                        return ReadRange(command);
                    case "excel.write_range":
                        return WriteRange(command);
                    case "excel.add_sheet":
                        return AddSheet(command);
                    case "excel.insert_vba_module":
                        return InsertVbaModule(command);
                    default:
                        return SkillResult.Fail("Unsupported Excel skill: " + command.SkillId);
                }
            }
            catch (Exception ex)
            {
                return SkillResult.Fail(ex.Message);
            }
        }

        private SkillResult WorkbookSummary()
        {
            var workbook = RequireWorkbook();
            var sheets = new List<object>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                sheets.Add(new { name = sheet.Name, usedRange = sheet.UsedRange.Address[false, false] });
            }

            return SkillResult.Ok("Workbook summary collected.", JsonConvert.SerializeObject(new
            {
                name = workbook.Name,
                fullName = workbook.FullName,
                sheets = sheets
            }));
        }

        private SkillResult ListSheets()
        {
            var workbook = RequireWorkbook();
            var names = new List<string>();
            foreach (Excel.Worksheet sheet in workbook.Worksheets)
            {
                names.Add(sheet.Name);
            }

            return SkillResult.Ok("Sheets listed.", JsonConvert.SerializeObject(names));
        }

        private SkillResult ReadRange(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var address = SkillArgumentReader.String(command.Arguments, "address", "A1");
            var range = sheet.Range[address];
            var rows = RangeToRows(range);
            return SkillResult.Ok("Range read: " + sheet.Name + "!" + address, JsonConvert.SerializeObject(rows));
        }

        private SkillResult WriteRange(SkillCommand command)
        {
            var sheet = ResolveSheet(SkillArgumentReader.String(command.Arguments, "sheet", null));
            var address = SkillArgumentReader.String(command.Arguments, "address", "A1");
            var value = SkillArgumentReader.String(command.Arguments, "value", string.Empty);
            sheet.Range[address].Value2 = value;
            return SkillResult.Ok("Wrote value to " + sheet.Name + "!" + address);
        }

        private SkillResult AddSheet(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var name = SkillArgumentReader.String(command.Arguments, "name", "AI Sheet");
            var sheet = (Excel.Worksheet)workbook.Worksheets.Add();
            sheet.Name = name;
            return SkillResult.Ok("Added sheet: " + name);
        }

        private SkillResult InsertVbaModule(SkillCommand command)
        {
            var workbook = RequireWorkbook();
            var moduleName = SkillArgumentReader.String(command.Arguments, "moduleName", "RNAssistantModule");
            var code = SkillArgumentReader.String(command.Arguments, "code", string.Empty);
            if (string.IsNullOrWhiteSpace(code))
            {
                return SkillResult.Fail("No VBA code provided.");
            }

            try
            {
                VBIDE.VBProject vbProject = workbook.VBProject;
                VBIDE.VBComponent component = vbProject.VBComponents.Add(VBIDE.vbext_ComponentType.vbext_ct_StdModule);
                component.Name = moduleName;
                component.CodeModule.AddFromString(code);
                return SkillResult.Ok("Inserted VBA module: " + moduleName);
            }
            catch (Exception ex)
            {
                return SkillResult.Ok("VBA insert was blocked. Enable 'Trust access to the VBA project object model' or copy the code manually. " + ex.Message, JsonConvert.SerializeObject(new { moduleName = moduleName, code = code }));
            }
        }

        private Excel.Workbook ActiveWorkbook()
        {
            try { return _application.ActiveWorkbook; }
            catch { return null; }
        }

        private Excel.Workbook RequireWorkbook()
        {
            var workbook = ActiveWorkbook();
            if (workbook == null)
            {
                throw new InvalidOperationException("No active workbook.");
            }

            return workbook;
        }

        private Excel.Worksheet ResolveSheet(string name)
        {
            var workbook = RequireWorkbook();
            if (string.IsNullOrWhiteSpace(name))
            {
                return (Excel.Worksheet)_application.ActiveSheet;
            }

            return (Excel.Worksheet)workbook.Worksheets[name];
        }

        private static SkillDefinition Skill(string id, string description, string schema)
        {
            return new SkillDefinition { Id = id, Host = "Excel", Name = id, Description = description, ArgumentSchemaJson = schema, BuiltIn = true, Enabled = true };
        }

        private static List<List<object>> RangeToRows(Excel.Range range)
        {
            var rows = new List<List<object>>();
            object value = range.Value2;
            var array = value as object[,];
            if (array == null)
            {
                rows.Add(new List<object> { value });
                return rows;
            }

            for (var r = array.GetLowerBound(0); r <= array.GetUpperBound(0); r++)
            {
                var row = new List<object>();
                for (var c = array.GetLowerBound(1); c <= array.GetUpperBound(1); c++)
                {
                    row.Add(array[r, c]);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static void AppendRangeValues(StringBuilder builder, Excel.Range range, int maxChars)
        {
            foreach (var row in RangeToRows(range))
            {
                builder.AppendLine(string.Join("\t", row));
                if (builder.Length >= maxChars)
                {
                    return;
                }
            }
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
