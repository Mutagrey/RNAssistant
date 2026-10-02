using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlAssetsCatalogImportAndPublish()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var packageDirectory = Path.Combine(FixturePaths.Value.Root,
                    "assets", "local-helper", "1.0.0");
                Directory.CreateDirectory(packageDirectory);
                File.WriteAllText(Path.Combine(packageDirectory, "index.html"),
                    "<main id=\"app\">Local</main>", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(packageDirectory, "helper.js"),
                    "window.LocalHelper = { ready: true };", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(packageDirectory, "manifest.json"),
                    "{\"schemaVersion\":1,\"id\":\"local-helper\",\"version\":\"1.0.0\",\"kind\":\"html-workspace\",\"title\":\"Local helper\",\"description\":\"Offline reusable page helper\",\"offline\":true,\"files\":[\"index.html\",\"helper.js\"]}",
                    new UTF8Encoding(false));

                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools,
                    new AppSettings(), "agent", false);
                var listed = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ListAssetsToolId, new JObject());
                AssertEqual(ToolExecutionOutcome.Ok, listed.Outcome,
                    "local package catalog can be read");
                AssertEqual(ToolEffectEvidence.None, listed.Evidence.Effect,
                    "catalog read has no effect");
                var first = (JObject)JObject.Parse(listed.Result.DataJson)["items"][0];
                AssertEqual("local-helper", (string)first["id"],
                    "manual package is discovered");
                AssertEqual(2, (int)first["fileCount"],
                    "catalog returns bounded file metadata");
                AssertTrue(JObject.Parse(listed.Result.DataJson)["directory"] == null,
                    "model catalog does not expose the app-data root");
                AssertTrue(first["revision"] == null,
                    "runtime content revision is not delegated to the model");
                AssertEqual(0, session.HtmlWorkspace.Files.Count,
                    "catalog discovery does not import or execute files");

                File.WriteAllText(Path.Combine(packageDirectory, "helper.js"),
                    "window.LocalHelper = { ready: false };", new UTF8Encoding(false));
                var args = new JObject { ["id"] = "local-helper",
                    ["version"] = "1.0.0" };
                var stale = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ImportAssetToolId, args);
                AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                    "changed package refuses stale revision");
                AssertEqual(0, session.HtmlWorkspace.Files.Count,
                    "stale import has no workspace effect");

                listed = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ListAssetsToolId, new JObject());
                var imported = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ImportAssetToolId, args);
                AssertEqual(ToolExecutionOutcome.Ok, imported.Outcome,
                    "selected exact package imports");
                AssertEqual(ToolEffectEvidence.VerifiedChange, imported.Evidence.Effect,
                    "import creates a verified workspace revision");
                AssertEqual(2, session.HtmlWorkspace.Files.Count,
                    "all package files copied together");
                AssertEqual("window.LocalHelper = { ready: false };",
                    session.HtmlWorkspace.Files.Single(file => file.Path == "helper.js").Content,
                    "import retains exact local source");
                var head = session.ActiveHtmlArtifactId;
                var duplicate = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ImportAssetToolId, args);
                AssertEqual(ToolExecutionOutcome.Error, duplicate.Outcome,
                    "import never overwrites a workspace file");
                AssertEqual(head, session.ActiveHtmlArtifactId,
                    "conflict creates no revision");

                var published = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.PublishAssetToolId,
                    new JObject { ["id"] = "saved-helper", ["version"] = "1.0.0",
                        ["title"] = "Saved helper", ["description"] = "Reusable local helper",
                        ["files"] = new JArray("helper.js") });
                AssertEqual(ToolExecutionOutcome.Ok, published.Outcome,
                    "agent can publish selected existing source");
                AssertTrue(JObject.Parse(published.Result.DataJson)["revision"] == null,
                    "publish does not expose runtime revision as a model argument");
                AssertTrue(File.Exists(Path.Combine(FixturePaths.Value.Root,
                    "assets", "saved-helper", "1.0.0", "manifest.json")),
                    "publish writes a manifest in the user asset library");
                var secondSession = NewSession(adapter);
                var secondRuntime = executor.CreateNativeRuntime(secondSession, tools,
                    new AppSettings(), "agent", false);
                var notListed = ExecuteHtmlNative(secondRuntime,
                    HtmlWorkspaceToolCatalog.ImportAssetToolId,
                    new JObject { ["id"] = "saved-helper", ["version"] = "1.0.0" });
                AssertEqual(ToolExecutionOutcome.Error, notListed.Outcome,
                    "a second chat must discover the asset first");
                var secondList = ExecuteHtmlNative(secondRuntime,
                    HtmlWorkspaceToolCatalog.ListAssetsToolId,
                    new JObject { ["query"] = "saved-helper" });
                AssertEqual(ToolExecutionOutcome.Ok, secondList.Outcome,
                    "second chat can discover published package");
                var reused = ExecuteHtmlNative(secondRuntime,
                    HtmlWorkspaceToolCatalog.ImportAssetToolId,
                    new JObject { ["id"] = "saved-helper",
                        ["version"] = "1.0.0" });
                AssertEqual(ToolExecutionOutcome.Ok, reused.Outcome,
                    "published source can be reused in another workspace");
                AssertEqual("window.LocalHelper = { ready: false };",
                    secondSession.HtmlWorkspace.Files.Single().Content,
                    "reused source is exact");
                var republish = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.PublishAssetToolId,
                    new JObject { ["id"] = "saved-helper", ["version"] = "1.0.0",
                        ["title"] = "Saved helper", ["description"] = "Reusable local helper",
                        ["files"] = new JArray("helper.js") });
                AssertEqual(ToolExecutionOutcome.Error, republish.Outcome,
                    "publishing never replaces an existing package version");

                var unsafeDirectory = Path.Combine(FixturePaths.Value.Root,
                    "assets", "unsafe-package", "1.0.0");
                Directory.CreateDirectory(unsafeDirectory);
                File.WriteAllText(Path.Combine(unsafeDirectory, "manifest.json"),
                    "{\"schemaVersion\":1,\"id\":\"unsafe-package\",\"version\":\"1.0.0\",\"kind\":\"html-workspace\",\"title\":\"Unsafe\",\"description\":\"Bad path\",\"offline\":true,\"files\":[\"../outside.js\"]}");
                var afterUnsafe = ExecuteHtmlNative(runtime,
                    HtmlWorkspaceToolCatalog.ListAssetsToolId, new JObject());
                AssertEqual(1, (int)JObject.Parse(afterUnsafe.Result.DataJson)["skipped"],
                    "unsafe manifest is excluded without hiding valid packages");
            });
        }
    }
}
