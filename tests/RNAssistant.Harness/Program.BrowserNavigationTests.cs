using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
                    AssertTrue(!page.Capabilities.Preview && page.Capabilities.PreviewReason == BrowserRefusalCode.IsolationUnqualified,
                        "catalog does not invent native qualification");
                }
            });
        }
    }
}
