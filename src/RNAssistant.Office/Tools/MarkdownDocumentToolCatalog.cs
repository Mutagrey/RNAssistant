using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class MarkdownDocumentToolCatalog
    {
        internal const string SaveToolId = "markdown.save";
        internal const string RestoreToolId = "markdown.restore";
        internal static bool Owns(string id) { return id == SaveToolId || id == RestoreToolId; }
        internal static ToolBinding BindingFor(string id) { return Owns(id) ? new ToolBinding(id + ".intent.v1") : null; }
    }
}
