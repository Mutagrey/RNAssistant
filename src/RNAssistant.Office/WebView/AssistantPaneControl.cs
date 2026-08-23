using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantPaneControl : UserControl
    {
        private readonly AssistantController _controller;
        private readonly string _webRoot;
        private readonly AssistantWebBridge _bridge;
        private readonly System.Threading.CancellationTokenSource _lifetimeCancellation;
        private WebView2 _webView;
        private CoreWebView2Controller _webViewController;
        private string _trustedDocumentUri;
        private bool _webContentWantsKeyboard;
        private bool _resourcesDisposed;
        private IntPtr _lastExternalFocusWindow;

        public AssistantPaneControl(AssistantController controller, string webRoot)
        {
            _controller = controller;
            _webRoot = webRoot;
            _bridge = new AssistantWebBridge(controller, PostBridgeMessage);
            _lifetimeCancellation = new System.Threading.CancellationTokenSource();
            CreateWebViewControl();
            Load += OnLoad;
        }

        public void BlurComposer()
        {
            ExecuteScript("window.RNAssistantHost && window.RNAssistantHost.blurComposer && window.RNAssistantHost.blurComposer();");
        }

        public void ReleaseKeyboardFocusToHost(Action activateHost)
        {
            BlurComposer();
            if (activateHost == null)
            {
                return;
            }

            if (IsDisposed || !IsHandleCreated)
            {
                activateHost();
                RememberExternalFocusWindow();
                return;
            }

            BeginInvoke(new Action(() =>
            {
                BlurComposer();
                activateHost();
                RememberExternalFocusWindow();
            }));
        }

        public void RefreshContext()
        {
            ExecuteScript("window.RNAssistantHost && window.RNAssistantHost.refreshContext && window.RNAssistantHost.refreshContext();");
        }

        public void RefreshState()
        {
            ExecuteScript("window.RNAssistantHost && window.RNAssistantHost.refreshState && window.RNAssistantHost.refreshState();");
        }

        public void RunQuickAction(string action)
        {
            ExecuteScript("window.RNAssistantHost && window.RNAssistantHost.runQuickAction && window.RNAssistantHost.runQuickAction(" + JsonConvert.SerializeObject(action ?? string.Empty) + ");");
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                await InitializeAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RenderStartupError(ex);
            }
        }

        private async Task InitializeAsync()
        {
            var cancellationToken = _lifetimeCancellation.Token;
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeLog.Info("WebView2 initialization started. WebRoot=" + _webRoot);
            var errors = new StringBuilder();
            var candidates = BuildEnvironmentCandidates();
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                try
                {
                    if (i > 0)
                    {
                        ResetWebViewControl();
                    }

                    Directory.CreateDirectory(candidate.UserDataFolder);
                    RuntimeLog.Info("WebView2 candidate=" + candidate.Name + ", userData=" + candidate.UserDataFolder);
                    var environment = await CoreWebView2Environment.CreateAsync(candidate.BrowserFolder, candidate.UserDataFolder).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    ConfigureInitializedWebView();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var baseException = ex.GetBaseException();
                    errors.AppendLine(candidate.Name + ": " + baseException.Message);
                    Debug.WriteLine("RNAssistant WebView2 startup failed for " + candidate.Name + ": " + baseException);
                    RuntimeLog.Error("WebView2 candidate failed: " + candidate.Name, baseException);
                }
            }

            throw new InvalidOperationException("WebView2 startup failed after fallback attempts.\r\n" + errors.ToString());
        }

        private void ConfigureInitializedWebView()
        {
            var indexPath = Path.Combine(_webRoot, "index.html");
            _trustedDocumentUri = File.Exists(indexPath)
                ? WebViewSecurityPolicy.TrustedDocumentUri(indexPath)
                : string.Empty;

            var core = _webView.CoreWebView2;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.Settings.IsScriptEnabled = true;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            TryAttachAcceleratorFilter();

            if (!File.Exists(indexPath))
            {
                RuntimeLog.Error("WebView index was not found: " + indexPath);
                _webView.NavigateToString("<html><body style='font-family:Segoe UI;padding:20px'><h3>RN Assistant</h3><p>web/index.html not found.</p></body></html>");
                return;
            }

            RuntimeLog.Info("WebView navigating to " + indexPath);
            _webView.Source = new Uri(indexPath);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (WebViewSecurityPolicy.CanNavigateTopLevel(e.Uri, _trustedDocumentUri))
            {
                return;
            }

            e.Cancel = true;
            RuntimeLog.Info("Blocked WebView top-level navigation: " + (e.Uri ?? string.Empty));
            if (e.IsUserInitiated)
            {
                OpenExternalUri(e.Uri);
            }
        }

        private void OnFrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!WebViewSecurityPolicy.CanNavigateFrame(e.Uri))
            {
                e.Cancel = true;
                RuntimeLog.Info("Blocked WebView frame navigation: " + (e.Uri ?? string.Empty));
                if (e.IsUserInitiated)
                {
                    OpenExternalUri(e.Uri);
                }
            }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (e.IsUserInitiated)
            {
                OpenExternalUri(e.Uri);
            }
        }

        private static void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
        }

        private static void OpenExternalUri(string value)
        {
            if (!WebViewSecurityPolicy.CanOpenExternally(value))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Failed to open external WebView link.", ex);
            }
        }

        private void CreateWebViewControl()
        {
            _webView = new WebView2 { Dock = DockStyle.Fill, TabStop = false };
            Controls.Add(_webView);
        }

        private void ResetWebViewControl()
        {
            if (_webView != null)
            {
                DetachInitializedWebView();
                Controls.Remove(_webView);
                _webView.Dispose();
            }

            CreateWebViewControl();
        }

        private IReadOnlyList<WebViewEnvironmentCandidate> BuildEnvironmentCandidates()
        {
            var candidates = new List<WebViewEnvironmentCandidate>();
            var root = RNAssistant.Core.Storage.AppDataPaths.CreateDefault().WebViewUserDataDirectory;
            var browserFolder = ResolveFixedRuntimeFolder();
            var processFolder = Path.Combine(root, SafeFolderName(Process.GetCurrentProcess().ProcessName));
            var recoveryFolder = Path.Combine(root, "recovery-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

            if (!string.IsNullOrWhiteSpace(browserFolder))
            {
                candidates.Add(new WebViewEnvironmentCandidate("fixed runtime + shared profile", browserFolder, root));
                candidates.Add(new WebViewEnvironmentCandidate("fixed runtime + process profile", browserFolder, processFolder));
            }

            candidates.Add(new WebViewEnvironmentCandidate("installed WebView2 Runtime + process profile", null, processFolder));
            candidates.Add(new WebViewEnvironmentCandidate("installed WebView2 Runtime + recovery profile", null, recoveryFolder));
            return candidates;
        }

        private void TryAttachAcceleratorFilter()
        {
            var field = typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
            var controller = field == null ? null : field.GetValue(_webView) as CoreWebView2Controller;
            if (controller != null)
            {
                _webViewController = controller;
                // This WebView2 WinForms package keeps the controller internal.
                controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
                controller.LostFocus += OnControllerLostFocus;
            }
        }

        private void OnControllerLostFocus(object sender, object e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvoke(new Action(RememberExternalFocusWindow));
        }

        private void OnAcceleratorKeyPressed(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (!IsEditOrNavigationAccelerator((int)e.VirtualKey))
            {
                return;
            }

            var focusedWindow = GetFocus();
            var focusInsidePane = IsKeyboardFocusInsidePane(focusedWindow);
            if (focusInsidePane && _webContentWantsKeyboard)
            {
                return;
            }

            if (!focusInsidePane && focusedWindow != IntPtr.Zero)
            {
                RememberExternalFocusWindow(focusedWindow);
            }

            if (!focusInsidePane || !_webContentWantsKeyboard)
            {
                var targetWindow = focusInsidePane ? ExternalFocusFallback() : focusedWindow;
                ForwardKeyToFocusedWindow(targetWindow, e);
                e.Handled = true;
            }
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (!WebViewSecurityPolicy.IsTrustedDocument(e.Source, _trustedDocumentUri))
                {
                    RuntimeLog.Info("Blocked WebView message from untrusted source: " + (e.Source ?? string.Empty));
                    return;
                }

                var requestJson = e.WebMessageAsJson;
                if (TryHandleHostStateMessage(requestJson))
                {
                    return;
                }

                RuntimeLog.Info("Web message received: " + DescribeMessageForLog(requestJson));
                var responseJson = await _bridge.HandleMessageAsync(requestJson).ConfigureAwait(true);
                PostBridgeMessage(responseJson);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Web message handling failed.", ex);
                PostBridgeMessage("{\"ok\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}");
            }
        }

        private bool TryHandleHostStateMessage(string requestJson)
        {
            var request = JsonConvert.DeserializeObject<FocusStateMessage>(requestJson);
            var type = request == null ? string.Empty : (request.Type ?? string.Empty).Trim();
            if (!string.Equals(type, "focusState", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _webContentWantsKeyboard = request.Payload != null && request.Payload.WantsKeyboard;
            return true;
        }

        private string ResolveFixedRuntimeFolder()
        {
            var portableRoot = string.IsNullOrWhiteSpace(_webRoot)
                ? null
                : Directory.GetParent(Path.GetFullPath(_webRoot));
            var root = portableRoot == null
                ? null
                : Path.Combine(portableRoot.FullName, "vendor", "webview2-runtime");
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "webview2-runtime");
            }

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

        private static string SafeFolderName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "process";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            return builder.ToString();
        }

        private sealed class WebViewEnvironmentCandidate
        {
            public WebViewEnvironmentCandidate(string name, string browserFolder, string userDataFolder)
            {
                Name = name;
                BrowserFolder = browserFolder;
                UserDataFolder = userDataFolder;
            }

            public string Name { get; private set; }
            public string BrowserFolder { get; private set; }
            public string UserDataFolder { get; private set; }
        }

        private void RenderStartupError(Exception ex)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RenderStartupError(ex)));
                return;
            }

            Controls.Clear();
            if (_webView != null)
            {
                DetachInitializedWebView();
                _webView.Dispose();
            }

            var baseException = ex == null ? null : ex.GetBaseException();
            var message = baseException == null ? "Unknown WebView2 startup error." : baseException.Message;
            if (!message.Contains("WebView2 Runtime"))
            {
                message += "\r\n\r\nInstall Microsoft Edge WebView2 Runtime or copy a fixed runtime to vendor\\webview2-runtime. The NuGet package only provides the .NET control/API, not the browser runtime.";
            }

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(20)
            };
            var label = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.TopLeft,
                Text = "RN Assistant\r\n\r\nWebView2 startup failed.\r\n\r\n" + message
            };
            panel.Controls.Add(label);
            Controls.Add(panel);
        }

        private void DetachInitializedWebView()
        {
            try
            {
                var core = _webView == null || _webView.IsDisposed ? null : _webView.CoreWebView2;
                if (core != null)
                {
                    core.WebMessageReceived -= OnWebMessageReceived;
                    core.NavigationStarting -= OnNavigationStarting;
                    core.FrameNavigationStarting -= OnFrameNavigationStarting;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.PermissionRequested -= OnPermissionRequested;
                }
            }
            catch
            {
            }

            if (_webViewController != null)
            {
                _webViewController.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
                _webViewController.LostFocus -= OnControllerLostFocus;
                _webViewController = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                Load -= OnLoad;
                try { _lifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
                DetachInitializedWebView();
                _bridge.Dispose();
                _lifetimeCancellation.Dispose();
            }

            base.Dispose(disposing);
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

            if (!_webView.IsDisposed && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
        }

        private void PostBridgeMessage(string json)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => PostBridgeMessage(json)));
                return;
            }

            if (!_webView.IsDisposed && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string DescribeMessageForLog(string value)
        {
            try
            {
                var message = JObject.Parse(value ?? "{}");
                return "type=" + (message.Value<string>("type") ?? string.Empty)
                    + ", id=" + (message.Value<string>("id") ?? string.Empty);
            }
            catch
            {
                return "invalid JSON, chars=" + (value == null ? 0 : value.Length);
            }
        }

        private bool IsKeyboardFocusInsidePane(IntPtr focusedWindow)
        {
            return focusedWindow != IntPtr.Zero && (focusedWindow == Handle || IsChild(Handle, focusedWindow));
        }

        private void RememberExternalFocusWindow()
        {
            RememberExternalFocusWindow(GetFocus());
        }

        private void RememberExternalFocusWindow(IntPtr focusedWindow)
        {
            if (focusedWindow != IntPtr.Zero && !IsKeyboardFocusInsidePane(focusedWindow))
            {
                _lastExternalFocusWindow = focusedWindow;
            }
        }

        private IntPtr ExternalFocusFallback()
        {
            return _lastExternalFocusWindow != IntPtr.Zero ? _lastExternalFocusWindow : GetAncestor(Handle, 2);
        }

        private bool ForwardKeyToFocusedWindow(IntPtr focusedWindow, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (focusedWindow == IntPtr.Zero)
            {
                return false;
            }

            var message = KeyEventMessage(e.KeyEventKind);
            if (message == 0)
            {
                return false;
            }

            return PostMessage(focusedWindow, message, (IntPtr)e.VirtualKey, (IntPtr)e.KeyEventLParam);
        }

        private static int KeyEventMessage(CoreWebView2KeyEventKind kind)
        {
            switch (kind)
            {
                case CoreWebView2KeyEventKind.KeyDown:
                    return 0x0100;
                case CoreWebView2KeyEventKind.KeyUp:
                    return 0x0101;
                case CoreWebView2KeyEventKind.SystemKeyDown:
                    return 0x0104;
                case CoreWebView2KeyEventKind.SystemKeyUp:
                    return 0x0105;
                default:
                    return 0;
            }
        }

        private static bool IsEditOrNavigationAccelerator(int virtualKey)
        {
            var key = (Keys)virtualKey;
            var ctrl = (GetKeyState((int)Keys.ControlKey) & 0x8000) != 0;
            var shift = (GetKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
            var alt = (GetKeyState((int)Keys.Menu) & 0x8000) != 0;

            if (alt)
            {
                return false;
            }

            if (ctrl)
            {
                switch (key)
                {
                    case Keys.A:
                    case Keys.C:
                    case Keys.F:
                    case Keys.Insert:
                    case Keys.V:
                    case Keys.X:
                    case Keys.Y:
                    case Keys.Z:
                        return true;
                }
            }

            if (shift)
            {
                return key == Keys.Delete || key == Keys.Insert;
            }

            switch (key)
            {
                case Keys.Back:
                case Keys.Delete:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                    return true;
                default:
                    return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parentWindow, IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, int flags);
    }
}
