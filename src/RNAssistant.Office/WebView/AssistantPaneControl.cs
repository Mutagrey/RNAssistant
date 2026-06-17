using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantPaneControl : UserControl
    {
        private readonly AssistantController _controller;
        private readonly string _webRoot;
        private readonly WebView2 _webView;
        private readonly AssistantWebBridge _bridge;

        public AssistantPaneControl(AssistantController controller, string webRoot)
        {
            _controller = controller;
            _webRoot = webRoot;
            _bridge = new AssistantWebBridge(controller);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Load += OnLoad;
        }

        public void BlurComposer()
        {
            ExecuteScript("window.RNAssistantHost && window.RNAssistantHost.blurComposer && window.RNAssistantHost.blurComposer();");
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                await InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                RenderStartupError(ex);
            }
        }

        private async Task InitializeAsync()
        {
            var userDataFolder = RNAssistant.Core.Storage.AppDataPaths.CreateDefault().WebViewUserDataDirectory;
            var browserFolder = ResolveFixedRuntimeFolder();
            var environment = await CoreWebView2Environment.CreateAsync(browserFolder, userDataFolder).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            var indexPath = Path.Combine(_webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                _webView.NavigateToString("<html><body style='font-family:Segoe UI;padding:20px'><h3>RN Assistant</h3><p>web/index.html not found.</p></body></html>");
                return;
            }

            _webView.Source = new Uri(indexPath);
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var requestJson = e.WebMessageAsJson;
                var responseJson = await _bridge.HandleMessageAsync(requestJson).ConfigureAwait(true);
                _webView.CoreWebView2.PostWebMessageAsJson(responseJson);
            }
            catch (Exception ex)
            {
                _webView.CoreWebView2.PostWebMessageAsJson("{\"ok\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}");
            }
        }

        private static string ResolveFixedRuntimeFolder()
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "webview2-runtime");
            if (!Directory.Exists(root))
            {
                return null;
            }

            if (File.Exists(Path.Combine(root, "msedgewebview2.exe")))
            {
                return root;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(directory, "msedgewebview2.exe")))
                {
                    return directory;
                }
            }

            return null;
        }

        private void RenderStartupError(Exception ex)
        {
            var message = System.Net.WebUtility.HtmlEncode(ex.Message);
            _webView.NavigateToString("<html><body style='font-family:Segoe UI;padding:20px'><h3>RN Assistant</h3><p>WebView2 startup failed.</p><pre>" + message + "</pre></body></html>");
        }

        private void ExecuteScript(string script)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ExecuteScript(script)));
                return;
            }

            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
