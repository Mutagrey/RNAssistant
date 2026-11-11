using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
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
        private static readonly string SemanticScript = LoadAsset("ExternalBrowserSemanticRead.js");
        private static readonly string VisualScript = LoadAsset("ExternalBrowserActionView.js") +
            ";globalThis.__rnaBrowserVisual=globalThis.__rnaBrowserVisual||RNAssistantBrowserActionView.create(" +
            JsonConvert.SerializeObject(LoadAsset("ProductTokens.css").Replace(":root", ":host") + LoadAsset("ExternalBrowserActionView.css")) + ");true";
        private int _semanticContext;
        private int _visualContext;
        private string _observationRevision, _observationId, _preparationId;
        private ExternalBrowserReadTarget _observationTarget;
        private bool _actionUnknown;
        private int _abandonedAction;
        internal void BlockActions() { Interlocked.Exchange(ref _abandonedAction, 1); }
        private string _runtime = "—";
        private string _url = ExternalBrowserPolicy.ProbeUrl, _title = "Внешняя страница", _message = "Запуск WebView2…";
        private ExternalBrowserStatus _status = ExternalBrowserStatus.Starting;
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public WebView2 Control { get; }
        public event Action Changed;

        public ExternalBrowserPage(string assets, string chatId)
        {
            _assets = assets; _chatId = chatId;
            Control = new WebView2 { Visible = false, TabStop = true };
            Control.Enter += FocusChanged; Control.Leave += FocusChanged;
            Control.VisibleChanged += VisibilityChanged;
        }

        public ExternalBrowserReadTarget ReadTarget
        { get { return !_disposed && _documentReady && _status == ExternalBrowserStatus.Ready && !string.IsNullOrEmpty(_chatId)
            ? new ExternalBrowserReadTarget(_chatId, SessionId, _documentGeneration) : null; } }

        private static string LoadAsset(string name)
        {
            using (var stream = typeof(ExternalBrowserPage).Assembly.GetManifestResourceStream("RNAssistant.Office." + name))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Browser asset is unavailable: " + name)))
                return reader.ReadToEnd();
        }

        private sealed class FrameResponse { [JsonProperty("frameTree")] public FrameTree Tree { get; set; } }
        private sealed class FrameTree { [JsonProperty("frame")] public Frame Frame { get; set; } }
        private sealed class Frame { [JsonProperty("id")] public string Id { get; set; } }
        private sealed class WorldResponse { [JsonProperty("executionContextId")] public int ContextId { get; set; } }
        private sealed class CaptureResponse<T>
        {
            [JsonProperty("result")] public CaptureValue<T> Result { get; set; }
            [JsonProperty("exceptionDetails")] public object Exception { get; set; }
        }
        private sealed class CaptureValue<T> { [JsonProperty("value")] public T Value { get; set; } }
        private async Task<T> Evaluate<T>(string expression)
        {
            var capture = await Protocol<CaptureResponse<T>>("Runtime.evaluate", new { expression,
                contextId = _semanticContext, returnByValue = true, silent = true, timeout = 1000 });
            if (capture?.Result == null || capture.Exception != null)
                throw ExternalBrowserResourceProvider.Error("The bounded browser operation failed.", "browser_operation_failed");
            return capture.Result.Value;
        }
        private async Task<T> Protocol<T>(string method, object arguments)
        {
            var wire = await Control.CoreWebView2.CallDevToolsProtocolMethodAsync(method, JsonConvert.SerializeObject(arguments));
            if (wire == null || wire.Length > 262144) throw ExternalBrowserResourceProvider.Error("Browser capture exceeded its transport bound.", "browser_capture_invalid");
            return JsonConvert.DeserializeObject<T>(wire);
        }
        public async Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target)
        {
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("Browser document changed before capture.", "browser_document_stale");
            var tree = await Protocol<FrameResponse>("Page.getFrameTree", new { });
            if (string.IsNullOrEmpty(tree?.Tree?.Frame?.Id)) throw ExternalBrowserResourceProvider.Error("The top document is unavailable.", "browser_page_unavailable");
            var world = await Protocol<WorldResponse>("Page.createIsolatedWorld", new { frameId = tree.Tree.Frame.Id, worldName = "rna-semantic-read" });
            if (world == null || world.ContextId <= 0) throw ExternalBrowserResourceProvider.Error("The isolated capture context is unavailable.", "browser_page_unavailable");
            _semanticContext = world.ContextId;
            var content = await Evaluate<ExternalBrowserSemanticContent>(SemanticScript);
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("Browser document changed during capture.", "browser_document_stale");
            if (content == null)
                throw ExternalBrowserResourceProvider.Error("The bounded semantic capture failed.", "browser_capture_failed");
            _observationRevision = ExternalBrowserResourceProvider.Revision(target, content);
            _observationTarget = target;
            _observationId = content.ObservationId; _preparationId = null;
            return content;
        }

        private void CheckAction(ExternalBrowserReadTarget target)
        {
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The browser document changed. Read it again.", "browser_document_stale");
            if (!target.Matches(_observationTarget)) throw ExternalBrowserResourceProvider.Error("The observation belongs to an earlier document. Read it again.", "browser_observation_stale");
            if (_url != ExternalBrowserPolicy.ProbeUrl || Control.Source?.AbsoluteUri != ExternalBrowserPolicy.ProbeUrl)
                throw ExternalBrowserResourceProvider.Error("P6b actions are available only on the packaged local probe.", "browser_probe_only");
            if (_actionUnknown || Volatile.Read(ref _abandonedAction) != 0) throw ExternalBrowserResourceProvider.Error("A previous effect is unknown. Inspect the page; mutations require closing and reopening it manually.", "browser_effect_unknown");
        }

        public async Task<bool> PrepareAsync(ExternalBrowserReadTarget target, string observationRevision,
            string preparationId, ExternalBrowserActionRequest request)
        {
            CheckAction(target);
            if (observationRevision != _observationRevision || _semanticContext <= 0)
                throw ExternalBrowserResourceProvider.Error("Read the current page before acting.", "browser_observation_stale");
            var result = await Evaluate<ExternalBrowserActionResult>("globalThis.__rnaBrowser.prepare(" +
                JsonConvert.SerializeObject(_observationId) + "," + JsonConvert.SerializeObject(preparationId) + "," + JsonConvert.SerializeObject(request) + ")");
            CheckAction(target);
            if (result?.Effect != ExternalBrowserEffect.VerifiedNoChange || result.Code != "browser_prepared")
                throw ExternalBrowserResourceProvider.Error("The exact page element could not be prepared. Read the page again.", ExternalBrowserPolicy.SafeActionCode(result?.Code));
            _preparationId = preparationId;
            return true;
        }

        public async Task<ExternalBrowserActionResult> ActAsync(ExternalBrowserReadTarget target, string preparationId, Action beforeDispatch)
        {
            CheckAction(target);
            if (preparationId != _preparationId || preparationId == null)
                throw ExternalBrowserResourceProvider.Error("The prepared browser action is stale. Read the page again.", "browser_preparation_stale");
            if (_visualContext != _semanticContext)
            {
                await Evaluate<bool>(VisualScript);
                _visualContext = _semanticContext;
            }
            var animationMs = await Evaluate<int>("globalThis.__rnaBrowser.showAction(" + JsonConvert.SerializeObject(preparationId) + "," +
                (Control.Visible ? "true" : "false") + ")");
            if (animationMs > 0) await Task.Delay(Math.Min(animationMs, 300));
            CheckAction(target); // The animation grants no authority; act() also rechecks the exact element/user epoch.
            beforeDispatch();
            _actionUnknown = true; _preparationId = null;
            var result = await Evaluate<ExternalBrowserActionResult>("globalThis.__rnaBrowser.act(" + JsonConvert.SerializeObject(preparationId) + ")");
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The page changed after dispatch; its effect is unknown.", "browser_effect_unknown");
            if (result == null || !Enum.IsDefined(typeof(ExternalBrowserEffect), result.Effect))
                throw ExternalBrowserResourceProvider.Error("The renderer returned no valid effect evidence.", "browser_effect_unknown");
            _actionUnknown = result.Effect == ExternalBrowserEffect.Unknown;
            return result;
        }

        internal async void PresentResult(ExternalBrowserReadTarget target, string preparationId, ExternalBrowserEffect effect)
        {
            if (_disposed || !target.Matches(ReadTarget) || _visualContext != _semanticContext) return;
            try { await Evaluate<bool>("globalThis.__rnaBrowser.completeAction(" + JsonConvert.SerializeObject(preparationId) + "," +
                JsonConvert.SerializeObject(effect.ToString()) + ")"); }
            catch (Exception error) { Diagnostics.RuntimeLog.Error("Browser action presentation was unavailable.", error); }
        }

        private void VisibilityChanged(object sender, EventArgs e) { if (!Control.Visible) HideAction(); }
        private async void HideAction()
        {
            if (_disposed || _visualContext <= 0 || _visualContext != _semanticContext) return;
            try { await Evaluate<bool>("globalThis.__rnaBrowser.hideAction()"); }
            catch (Exception) { /* Navigation/disposal already destroys the old presentation. */ }
        }

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
            core.NavigationCompleted += NavigationCompleted;
            core.SourceChanged += SourceChanged;
            core.HistoryChanged += HistoryChanged;
            core.DocumentTitleChanged += TitleChanged;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
            core.SetVirtualHostNameToFolderMapping("rna-browser-probe.invalid", _assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate(_url);
        }

        public ExternalBrowserStateDto Snapshot(string placement)
        {
            return new ExternalBrowserStateDto
            {
                SessionId = SessionId, Revision = _revision, Status = _status, Url = _url, Title = _title,
                CanGoBack = !_disposed && _canGoBack, CanGoForward = !_disposed && _canGoForward,
                Message = _message,
                Diagnostic = "P5a · WinForms child · Runtime " + _runtime +
                    " · profile InPrivate · " + placement
            };
        }

        public void Execute(ExternalBrowserCommandPayload request)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed || Control.CoreWebView2 == null)
                throw new InvalidOperationException("Страница недоступна. Закройте вкладку и откройте её снова.");
            var core = Control.CoreWebView2;
            switch (request.Action)
            {
                case ExternalBrowserAction.Navigate: core.Navigate(ExternalBrowserPolicy.NavigationUrl(request.Url)); break;
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
            _documentReady = false; _documentGeneration++;
            _status = ExternalBrowserStatus.Failed; Control.Visible = false;
            var message = error.GetBaseException().Message;
            _message = "WebView2: " + message.Substring(0, Math.Min(message.Length, 400)) + ". Закройте вкладку и откройте снова.";
            Publish();
        }
        private void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            HideAction();
            if (_disposed || _status == ExternalBrowserStatus.Failed) { e.Cancel = true; return; }
            _navigationId = e.NavigationId; _navigationRefused = false;
            try { ExternalBrowserPolicy.NavigationUrl(e.Uri); _message = "Загрузка…"; _documentReady = false; _documentGeneration++; }
            catch (InvalidOperationException) { e.Cancel = true; _navigationRefused = true; _message = "Переход запрещён: поддерживаются только HTTP(S)-адреса без credentials."; }
            Publish();
        }
        private void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed) return;
            if (e.NavigationId != _navigationId || _navigationRefused) return;
            _status = ExternalBrowserStatus.Ready;
            _documentReady = e.IsSuccess;
            _message = e.IsSuccess ? "Экспериментальный браузер · Windows-проверка открыта" : "Ошибка загрузки: " + e.WebErrorStatus;
            Publish();
        }
        private void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e) { HideAction(); _url = Control.CoreWebView2.Source; _documentGeneration++; Publish(); }
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
            _disposed = true; _documentReady = false; _documentGeneration++; _status = ExternalBrowserStatus.Closed; _revision++;
            Changed = null;
            Control.Enter -= FocusChanged; Control.Leave -= FocusChanged;
            Control.VisibleChanged -= VisibilityChanged;
            try
            {
                var core = Control.CoreWebView2;
                if (core != null)
                {
                    core.NavigationStarting -= NavigationStarting; core.NavigationCompleted -= NavigationCompleted;
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
