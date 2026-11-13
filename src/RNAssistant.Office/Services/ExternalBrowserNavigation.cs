using RNAssistant.Application.Contracts;
using System;
using System.Threading.Tasks;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // One dispatched navigation, correlated through redirects and terminal read-back.
    internal sealed class ExternalBrowserNavigation
    {
        private readonly BrowserPageHandle _before;
        private readonly Action<string> _access;
        private readonly TaskCompletionSource<BrowserActionResult> _completion =
            new TaskCompletionSource<BrowserActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong? _navigationId;
        private string _expectedUrl;
        internal ExternalBrowserNavigation(BrowserPageHandle before, string url, Action<string> access)
        { _before = before; _expectedUrl = CanonicalUrl(url); _access = access; _access(_expectedUrl); }
        private static string CanonicalUrl(string value)
        { var uri = new Uri(ExternalBrowserPolicy.NavigationUrl(value)); return new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri; }
        internal Task<BrowserActionResult> Completion { get { return _completion.Task; } }
        internal void Starting(ulong id, string url, bool redirect)
        {
            if (Completion.IsCompleted) throw ExternalBrowserResourceProvider.Error("Navigation is no longer active.", "browser_navigation_changed");
            var actual = CanonicalUrl(url);
            if (!_navigationId.HasValue ? redirect || actual != _expectedUrl : id != _navigationId || !redirect)
                throw ExternalBrowserResourceProvider.Error("Another navigation replaced the requested transition.", "browser_navigation_changed");
            _access(actual); _navigationId = id; _expectedUrl = actual;
        }
        internal void Completed(ulong? id, bool success, BrowserPageHandle after, string actualUrl)
        {
            if (Completion.IsCompleted || id != _navigationId) return; // Late completion of a different document.
            try
            {
                if (!success || after == null || after.ChatId != _before.ChatId || after.SessionId != _before.SessionId ||
                    after.DocumentGeneration <= _before.DocumentGeneration || CanonicalUrl(actualUrl) != _expectedUrl)
                { Unknown("browser_navigation_changed"); return; }
                _access(actualUrl); // Revocation during loading cannot produce success.
                _completion.TrySetResult(new BrowserActionResult { Effect = BrowserActionEffect.VerifiedChange,
                    Code = "browser_verified", NavigationTarget = after });
            }
            catch (Exception) { Unknown("browser_site_denied"); }
        }
        internal void Unknown(string code)
        { _completion.TrySetResult(new BrowserActionResult { Effect = BrowserActionEffect.Unknown, Code = code }); }
    }
}
