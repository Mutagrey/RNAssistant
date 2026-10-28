using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class AuthoredFileToolCatalog
    {
        internal const string CreateToolId = "file.source.create";
        internal const string UpdateToolId = "file.source.update";
        internal const string CopyToolId = "file.source.copy";
        internal const string PatchToolId = "file.source.patch";
        internal static bool Owns(string id)
        { return id == CreateToolId || id == CopyToolId || id == PatchToolId || id == UpdateToolId; }
        internal static ToolBinding BindingFor(string id)
        { return Owns(id) ? new ToolBinding(id + ".intent.v1") : null; }

    }
}
