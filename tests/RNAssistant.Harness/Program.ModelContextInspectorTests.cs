using RNAssistant.Core.Services;
using RNAssistant.Core.Agent;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelContextWorkspaceExchange()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.ConnectProject(System.IO.Path.Combine(paths.Root, "project"), true);
                var other = workspaces.ConnectProject(System.IO.Path.Combine(paths.Root, "other-project"), true);
                var session = workspaces.CreateSession(chats, workspace, "Diagnostics");
                var foreign = workspaces.CreateSession(chats, other, "Other");
                const string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Прочитай отчёт\"}]}";
                var request = chats.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "shared-id", RunId = "run", StepId = "step" }, body,
                    "application/json", "run", "turn", "step");
                var response = chats.AppendTrace(session, SessionEventTypes.LlmResponse,
                    new LlmTraceRecord { RequestId = "shared-id" }, "{\"Content\":\"Ответ\"}",
                    "application/json", "run", "turn", "step");
                var foreignEvent = chats.AppendTrace(foreign, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "shared-id" }, "{}", "application/json", "run", "turn", "step");
                var loaded = ChatDiagnosticsScope.ReadWorkspaceSession(paths, chats, workspace.WorkspaceId, session.Id);
                RuntimeThrows<InvalidOperationException>(() => ChatDiagnosticsScope.ReadWorkspaceSession(paths, chats, other.WorkspaceId, session.Id));
                RuntimeThrows<InvalidOperationException>(() => ChatDiagnosticsScope.ReadWorkspaceSession(paths, chats, workspace.WorkspaceId, ""));
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var eventStore = new ChatEventStoreAdapter(chats);
                    var journal = new EventStreamTrajectoryQuery().Query(eventStore.Read(loaded, SessionEventReadMode.RequireComplete),
                        new TrajectoryQueryRequest { RunId = "run", StepId = "step", PageSize = 100 });
                    AssertTrue(journal.Records.Count > 0 && journal.Records.All(item => item.Event.SessionId == session.Id), "workspace journal stays in its validated session");
                    var preview = new TrajectoryPayloadService(eventStore, new ChatBlobStore(paths), data)
                        .Open(loaded, request.EventId, CancellationToken.None);
                    AssertEqual(workspace.WorkspaceId, preview.WorkspaceId, "event preview echoes workspace identity");
                    RuntimeThrows<InvalidOperationException>(() => data.Close(session.Id, TrajectoryPayloadService.Owner, preview.Data.LeaseId));
                    data.Close(session.Id, ChatDiagnosticsScope.DownloadOwner(TrajectoryPayloadService.Owner, workspace.WorkspaceId), preview.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => new TrajectoryPayloadService(eventStore, new ChatBlobStore(paths), data)
                        .Open(loaded, foreignEvent.EventId, CancellationToken.None));
                    var export = new TrajectoryExportDownloadService(new TrajectoryExportService(paths, null, new EventStreamTrajectoryQuery()), data)
                        .Open(loaded, () => eventStore.Read(loaded, SessionEventReadMode.RequireComplete),
                            new TrajectoryExportRequest { RunId = "run", RedactionMode = "metadata" }, CancellationToken.None);
                    AssertEqual(workspace.WorkspaceId, export.WorkspaceId, "export echoes workspace identity");
                    RuntimeThrows<InvalidOperationException>(() => data.Close(session.Id, TrajectoryExportDownloadService.Owner, export.Data.LeaseId));
                    data.Close(session.Id, ChatDiagnosticsScope.DownloadOwner(TrajectoryExportDownloadService.Owner, workspace.WorkspaceId), export.Data.LeaseId);
                    var service = new ModelContextInspectorService(new ChatEventStoreAdapter(chats), new ChatBlobStore(paths), data);
                    var query = new ModelContextQuery { ChatId = session.Id, WorkspaceId = workspace.WorkspaceId, RequestEventId = request.EventId };
                    var detail = service.Query(loaded, query, CancellationToken.None);
                    AssertEqual(workspace.WorkspaceId, detail.WorkspaceId, "response retains exact workspace");
                    AssertEqual(2, detail.Events.Count, "workspace request and matching response only");
                    AssertEqual(response.EventId, detail.Events.Last().EventId, "foreign shared request ID is isolated");
                    var version = loaded.Revision;
                    var unchanged = service.Query(loaded, new ModelContextQuery { ChatId = session.Id,
                        WorkspaceId = workspace.WorkspaceId, KnownRevision = version }, CancellationToken.None);
                    AssertTrue(unchanged.Unchanged && unchanged.WorkspaceId == workspace.WorkspaceId, "polling preserves scope");
                    var download = service.Open(loaded, new ModelContextPayloadQuery { ChatId = session.Id,
                        WorkspaceId = workspace.WorkspaceId, EventId = request.EventId }, CancellationToken.None);
                    string mime;
                    AssertEqual(body, Encoding.UTF8.GetString(data.ReadDownload(download.Data.LeaseId, 0,
                        (int)download.Data.Payload.ByteLength, CancellationToken.None, out mime)), "exact saved workspace bytes");
                    RuntimeThrows<InvalidOperationException>(() => data.Close(session.Id, ModelContextInspectorService.Owner, download.Data.LeaseId));
                    data.Close(session.Id, ChatDiagnosticsScope.DownloadOwner(ModelContextInspectorService.Owner, workspace.WorkspaceId), download.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => service.Query(loaded, new ModelContextQuery { ChatId = session.Id }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => service.Query(loaded, new ModelContextQuery { ChatId = session.Id, WorkspaceId = other.WorkspaceId }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => service.Open(loaded, new ModelContextPayloadQuery { ChatId = foreign.Id,
                        WorkspaceId = workspace.WorkspaceId, EventId = request.EventId }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => service.Open(loaded, new ModelContextPayloadQuery { ChatId = session.Id,
                        WorkspaceId = workspace.WorkspaceId, EventId = foreignEvent.EventId }, CancellationToken.None));
                    AssertEqual(version, chats.Load(session.Id).Revision, "diagnostic reads do not change history");
                }
                System.IO.Directory.Delete(workspace.RootPath, true);
                AssertEqual(session.Id, ChatDiagnosticsScope.ReadWorkspaceSession(paths, chats, workspace.WorkspaceId, session.Id).Id,
                    "saved exchange remains readable while the source folder is unavailable");
                workspaces.HideProject(workspace.WorkspaceId);
                RuntimeThrows<System.IO.InvalidDataException>(() => ChatDiagnosticsScope.ReadWorkspaceSession(paths, chats, workspace.WorkspaceId, session.Id));
            });
        }

        private static void WorkspaceDiagnosticUsage()
        {
            var session = new ChatSession { LastRun = new ChatRunRecord { RunId = "run",
                ModelConfiguration = new ModelRunMetadata { ContextWindowTokens = 32000 } } };
            AssertTrue(RNAssistant.Runtime.WorkspaceConversationService.ReadContextUsage(session) == null, "no response is not zero usage");
            session.Messages.Add(new ChatMessage { Role = "assistant", ProtocolMessage = true, RunId = "run",
                PromptTokens = 1200, CompletionTokens = 100, TotalTokens = 1300, AcceptedCallOrigin = new AcceptedToolCallOrigin("step", "attempt", 0) });
            session.Messages.Add(new ChatMessage { Role = "assistant", ProtocolMessage = true, RunId = "run",
                AcceptedCallOrigin = new AcceptedToolCallOrigin("step", "attempt", 1) });
            var usage = RNAssistant.Runtime.WorkspaceConversationService.ReadContextUsage(session);
            AssertEqual(1200, usage.PromptTokens.Value, "parallel calls retain their one provider completion");
            AssertEqual("step", usage.StepId, "usage has its accepted source step");
            AssertEqual(32000, usage.ContextWindowTokens.Value, "window belongs to the same run");
            session.LastRun.RunId = "next-run";
            AssertTrue(RNAssistant.Runtime.WorkspaceConversationService.ReadContextUsage(session).ContextWindowTokens == null,
                "a newer run cannot lend its model window to an older response");
            session.Messages.Add(new ChatMessage { Role = "assistant", ProtocolMessage = true, RunId = "next-run", ResponseModelAttemptId = "new-attempt" });
            AssertTrue(RNAssistant.Runtime.WorkspaceConversationService.ReadContextUsage(session).PromptTokens == null,
                "missing usage in the last response does not reuse an older count");
        }

        private static void ModelContextExactReadMessageCost()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var session = chats.Create("Word", "read-cost", "Cost.docx", "Cost");
                var scope = new ResourceAuthorityScopeId("conversation", session.Id);
                var reference = new ResourceRef(ResourceStateProvider.Identity(scope, "read-cost").Uri, "r1");
                var message = new ChatMessage { Id = "read-cost-result", Role = "tool",
                    ToolCallId = "read-cost-call", ToolName = "common.resources_read",
                    Content = "{\"text\":\"Ω😀\",\"coverage\":{\"kind\":\"line-range\"}}" };
                var source = new ContextMessagePresentation { SourceMessageId = message.Id,
                    MessageIndex = 0, Role = "tool", ToolCallId = message.ToolCallId,
                    Presentation = ContextPresentationKind.Fragment,
                    EvidenceLinks = new System.Collections.Generic.List<ContextEvidenceLink> {
                        new ContextEvidenceLink { EvidenceId = "read-cost-evidence", ScopeId = scope,
                            Resource = reference, View = "source",
                            ObservedCoverage = new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 2) } } };
                var receipt = new ContextReceipt { Messages = new System.Collections.Generic.List<ContextMessagePresentation> { source } };
                var settings = new AppSettings { BaseUrl = "https://example.invalid", Model = "cost-fixture",
                    StreamResponses = false };
                var options = new LlmRequestOptions { TraceSession = session, TraceContextReceipt = receipt,
                    TraceMessageContentChanged = new bool[0] };
                new ModelTracePersistenceService(new ChatEventStoreAdapter(chats)).Configure(options);
                var persist = options.TraceSink;
                LlmTraceRecord trace = null;
                options.TraceSink = record => { persist(record); trace = record;
                    throw new InvalidOperationException("stop-after-saved-request"); };
                try { new LlmClient(() => "key").CompleteAsync(settings, new[] { message }, options, null)
                    .GetAwaiter().GetResult(); }
                catch (InvalidOperationException error)
                { AssertEqual("stop-after-saved-request", error.Message, "request is saved before network dispatch"); }
                AssertTrue(trace?.PayloadUtf8Bytes != null, "exact read request was serialized");
                var saved = chats.ReadEvents(session.Host, session.DocumentKey, session.Id)
                    .Single(item => item.Type == SessionEventTypes.LlmRequest);
                var savedBytes = Encoding.UTF8.GetBytes(chats.ReadEventPayload(saved));
                AssertTrue(trace.PayloadUtf8Bytes.SequenceEqual(savedBytes), "cost uses the saved request bytes");
                var wire = (JArray)JObject.Parse(Encoding.UTF8.GetString(savedBytes))["messages"];
                var exact = (JObject)wire.Single();
                var expected = ModelContextBudget.EstimateMessageTokens(new ChatMessage {
                    Role = (string)exact["role"], Content = (string)exact["content"],
                    ToolCallId = (string)exact["tool_call_id"] }, settings, includeExtractedAttachments: false);
                AssertEqual(expected, trace.ContextMessages.Single().EstimatedWireMessageTokens.Value,
                    "single-view read cost estimates the entire exact serialized tool message");
                AssertTrue(source.EstimatedWireMessageTokens == null,
                    "wire estimate does not modify the frozen compiler receipt");
                AssertTrue(JToken.DeepEquals(JToken.FromObject(trace.ContextMessages), saved.Data["ContextMessages"]),
                    "read-message cost is retained in the request trace, outside provider JSON");
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(chats),
                        new ChatBlobStore(paths), plane);
                    var selected = inspector.Query(session, new ModelContextQuery { RequestEventId = saved.EventId },
                        CancellationToken.None).Resources.Single().Observations.Single();
                    AssertEqual(expected, selected.EstimatedWireMessageTokens.Value,
                        "Inspector associates the whole-message estimate with the sole exact read view");
                }
                source.EstimatedWireMessageTokens = 1;
                var unproved = LlmClient.DescribeWireMessages(new[] { message }, receipt, null, null, wire, settings);
                AssertTrue(unproved.Single().EstimatedWireMessageTokens == null,
                    "a missing projection comparison cannot prove exact read-view attribution");
                var altered = LlmClient.DescribeWireMessages(new[] { message }, receipt, new[] { true }, null,
                    wire, settings);
                AssertTrue(altered.Single().EstimatedWireMessageTokens == null,
                    "a changed post-compiler message cannot inherit a stale read-view cost");
                source.EstimatedWireMessageTokens = null;
                source.EvidenceLinks.Add(new ContextEvidenceLink { EvidenceId = "second", ScopeId = scope,
                    Resource = reference, View = "source" });
                var ambiguous = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null, wire, settings);
                AssertTrue(ambiguous.Single().EstimatedWireMessageTokens == null,
                    "one model message is not charged independently to multiple evidence links");
                source.EvidenceLinks.RemoveAt(1);
                receipt.Messages.Add(source);
                var duplicate = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null, wire, settings);
                AssertTrue(duplicate.Single().EstimatedWireMessageTokens == null,
                    "duplicate source-message mapping cannot assign a view cost");
                receipt.Messages.RemoveAt(1);
                source.MessageIndex = 1;
                var reordered = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null, wire, settings);
                AssertTrue(reordered.Single().EstimatedWireMessageTokens == null,
                    "a shifted source index cannot assign the view cost");
                source.MessageIndex = 0;
                var changedMessage = (JObject)exact.DeepClone();
                changedMessage["content"] = "different";
                var changedWire = new JArray(changedMessage);
                var mismatched = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null,
                    changedWire, settings);
                AssertTrue(mismatched.Single().EstimatedWireMessageTokens == null,
                    "a final wire text mismatch cannot assign the view cost");
                message.ToolName = "common.resources_search";
                var otherTool = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null, wire, settings);
                AssertTrue(otherTool.Single().EstimatedWireMessageTokens == null,
                    "an unrelated tool result cannot inherit the read-view estimate");
                source.MessageIndex = null;
                source.EstimatedWireMessageTokens = 1;
                var omitted = LlmClient.DescribeWireMessages(new[] { message }, receipt, new bool[0], null, wire, settings);
                AssertTrue(omitted.Single(item => !item.MessageIndex.HasValue).EstimatedWireMessageTokens == null,
                    "an excluded receipt entry cannot retain a request-specific read-view estimate");
            });
        }

        private static void ModelRequestSchemaDuplicationModesAndFallback()
        {
            var tool = V8ReadTool();
            var tools = new[] { tool };
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var settings = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema,
                FallbackToJsonObject = true, MaxAgentFormatRetries = 2, StreamResponses = false };
            var accepted = new ConversationPromptComposer().BuildRequiredMessages(ChatModes.Agent,
                "Read the workbook.", adapter, tools, new SkillDefinition[0], new DocumentContext(),
                settings, NewSession(adapter), null);
            accepted[0].Id = "schema-prompt";
            var fullPrompt = accepted[0].Content;
            AssertContains(fullPrompt, "\"parameters\"", "accepted prompt retains exact fallback contracts");
            var options = ModelProtocolWire.CreateRequestOptions(AgentResponseModes.JsonSchema, tools,
                new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(tools)));
            options.TraceContextReceipt = new ContextReceipt { Messages = new System.Collections.Generic.List<ContextMessagePresentation> {
                new ContextMessagePresentation { SourceMessageId = accepted[0].Id, MessageIndex = 0,
                    Role = accepted[0].Role, Presentation = ContextPresentationKind.Full } } };
            var driftedSchema = JObject.Parse(options.ResponseSchemaJson);
            driftedSchema.SelectToken("properties.response.anyOf[0].properties.tool_calls.items.anyOf[0].properties.name.const")
                .Replace(new JValue("different.tool"));
            AssertTrue(ReferenceEquals(accepted, ConversationPromptComposer.ProjectJsonSchemaMessages(
                accepted, tools, driftedSchema.ToString(Newtonsoft.Json.Formatting.None))),
                "schema drift leaves the full prompt untouched");
            var userRoleSettings = new AppSettings { SystemPromptRole = "user" };
            var userRoleMessages = new ConversationPromptComposer().BuildRequiredMessages(ChatModes.Agent,
                "Keep this user request.", adapter, tools, new SkillDefinition[0], new DocumentContext(),
                userRoleSettings, NewSession(adapter), null);
            var projectedUserRole = ConversationPromptComposer.ProjectJsonSchemaMessages(
                userRoleMessages, tools, options.ResponseSchemaJson);
            AssertTrue(projectedUserRole[0].Content.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0 &&
                projectedUserRole[0].Content.EndsWith("Keep this user request.", StringComparison.Ordinal),
                "user instruction role preserves the request suffix while eliding strict duplicates");
            var request = new ModelProtocolRequest
            {
                Settings = settings, AcceptedMessages = accepted, CallableTools = tools,
                RunnableCatalog = tools, CallContext = new ModelProtocolCallContext(new string[0]),
                Options = options,
                CompileRepair = notice => accepted.Concat(new[] { notice }).ToArray(),
                ProjectJsonSchemaMessages = messages => ConversationPromptComposer.ProjectJsonSchemaMessages(
                    messages, tools, options.ResponseSchemaJson)
            };
            var formats = new System.Collections.Generic.List<string>();
            var wirePrompts = new System.Collections.Generic.List<string>();
            var repairMessages = new System.Collections.Generic.List<string>();
            var presentations = new System.Collections.Generic.List<ContextPresentationKind>();
            var protocol = new ModelProtocolClient((currentSettings, messages, currentOptions, progress, token) =>
            {
                var snapshot = messages.ToList();
                var api = new LlmMessageBuilder().Build(snapshot, currentSettings, currentOptions);
                var body = LlmClient.BuildRequestBody(currentSettings, api.Messages,
                    api.EstimatedPromptTokens, currentOptions);
                var exact = JObject.Parse(Encoding.UTF8.GetString(LlmHttpTransport.SerializeJson(body)));
                formats.Add((string)exact.SelectToken("response_format.type"));
                wirePrompts.Add((string)exact["messages"][0]["content"]);
                repairMessages.Add(snapshot.Last().Content);
                var provenance = LlmClient.DescribeWireMessages(snapshot, currentOptions.TraceContextReceipt,
                    currentOptions.TraceMessageContentChanged);
                presentations.Add(provenance[0].Presentation);
                if (formats.Count < 3)
                    AssertContains(provenance[0].Reason, "изменён", "strict projection is not attributed as a full source message");
                if (formats.Count == 1)
                    return System.Threading.Tasks.Task.FromResult(new LlmCompletionResult { Content = "invalid" });
                if (formats.Count == 2)
                    throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "schema rejected during repair");
                return System.Threading.Tasks.Task.FromResult(new LlmCompletionResult
                    { Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                        "Answer directly.", "Done.", results: new ConversationResultClaim[0]) });
            });
            var result = protocol.GetResponseAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(result.Failure == null, "strict repair falls back and accepts a valid object response");
            AssertEqual("json_schema,json_schema,json_object", string.Join(",", formats),
                "schema rejection retains the one-time fallback during repair");
            AssertTrue(wirePrompts[0].IndexOf("\"parameters\"", StringComparison.Ordinal) < 0 &&
                wirePrompts[1].IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                "strict attempts omit only the repeated parameter schemas");
            AssertEqual(fullPrompt, wirePrompts[2], "fallback reuses the exact accepted prompt");
            AssertEqual("Unknown,Unknown,Full", string.Join(",", presentations),
                "provenance follows the serialized strict, repair and fallback attempts");
            AssertEqual(ContextPresentationKind.Full, options.TraceContextReceipt.Messages[0].Presentation,
                "wire attribution does not mutate the frozen compiler receipt");
            AssertEqual(repairMessages[1], ModelProtocolWire.ProjectSchemaGuidance(new[] {
                new ChatMessage { Role = "user", ProtocolMessage = true, Content = repairMessages[2] } })[0].Content,
                "fallback restores full format guidance on the same repair instruction");
            AssertContains(wirePrompts[0], "\"description\"", "strict prompt retains descriptions");
            AssertContains(wirePrompts[0], "\"safety\"", "strict prompt retains safety policy");
            AssertContains(wirePrompts[0], "\"office_tool_policy\"", "strict prompt retains document policy");
            AssertTrue(options.ResponseSchemaJson == null, "fallback clears strict response schema");
            AssertEqual(fullPrompt, accepted[0].Content, "wire projection does not mutate accepted history");

            var objectOptions = ModelProtocolWire.CreateRequestOptions(AgentResponseModes.JsonObject, tools,
                new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(tools)));
            objectOptions.TraceContextReceipt = options.TraceContextReceipt;
            var objectRequest = new ModelProtocolRequest
            {
                Settings = settings, AcceptedMessages = accepted, CallableTools = tools,
                RunnableCatalog = tools, CallContext = new ModelProtocolCallContext(new string[0]),
                Options = objectOptions,
                ProjectJsonSchemaMessages = request.ProjectJsonSchemaMessages
            };
            var objectProtocol = new ModelProtocolClient((currentSettings, messages, currentOptions, progress, token) =>
            {
                AssertEqual(LlmResponseFormats.JsonObject, currentOptions.ResponseFormat, "direct object mode stays selected");
                AssertEqual(fullPrompt, messages.First().Content, "direct json_object retains exact parameter contracts");
                AssertEqual(ContextPresentationKind.Full, LlmClient.DescribeWireMessages(messages.ToList(),
                    currentOptions.TraceContextReceipt, currentOptions.TraceMessageContentChanged)[0].Presentation,
                    "direct object request retains Full provenance");
                return System.Threading.Tasks.Task.FromResult(new LlmCompletionResult
                    { Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                        "Answer directly.", "Done.", results: new ConversationResultClaim[0]) });
            });
            AssertTrue(objectProtocol.GetResponseAsync(objectRequest, null, CancellationToken.None)
                .GetAwaiter().GetResult().Failure == null, "json_object mode accepts the shared v8 response");
        }

        private static void ModelRequestSchemaDuplicationSize()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                const string extensionId = "common.markdown_save";
                var catalog = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var store = new ChatStore(FixturePaths.Value);
                var session = store.Create("Excel", "size-document", "Size.xlsx", "Size");
                session.Mode = ChatModes.Agent;
                session.LastRun = new ChatRunRecord { RunId = "size-run", TurnId = "size-turn" };
                store.Save(session);
                var pack = CallableToolPack.Create(ChatModes.Agent, "Excel", session.LastRun.RunId, catalog);
                var native = executor.CreateNativeRuntime(session, catalog, new AppSettings(), ChatModes.Agent,
                    false, discoveryCatalog: catalog, manualRun: true);
                var call = new ToolCall("size-read", CapabilityToolCatalog.ReadToolId,
                    new JObject { ["id"] = extensionId }.ToString());
                var read = native.ExecuteAsync(new ToolExecutionContext(call, native.Describe(call),
                    session.LastRun.RunId, session.LastRun.TurnId, "size-read-step", DateTime.UtcNow, false, 1),
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "native schema read succeeds");
                // Admission consumes exact native evidence, before model-facing projection removes runtime revisions.
                var evidence = new ChatMessage { Role = "tool", ToolResultRole = "tool", ToolName = call.Name, ToolCallId = call.Id,
                    RunId = session.LastRun.RunId, ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                    Content = ToolResultWire.Write(call.Id, call.Name, read.Result), ProtocolMessage = true };
                ToolResultWireReadResult acceptedRead;
                string readError;
                AssertTrue(ToolResultHistoryReader.TryRead(evidence, out acceptedRead, out readError), readError ?? "native read parses");
                var nativeData = JObject.Parse(acceptedRead.Result.DataJson);
                AssertEqual("tool-schema", (string)nativeData["kind"], "native read returns a direct schema");
                AssertEqual(CapabilityCatalogService.Revision(catalog.Single(tool => tool.Id == extensionId)),
                    (string)nativeData["revision"], "native read preserves exact descriptor revision");
                AssertTrue(pack.StageReadResult(evidence), "representative extension stages from exact schema evidence");
                var admission = pack.PreparePending((tools, state) => true);
                new ToolPackAdmissionJournal(new ChatEventStoreAdapter(store), session)
                    .Append(admission, "size-step");
                pack.Publish(admission);
                AssertEqual(1, pack.OptionalSchemaCount, "representative pack contains one loaded extension");
                Console.WriteLine("REQUEST_PACK core=" + (pack.Tools.Count - pack.OptionalSchemaCount) +
                    " extension=" + extensionId);

                var skills = executor.CaptureSkills(executor.CaptureCatalogs());
                var allTools = new ToolPackSnapshot("size-pack", ChatModes.Agent, "Excel", catalog.Select(tool =>
                    ToolPackSnapshot.Capture(new ToolDescriptor(tool.Id, tool.Description, tool.ArgumentSchemaJson,
                        tool.Exposure, tool.ContractVersion), tool.Policy, tool.Binding)));
                foreach (var skill in skills.Skills.Where(item => item.BuiltIn))
                    Console.WriteLine("PROMPT_SKILL " + skill.Id + " characters=" + skill.BodyMarkdown.Length);
                Console.WriteLine("PROMPT_BLOCKS general=" + AgentPromptDefaults.GeneralInstructions.Length +
                    " tools=" + AgentPromptDefaults.ToolInstructions.Length + " skills=" + AgentPromptDefaults.SkillInstructions.Length);

                var blobs = new ChatBlobStore(FixturePaths.Value);
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    foreach (var selection in new[] { "", "common.task_tracking", "common.vba_code_editing", "common.html_workspace_authoring" })
                    {
                        var requestSizes = new System.Collections.Generic.Dictionary<string, int>();
                        foreach (var mode in new[] { AgentResponseModes.JsonSchema, AgentResponseModes.JsonObject })
                        {
                            var settings = new AppSettings { Model = "request-size-fixture", StreamResponses = false,
                                AgentResponseMode = mode, ContextWindowOverrideTokens = 131072 };
                            session.LastRun.SelectedSkills.Clear();
                            if (selection.Length > 0)
                                SkillTaskService.Select(session, selection, skills, allTools, settings, "size-load");
                            var surface = SkillTaskService.Compile(session, skills, allTools, catalog, pack.Tools,
                                "size-step", settings);
                            var callable = surface.Tools;
                            var messages = new ConversationPromptComposer().BuildRequiredMessages(ChatModes.Agent,
                                "Inspect the workbook and update the report.", adapter, callable, skills.Skills,
                                new DocumentContext(), settings, session, null, false, 0,
                                CapabilityCatalogService.BuildPromptCatalog(catalog, skills.Skills, callable));
                            messages.Insert(1, admission.StateMessage);
                            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                                "size-pack", skills, null, 1);
                            var compiled = new ModelContextCompiler().Compile(authority, messages, new ChatMessage[0],
                                null, callable, settings, ModelContextBudget.InputBudgetTokens(settings), skillSurface: surface).Messages;
                            foreach (var skill in surface.Instructions)
                                AssertEqual(1, compiled.Count(message => message.Content == "SELECTED_SKILL " + skill.Id + "\n" + skill.BodyMarkdown),
                                    "selected instructions remain complete and appear once");
                            var options = ModelProtocolWire.CreateRequestOptions(mode, callable,
                                new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(callable)));
                            var wireMessages = mode == AgentResponseModes.JsonSchema
                                ? ConversationPromptComposer.ProjectJsonSchemaMessages(compiled, callable,
                                    options.ResponseSchemaJson)
                                : compiled;
                            if (mode == AgentResponseModes.JsonSchema) wireMessages = ModelProtocolWire.ProjectSchemaGuidance(wireMessages);
                            var api = new LlmMessageBuilder().Build(wireMessages, settings, options);
                            var body = LlmClient.BuildRequestBody(settings, api.Messages, api.EstimatedPromptTokens,
                                options);
                            var bytes = LlmHttpTransport.SerializeJson(body);
                            options.TraceSession = session;
                            new ModelTracePersistenceService(new ChatEventStoreAdapter(store)).Configure(options);
                            var requestId = selection + "/" + mode;
                            options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = requestId,
                                PayloadUtf8Bytes = bytes, PayloadContentType = "application/json" });
                            var request = inspector.Query(session, new ModelContextQuery(), CancellationToken.None)
                                .Events.First(item => item.Trace.RequestId == requestId);
                            var opened = inspector.Open(session, new ModelContextPayloadQuery { EventId = request.EventId },
                                CancellationToken.None);
                            string mime;
                            var retained = data.ReadDownload(opened.Data.LeaseId, 0, bytes.Length,
                                CancellationToken.None, out mime);
                            data.Close(session.Id, ModelContextInspectorService.Owner, opened.Data.LeaseId);
                            AssertTrue(bytes.SequenceEqual(retained), "inspector returns exact materialized HTTP bytes");
                            requestSizes[mode] = retained.Length;
                            if (mode == AgentResponseModes.JsonSchema) {
                                var duplicateApi = new LlmMessageBuilder().Build(compiled, settings, options);
                                var duplicateBody = LlmClient.BuildRequestBody(settings, duplicateApi.Messages, duplicateApi.EstimatedPromptTokens, options);
                                AssertTrue(retained.Length < LlmHttpTransport.SerializeJson(duplicateBody).Length,
                                    "strict projection removes duplicated contracts while retaining argument semantics");
                                AssertTrue(!wireMessages[0].Content.Contains("TOOL_NAME"), "strict prompt omits shape examples");
                            }
                            var exact = JObject.Parse(Encoding.UTF8.GetString(retained));
                            var messagesBytes = LlmHttpTransport.SerializeJson(new JObject { ["messages"] = exact["messages"].DeepClone() }).Length -
                                Encoding.UTF8.GetByteCount("{\"messages\":}");
                            var schemaToken = exact.SelectToken("response_format.json_schema");
                            var schemaBytes = schemaToken == null ? 0 : Encoding.UTF8.GetByteCount(schemaToken.ToString(Newtonsoft.Json.Formatting.None));
                            var content = (string)exact["messages"][0]["content"];
                            var runtime = JObject.Parse(content.Substring(content.IndexOf("RUNTIME_CONTEXT:\n", StringComparison.Ordinal) +
                                "RUNTIME_CONTEXT:\n".Length));
                            var toolsJson = runtime["tools"].ToString(Newtonsoft.Json.Formatting.None);
                            AssertEqual(mode == AgentResponseModes.JsonSchema ? 0 : callable.Count,
                                ((JArray)runtime["tools"]).Count(item => item.SelectToken("function.parameters") != null),
                                "strict wire omits duplicated parameters; fallback-ready prompt retains them");
                            var marker = "\"tools\":" + toolsJson;
                            AssertContains(content, marker, "exact prompt contains the loaded callable pack");
                            var without = (JObject)exact.DeepClone();
                            without["messages"][0]["content"] = content.Replace(marker, "\"tools\":[]");
                            var toolsBytes = retained.Length - LlmHttpTransport.SerializeJson(without).Length;
                            var otherMessagesBytes = messagesBytes - toolsBytes;
                            var otherFieldsBytes = retained.Length - messagesBytes - schemaBytes;
                            Console.WriteLine("REQUEST_SIZE " + (selection.Length == 0 ? "core" : selection) + " " + mode +
                                " tools=" + toolsBytes + " schema=" + schemaBytes +
                                " otherMessages=" + otherMessagesBytes + " otherFields=" + otherFieldsBytes +
                                " total=" + retained.Length + " estimatedTokens=" +
                                ModelContextBudget.EstimateTextTokens(Encoding.UTF8.GetString(retained), settings) +
                                " sectionTokens=" + string.Join(",", new[] { toolsBytes, schemaBytes,
                                    otherMessagesBytes, otherFieldsBytes }.Select(size => (size + 3) / 4)));
                        }
                        // Strict required/null alternatives may cost more bytes than
                        // json_object; the invariant is no duplicate argument contract,
                        // with semantic descriptions retained in the supplied schema.
                    }
                }
            });
        }

        private static void ModelContextExactExchange()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "model-context", "Context.docx", "Context");
                var blobs = new ChatBlobStore(paths);
                var source = "{\"number\":9007199254740993123456789,\"message\":\"" + new string('語', 600000) + "😀\"}";
                var request = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "one", ModelAttemptId = "attempt-1", Streaming = true }, source,
                    "application/json", "run", "turn", "one");
                store.AppendTrace(session, SessionEventTypes.AssistantChunk, new LlmTraceRecord { RequestId = "one", ChunkIndex = 0 },
                    "[\"data frame\"]", "application/json", "run", "turn", "one");
                var response = store.AppendTrace(session, SessionEventTypes.LlmResponse, new LlmTraceRecord { RequestId = "one" },
                    "{\"Content\":\"done\"}", "application/json", "run", "turn", "one");
                var retry = store.AppendTrace(session, SessionEventTypes.LlmRequest, new LlmTraceRecord { RequestId = "two", ModelAttemptId = "attempt-2" },
                    "{}", "application/json", "run", "turn", "two");
                store.AppendTrace(session, SessionEventTypes.LlmFailure, new LlmTraceRecord { RequestId = "two", Error = "timeout" },
                    null, "application/json", "run", "turn", "two");
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    var page = service.Query(session, new ModelContextQuery(), CancellationToken.None);
                    AssertEqual(retry.EventId, page.Events[0].EventId, "latest prepared request first");
                    AssertEqual(2, page.Events.Count, "attempts are not collapsed");
                    var detail = service.Query(session, new ModelContextQuery { RequestEventId = request.EventId }, CancellationToken.None);
                    AssertEqual(3, detail.Events.Count, "request, matching chunks and response only");
                    AssertEqual(response.EventId, detail.Events.Last().EventId, "exact matching response");
                    AssertTrue(service.Query(session, new ModelContextQuery { KnownRevision = session.Revision }, CancellationToken.None).Unchanged,
                        "unchanged revision avoids re-reading the event stream");
                    var download = service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId }, CancellationToken.None);
                    var bytes = new byte[download.Data.Payload.ByteLength];
                    for (var offset = 0; offset < bytes.Length;)
                    {
                        string mime;
                        var chunk = data.ReadDownload(download.Data.LeaseId, offset, Math.Min(65536, bytes.Length - offset), CancellationToken.None, out mime);
                        Array.Copy(chunk, 0, bytes, offset, chunk.Length); offset += chunk.Length;
                    }
                    AssertEqual(source, Encoding.UTF8.GetString(bytes), "full payload above 512 KiB, Unicode and number spelling preserved");
                    AssertEqual(request.Payload.Sha256, download.Data.Payload.Sha256, "download retains stored byte identity");
                    data.Close(session.Id, ModelContextInspectorService.Owner, download.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = "foreign-event" }, CancellationToken.None));
                    RuntimeThrows<OperationCanceledException>(() => service.Query(session, new ModelContextQuery(), new CancellationToken(true)));
                    System.IO.File.Delete(blobs.PathFor(request.Payload.Sha256));
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId }, CancellationToken.None));
                }
            });
        }

        private static void ModelContextStableAliases()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var session = chats.Create("Word", "context-aliases", "Aliases.docx", "Aliases");
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var scope = new ResourceAuthorityScopeId("conversation", session.Id);
                var identity = ResourceStateProvider.Identity(scope, "one");
                var other = ResourceStateProvider.Identity(scope, "two");
                var first = new ResourceRef(identity.Uri, "revision-a");
                var second = new ResourceRef(identity.Uri, "revision-b");
                var restored = new ResourceRef(identity.Uri, "revision-restored");
                var otherRef = new ResourceRef(other.Uri, "revision-a");
                var body = PayloadRef.FromBlob(payloads.StoreText("same bytes", "text/plain; charset=utf-8"));
                var viewPart = PayloadRef.FromBlob(payloads.StoreText("retained view part", "text/plain"));
                foreach (var reference in new[] { first, second, restored })
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(reference, body.Sha256, body,
                        parent: reference == restored ? second : null, restoredFrom: reference == restored ? first : null,
                        dependencies: new[] { new ResourceDependency(otherRef, "text", ResourceCoverage.Whole(), "source") }));
                store.RegisterView(scope, new ResourceRevisionView(first, "text", body.Sha256, body,
                    ResourceCoverage.Whole(), new[] { viewPart }));
                store.Publish(ResourceAuthorityCommit.Create(scope, 0, null,
                    new[] { new ResourceHeadChange(identity, null, ResourceHeadState.Known(restored, 1)) },
                    AuthorityCommitReason.InitialObservation));
                Func<string, string, ResourceRef, bool, SessionEvent> request = (call, evidence, reference, complete) =>
                    chats.AppendTrace(session, SessionEventTypes.LlmRequest,
                        new LlmTraceRecord { RequestId = Guid.NewGuid().ToString("N"),
                            ContextMessages = new System.Collections.Generic.List<ContextMessagePresentation> {
                                new ContextMessagePresentation { Role = "tool", ToolCallId = call,
                                    EvidenceLinks = new System.Collections.Generic.List<ContextEvidenceLink> {
                                        new ContextEvidenceLink { EvidenceId = evidence, ScopeId = scope,
                                            Resource = reference, View = "text",
                                            ObservedCoverage = complete ? ResourceCoverage.Whole() :
                                                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 4),
                                            ObservationComplete = complete, BodyIncludedInMessage = complete } } } } },
                        "{}", "application/json", "run", "turn", call);
                var q1 = request("operation-a", "evidence-a", first, false);
                var q2 = request("operation-b", "evidence-b", otherRef, true);
                var q3 = request("operation-c", "evidence-c", second, true);
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    Func<ModelContextInspectorService> inspector = () =>
                        new ModelContextInspectorService(new ChatEventStoreAdapter(chats), payloads, plane, authority);
                    var original = inspector().Query(session, new ModelContextQuery { RequestEventId = q1.EventId }, CancellationToken.None);
                    AssertEqual("Q1", original.Events[0].Alias, "first request alias derives from immutable order");
                    AssertEqual("R1@v1", original.Resources[0].Alias, "first exact resource revision");
                    AssertEqual("O1", original.Operations[0].Alias, "operation alias is separate from its runtime id");
                    AssertEqual("E1", original.Evidence[0].Alias, "evidence alias is separate from its runtime id");
                    AssertEqual(ModelContextCurrentness.Superseded, original.Resources[0].Currentness,
                        "only the exact current authority head is current");
                    AssertTrue(original.Resources[0].RevisionRecorded == true &&
                        original.Resources[0].Observations[0].ObservationComplete == false &&
                        original.Resources[0].Observations[0].BodyIncludedInMessage == false,
                        "partial observed coverage and non-delivery remain distinct from retained revision metadata");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked, original.Resources[0].PayloadAvailability,
                        "metadata-only inspection does not verify source bytes");
                    AssertEqual("text", original.Resources[0].RetainedViews.Single().View,
                        "inspector finds retained metadata for the observed exact view");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked,
                        original.Resources[0].RetainedViews.Single().PartsAvailability.Value,
                        "view parts are not verified during metadata-only inspection");
                    AssertTrue(original.Resources[0].RetainedViews.Single().EstimatedTextBodyTokens == null,
                        "multipart retained views do not claim a direct text-body cost");
                    AssertTrue(original.Resources[0].PayloadCheckedUtc == null, "no invented verification timestamp");
                    Func<string, string, ModelContextResourceDto> verify = (eventId, alias) => inspector().Query(session,
                        new ModelContextQuery { RequestEventId = eventId, VerifyResourceAlias = alias }, CancellationToken.None)
                        .Resources.Single(row => row.Alias == alias);
                    Func<string, string, string, ModelContextResourceDto> verifyView = (eventId, alias, view) => inspector().Query(session,
                        new ModelContextQuery { RequestEventId = eventId, VerifyResourceAlias = alias,
                            VerifyRetainedView = view }, CancellationToken.None).Resources.Single(row => row.Alias == alias);
                    var verified = verify(q1.EventId, "R1@v1");
                    AssertEqual(ModelContextPayloadAvailability.Verified, verified.PayloadAvailability, "explicit CAS verification");
                    AssertTrue(verified.PayloadCheckedUtc.HasValue, "verification has a time rather than perpetual availability");
                    AssertEqual(body.Sha256, verified.RevisionMetadata.Payload.Sha256, "exact authority payload supplies verification");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked,
                        verified.RetainedViews.Single().PartsAvailability.Value,
                        "revision verification does not check retained view parts");
                    var checkedView = verifyView(q1.EventId, "R1@v1", "text");
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        checkedView.RetainedViews.Single().PayloadAvailability, "view payload is checked independently");
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        checkedView.RetainedViews.Single().PartsAvailability.Value, "every retained view part is checked");
                    AssertTrue(checkedView.RetainedViews.Single().CheckedUtc.HasValue,
                        "view verification has its own timestamp");
                    var viewJson = Newtonsoft.Json.JsonConvert.SerializeObject(checkedView);
                    AssertContains(viewJson, "\"partsAvailability\":\"Verified\"",
                        "typed bridge serializes selected view-part verification");
                    AssertTrue(!viewJson.Contains(viewPart.Sha256),
                        "retained part CAS addresses stay runtime-only in the typed bridge response");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked, checkedView.PayloadAvailability,
                        "view verification does not claim to check the revision payload");
                    AssertEqual(ModelContextCurrentness.Superseded, verified.Currentness, "CAS validity does not move the head");
                    AssertTrue(verified.Observations[0].BodyIncludedInMessage == false, "CAS validity does not imply delivery");
                    var partPath = payloads.PathFor(viewPart.Sha256);
                    var partBytes = System.IO.File.ReadAllBytes(partPath);
                    System.IO.File.Delete(partPath);
                    var damaged = verifyView(q1.EventId, "R1@v1", "text");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked, damaged.PayloadAvailability,
                        "missing view part does not manufacture a revision-payload check");
                    AssertEqual(ModelContextPayloadAvailability.Unavailable,
                        damaged.RetainedViews.Single().PartsAvailability.Value,
                        "a missing view part cannot be reported as available");
                    System.IO.File.WriteAllBytes(partPath, partBytes);
                    RuntimeThrows<InvalidOperationException>(() => verifyView(q1.EventId, "R1@v1", "unadvertised"));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session,
                        new ModelContextQuery { RequestEventId = q1.EventId, VerifyRetainedView = "text" }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => verify(q1.EventId, "R2@v1"));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session,
                        new ModelContextQuery { VerifyResourceAlias = "R1@v1", KnownRevision = session.Revision }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session,
                        new ModelContextQuery { Alias = "R1@v1", VerifyResourceAlias = "R1@v1" }, CancellationToken.None));
                    AssertEqual(ModelContextPayloadAvailability.Unknown, verify(q2.EventId, "R2@v1").PayloadAvailability,
                        "missing revision metadata is unknown, not a missing payload");
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(otherRef));
                    AssertEqual(ModelContextPayloadAvailability.NotRetained, verify(q2.EventId, "R2@v1").PayloadAvailability,
                        "registered payload-free revision differs from unknown metadata");
                    store.RegisterView(scope, new ResourceRevisionView(otherRef, "text", body.Sha256, null,
                        ResourceCoverage.Whole(), new[] { viewPart }));
                    var viewOnly = verifyView(q2.EventId, "R2@v1", "text");
                    AssertEqual(ModelContextPayloadAvailability.NotRetained, viewOnly.PayloadAvailability,
                        "view payload does not manufacture a revision payload");
                    AssertEqual(ModelContextPayloadAvailability.NotRetained,
                        viewOnly.RetainedViews.Single().PayloadAvailability,
                        "parts-only view does not manufacture a view payload");
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        viewOnly.RetainedViews.Single().PartsAvailability.Value,
                        "retained view parts can be checked without a revision or view payload");
                    var next = inspector().Query(session, new ModelContextQuery { RequestEventId = q3.EventId }, CancellationToken.None);
                    AssertEqual("Q3", next.Events[0].Alias, "request aliases survive intervening requests");
                    AssertEqual("R1@v2", next.Resources[0].Alias, "revisions are numbered within their resource");
                    AssertEqual("R2@v1", inspector().Query(session, new ModelContextQuery {
                        RequestEventId = q2.EventId }, CancellationToken.None).Resources[0].Alias,
                        "a second resource starts at v1");
                    var q4 = request("operation-d", "evidence-d", restored, true);
                    var search = inspector().Query(session, new ModelContextQuery { Alias = "R1@v3" }, CancellationToken.None);
                    AssertEqual(q4.EventId, search.Events[0].EventId,
                        "restoring identical bytes creates a new exact revision alias");
                    AssertEqual(ModelContextCurrentness.Current, search.Resources[0].Currentness,
                        "the current authority revision is reported without a live provider read");
                    AssertEqual(second.Revision, search.Resources[0].RevisionMetadata.Parent.Revision, "exact predecessor provenance");
                    AssertEqual(first.Revision, search.Resources[0].RevisionMetadata.RestoredFrom.Revision, "explicit restore origin");
                    AssertEqual(otherRef.Uri, search.Resources[0].RevisionMetadata.Dependencies[0].Resource.Uri,
                        "dependency provenance is retained without reading dependency bytes");
                    AssertEqual("Q1", inspector().Query(session, new ModelContextQuery { Alias = "E1" }, CancellationToken.None).Events[0].Alias,
                        "evidence lookup resolves the saved request");
                    AssertEqual("Q1", inspector().Query(session, new ModelContextQuery { Alias = "O1" }, CancellationToken.None).Events[0].Alias,
                        "operation lookup resolves the saved request");
                    AssertEqual("Q1", inspector().Query(session, new ModelContextQuery { Alias = "Q1" }, CancellationToken.None).Events[0].Alias,
                        "request lookup resolves itself");
                    AssertEqual("R1@v1", inspector().Query(session, new ModelContextQuery { Alias = "R1@v1" }, CancellationToken.None).Resources[0].Alias,
                        "older aliases stay stable after later observations");
                    AssertEqual("Q2", inspector().Query(session, new ModelContextQuery { BeforeSequence = q3.Sequence }, CancellationToken.None)
                        .Events[0].Alias, "pagination does not renumber aliases");
                    store.RegisterView(scope, new ResourceRevisionView(first, "outline", body.Sha256, body,
                        ResourceCoverage.Whole()));
                    var advertised = chats.AppendTrace(session, SessionEventTypes.LlmRequest,
                        new LlmTraceRecord { RequestId = Guid.NewGuid().ToString("N"),
                            ContextMessages = new System.Collections.Generic.List<ContextMessagePresentation> {
                                new ContextMessagePresentation { Role = "tool",
                                    EvidenceLinks = new System.Collections.Generic.List<ContextEvidenceLink> {
                                        new ContextEvidenceLink { ScopeId = scope, EvidenceId = "advertised-view",
                                            Resource = first, View = "text", ReadMetadata = new ResourceReadMetadata(
                                                descriptorAtRead: new ResourceReadDescriptorSnapshot(
                                                    representations: new[] { "text", "outline" })) } } } } },
                        "{}", "application/json", "run", "turn", "advertised");
                    var advertisedRow = inspector().Query(session, new ModelContextQuery {
                        RequestEventId = advertised.EventId }, CancellationToken.None).Resources.Single();
                    AssertTrue(advertisedRow.RetainedViews.Select(view => view.View).OrderBy(view => view)
                        .SequenceEqual(new[] { "outline", "text" }),
                        "read-time descriptor can discover another exact retained view without a live provider read");
                    var rangeBody = PayloadRef.FromBlob(payloads.StoreText("selected range bytes", "text/plain"));
                    var rangePart = PayloadRef.FromBlob(payloads.StoreText("selected range part", "text/plain"));
                    for (var index = 0; index < 23; index++)
                        store.RegisterView(scope, new ResourceRevisionView(first, "text",
                            index == 0 ? rangeBody.Sha256 : body.Sha256, index == 0 ? rangeBody : body,
                            new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: index, end: index + 1),
                            index == 0 ? new[] { rangePart } : null));
                    AssertEqual(ResourceCoverageKinds.Whole, store.GetView(scope, first, "text").Coverage.Kind,
                        "existing known-name lookup still prefers the whole retained view");
                    store.RegisterView(scope, new ResourceRevisionView(second, "text", body.Sha256, body,
                        ResourceCoverage.Whole()));
                    AssertEqual((body.ByteLength + 3) / 4,
                        inspector().Query(session, new ModelContextQuery { RequestEventId = q3.EventId }, CancellationToken.None)
                            .Resources.Single().RetainedViews.Single().EstimatedTextBodyTokens.Value,
                        "known-name metadata exposes the same direct UTF-8 body estimate");
                    Func<ModelContextQuery, ModelContextViewInventoryDto> inventory = query =>
                        inspector().Query(session, query, CancellationToken.None).ViewInventory;
                    var firstPage = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1" });
                    AssertEqual(25, firstPage.SnapshotCount, "inventory includes every registered coverage variant of the exact revision");
                    AssertEqual(20, firstPage.Items.Count, "inventory returns a bounded first page");
                    AssertEqual(20, firstPage.NextOffset.Value, "inventory exposes a continuation offset");
                    AssertEqual("outline", firstPage.Items[1].View, "unobserved registered view is listed");
                    AssertTrue(firstPage.Items[1].EstimatedTextBodyTokens == null,
                        "non-text view names do not claim a model text-body cost");
                    AssertEqual(0L, firstPage.Items[2].Coverage.Start.Value, "coverage variants retain append order");
                    AssertTrue(firstPage.Items[2].EstimatedTextBodyTokens == null,
                        "multipart range payload is not priced as complete text");
                    AssertEqual((body.ByteLength + 3) / 4, firstPage.Items[3].EstimatedTextBodyTokens.Value,
                        "direct UTF-8 range uses the existing uncalibrated bytes-per-four baseline");
                    AssertEqual(1, firstPage.Items[0].PartsCount, "inventory describes retained parts without reading them");
                    AssertTrue(firstPage.Items[0].PayloadRegistered, "inventory distinguishes registered payloads");
                    AssertTrue(!Newtonsoft.Json.JsonConvert.SerializeObject(firstPage).Contains(viewPart.Sha256),
                        "inventory does not expose part CAS addresses");
                    Func<string, string, int, int, ModelContextResponse> verifyVariant = (eventId, alias, ordinal, count) =>
                        inspector().Query(session, new ModelContextQuery { RequestEventId = eventId,
                            VerifyResourceAlias = alias, VerifyViewVariantOrdinal = ordinal,
                            VerifyViewVariantSnapshotCount = count }, CancellationToken.None);
                    var unobserved = verifyVariant(q1.EventId, "R1@v1", 1, firstPage.SnapshotCount);
                    AssertEqual("outline", unobserved.ViewVariantVerification.Variant.View,
                        "an unobserved but registered view can be verified by its inventory ordinal");
                    var selected = verifyVariant(q1.EventId, "R1@v1", 2, firstPage.SnapshotCount);
                    AssertEqual(0L, selected.ViewVariantVerification.Variant.Coverage.Start.Value,
                        "variant verification targets the selected coverage rather than the whole view");
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        selected.ViewVariantVerification.Variant.PayloadAvailability, "selected range payload is verified");
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        selected.ViewVariantVerification.Variant.PartsAvailability.Value, "selected range parts are verified");
                    AssertTrue(selected.ViewVariantVerification.Variant.CheckedUtc.HasValue &&
                        selected.Resources[0].PayloadAvailability == ModelContextPayloadAvailability.Unchecked,
                        "variant verification records its own time without claiming revision-payload verification");
                    var selectedJson = Newtonsoft.Json.JsonConvert.SerializeObject(selected.ViewVariantVerification);
                    AssertTrue(!selectedJson.Contains(rangeBody.Sha256) && !selectedJson.Contains(rangePart.Sha256),
                        "variant verification does not expose retained CAS addresses");
                    var rangePartPath = payloads.PathFor(rangePart.Sha256);
                    var rangePartBytes = System.IO.File.ReadAllBytes(rangePartPath);
                    System.IO.File.Delete(rangePartPath);
                    var missingPart = verifyVariant(q1.EventId, "R1@v1", 2, firstPage.SnapshotCount);
                    AssertEqual(ModelContextPayloadAvailability.Verified,
                        missingPart.ViewVariantVerification.Variant.PayloadAvailability,
                        "a missing part does not invalidate a separately verified selected payload");
                    AssertEqual(ModelContextPayloadAvailability.Unavailable,
                        missingPart.ViewVariantVerification.Variant.PartsAvailability.Value,
                        "a missing selected part is not masked by a valid whole-view payload");
                    System.IO.File.WriteAllBytes(rangePartPath, rangePartBytes);
                    var rangeBodyPath = payloads.PathFor(rangeBody.Sha256);
                    var rangeBodyBytes = System.IO.File.ReadAllBytes(rangeBodyPath);
                    System.IO.File.Delete(rangeBodyPath);
                    var missingBody = verifyVariant(q1.EventId, "R1@v1", 2, firstPage.SnapshotCount);
                    AssertEqual(ModelContextPayloadAvailability.Unavailable,
                        missingBody.ViewVariantVerification.Variant.PayloadAvailability,
                        "selected variant checks its own payload rather than the whole-view payload");
                    System.IO.File.WriteAllBytes(rangeBodyPath, rangeBodyBytes);
                    store.RegisterView(scope, new ResourceRevisionView(first, "internal", body.Sha256, null,
                        ResourceCoverage.Whole()));
                    var secondPage = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1", ViewInventoryOffset = firstPage.NextOffset,
                        ViewInventorySnapshotCount = firstPage.SnapshotCount });
                    AssertEqual(5, secondPage.Items.Count, "continuation ends at the first-page snapshot count");
                    AssertTrue(secondPage.NextOffset == null && secondPage.Items.Last().Ordinal == 24,
                        "newly registered views cannot enter an in-progress inventory");
                    var freshPage = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1" });
                    AssertEqual(26, freshPage.SnapshotCount, "a fresh inventory sees later registrations");
                    var revisited = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 0,
                        ViewInventorySnapshotCount = firstPage.SnapshotCount });
                    AssertEqual(25, revisited.SnapshotCount, "earlier pages can be revisited within the pinned inventory");
                    var nonUtf8 = PayloadRef.FromBlob(payloads.StoreText("opaque", "text/plain; charset=windows-1251"));
                    store.RegisterView(scope, new ResourceRevisionView(first, "source", nonUtf8.Sha256,
                        nonUtf8, ResourceCoverage.Whole()));
                    var unknownCost = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 26,
                        ViewInventorySnapshotCount = 27 });
                    AssertTrue(unknownCost.Items.Single().EstimatedTextBodyTokens == null,
                        "non-UTF-8 charset leaves token cost unknown");
                    var treeBody = PayloadRef.FromBlob(payloads.StoreText("{\"nodes\":[]}",
                        "application/json; charset=utf-8"));
                    store.RegisterView(scope, new ResourceRevisionView(first, "tree", treeBody.Sha256,
                        treeBody, new ResourceCoverage(ResourceCoverageKinds.JsonPath, path: "/items")));
                    var treeCost = inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 27,
                        ViewInventorySnapshotCount = 28 });
                    AssertEqual((treeBody.ByteLength + 3) / 4,
                        treeCost.Items.Single().EstimatedTextBodyTokens.Value,
                        "serialized UTF-8 tree JSON has a retained-body estimate");
                    AssertEqual((treeBody.ByteLength + 3) / 4,
                        verifyVariant(q1.EventId, "R1@v1", 27, 28).ViewVariantVerification.Variant
                            .EstimatedTextBodyTokens.Value,
                        "exact tree variant verification uses the same body estimate");
                    var oldTreeBody = PayloadRef.FromBlob(payloads.StoreText("{\"legacy\":[]}", "application/yaml"));
                    store.RegisterView(scope, new ResourceRevisionView(first, "tree", oldTreeBody.Sha256,
                        oldTreeBody, new ResourceCoverage(ResourceCoverageKinds.YamlPath, path: "/old")));
                    AssertTrue(inventory(new ModelContextQuery { RequestEventId = q1.EventId,
                        ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 28,
                        ViewInventorySnapshotCount = 29 }).Items.Single().EstimatedTextBodyTokens == null,
                        "old tree payload with source MIME does not gain an inferred body estimate");
                    var replayed = new ResourceAuthorityStore(paths).ReadViews(scope, first, 20, 20, firstPage.SnapshotCount);
                    AssertTrue(replayed.Items.Select(view => view.Coverage.Start).SequenceEqual(
                        secondPage.Items.Select(item => item.Coverage.Start)),
                        "append-order continuation is stable after authority-store restart");
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        RequestEventId = q1.EventId, ViewInventoryAlias = "R2@v1" }));
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        ViewInventoryAlias = "R1@v1" }));
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        RequestEventId = q1.EventId, ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 20 }));
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        RequestEventId = q1.EventId, ViewInventoryAlias = "R1@v1", ViewInventoryOffset = -1 }));
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        RequestEventId = q1.EventId, ViewInventoryAlias = "R1@v1", ViewInventoryOffset = 30,
                        ViewInventorySnapshotCount = 26 }));
                    RuntimeThrows<InvalidOperationException>(() => verifyVariant(q1.EventId, "R2@v1", 2, firstPage.SnapshotCount));
                    RuntimeThrows<InvalidOperationException>(() => verifyVariant(q1.EventId, "R1@v1", 25, firstPage.SnapshotCount));
                    RuntimeThrows<ResourceAuthorityConflictException>(() => verifyVariant(q1.EventId, "R1@v1", 2, 100));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session, new ModelContextQuery {
                        RequestEventId = q1.EventId, VerifyResourceAlias = "R1@v1", VerifyViewVariantOrdinal = 2 }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session, new ModelContextQuery {
                        RequestEventId = q1.EventId, VerifyResourceAlias = "R1@v1", VerifyViewVariantSnapshotCount = 25 }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session, new ModelContextQuery {
                        RequestEventId = q1.EventId, VerifyResourceAlias = "R1@v1", VerifyRetainedView = "text",
                        VerifyViewVariantOrdinal = 2, VerifyViewVariantSnapshotCount = 25 }, CancellationToken.None));
                    var foreign = chats.AppendTrace(session, SessionEventTypes.LlmRequest,
                        new LlmTraceRecord { RequestId = Guid.NewGuid().ToString("N"),
                            ContextMessages = new System.Collections.Generic.List<ContextMessagePresentation> {
                                new ContextMessagePresentation { Role = "tool",
                                    EvidenceLinks = new System.Collections.Generic.List<ContextEvidenceLink> {
                                        new ContextEvidenceLink { ScopeId = new ResourceAuthorityScopeId("document", "other-doc"),
                                            EvidenceId = "foreign-evidence", Resource = first, View = "text" } } } } },
                        "{}", "application/json", "run", "turn", "foreign");
                    var foreignRow = inspector().Query(session, new ModelContextQuery {
                        RequestEventId = foreign.EventId }, CancellationToken.None).Resources[0];
                    AssertEqual("R3@v1", foreignRow.Alias, "same URI in a different authority scope gets its own alias");
                    AssertEqual(ModelContextCurrentness.Unknown, foreignRow.Currentness,
                        "inspector does not consult another document's authority");
                    store.RegisterRevision(new ResourceAuthorityScopeId("document", "other-doc"),
                        new ResourceRevisionMetadata(first, body.Sha256, body));
                    store.RegisterView(new ResourceAuthorityScopeId("document", "other-doc"),
                        new ResourceRevisionView(first, "private", body.Sha256, body, ResourceCoverage.Whole()));
                    var foreignVerification = verify(foreign.EventId, "R3@v1");
                    AssertEqual(ModelContextPayloadAvailability.Unknown, foreignVerification.PayloadAvailability,
                        "a saved link cannot authorize another document's payload verification");
                    AssertTrue(foreignVerification.RevisionMetadata == null && foreignVerification.PayloadCheckedUtc == null,
                        "foreign metadata is not exposed by payload verification");
                    AssertTrue(foreignVerification.RetainedViews == null,
                        "foreign scope cannot expose retained view metadata");
                    RuntimeThrows<InvalidOperationException>(() => verifyView(foreign.EventId, "R3@v1", "text"));
                    RuntimeThrows<InvalidOperationException>(() => inventory(new ModelContextQuery {
                        RequestEventId = foreign.EventId, ViewInventoryAlias = "R3@v1" }));
                    RuntimeThrows<InvalidOperationException>(() => verifyVariant(foreign.EventId, "R3@v1", 0, 1));
                    AssertEqual("R1@v1", inspector().Query(session, new ModelContextQuery {
                        Alias = "R1@v1" }, CancellationToken.None).Resources[0].Alias,
                        "new scope does not renumber an older alias");
                    var another = chats.Create("Word", "other-alias-chat", "Other.docx", "Other");
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(another,
                        new ModelContextQuery { Alias = "R1@v1" }, CancellationToken.None));
                    RuntimeThrows<InvalidOperationException>(() => inspector().Query(session,
                        new ModelContextQuery { Alias = "R1@v999" }, CancellationToken.None));
                    System.IO.File.WriteAllText(payloads.PathFor(body.Sha256), "damaged CAS bytes");
                    AssertEqual(ModelContextPayloadAvailability.Unavailable, verify(q1.EventId, "R1@v1").PayloadAvailability,
                        "corrupt CAS never reports verified availability");
                    System.IO.File.Delete(payloads.PathFor(body.Sha256));
                    AssertEqual(ModelContextPayloadAvailability.Unavailable, verify(q1.EventId, "R1@v1").PayloadAvailability,
                        "missing CAS never reports verified availability");
                    payloads.StoreText("same bytes", "text/plain");
                    AssertEqual(ModelContextPayloadAvailability.Verified, verify(q1.EventId, "R1@v1").PayloadAvailability,
                        "explicit repeat verifies repaired bytes instead of caching an old failure");
                    AssertEqual(ModelContextPayloadAvailability.Unchecked, inspector().Query(session,
                        new ModelContextQuery { RequestEventId = q1.EventId }, CancellationToken.None).Resources[0].PayloadAvailability,
                        "verification is not silently persisted as permanent availability");
                    var largePath = System.IO.Path.Combine(paths.ChatBlobDirectory, "verification-source.bin");
                    using (var source = System.IO.File.Create(largePath)) source.SetLength(51L * 1024 * 1024);
                    var largeBody = PayloadRef.FromBlob(payloads.StoreFile(largePath, "application/octet-stream"));
                    var largeRef = new ResourceRef(ResourceStateProvider.Identity(scope, "large").Uri, "large-revision");
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(largeRef, largeBody.Sha256, largeBody));
                    var largeRequest = request("operation-large", "evidence-large", largeRef, true);
                    var largeRow = inspector().Query(session, new ModelContextQuery {
                        RequestEventId = largeRequest.EventId }, CancellationToken.None).Resources[0];
                    AssertEqual(ModelContextPayloadAvailability.Verified, verify(largeRequest.EventId, largeRow.Alias).PayloadAvailability,
                        "streamed verification is independent of the diagnostic download size budget");
                    var emptyBody = PayloadRef.FromBlob(payloads.StoreText("", "text/plain"));
                    var emptyRef = new ResourceRef(largeRef.Uri, "empty-revision");
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(emptyRef, emptyBody.Sha256, emptyBody));
                    var emptyRequest = request("operation-empty", "evidence-empty", emptyRef, true);
                    var emptyRow = inspector().Query(session, new ModelContextQuery {
                        RequestEventId = emptyRequest.EventId }, CancellationToken.None).Resources[0];
                    AssertEqual(ModelContextPayloadAvailability.Verified, verify(emptyRequest.EventId, emptyRow.Alias).PayloadAvailability,
                        "empty retained bytes are verifiable, not absent");
                    using (var cancelled = new CancellationTokenSource())
                    {
                        cancelled.Cancel();
                        RuntimeThrows<OperationCanceledException>(() => inspector().Query(session, new ModelContextQuery {
                            RequestEventId = q1.EventId, VerifyResourceAlias = "R1@v1" }, cancelled.Token));
                    }
                }
            });
        }

        private static void ModelContextSourceAndPagination()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "context-source", "Source.docx", "Source");
                var blobs = new ChatBlobStore(paths);
                var original = PayloadRef.FromBlob(blobs.StoreText("original source", "text/plain"));
                var options = new LlmRequestOptions { TraceSession = session, TraceStepId = "step", TraceModelAttemptId = "attempt" };
                new ModelTracePersistenceService(new ChatEventStoreAdapter(store)).Configure(options);
                options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = "first", PayloadJson = "{}",
                    PayloadContentType = "application/json", ContextMessages = new System.Collections.Generic.List<ContextMessagePresentation> {
                        new ContextMessagePresentation { MessageIndex = 0, Presentation = ContextPresentationKind.Summary,
                            OriginalPayload = original, ToolCallId = "operation-1",
                            EvidenceLinks = new System.Collections.Generic.List<ContextEvidenceLink> {
                                new ContextEvidenceLink { EvidenceId = "evidence-1", Resource = new ResourceRef("rna://vba/source/module", "r1"),
                                    View = "source", ObservedCoverage = new ResourceCoverage(ResourceCoverageKinds.LineRange,
                                        start: 1, end: 2), BodyIncludedInMessage = true } } },
                        new ContextMessagePresentation { SourceMessageId = "archived", Role = "tool",
                            Presentation = ContextPresentationKind.Excluded, ExclusionKind = ContextExclusionKind.Budget,
                            Reason = "Тело источника не вошло в бюджет этого запроса." } } });
                var request = store.ReadEvents(session.Host, session.DocumentKey, session.Id).Single(e => e.Type == SessionEventTypes.LlmRequest);
                for (var i = 0; i < 51; i++) store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "request-" + i }, "{}", "application/json", null, null, null);
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var service = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    var page = service.Query(session, new ModelContextQuery(), CancellationToken.None);
                    AssertEqual(50, page.Events.Count, "bounded attempt page");
                    AssertTrue(page.Events.All(e => e.Trace.ContextMessages == null), "list omits full provenance");
                    var older = service.Query(session, new ModelContextQuery { BeforeSequence = page.NextBeforeSequence }, CancellationToken.None);
                    AssertEqual(2, older.Events.Count, "older attempts retained");
                    var detail = service.Query(session, new ModelContextQuery { RequestEventId = request.EventId }, CancellationToken.None);
                    AssertEqual(ContextPresentationKind.Summary, detail.Events[0].Trace.ContextMessages[0].Presentation, "typed provenance survives trace storage");
                    var stored = detail.Events[0].Trace.ContextMessages[0];
                    AssertEqual("operation-1", stored.ToolCallId, "operation correlation survives event storage");
                    AssertEqual("evidence-1", stored.EvidenceLinks.Single().EvidenceId, "evidence link survives event storage");
                    AssertEqual(ResourceCoverageKinds.LineRange, stored.EvidenceLinks.Single().ObservedCoverage.Kind,
                        "typed observed coverage survives event storage");
                    AssertTrue(detail.Resources.Single().Observations.Single().ReadMetadata == null,
                        "old saved traces never invent separate read-completeness facts");
                    AssertEqual(ContextExclusionKind.Budget, detail.Events[0].Trace.ContextMessages[1].ExclusionKind.Value,
                        "typed compiler exclusion survives saved request and inspector query");
                    var download = service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId, OriginalIndex = 0 }, CancellationToken.None);
                    AssertEqual(original.Sha256, download.Data.Payload.Sha256, "original source is bound to the exact request");
                    data.Close(session.Id, ModelContextInspectorService.Owner, download.Data.LeaseId);
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, new ModelContextPayloadQuery { EventId = request.EventId, OriginalIndex = 1 }, CancellationToken.None));
                }
            });
        }

        private static void ModelContextCompilerPresentation()
        {
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var scope = new ResourceAuthorityScopeId("document", "context-inspector");
                var reference = new ResourceRef("rna://vba/context-inspector/module", "r1");
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(reference, 1) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var full = new ChatMessage { Role = "user", Content = "exact user text" };
                var excluded = new ChatMessage { Role = "assistant", Content = "local only", ExcludeFromModelContext = true };
                var source = PayloadRef.FromBlob(blobs.StoreText("selected lines", "text/plain"));
                var fragmentEvidence = new ResourceEvidence("fragment", scope, reference, "source",
                    new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 2), true, 1, source);
                var descriptor = new ResourceDescriptor { Reference = reference, Provider = "vba", Kind = "module",
                    MimeType = "text/plain", ByteLength = 1234 };
                descriptor.Representations.Add("source");
                descriptor.Capabilities.Add("read");
                descriptor.ViewCapabilities.Add(new ResourceViewCapability("source", supportsOffset: true,
                    maxBatchBytes: 65536));
                var selectedRead = new ResourceReadResult { Resource = descriptor,
                    Representation = "source", Coverage = fragmentEvidence.Coverage, Complete = true,
                    ExtractionProfile = "fixture:extractor-v1" };
                var readProjection = ResourceReadProjection.From(selectedRead, "Module1", "VBA", "document");
                readProjection.SelectionComplete = true;
                readProjection.SourceComplete = false;
                readProjection.SourceCoverage = fragmentEvidence.Coverage;
                fragmentEvidence = new ResourceReadObservation(selectedRead, new[] { fragmentEvidence })
                    .WithReadMetadata(readProjection, new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 6))
                    .Evidence.Single();
                descriptor.Representations.Add("later");
                descriptor.ViewCapabilities.Clear();
                AssertEqual(1, fragmentEvidence.ReadMetadata.DescriptorAtRead.Representations.Count,
                    "read-time views do not follow later descriptor mutations");
                AssertEqual(1, fragmentEvidence.ReadMetadata.DescriptorAtRead.ViewCapabilities.Count,
                    "read-time view capabilities do not follow later descriptor mutations");
                fragmentEvidence = Newtonsoft.Json.JsonConvert.DeserializeObject<ResourceEvidence>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(fragmentEvidence));
                excluded.ResourceEvidence.Add(fragmentEvidence);
                var fragment = new ChatMessage { Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                    Content = "{\"target\":\"Module1\"}", ResultPayload = source,
                    ResourceEvidence = new System.Collections.Generic.List<ResourceEvidence> { fragmentEvidence } };
                var compiler = new ModelContextCompiler(blobs, projection: ModelToolResultProjection.Instance);
                var snapshot = compiler.Compile(authority, new ChatMessage[0], new[] { full, excluded, fragment }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(ContextPresentationKind.Full, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == full.Id).Presentation, "unchanged text is full");
                AssertEqual(ContextPresentationKind.Excluded, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id).Presentation, "excluded is outside wire positions");
                AssertEqual(ContextExclusionKind.Explicit, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id).ExclusionKind.Value,
                    "explicit source exclusion keeps its compiler decision");
                AssertContains(Newtonsoft.Json.JsonConvert.SerializeObject(snapshot.Receipt.Messages),
                    "\"ExclusionKind\":\"Explicit\"", "the typed exclusion decision survives trace serialization");
                AssertEqual(1, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id).EvidenceLinks.Count,
                    "excluded source has one typed evidence link");
                AssertEqual(false, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id)
                    .EvidenceLinks[0].BodyIncludedInMessage.Value, "excluded source has typed evidence without a delivery claim");
                var entry = snapshot.Receipt.Messages.Single(e => e.SourceMessageId == fragment.Id);
                AssertEqual(ContextPresentationKind.Fragment, entry.Presentation, "range coverage determines fragment without text heuristics");
                AssertEqual(source.Sha256, entry.OriginalPayload.Sha256, "original CAS binding remains diagnostic");
                AssertEqual(1, entry.EvidenceLinks.Count, "selected source has one typed evidence link");
                var link = entry.EvidenceLinks[0];
                AssertEqual(fragmentEvidence.EvidenceId, link.EvidenceId, "exact observation ID reaches compiler receipt");
                AssertTrue(scope.Equals(link.ScopeId), "evidence authority scope reaches the saved receipt");
                AssertEqual(reference.Uri, link.Resource.Uri, "exact resource identity is typed");
                AssertEqual(reference.Revision, link.Resource.Revision, "exact resource revision is typed");
                AssertEqual(ResourceCoverageKinds.LineRange, link.ObservedCoverage.Kind,
                    "observed coverage is typed and distinct from delivery status");
                AssertEqual(true, link.ObservationComplete.Value, "observation completeness remains distinct from body delivery");
                AssertEqual(6L, link.ReadMetadata.RequestedCoverage.End.Value, "requested bounds are retained beyond actual EOF");
                AssertEqual(2L, link.ObservedCoverage.End.Value, "actual coverage is not replaced by requested bounds");
                AssertTrue(link.ReadMetadata.SelectionComplete == true && link.ReadMetadata.RepresentationComplete == true &&
                    link.ReadMetadata.SourceComplete == false, "complete selection and selected target do not claim a complete source");
                AssertEqual("module", link.ReadMetadata.DescriptorAtRead.Kind,
                    "saved receipt retains the provider-reported kind at accepted read");
                AssertEqual(1234L, link.ReadMetadata.DescriptorAtRead.ByteLength.Value,
                    "source byte length is diagnostic metadata, not a measured token cost");
                AssertEqual(65536, link.ReadMetadata.DescriptorAtRead.ViewCapabilities.Single().MaxBatchBytes.Value,
                    "saved receipt retains declared view bounds");
                AssertEqual("fixture:extractor-v1", link.ReadMetadata.ExtractionProfile,
                    "saved receipt retains derived-view extraction provenance");
                AssertTrue(snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id)
                    .EvidenceLinks.Single().ReadMetadata.SourceComplete == false, "excluded body retains read metadata");
                AssertEqual(true, link.BodyIncludedInMessage.Value, "verified synthetic payload hydration proves body delivery");
                var wire = Newtonsoft.Json.JsonConvert.SerializeObject(
                    new LlmMessageBuilder().Build(snapshot.Messages, new AppSettings()).Messages);
                AssertTrue(!wire.Contains("\"EvidenceLinks\"") && !wire.Contains("\"ObservedCoverage\"") &&
                    !wire.Contains("\"ScopeId\"") && !wire.Contains("\"ObservationComplete\"") &&
                    !wire.Contains("\"ExclusionKind\"") && !wire.Contains("\"ReadMetadata\"") &&
                    !wire.Contains("\"descriptorAtRead\"") && !wire.Contains("fixture:extractor-v1"),
                    "typed diagnostics do not enter provider messages");
                var changed = LlmClient.DescribeWireMessages(new[] { snapshot.Messages.First(m => m.Id == fragment.Id) },
                    snapshot.Receipt, new[] { true });
                AssertTrue(!changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().BodyIncludedInMessage.HasValue,
                    "later wire projection invalidates body-delivery proof but keeps source link");
                AssertTrue(scope.Equals(changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().ScopeId) &&
                    changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().ObservationComplete == true,
                    "wire attribution retains scope and observation coverage when body delivery becomes unknown");
                var changedLink = changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single();
                AssertTrue(changedLink.ReadMetadata.SelectionComplete == true && changedLink.ReadMetadata.SourceComplete == false,
                    "transport changes delivery proof without rewriting observation completeness");
                var chats = new ChatStore(paths);
                var session = chats.Create("Word", "read-metadata", "Metadata.docx", "Read metadata");
                var request = chats.AppendTrace(session, SessionEventTypes.LlmRequest, new LlmTraceRecord {
                    RequestId = "read-metadata-request", ContextMessages = changed }, "{}", "application/json", "run", "turn", "call");
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var saved = new ModelContextInspectorService(new ChatEventStoreAdapter(new ChatStore(paths)), blobs, plane)
                        .Query(session, new ModelContextQuery { RequestEventId = request.EventId }, CancellationToken.None);
                    var observation = saved.Resources.Single().Observations.Single(item => item.BodyIncludedInMessage == null);
                    AssertTrue(observation.ReadMetadata.SelectionComplete == true && observation.ReadMetadata.SourceComplete == false &&
                        observation.BodyIncludedInMessage == null, "saved trace and inspector preserve independent completeness after restart");
                    AssertEqual(6L, observation.ReadMetadata.RequestedCoverage.End.Value, "inspector receives requested bounds");
                    AssertEqual(2L, observation.ReadMetadata.SourceCoverage.End.Value, "inspector receives source coordinates");
                    AssertEqual("vba", observation.ReadMetadata.DescriptorAtRead.Provider,
                        "inspector receives the accepted read's provider claim after restart");
                    AssertEqual("source", observation.ReadMetadata.DescriptorAtRead.ViewCapabilities.Single().View,
                        "inspector receives the accepted read's declared view after restart");
                    AssertEqual("fixture:extractor-v1", observation.ReadMetadata.ExtractionProfile,
                        "inspector receives the saved extraction profile after restart");
                }
                var mutation = ContinuityResult("saved", "common.vba_write_module", RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Saved", "{\"changed\":true}"));
                mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged, verification: "read-back");
                var folded = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(2, folded.Messages.Count, "latest operation is a pair");
                AssertEqual(ContextPresentationKind.Full, folded.Receipt.Messages.Single(e => e.SourceMessageId == mutation.Id).Presentation, "short recent result is complete, not a summary");
                AssertEqual(mutation.ToolCallId, folded.Receipt.Messages.Single(e => e.SourceMessageId == mutation.Id).ToolCallId,
                    "exact tool operation ID reaches the result receipt");
            });
        }

        private static void ModelContextFullPreview()
        {
            bool truncated;
            var content = new string('ж', 600000) + "😀";
            var messages = new[] { new ChatMessage { Role = "user", Content = content } };
            var raw = PromptContextInspectorService.BuildRawRequest("chat", "test", messages, new LlmRequestOptions(), out truncated, true);
            AssertTrue(!truncated, "full preview has no 512K clipping");
            AssertEqual(content, (string)JObject.Parse(raw)["messages"][0]["content"], "full preview content roundtrip");
        }
    }
}
