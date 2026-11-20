using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static int DiagnosticLogProcessProbe(string root, string prefix)
        {
            try
            {
                var store = new DiagnosticLogStore(root);
                for (var index = 0; index < 60; index++) store.Append("rnassistant.log", new[] { prefix + ":" + index + " Пример 😀" });
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void DiagnosticLogsIndependentProcessWriters()
        {
            WithTempPaths(paths =>
            {
                var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
                Func<string, Process> start = prefix =>
                {
                    var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                    info.ArgumentList.Add(typeof(Program).Assembly.Location);
                    info.ArgumentList.Add("--diagnostic-log-probe"); info.ArgumentList.Add(paths.Root); info.ArgumentList.Add(prefix);
                    return Process.Start(info);
                };
                using (var first = start("first"))
                using (var second = start("second"))
                {
                    foreach (var process in new[] { first, second })
                    {
                        if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Diagnostic log child timed out."); }
                        AssertEqual(0, process.ExitCode, "independent writer exits successfully: " + process.StandardError.ReadToEnd());
                    }
                }
                var lines = new DiagnosticLogStore(paths.Root).Read("rnassistant.log").Text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                AssertEqual(120, lines.Length, "both processes retain all complete records");
                AssertEqual(120, lines.Distinct().Count(), "process writes do not overwrite or duplicate each other");
                AssertTrue(lines.All(line => line.EndsWith(" Пример 😀", StringComparison.Ordinal)), "concurrent UTF-8 appends are not interleaved");
            });
        }

        private static void DiagnosticLogsBridgeUsesFixedScope()
        {
            WithTempPaths(paths =>
            {
                var settings = new SettingsService(paths);
                settings.Save(new AppSettings { Model = "keep-this-model" });
                using (var bridge = new AssistantWebBridge(new AssistantController(paths), null))
                {
                    var token = BridgeToken(bridge);
                    Func<string, object, string, JObject> send = (type, body, auth) => JObject.Parse(
                        bridge.HandleMessageAsync(JsonConvert.SerializeObject(new {
                            contractVersion = BridgeOperationCatalog.Version, id = Guid.NewGuid().ToString("N"),
                            type = type, bridgeToken = auth, payload = body
                        })).GetAwaiter().GetResult());
                    var directory = Path.Combine(paths.Root, "logs");
                    Directory.CreateDirectory(directory);
                    var name = DateTime.UtcNow.ToString("yyyyMMdd") + ".log";
                    var file = Path.Combine(directory, name);
                    const string content = "2026-11-16 [ERROR] Пример 😀 <img src=x onerror=alert(1)>\n  stack trace\n";
                    File.WriteAllText(file, content, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(directory, "unrelated.log"), "not an owned diagnostic source");
                    File.WriteAllText(Path.Combine(directory, "history.jsonl"), "canonical sentinel");

                    var inventory = send("listDiagnosticLogs", new { }, token);
                    AssertTrue((bool)inventory["ok"], "file inventory is available without a selected chat: " + inventory.ToString(Formatting.None));
                    AssertTrue(inventory.SelectToken("payload.files") is JArray, "typed camelCase inventory");
                    var denied = send("clearDiagnosticLog", new { fileId = name }, "wrong-token");
                    AssertTrue(!(bool)denied["ok"], "log mutations keep the shell authentication gate");
                    AssertEqual(content, File.ReadAllText(file), "denied mutation preserves bytes");

                    var read = send("readDiagnosticLog", new DiagnosticLogReadRequest { FileId = name }, token);
                    AssertTrue((bool)read["ok"], "typed bounded read is registered");
                    AssertEqual(content, (string)read.SelectToken("payload.text"), "UTF-8 and raw text cross the bridge unchanged");
                    AssertTrue(!string.IsNullOrEmpty((string)read.SelectToken("payload.snapshotToken")), "reader owns a snapshot");
                    var traversal = send("deleteDiagnosticLog", new { fileId = "../settings.json" }, token);
                    AssertTrue(!(bool)traversal["ok"], "UI cannot turn a log id into a filesystem path");
                    var missing = send("clearDiagnosticLog", new { }, token);
                    AssertTrue(!(bool)missing["ok"], "missing file id cannot mean clear all");

                    var options = send("saveDiagnosticLogOptions", new DiagnosticLogOptionsDto {
                        MaxFileMiB = 2, MaxTotalMiB = 8, RetentionDays = 3, MaxFiles = 5
                    }, token);
                    AssertTrue((bool)options["ok"], "policy changes use the log owner");
                    AssertEqual(8, (int)options.SelectToken("payload.inventory.options.maxTotalMiB"), "accepted policy returned");
                    AssertEqual("keep-this-model", settings.Load().Model, "log controls preserve unrelated settings");

                    var cleared = send("clearDiagnosticLog", new DiagnosticLogFileRequest { FileId = name }, token);
                    AssertTrue((bool)cleared["ok"], "clear selected is registered");
                    AssertEqual(0L, new FileInfo(file).Length, "clear keeps an empty file");
                    var deleted = send("deleteDiagnosticLog", new DiagnosticLogFileRequest { FileId = name }, token);
                    AssertTrue((bool)deleted["ok"] && !File.Exists(file), "delete removes the selected file");
                    var all = send("clearDiagnosticLogs", new { }, token);
                    AssertTrue((bool)all["ok"], "clear all has an explicit separate operation");
                    AssertEqual("not an owned diagnostic source", File.ReadAllText(Path.Combine(directory, "unrelated.log")),
                        "unregistered log files are outside destructive scope");
                    AssertEqual("canonical sentinel", File.ReadAllText(Path.Combine(directory, "history.jsonl")),
                        "append-only journals are outside log maintenance");
                    AssertEqual("keep-this-model", settings.Load().Model, "clear all does not reset application data");
                }
            });
        }
        private static void ArtifactViewerBridgeCancellation()
        {
            WithTempPaths(paths => {
                var controller = new AssistantController(paths);
                using (var bridge = new AssistantWebBridge(controller, null))
                using (var started = new ManualResetEventSlim())
                {
                    var token = BridgeToken(bridge);
                    controller.ArtifactReadWork = cancellation => {
                        started.Set();
                        if (!cancellation.WaitHandle.WaitOne(5000)) throw new TimeoutException("Viewer cancellation did not reach the worker.");
                        cancellation.ThrowIfCancellationRequested();
                    };
                    var read = bridge.HandleMessageAsync(JsonConvert.SerializeObject(new {
                        contractVersion = BridgeOperationCatalog.Version, id = "held-viewer", type = "readArtifactViewerPage", bridgeToken = token,
                        payload = new { chatId = "chat", resourceUri = "rna://chat/doc/artifact/fixture/revision/1" }
                    }));
                    AssertTrue(started.Wait(3000) && !read.IsCompleted, "source read returned control to the WebView");
                    var cancel = JObject.Parse(bridge.HandleMessageAsync(JsonConvert.SerializeObject(new {
                        contractVersion = BridgeOperationCatalog.Version, id = "close-viewer", type = "cancelRequest", bridgeToken = token,
                        payload = new { requestId = "held-viewer" }
                    })).GetAwaiter().GetResult());
                    AssertTrue((bool)cancel["payload"]["cancelled"], "bridge services an independent request while the viewer is held");
                    var response = JObject.Parse(read.GetAwaiter().GetResult());
                    AssertTrue(!(bool)response["ok"] && response["payload"] == null, "closing a reader returns no stale viewer payload");
                }
            });
        }

        private static void DiagnosticMaintenanceBridgeDoesNotBlockCaller()
        {
            WithTempPaths(paths =>
            {
                var controller = new AssistantController(paths);
                using (var bridge = new AssistantWebBridge(controller, null))
                {
                    var token = BridgeToken(bridge);
                    foreach (var operation in new[] { "getCasHealth", "collectCasGarbage", "clearRuntimeData" })
                    using (var entered = new ManualResetEventSlim())
                    using (var release = new ManualResetEventSlim())
                    {
                        Action hold = () =>
                        {
                            entered.Set();
                            if (!release.Wait(5000)) throw new TimeoutException("Maintenance fixture was not released.");
                        };
                        controller.CasHealthOverride = () => {
                            hold(); return new CasHealthResponse { Healthy = true, ReachabilityComplete = true, CanGarbageCollect = true };
                        };
                        controller.CasCollectionOverride = () => {
                            hold(); return new CasGarbageCollectionResponse { Completed = true,
                                Health = new CasHealthResponse { Healthy = true, ReachabilityComplete = true, CanGarbageCollect = true } };
                        };
                        controller.RuntimeClearOverride = () => { hold(); return controller.Initialize(); };
                        var request = JsonConvert.SerializeObject(new {
                            contractVersion = BridgeOperationCatalog.Version, id = Guid.NewGuid().ToString("N"),
                            type = operation, bridgeToken = token, payload = new { }
                        });
                        AssertTrue(!BridgeOperationCatalog.Resolve(operation).Cancellable,
                            operation + " does not advertise unsupported post-dispatch cancellation");
                        var elapsed = Stopwatch.StartNew();
                        var pending = bridge.HandleMessageAsync(request);
                        elapsed.Stop();
                        try
                        {
                            AssertTrue(elapsed.ElapsedMilliseconds < 1000,
                                operation + " returns an asynchronous task instead of blocking the WebView caller");
                            AssertTrue(entered.Wait(3000), operation + " executes on a worker");
                            AssertTrue(!pending.IsCompleted, operation + " remains in flight while maintenance is held");
                        }
                        finally { release.Set(); }
                        var response = JObject.Parse(pending.GetAwaiter().GetResult());
                        AssertTrue((bool)response["ok"], operation + " publishes its result after the worker finishes");
                    }
                }
            });
        }
    }
}
