using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // A transient export capability set, not another workspace body or head store.
    internal sealed class HtmlWorkspaceExportService
    {
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceDataPlaneService _data;

        internal HtmlWorkspaceExportService(ResourceGatewayService gateway, ResourceDataPlaneService data)
        { _gateway = gateway; _data = data; }

        internal HtmlResourceExport Open(ChatSession session, string workspaceId, CancellationToken cancellationToken)
        {
            if (session == null || string.IsNullOrEmpty(workspaceId) || session.ActiveHtmlArtifactId != workspaceId)
                throw Error("RESOURCE_ACCESS_DENIED", "Export requires the active exact workspace checkpoint.");
            var sources = session.HtmlWorkspace.DataSources.ToArray();
            if (sources.Length > 32 || sources.Any(item => item == null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 128) ||
                sources.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != sources.Length)
                throw Error("RESOURCE_EXPORT_BOUNDS", "Export requires at most 32 uniquely named bindings.");
            foreach (var source in sources) HtmlWorkspaceToolService.NormalizeBinding(source.Binding, source);
            var opened = new List<HtmlResourceExportBinding>();
            try
            {
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var binding = source.Binding;
                    var reference = binding.Policy == "head" ? new ResourceRef(binding.Resource.Identity.Uri) : binding.Resource.Copy();
                    var lease = _data.Open(session, workspaceId, reference, binding.View, binding.ViewPath, cancellationToken);
                    opened.Add(new HtmlResourceExportBinding { Name = source.Name, Lease = lease });
                }
                // Reads may reconcile external drift. Accept the set only when every
                // head binding agrees with ONE frozen authority tuple after capture.
                // Exact historical bindings deliberately need not equal current heads.
                var authority = _gateway.CaptureAuthorityFor(session, opened.Select(item => item.Lease.Descriptor));
                for (var index = 0; index < sources.Length; index++)
                {
                    var reference = opened[index].Lease.Descriptor.Reference;
                    if (!sources[index].Binding.Resource.Identity.Equals(reference.Identity))
                        throw Error("RESOURCE_REVISION_CHANGED", "Export did not capture the requested resource identity.");
                    if (sources[index].Binding.Policy != "head")
                    {
                        var expected = sources[index].Binding.Resource;
                        if (!expected.Identity.Equals(reference.Identity) || expected.Revision != reference.Revision)
                            throw Error("RESOURCE_REVISION_CHANGED", "Export did not capture the requested exact revision.");
                        continue;
                    }
                    _gateway.RequireCurrent(session, opened[index].Lease.Descriptor, opened[index].Lease.View, authority);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new HtmlResourceExport { Bindings = opened,
                    Generations = authority.Snapshots.ToDictionary(item => item.Key, item => item.Value.Generation) };
            }
            catch
            {
                foreach (var item in opened) _data.Close(session.Id, workspaceId, item.Lease.LeaseId);
                throw;
            }
        }

        private static ResourceRequestException Error(string code, string message)
        { return new ResourceRequestException(message, code, false); }
    }
}
