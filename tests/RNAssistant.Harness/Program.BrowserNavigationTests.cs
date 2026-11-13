using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void BrowserManualScopeHandoff()
        {
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths)) {
                    var plugin = PluginFixture(service, paths, false);
                    var project = new HtmlProjectImportService(paths).Import(HtmlProjectSourceFixture(), default(System.Threading.CancellationToken));
                    var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = plugin.SourceWorkspaceId,
                        ChatId = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = plugin.SourceWorkspaceId }).SessionId };
                    var next = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = project.WorkspaceId,
                        ChatId = service.CreateProjectChat(new ProjectChatRequest { WorkspaceId = project.WorkspaceId }).SessionId };
                    var original = BrowserLocalLocator.ForPlugin(plugin.PluginId, "index.html");
                    var destination = BrowserLocalLocator.ForProject(project.WorkspaceId, project.EntryPath);
                    using (var runtime = new LocalBrowserPageRuntime(service.ResolveBrowserLocator, null)) {
                        var page = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "scope", OperationId = "open",
                            OwnerScope = owner, Locator = original }, default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                        AssertTrue(page.ChatBound, "an explicit chat open starts bound");
                        Func<BrowserCommandPayload, BrowserPageStateDto> execute = request => {
                            request.BrowserTabId = "scope"; request.SessionId = page.SessionId;
                            return runtime.ExecuteAsync(request, default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                        };
                        RuntimeThrows<InvalidOperationException>(() => execute(new BrowserCommandPayload { Action = BrowserCommandKind.Navigate, OwnerScope = owner, Locator = destination }));
                        page = execute(new BrowserCommandPayload { Action = BrowserCommandKind.NavigateUser, Locator = destination });
                        AssertTrue(!page.ChatBound && page.OwnerScope.ScopeId == next.ScopeId && page.OwnerScope.ChatId == null, "manual handoff opens the allowed source without transferring the old agent");
                        RuntimeThrows<InvalidOperationException>(() => execute(new BrowserCommandPayload { Action = BrowserCommandKind.Navigate, OwnerScope = owner, Locator = original }));
                        RuntimeThrows<InvalidOperationException>(() => execute(new BrowserCommandPayload { Action = BrowserCommandKind.Bind, Generation = page.DocumentGeneration - 1, Binding = next }));
                        RuntimeThrows<InvalidOperationException>(() => execute(new BrowserCommandPayload { Action = BrowserCommandKind.Bind, Generation = page.DocumentGeneration,
                            Binding = new BrowserOwnerScope { Kind = next.Kind, ScopeId = next.ScopeId, ChatId = owner.ChatId } }));
                        page = execute(new BrowserCommandPayload { Action = BrowserCommandKind.Bind, Generation = page.DocumentGeneration, Binding = next });
                        AssertTrue(page.ChatBound && page.OwnerScope.ChatId == next.ChatId, "only explicit exact-project Bind grants the new chat");
                        page = execute(new BrowserCommandPayload { Action = BrowserCommandKind.Back });
                        AssertTrue(!page.ChatBound && page.OwnerScope.ChatId == owner.ChatId, "Back retains source access but cannot revive its former agent binding");
                        var restored = runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "restore", OperationId = "restore",
                            OwnerScope = page.OwnerScope, Locator = page.Url, BindChat = false }, default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                        AssertTrue(!restored.ChatBound, "layout restoration preserves explicit unbound state");
                    }
                }
            });
        }
        private static async System.Threading.Tasks.Task BrowserLocalSessionLifecycle()
        {
            var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "workspace", ChatId = "chat" };
            var saved = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginRelease, OwnerScope = owner, State = BrowserTargetState.Ready,
                DisplayLocator = BrowserLocalLocator.ForPlugin("plugin", "index.html", "entry-one"), SavedVersionId = "entry-one", ReleaseId = "same-package",
                Capabilities = new BrowserTargetCapabilities { Source = true, Preview = true, Interact = true } };
            var other = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginRelease, OwnerScope = owner, State = BrowserTargetState.Ready,
                DisplayLocator = BrowserLocalLocator.ForPlugin("plugin", "index.html", "entry-two"), SavedVersionId = "entry-two", ReleaseId = "same-package",
                Capabilities = new BrowserTargetCapabilities { Source = true } };
            using (var runtime = new LocalBrowserPageRuntime(request => BrowserTargetResolver.ResolveLocalLocator(request.OwnerScope, request.Locator, new[] { saved, other }), null))
            {
                var open = new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "tab", OperationId = "op", OwnerScope = owner, Locator = "/" };
                var home = await runtime.ExecuteAsync(open, default(System.Threading.CancellationToken));
                var repeat = await runtime.ExecuteAsync(open, default(System.Threading.CancellationToken));
                AssertEqual(home.SessionId, repeat.SessionId, "duplicate open reuses exact page session");
                AssertTrue(!home.Attached && home.Status == BrowserPageStatus.Home, "open alone is not Dock attach or renderer readiness");
                Func<BrowserCommandKind, string, System.Threading.Tasks.Task<BrowserPageStateDto>> command = (action, locator) => runtime.ExecuteAsync(
                    new BrowserCommandPayload { BrowserTabId = "tab", SessionId = home.SessionId, OperationId = "op", Action = action, Locator = locator }, default(System.Threading.CancellationToken));
                AssertTrue((await command(BrowserCommandKind.Attach, null)).Attached, "exact attach acknowledgement");
                var first = await command(BrowserCommandKind.Navigate, saved.DisplayLocator);
                AssertEqual(BrowserPageStatus.Unavailable, first.Status, "unqualified execution fails closed before renderer creation");
                AssertEqual(BrowserRefusalCode.NativeUnavailable, first.Reason, "host without native renderer reports unavailable");
                AssertTrue(!first.Target.Capabilities.Preview && runtime.TargetsFor("chat").Count == 0, "source-only page cannot grant live actions");
                await command(BrowserCommandKind.Navigate, other.DisplayLocator);
                saved.State = BrowserTargetState.Removed;
                var removed = await command(BrowserCommandKind.Back, null);
                AssertEqual(BrowserPageStatus.Removed, removed.Status, "Back revalidates the exact entry");
                AssertEqual("entry-one", removed.Target.SavedVersionId, "same bytes are never substituted");
                AssertEqual("entry-two", (await command(BrowserCommandKind.Forward, null)).Target.SavedVersionId, "peer entry is unaffected");
                var peer = await runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Open, BrowserTabId = "peer", OperationId = "peer-op", OwnerScope = owner, Locator = "/" }, default(System.Threading.CancellationToken));
                await command(BrowserCommandKind.Close, null);
                AssertEqual(BrowserPageStatus.Closed, (await runtime.ExecuteAsync(open, default(System.Threading.CancellationToken))).Status, "late duplicate cannot resurrect closed page");
                AssertEqual(BrowserPageStatus.Home, (await runtime.ExecuteAsync(new BrowserCommandPayload { Action = BrowserCommandKind.Attach, BrowserTabId = "peer", SessionId = peer.SessionId, OperationId = "peer-op" }, default(System.Threading.CancellationToken))).Status, "close affects only its page");
            }
            var source = new BrowserSourceRequest { Count = 2, RequestGeneration = 9 };
            var page = BrowserSourceReader.Read(saved, source, new[] { "index.html" }, path => "12345");
            AssertEqual("12", page.Text, "bounded source delivery");
            source.Offset = 2; source.ExpectedRevision = page.Revision;
            RuntimeThrows<InvalidOperationException>(() => BrowserSourceReader.Read(saved, source, new[] { "index.html" }, path => "changed"));
            source.Path = "../secret";
            RuntimeThrows<InvalidOperationException>(() => BrowserSourceReader.Read(saved, source, new[] { "index.html" }, path => "12345"));
        }

        private static void BrowserLocalAddressesAndScope()
        {
            BrowserLocalLocator locator;
            foreach (var value in new[] { "https://example.com", "http://localhost", "//host/a", "file:///C:/a", "rna://x/a", "/plugins/x/source/../secret",
                "/projects/x/source/%2e%2e/secret", "/projects/x/source/%252e%252e/secret", "/projects/x/source/a%2fb.html",
                "/plugins/x/source/%5csecret", "/plugins/x/versions/v1", "/unknown/x", "/resources/x/versions/", "/resources/x\n" })
                AssertTrue(!BrowserLocalLocator.TryParse(value, out locator), "network/traversal/ambiguous grammar rejected");
            var address = BrowserLocalLocator.ForPlugin("plug", "versions/Моя страница.html", "v1");
            AssertTrue(BrowserLocalLocator.TryParse(address + "?view=one#details", out locator), "encoded local filename and route accepted");
            AssertEqual("versions/Моя страница.html", locator.RelativePath, "reserved filename segment is unambiguous");
            AssertEqual(address, locator.RecentLocator, "recent metadata excludes arbitrary query/hash payload");
            var owner = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "project", ChatId = "chat" };
            var saved = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginRelease, OwnerScope = owner,
                State = BrowserTargetState.Removed, DisplayLocator = address, SavedVersionId = "v1", ReleaseId = "package", Title = "Old page" };
            var latest = new BrowserTargetDescriptor { Kind = BrowserTargetKind.PluginRelease, OwnerScope = owner,
                State = BrowserTargetState.Ready, DisplayLocator = BrowserLocalLocator.ForPlugin("plug", "versions/Моя страница.html", "v3"),
                SavedVersionId = "v3", ReleaseId = "package", Title = "Current page" };
            var resolved = BrowserTargetResolver.ResolveLocalLocator(owner, address + "?view=one#details", new[] { saved, latest });
            AssertEqual(BrowserRefusalCode.Removed, resolved.Reason, "deleted entry never resolves to same-bytes latest");
            AssertEqual("v1", resolved.Target.SavedVersionId, "catalog identity retained");
            AssertTrue(resolved.Locator.EndsWith("?view=one#details"), "live navigation retains route");
            var other = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "project", ChatId = "other" };
            AssertEqual(BrowserRefusalCode.TargetUnavailable, BrowserTargetResolver.ResolveLocalLocator(other, address, new[] { saved }).Reason,
                "another chat cannot inherit the page binding");
            AssertEqual(BrowserRefusalCode.AmbiguousTarget, BrowserTargetResolver.ResolveLocalLocator(owner, address, new[] { saved, saved }).Reason,
                "ambiguous catalog does not choose an arbitrary target");
            var generation = 17L;
            var suggestions = BrowserTargetResolver.QueryNavigationSuggestions(owner, new BrowserSuggestionRequest { Prefix = "page", Limit = 1,
                RequestGeneration = generation }, new[] { saved, latest });
            AssertEqual(1, suggestions.Items.Count, "bounded completion");
            AssertEqual(generation, suggestions.RequestGeneration, "stale generation can be discarded by caller");
            AssertTrue(!JsonConvert.SerializeObject(suggestions).Contains("package"), "completion carries no package/body authority");
            var handle = new BrowserPageHandle("chat", "session", 1, "Page", true, "tab", owner, "bytes", "v1");
            AssertTrue(!handle.Matches(new BrowserPageHandle("chat", "session", 1, "Page", true, "tab", owner, "bytes", "v3")),
                "identical bytes never merge saved-entry action bindings");
            AssertTrue(!handle.Matches(new BrowserPageHandle("chat", "session", 1, "Page", true, "other-tab", owner, "bytes", "v1")),
                "tab identity stays exact");
            WithPluginPaths(paths => {
                using (var service = new PluginApplicationService(paths))
                {
                    var plugin = PluginFixture(service, paths);
                    var before = service.Check(plugin.PluginId).CandidateId;
                    var page = service.BrowserTargets(new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" }).Single();
                    AssertEqual(BrowserTargetKind.PluginSource, page.Kind, "Home catalog exposes registered local source");
                    AssertEqual(before, service.Check(plugin.PluginId).CandidateId, "metadata query neither captures nor changes actor intent");
                    AssertTrue(!page.Capabilities.Preview && page.Capabilities.PreviewReason == BrowserRefusalCode.NativeUnavailable,
                        "metadata catalog does not instantiate a native renderer");
                }
            });
        }
    }
}
