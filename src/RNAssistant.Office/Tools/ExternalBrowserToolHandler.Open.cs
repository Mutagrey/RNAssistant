using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class ExternalBrowserToolHandler
    {
        private static ToolCatalogEntry OpenCatalog()
        {
            var descriptor = new ToolDescriptor(Open,
                "Open one exact local Browser source returned by core.find in this run. The source belongs to this conversation; opening grants no extra data access and neither saves nor activates a version. " +
                "A result confirms the visible Dock tab only; execution/read/input availability is explicit. Unknown opens must not be retried; close the tab manually first.",
                "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512,\"description\":\"Exact Browser source semantic target returned by core.find in this run.\"}},\"required\":[\"target\"],\"additionalProperties\":false}");
            return ControllerToolCatalogEntry.CreateTypedProjection(descriptor,
                new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1, manualVerificationAfterUnknown: true),
                BindingFor(Open), scope: "session", mutatesLocalState: true);
        }
        private ToolPreparationResult PrepareOpen(ToolHandlerContext context, CancellationToken token)
        {
            var source = _source?.Invoke() as IBrowserOpenSource;
            var owner = ExternalBrowserResourceProvider.Owner(_session);
            var name = ToolArgumentReader.String(context.Arguments, "target", "");
            if (IsRelativePathTarget(name))
                throw Error("target is a file path, not a Browser source. Run discovery and pass the exact returned target: " +
                    DiscoveryHint(name) + ". In an ordinary Workspace without a Browser source, discover html.project.register first and declare the existing entry and members.", "browser_target_path");
            var targets = source?.OpenableTargetsFor(owner).Where(t => t.SemanticTarget == name).ToArray() ?? new BrowserOpenableTarget[0];
            if (targets.Length > 1) throw Error("Several sources share this name. Select an unambiguous source; no active-tab fallback is allowed. " + DiscoveryHint(name), "resource_target_ambiguous");
            if (targets.Length != 1) throw Error("No exact local Browser source matches this target in the current owner. " + DiscoveryHint(name) +
                (string.IsNullOrWhiteSpace(_session.WorkspaceId) ? "" :
                    " If the page exists but no source is listed, discover html.project.register, register its entryPath and members, then repeat core.find in this run."), "browser_source_unavailable");
            var selected = targets[0]; var reference = ExternalBrowserResourceProvider.SourceReference(_session, selected);
            if (!_session.Messages.Any(m => m.RunId == context.Execution.RunId && m.ProtocolMessage && m.Role == ToolResultRoles.User &&
                m.ToolResultRole == ToolResultRoles.User && m.ToolName == ResourceToolCatalog.FindToolId &&
                m.ResourceRefs != null && m.ResourceRefs.Any(r => r.Uri == reference.Uri)))
                throw Error("The source must be delivered by core.find in the current run before opening.", "browser_discovery_required");
            using (_documentAccess?.Invoke(_session)) { }
            token.ThrowIfCancellationRequested();
            var operation = "browser-open-" + TextPatternEngine.Sha256(_session.Id + "\n" + context.Execution.RunId + "\n" + context.Execution.StepId + "\n" + context.Execution.Call.Id);
            return new ToolPreparationResult(RuntimeResult.Ok("The exact local source is prepared for visible Dock attachment."),
                JsonConvert.SerializeObject(new ExternalBrowserPrepared { PreparationId = operation,
                    Observation = new ResourceRef(ResourceUri.Create("browser", _session.Id, "open", operation), "prepared"),
                    Open = new BrowserAgentOpenRequest { OwnerScope = owner, Target = selected.Target, OperationId = operation,
                        RunId = context.Execution.RunId, CallId = context.Execution.Call.Id } }));
        }
        private ToolHandlerResult ExecuteOpen(ToolHandlerContext context, CancellationToken token)
        {
            var source = _source?.Invoke() as IBrowserOpenSource;
            ExternalBrowserPrepared prepared = null;
            try
            {
                prepared = JsonConvert.DeserializeObject<ExternalBrowserPrepared>(context.PreparedStateJson);
                if (source == null || prepared?.Open?.OwnerScope?.ChatId != _session.Id || prepared.Open.RunId != context.Execution.RunId ||
                    prepared.Open.CallId != context.Execution.Call.Id || !prepared.Open.OwnerScope.Matches(ExternalBrowserResourceProvider.Owner(_session)))
                    throw Error("The prepared Browser source belongs to another run or conversation.", "browser_document_stale");
                using (_documentAccess?.Invoke(_session)) { }
                var page = source.OpenAgentAsync(prepared.Open, context.MarkDispatchPossible, token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                if (source != _source() || !page.Attached || page.Status == BrowserPageStatus.Closed || page.Target?.State != BrowserTargetState.Ready ||
                    page.OperationId != prepared.Open.OperationId || !page.OwnerScope.Matches(prepared.Open.OwnerScope) ||
                    page.Target.SavedVersionId != prepared.Open.Target.SavedVersionId || page.Url != prepared.Open.Target.DisplayLocator)
                    throw Error("The exact Browser attachment could not be verified.", "browser_effect_unknown");
                using (_documentAccess?.Invoke(_session)) { }
                var ready = page.Status == BrowserPageStatus.Ready;
                var result = new ToolHandlerResult(RuntimeResult.Ok(ready ? "The exact Browser tab is attached. Discover its live page target and read it before acting." :
                    "The exact source tab is attached. Page execution, semantic page reads, media and input are unavailable in the current host.",
                    JsonConvert.SerializeObject(new OpenResult { Status = ready ? "ready" : "source_only", ExecutionAvailable = ready })), ToolEffectEvidence.VerifiedChange);
                return context.Complete(result);
            }
            catch (Exception error)
            {
                if (context.MayHaveDispatched) source?.BlockAgentOpen(prepared?.Open?.OperationId);
                context.CompleteFailure(error); throw;
            }
        }

        private static bool IsRelativePathTarget(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !value.StartsWith("Browser source: ", StringComparison.Ordinal) &&
                (value.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0);
        }

        private string DiscoveryHint(string target)
        {
            var query = target ?? string.Empty;
            var separator = Math.Max(query.LastIndexOf('/'), query.LastIndexOf('\\'));
            if (separator >= 0) query = query.Substring(separator + 1);
            if (query.Length > 120) query = query.Substring(0, 120);
            var request = string.IsNullOrWhiteSpace(_session.WorkspaceId)
                ? new Newtonsoft.Json.Linq.JObject { ["scope"] = "browser", ["query"] = query }
                : new Newtonsoft.Json.Linq.JObject { ["type"] = "browser", ["query"] = query };
            return "core.find " + request.ToString(Formatting.None);
        }

        private sealed class OpenResult
        {
            [JsonProperty("status")] public string Status { get; set; }
            [JsonProperty("executionAvailable")] public bool ExecutionAvailable { get; set; }
        }
    }
}
