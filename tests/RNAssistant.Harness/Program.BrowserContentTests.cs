using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
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
            using (var runtime = new LocalBrowserPageRuntime(request => new BrowserResolveResult { Locator = request.Locator,
                Target = new BrowserTargetDescriptor { Kind = BrowserTargetKind.WorkspaceHtmlProject, State = BrowserTargetState.Ready,
                    OwnerScope = request.OwnerScope, DisplayLocator = request.Locator, Capabilities = new BrowserTargetCapabilities { Source = true } } }, null, renderer: renderer)) {
                var opened = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "tab", OperationId = "open", OwnerScope = owner, Locator = "/projects/project/source/index.html" }, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(BrowserPageStatus.Starting, opened.Status, "metadata does not claim native readiness");
                var attached = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Attach, BrowserTabId = "tab", SessionId = opened.SessionId, OperationId = "open",
                    Bounds = new BrowserBoundsDto { Visible = true, X = 0, Y = 20, Width = 100, Height = 100, ViewportWidth = 300, ViewportHeight = 300 } }, CancellationToken.None);
                var closed = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Close, BrowserTabId = "tab", SessionId = opened.SessionId }, CancellationToken.None);
                AssertTrue(!closed.IsCompleted, "commands serialize across native initialization");
                renderer.Ready.TrySetResult(true);
                AssertEqual(BrowserPageStatus.Ready, attached.GetAwaiter().GetResult().Status, "attached native renderer promotes readiness");
                AssertEqual(BrowserPageStatus.Closed, closed.GetAwaiter().GetResult().Status, "close follows native attachment without an orphan");
                renderer.Notify(opened);
                RuntimeThrows<InvalidOperationException>(() => runtime.CaptureAnnotationAsync(new BrowserAnnotationRequest {
                    BrowserTabId = "tab", SessionId = opened.SessionId, Generation = opened.DocumentGeneration }, CancellationToken.None).GetAwaiter().GetResult());
                RuntimeThrows<InvalidOperationException>(() => runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Reload, BrowserTabId = "tab", SessionId = opened.SessionId }, CancellationToken.None).GetAwaiter().GetResult());
            }
            AssertTrue(renderer.Disposed, "runtime disposes its sole native adapter");
        }
        private sealed class BrowserRendererFixture : IBrowserRenderer
        {
            internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed;
            public event Action<BrowserPageStateDto> Changed;
            internal void Notify(BrowserPageStateDto state) { Changed?.Invoke(state); }
            public async Task<BrowserPageStateDto> UpdateAsync(BrowserCommandPayload command, BrowserPageStateDto state, CancellationToken token)
            { if (command.Action == BrowserCommandKind.Attach) { await Ready.Task; state.Status = BrowserPageStatus.Ready; state.Target.Capabilities.Preview = true; } return state; }
            public Task ResetAsync() => Task.CompletedTask;
            public void Dispose() { Disposed = true; }
            public IReadOnlyList<BrowserPageHandle> TargetsFor(string chatId) => new BrowserPageHandle[0];
            public Task<BrowserObservation> ReadAsync(BrowserPageHandle page, CancellationToken token) => throw new NotSupportedException();
            public Task PrepareAsync(BrowserPageHandle page, string revision, string id, BrowserActionRequest request, CancellationToken token) => throw new NotSupportedException();
            public Task<BrowserActionResult> ActAsync(BrowserPageHandle page, string id, Action dispatch, CancellationToken token, bool navigation = false) => throw new NotSupportedException();
            public void BlockActions(BrowserPageHandle page) { }
            public void PresentResult(BrowserPageHandle page, string id, BrowserActionEffect effect) { }
            public Task<BrowserMediaCapture> ReadMediaAsync(BrowserPageHandle page, CancellationToken token) => throw new NotSupportedException();
            public bool IsCaptureCurrent(BrowserMediaCapture capture) => false;
        }
        private static void BrowserExactLocalContent()
        {
            WithPluginPaths(paths => {
                var imported = new HtmlProjectImportService(paths).Import(HtmlProjectSourceFixture(), CancellationToken.None);
                var service = new WorkspaceHtmlVersionService(paths);
                var target = service.Resolve(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" },
                    Locator = BrowserLocalLocator.ForProject(imported.WorkspaceId, imported.EntryPath) }).Target;
                using (var content = service.OpenBrowserContent(target, CancellationToken.None)) {
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
    }
}
