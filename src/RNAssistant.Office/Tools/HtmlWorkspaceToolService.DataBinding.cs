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
            return "Workspace: Bind a semantic target returned by common.resources_find. " +
                "The workspace stores only a canonical resource reference, view and head/exact policy. " +
                "For Excel chart/report data, bind an Excel range, table, or name target with table/records; an Excel search scope is discovery output, not a tabular data source. " +
                "Page code opens RN.resources.open(name) and consumes bounded read/stream batches. " +
                "head resolves current state on open; exact retains an immutable revision.";
        }

        internal static string BindSchema()
        {
            var schema = new JObject {
                ["type"] = "object",
                ["properties"] = new JObject {
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "Stable binding name opened through RN.resources.", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["target"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024,
                        ["description"] = "Exact semantic target copied verbatim from common.resources_find. It never contains ://; do not construct it from a title." },
                    ["view"] = new JObject { ["type"] = "string", ["description"] = "Bounded resource view. raw returns exact original attachment bytes as inert binary (up to 20 MiB); text is extracted content. Page views use a zero-based page index in path.", ["enum"] = new JArray("text", "source", "table", "records", "raw", "image", "thumbnail", "render-page", "page-thumbnail"), ["default"] = "text" },
                    ["path"] = new JObject { ["type"] = "string", ["description"] = "For table/records use $ or an object-property path such as $.records. Page views require a zero-based integer page index.", ["maxLength"] = 256 },
                    ["policy"] = new JObject { ["type"] = "string", ["description"] = "Resolve current head on open, or retain the exact observed revision.", ["enum"] = new JArray("head", "exact"), ["default"] = "exact" }
                },
                ["required"] = new JArray("name", "target"), ["additionalProperties"] = false,
                ["anyOf"] = new JArray(
                    BindBranch(new[] { "text", "source", "raw", "image", "thumbnail" }),
                    BindBranch(new[] { "table", "records" },
                        @"^\$(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
                        "Root array $ or an explicit object-property path to a record array; brackets, indexes, and wildcards are unsupported.", false),
                    BindBranch(new[] { "render-page", "page-thumbnail" },
                        @"^(0|[1-9][0-9]{0,5})$", "Zero-based page index.", true))
            };
            return schema.ToString(Formatting.None);
        }

        private static JObject BindBranch(string[] views, string pathPattern = null,
            string pathDescription = null, bool requirePath = false)
        {
            var properties = new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                ["target"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 },
                ["view"] = new JObject { ["type"] = "string", ["enum"] = new JArray(views) },
                ["policy"] = new JObject { ["type"] = "string", ["enum"] = new JArray("head", "exact"), ["default"] = "exact" }
            };
            if (pathPattern != null)
                properties["path"] = new JObject { ["type"] = "string", ["description"] = pathDescription,
                    ["pattern"] = pathPattern, ["maxLength"] = 256 };
            var required = new JArray("name", "target");
            if (pathPattern != null) required.Add("view");
            if (requirePath) required.Add("path");
            return new JObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = required, ["additionalProperties"] = false };
        }

        private HtmlWorkspaceToolOutcome BindDataSource(ChatSession session,
            IDictionary<string, object> arguments, Action markDispatchPossible, CancellationToken cancellationToken)
        {
            RequireGateway();
            var name = NormalizeDataName(ToolArgumentReader.String(arguments, "name", string.Empty));
            var target = _resources.ResolveIntentTarget(session, ToolArgumentReader.String(arguments, "target", string.Empty));
            var view = ToolArgumentReader.String(arguments, "view", "text");
            var policy = ToolArgumentReader.String(arguments, "policy", "exact");
            if (policy != "head" && policy != "exact") throw new InvalidOperationException("Invalid binding policy.");
            if (target.Type == "Excel search scope" && (view == "table" || view == "records"))
                throw new ResourceRequestException(
                    "Excel search scopes expose search evidence, not tabular source data. Bind an Excel range, table, or name target returned by common.resources_find.",
                    "RESOURCE_VIEW_UNSUPPORTED", false);
            var requestedPath = ToolArgumentReader.String(arguments, "path", null);
            var binding = new HtmlWorkspaceDataBinding { Resource = target.Reference, Policy = "head", View = view,
                ViewPath = view == "table" || view == "records"
                    ? ResourceGatewayService.ResolveStructuralViewPath(target, requestedPath)
                    : requestedPath };
            var exact = ReadBinding(session, binding, cancellationToken).Resource.Reference;
            binding.Resource = policy == "head" ? new ResourceRef(exact.Identity.Uri) : exact.Copy();
            binding.Policy = policy;
            NormalizeBinding(binding, null);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var id = DataSourceId(name);
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
                WorkspaceMutationJson(session, "data", name), HtmlWorkspaceEffect.VerifiedChange);
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
