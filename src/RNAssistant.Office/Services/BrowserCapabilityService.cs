using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Office.Services
{
    // The same Browser gateway/actions as Office, with no borrowed Office target or COM gate.
    internal sealed class BrowserCapabilityService : IBrowserCapabilityContribution
    {
        private readonly AppDataPaths _paths;
        private readonly Func<IBrowserReadSource> _source;
        internal BrowserCapabilityService(AppDataPaths paths, Func<IBrowserReadSource> source) { _paths = paths; _source = source; }
        public BrowserCapabilitySet ForOwner(BrowserOwnerScope owner, ChatSession session, AppSettings settings,
            string runId, Action<string, IReadOnlyList<ChatAttachment>> captureMedia)
        {
            if (session == null || owner == null || owner.Kind != BrowserOwnerKind.Workspace ||
                owner.ChatId != session.Id || owner.ScopeId != session.WorkspaceId)
                throw new InvalidOperationException("Browser contribution requires the exact Workspace chat.");
            var store = new ResourceAuthorityStore(_paths); var blobs = new ChatBlobStore(_paths);
            var authority = new ResourceAuthorityService(store, store, new ResourceMutationJournal(_paths), blobs);
            var gateway = new ResourceGatewayService(null, null, null, authority: authority, externalBrowser: _source);
            return new BrowserCapabilitySet {
                ModelProjection = ModelToolResultProjection.Instance,
                Tools = ExternalBrowserToolHandler.Catalog().Select(entry => new WorkspaceToolContribution { Entry = entry,
                    Handler = new ExternalBrowserToolHandler(session, _source, null) }).ToArray(),
                ResourceFind = new ScopedBrowserRead(new ResourceFindToolHandler(gateway, session), true),
                ResourceRead = new ScopedBrowserRead(new ResourceReadToolHandler(gateway, session, captureMedia, settings), false),
                ResourceSearch = new ScopedBrowserRead(new ResourceSearchToolHandler(gateway, session, new ResourceSearchObservation()), false)
            };
        }
        private sealed class ScopedBrowserRead : IReadOnlyToolHandler
        {
            private readonly IReadOnlyToolHandler _handler; private readonly bool _find;
            internal ScopedBrowserRead(IReadOnlyToolHandler handler, bool find) { _handler = handler; _find = find; }
            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
            {
                var arguments = new Dictionary<string, object>(context.Arguments); arguments.Remove("type");
                object value;
                if (_find) arguments["scope"] = "browser";
                else if (!arguments.TryGetValue("target", out value) || !(value is string target) ||
                    !target.StartsWith(ExternalBrowserResourceProvider.TargetPrefix, StringComparison.Ordinal) &&
                    !target.StartsWith(ExternalBrowserResourceProvider.ObservationPrefix, StringComparison.Ordinal))
                    return Task.FromResult(new ToolHandlerResult(ToolResult.Error("Select an exact Browser target from discovery.",
                        "{\"code\":\"browser_target_invalid\"}"), ToolEffectEvidence.None));
                return _handler.ExecuteAsync(new ToolHandlerContext(context.Execution, arguments), token);
            }
        }
    }
}
