using System.Collections.Generic;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        private IReadOnlyList<BrowserTargetDescriptor> BrowserTargets(BrowserOwnerScope owner)
        {
            if (owner?.Kind == BrowserOwnerKind.Document) return _controller.BrowserTargets(owner);
            if (Plugins == null) return new BrowserTargetDescriptor[0];
            return Plugins.BrowserTargets(owner);
        }

        internal BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        { return BrowserTargetResolver.ResolveLocalLocator(request.OwnerScope, request.Locator, BrowserTargets(request.OwnerScope)); }

        private BrowserSuggestionPage BrowserSuggestions(BrowserSuggestionRequest request)
        { return BrowserTargetResolver.QueryNavigationSuggestions(request.OwnerScope, request, BrowserTargets(request.OwnerScope)); }

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
