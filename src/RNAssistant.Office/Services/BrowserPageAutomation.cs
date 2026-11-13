using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // One observation/action/control mechanism; native adapters supply the exact document and authority.
    internal sealed class BrowserPageAutomation : IDisposable
    {
        private readonly string _chatId;
        private readonly Action<Action> _post;
        private readonly Func<bool> _onUi;
        internal SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
        private readonly Func<ExternalBrowserReadTarget> _target;
        private readonly Func<string> _sourceUrl;
        private readonly Func<bool> _visible;
        private readonly Func<string, string, Task<string>> _protocol;
        private readonly Action<string> _requireAccess;
        private readonly Action _changed;
        private bool _disposed;
        private string _message;
        internal bool ManualControl => _manualControl;
        internal long ControlRevision => _controlRevision;
        internal string Message => _message;
        private ExternalBrowserReadTarget ReadTarget => _disposed ? null : _target();
        private readonly Func<string, string, Task<Action>> _pauseRun;
        private Task<Action> _pauseTask;
        private bool _manualControl, _pauseFailed;
        private long _controlRevision;
        private string _pauseId;
        private int _interactionEpoch;
        internal static readonly string ControlScript = LoadAsset("ExternalBrowserControl.js");
        private static readonly string SemanticScript = LoadAsset("ExternalBrowserSemanticRead.js");
        private static readonly string VisualScript = LoadAsset("ExternalBrowserActionView.js") +
            ";globalThis.__rnaBrowserVisual=globalThis.__rnaBrowserVisual||RNAssistantBrowserActionView.create(" +
            JsonConvert.SerializeObject(LoadAsset("ProductTokens.css").Replace(":root", ":host") + LoadAsset("ExternalBrowserActionView.css")) + ");true";
        private int _semanticContext;
        private int _visualContext;
        private string _observationRevision, _observationId, _preparationId;
        private string _preparedNavigationUrl;
        private bool _preparedLink;
        private ExternalBrowserNavigation _navigation;
        private ExternalBrowserReadTarget _observationTarget;
        private bool _actionUnknown;
        private int _abandonedAction;
        internal void BlockActions() { Interlocked.Exchange(ref _abandonedAction, 1); }

        internal BrowserPageAutomation(string chatId, Func<ExternalBrowserReadTarget> target, Func<string> sourceUrl, Func<bool> visible,
            Func<string, string, Task<string>> protocol, Action<string> requireAccess,
            Func<string, string, Task<Action>> pauseRun, Action changed, Action<Action> post, Func<bool> onUi)
        { _chatId = chatId; _target = target; _sourceUrl = sourceUrl; _visible = visible; _protocol = protocol;
            _requireAccess = requireAccess; _pauseRun = pauseRun; _changed = changed; _post = post; _onUi = onUi; }
        internal void ResetDocument() { HideAction(); _semanticContext = 0; _visualContext = 0; _interactionEpoch = 0; _observationTarget = null; _preparationId = null; }
        internal void NavigationStarting(ulong id, string url, bool redirected) { _navigation?.Starting(id, url, redirected); }
        internal void NavigationCompleted(ulong? id, bool success, string url) { _navigation?.Completed(id, success, ReadTarget, url); }
        internal void NavigationUnknown(string code) { _navigation?.Unknown(code); }
        private void Publish() { if (!_disposed) _changed(); }
        public void Dispose() { if (_disposed) return; HideAction(); AbandonNavigation("browser_effect_unknown"); _disposed = true; BlockActions(); }
        internal async Task<T> InvokeAsync<T>(ExternalBrowserReadTarget target,
            Func<BrowserPageAutomation, Action, Task<T>> operation, CancellationToken token, Action beforeDispatch = null,
            int timeoutMs = 3000, Func<T, ExternalBrowserReadTarget> resultTarget = null)
        {
            token.ThrowIfCancellationRequested();
            if (_onUi()) throw ExternalBrowserResourceProvider.Error("Browser operations require a background caller.", "browser_dispatch_invalid");
            var gate = Gate;
            if (!await gate.WaitAsync(3000, token).ConfigureAwait(false))
                throw ExternalBrowserResourceProvider.Error("A browser operation is still pending.", "browser_operation_busy");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatchSync = new object(); bool dispatched = false; BrowserPageAutomation page = this;
            Action dispatch = () => {
                lock (dispatchSync)
                {
                    if (completion.Task.IsCompleted) throw new OperationCanceledException();
                    token.ThrowIfCancellationRequested();
                    beforeDispatch(); dispatched = true;
                }
            };
            try
            {
                _post(async () =>
                {
                    try
                    {
                        if (completion.Task.IsCompleted) return;
                        token.ThrowIfCancellationRequested();
                        if (_disposed || !target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The browser binding is no longer current.", "browser_document_stale");
                        page = this;
                        var value = await operation(page, dispatch);
                        var after = resultTarget?.Invoke(value) ?? target;
                        if (after.ChatId != target.ChatId || after.SessionId != target.SessionId || !after.Matches(ReadTarget))
                            throw ExternalBrowserResourceProvider.Error("The page changed during the operation.", "browser_document_stale");
                        completion.TrySetResult(value);
                    }
                    catch (Exception error)
                    {
                        if (dispatched) page?.BlockActions();
                        if (error is OperationCanceledException) completion.TrySetCanceled();
                        else completion.TrySetException(error is RNAssistant.Core.Models.ResourceRequestException ? error :
                            ExternalBrowserResourceProvider.Error("The native browser operation failed.", "browser_operation_failed"));
                    }
                    finally { gate.Release(); }
                });
            }
            catch { gate.Release(); throw; }
            if (await Task.WhenAny(completion.Task, Task.Delay(timeoutMs, token)).ConfigureAwait(false) != completion.Task)
            {
                // Queued work cannot dispatch later. In-flight work retains the gate until it ends.
                bool abandoned;
                lock (dispatchSync)
                {
                    abandoned = completion.TrySetCanceled();
                    if (abandoned && dispatched) page.BlockActions();
                }
                if (abandoned)
                {
                    token.ThrowIfCancellationRequested();
                    throw ExternalBrowserResourceProvider.Error("The browser operation timed out. A dispatched effect may be unknown.", "browser_operation_timeout");
                }
            }
            return await completion.Task.ConfigureAwait(false);
        }

        private static string LoadAsset(string name)
        {
            using (var stream = typeof(BrowserPageAutomation).Assembly.GetManifestResourceStream("RNAssistant.Office." + name))
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
            var wire = await _protocol(method, JsonConvert.SerializeObject(arguments));
            if (wire == null || wire.Length > 262144) throw ExternalBrowserResourceProvider.Error("Browser capture exceeded its transport bound.", "browser_capture_invalid");
            return JsonConvert.DeserializeObject<T>(wire);
        }
        public async Task<ExternalBrowserSemanticContent> ReadAsync(ExternalBrowserReadTarget target)
        {
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("Browser document changed before capture.", "browser_document_stale");
            RequireAgentAccess();
            var sourceUrl = _sourceUrl();
            if (_manualControl) { EnsureUserPause(); await Evaluate<bool>("globalThis.__rnaBrowserControl.hold()"); }
            var content = await Evaluate<ExternalBrowserSemanticContent>(SemanticScript);
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("Browser document changed during capture.", "browser_document_stale");
            RequireAgentAccess(); // A revoked permission cannot deliver an in-flight observation.
            if (sourceUrl != _sourceUrl())
                throw ExternalBrowserResourceProvider.Error("Browser address changed during capture.", "browser_document_stale");
            if (content == null)
                throw ExternalBrowserResourceProvider.Error("The bounded semantic capture failed.", "browser_capture_failed");
            content.SourceUrl = ExternalBrowserResourceProvider.SourceAddress(sourceUrl);
            content.CapturedUtc = DateTime.UtcNow;
            _observationRevision = ExternalBrowserResourceProvider.Revision(target, content);
            _observationTarget = target;
            _observationId = content.ObservationId; _preparationId = null; _preparedNavigationUrl = null;
            return content;
        }

        private void RequireAgentAccess() { _requireAccess(_sourceUrl()); }

        internal async Task InitializeAsync()
        {
            var target = ReadTarget;
            var tree = await Protocol<FrameResponse>("Page.getFrameTree", new { });
            var frame = tree?.Tree?.Frame;
            if (string.IsNullOrEmpty(frame?.Id)) throw ExternalBrowserResourceProvider.Error("The exact browser document is unavailable.", "browser_page_unavailable");
            var world = await Protocol<WorldResponse>("Page.createIsolatedWorld", new { frameId = frame.Id, worldName = "rna-semantic-read" });
            if (world == null || world.ContextId <= 0) throw ExternalBrowserResourceProvider.Error("The isolated browser context is unavailable.", "browser_page_unavailable");
            if (_disposed || !target.Matches(ReadTarget)) return;
            _semanticContext = world.ContextId;
            var state = await Evaluate<ControlState>(ControlScript + ";globalThis.__rnaBrowserScope=Object.freeze({interact:true,navigate:" +
                (target.CanNavigate ? "true" : "false") + "});globalThis.__rnaBrowserControl.snapshot()");
            if (_disposed || !target.Matches(ReadTarget)) return;
            _interactionEpoch = Math.Max(_interactionEpoch, state.Epoch);
            if (state.Manual) TakeControl();
            if (_manualControl) await Evaluate<bool>("globalThis.__rnaBrowserControl.hold()");
        }

        internal void ControlEvent(string json)
        {
            if (_disposed || json.Length > 1024) return;
            try
            {
                var binding = JsonConvert.DeserializeObject<ControlBinding>(json);
                if (binding?.Name != "__rnaUserControl" || binding.ContextId != _semanticContext || _semanticContext <= 0) return;
                var state = JsonConvert.DeserializeObject<ControlState>(binding.Payload ?? "");
                if (state == null || state.Epoch <= _interactionEpoch) return;
                _interactionEpoch = state.Epoch;
                TakeControl();
            }
            catch (JsonException error) { Diagnostics.RuntimeLog.Error("Browser user-control signal was invalid.", error); }
        }

        internal void TakeControl()
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
            RequireAgentAccess();
            var target = ReadTarget; var epoch = _interactionEpoch;
            if (_pauseFailed) EnsureUserPause();
            var resume = _pauseTask == null ? null : await _pauseTask;
            if (_pauseFailed) throw new InvalidOperationException(_message);
            RequireAgentAccess();
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
            if (navigation && !target.CanNavigate) throw ExternalBrowserResourceProvider.Error("This preview has no external navigation capability.", "browser_navigation_unavailable");
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
                _requireAccess(url);
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
                (_visible() ? "true" : "false") + ")");
            if (animationMs > 0) await Task.Delay(Math.Min(animationMs, 300));
            CheckAction(target); // The animation grants no authority; act() also rechecks the exact element/user epoch.
            if (_preparedNavigationUrl != null) return await NavigateAgentAsync(target, preparationId, beforeDispatch);
            beforeDispatch();

            _actionUnknown = true; _preparationId = null;
            var result = await Evaluate<ExternalBrowserActionResult>("globalThis.__rnaBrowser.act(" + JsonConvert.SerializeObject(preparationId) + ")", true);
            if (!target.Matches(ReadTarget)) throw ExternalBrowserResourceProvider.Error("The page changed after dispatch; its effect is unknown.", "browser_effect_unknown");
            RequireAgentAccess();
            if (result == null || !Enum.IsDefined(typeof(ExternalBrowserEffect), result.Effect))
                throw ExternalBrowserResourceProvider.Error("The renderer returned no valid effect evidence.", "browser_effect_unknown");
            _actionUnknown = result.Effect == ExternalBrowserEffect.Unknown;
            return result;
        }

        internal void AbandonNavigation(string code)
        { var pending = _navigation; _navigation = null; pending?.Unknown(code); }

        private async Task<ExternalBrowserActionResult> NavigateAgentAsync(ExternalBrowserReadTarget target, string preparationId, Action beforeDispatch)
        {
            var url = _preparedNavigationUrl;
            var pending = new ExternalBrowserNavigation(target, url,
                address => _requireAccess(address));
            var follow = _preparedLink;
            beforeDispatch();  _actionUnknown = true; _preparationId = null; _preparedNavigationUrl = null; _preparedLink = false; _navigation = pending;
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

        internal async void HideAction()
        {
            if (_disposed || _visualContext <= 0 || _visualContext != _semanticContext) return;
            try { await Evaluate<bool>("globalThis.__rnaBrowser.hideAction()"); }
            catch (Exception) { /* Navigation/disposal already destroys the old presentation. */ }
        }

    }
}
