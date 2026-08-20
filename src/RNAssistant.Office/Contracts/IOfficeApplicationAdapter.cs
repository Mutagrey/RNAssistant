using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public interface IOfficeApplicationAdapter
    {
        string HostName { get; }
        string DocumentKey { get; }
        string RuntimeDocumentKey { get; }
        string DocumentTitle { get; }
        string GetDocumentSnapshot(int maxChars);
        void PrepareForContextCapture();
        ContextNote CaptureSelectionContext(string mode, int maxChars);
        IEnumerable<ToolDefinition> GetBuiltInTools();
        ToolResult ExecuteTool(ToolCommand command);
    }

    public interface IOfficeContextProvider
    {
        OfficeContext GetOfficeContext();
    }

    public interface IOfficeBuiltInSkillProvider
    {
        IEnumerable<SkillDefinition> GetBuiltInSkills();
    }

    public interface IOfficeDocumentCatalog
    {
        IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments();
        bool ActivateDocument(string documentKey);
        bool OpenDocument(string path);
    }
}
