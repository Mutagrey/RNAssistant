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

        private BrowserResolveResult ResolveBrowserLocator(BrowserResolveRequest request)
        { return BrowserTargetResolver.ResolveLocalLocator(request.OwnerScope, request.Locator, BrowserTargets(request.OwnerScope)); }

        private BrowserSuggestionPage BrowserSuggestions(BrowserSuggestionRequest request)
        { return BrowserTargetResolver.QueryNavigationSuggestions(request.OwnerScope, request, BrowserTargets(request.OwnerScope)); }
    }
}
