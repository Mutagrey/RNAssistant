using System.Collections.Generic;
using System.Threading;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceSearchToolHandler : ResourceToolHandlerBase, IToolContinuationOwner
    {
        private readonly RNAssistant.Core.Services.ResourceTextSearch _search = new RNAssistant.Core.Services.ResourceTextSearch();
        private readonly RNAssistant.Core.Services.ResourceSearchObservation _observation;
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.SearchToolId, RNAssistant.Core.Services.ResourceModelSearchSchema.Description,
            RNAssistant.Core.Services.ResourceModelSearchSchema.Create(false, true));
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, false, new[] { "agent", "plan", "chat" }, evidenceEligibility: EvidenceEligibility.None);
        internal static readonly ToolBinding Binding = new ToolBinding("resources.search.v3");

        internal ResourceSearchToolHandler(ResourceGatewayService gateway, RNAssistant.Core.Models.ChatSession session, RNAssistant.Core.Services.ResourceSearchObservation observation = null)
            : base(gateway, session) { _observation = observation; }

        public bool OwnsResult(string runId, string callId) => _search.OwnsResult(runId, callId);
        public bool CanContinue(IDictionary<string, object> arguments) => _search.CanContinue(arguments);

        protected override ToolHandlerResult Execute(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            var request = RNAssistant.Core.Services.ResourceModelSearchSchema.Parse(context.Arguments);
            var page = Gateway.SearchText(Session, request, _search, cancellationToken);
            _search.RecordResult(context.Execution.RunId, context.Execution.Call.Id, page);
            _observation?.Record(request, page, context.Execution.RunId + ":" + context.Execution.StepId);
            return Completed(RuntimeResult.Ok(page.ScanComplete && page.SourceComplete
                ? "The selected text scope was searched completely."
                : "Search is incomplete; inspect reasons or continue. Absence is not established.",
                Serialize(page), page.ExactReferences));
        }
    }
}
