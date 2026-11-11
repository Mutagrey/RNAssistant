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
        private readonly Func<RNAssistant.Core.Models.AppSettings> _settings;
        private readonly Func<string, string, Task<Action>> _pauseRun;
        private Task<Action> _pauseTask;
        private bool _manualControl, _pauseFailed;
        private long _controlRevision;
        private string _pauseId;
        private int _interactionEpoch;
        private CoreWebView2DevToolsProtocolEventReceiver _controlEvents;
        private static readonly string ControlScript = LoadAsset("ExternalBrowserControl.js");
        private static readonly string SemanticScript = LoadAsset("ExternalBrowserSemanticRead.js");
        private static readonly string VisualScript = LoadAsset("ExternalBrowserActionView.js") +
            ";globalThis.__rnaBrowserVisual=globalThis.__rnaBrowserVisual||RNAssistantBrowserActionView.create(" +
            JsonConvert.SerializeObject(LoadAsset("ProductTokens.css").Replace(":root", ":host") + LoadAsset("ExternalBrowserActionView.css")) + ");true";
        private int _semanticContext;
        private int _visualContext;
        private string _observationRevision, _observationId, _preparationId;
        private string _preparedNavigationUrl;
        private bool _preparedLink, _agentNavigationRestricted;
        private ExternalBrowserNavigation _navigation;
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

        public ExternalBrowserPage(string assets, string chatId, Func<RNAssistant.Core.Models.AppSettings> settings,
            Func<string, string, Task<Action>> pauseRun)
        {
            _assets = assets; _chatId = chatId; _settings = settings; _pauseRun = pauseRun;
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
        private sealed class ControlBinding
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("executionContextId")] public int ContextId { get; set; }
            [JsonProperty("payload")] public string Payload { get; set; }
        }
        private sealed class ControlState
        {
            [JsonProperty("manual")] public bool Manual { get; set; }
            [JsonProperty("epoch")] public int Epoch { get; set; }
        }
        private sealed class CaptureResponse<T>
        {
            [JsonProperty("result")] public CaptureValue<T> Result { get; set; }
            [JsonProperty("exceptionDetails")] public object Exception { get; set; }
        }
        private sealed class CaptureValue<T> { [JsonProperty("value")] public T Value { get; set; } }
        private async Task<T> Evaluate<T>(string expression, bool awaitPromise = false)
        {
            var capture = await Protocol<CaptureResponse<T>>("Runtime.evaluate", new { expression,
                contextId = _semanticContext, returnByValue = true, silent = true, awaitPromise, timeout = awaitPromise ? 2000 : 1000 });
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
            RequireAgentAccess();
            if (_manualControl) { EnsureUserPause(); await Evaluate<bool>("globalThis.__rnaBrowserControl.hold()"); }
            var content = await Evaluate<ExternalBrowserSemanticContent>(SemanticScript);
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("Browser document changed during capture.", "browser_document_stale");
            RequireAgentAccess(); // A revoked permission cannot deliver an in-flight observation.
            if (content == null)
                throw ExternalBrowserResourceProvider.Error("The bounded semantic capture failed.", "browser_capture_failed");
            _observationRevision = ExternalBrowserResourceProvider.Revision(target, content);
            _observationTarget = target;
            _observationId = content.ObservationId; _preparationId = null; _preparedNavigationUrl = null;
            return content;
        }

        private void RequireAgentAccess()
        {
            var actual = Control.CoreWebView2.Source;
            if (actual != _url) throw ExternalBrowserResourceProvider.Error("Browser navigation changed before admission.", "browser_document_stale");
            ExternalBrowserPolicy.RequireAgentAccess(actual, _settings().BrowserAgentAllowedSites);
        }

        private async Task InitializeControlAsync(long generation)
        {
            var tree = await Protocol<FrameResponse>("Page.getFrameTree", new { });
            if (string.IsNullOrEmpty(tree?.Tree?.Frame?.Id)) throw ExternalBrowserResourceProvider.Error("The top document is unavailable.", "browser_page_unavailable");
            var world = await Protocol<WorldResponse>("Page.createIsolatedWorld", new { frameId = tree.Tree.Frame.Id, worldName = "rna-semantic-read" });
            if (world == null || world.ContextId <= 0) throw ExternalBrowserResourceProvider.Error("The isolated capture context is unavailable.", "browser_page_unavailable");
            if (_disposed || generation != _documentGeneration) return;
            _semanticContext = world.ContextId;
            var state = await Evaluate<ControlState>("globalThis.__rnaBrowserControl.snapshot()");
            if (_disposed || generation != _documentGeneration) return;
            _interactionEpoch = Math.Max(_interactionEpoch, state.Epoch);
            if (state.Manual) TakeControl();
            if (_manualControl) await Evaluate<bool>("globalThis.__rnaBrowserControl.hold()");
        }

        private void ControlEvent(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            if (_disposed || e.ParameterObjectAsJson.Length > 1024) return;
            try
            {
                var binding = JsonConvert.DeserializeObject<ControlBinding>(e.ParameterObjectAsJson);
                if (binding?.Name != "__rnaUserControl" || binding.ContextId != _semanticContext || _semanticContext <= 0) return;
                var state = JsonConvert.DeserializeObject<ControlState>(binding.Payload ?? "");
                if (state == null || state.Epoch <= _interactionEpoch) return;
                _interactionEpoch = state.Epoch;
                TakeControl();
            }
            catch (JsonException error) { Diagnostics.RuntimeLog.Error("Browser user-control signal was invalid.", error); }
        }

        private void TakeControl()
        {
            _navigation?.Unknown("browser_user_control");
            if (!_manualControl) { _pauseId = Guid.NewGuid().ToString("N"); _pauseTask = null; }
            _manualControl = true; _controlRevision++;
            _observationTarget = null; _preparationId = null; _preparedNavigationUrl = null;
            HideAction();
            if (_pauseTask == null) EnsureUserPause();
            Publish();
        }

        private void EnsureUserPause()
        {
            if (_pauseTask == null || _pauseTask.IsCompleted) _pauseTask = CaptureUserPauseAsync();
        }

        private async Task<Action> CaptureUserPauseAsync()
        {
            var pauseId = _pauseId;
            try
            {
                var resume = string.IsNullOrEmpty(_chatId) ? null : await _pauseRun(_chatId, pauseId);
                if (_disposed || pauseId != _pauseId) return null;
                _pauseFailed = false; return resume;
            }
            catch (Exception error)
            {
                if (_disposed || pauseId != _pauseId) return null;
                _pauseFailed = true;
                _message = "Управление агента заблокировано; приостановить запуск не удалось. Остановите запуск в чате.";
                Diagnostics.RuntimeLog.Error("Browser run pause failed.", error); Publish(); return null;
            }
        }

        public async Task ResumeAgentAsync(long? revision)
        {
            if (!_manualControl || revision != _controlRevision || ReadTarget == null)
                throw new InvalidOperationException("Управление страницей изменилось. Повторите явное продолжение.");
            var target = ReadTarget; var epoch = _interactionEpoch;
            if (_pauseFailed) EnsureUserPause();
            var resume = _pauseTask == null ? null : await _pauseTask;
            if (_pauseFailed) throw new InvalidOperationException(_message);
            if (!target.Matches(ReadTarget) || revision != _controlRevision ||
                !await Evaluate<bool>("globalThis.__rnaBrowserControl.resume(" + epoch + ")") || revision != _controlRevision)
                throw new InvalidOperationException("Страница или ручной ввод изменились. Повторите продолжение.");
            _observationTarget = null; _preparationId = null;
            _manualControl = false; _controlRevision++; _pauseTask = null; _pauseId = null;
            var releasedRevision = _controlRevision;
            try { if (resume != null) await Task.Run(resume); }
            catch
            {
                if (target.Matches(ReadTarget) && releasedRevision == _controlRevision) TakeControl();
                throw;
            }
            if (!target.Matches(ReadTarget) || releasedRevision != _controlRevision) return;
            _message = "Управление возвращено агенту";
            Publish();
        }

        private void CheckAction(ExternalBrowserReadTarget target)
        {
            RequireAgentAccess();
            if (_manualControl) { EnsureUserPause(); throw ExternalBrowserResourceProvider.Error("The user is controlling this page. Wait for explicit Resume agent; do not retry browser actions.", "browser_user_control"); }
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The browser document changed. Read it again.", "browser_document_stale");
            if (!target.Matches(_observationTarget)) throw ExternalBrowserResourceProvider.Error("The observation belongs to an earlier document. Read it again.", "browser_observation_stale");
            if (_actionUnknown || Volatile.Read(ref _abandonedAction) != 0) throw ExternalBrowserResourceProvider.Error("A previous effect is unknown. Inspect the page; mutations require closing and reopening it manually.", "browser_effect_unknown");
        }

        public async Task<bool> PrepareAsync(ExternalBrowserReadTarget target, string observationRevision,
            string preparationId, ExternalBrowserActionRequest request)
        {
            var navigation = request.Action == "navigate";
            CheckAction(target);
            if (observationRevision != _observationRevision || _semanticContext <= 0)
                throw ExternalBrowserResourceProvider.Error("Read the current page before acting.", "browser_observation_stale");
            _preparedNavigationUrl = null; _preparedLink = false;
            if (navigation)
            {
                if ((request.Url == null) == (request.Name == null))
                    throw ExternalBrowserResourceProvider.Error("Provide exactly one address or observed link.", "browser_action_invalid");
                string url;
                if (request.Name != null)
                {
                    var link = await Evaluate<ExternalBrowserLinkPreparation>("globalThis.__rnaBrowser.prepareLink(" +
                        JsonConvert.SerializeObject(_observationId) + "," + JsonConvert.SerializeObject(preparationId) + "," + JsonConvert.SerializeObject(request.Name) + ")");
                    if (link?.Prepared != true) throw ExternalBrowserResourceProvider.Error("The observed link changed or is not actionable.", ExternalBrowserPolicy.SafeActionCode(link?.Code));
                    url = link.Url; _preparedLink = true;
                }
                else url = ExternalBrowserPolicy.NavigationUrl(request.Url);
                ExternalBrowserPolicy.RequireAgentAccess(url, _settings().BrowserAgentAllowedSites);
                var control = await Evaluate<ControlState>("globalThis.__rnaBrowserControl.snapshot()");
                if (control.Manual) TakeControl();
                CheckAction(target);
                _preparedNavigationUrl = url; _preparationId = preparationId;
                return true;
            }
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
            if (_preparedNavigationUrl != null && !_preparedLink) return await NavigateAgentAsync(target, preparationId, beforeDispatch);
            if (_visualContext != _semanticContext)
            {
                await Evaluate<bool>(VisualScript);
                _visualContext = _semanticContext;
            }
            var animationMs = await Evaluate<int>("globalThis.__rnaBrowser.showAction(" + JsonConvert.SerializeObject(preparationId) + "," +
                (Control.Visible ? "true" : "false") + ")");
            if (animationMs > 0) await Task.Delay(Math.Min(animationMs, 300));
            CheckAction(target); // The animation grants no authority; act() also rechecks the exact element/user epoch.
            if (_preparedNavigationUrl != null) return await NavigateAgentAsync(target, preparationId, beforeDispatch);
            beforeDispatch();
            _agentNavigationRestricted = true; _actionUnknown = true; _preparationId = null;
            var result = await Evaluate<ExternalBrowserActionResult>("globalThis.__rnaBrowser.act(" + JsonConvert.SerializeObject(preparationId) + ")", true);
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The page changed after dispatch; its effect is unknown.", "browser_effect_unknown");
            RequireAgentAccess();
            if (result == null || !Enum.IsDefined(typeof(ExternalBrowserEffect), result.Effect))
                throw ExternalBrowserResourceProvider.Error("The renderer returned no valid effect evidence.", "browser_effect_unknown");
            _actionUnknown = result.Effect == ExternalBrowserEffect.Unknown;
            return result;
        }

        private void AbandonNavigation(string code)
        { var pending = _navigation; _navigation = null; pending?.Unknown(code); }

        private async Task<ExternalBrowserActionResult> NavigateAgentAsync(ExternalBrowserReadTarget target, string preparationId, Action beforeDispatch)
        {
            var url = _preparedNavigationUrl;
            var pending = new ExternalBrowserNavigation(target, url,
                address => ExternalBrowserPolicy.RequireAgentAccess(address, _settings().BrowserAgentAllowedSites));
            var follow = _preparedLink;
            beforeDispatch(); _agentNavigationRestricted = true; _actionUnknown = true; _preparationId = null; _preparedNavigationUrl = null; _preparedLink = false; _navigation = pending;
            try
            {
                // The fixed isolated script checks trusted input and starts navigation atomically.
                var dispatched = await Evaluate<ExternalBrowserNavigationDispatch>("globalThis.__rnaBrowser." + (follow ? "follow(" : "navigate(") +
                    JsonConvert.SerializeObject(follow ? preparationId : _observationId) + "," + JsonConvert.SerializeObject(url) + ")");
                if (dispatched == null) throw ExternalBrowserResourceProvider.Error("Navigation dispatch is unknown.", "browser_effect_unknown");
                if (!dispatched.Started)
                {
                    _actionUnknown = false;
                    return new ExternalBrowserActionResult { Effect = dispatched.Unchanged ? ExternalBrowserEffect.VerifiedNoChange : ExternalBrowserEffect.Rejected,
                        Code = ExternalBrowserPolicy.SafeActionCode(dispatched.Code) };
                }
                if (await Task.WhenAny(pending.Completion, Task.Delay(12000)) != pending.Completion)
                    pending.Unknown("browser_effect_unknown");
                var result = await pending.Completion;
                if (result.Effect == ExternalBrowserEffect.VerifiedChange)
                {
                    await Evaluate<bool>("globalThis.__rnaBrowser ? globalThis.__rnaBrowser.completeNavigation() : true");
                    RequireAgentAccess();
                    if (_manualControl || !result.NavigationTarget.Matches(ReadTarget) || Volatile.Read(ref _abandonedAction) != 0)
                        throw ExternalBrowserResourceProvider.Error("The browser changed before navigation delivery.", "browser_effect_unknown");
                }
                _actionUnknown = result.Effect == ExternalBrowserEffect.Unknown;
                return result;
            }
            finally
            {
                // Keep the redirect gate after timeout/takeover/channel loss. Only an explicit
                // manual navigation or close may release it; a late redirect gets no new grant.
                if (_navigation == pending)
                {
                    if (_actionUnknown) pending.Unknown("browser_effect_unknown");
                    else _navigation = null;
                }
            }
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
            _controlEvents = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
            _controlEvents.DevToolsProtocolEventReceived += ControlEvent;
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", JsonConvert.SerializeObject(new { name = "__rnaUserControl", executionContextName = "rna-semantic-read" }));
            await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", JsonConvert.SerializeObject(new { source = ControlScript, worldName = "rna-semantic-read" }));
            if (_disposed) throw new ObjectDisposedException(nameof(ExternalBrowserPage));
            core.SetVirtualHostNameToFolderMapping("rna-browser-probe.invalid", _assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate(_url);
        }

        public ExternalBrowserStateDto Snapshot(string placement)
        {
            return new ExternalBrowserStateDto
            {
                SessionId = SessionId, Revision = _revision, Status = _status, Url = _url, Title = _title,
                ManualControl = _manualControl, ControlRevision = _controlRevision,
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
            if (request.Action == ExternalBrowserAction.Navigate || request.Action == ExternalBrowserAction.Back ||
                request.Action == ExternalBrowserAction.Forward || request.Action == ExternalBrowserAction.Reload)
            { _agentNavigationRestricted = false; AbandonNavigation("browser_user_control"); TakeControl(); }
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
            AbandonNavigation("browser_effect_unknown");
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
            if (e.IsUserInitiated) { _agentNavigationRestricted = false; AbandonNavigation("browser_user_control"); TakeControl(); }
            _navigationId = e.NavigationId; _navigationRefused = false;
            try
            {
                ExternalBrowserPolicy.NavigationUrl(e.Uri);
                if (_agentNavigationRestricted) ExternalBrowserPolicy.RequireAgentAccess(e.Uri, _settings().BrowserAgentAllowedSites);
                _navigation?.Starting(e.NavigationId, e.Uri, e.IsRedirect);
                _message = "Загрузка…";
                _documentReady = false; _documentGeneration++; _semanticContext = 0; _visualContext = 0; _interactionEpoch = 0;
            }
            catch (Exception error) when (error is InvalidOperationException || error is RNAssistant.Core.Models.ResourceRequestException)
            {
                e.Cancel = true; _navigationRefused = true;
                _navigation?.Unknown(error is RNAssistant.Core.Models.ResourceRequestException denied ? denied.ErrorCode : "browser_navigation_changed");
                _message = "Переход запрещён: " + error.Message;
            }
            Publish();
        }
        private async void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_disposed || _status == ExternalBrowserStatus.Failed) return;
            if (e.NavigationId != _navigationId || _navigationRefused) return;
            var generation = _documentGeneration;
            if (e.IsSuccess)
            {
                try { await InitializeControlAsync(generation); }
                catch (Exception error) { if (!_disposed && generation == _documentGeneration) Fail(error); return; }
            }
            if (_disposed || generation != _documentGeneration) return;
            _status = ExternalBrowserStatus.Ready;
            _documentReady = e.IsSuccess;
            _message = e.IsSuccess ? "Экспериментальный браузер · Windows-проверка открыта" : "Ошибка загрузки: " + e.WebErrorStatus;
            Publish();
            _navigation?.Completed(e.NavigationId, e.IsSuccess, ReadTarget, _url);
        }
        private void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            HideAction(); _url = Control.CoreWebView2.Source; _documentGeneration++; Publish();
            if (!e.IsNewDocument && _documentReady) _navigation?.Completed(null, true, ReadTarget, _url);
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
            AbandonNavigation("browser_effect_unknown");
            _disposed = true; _documentReady = false; _documentGeneration++; _status = ExternalBrowserStatus.Closed; _revision++;
            Changed = null;
            Control.Enter -= FocusChanged; Control.Leave -= FocusChanged;
            Control.VisibleChanged -= VisibilityChanged;
            try
            {
                if (_controlEvents != null) _controlEvents.DevToolsProtocolEventReceived -= ControlEvent;
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
