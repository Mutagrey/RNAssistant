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
        private static void ModelRequestSchemaDuplicationModesAndFallback()
        {
            var tool = V4ReadTool();
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
            driftedSchema.SelectToken("properties.tool_calls.items.anyOf[0].properties.name.const")
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
                    { Content = "{\"message\":\"Done.\",\"action\":\"done\",\"tool_calls\":[]}" });
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
            AssertEqual(repairMessages[1], repairMessages[2], "fallback reuses the exact repair message");
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
                    { Content = "{\"message\":\"Done.\",\"action\":\"done\",\"tool_calls\":[]}" });
            });
            AssertTrue(objectProtocol.GetResponseAsync(objectRequest, null, CancellationToken.None)
                .GetAwaiter().GetResult().Failure == null, "json_object mode accepts the shared v5 response");
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
                        AssertTrue(requestSizes[AgentResponseModes.JsonSchema] < requestSizes[AgentResponseModes.JsonObject],
                            "representative strict request is smaller than fallback-ready json_object request");
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
                var body = PayloadRef.FromBlob(payloads.StoreText("same bytes", "text/plain"));
                foreach (var reference in new[] { first, second, restored })
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(reference, body.Sha256, body,
                        parent: reference == restored ? second : null, restoredFrom: reference == restored ? first : null,
                        dependencies: new[] { new ResourceDependency(otherRef, "text", ResourceCoverage.Whole(), "source") }));
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
                    AssertTrue(original.Resources[0].PayloadCheckedUtc == null, "no invented verification timestamp");
                    Func<string, string, ModelContextResourceDto> verify = (eventId, alias) => inspector().Query(session,
                        new ModelContextQuery { RequestEventId = eventId, VerifyResourceAlias = alias }, CancellationToken.None)
                        .Resources.Single(row => row.Alias == alias);
                    var verified = verify(q1.EventId, "R1@v1");
                    AssertEqual(ModelContextPayloadAvailability.Verified, verified.PayloadAvailability, "explicit CAS verification");
                    AssertTrue(verified.PayloadCheckedUtc.HasValue, "verification has a time rather than perpetual availability");
                    AssertEqual(body.Sha256, verified.RevisionMetadata.Payload.Sha256, "exact authority payload supplies verification");
                    AssertEqual(ModelContextCurrentness.Superseded, verified.Currentness, "CAS validity does not move the head");
                    AssertTrue(verified.Observations[0].BodyIncludedInMessage == false, "CAS validity does not imply delivery");
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
                    var foreignVerification = verify(foreign.EventId, "R3@v1");
                    AssertEqual(ModelContextPayloadAvailability.Unknown, foreignVerification.PayloadAvailability,
                        "a saved link cannot authorize another document's payload verification");
                    AssertTrue(foreignVerification.RevisionMetadata == null && foreignVerification.PayloadCheckedUtc == null,
                        "foreign metadata is not exposed by payload verification");
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
                AssertEqual(true, link.BodyIncludedInMessage.Value, "verified synthetic payload hydration proves body delivery");
                var wire = Newtonsoft.Json.JsonConvert.SerializeObject(
                    new LlmMessageBuilder().Build(snapshot.Messages, new AppSettings()).Messages);
                AssertTrue(!wire.Contains("\"EvidenceLinks\"") && !wire.Contains("\"ObservedCoverage\"") &&
                    !wire.Contains("\"ScopeId\"") && !wire.Contains("\"ObservationComplete\"") &&
                    !wire.Contains("\"ExclusionKind\""), "typed diagnostics do not enter provider messages");
                var changed = LlmClient.DescribeWireMessages(new[] { snapshot.Messages.First(m => m.Id == fragment.Id) },
                    snapshot.Receipt, new[] { true });
                AssertTrue(!changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().BodyIncludedInMessage.HasValue,
                    "later wire projection invalidates body-delivery proof but keeps source link");
                AssertTrue(scope.Equals(changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().ScopeId) &&
                    changed.Single(e => e.MessageIndex.HasValue).EvidenceLinks.Single().ObservationComplete == true,
                    "wire attribution retains scope and observation coverage when body delivery becomes unknown");
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
