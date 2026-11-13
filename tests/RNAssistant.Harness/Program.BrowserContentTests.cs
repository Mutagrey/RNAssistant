using System;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
                BrowserNetworkPolicy.RequireSupportedRuntime("157.0.8103.51");
                RuntimeThrows<InvalidOperationException>(() => BrowserNetworkPolicy.RequireSupportedRuntime("151.0.0.0"));
            });
        }
    }
}
