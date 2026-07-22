using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office
{
    public static class VbaProjectSupport
    {
        private const int StdModuleType = 1;

        public static object GetVbaProject(object documentObject)
        {
            if (documentObject == null)
            {
                throw new InvalidOperationException("No active document object.");
            }

            try
            {
                return documentObject.GetType().InvokeMember("VBProject", BindingFlags.GetProperty, null, documentObject, null);
            }
            catch (MissingMethodException)
            {
                dynamic target = documentObject;
                return target.VBProject;
            }
        }

        public static string GetSnapshot(object documentObject, string title, int maxChars)
        {
            try
            {
                return Trim(ReadProjectText(GetVbaProject(documentObject), title, maxChars), maxChars);
            }
            catch (Exception ex)
            {
                return "VBA project could not be read. Enable 'Trust access to the VBA project object model'. " + ex.Message;
            }
        }

        public static ToolResult ReadProject(object documentObject, string title, int maxChars)
        {
            dynamic vbProject = GetVbaProject(documentObject);
            var modules = new List<object>();
            foreach (dynamic component in vbProject.VBComponents)
            {
                var code = ReadComponentCode(component);
                modules.Add(new
                {
                    name = (string)component.Name,
                    type = ComponentTypeName((int)component.Type),
                    lineCount = (int)component.CodeModule.CountOfLines,
                    code = Trim(code, maxChars)
                });
            }

            return ToolResult.Ok("VBA project read.", JsonConvert.SerializeObject(new { title = title, modules = modules }));
        }

        public static ToolResult ReadModule(object documentObject, string moduleName, int maxChars)
        {
            dynamic component = FindComponent(GetVbaProject(documentObject), moduleName);
            if (component == null)
            {
                return ToolResult.Fail("VBA module not found: " + moduleName);
            }

            var code = Trim(ReadComponentCode(component), maxChars);
            return ToolResult.Ok("VBA module read: " + component.Name, JsonConvert.SerializeObject(new
            {
                name = (string)component.Name,
                type = ComponentTypeName((int)component.Type),
                lineCount = (int)component.CodeModule.CountOfLines,
                code = code
            }));
        }

        public static ToolResult ReplaceModule(object documentObject, string moduleName, string code, bool createIfMissing)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return ToolResult.Fail("No moduleName provided.");
            }
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("No VBA code provided.");
            }

            dynamic vbProject = GetVbaProject(documentObject);
            dynamic component = FindComponent(vbProject, moduleName);
            if (component == null)
            {
                if (!createIfMissing)
                {
                    return ToolResult.Fail("VBA module not found: " + moduleName);
                }

                component = vbProject.VBComponents.Add(StdModuleType);
                component.Name = moduleName;
            }

            dynamic module = component.CodeModule;
            if ((int)module.CountOfLines > 0)
            {
                module.DeleteLines(1, (int)module.CountOfLines);
            }

            module.AddFromString(code);
            return ToolResult.Ok("VBA module replaced: " + component.Name, JsonConvert.SerializeObject(new { moduleName = (string)component.Name, lineCount = (int)component.CodeModule.CountOfLines }));
        }

        public static ToolResult InsertModule(object documentObject, string moduleName, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("No VBA code provided.");
            }

            dynamic vbProject = GetVbaProject(documentObject);
            dynamic component = null;
            try
            {
                component = vbProject.VBComponents.Add(StdModuleType);
                component.Name = moduleName;
                component.CodeModule.AddFromString(code);
                return ToolResult.Ok("Inserted VBA module: " + moduleName, JsonConvert.SerializeObject(new
                {
                    moduleName = (string)component.Name,
                    lineCount = (int)component.CodeModule.CountOfLines
                }));
            }
            catch
            {
                if (component != null)
                {
                    try
                    {
                        vbProject.VBComponents.Remove(component);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        private static object FindComponent(object vbProjectObject, string moduleName)
        {
            if (vbProjectObject == null || string.IsNullOrWhiteSpace(moduleName))
            {
                return null;
            }

            dynamic vbProject = vbProjectObject;
            foreach (dynamic component in vbProject.VBComponents)
            {
                if (string.Equals((string)component.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return component;
                }
            }

            return null;
        }

        private static string ReadProjectText(object vbProjectObject, string title, int maxChars)
        {
            dynamic vbProject = vbProjectObject;
            var builder = new StringBuilder();
            builder.AppendLine("VBA Project: " + title);
            foreach (dynamic component in vbProject.VBComponents)
            {
                builder.AppendLine();
                builder.AppendLine("===== " + component.Name + " (" + ComponentTypeName((int)component.Type) + ") =====");
                builder.AppendLine(ReadComponentCode(component));
                if (builder.Length >= maxChars)
                {
                    break;
                }
            }

            return builder.ToString();
        }

        private static string ReadComponentCode(dynamic component)
        {
            dynamic module = component.CodeModule;
            return (int)module.CountOfLines <= 0 ? string.Empty : (string)module.Lines[1, (int)module.CountOfLines];
        }

        private static string ComponentTypeName(int type)
        {
            switch (type)
            {
                case 1:
                    return "StdModule";
                case 2:
                    return "ClassModule";
                case 3:
                    return "MSForm";
                case 100:
                    return "Document";
                default:
                    return type.ToString();
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
