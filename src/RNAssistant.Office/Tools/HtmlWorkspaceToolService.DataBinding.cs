using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        internal string BuildBindDescription()
        {
            return "Workspace: Bind a source target returned by common.resources_find or a binding's sourceTarget. " +
                "The workspace stores only a canonical resource reference, view and head/exact policy. " +
                "HTML workspace and HTML data targets describe the workspace/binding, not its source values; never bind them. html_data_write already creates a text binding. " +
                "For Excel chart/report data, bind an Excel range, table, or name target with table/records; an Excel search scope is discovery output, not a tabular data source. " +
                "For an uploaded spreadsheet file, bind its original file target with raw and parse bounded bytes locally with the HTML skill's XLSX vendor; this does not replace live Excel range authority. " +
                "Page code opens RN.resources.open(name) and consumes bounded read/stream batches. " +
                "Choose view explicitly. policy=head (default) resolves current state on open; exact retains an immutable revision.";
        }

        internal static string BindSchema()
        { return ResourceSelectorContract.NamedInput(true).ToString(Formatting.None); }

        private HtmlWorkspaceToolOutcome BindDataSource(ChatSession session,
            IDictionary<string, object> arguments, Action markDispatchPossible, CancellationToken cancellationToken)
        {
            RequireGateway();
            var name = NormalizeDataName(ToolArgumentReader.String(arguments, "name", string.Empty));
            var target = _resources.ResolveIntentTarget(session, ToolArgumentReader.String(arguments, "target", string.Empty));
            if (target.Type == "HTML workspace" || target.Type == "HTML data")
                throw new ResourceRequestException(
                    "This target describes an HTML workspace or an existing binding, not source values. " +
                    "html_data_write already binds JSON: use RN.resources.open(name). To change its view, read the HTML data target as text and bind its sourceTarget instead.",
                    "html_binding_target_invalid", false);
            var view = ToolArgumentReader.String(arguments, "view", "text");
            var policy = ToolArgumentReader.String(arguments, "policy", "head");
            if (policy != "head" && policy != "exact") throw new InvalidOperationException("Invalid binding policy.");
            if (target.Type == "Excel search scope" && (view == "table" || view == "records"))
                throw new ResourceRequestException(
                    "Excel search scopes expose search evidence, not tabular source data. Bind an Excel range, table, or name target returned by common.resources_find.",
                    "RESOURCE_VIEW_UNSUPPORTED", false);
            var requestedPath = ToolArgumentReader.String(arguments, "path", null);
            var binding = new HtmlWorkspaceDataBinding { Resource = target.Reference, Policy = "head", View = view,
                ViewPath = ResourceSelectorContract.ResolvePath(target, view, requestedPath,
                    arguments.ContainsKey("pageIndex") ? (int?)ToolArgumentReader.Int32(arguments, "pageIndex", 0) : null) };
            var exact = ReadBinding(session, binding, cancellationToken).Resource.Reference;
            binding.Resource = policy == "head" ? new ResourceRef(exact.Identity.Uri) : exact.Copy();
            binding.Policy = policy;
            NormalizeBinding(binding, null);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var id = DataSourceId(name);
            var previous = workspace.DataSources.SingleOrDefault(item => item.Id == id)?.Binding;
            if (previous != null && previous.Policy == binding.Policy && previous.View == binding.View &&
                previous.ViewPath == binding.ViewPath && previous.Resource.Uri == binding.Resource.Uri &&
                previous.Resource.Revision == binding.Resource.Revision)
                return HtmlWorkspaceToolOutcome.Ok("HTML resource is already bound: " + name + ".",
                    WorkspaceMutationJson(session, "data", name, target.Descriptor), HtmlWorkspaceEffect.VerifiedNoChange);
            ValidateWorkspaceCapacity(workspace, null, null, id, null);
            markDispatchPossible();
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var data = session.HtmlWorkspace.DataSources.SingleOrDefault(item => item.Id == id);
            if (data == null)
            {
                data = new HtmlWorkspaceDataSource { Id = id, Name = name };
                session.HtmlWorkspace.DataSources.Add(data);
            }
            data.Binding = binding; data.UpdatedUtc = DateTime.UtcNow;
            session.HtmlWorkspace.UpdatedUtc = data.UpdatedUtc;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML resource binding: " + name);
            return HtmlWorkspaceToolOutcome.Ok("HTML resource bound: " + name + ".",
                WorkspaceMutationJson(session, "data", name, target.Descriptor), HtmlWorkspaceEffect.VerifiedChange);
        }

        private HtmlWorkspaceToolOutcome RefreshDataSources(ChatSession session,
            IDictionary<string, object> arguments, Action markDispatchPossible, CancellationToken cancellationToken)
        {
            RequireGateway();
            var name = ToolArgumentReader.String(arguments, "name", string.Empty);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var targets = string.IsNullOrWhiteSpace(name) ? workspace.DataSources :
                new List<HtmlWorkspaceDataSource> { FindDataSource(workspace, name) };
            foreach (var source in targets) ReadBinding(session, source.Binding, cancellationToken);
            // A read may observe source drift; only source authority publishes it.
            // No workspace payload/status refresh and no new workspace revision.
            return HtmlWorkspaceToolOutcome.Ok("Resource bindings resolved. Reopen handles to read their current revisions.",
                WorkspaceMutationJson(session, "data", name), HtmlWorkspaceEffect.VerifiedNoChange);
        }

        private HtmlWorkspaceToolOutcome FreezeDataSource(ChatSession session,
            IDictionary<string, object> arguments, Action markDispatchPossible)
        {
            RequireGateway();
            var name = ToolArgumentReader.String(arguments, "name", string.Empty);
            var source = FindDataSource(NormalizedWorkspaceCopy(session.HtmlWorkspace), name);
            if (source.Binding.Policy == "exact")
                return HtmlWorkspaceToolOutcome.Ok("Resource is already revision-pinned.",
                    WorkspaceMutationJson(session, "data", name), HtmlWorkspaceEffect.VerifiedNoChange);
            var exact = ReadBinding(session, source.Binding, CancellationToken.None).Resource.Reference;
            markDispatchPossible();
            var current = FindDataSource(session.HtmlWorkspace, name);
            current.Binding.Resource = exact.Copy(); current.Binding.Policy = "exact";
            current.UpdatedUtc = DateTime.UtcNow;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML resource frozen: " + name);
            return HtmlWorkspaceToolOutcome.Ok("Resource pinned to an exact revision: " + name + ".",
                WorkspaceMutationJson(session, "data", name), HtmlWorkspaceEffect.VerifiedChange);
        }

        private ResourceReadResult ReadBinding(ChatSession session, HtmlWorkspaceDataBinding binding, CancellationToken cancellationToken)
        {
            NormalizeBinding(binding, null);
            cancellationToken.ThrowIfCancellationRequested();
            var reference = binding.Policy == "head" ? new ResourceRef(binding.Resource.Identity.Uri) : binding.Resource.Copy();
            return _resources.Read(session, new ResourceReadRequest {
                Reference = reference, Representation = binding.View, ViewPath = binding.ViewPath, MaxChars = 1, MaxRows = 1 }).Result;
        }

        internal static void NormalizeBinding(HtmlWorkspaceDataBinding binding, HtmlWorkspaceDataSource dataSource)
        {
            if (binding?.Resource == null || binding.Policy != "head" && binding.Policy != "exact" ||
                binding.Policy == "exact" && !binding.Resource.IsExact)
                throw new InvalidOperationException("HTML_RESOURCE_BINDING_INVALID: explicit canonical head/exact binding required.");
            if (string.IsNullOrWhiteSpace(binding.View) || binding.View.Length > 64)
                throw new InvalidOperationException("HTML_RESOURCE_VIEW_INVALID: an explicit bounded view is required.");
            if (binding.ViewPath != null && (binding.ViewPath.Length > 256 || binding.View != "table" && binding.View != "records" &&
                binding.View != "render-page" && binding.View != "page-thumbnail"))
                throw new InvalidOperationException("HTML_RESOURCE_VIEW_INVALID: path belongs to a bounded structural view.");
        }

        private void RequireGateway()
        {
            if (_resources == null) throw new InvalidOperationException("Resource gateway is unavailable.");
        }
    }
}
