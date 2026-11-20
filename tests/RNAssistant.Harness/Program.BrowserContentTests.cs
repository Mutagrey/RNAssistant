using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BrowserAnnotationsPinSavedEntry()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths)) {
                    var plugin = PluginFixture(service, paths, false);
                    var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = plugin.SourceWorkspaceId,
                        ChatId = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = plugin.SourceWorkspaceId }).SessionId };
                    var file = Path.Combine(plugin.SourceRoot, "index.html"); var original = File.ReadAllText(file);
                    File.WriteAllText(file, original + "\n<!-- v2 -->"); SavePluginCurrent(service, plugin.PluginId);
                    File.WriteAllText(file, original); var third = SavePluginCurrent(service, plugin.PluginId);
                    Func<string, BrowserContentSession> open = version => service.OpenBrowserContent(service.ResolveBrowserLocator(
                        new BrowserResolveRequest { OwnerScope = owner, Locator = BrowserLocalLocator.ForPlugin(plugin.PluginId, "index.html", version) }).Target, CancellationToken.None);
                    using (var firstPage = open(plugin.SavedVersionId)) using (var thirdPage = open(third.Version.VersionId)) {
                        var first = firstPage.Annotation("index.html"); var last = thirdPage.Annotation("index.html");
                        AssertEqual(first.Source.Uri, last.Source.Uri, "v1 and v3 retain the same immutable content");
                        AssertTrue(first.Page.SavedVersionId != last.Page.SavedVersionId, "catalog entries remain distinct");
                        AssertTrue(first.Context.SourceId != last.Context.SourceId, "equal bytes cannot reanchor an annotation to another saved entry");
                    }
                }
            });
        }
        private static void BrowserNativeDeliveryLifecycle()
        {
            var renderer = new BrowserRendererFixture(); var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "project", ChatId = "chat" };
            var caller = Thread.CurrentThread.ManagedThreadId;
            using (var runtime = new LocalBrowserPageRuntime(request => {
                AssertTrue(Thread.CurrentThread.ManagedThreadId != caller, "source resolution leaves the UI caller");
                return new BrowserResolveResult { Locator = request.Locator,
                    Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                        OwnerScope = request.OwnerScope, DisplayLocator = request.Locator, Capabilities = new BrowserTargetCapabilities { Source = true } } };
            }, null, renderer: renderer)) {
                var opened = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "tab", OperationId = "open", OwnerScope = owner, Locator = "/projects/project/source/index.html" }, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(BrowserPageStatus.Starting, opened.Status, "metadata does not claim native readiness");
                var attached = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Attach, BrowserTabId = "tab", SessionId = opened.SessionId, OperationId = "open",
                    Bounds = new BrowserBoundsDto { Visible = true, X = 0, Y = 20, Width = 100, Height = 100, ViewportWidth = 300, ViewportHeight = 300 } }, CancellationToken.None);
                var clipped = new BrowserBoundsDto { Visible = true, Y = 20, Width = 100, Height = 100, ViewportWidth = 300, ViewportHeight = 300,
                    Cutouts = new[] { new BrowserCutoutDto { X = double.NaN, Y = 20, Width = 70, Height = 35 } } };
                AssertTrue(!LocalBrowserPageRuntime.ValidBounds(clipped), "nonfinite popup geometry is refused");
                clipped.Cutouts[0].X = 40;
                AssertTrue(!LocalBrowserPageRuntime.ValidBounds(clipped), "cutouts must be inside the visible pane");
                clipped.Cutouts[0].X = 15;
                AssertTrue(LocalBrowserPageRuntime.ValidBounds(clipped) && !clipped.Unobstructed, "valid cutout keeps the page visible without certifying an unobstructed viewport");
                var layout = new BrowserCommandPayload { Action = BrowserCommandKind.Layout, BrowserTabId = "tab", SessionId = opened.SessionId, Bounds = clipped };
                var firstLayout = runtime.ExecuteAsync(layout, CancellationToken.None);
                var sameLayout = runtime.ExecuteAsync(layout, CancellationToken.None);
                var closed = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Close, BrowserTabId = "tab", SessionId = opened.SessionId }, CancellationToken.None);
                AssertTrue(!closed.IsCompleted, "commands serialize across native initialization");
                renderer.Ready.TrySetResult(true);
                var ready = attached.GetAwaiter().GetResult();
                AssertEqual(BrowserPageStatus.Ready, ready.Status, "attached native renderer promotes readiness");
                var cut = firstLayout.GetAwaiter().GetResult();
                AssertEqual(ready.ViewportRevision + 1, cut.ViewportRevision, "a popup invalidates current placement evidence");
                AssertEqual(cut.ViewportRevision, sameLayout.GetAwaiter().GetResult().ViewportRevision, "identical cutouts do not invalidate again");
                AssertEqual(BrowserPageStatus.Closed, closed.GetAwaiter().GetResult().Status, "close follows native attachment without an orphan");
                renderer.Notify(opened);
                RuntimeThrows<InvalidOperationException>(() => runtime.CaptureAnnotationAsync(new BrowserAnnotationRequest {
                    BrowserTabId = "tab", SessionId = opened.SessionId, Generation = opened.DocumentGeneration }, CancellationToken.None).GetAwaiter().GetResult());
                RuntimeThrows<InvalidOperationException>(() => runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Reload, BrowserTabId = "tab", SessionId = opened.SessionId }, CancellationToken.None).GetAwaiter().GetResult());
            }
            AssertTrue(renderer.Disposed, "runtime disposes its sole native adapter");
        }
        private static void BrowserInlineExecutorLifecycle()
        {
            var renderer = new BrowserRendererFixture(); renderer.Ready.SetResult(true);
            BrowserPageStateDto latest = null;
            var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" };
            using (var runtime = new LocalBrowserPageRuntime(request => { throw new InvalidOperationException("Inline HTML must not resolve a stored source."); },
                value => latest = value, renderer: renderer, inlineLifetime: TimeSpan.FromMilliseconds(250)))
            {
                var opened = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.OpenInline,
                    BrowserTabId = "inline", OperationId = "inline-open", OwnerScope = owner,
                    Html = "<!doctype html><button id=run>Run</button><script>document.getElementById('run').onclick=()=>document.body.dataset.ran='yes'</" + "script>",
                    Title = "Chat HTML" }, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(BrowserPageStatus.Starting, opened.Status, "inline execution waits for its native renderer");
                AssertEqual(BrowserTargetKind.InlineHtml, opened.Target.Kind, "inline execution has a transient target kind");
                AssertTrue(opened.Target.Capabilities.Preview && opened.Target.Capabilities.Interact && !opened.Target.Capabilities.Source,
                    "inline execution is interactive but grants no source authority");
                var attached = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Attach,
                    BrowserTabId = opened.BrowserTabId, SessionId = opened.SessionId, OperationId = opened.OperationId,
                    Bounds = VisibleBrowserBounds() }, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(BrowserPageStatus.Ready, attached.Status, "native renderer accepts the isolated inline page");
                AssertTrue(renderer.LastTransientContent != null && renderer.LastTransientContent.Target.Kind == BrowserTargetKind.InlineHtml,
                    "runtime passes one transient content binding to the native renderer");
                string bootstrap;
                using (var reader = new StreamReader(renderer.LastTransientContent.Open("index.html", CancellationToken.None)))
                    bootstrap = reader.ReadToEnd();
                AssertContains(bootstrap, "Content-Security-Policy", "inline executor document carries the fixed CSP");
                var encoded = Regex.Match(bootstrap, "atob\\('([^']+)'\\)").Groups[1].Value;
                var source = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                AssertContains(source, "id=run", "isolated executor receives the exact accepted HTML");
                AssertContains(source, "getElementById('run')", "user scripts stay in the native executor, not the chat iframe");
                RuntimeThrows<InvalidOperationException>(() => runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Reload,
                    BrowserTabId = opened.BrowserTabId, SessionId = opened.SessionId }, CancellationToken.None).GetAwaiter().GetResult());
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while ((latest == null || latest.Status != BrowserPageStatus.Closed) && DateTime.UtcNow < deadline)
                    Thread.Sleep(25);
                AssertEqual(BrowserPageStatus.Closed, latest?.Status, "inline executor is closed at its exact lifetime deadline");
                AssertContains(latest?.Message, "Скрипты остановлены", "deadline reports that the executor stopped");
            }
            using (var sourceOnly = new LocalBrowserPageRuntime(request => new BrowserResolveResult(), null))
                RuntimeThrows<InvalidOperationException>(() => sourceOnly.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.OpenInline,
                    BrowserTabId = "source-only", OperationId = "source-only-open", OwnerScope = owner,
                    Html = "<p>Not executable</p>", Title = "Source only" }, CancellationToken.None).GetAwaiter().GetResult());
        }
        private static void BrowserNativeHistoryReconciliation()
        {
            var renderer = new BrowserRendererFixture(); renderer.Ready.SetResult(true);
            var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "project", ChatId = "chat" };
            BrowserPageStateDto latest = null;
            const string root = "/projects/project/source/index.html";
            using (var runtime = new LocalBrowserPageRuntime(request => new BrowserResolveResult { Locator = request.Locator,
                Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                    OwnerScope = owner, DisplayLocator = request.Locator, Capabilities = new BrowserTargetCapabilities() } }, value => latest = value, renderer: renderer)) {
                var state = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "history", OperationId = "open",
                    OwnerScope = owner, Locator = root }, CancellationToken.None).GetAwaiter().GetResult();
                Action<BrowserCommandKind> command = action => runtime.ExecuteAsync(new BrowserCommandPayload { Action = action,
                    BrowserTabId = "history", SessionId = state.SessionId, OperationId = "open" }, CancellationToken.None).GetAwaiter().GetResult();
                command(BrowserCommandKind.Attach);
                long sequence = 0;
                Action<int, BrowserNativeHistoryEntry[]> changed = (current, entries) => {
                    var value = latest; value.DocumentGeneration++; value.Status = BrowserPageStatus.Ready;
                    value.Url = entries.Single(entry => entry.Id == current).Locator;
                    value.NativeHistory = new BrowserNativeHistory { ControllerId = "native", Revision = ++sequence, CurrentEntryId = current, Entries = entries };
                    renderer.Notify(value);
                };
                var first = new BrowserNativeHistoryEntry { Id = 10, Locator = root };
                var second = new BrowserNativeHistoryEntry { Id = 20, Locator = root + "#two" };
                var third = new BrowserNativeHistoryEntry { Id = 30, Locator = root + "#three" };
                changed(10, new[] { first });
                var initialVisit = latest.VisitRevision;
                changed(30, new[] { first, second, third });
                AssertTrue(latest.VisitRevision > initialVisit && latest.CanGoBack, "native visits reach the common accepted-visit revision");
                var generation = latest.DocumentGeneration; command(BrowserCommandKind.Back);
                AssertEqual((int?)20, renderer.LastCommand.NativeHistoryEntryId, "Back uses the exact native entry");
                AssertEqual(generation, latest.DocumentGeneration, "runtime does not rebuild the same-document page before native acknowledgement");
                changed(20, new[] { first, second, third });
                second = new BrowserNativeHistoryEntry { Id = 20, Locator = root + "#replaced" };
                changed(20, new[] { first, second, third }); command(BrowserCommandKind.Forward);
                AssertEqual((int?)30, renderer.LastCommand.NativeHistoryEntryId, "replaceState retains the forward entry, not an appended duplicate");
                changed(40, new[] { first, second, new BrowserNativeHistoryEntry { Id = 40, Locator = root + "#new" } });
                AssertTrue(!latest.CanGoForward, "new native navigation after Back truncates forward history");
                var beforeFailure = latest.VisitRevision; latest.Status = BrowserPageStatus.Failed; renderer.Notify(latest);
                AssertEqual(beforeFailure, latest.VisitRevision, "failed native navigation never becomes a recent visit");
            }
        }
        private static void BrowserUserScreenshotCapture()
        {
            var renderer = new BrowserRendererFixture(); renderer.Ready.SetResult(true);
            var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "project" };
            using (var runtime = new LocalBrowserPageRuntime(request => new BrowserResolveResult { Locator = request.Locator,
                Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                    OwnerScope = request.OwnerScope, DisplayLocator = request.Locator, Capabilities = new BrowserTargetCapabilities { Source = true } } }, null, renderer: renderer)) {
                var opened = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "screen", OperationId = "screen-open",
                    OwnerScope = owner, BindChat = false, Locator = "/projects/project/source/index.html" }, CancellationToken.None).GetAwaiter().GetResult();
                var page = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Attach, BrowserTabId = opened.BrowserTabId,
                    SessionId = opened.SessionId, OperationId = opened.OperationId }, CancellationToken.None).GetAwaiter().GetResult();
                var request = new BrowserScreenshotRequest { BrowserTabId = page.BrowserTabId, SessionId = page.SessionId, Generation = page.DocumentGeneration };
                var pixels = new BrowserMediaCapture { Page = new BrowserPageHandle(null, page.SessionId, page.DocumentGeneration, browserTabId: page.BrowserTabId, ownerScope: owner),
                    Png = HarnessBrowserMediaSource.Pixels, PrivacyVerified = true };
                renderer.Screenshot = value => Task.FromResult(pixels);
                AssertTrue(!page.ChatBound, "user screenshot does not require an agent binding");
                AssertTrue(ReferenceEquals(pixels, runtime.CaptureScreenshotAsync(request, CancellationToken.None).GetAwaiter().GetResult()), "exact native capture returned");
                AssertTrue(runtime.TargetsFor("chat").Count == 0, "download grants no agent target");
                foreach (var stale in new[] {
                    new BrowserScreenshotRequest { BrowserTabId = page.BrowserTabId, SessionId = "foreign", Generation = request.Generation },
                    new BrowserScreenshotRequest { BrowserTabId = page.BrowserTabId, SessionId = request.SessionId, Generation = request.Generation - 1 } })
                    RuntimeThrows<InvalidOperationException>(() => runtime.CaptureScreenshotAsync(stale, CancellationToken.None).GetAwaiter().GetResult());
                var pending = new TaskCompletionSource<BrowserMediaCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
                renderer.Screenshot = value => pending.Task;
                var capture = runtime.CaptureScreenshotAsync(request, CancellationToken.None);
                runtime.Reset(); pending.SetResult(pixels);
                RuntimeThrows<InvalidOperationException>(() => capture.GetAwaiter().GetResult());
                AssertTrue(!runtime.IsCaptureCurrent(pixels), "reset revokes the download even before native cleanup");
                RuntimeThrows<InvalidOperationException>(() => runtime.CaptureScreenshotAsync(request, CancellationToken.None).GetAwaiter().GetResult());
            }
        }
        private sealed class BrowserRendererFixture : IBrowserRenderer, IBrowserScreenshotSource
        {
            internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed;
            internal BrowserCommandPayload LastCommand;
            internal BrowserContentSession LastTransientContent;
            internal Func<BrowserScreenshotRequest, Task<BrowserMediaCapture>> Screenshot;
            public event Action<BrowserPageStateDto> Changed;
            internal void Notify(BrowserPageStateDto state) { Changed?.Invoke(state); }
            public async Task<BrowserPageStateDto> UpdateAsync(BrowserCommandPayload command, BrowserPageStateDto state, CancellationToken token)
            { LastCommand = command; LastTransientContent = command.TransientContent;
                if (command.Action == BrowserCommandKind.Attach) { await Ready.Task; state.Status = BrowserPageStatus.Ready; state.Target.Capabilities.Preview = true; state.Target.Capabilities.Interact = true; } return state; }
            public Task ResetAsync() => Task.CompletedTask;
            public void Dispose() { Disposed = true; }
            public IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId) => new BrowserPageHandle[0];
            public Task<BrowserObservation> ReadAsync(BrowserPageHandle page, CancellationToken token) => throw new NotSupportedException();
            public Task PrepareAsync(BrowserPageHandle page, string revision, string id, BrowserActionRequest request, CancellationToken token) => throw new NotSupportedException();
            public Task<BrowserActionResult> ActAsync(BrowserPageHandle page, string id, Action dispatch, CancellationToken token, bool navigation = false) => throw new NotSupportedException();
            public void BlockActions(BrowserPageHandle page) { }
            public void PresentResult(BrowserPageHandle page, string id, BrowserActionEffect effect) { }
            public Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle page, CancellationToken token) => throw new NotSupportedException();
            public bool IsCaptureCurrent(BrowserMediaCapture capture) => Screenshot != null;
            public Task<BrowserMediaCapture> CaptureScreenshotAsync(BrowserScreenshotRequest request, CancellationToken token) => Screenshot(request);
        }
        private static void BrowserExactLocalContent()
        {
            var document = new BrowserTargetDescriptor { DisplayLocator = "/resources/document~1" };
            var fileList = BrowserSourceReader.Read(document, new BrowserSourceRequest { FilesOnly = true, RequestGeneration = 4 },
                new[] { "pages/start.html", "other.html" }, path => throw new InvalidOperationException("Listing opened the body."));
            AssertTrue(fileList.Files.SequenceEqual(new[] { "pages/start.html", "other.html" }) && fileList.Text == null && fileList.RequestGeneration == 4,
                "file navigation reads exact member metadata without the selected source body");
            using (var content = new BrowserContentSession(document, new[] { "pages/start.html", "other.html" },
                "pages/start.html", (path, token) => Stream.Null, () => { })) {
                var displayed = new BrowserPageStateDto { Target = new BrowserTargetDescriptor() };
                content.UpdateLocation(displayed, content.InitialUrl(document.DisplayLocator + "#section"));
                AssertEqual(document.DisplayLocator + "#section", displayed.Url, "native history retains the admitted default resource address");
                AssertEqual("pages/start.html", displayed.Target.RelativePath, "default address retains the exact entry member");
                AssertEqual(document.DisplayLocator + "/files/other.html", content.LocalLocator("other.html"), "other members stay distinct");
            }
            WithPluginPaths(paths => {
                var imported = new HtmlProjectImportService(paths).Import(HtmlProjectSourceFixture(), CancellationToken.None);
                var service = WorkspaceHtmlVersionsFixture(paths);
                var target = service.Resolve(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" },
                    Locator = BrowserLocalLocator.ForProject(imported.WorkspaceId, imported.EntryPath) }).Target;
                using (var content = service.OpenBrowserContent(target, CancellationToken.None)) {
                    var initial = content.InitialUrl(target.DisplayLocator + "?mode=edit#field");
                    AssertTrue(initial.EndsWith("?mode=edit#field", StringComparison.Ordinal), "native entry preserves query and fragment");
                    var assembly = content.AssemblyWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult();
                    AssertTrue(assembly != null && assembly.Files.Any(file => file.Kind == "html"),
                        "Workspace project Browser content reaches the common vendor assembler");
                    var displayed = new BrowserPageStateDto { Target = target };
                    content.UpdateLocation(displayed, initial);
                    AssertEqual(target.DisplayLocator + "?mode=edit#field", displayed.Url, "display locator and current native member agree");
                    string member;
                    AssertTrue(content.TryMember(content.Url(content.EntryPath) + "?query=one#section", out member) && member == content.EntryPath,
                        "local navigation resolves an exact declared member with query/fragment");
                    foreach (var url in new[] { "https://example.com/index.html", "http://127.0.0.1/index.html", content.Origin + "/%2e%2e/index.html",
                        content.Origin + "/%252e%252e/index.html", content.Origin + "/.rna/workspace.json", content.Origin + "//" + content.EntryPath })
                        AssertTrue(!content.TryMember(url, out member), "foreign, traversal or undeclared resource is denied: " + url);
                    string before;
                    using (var read = new StreamReader(content.Open(content.EntryPath, CancellationToken.None))) before = read.ReadToEnd();
                    File.WriteAllText(Path.Combine(imported.RootPath, imported.EntryPath), "changed source");
                    using (var read = new StreamReader(content.Open(content.EntryPath, CancellationToken.None))) AssertEqual(before, read.ReadToEnd(), "one live page retains exact captured bytes until explicit reload");
                    RuntimeThrows<InvalidOperationException>(() => content.Open("../secret", CancellationToken.None));
                    using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); RuntimeThrows<OperationCanceledException>(() => content.Open(content.EntryPath, cancelled.Token)); }
                }
                using (var plugins = new PluginApplicationService(paths)) {
                    var plugin = PluginFixture(plugins, paths, false);
                    var request = new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" }, Locator = BrowserLocalLocator.ForPlugin(plugin.PluginId, "plugin.json") };
                    var content = plugins.OpenBrowserContent(plugins.ResolveBrowserLocator(request).Target, CancellationToken.None);
                    AssertTrue(content.Plugin != null && content.EntryPath.EndsWith(".html", StringComparison.Ordinal), "plugin metadata opens its captured HTML entry and existing SDK instance");
                    var instance = content.Plugin; content.Dispose(); content.Drained.GetAwaiter().GetResult();
                    AssertTrue(instance.Token.IsCancellationRequested, "closing content revokes its exact plugin instance");
                }
                BrowserLocalLocator memberLocator;
                AssertTrue(BrowserLocalLocator.TryParse(BrowserLocalLocator.ForResource("html_ws_one", "saved", "pages/two.html"), out memberLocator) &&
                    memberLocator.RelativePath == "pages/two.html" && memberLocator.VersionId == "saved", "document member navigation preserves saved identity");
                BrowserNetworkPolicy.RequireSupportedRuntime("157.0.8103.51");
                RuntimeThrows<InvalidOperationException>(() => BrowserNetworkPolicy.RequireSupportedRuntime("151.0.0.0"));
            });
        }

        private static void BrowserWorkspaceProjectAssembly()
        {
            var target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                WorkspaceId = "workspace", DisplayLocator = "/projects/workspace/source/index.html" };
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["index.html"] = "<main id=\"chart\"></main>",
                ["app.js"] = "echarts.init(document.getElementById('chart'));",
                ["site.css"] = "#chart{height:240px}",
                ["logo.png"] = null
            };
            using (var content = new BrowserContentSession(target, files.Keys, "index.html",
                (path, token) => new MemoryStream(files[path] == null ? new byte[] { 0x89, 0x50, 0x4e, 0x47 } : new UTF8Encoding(false).GetBytes(files[path]), false),
                () => { }))
            {
                var assembly = content.AssemblyWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(assembly != null && assembly.DataSources.Count == 0,
                    "Workspace project gets a transient assembly view without invented bindings");
                AssertEqual("index.html", assembly.ActiveFileId, "Workspace entry remains exact");
                AssertEqual("html,js,css", string.Join(",", assembly.Files.Select(file => file.Kind)), "only text assembly members are loaded");
                AssertContains(assembly.Files.Single(file => file.Path == "app.js").Content, "echarts.init",
                    "vendor activation source reaches the common assembler");
            }
            var raw = new BrowserTargetDescriptor { Kind = BrowserTargetKind.HtmlResource, State = BrowserTargetState.Ready };
            using (var rawContent = new BrowserContentSession(raw, new[] { "index.html" }, "index.html",
                (path, token) => new MemoryStream(Encoding.UTF8.GetBytes("<main>raw</main>"), false), () => { }))
                AssertTrue(rawContent.AssemblyWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult() == null,
                    "source-only raw HTML never enters workspace assembly");
            var plugin = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginSource, State = BrowserTargetState.Ready };
            using (var pluginContent = new BrowserContentSession(plugin, new[] { "index.html" }, "index.html",
                (path, token) => new MemoryStream(Encoding.UTF8.GetBytes("<main>plugin</main>"), false), () => { }))
                AssertTrue(pluginContent.AssemblyWorkspaceAsync(CancellationToken.None).GetAwaiter().GetResult() == null,
                    "plugin pages keep their package/SDK route instead of the workspace vendor pool");
        }

    }
}
