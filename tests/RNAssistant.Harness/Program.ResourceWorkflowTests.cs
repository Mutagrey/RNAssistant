using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlWorkspaceIdenticalWritesRetainSnapshot()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                foreach (var path in new[] { "index.html", "styles.css", "app.js" })
                {
                    var source = path == "index.html" ? "<main>Exact</main>" : "/* unchanged */\r\n";
                    var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false, manualRun: true);
                    var arguments = new Newtonsoft.Json.Linq.JObject { ["path"] = path, ["content"] = source };
                    AssertEqual(RNAssistant.Core.Tools.ToolExecutionOutcome.Ok,
                        ExecuteHtmlNative(runtime, HtmlWorkspaceToolCatalog.WriteFileToolId, arguments).Outcome, "initial file write");
                    var snapshot = session.ActiveHtmlArtifactId;
                    var stamp = session.HtmlWorkspace.Files.Single(file => file.Path == path).UpdatedUtc;
                    var count = session.Artifacts.Count;
                    var same = ExecuteHtmlNative(runtime, HtmlWorkspaceToolCatalog.WriteFileToolId, arguments);
                    AssertEqual(RNAssistant.Core.Tools.ToolEffectEvidence.VerifiedNoChange, same.Evidence.Effect, "identical write is not a content change");
                    AssertEqual(snapshot, session.ActiveHtmlArtifactId, "identical write retains snapshot");
                    AssertEqual(count, session.Artifacts.Count, "identical write does not grow history");
                    AssertEqual(stamp, session.HtmlWorkspace.Files.Single(file => file.Path == path).UpdatedUtc, "content timestamp stays exact");
                    new ChatStore(FixturePaths.Value).Save(session);
                }
            });
        }

        // Opt-in real model diagnostic. Uses the production Office conversation and
        // resource owners with a fake document; never qualifies COM or WebView2.
        private static int ResourceWorkflowLive(string evidenceDirectory)
        {
            var endpoint = Environment.GetEnvironmentVariable("RNA_BASE_URL");
            var model = Environment.GetEnvironmentVariable("RNA_MODEL");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("RNA_BASE_URL and RNA_MODEL are required.");
            var evidence = Path.GetFullPath(evidenceDirectory);
            if (Directory.Exists(evidence)) throw new InvalidOperationException("Use a new evidence directory.");
            Directory.CreateDirectory(evidence);
            var settings = new AppSettings {
                BaseUrl = endpoint, Model = model,
                AgentResponseMode = Environment.GetEnvironmentVariable("RNA_RESPONSE_MODE") ?? AgentResponseModes.JsonSchema,
                SystemPromptRole = Environment.GetEnvironmentVariable("RNA_INSTRUCTION_ROLE") ?? "developer",
                ReasoningRequestMode = Environment.GetEnvironmentVariable("RNA_REASONING_MODE") ?? ReasoningRequestModes.ChatTemplateKwargs,
                ContextWindowOverrideTokens = int.Parse(Environment.GetEnvironmentVariable("RNA_CONTEXT_TOKENS") ?? "32768"),
                MaxTokens = 4096, Temperature = 0,
                AutoConfirmToolActions = true, MaxAgentIterations = 24, MaxAgentToolSteps = 32,
                MaxAgentFormatRetries = 2, RequestTimeoutSeconds = 120, StreamResponses = false,
                AutoCalibrateTokenEstimate = false
            };
            File.WriteAllText(Path.Combine(evidence, "profile.json"), JsonConvert.SerializeObject(settings, Formatting.Indented));
            var passed = false;
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), settings, (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var original = new[] {
                    new[] { "index.html", "<!doctype html><html><head></head><body><h1>Old report</h1><button id=\"counter\">0</button></body></html>" },
                    new[] { "styles.css", "body { color: #112233; background: #ffffff; }\nh1 { font-size: 24px; }\n" },
                    new[] { "app.js", "let count = 0;\ndocument.getElementById('counter').onclick = function () { count += 1; this.textContent = count; };\n" }
                };
                foreach (var file in original)
                    AssertTrue(executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.WriteFileToolId,
                        "path", file[0], "content", file[1]), tools, settings, false, false, session).Success, "seed HTML");
                const string vba = "Option Explicit\r\nPublic Function ReportTitle() As String\r\n    ReportTitle = \"Old report\"\r\nEnd Function\r\n";
                adapter.SetVbaModule("ReportModule", vba, "StdModule");
                var task = "В существующем HTML-проекте измени заголовок Old report на Sales report, цвет текста #112233 на #225588, " +
                    "а кнопку-счётчик — чтобы каждый клик прибавлял 2 вместо 1. В существующем VBA ReportModule измени возвращаемую строку на Sales report. " +
                    "Сохрани остальные части исходников. Выполни изменения и проверь конечные исходники всех четырёх файлов. Кратко сообщи результат.";
                var requireTaskList = Environment.GetEnvironmentVariable("RNA_REQUIRE_TASK_LIST") == "true";
                if (requireTaskList) task = "Сначала подключи skill common.task_tracking и создай Task List с этапами; веди его до завершения задачи. " + task;
                File.WriteAllText(Path.Combine(evidence, "task.txt"), task);
                File.WriteAllText(Path.Combine(evidence, "before.json"), JsonConvert.SerializeObject(new { html = original, vba }, Formatting.Indented));
                var client = new LlmClient(() => Environment.GetEnvironmentVariable("RNA_API_KEY"));
                var requests = 0;
                LlmCompletionDelegate complete = async (config, messages, options, stream, token) => {
                    var number = ++requests;
                    File.WriteAllText(Path.Combine(evidence, "request-" + number + ".json"), JsonConvert.SerializeObject(messages, Formatting.Indented));
                    Console.WriteLine("MODEL request " + number);
                    var response = await client.CompleteAsync(config, messages, options, stream, token).ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(evidence, "response-" + number + ".json"), JsonConvert.SerializeObject(response, Formatting.Indented));
                    Console.WriteLine(response.Content);
                    return response;
                };
                try
                {
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
                    {
                        var service = CreateConversationRunService(adapter, executor, complete, new ContextCompactionService(complete));
                        var result = service.ExecuteWithApprovalsAsync(ChatModes.Agent, task, session, NewContext(adapter), settings,
                            tools, (kind, text, activity) => { if (activity != null) Console.WriteLine(activity.ToolId + ": " + activity.ResultMessage); },
                            cancellationToken: deadline.Token).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
                    }
                    var expected = new[] { original[0][1].Replace("Old report", "Sales report"),
                        original[1][1].Replace("#112233", "#225588"), original[2][1].Replace("count += 1", "count += 2") };
                    var actual = session.HtmlWorkspace.Files;
                    passed = original.Select((file, index) => actual.Single(item => item.Path == file[0]).Content == expected[index]).All(value => value) &&
                        adapter.GetVbaModuleCode("ReportModule") == vba.Replace("Old report", "Sales report");
                    if (requireTaskList) passed &= session.LastRun.SelectedSkills.Any(skill => skill.Id == "common.task_tracking") &&
                        session.Artifacts.Any(artifact => artifact.Kind == ChatArtifactKinds.TaskList);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(evidence, "error.txt"), ex.ToString()); Console.WriteLine(ex.Message); }
                finally
                {
                    File.WriteAllText(Path.Combine(evidence, "session.json"), JsonConvert.SerializeObject(session, Formatting.Indented));
                    File.WriteAllText(Path.Combine(evidence, "after-vba.txt"), adapter.GetVbaModuleCode("ReportModule"));
                    foreach (var file in session.HtmlWorkspace.Files) File.WriteAllText(Path.Combine(evidence, "after-" + file.Path), file.Content);
                    foreach (var file in Directory.EnumerateFiles(FixturePaths.Value.Root, "*", SearchOption.AllDirectories))
                    {
                        var destination = Path.Combine(evidence, "store", file.Substring(FixturePaths.Value.Root.Length).TrimStart(Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination);
                    }
                    File.WriteAllText(Path.Combine(evidence, "acceptance.json"), JsonConvert.SerializeObject(new { passed, requests }));
                }
            });
            Console.WriteLine("Exact source acceptance: " + passed);
            return passed ? 0 : 1;
        }
    }
}
