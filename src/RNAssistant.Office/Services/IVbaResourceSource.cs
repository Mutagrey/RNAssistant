using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal interface IVbaResourceSource
    {
        ToolResult ListResourceModules();
        ToolResult ReadResourceModule(ChatSession session, string moduleName, int maxChars);
    }
}
