using System;
using RNAssistant.Application.Conversations;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TabularBundlePinsOnlyShippedModules()
        {
            WithTempPaths(paths =>
            {
                var shipped = Path.Combine(FindHarnessRepositoryRoot(), "web");
                var captured = Path.Combine(paths.Root, "package");
                foreach (var file in TabularRuntimeBundle.ApprovedPaths.Concat(new[] { "tabular/runtime-lock.json" }))
                {
                    var target = Path.Combine(captured, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(shipped, file), target);
                }
                var bundle = TabularRuntimeBundle.Capture(captured);
                AssertEqual(TabularRuntimeBundle.Capture(shipped).Sha256, bundle.Sha256, "package pin is reproducible");
                var worker = File.ReadAllText(Path.Combine(captured, "data-templates/core/worker.js"));
                File.WriteAllText(Path.Combine(captured, "data-templates/core/worker.js"), "throw Error('changed');");
                using (var input = bundle.OpenAsset("data-templates/core/worker.js"))
                using (var reader = new StreamReader(input))
                {
                    AssertTrue(!input.CanWrite, "module bytes are read-only");
                    AssertEqual(worker, reader.ReadToEnd(), "captured bytes survive installation drift");
                }
                ExpectSourceQueueError("tabular_bundle_invalid", () => TabularRuntimeBundle.Capture(captured));
                File.WriteAllText(Path.Combine(captured, "data-templates/core/worker.js"), worker);
                var manifestPath = Path.Combine(captured, "tabular/runtime-lock.json");
                var manifest = File.ReadAllText(manifestPath);
                var replacement = Encoding.UTF8.GetBytes("throw Error('self-repinned module');");
                File.WriteAllBytes(Path.Combine(captured, "data-templates/core/worker.js"), replacement);
                var repinned = JObject.Parse(manifest);
                var workerPin = repinned["files"].Single(file => (string)file["path"] == "data-templates/core/worker.js");
                workerPin["bytes"] = replacement.Length;
                using (var sha = SHA256.Create()) workerPin["sha256"] = HexSourceHash(sha.ComputeHash(replacement));
                File.WriteAllText(manifestPath, repinned.ToString(Newtonsoft.Json.Formatting.Indented).Replace("\r\n", "\n") + "\n");
                ExpectSourceQueueError("tabular_bundle_invalid", () => TabularRuntimeBundle.Capture(captured));
                File.WriteAllText(Path.Combine(captured, "data-templates/core/worker.js"), worker);
                foreach (var invalid in new[] {
                    manifest.Replace("\"workerProtocol\": 5", "\"workerProtocol\": 4"),
                    manifest.Replace("\"formatVersion\": 1", "\"formatVersion\": 1,\n  \"formatVersion\": 1"),
                    manifest.Replace("\"data-templates/vendor/sax.js\"", "\"../injected.js\""),
                    manifest.TrimEnd(), manifest + "{}" })
                {
                    File.WriteAllText(manifestPath, invalid);
                    ExpectSourceQueueError("tabular_bundle_invalid", () => TabularRuntimeBundle.Capture(captured));
                }
                AssertTrue(bundle.OpenAsset("../data-templates/core/worker.js") == null && bundle.OpenAsset("index.html") == null &&
                    bundle.OpenAsset("data-templates/core/examples/matrix-source.xlsx") == null, "no aliases, main UI, fixtures or project scripts");
            });
        }

        private static void TabularResourceSessionKeepsExactScope()
        {
            WithTempPaths(paths =>
            {
                var bundle = TabularRuntimeBundle.Capture(Path.Combine(FindHarnessRepositoryRoot(), "web"));
                using (var app = new WorkspaceApplicationService(paths))
                {
                    var queue = PrepareNativeTabularFixture(app, paths);
                    var session = WorkspaceTabularResourceSession.OpenAsync(app, queue.QueueId, queue.Items[0].Id, bundle).GetAwaiter().GetResult();
                    var prefix = session.EntryUri.Substring(0, session.EntryUri.Length - TabularRuntimeBundle.EntryPath.Length);
                    try
                    {
                        using (var response = session.HandleAsync("GET", session.EntryUri).GetAwaiter().GetResult())
                        {
                            AssertEqual(200, response.StatusCode, "pinned entry");
                            AssertTrue(response.Headers["Content-Security-Policy"].Contains("worker-src " + prefix + "data-templates/core/worker.js;"), "only the pinned worker URL");
                            AssertEqual("no-store", response.Headers["Cache-Control"], "capability responses never enter cache");
                            AssertTrue(!response.Headers.ContainsKey("Access-Control-Allow-Origin"), "no cross-origin authority");
                        }
                        using (var response = session.HandleAsync("GET", prefix + "data-templates/core/worker.js").GetAwaiter().GetResult())
                        {
                            AssertTrue(response.Headers["Content-Security-Policy"].Contains("connect-src 'none'"), "Worker cannot fetch native bytes or network directly");
                            AssertTrue(response.Headers["Content-Security-Policy"].Contains("worker-src 'none'"), "Worker cannot spawn children");
                        }
                        using (var response = session.HandleAsync("GET", prefix + "descriptor.json").GetAwaiter().GetResult())
                        using (var reader = new StreamReader(response.Body))
                        {
                            var descriptor = JObject.Parse(reader.ReadToEnd());
                            AssertEqual(6, descriptor.Properties().Count(), "closed metadata descriptor");
                            AssertEqual(bundle.Sha256, (string)descriptor["bundleSha256"], "exact module pin in runtime metadata");
                            AssertEqual("book.xlsx", (string)descriptor["label"], "no workspace authority locator");
                        }
                        foreach (var route in new[] { "data-templates/core/../worker.js", "data-templates/core/%77orker.js", "data-templates/core/worker.js?x=1", "data-templates/core/worker.js#x", "../index.html", "project.json" })
                            AssertNativeStatus(session, 404, "GET", prefix + route);
                        AssertNativeStatus(session, 404, "GET", "https://foreign.invalid/" + session.EntryUri);
                        AssertNativeStatus(session, 404, "GET", session.EntryUri, "null");
                        AssertNativeStatus(session, 404, "GET", session.EntryUri, "https://foreign.invalid");
                        AssertNativeStatus(session, 405, "POST", prefix + "source");
                        foreach (var query in new[] { "sequence=01&offset=0&count=1", "sequence=1&offset=-1&count=1",
                            "sequence=1&offset=0&count=262145", "sequence=1&offset=9223372036854775808&count=1",
                            "sequence=1&offset=0&count=1&count=2", "offset=0&count=1&sequence=1" })
                            AssertNativeStatus(session, 400, "GET", prefix + "source?" + query);
                        using (var response = session.HandleAsync("GET", prefix + "source?sequence=1&offset=0&count=4").GetAwaiter().GetResult())
                        {
                            AssertEqual(200, response.StatusCode, "exact range");
                            AssertEqual("1", response.Headers["X-RNA-Source-Sequence"], "receipt sequence");
                            AssertEqual("0", response.Headers["X-RNA-Source-Offset"], "receipt offset");
                            AssertEqual("4", response.Headers["Content-Length"], "binary bound");
                            AssertEqual((int)'P', response.Body.ReadByte(), "retained XLSX survives live file replacement");
                            AssertEqual((int)'K', response.Body.ReadByte(), "ZIP header");
                        }
                        AssertNativeStatus(session, 409, "GET", prefix + "source?sequence=1&offset=0&count=1");
                        ExpectSourceQueueError("source_queue_active", app.Reset);
                        AssertTrue(ConversationLeaseService.ForOffice(paths).AnyHeld(), "resource scope holds maintenance activity");
                        AssertNativeStatus(session, 204, "DELETE", prefix + "source");
                        AssertTrue(session.ClientCloseConfirmed && !session.WasRevoked && session.SourceDrained.IsCompleted,
                            "ordinary browser cleanup is distinct from native revocation");
                        AssertNativeStatus(session, 410, "GET", prefix + "source?sequence=2&offset=0&count=1");
                        AssertNativeStatus(session, 410, "GET", session.EntryUri);
                        AssertNativeStatus(session, 204, "DELETE", prefix + "source");
                        app.Reset();
                        AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "native close confirms I/O drain");
                    }
                    finally { session.CloseAsync().GetAwaiter().GetResult(); }
                }
            });
        }

        private static void TabularResourceSessionHasNativeDeadline()
        {
            WithTempPaths(paths =>
            {
                using (var app = new WorkspaceApplicationService(paths))
                {
                    var queue = PrepareNativeTabularFixture(app, paths);
                    var bundle = TabularRuntimeBundle.Capture(Path.Combine(FindHarnessRepositoryRoot(), "web"));
                    var session = WorkspaceTabularResourceSession.OpenAsync(app, queue.QueueId, queue.Items[0].Id, bundle,
                        deadlineMs: 1000).GetAwaiter().GetResult();
                    try
                    {
                        AssertTrue(SpinWait.SpinUntil(() => session.IsClosed, 5000), "native deadline revokes abandoned page access");
                        AssertTrue(session.WasRevoked && !session.ClientCloseConfirmed, "native executor observes source revocation independently of JavaScript");
                        session.SourceDrained.GetAwaiter().GetResult();
                        AssertNativeStatus(session, 410, "GET", session.EntryUri);
                        var prefix = session.EntryUri.Substring(0, session.EntryUri.Length - TabularRuntimeBundle.EntryPath.Length);
                        AssertNativeStatus(session, 410, "DELETE", prefix + "source");
                    }
                    finally { session.CloseAsync().GetAwaiter().GetResult(); }
                    app.EnsureNoSourceWork();
                    AssertTrue(!ConversationLeaseService.ForOffice(paths).AnyHeld(), "abandoned realm cannot retain the source forever");
                }
            });
        }

        private static WorkspaceSourceQueuePage PrepareNativeTabularFixture(WorkspaceApplicationService app, AppDataPaths paths)
        {
            var state = CreateSourceQueueProject(app, paths);
            var sourcePath = Path.Combine(state.Workspace.RootPath, "book.xlsx");
            File.Copy(Path.Combine(FindHarnessRepositoryRoot(), "web/data-templates/core/examples/matrix-source.xlsx"), sourcePath);
            var queue = app.PrepareSources(SourceQueueRequest(state, new System.Collections.Generic.List<string> { "book.xlsx" }));
            queue = WaitSourceQueue(app, queue.QueueId, page => page.State == WorkspaceSourceQueueState.Finished);
            AssertEqual(1, queue.Prepared, "source prepared");
            File.WriteAllText(sourcePath, "changed live source");
            return queue;
        }
        private static void AssertNativeStatus(WorkspaceTabularResourceSession session, int expected, string method, string url, string origin = null)
        { using (var response = session.HandleAsync(method, url, origin).GetAwaiter().GetResult()) AssertEqual(expected, response.StatusCode, "native resource refusal " + method); }
    }
}
