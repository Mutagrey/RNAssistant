using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Office.Services
{
    internal interface IVbaResourceSource
    {
        ToolRunResult ListResourceModules();
        ToolRunResult ReadResourceModule(ChatSession session, string moduleName, int maxChars);
        VbaModuleLineSnapshot ReadResourceModuleLines(ChatSession session, string moduleName,
            int startLine, int lineCount, int maxChars);
    }
}
