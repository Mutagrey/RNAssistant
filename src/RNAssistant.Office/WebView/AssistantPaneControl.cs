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
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantPaneControl : UserControl
    {
        private readonly AssistantController _controller;
        private readonly string _webRoot;
        private readonly AssistantWebBridge _bridge;
        private WebView2 _webView;
        private bool _webContentWantsKeyboard;
        private IntPtr _lastExternalFocusWindow;

        public AssistantPaneControl(AssistantController controller, string webRoot)
        {
            _controller = controller;
            _webRoot = webRoot;
            _bridge = new AssistantWebBridge(controller, PostBridgeMessage);
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
            catch (Exception ex)
            {
                RenderStartupError(ex);
            }
        }

        private async Task InitializeAsync()
        {
            var errors = new StringBuilder();
            var candidates = BuildEnvironmentCandidates();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                try
                {
                    if (i > 0)
                    {
                        ResetWebViewControl();
                    }

                    Directory.CreateDirectory(candidate.UserDataFolder);
                    var environment = await CoreWebView2Environment.CreateAsync(candidate.BrowserFolder, candidate.UserDataFolder).ConfigureAwait(true);
                    await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
                    ConfigureInitializedWebView();
                    return;
                }
                catch (Exception ex)
                {
                    var baseException = ex.GetBaseException();
                    errors.AppendLine(candidate.Name + ": " + baseException.Message);
                    Debug.WriteLine("RNAssistant WebView2 startup failed for " + candidate.Name + ": " + baseException);
                }
            }

            throw new InvalidOperationException("WebView2 startup failed after fallback attempts.\r\n" + errors.ToString());
        }

        private void ConfigureInitializedWebView()
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            TryAttachAcceleratorFilter();

            var indexPath = Path.Combine(_webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                _webView.NavigateToString("<html><body style='font-family:Segoe UI;padding:20px'><h3>RN Assistant</h3><p>web/index.html not found.</p></body></html>");
                return;
            }

            _webView.Source = new Uri(indexPath);
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
                Controls.Remove(_webView);
                _webView.Dispose();
            }

            CreateWebViewControl();
        }

        private static IReadOnlyList<WebViewEnvironmentCandidate> BuildEnvironmentCandidates()
        {
            var candidates = new List<WebViewEnvironmentCandidate>();
            var root = RNAssistant.Core.Storage.AppDataPaths.CreateDefault().WebViewUserDataDirectory;
            var browserFolder = ResolveFixedRuntimeFolder();
            var processFolder = Path.Combine(root, SafeFolderName(Process.GetCurrentProcess().ProcessName));
            var recoveryFolder = Path.Combine(root, "recovery-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

            candidates.Add(new WebViewEnvironmentCandidate("fixed runtime + shared profile", browserFolder, root));
            candidates.Add(new WebViewEnvironmentCandidate("fixed runtime + process profile", browserFolder, processFolder));
            candidates.Add(new WebViewEnvironmentCandidate("installed runtime + process profile", null, processFolder));
            candidates.Add(new WebViewEnvironmentCandidate("installed runtime + recovery profile", null, recoveryFolder));
            return candidates;
        }

        private void TryAttachAcceleratorFilter()
        {
            var field = typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
            var controller = field == null ? null : field.GetValue(_webView) as CoreWebView2Controller;
            if (controller != null)
            {
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
                var requestJson = e.WebMessageAsJson;
                if (TryHandleHostStateMessage(requestJson))
                {
                    return;
                }

                var responseJson = await _bridge.HandleMessageAsync(requestJson).ConfigureAwait(true);
                PostBridgeMessage(responseJson);
            }
            catch (Exception ex)
            {
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
                _webView.Dispose();
            }

            var baseException = ex == null ? null : ex.GetBaseException();
            var message = baseException == null ? "Unknown WebView2 startup error." : baseException.Message;
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
