using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using RNAssistant.Application.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.WebView
{
    internal sealed partial class LocalBrowserPage
    {
        internal bool InputAvailable { get; set; }
        private bool _geometryObserver;
        private sealed class BindingCall { public string name { get; set; } public int executionContextId { get; set; } }
        private int _world;
        private string _observationRevision, _observationId, _preparationId;
        private BrowserActionRequest _request;
        private NativePoint _point;
        private DateTime _preparedAt;
        private long _preparedViewport, _preparedControl;
        private bool _actionsBlocked;
        private CancellationTokenSource _input;
        private static readonly string AutomationScript = ReadAsset("LocalBrowserAutomation.js");
        private static string ReadAsset(string name)
        { using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RNAssistant.Office.WebView." + name)) using (var read = new StreamReader(stream ?? throw new InvalidOperationException("Browser asset missing: " + name))) return read.ReadToEnd(); }
        private sealed class FrameTreeResponse { public FrameTree frameTree { get; set; } }
        private sealed class FrameTree { public Frame frame { get; set; } }
        private sealed class Frame { public string id { get; set; } }
        private sealed class World { public int executionContextId { get; set; } }
        private sealed class Remote<T> { public RemoteValue<T> result { get; set; } public object exceptionDetails { get; set; } }
        private sealed class RemoteValue<T> { public T value { get; set; } }
        private sealed class NativePoint { public double x { get; set; } public double y { get; set; } public double? toX { get; set; } public double? toY { get; set; } public int option { get; set; } public string href { get; set; } public bool satisfied { get; set; } }
        private async Task<T> Protocol<T>(string method, object args)
        { return JsonConvert.DeserializeObject<T>(await Core.CallDevToolsProtocolMethodAsync(method, JsonConvert.SerializeObject(args))); }
        private async Task<T> Evaluate<T>(string expression)
        {
            if (_world <= 0 || _closed) throw new InvalidOperationException("The isolated Browser observation is unavailable.");
            var result = await Protocol<Remote<T>>("Runtime.evaluate", new { expression, contextId = _world, returnByValue = true, awaitPromise = true });
            if (result?.exceptionDetails != null || result?.result == null) throw new InvalidOperationException("Browser semantic target changed or was refused.");
            return result.result.value;
        }
        private async Task InitializeAutomationAsync()
        {
            var frame = await Protocol<FrameTreeResponse>("Page.getFrameTree", new { });
            var world = await Protocol<World>("Page.createIsolatedWorld", new { frameId = frame.frameTree.frame.id, worldName = "RNAssistantLocal", grantUniveralAccess = false });
            _world = world.executionContextId;
            if (!_geometryObserver) {
                _geometryObserver = true;
                Core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled").DevToolsProtocolEventReceived += (sender, args) => {
                    if (_closed) return;
                    try {
                        var call = JsonConvert.DeserializeObject<BindingCall>(args.ParameterObjectAsJson);
                        if (call?.name != "__rnaViewportChanged" || call.executionContextId != _world) return;
                        State.ViewportRevision++; _observationId = _observationRevision = null; Publish();
                    } catch { }
                };
                await Core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", "{\"name\":\"__rnaViewportChanged\",\"executionContextName\":\"RNAssistantLocal\"}");
            }
            var visual = File.ReadAllText(Path.Combine(_assets, "js", "app-browser-action-view.js"));
            var styles = File.ReadAllText(Path.Combine(_assets, "css", "app-browser-action-view.css"));
            await Evaluate<bool>(visual + "\n;globalThis.__rnaActionView=RNAssistantBrowserActionView.create(" + JsonConvert.SerializeObject(styles) + ",{nativeOwned:true});true");
            await Evaluate<bool>(AutomationScript);
        }
        private void InvalidateAutomation()
        { _observationRevision = _observationId = _preparationId = null; _input?.Cancel(); }
        internal void BlockActions() { _actionsBlocked = true; InvalidateAutomation(); }
        internal void ResumeAgent(long? revision)
        {
            if (Manual.Pending || revision != State.ControlRevision) throw new InvalidOperationException("The manual-control revision changed.");
            if (Manual.Failed) throw new InvalidOperationException("Resume the paused run explicitly in its chat, then reopen the page.");
            if (_actionsBlocked) throw new InvalidOperationException("The previous input effect is unknown. Close and reopen this Browser tab.");
            State.ManualControl = false; State.ControlRevision++; InvalidateAutomation(); Manual.Resume?.Invoke(); Manual.Resume = null; Publish();
        }
        private void RequireAction()
        {
            if (!InputAvailable || !Ready || !Control.Visible || State.ManualControl || _actionsBlocked || _closed)
                throw new InvalidOperationException("Browser input is unavailable, hidden, under manual control or has an unknown effect.");
            Content.Validate();
        }
        internal async Task<BrowserObservation> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (!Ready) throw new InvalidOperationException("The Browser page is not ready.");
            var id = Guid.NewGuid().ToString("N"); var handle = Handle;
            var result = await Evaluate<BrowserObservation>("__rnaLocalBrowser.read(" + JsonConvert.SerializeObject(id) + ")");
            token.ThrowIfCancellationRequested(); if (!Handle.Matches(handle)) throw new InvalidOperationException("The Browser page changed during observation.");
            result.SourceUrl = ExternalBrowserResourceProvider.SourceAddress(State.Url); result.CapturedUtc = DateTime.UtcNow;
            _observationId = id; _observationRevision = ExternalBrowserResourceProvider.Revision(handle, result); return result;
        }
        internal async Task PrepareAsync(string revision, string id, BrowserActionRequest request, CancellationToken token)
        {
            RequireAction(); token.ThrowIfCancellationRequested();
            if (revision != _observationRevision || string.IsNullOrEmpty(_observationId)) throw new InvalidOperationException("Read the current page before native input.");
            if (request.Locator != null) throw new InvalidOperationException("Local navigation must be dispatched by the Browser session owner.");
            var point = await Evaluate<NativePoint>("__rnaLocalBrowser.prepare(" + JsonConvert.SerializeObject(_observationId) + "," + JsonConvert.SerializeObject(id) + "," + JsonConvert.SerializeObject(request) + ")");
            token.ThrowIfCancellationRequested(); RequireAction();
            _point = point; _request = request; _preparationId = id; _preparedAt = DateTime.UtcNow;
            _preparedViewport = State.ViewportRevision; _preparedControl = State.ControlRevision;
        }
        internal void PrepareNavigation(string revision, string id, BrowserActionRequest request)
        {
            RequireAction();
            if (revision != _observationRevision || string.IsNullOrEmpty(_observationId)) throw new InvalidOperationException("Read the current page before navigation.");
            _request = request; _preparationId = id; _point = null; _preparedAt = DateTime.UtcNow;
            _preparedViewport = State.ViewportRevision; _preparedControl = State.ControlRevision;
        }
        internal async Task<string> AdmitNavigationAsync(string id, CancellationToken token)
        {
            RequireAction(); token.ThrowIfCancellationRequested();
            if (id != _preparationId || DateTime.UtcNow - _preparedAt > TimeSpan.FromSeconds(10) || _preparedViewport != State.ViewportRevision ||
                _preparedControl != State.ControlRevision) throw new InvalidOperationException("The navigation preparation expired.");
            string locator = _request.Locator;
            if (locator == null) {
                await Evaluate<bool>("__rnaLocalBrowser.admit(" + JsonConvert.SerializeObject(id) + ")");
                string path; if (!Content.TryMember(_point.href, out path)) throw new InvalidOperationException("The observed link leaves the local member set.");
                var target = State.Target; var address = new Uri(_point.href);
                locator = LocalLocator(path);
                locator += address.Query + address.Fragment;
            }
            _preparationId = _observationId = _observationRevision = null; return locator;
        }
        internal async Task PresentResult(string id, BrowserActionEffect effect)
        { if (!_closed && Ready) await Evaluate<bool>("__rnaActionView.finish(" + JsonConvert.SerializeObject(id) + "," + JsonConvert.SerializeObject(effect.ToString()) + ");true"); }
        internal async void HideAction()
        { try { if (!_closed && _world > 0) await Evaluate<bool>("__rnaActionView.hide();true"); } catch { } }
        private Task Mouse(string type, double x, double y, string button = "none", int buttons = 0, int count = 0, int modifiers = 0, int deltaX = 0, int deltaY = 0)
        { return Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new { type, x, y, button, buttons, clickCount = count, modifiers, deltaX, deltaY })); }
        private Task Key(string type, string key, int modifiers = 0)
        {
            var codes = new Dictionary<string, int> { { "Control", 17 }, { "Shift", 16 }, { "Alt", 18 }, { "Enter", 13 }, { "Escape", 27 }, { "Tab", 9 },
                { "ArrowLeft", 37 }, { "ArrowRight", 39 }, { "ArrowUp", 38 }, { "ArrowDown", 40 }, { "Home", 36 }, { "End", 35 }, { "PageUp", 33 }, { "PageDown", 34 }, { "Backspace", 8 }, { "Delete", 46 }, { "Space", 32 }, { "A", 65 } };
            int code; if (!codes.TryGetValue(key, out code)) throw new InvalidOperationException("Key is outside the in-page allowlist.");
            return Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonConvert.SerializeObject(new { type, key = key == "Space" ? " " : key,
                windowsVirtualKeyCode = code, nativeVirtualKeyCode = code, modifiers, text = type == "keyDown" && modifiers == 0 ? key == "Space" ? " " : key == "Enter" ? "\r" : "" : "" }));
        }
        internal async Task<BrowserActionResult> ActAsync(string id, Action dispatch, CancellationToken token)
        {
            RequireAction(); token.ThrowIfCancellationRequested();
            if (id != _preparationId || DateTime.UtcNow - _preparedAt > TimeSpan.FromSeconds(10) ||
                _preparedViewport != State.ViewportRevision || _preparedControl != State.ControlRevision) throw new InvalidOperationException("The native preparation or viewport expired.");
            await SetAgentInput(true);
            await Evaluate<bool>("__rnaLocalBrowser.admit(" + JsonConvert.SerializeObject(id) + ")");
            token.ThrowIfCancellationRequested(); RequireAction();
            var request = _request; var point = _point; _preparationId = null;
            if (point.satisfied) return new BrowserActionResult { Effect = BrowserActionEffect.VerifiedNoChange, Code = "browser_verified" };
            var heldKeys = new List<string>(); var down = false; var dispatched = false; var modifiers = 0;
            using (_input = CancellationTokenSource.CreateLinkedTokenSource(token, _cancel.Token)) {
                var input = _input.Token;
                try {
                    await Evaluate<bool>("__rnaLocalBrowser.dispatch(" + JsonConvert.SerializeObject(id) + ")");
                    input.ThrowIfCancellationRequested(); RequireAction(); dispatch(); dispatched = true;
                    foreach (var key in request.Modifiers) { input.ThrowIfCancellationRequested(); modifiers |= key == "Alt" ? 1 : key == "Control" ? 2 : 8; heldKeys.Add(key); await Key("keyDown", key, modifiers); }
                    input.ThrowIfCancellationRequested();
                    await Mouse("mouseMoved", point.x, point.y, modifiers: modifiers);
                    var click = request.Action == "click" || request.Action == "fill" || request.Action == "select" || request.Action == "check" || request.Action == "press_key" || request.Action == "navigate" || request.Action == "drag";
                    if (click) {
                        var button = request.Button ?? "left"; var mask = button == "right" ? 2 : button == "middle" ? 4 : 1;
                        for (var repeat = 0; repeat < (request.Action == "click" ? request.Count : 1); repeat++) {
                            input.ThrowIfCancellationRequested(); down = true; await Mouse("mousePressed", point.x, point.y, button, mask, repeat + 1, modifiers);
                            if (request.DurationMs > 0) await Task.Delay(request.DurationMs, input);
                            if (request.Action == "drag") { input.ThrowIfCancellationRequested(); await Mouse("mouseMoved", point.toX.Value, point.toY.Value, button, mask, 1, modifiers); }
                            input.ThrowIfCancellationRequested(); await Mouse("mouseReleased", request.Action == "drag" ? point.toX.Value : point.x, request.Action == "drag" ? point.toY.Value : point.y, button, 0, repeat + 1, modifiers); down = false;
                        }
                    }
                    if (request.Action == "fill") {
                        heldKeys.Add("Control"); await Key("keyDown", "Control", 2); heldKeys.Add("A"); await Key("keyDown", "A", 2);
                        await Key("keyUp", "A", 2); heldKeys.Remove("A"); await Key("keyUp", "Control"); heldKeys.Remove("Control");
                        input.ThrowIfCancellationRequested();
                        if (request.Value.Length == 0) { heldKeys.Add("Backspace"); await Key("keyDown", "Backspace"); await Key("keyUp", "Backspace"); heldKeys.Remove("Backspace"); }
                        else await Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonConvert.SerializeObject(new { text = request.Value }));
                    } else if (request.Action == "press_key") {
                        var chord = request.Key.Split('+'); var key = chord[chord.Length - 1];
                        for (var i = 0; i < chord.Length - 1; i++) { modifiers |= chord[i] == "Control" ? 2 : 8; heldKeys.Add(chord[i]); await Key("keyDown", chord[i], modifiers); }
                        for (var i = 0; i < request.Count; i++) { input.ThrowIfCancellationRequested(); heldKeys.Add(key); await Key("keyDown", key, modifiers); await Key("keyUp", key, modifiers); heldKeys.Remove(key); }
                    } else if (request.Action == "select") {
                        heldKeys.Add("Home"); await Key("keyDown", "Home"); await Key("keyUp", "Home"); heldKeys.Remove("Home");
                        for (var i = 0; i < point.option; i++) { input.ThrowIfCancellationRequested(); heldKeys.Add("ArrowDown"); await Key("keyDown", "ArrowDown"); await Key("keyUp", "ArrowDown"); heldKeys.Remove("ArrowDown"); }
                        heldKeys.Add("Enter"); await Key("keyDown", "Enter"); await Key("keyUp", "Enter"); heldKeys.Remove("Enter");
                    } else if (request.Action == "scroll") await Mouse("mouseWheel", point.x, point.y, modifiers: modifiers, deltaX: request.DeltaX, deltaY: request.DeltaY);
                    else if (request.Action == "hover" && request.DurationMs > 0) await Task.Delay(request.DurationMs, input);
                    input.ThrowIfCancellationRequested(); RequireAction();
                    BrowserActionResult result = null;
                    for (var attempt = 0; attempt < 20; attempt++) {
                        await Task.Delay(100, input); RequireAction();
                        result = await Evaluate<BrowserActionResult>("__rnaLocalBrowser.verify(" + JsonConvert.SerializeObject(id) + "," + (attempt == 19 ? "true" : "false") + ")");
                        if (result.Effect != BrowserActionEffect.Unknown) break;
                    }
                    if (result.Effect == BrowserActionEffect.Unknown) BlockActions();
                    return result;
                } catch { if (dispatched) BlockActions(); throw; }
                finally {
                    // Cancellation, navigation, disposal and physical takeover all release held state.
                    if (Controller != null && !_closed) {
                        try { if (down) await Mouse("mouseReleased", point.x, point.y, request.Button ?? "left"); } catch { _actionsBlocked = true; }
                        for (var i = heldKeys.Count - 1; i >= 0; i--) try { await Key("keyUp", heldKeys[i]); } catch { _actionsBlocked = true; }
                    }
                    _input = null;
                }
            }
            // The field must not retain a disposed cancellation source.
        }
    }
}
