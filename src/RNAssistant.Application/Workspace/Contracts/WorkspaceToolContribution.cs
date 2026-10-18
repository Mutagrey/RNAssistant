using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
namespace RNAssistant.Runtime.Contracts
{
    public sealed class WorkspaceToolContribution
    {
        public ToolCatalogEntry Entry { get; set; }
        public IToolHandler Handler { get; set; }
    }
}
