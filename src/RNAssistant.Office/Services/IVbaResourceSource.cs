using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal interface IVbaResourceSource
    {
        ToolRunResult ListResourceModules();
        ToolRunResult ReadResourceModule(ChatSession session, string moduleName, int maxChars);
    }
}
