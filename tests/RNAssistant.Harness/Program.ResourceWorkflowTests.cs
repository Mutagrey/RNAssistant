using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;
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

        // Opt-in diagnostics share the production conversation, budget and resource
        // owners with a fake document. Scripted mode makes no provider requests.
        private static int ResourceWorkflowDiagnostic(string evidenceDirectory, bool vbaOnly = false, bool scripted = false, bool excelReport = false)
        {
            var endpoint = Environment.GetEnvironmentVariable("RNA_BASE_URL");
            var model = Environment.GetEnvironmentVariable("RNA_MODEL");
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("RNA_BASE_URL and RNA_MODEL are required.");
            var evidence = Path.GetFullPath(evidenceDirectory);
            if (Directory.Exists(evidence)) throw new InvalidOperationException("Use a new evidence directory.");
            Directory.CreateDirectory(evidence);
            var requireReportSkill = excelReport && Environment.GetEnvironmentVariable("RNA_REQUIRE_REPORT_SKILL") == "true";
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
            File.WriteAllText(Path.Combine(evidence, "scenario.json"), JsonConvert.SerializeObject(new { vbaOnly, scripted, excelReport, requireReportSkill }));
            var passed = false;
            var taskPassed = false;
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), settings, (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = Guid.NewGuid().ToString("N"),
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var original = new[] {
                    new[] { "index.html", "<!doctype html><html><head></head><body><h1>Old report</h1><button id=\"counter\">0</button></body></html>" },
                    new[] { "styles.css", "body { color: #112233; background: #ffffff; }\nh1 { font-size: 24px; }\n" },
                    new[] { "app.js", "let count = 0;\ndocument.getElementById('counter').onclick = function () { count += 1; this.textContent = count; };\n" }
                };
                foreach (var file in vbaOnly || excelReport ? new string[0][] : original)
                    AssertTrue(executor.ExecuteManual(Command("core.write",
                        "target", "HTML file: " + file[0], "content", file[1]), tools, settings, false, false, session).Success, "seed HTML");
                const string vba = "Option Explicit\r\nPublic Function ReportTitle() As String\r\n    ReportTitle = \"Old report\"\r\nEnd Function\r\n";
                if (!excelReport) adapter.SetVbaModule("ReportModule", vba, "StdModule");
                var task = "В существующем HTML-проекте измени заголовок Old report на Sales report, цвет текста #112233 на #225588, " +
                    "а кнопку-счётчик — чтобы каждый клик прибавлял 2 вместо 1. В существующем VBA ReportModule измени возвращаемую строку на Sales report. " +
                    "Сохрани остальные части исходников. Выполни изменения и проверь конечные исходники всех четырёх файлов. Кратко сообщи результат.";
                if (vbaOnly) task = "В существующем VBA-модуле ReportModule замени возвращаемую строку Old report на Sales report. " +
                    "Сначала подключи только навык skills.vba.code.editing, затем прочитай модуль и выполни одну точечную правку. " +
                    "Сохрани остальной исходник, проверь конечный текст и кратко сообщи результат. Выполнять VBA не нужно.";
                var report = excelReport ? new ExcelReportWorkflowFixture(adapter) : null;
                if (report != null) task = ExcelReportWorkflowFixture.Task;
                if (requireReportSkill) task = "Сначала подключи навык skills.excel.analysis.reporting. " + task;
                var requireTaskList = !vbaOnly && !excelReport && Environment.GetEnvironmentVariable("RNA_REQUIRE_TASK_LIST") == "true";
                if (requireTaskList) task = "Сначала подключи skill skills.task.tracking и создай Task List с этапами; веди его до завершения задачи. " + task;
                File.WriteAllText(Path.Combine(evidence, "task.txt"), task);
                var reportSkillBody = string.Empty;
                var reportSkillOffered = false;
                if (report != null)
                {
                    File.WriteAllText(Path.Combine(evidence, "before.json"), report.Before.ToString());
                    var reportSkill = executor.CaptureSkills(executor.CaptureCatalogs()).Skills.Single(skill => skill.Id == "skills.excel.analysis.reporting");
                    reportSkillBody = reportSkill.BodyMarkdown;
                    File.WriteAllText(Path.Combine(evidence, "report-skill.json"), JsonConvert.SerializeObject(reportSkill, Formatting.Indented));
                }
                else File.WriteAllText(Path.Combine(evidence, "before.json"), JsonConvert.SerializeObject(new {
                    html = vbaOnly ? new string[0][] : original, vba }, Formatting.Indented));
                var client = new LlmClient(() => Environment.GetEnvironmentVariable("RNA_API_KEY"));
                Func<string, JObject, string> call = (name, arguments) =>
                    ModelProtocolWire.WriteAcceptedCall("Perform the next requested workflow step.", "",
                        new ConversationToolCall { Name = name, Arguments = arguments });
                var script = new Queue<string>(new[] {
                    LoadSkillResponse("skills.vba.code.editing"),
                    call(ResourceToolCatalog.ReadToolId, new JObject { ["target"] = "VBA module: ReportModule", ["representation"] = "source" }),
                    call("core.patch", PatchArguments("VBA module: ReportModule", EditSet(Edit("ReportTitle = \"Old report\"", "ReportTitle = \"Sales report\"")))),
                    null // Generate finish after terminal call IDs are assigned.
                });
                if (report != null) script = ExcelReportWorkflowFixture.Script();
                var requests = 0;
                LlmCompletionDelegate complete = async (config, messages, options, stream, token) => {
                    var number = ++requests;
                    if (report != null) reportSkillOffered |=
                        session.LastRun.SelectedSkills.Any(skill => skill.Id == "skills.excel.analysis.reporting") &&
                        messages.Any(message => message.Role == config.SystemPromptRole &&
                            (message.Content ?? string.Empty).Contains(reportSkillBody));
                    File.WriteAllText(Path.Combine(evidence, "request-" + number + ".json"), JsonConvert.SerializeObject(messages, Formatting.Indented));
                    File.WriteAllText(Path.Combine(evidence, "context-" + number + ".json"), JsonConvert.SerializeObject(options.TraceContextReceipt ?? session.LastContextReceipt, Formatting.Indented));
                    var helper = options.TracePurpose == "context_compaction";
                    File.WriteAllText(Path.Combine(evidence, "budget-" + number + ".json"), JsonConvert.SerializeObject(new {
                        purpose = options.TracePurpose,
                        input = ModelContextBudget.InputBudgetTokens(config),
                        options = ModelContextBudget.EstimateRequestOptionsTokens(options, config),
                        repair = helper ? 0 : ModelProtocolClient.EstimateFormatRepairOverheadTokens(config),
                        continuation = helper ? 0 : ModelContextBudget.ContinuationReserveTokens(config)
                    }));
                    Console.WriteLine("MODEL request " + number);
                    LlmCompletionResult response;
                    if (scripted)
                    {
                        AssertTrue(options.TracePurpose != "context_compaction", "scripted workflow must fit without compaction");
                        if (!excelReport && number == 2) AssertEqual(1, session.LastRun.SelectedSkills.Count,
                            "minimal VBA skill must fit the explicit profile; see retained load result");
                        AssertTrue(script.Count > 0, "scripted workflow must not need protocol repair or compaction");
                        var scriptedResponse = script.Dequeue();
                        if (scriptedResponse == null)
                            scriptedResponse = report == null
                                ? ScriptedVerifiedFinish(session, "The module patch completed with read-back.",
                                    "Исходник исправлен и проверен; выполнение VBA не проверялось.",
                                    "VBA module source was updated.", "core.patch")
                                : ScriptedVerifiedFinish(session, "The report steps and final read completed.",
                                    "Создан и проверен отчёт. Реальный Excel и внешний вид диаграммы не проверялись.",
                                    "The requested report was created and read back.",
                                    "core.read", "core.read", "excel.sheet.add",
                                    "excel.range.write", "excel.table.add", "excel.range.format", "excel.chart.upsert");
                        response = new LlmCompletionResult { Content = scriptedResponse };
                    }
                    else
                    {
                        var priorSink = options.TraceSink;
                        options.TraceSink = record => {
                            priorSink?.Invoke(record);
                            if (record.PayloadUtf8Bytes != null)
                                File.WriteAllBytes(Path.Combine(evidence, "http-" + number + "-" + record.Type + ".json"), record.PayloadUtf8Bytes);
                        };
                        try { response = await client.CompleteAsync(config, messages, options, stream, token).ConfigureAwait(false); }
                        finally { options.TraceSink = priorSink; }
                    }
                    File.WriteAllText(Path.Combine(evidence, "response-" + number + ".json"), JsonConvert.SerializeObject(response, Formatting.Indented));
                    Console.WriteLine(response.Content);
                    return response;
                };
                try
                {
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8)))
                    {
                        // Scripted replies are domain JSON; live replies already use the negotiated wire envelope.
                        // Do not send live responses through FixtureCompletion, which wraps scripted schema replies.
                        var store = new ChatStore(FixturePaths.Value);
                        var service = scripted
                            ? CreateConversationRunService(adapter, executor, complete, new ContextCompactionService(complete))
                            : new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store),
                                complete, new ContextCompactionService(complete));
                        var result = service.ExecuteWithApprovalsAsync(ChatModes.Agent, task, session, NewContext(adapter), settings,
                            tools, (kind, text, activity) => { if (activity != null) Console.WriteLine(activity.ToolId + ": " + activity.ResultMessage); },
                            cancellationToken: deadline.Token).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
                        passed = result.ResponseStatus == AgentResponseStatuses.Completed;
                    }
                    var expected = new[] { original[0][1].Replace("Old report", "Sales report"),
                        original[1][1].Replace("#112233", "#225588"), original[2][1].Replace("count += 1", "count += 2") };
                    var actual = session.HtmlWorkspace.Files;
                    if (report != null) passed &= report.Checks().Values.All(value => value) &&
                        session.LastRun.KernelState.Summary.ToolCounts.WriteUnknown == 0 &&
                        session.LastRun.KernelState.Summary.ToolCounts.WriteOk >= 5;
                    else passed &= (vbaOnly || original.Select((file, index) => actual.Single(item => item.Path == file[0]).Content == expected[index]).All(value => value)) &&
                        adapter.GetVbaModuleCode("ReportModule") == vba.Replace("Old report", "Sales report");
                    if (vbaOnly) passed &= adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule) == 1 &&
                        session.LastRun.SelectedSkills.Count == 1 && session.LastRun.SelectedSkills[0].Id == "skills.vba.code.editing";
                    if (requireTaskList) passed &= session.LastRun.SelectedSkills.Any(skill => skill.Id == "skills.task.tracking") &&
                        session.Artifacts.Any(artifact => artifact.Kind == ChatArtifactKinds.TaskList);
                    taskPassed = passed;
                    if (requireReportSkill) passed &= reportSkillOffered;
                }
                catch (Exception ex) { taskPassed = passed = false; File.WriteAllText(Path.Combine(evidence, "error.txt"), ex.ToString()); Console.WriteLine(ex.Message); }
                finally
                {
                    File.WriteAllText(Path.Combine(evidence, "session.json"), JsonConvert.SerializeObject(session, Formatting.Indented));
                    if (report != null)
                    {
                        File.WriteAllText(Path.Combine(evidence, "after-workbook.json"), adapter.ExcelWorkbookStateForTest().ToString());
                        File.WriteAllText(Path.Combine(evidence, "report-checks.json"), JsonConvert.SerializeObject(report.Checks(), Formatting.Indented));
                    }
                    else File.WriteAllText(Path.Combine(evidence, "after-vba.txt"), adapter.GetVbaModuleCode("ReportModule"));
                    foreach (var file in session.HtmlWorkspace.Files) File.WriteAllText(Path.Combine(evidence, "after-" + file.Path), file.Content);
                    foreach (var file in Directory.EnumerateFiles(FixturePaths.Value.Root, "*", SearchOption.AllDirectories))
                    {
                        var destination = Path.Combine(evidence, "store", file.Substring(FixturePaths.Value.Root.Length).TrimStart(Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination);
                    }
                    File.WriteAllText(Path.Combine(evidence, "acceptance.json"), JsonConvert.SerializeObject(new {
                        passed, taskPassed, requests, scripted, excelReport, requireReportSkill, reportSkillOffered,
                        vbaWrites = adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule) }));
                }
            });
            Console.WriteLine("Workflow acceptance: " + passed);
            return passed ? 0 : 1;
        }
    }
}
