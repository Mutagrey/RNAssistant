using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using RNAssistant.Application;

namespace RNAssistant.Office.WebView
{
    // One lazy, isolated environment for visible local pages and hidden plugin commands.
    // Never the privileged Shell environment or the user's Edge profile.
    internal sealed class LocalBrowserEnvironment : IDisposable
    {
        private Task<CoreWebView2Environment> _opening;
        private readonly IntPtr _parent;
        private CoreWebView2Controller _workers;
        private readonly Dictionary<string, Action<CoreWebView2WebResourceRequestedEventArgs>> _routes = new Dictionary<string, Action<CoreWebView2WebResourceRequestedEventArgs>>(StringComparer.Ordinal);
        private bool _closed;
        private int _consumers;
        private CoreWebView2Environment _current;
        internal LocalBrowserEnvironment(IntPtr parent) { _parent = parent; }
        internal IDisposable Route(CoreWebView2 core, string origin, Action<CoreWebView2WebResourceRequestedEventArgs> read)
        {
            if (_closed || _workers == null || _routes.ContainsKey(origin)) throw new InvalidOperationException("Duplicate or closed local Browser route.");
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
            EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = (sender, args) => read(args);
            core.WebResourceRequested += handler; _routes.Add(origin, read);
            return new RouteLease(() => { core.WebResourceRequested -= handler; _routes.Remove(origin); });
        }
        internal sealed class Lease : IDisposable
        {
            private LocalBrowserEnvironment _owner;
            internal CoreWebView2Environment Environment { get; }
            internal Lease(LocalBrowserEnvironment owner, CoreWebView2Environment environment) { _owner = owner; Environment = environment; }
            public void Dispose() { var owner = _owner; _owner = null; owner?.Release(); }
        }
        internal async Task<Lease> AcquireAsync()
        {
            if (_closed) throw new ObjectDisposedException(nameof(LocalBrowserEnvironment));
            _consumers++;
            try { return new Lease(this, await GetAsync()); } catch { Release(); throw; }
        }
        private void Release()
        {
            if (--_consumers != 0) return;
            _routes.Clear(); _workers?.Close(); _workers = null; _opening = null; _current = null;
        }
        private async Task<CoreWebView2Environment> GetAsync()
        {
            if (_closed) throw new ObjectDisposedException(nameof(LocalBrowserEnvironment));
            var value = await (_opening ?? (_opening = CreateAsync()));
            if (_closed) throw new ObjectDisposedException(nameof(LocalBrowserEnvironment));
            return value;
        }
        private async Task<CoreWebView2Environment> CreateAsync()
        {
            var profile = Path.Combine(Path.GetTempPath(), "RNAssistant", "LocalBrowser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserNetworkPolicy.BrowserArguments };
            var value = await CoreWebView2Environment.CreateAsync(null, profile, options);
            _current = value;
            value.BrowserProcessExited += (sender, args) => {
                if (ReferenceEquals(_current, value)) { _current = null; _opening = null; }
                Task.Run(() => { try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } });
            };
            if (!string.Equals(Path.GetFullPath(value.UserDataFolder), Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 replaced the isolated local Browser profile.");
            BrowserNetworkPolicy.RequireSupportedRuntime(value.BrowserVersionString);
            // WebView2 requires a single receiver for Shared/Service Worker requests.
            var controllerOptions = value.CreateCoreWebView2ControllerOptions();
            controllerOptions.ProfileName = "RNAssistantLocal"; controllerOptions.IsInPrivateModeEnabled = true;
            _workers = await value.CreateCoreWebView2ControllerAsync(_parent, controllerOptions);
            if (_closed) { _workers.Close(); _workers = null; throw new ObjectDisposedException(nameof(LocalBrowserEnvironment)); }
            _workers.IsVisible = false; _workers.Bounds = new Rectangle(0, 0, 1, 1);
            var workerCore = _workers.CoreWebView2;
            workerCore.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.SharedWorker | CoreWebView2WebResourceRequestSourceKinds.ServiceWorker);
            workerCore.WebResourceRequested += (sender, args) => {
                Uri address; Action<CoreWebView2WebResourceRequestedEventArgs> route;
                if (!_closed && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out address) &&
                    _routes.TryGetValue(address.GetLeftPart(UriPartial.Authority), out route)) route(args);
                else args.Response = value.CreateWebResourceResponse(new MemoryStream(), 403, "Local resource only", BrowserNetworkPolicy.Headers("text/plain"));
            };
            return value;
        }
        public void Dispose() { _closed = true; _routes.Clear(); _workers?.Close(); _workers = null; _opening = null; }
        private sealed class RouteLease : IDisposable
        {
            private Action _close;
            internal RouteLease(Action close) { _close = close; }
            public void Dispose() { var close = _close; _close = null; close?.Invoke(); }
        }
    }
}
