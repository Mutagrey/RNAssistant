using System.Collections.Generic;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        internal System.Action<RNAssistant.Office.Contracts.ResourceChangedMessage> BrowserResourcesChanged { get; set; }
        internal BrowserContentSession OpenBrowserContent(BrowserTargetDescriptor target, System.Threading.CancellationToken token)
        { return target?.OwnerScope?.Kind == BrowserOwnerKind.Document ? _controller.OpenBrowserContent(target, token) : Plugins.OpenBrowserContent(target, token); }

        private void ReportBrowserVersionChanged(BrowserVersionStateChanged value)
        { if (!_disposed) _postMessageJson(Newtonsoft.Json.JsonConvert.SerializeObject(value)); }

        internal IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope owner, string suggestionsPrefix = null)
        {
            if (owner?.Kind == BrowserOwnerKind.Document) return _controller.BrowserTargets(owner, suggestionsPrefix);
            if (Plugins == null) return new BrowserTargetDescriptor[0];
            return Plugins.BrowserTargets(owner, suggestionsPrefix);
        }

        internal BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        { return request.OwnerScope?.Kind == BrowserOwnerKind.Document ?
            _controller.ResolveBrowserLocator(request) :
            Plugins.ResolveBrowserLocator(request); }

        private BrowserVersionState ReadBrowserVersionState(BrowserVersionRequest request, System.Threading.CancellationToken token)
        { return request.Target?.OwnerScope?.Kind == BrowserOwnerKind.Document ? _controller.BrowserVersionState(request) : Plugins.BrowserVersionState(request, token); }
        private BrowserVersionPage ReadBrowserVersions(BrowserVersionRequest request)
        { return request.Target?.OwnerScope?.Kind == BrowserOwnerKind.Document ? _controller.BrowserVersions(request) : Plugins.BrowserVersions(request); }
        private BrowserVersionResult WriteBrowserVersion(BrowserVersionCommand request, System.Threading.CancellationToken token)
        {
            if (request.Target?.OwnerScope?.Kind != BrowserOwnerKind.Document) return Plugins.BrowserVersionCommand(request, token);
            var result = _controller.BrowserVersionCommand(request, token);
            if (result.Outcome != BrowserVersionOutcome.Unknown && result.Outcome != BrowserVersionOutcome.Rejected)
                try { ReportBrowserVersionChanged(new BrowserVersionStateChanged { Target = result.State.Target, Reason = request.Action, StateRevision = result.State.StateRevision, VersionId = result.Version?.VersionId }); } catch { }
            return result;
        }

        private BrowserSuggestionPage BrowserSuggestions(BrowserSuggestionRequest request)
        {
            BrowserTargetResolver.QueryNavigationSuggestions(request?.OwnerScope, request, new BrowserTargetDescriptor[0]);
            return BrowserTargetResolver.QueryNavigationSuggestions(request.OwnerScope, request, BrowserTargets(request.OwnerScope, request.Prefix ?? ""));
        }

        private SourceComparisonDto BrowserComparison(BrowserSourceComparisonRequest request)
        { return request.Target?.OwnerScope?.Kind == BrowserOwnerKind.Document ? _controller.CompareBrowserSources(request) : Plugins.CompareBrowserSources(request); }

        private BrowserSourcePage BrowserSource(BrowserSourceRequest request)
        {
            var resolved = ResolveBrowserLocator(new BrowserResolveRequest { OwnerScope = request.OwnerScope, Locator = request.Locator });
            if (resolved.Target == null) throw new System.InvalidOperationException(resolved.Message);
            if (resolved.Target.Kind == BrowserTargetKind.Home) throw new System.InvalidOperationException("У Главной нет исходников.");
            return resolved.Target.OwnerScope.Kind == BrowserOwnerKind.Document ? _controller.ReadBrowserSource(request, resolved.Target) :
                Plugins.ReadBrowserSource(request, resolved.Target);
        }
    }
}
