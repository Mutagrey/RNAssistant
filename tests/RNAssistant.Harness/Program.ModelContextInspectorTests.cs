using RNAssistant.Core.Services;
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
            var fullPrompt = accepted[0].Content;
            AssertContains(fullPrompt, "\"parameters\"", "accepted prompt retains exact fallback contracts");
            var options = ModelProtocolWire.CreateRequestOptions(AgentResponseModes.JsonSchema, tools);
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
            AssertEqual(repairMessages[1], repairMessages[2], "fallback reuses the exact repair message");
            AssertContains(wirePrompts[0], "\"description\"", "strict prompt retains descriptions");
            AssertContains(wirePrompts[0], "\"safety\"", "strict prompt retains safety policy");
            AssertContains(wirePrompts[0], "\"office_tool_policy\"", "strict prompt retains document policy");
            AssertTrue(options.ResponseSchemaJson == null, "fallback clears strict response schema");
            AssertEqual(fullPrompt, accepted[0].Content, "wire projection does not mutate accepted history");

            var objectOptions = ModelProtocolWire.CreateRequestOptions(AgentResponseModes.JsonObject, tools);
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
                const string extensionId = "common.html_workspace_write_file";
                var catalog = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var store = new ChatStore(FixturePaths.Value);
                var session = store.Create("Excel", "size-document", "Size.xlsx", "Size");
                session.Mode = ChatModes.Agent;
                session.LastRun = new ChatRunRecord { RunId = "size-run", TurnId = "size-turn" };
                store.Save(session);
                var pack = CallableToolPack.Create(ChatModes.Agent, "Excel", session.LastRun.RunId, catalog);
                var evidence = ReadSchemaEvidence(executor, catalog, extensionId, "size-read");
                evidence.RunId = session.LastRun.RunId;
                AssertTrue(pack.StageReadResult(evidence), "representative extension stages from exact schema evidence");
                var admission = pack.PreparePending((tools, state) => true);
                new ToolPackAdmissionJournal(new ChatEventStoreAdapter(store), session)
                    .Append(admission, "size-step");
                pack.Publish(admission);
                AssertEqual(1, pack.OptionalSchemaCount, "representative pack contains one loaded extension");
                Console.WriteLine("REQUEST_PACK core=" + (pack.Tools.Count - pack.OptionalSchemaCount) +
                    " extension=" + extensionId);

                var blobs = new ChatBlobStore(FixturePaths.Value);
                using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), blobs, data);
                    var requestSizes = new System.Collections.Generic.Dictionary<string, int>();
                    foreach (var mode in new[] { AgentResponseModes.JsonSchema, AgentResponseModes.JsonObject })
                    {
                        var settings = new AppSettings { Model = "request-size-fixture", StreamResponses = false,
                            AgentResponseMode = mode, ContextWindowOverrideTokens = 131072 };
                        var messages = new ConversationPromptComposer().BuildRequiredMessages(ChatModes.Agent,
                            "Inspect the workbook and update the report.", adapter, pack.Tools, new SkillDefinition[0],
                            new DocumentContext(), settings, session, null, false, 0, pack.CapabilityContext(null));
                        messages.Insert(1, admission.StateMessage);
                        var options = ModelProtocolWire.CreateRequestOptions(mode, pack.Tools);
                        var wireMessages = mode == AgentResponseModes.JsonSchema
                            ? ConversationPromptComposer.ProjectJsonSchemaMessages(messages, pack.Tools,
                                options.ResponseSchemaJson)
                            : messages;
                        var api = new LlmMessageBuilder().Build(wireMessages, settings, options);
                        var body = LlmClient.BuildRequestBody(settings, api.Messages, api.EstimatedPromptTokens,
                            options);
                        var bytes = LlmHttpTransport.SerializeJson(body);
                        options.TraceSession = session;
                        new ModelTracePersistenceService(new ChatEventStoreAdapter(store)).Configure(options);
                        options.TraceSink(new LlmTraceRecord { Type = "request", RequestId = mode,
                            PayloadUtf8Bytes = bytes, PayloadContentType = "application/json" });
                        var request = inspector.Query(session, new ModelContextQuery(), CancellationToken.None)
                            .Events.First(item => item.Trace.RequestId == mode);
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
                        AssertEqual(mode == AgentResponseModes.JsonSchema ? 0 : pack.Tools.Count,
                            ((JArray)runtime["tools"]).Count(item => item.SelectToken("function.parameters") != null),
                            "strict wire omits duplicated parameters; fallback-ready prompt retains them");
                        var marker = "\"tools\":" + toolsJson;
                        AssertContains(content, marker, "exact prompt contains the loaded callable pack");
                        var without = (JObject)exact.DeepClone();
                        without["messages"][0]["content"] = content.Replace(marker, "\"tools\":[]");
                        var toolsBytes = retained.Length - LlmHttpTransport.SerializeJson(without).Length;
                        var otherMessagesBytes = messagesBytes - toolsBytes;
                        var otherFieldsBytes = retained.Length - messagesBytes - schemaBytes;
                        Console.WriteLine("REQUEST_SIZE " + mode + " tools=" + toolsBytes + " schema=" + schemaBytes +
                            " otherMessages=" + otherMessagesBytes + " otherFields=" + otherFieldsBytes +
                            " total=" + retained.Length + " estimatedTokens=" +
                            ModelContextBudget.EstimateTextTokens(Encoding.UTF8.GetString(retained), settings) +
                            " sectionTokens=" + string.Join(",", new[] { toolsBytes, schemaBytes,
                                otherMessagesBytes, otherFieldsBytes }.Select(size => (size + 3) / 4)));
                    }
                    AssertTrue(requestSizes[AgentResponseModes.JsonSchema] < requestSizes[AgentResponseModes.JsonObject],
                        "representative strict request is smaller than fallback-ready json_object request");
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
                        new ContextMessagePresentation { MessageIndex = 0, Presentation = ContextPresentationKind.Summary, OriginalPayload = original } } });
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
                var fragment = new ChatMessage { Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                    Content = "{\"target\":\"Module1\"}", ResultPayload = source,
                    ResourceEvidence = new System.Collections.Generic.List<ResourceEvidence> { new ResourceEvidence("fragment", scope,
                        reference, "source", new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 2), true, 1, source) } };
                var compiler = new ModelContextCompiler(blobs, projection: ModelToolResultProjection.Instance);
                var snapshot = compiler.Compile(authority, new ChatMessage[0], new[] { full, excluded, fragment }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(ContextPresentationKind.Full, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == full.Id).Presentation, "unchanged text is full");
                AssertEqual(ContextPresentationKind.Excluded, snapshot.Receipt.Messages.Single(e => e.SourceMessageId == excluded.Id).Presentation, "excluded is outside wire positions");
                var entry = snapshot.Receipt.Messages.Single(e => e.SourceMessageId == fragment.Id);
                AssertEqual(ContextPresentationKind.Fragment, entry.Presentation, "range coverage determines fragment without text heuristics");
                AssertEqual(source.Sha256, entry.OriginalPayload.Sha256, "original CAS binding remains diagnostic");
                var mutation = ContinuityResult("saved", "common.vba_write_module", RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Saved", "{\"changed\":true}"));
                mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged, verification: "read-back");
                var folded = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation }, null,
                    new RNAssistant.Core.Tools.ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(ContextPresentationKind.Summary, folded.Receipt.Messages.First(e => e.MessageIndex.HasValue).Presentation, "folded operation is summary");
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
