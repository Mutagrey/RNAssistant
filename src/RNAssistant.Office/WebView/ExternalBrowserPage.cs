using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    // Owns one unprivileged browser. Placement belongs to ExternalBrowserDockHost.
    internal sealed class ExternalBrowserPage : IDisposable
    {
        private readonly string _profile = Path.Combine(Path.GetTempPath(), "RNAssistant", "BrowserPilot", Guid.NewGuid().ToString("N"));
        private readonly string _assets;
        private CoreWebView2Environment _environment;
        private bool _disposed;
        private long _revision;
        private ulong _navigationId;
        private bool _navigationRefused;
        private bool _canGoBack, _canGoForward;
        private long _documentGeneration;
        private bool _documentReady;
        private readonly string _chatId;
        private readonly Action<string, BrowserAccessKind> _requireAccess;
        private bool _agentNetwork; // Only explicit native manual navigation can widen network admission again.
        private readonly BrowserPageAutomation _automation;
        private CoreWebView2DevToolsProtocolEventReceiver _controlEvents;
        internal void BlockActions() { _automation.BlockActions(); }
        private string _runtime = "—";
        private string _url = ExternalBrowserPolicy.ProbeUrl, _title = "Внешняя страница", _message = "Запуск WebView2…";
        private ExternalBrowserStatus _status = ExternalBrowserStatus.Starting;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public WebView2 Control { get; }
        internal BrowserPageAutomation Automation => _automation;
        public event Action Changed;

        public ExternalBrowserPage(string assets, string chatId, Action<string, BrowserAccessKind> requireAccess,
            Func<string, string, Task<Action>> pauseRun)
        {
            _assets = assets; _chatId = chatId; _requireAccess = requireAccess;
            Control = new WebView2 { Visible = false, TabStop = true };
            Control.Enter += FocusChanged; Control.Leave += FocusChanged;
            Control.VisibleChanged += VisibilityChanged;
            _automation = new BrowserPageAutomation(chatId, () => ReadTarget, () => Control.CoreWebView2.Source, () => Control.Visible,
                (method, json) => Control.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json), address => {
                    if (Control.CoreWebView2.Source != _url) throw ExternalBrowserResourceProvider.Error("Browser address changed.", "browser_document_stale");
                    _requireAccess(address, BrowserAccessKind.Agent);
                }, () => _agentNetwork = true, pauseRun, Publish, action => Control.BeginInvoke(action), () => !Control.InvokeRequired);
        }

        public ExternalBrowserReadTarget ReadTarget
        { get { return !_disposed && _documentReady && _status == ExternalBrowserStatus.Ready && !string.IsNullOrEmpty(_chatId)
            ? new ExternalBrowserReadTarget(_chatId, SessionId, _documentGeneration) : null; } }

        public Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target) => _automation.ReadAsync(target);
        public Task<bool> PrepareAsync(ExternalBrowserReadTarget target, string revision, string preparation, ExternalBrowserActionRequest request)
            => _automation.PrepareAsync(target, revision, preparation, request);
        public Task<ExternalBrowserActionResult> ActAsync(ExternalBrowserReadTarget target, string preparation, Action beforeDispatch)
            => _automation.ActAsync(target, preparation, beforeDispatch);
        public Task ResumeAgentAsync(long? revision) => _automation.ResumeAgentAsync(revision);
        internal void PresentResult(ExternalBrowserReadTarget target, string preparation, ExternalBrowserEffect effect)
            => _automation.PresentResult(target, preparation, effect);
        private void ControlEvent(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) => _automation.ControlEvent(e.ParameterObjectAsJson);
        private void VisibilityChanged(object sender, EventArgs e) { if (!Control.Visible) _automation.HideAction(); }

        public async Task InitializeAsync()
        {
            // Hidden controls still need an HWND; waiting for Ready before creating it would deadlock startup.
            _ = Control.Handle;
            Directory.CreateDirectory(_profile);
            _environment = await CoreWebView2Environment.CreateAsync(null, _profile);
            _runtime = _environment.BrowserVersionString;
            _environment.BrowserProcessExited += BrowserExited;
            if (_disposed) { CleanupProfile(); throw new ObjectDisposedException(nameof(ExternalBrowserPage)); }
            if (!string.Equals(Path.GetFullPath(_environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_profile).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 переопределил временный профиль. Пилот не использует чужой профиль.");
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "ExternalBrowser"; options.IsInPrivateModeEnabled = true;
            await Control.EnsureCoreWebView2Async(_environment, options);
            if (_disposed) throw new ObjectDisposedException(nameof(ExternalBrowserPage));
            var core = Control.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.FrameNavigationStarting += FrameNavigationStarting;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += WebResourceRequested;
            core.NavigationCompleted += NavigationCompleted;
            core.SourceChanged += SourceChanged;
            core.HistoryChanged += HistoryChanged;
            core.DocumentTitleChanged += TitleChanged;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
            _controlEvents = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
            _controlEvents.DevToolsProtocolEventReceived += ControlEvent;
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", JsonConvert.SerializeObject(new { name = "__rnaUserControl", executionContextName = "rna-semantic-read" }));
            await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", JsonConvert.SerializeObject(new { source = BrowserPageAutomation.ControlScript, worldName = "rna-semantic-read" }));
            if (_disposed) throw new ObjectDisposedException(nameof(ExternalBrowserPage));
            core.SetVirtualHostNameToFolderMapping("rna-browser-probe.invalid", _assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate(_url);
        }

        public ExternalBrowserStateDto Snapshot(string placement)
        {
            return new ExternalBrowserStateDto
            {
                SessionId = SessionId, Revision = _revision, Status = _status, Url = _url, Title = _title,
                ManualControl = _automation.ManualControl, ControlRevision = _automation.ControlRevision,
                CanGoBack = !_disposed && _canGoBack, CanGoForward = !_disposed && _canGoForward,
                Message = _automation.Message ?? _message,
                Diagnostic = "P5a · WinForms child · Runtime " + _runtime +
                    " · profile InPrivate · " + placement
            };
        }

        public void Execute(ExternalBrowserCommandPayload request)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed || Control.CoreWebView2 == null)
                throw new InvalidOperationException("Страница недоступна. Закройте вкладку и откройте её снова.");
            var core = Control.CoreWebView2;
            if (request.Action == ExternalBrowserAction.Navigate || request.Action == ExternalBrowserAction.Back ||
                request.Action == ExternalBrowserAction.Forward || request.Action == ExternalBrowserAction.Reload)
            { _agentNetwork = false; _automation.AbandonNavigation("browser_user_control"); _automation.TakeControl(); }
            switch (request.Action)
            {
                case ExternalBrowserAction.Navigate:
                    _requireAccess(request.Url, BrowserAccessKind.Manual);
                    core.Navigate(ExternalBrowserPolicy.NavigationUrl(request.Url)); break;
                case ExternalBrowserAction.Back: if (core.CanGoBack) core.GoBack(); break;
                case ExternalBrowserAction.Forward: if (core.CanGoForward) core.GoForward(); break;
                case ExternalBrowserAction.Reload: core.Reload(); break;
                case ExternalBrowserAction.Focus: if (Control.Visible) Control.Focus(); break;
                default: throw new InvalidOperationException("Недопустимая команда страницы.");
            }
        }

        private void Publish() { if (!_disposed) { _revision++; Changed?.Invoke(); } }
        private void FocusChanged(object sender, EventArgs e) { Publish(); }
        public void Fail(Exception error)
        {
            if (_disposed) return;
            _automation.AbandonNavigation("browser_effect_unknown");
            _documentReady = false; _documentGeneration++;
            _status = ExternalBrowserStatus.Failed; Control.Visible = false;
            var message = error.GetBaseException().Message;
            _message = "WebView2: " + message.Substring(0, Math.Min(message.Length, 400)) + ". Закройте вкладку и откройте снова.";
            Publish();
        }
        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _automation.HideAction();
            if (_disposed || _status == ExternalBrowserStatus.Failed) { e.Cancel = true; return; }
            if (e.IsUserInitiated) { _automation.AbandonNavigation("browser_user_control"); _automation.TakeControl(); }
            _navigationId = e.NavigationId; _navigationRefused = false;
            try
            {
                _requireAccess(e.Uri, _agentNetwork ? BrowserAccessKind.Agent : BrowserAccessKind.Manual);
                _automation.NavigationStarting(e.NavigationId, e.Uri, e.IsRedirected);
                _message = "Загрузка…";
                _documentReady = false; _documentGeneration++; _automation.ResetDocument();
            }
            catch (Exception error) when (error is InvalidOperationException || error is RNAssistant.Core.Models.ResourceRequestException)
            {
                e.Cancel = true; _navigationRefused = true;
                _automation.NavigationUnknown(error is RNAssistant.Core.Models.ResourceRequestException denied ? denied.ErrorCode : "browser_navigation_changed");
                _message = "Переход запрещён: " + error.Message;
            }
            Publish();
        }
        private void FrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try { if (_disposed) e.Cancel = true; else _requireAccess(e.Uri, _agentNetwork ? BrowserAccessKind.Agent : BrowserAccessKind.Manual); }
            catch (Exception error) when (error is InvalidOperationException || error is ResourceRequestException)
            { e.Cancel = true; }
        }
        private void WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if (_disposed) throw new InvalidOperationException();
                _requireAccess(e.Request.Uri, _agentNetwork ? BrowserAccessKind.Agent : BrowserAccessKind.Manual);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ResourceRequestException)
            { e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain\r\nCache-Control: no-store"); }
        }
        private async void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed) return;
            if (e.NavigationId != _navigationId || _navigationRefused) return;
            var generation = _documentGeneration;
            if (e.IsSuccess)
            {
                try { var ready = _documentReady; _documentReady = true; _status = ExternalBrowserStatus.Ready;
                    try { await _automation.InitializeAsync(); } finally { _documentReady = ready; } }
                catch (Exception error) { if (!_disposed && generation == _documentGeneration) Fail(error); return; }
            }
            if (_disposed || generation != _documentGeneration) return;
            _status = ExternalBrowserStatus.Ready;
            _documentReady = e.IsSuccess;
            _message = e.IsSuccess ? "Экспериментальный браузер · Windows-проверка открыта" : "Ошибка загрузки: " + e.WebErrorStatus;
            Publish();
            _automation.NavigationCompleted(e.NavigationId, e.IsSuccess, _url);
        }
        private void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            _automation.HideAction(); _url = Control.CoreWebView2.Source; _documentGeneration++; Publish();
            if (!e.IsNewDocument && _documentReady) _automation.NavigationCompleted(null, true, _url);
        }
        private void HistoryChanged(object sender, object e)
        { _canGoBack = Control.CoreWebView2.CanGoBack; _canGoForward = Control.CoreWebView2.CanGoForward; Publish(); }
        private void TitleChanged(object sender, object e)
        {
            var title = Control.CoreWebView2.DocumentTitle ?? "";
            _title = title.Substring(0, Math.Min(title.Length, 160)); Publish();
        }
        private void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        { e.Handled = true; _message = "Новое окно заблокировано: в пилоте одна страница."; Publish(); }
        private void DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        { e.Cancel = true; _message = "Загрузка файлов пока недоступна в пилоте."; Publish(); }
        private void PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        { e.State = CoreWebView2PermissionState.Deny; _message = "Доступ запрещён: " + e.PermissionKind; Publish(); }
        private void ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _documentReady = false; _documentGeneration++;
            _status = ExternalBrowserStatus.Failed; Control.Visible = false;
            _message = "WebView2: " + e.ProcessFailedKind + ". Закройте вкладку и откройте снова; страница автоматически не повторяется.";
            Publish();
        }

        private void BrowserExited(object sender, CoreWebView2BrowserProcessExitedEventArgs e)
        {
            _environment.BrowserProcessExited -= BrowserExited;
            CleanupProfile();
        }
        private void CleanupProfile()
        {
            // Only this randomly created temporary directory; never the user's browser/profile.
            Task.Run(() => { try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } });
        }
        public void Dispose()
        {
            if (_disposed) return;
            _automation.AbandonNavigation("browser_effect_unknown");
            _disposed = true; _documentReady = false; _documentGeneration++; _status = ExternalBrowserStatus.Closed; _revision++;
            _automation.Dispose(); Changed = null;
            Control.Enter -= FocusChanged; Control.Leave -= FocusChanged;
            Control.VisibleChanged -= VisibilityChanged;
            try
            {
                if (_controlEvents != null) _controlEvents.DevToolsProtocolEventReceived -= ControlEvent;
                var core = Control.CoreWebView2;
                if (core != null)
                {
                    core.NavigationStarting -= NavigationStarting; core.NavigationCompleted -= NavigationCompleted;
                    core.FrameNavigationStarting -= FrameNavigationStarting; core.WebResourceRequested -= WebResourceRequested;
                    core.SourceChanged -= SourceChanged; core.HistoryChanged -= HistoryChanged; core.DocumentTitleChanged -= TitleChanged;
                    core.NewWindowRequested -= NewWindowRequested; core.DownloadStarting -= DownloadStarting;
                    core.PermissionRequested -= PermissionRequested; core.ProcessFailed -= ProcessFailed;
                }
            }
            catch (Exception error) { Diagnostics.RuntimeLog.Error("External browser event cleanup failed.", error); }
            finally { Control.Dispose(); CleanupProfile(); }
        }
    }
}
