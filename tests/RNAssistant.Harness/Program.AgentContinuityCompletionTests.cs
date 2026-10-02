using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Storage;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AgentContinuityReadPresentation()
        {
            WithTempPaths(paths => {
                var payloads = new ChatBlobStore(paths);
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "read-presentation", "Book", "Read");
                foreach (var size in new[] { 3563, 10000, 40000 })
                {
                    var data = JsonConvert.SerializeObject(new { kind = "resource-read", representation = "source",
                        text = new string('x', size), returnedCharacters = size, complete = true });
                    var activity = AgentTranscript.CreateToolActivity(new ToolInvocation {
                        ToolId = "common.resources_read", ToolCallId = "read" + size }, ToolRunResult.Ok("Read", data), "tool");
                    activity.RunId = "run";
                    session.Messages.Add(new ChatMessage { Role = "assistant", RunId = "run", ExcludeFromModelContext = true, Activity = activity });
                }
                store.Save(session);
                var loaded = store.Load(session.Id);
                foreach (var message in loaded.Messages.Where(m => m.Activity != null))
                {
                    var activity = message.Activity;
                    var size = activity.ReadSummary.ReturnedCharacters.Value;
                    AssertEqual("source", activity.ReadSummary.Representation, "summary survives event replay");
                    AssertTrue(size < 8192 || activity.DataJson == null && activity.ResultPayload != null, "large body is externalized");
                    var preview = new ToolResultPresentationService(null, payloads).Read(loaded, "run", activity.ToolCallId);
                    var block = preview.Blocks.OfType<ToolTextBlockDto>().Single();
                    AssertTrue(block.Text.StartsWith(new string('x', Math.Min(size, 12000))), "exact retained body supplies bounded preview");
                    if (activity.ResultPayload == null) continue;
                    activity.ResultPayload = new PayloadRef(new string('e', 64), 20000, "application/json");
                    var missing = new ToolResultPresentationService(null, payloads).Read(loaded, "run", activity.ToolCallId);
                    AssertContains(missing.Blocks.OfType<ToolTextBlockDto>().Single().Text, "недоступно", "missing CAS is explicit");
                    AssertEqual(size, activity.ReadSummary.ReturnedCharacters.Value, "missing body does not rewrite the read outcome");
                }
            });
        }

        private static void AgentContinuityTaskContext()
        {
            WithTempPaths(paths => {
                var chats = new ChatStore(paths);
                var session = chats.Create("Excel", "task-context", "Book", "Tasks");
                var service = new TaskListService();
                AssertTrue(service.Set(session, "Fix CSS and JS", new List<ChatTaskStep> {
                    new ChatTaskStep { Text = "Fix styles" }, new ChatTaskStep { Text = "Fix script" }
                }, () => {}).Success, "plan can precede source discovery");
                var original = session.Artifacts.Last().InlineText;
                AssertTrue(service.Set(session, "Restore slider", new List<ChatTaskStep> {
                    new ChatTaskStep { Text = "Fix styles", Status = "completed", Note = "CSS applied." },
                    new ChatTaskStep { Text = "Inspect script", Status = "completed", Note = "Reviewed JS; no edit needed." },
                    new ChatTaskStep { Text = "Verify dragging", Status = "pending" }
                }, () => {}, "Inspection changed the proposed implementation.").Success, "model can revise plan and explain no-op");
                var marker = new ChatMessage { Role = "user", Content = "Continue" };
                session.Messages.Add(marker);
                var checkpoint = new ContextCheckpoint { ThroughMessageId = marker.Id };
                session.ContextCheckpoints.Add(checkpoint); session.ActiveContextCheckpointId = checkpoint.Id;
                chats.Save(session);
                session = chats.Load(session.Id);
                var runtime = JObject.Parse(ConversationPromptComposer.BuildRuntimeContext("agent", null, new ToolCatalogEntry[0], new SkillDefinition[0], null, session));
                var task = runtime["active_task_list"];
                AssertEqual("Restore slider", (string)task["goal"], "latest revised goal survives compaction and persistence");
                AssertEqual("agent_assessment", (string)task["statusSource"], "assessment is distinct from authoritative tool receipts");
                AssertEqual("Reviewed JS; no edit needed.", (string)task["steps"][1]["note"], "rationale survives independently of history");
                AssertEqual("pending", (string)task["steps"][2]["status"], "remaining verification is pinned");
                AssertEqual(original, session.Artifacts.First().InlineText, "replanning retains previous plan history");
                AssertTrue(service.CloseActive(session, "blocked", null, () => {}, "Need browser access").Success, "blocker can be saved outside a live run");
                chats.Save(session); session = chats.Load(session.Id);
                AssertEqual("Need browser access", TaskListService.Active(session).Blocker, "blocker survives reload");
                AssertTrue(service.UpdateStatuses(session, null, new List<TaskListStatusUpdate> {
                    new TaskListStatusUpdate { Index = 3, Status = "in_progress", Note = "Browser access is available." }
                }, () => {}).Success, "agent can resume without a run-bound blocker handshake");
                AssertEqual("active", TaskListService.Active(session).Status, "progress resumes plan");
                AssertTrue(TaskListService.Active(session).Blocker == null, "resolved blocker does not remain current");
                AssertEqual("Inspection changed the proposed implementation.", TaskListService.Active(session).Reason,
                    "resuming preserves plan rationale without keeping the resolved blocker as its reason");
            });
        }

        private static void AgentContinuityBlockedFinal()
        {
            foreach (var blocked in new[] { false, true })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                Func<JObject, string> taskCall = args => new JObject { ["message"] = "Сохраняю состояние задачи.", ["final"] = false,
                    ["tool_calls"] = new JArray(new JObject { ["name"] = TaskListToolCatalog.SetToolId, ["arguments"] = args }) }.ToString();
                var responses = new Queue<string>(new[] {
                    LoadToolSchemaResponse(TaskListToolCatalog.SetToolId),
                    taskCall(new JObject { ["action"] = "save", ["goal"] = "Two deliverables", ["steps"] = new JArray(
                        new JObject { ["text"] = "First result" }, new JObject { ["text"] = "Second result" }) }) });
                if (blocked) responses.Enqueue(taskCall(new JObject { ["action"] = "close", ["outcome"] = "blocked", ["reason"] = "Need the missing input" }));
                const string final = "{\"message\":\"Нужны исходные данные; результаты ещё не готовы.\",\"final\":true,\"tool_calls\":[]}";
                responses.Enqueue(final); responses.Enqueue(final);
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var completed = service.ExecuteAsync(ChatModes.Agent, "Подготовь два результата.", session, NewContext(adapter),
                    new AppSettings(), tools, (Action<string, string, ChatActivity>)null, null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, completed.ResponseStatus, "an open or blocked list does not override model final");
                AssertEqual(blocked ? 4 : 3, requests.Count, "no forced corrective continuation");
                var activeId = session.ActiveTaskListArtifactId;
                var task = TaskListService.Active(session);
                AssertEqual(blocked ? "blocked" : "active", task.Status, "final retains exact task state");
                AssertTrue(task.Steps.All(step => step.Status == "pending"), "ending the answer does not complete deliverables");
                var next = service.ExecuteAsync(ChatModes.Agent, "Что ещё осталось?", session, NewContext(adapter),
                    new AppSettings(), tools, (Action<string, string, ChatActivity>)null, null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, next.ResponseStatus, "next run can answer about work without a task-list ritual");
                AssertEqual(blocked ? 5 : 4, requests.Count, "next final also does not force a recovery loop");
                AssertEqual(activeId, session.ActiveTaskListArtifactId, "new run does not change saved plan");
                AssertContains(FlattenSimple(requests.Last()), "Second result", "unfinished work reaches next run context");
                if (blocked) AssertContains(FlattenSimple(requests.Last()), "Need the missing input", "saved blocker reaches next run context");
            });
        }

        private static void AgentContinuityTwoFileLoop()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var session = NewSession(adapter);
                foreach (var file in new[] { new[] { "index.html", "html", "<!doctype html><html><head><title>Test</title></head><body><main>Chart</main></body></html>" },
                    new[] { "styles.css", "css", "body{color:red}" }, new[] { "app.js", "script", "const zoom = false;" } })
                    executor.MutateLocalResources(session, HtmlWorkspaceToolCatalog.WriteFileToolId, null,
                        () => HtmlWorkspaceToolService.UpsertFile(session, file[0], file[1], file[2], true));
                var cssTarget = executor.ResourceGateway.Find(session, "styles.css", "html").Items.Single(i => i.Type == "HTML file").Target;
                var jsTarget = executor.ResourceGateway.Find(session, "app.js", "html").Items.Single(i => i.Type == "HTML file").Target;
                Func<string, JObject, string> call = (tool, args) => new JObject { ["message"] = "Выполняю следующий шаг.", ["final"] = false,
                    ["tool_calls"] = new JArray(new JObject { ["name"] = tool, ["arguments"] = args }) }.ToString();
                var close = call(TaskListToolCatalog.SetToolId, new JObject { ["action"] = "close", ["outcome"] = "completed",
                    ["updates"] = new JArray(new JObject { ["index"] = 1, ["status"] = "completed" }, new JObject { ["index"] = 2, ["status"] = "completed", ["note"] = "Reviewed JS; no change needed." }) });
                var responses = new Queue<string>(new[] {
                    call(ResourceToolCatalog.ReadToolId, new JObject { ["target"] = cssTarget, ["representation"] = "source" }),
                    call(ResourceToolCatalog.ReadToolId, new JObject { ["target"] = jsTarget, ["representation"] = "source" }),
                    LoadToolSchemaResponse(TaskListToolCatalog.SetToolId),
                    call(TaskListToolCatalog.SetToolId, new JObject { ["action"] = "save", ["goal"] = "Restore chart appearance",
                        ["steps"] = new JArray(new[] { cssTarget, jsTarget }.Select(target => new JObject { ["text"] = target })) }),
                    LoadToolSchemaResponse(HtmlWorkspaceToolCatalog.WriteFileToolId),
                    call(HtmlWorkspaceToolCatalog.WriteFileToolId, new JObject { ["path"] = "styles.css", ["content"] = "body{color:blue}" }),
                    close,
                    "{\"message\":\"Изменён CSS; JS проверен, правка не потребовалась.\",\"final\":true,\"tool_calls\":[]}" });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var completed = service.ExecuteAsync(ChatModes.Agent, "Проверь CSS и JS и исправь оформление графика.", session, NewContext(adapter), new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    (Action<string, string, ChatActivity>)null, null).GetAwaiter().GetResult();
                if (completed.ResponseStatus != AgentResponseStatuses.Completed)
                    foreach (var message in session.Messages) {
                        ToolResultWireReadResult wire; string error;
                        if (ToolResultHistoryReader.TryRead(message, out wire, out error))
                            Console.WriteLine(wire.Name + ": " + wire.Result.Status + " " + wire.Result.Message);
                    }
                AssertEqual(AgentResponseStatuses.Completed, completed.ResponseStatus,
                    "native loop accepts reasoned completion without a redundant JS edit (requests=" + requests.Count + "; " + completed.AssistantText + ")");
                AssertEqual(8, requests.Count, "no extra read, edit or completion repair is required");
                AssertContains(FlattenSimple(requests.Last()), "Reviewed JS; no change needed.", "assessment rationale reaches the next model request");
                AssertTrue(string.IsNullOrEmpty(session.ActiveTaskListArtifactId), "explicit model close archives its plan");
                AssertEqual("const zoom = false;", session.HtmlWorkspace.Files.Single(f => f.Path == "app.js").Content, "JS remains unchanged");
                AssertEqual("body{color:blue}", session.HtmlWorkspace.Files.Single(f => f.Path == "styles.css").Content, "CSS change was actually applied");
                var writes = session.Messages.Where(message => message.Activity?.ToolId == HtmlWorkspaceToolCatalog.WriteFileToolId).ToList();
                AssertEqual(1, writes.Count, "task assessment does not invent a second file operation");

            });
        }

        private static void AgentContinuitySourceRecovery()
        {
            WithTempPaths(paths => {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession(); var scope = authority.Scope(session, false);
                var body = "alpha\r\nbeta\rgamma\ndelta\n" + new string('z', 40000);
                var evidence = PublishContinuitySource(store, payloads, scope, "lines", body);
                var result = ContinuityResult("large", "common.resources_read", ToolResult.Ok("Read", JsonConvert.SerializeObject(new {
                    kind = "resource-read", target = "Source lines", text = body, complete = true })));
                result.ResourceEvidence.Add(evidence);
                result.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(result.Content, "application/json"));
                var snapshot = new ModelAuthoritySnapshot(authority.CaptureMany(new[] { scope }), "pack", new SkillCatalogSnapshot(null), null, 0);
                var compiled = new ModelContextCompiler(payloads).Compile(snapshot, new ChatMessage[0], new[] { ContinuityCall(result), result }, null, new ToolCatalogEntry[0], new AppSettings(), 1000);
                var receipt = compiled.Messages.Single(m => m.CompletedOperation != null);
                AssertEqual(ToolResultStatus.Ok, receipt.CompletedOperation.Status, "oversized read keeps its actual outcome");
                AssertContains(receipt.Content, "startLine/lineCount", "model can recover using bounded lines");
                AssertTrue(compiled.Messages.All(m => m.ResourceEvidence.Count == 0), "omitted body gives no overwrite authority");
                var gateway = new ResourceGatewayService(null, null, null, authority: authority);
                var selected = gateway.ReadLines(session, evidence.Resource, "text", 2, 2);
                AssertEqual("beta\rgamma\n", selected.Result.Text, "line selection preserves mixed newlines exactly");
                AssertTrue(!selected.Result.Complete && selected.Result.Coverage.Kind == ResourceCoverageKinds.CharacterRange, "excerpt cannot become whole-source evidence");
                AssertEqual(7, selected.Result.Offset, "coverage uses original character coordinates");
                var excerptEvidence = gateway.Evidence(session, selected.Result).Single();
                AssertTrue(!excerptEvidence.Complete && excerptEvidence.Coverage.Kind == ResourceCoverageKinds.CharacterRange,
                    "retaining an excerpt never upgrades its evidence to a whole read");
                AssertEqual(selected.Result.Text, payloads.ReadText(excerptEvidence.Payload.ToBlobReference()), "only excerpt bytes enter observed payload");
                AssertEqual("resource_lines_too_large", RuntimeThrows<ResourceRequestException>(() => gateway.ReadLines(session, evidence.Resource, "text", 5, 1)).ErrorCode, "oversized line fails with actionable recovery");
                var schema = JObject.Parse(ResourceReadToolHandler.Descriptor.ParametersJson);
                var arguments = new JObject { ["target"] = "Source lines", ["representation"] = "text", ["startLine"] = 2, ["lineCount"] = 2 };
                string schemaError;
                AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, false, out schemaError), "schema admits bounded source lines");
                arguments["offset"] = 1;
                AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out schemaError), "line and row selectors cannot mix");
            });
        }

        private static ResourceEvidence PublishContinuitySource(ResourceAuthorityStore store, ChatBlobStore payloads,
            ResourceAuthorityScopeId scope, string name, string body)
        {
            var identity = ResourceStateProvider.Identity(scope, name);
            var snapshot = store.Capture(scope); var previous = snapshot.GetHead(identity)?.Revision;
            var reference = new ResourceRef(identity.Uri, "r" + Guid.NewGuid().ToString("N"));
            var payload = PayloadRef.FromBlob(payloads.StoreText(body, "text/plain"));
            store.RegisterRevision(scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload, previous, previous));
            store.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                new[] { new ResourceHeadChange(identity, snapshot.GetHead(identity), ResourceHeadState.Known(reference, snapshot.Generation + 1)) },
                previous == null ? AuthorityCommitReason.InitialObservation : AuthorityCommitReason.Restore));
            return new ResourceEvidence(name + reference.Revision, scope, reference, "text", ResourceCoverage.Whole(), true,
                snapshot.Generation + 1, payload, contentSha256: payload.Sha256);
        }
    }
}
