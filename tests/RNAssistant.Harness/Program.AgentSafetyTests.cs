using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DefaultPromptsAreStructuredMarkdown()
        {
            var settings = new AppSettings();
            AssertTrue(settings.SystemPrompt.StartsWith("# RNAssistant Agent", StringComparison.Ordinal), "agent prompt Markdown heading");
            AssertContains(settings.SystemPrompt, "## Response contract", "agent prompt structured section");
            AssertContains(settings.SystemPrompt, "\"message\"", "agent prompt requires the visible message field");
            AssertContains(settings.SystemPrompt, "empty array ends it", "agent prompt defines the structural terminal condition");
            AssertTrue(settings.AgentToolsPrompt.StartsWith("# Agent tool policy", StringComparison.Ordinal), "tool prompt is separate Markdown");
            AssertContains(settings.AgentToolsPrompt, "non-empty `tool_calls`", "tool prompt couples calls to execution");
            AssertContains(settings.AgentToolsPrompt, "optional exact `resources`", "tool prompt explains externalized results");
            AssertTrue(settings.AgentSkillsPrompt.StartsWith("# Agent skill policy", StringComparison.Ordinal), "skill prompt is separate Markdown");
            AssertContains(settings.AgentSkillsPrompt, "metadata only", "skill catalog is explicitly not loaded guidance");
            AssertContains(settings.AgentSkillsPrompt, "package `revision`", "skill prompt describes revisions");
            AssertContains(settings.AgentSkillsPrompt, "`loaded=true`", "skill prompt defines explicit loaded evidence");
            AssertContains(settings.AgentSkillsPrompt, "do not retry unchanged", "skill prompt prevents truncated skill loops");
            AssertContains(settings.AgentSkillsPrompt, "referencePath", "skill prompt explains progressive reference reads");
            AssertTrue(settings.ChatSystemPrompt.StartsWith("# RNAssistant Chat", StringComparison.Ordinal), "chat prompt Markdown heading");
            AssertContains(settings.ChatSystemPrompt, "common.resources_*", "chat prompt documents read-only resource access");
            AssertContains(settings.ChatSystemPrompt, "## Response contract", "chat uses the structured response envelope");
            AssertContains(settings.ChatSystemPrompt, "multimodal model", "chat prompt keeps current media direct when supported");
            AssertTrue(settings.ContextCompactionPrompt.StartsWith("# Context compaction", StringComparison.Ordinal), "compaction prompt Markdown heading");
            AssertContains(settings.ContextCompactionPrompt, "Skill ids and revisions", "compaction preserves pending skill references");
            AssertTrue(settings.ChatTitlePrompt.StartsWith("# Chat title", StringComparison.Ordinal), "title prompt Markdown heading");
            AssertTrue(settings.AttachmentAnalysisPrompt.StartsWith("# Attachment analysis", StringComparison.Ordinal), "attachment worker prompt is editable Markdown");
            AssertEqual(AppSettings.DefaultMaxTokens, settings.MaxTokens, "long-run output token default");
            AssertEqual(AppSettings.DefaultRequestTimeoutSeconds, settings.RequestTimeoutSeconds, "long-run request timeout default");
            AssertEqual(AppSettings.DefaultMaxAgentIterations, settings.MaxAgentIterations, "long-run iteration default");
            AssertEqual(AppSettings.DefaultMaxAgentFormatRetries, settings.MaxAgentFormatRetries, "long-run format retry default");
            AssertEqual(AppSettings.DefaultMaxAgentToolSteps, settings.MaxAgentToolSteps, "long-run tool step default");
            AssertTrue(settings.ScreenCaptureProtectionEnabled, "screen capture protection default");
            AssertEqual(ReasoningRequestModes.ChatTemplateKwargs, settings.ReasoningRequestMode, "reasoning request mode default");
            AssertEqual(string.Empty, settings.BaseUrl, "base URL default");
            AssertEqual("/v1/models", settings.ModelsConfigUrl, "models endpoint default");
            AssertEqual(string.Empty, settings.Model, "model default");
            AssertEqual(5, AppSettings.DefaultMaxImagesPerPrompt, "configured image count default");
            AssertEqual(AppSettings.DefaultMaxImagesPerPrompt, ModelContextBudget.MaxImagesPerPrompt(settings), "image count default");
            settings.BaseUrl = "http://127.0.0.1:8000/v1";
            AssertEqual("http://127.0.0.1:8000/v1/models", LlmClient.BuildModelsConfigUrl(settings), "relative models endpoint");
        }

        private static void AgentSupportsSelectableResponseFormats()
        {
            AssertEqual(3, AgentResponseProtocol.CurrentVersion,
                "conversation response protocol cutover version");
            var settings = new AppSettings { StreamResponses = false };
            var messages = new List<object> { new { role = "user", content = "test" } };
            var objectBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonObject
            });
            AssertEqual("json_object", (string)objectBody.SelectToken("response_format.type"), "json_object request type");

            var schemaJson = AgentResponseSchemaBuilder.Build(new ToolDefinition[0]);
            var schemaBody = LlmClient.BuildRequestBody(settings, messages, 10, new LlmRequestOptions
            {
                ResponseFormat = LlmResponseFormats.JsonSchema,
                ResponseSchemaName = AgentResponseSchemaBuilder.SchemaName,
                ResponseSchemaJson = schemaJson
            });
            AssertEqual("json_schema", (string)schemaBody.SelectToken("response_format.type"), "json_schema request type");
            AssertEqual(AgentResponseSchemaBuilder.SchemaName,
                (string)schemaBody.SelectToken("response_format.json_schema.name"), "schema name");
            AssertTrue(schemaBody.SelectToken("response_format.json_schema.strict").Value<bool>(), "strict response schema");
        }

        private static void AgentJsonSchemaMirrorsToolContracts()
        {
            var tool = new ToolDefinition
            {
                Id = "excel.read_range",
                Description = "Read cells.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"range\":{\"type\":\"string\",\"description\":\"A1 range.\"}," +
                    "\"sheet\":{\"type\":\"string\",\"description\":\"Optional sheet name.\"}," +
                    "\"mode\":{\"type\":\"string\",\"description\":\"Read mode.\",\"default\":\"values\",\"enum\":[\"values\",\"formulas\"]}" +
                    "},\"required\":[\"range\"],\"additionalProperties\":false}"
            };
            var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
            var rootRequired = schema["required"] as JArray;
            AssertTrue(rootRequired != null &&
                rootRequired.Values<string>().SequenceEqual(new[] { "message", "tool_calls" }),
                "strict response schema exposes only the canonical root fields");
            AssertTrue(schema.SelectToken("properties.status") == null,
                "model-facing response schema does not expose runtime status");
            var call = schema.SelectToken("properties.tool_calls.items.anyOf[0]");
            AssertEqual("excel.read_range", (string)call.SelectToken("properties.name.const"), "exact tool name in schema");
            AssertEqual("string", (string)call.SelectToken("properties.arguments.properties.range.type"), "tool argument schema copied");
            var optionalSheetType = call.SelectToken("properties.arguments.properties.sheet.type") as JArray;
            AssertTrue(optionalSheetType != null && optionalSheetType.Values<string>().Contains("null"),
                "strict response schema makes optional arguments nullable");
            var optionalModeEnum = call.SelectToken("properties.arguments.properties.mode.enum") as JArray;
            AssertTrue(optionalModeEnum != null && optionalModeEnum.Any(item => item.Type == JTokenType.Null),
                "nullable optional enum accepts null");
            var strictRequired = call.SelectToken("properties.arguments.required") as JArray;
            AssertTrue(strictRequired != null && strictRequired.Values<string>().Contains("sheet"),
                "strict response schema still lists every property as required");
            AssertTrue(call.SelectToken("properties.arguments.additionalProperties").Value<bool>() == false, "tool arguments remain strict");
            AssertTrue(schema["additionalProperties"].Value<bool>() == false, "agent response root is strict");
        }

        private static void AgentJsonSchemaSupportsTypeNamedArguments()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.tools_upsert");
                var schema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { tool }));
                AssertEqual("string",
                    (string)schema.SelectToken("properties.tool_calls.items.anyOf[0].properties.arguments.properties.parameters.properties.type.type"),
                    "schema property named type");

                var patchTool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.vba_apply_patch");
                var patchSchema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { patchTool }));
                var exactReplace = patchSchema.SelectToken(
                    "properties.tool_calls.items.anyOf[0].properties.arguments.properties.patch.items") as JObject;
                AssertTrue(exactReplace != null, "patch schema exposes one exact replacement contract");
                AssertEqual("replace", (string)exactReplace.SelectToken("properties.op.enum[0]"),
                    "exact replacement is the only VBA patch operation");
                AssertEqual(3, ((JObject)exactReplace["properties"]).Properties().Count(),
                    "exact replacement exposes only op, find, and text");
                AssertTrue(exactReplace.SelectToken("properties.startLine") == null &&
                    exactReplace.SelectToken("properties.pattern") == null,
                    "line-number and regex patch fields are absent from the model schema");

                var restoreTool = executor.GetControllerTools().Single(candidate => candidate.Id == "common.vba_restore_backup");
                var restoreSchema = JObject.Parse(AgentResponseSchemaBuilder.Build(new[] { restoreTool }));
                var restoreVariants = restoreSchema.SelectToken(
                    "properties.tool_calls.items.anyOf[0].properties.arguments.anyOf") as JArray;
                AssertEqual(2, restoreVariants == null ? 0 : restoreVariants.Count,
                    "restore schema requires either backup id or module name");
                var backupVariant = restoreVariants.OfType<JObject>().Single(item =>
                    item.SelectToken("properties.backupId.type").Type == JTokenType.String);
                var optionalRestoreModuleType = backupVariant.SelectToken("properties.moduleName.type") as JArray;
                AssertTrue(optionalRestoreModuleType != null && optionalRestoreModuleType.Values<string>().Contains("null"),
                    "irrelevant restore selector is nullable in strict output");

                var strictPatchArguments = new JObject
                {
                    ["moduleName"] = "Module1",
                    ["patch"] = new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "Old",
                        ["text"] = "New"
                    })
                };
                JObject runtimePatchSchema;
                string parseError;
                AssertTrue(ToolSchemaSupport.TryParse(patchTool, out runtimePatchSchema, out parseError),
                    "runtime patch schema parses: " + parseError);
                ToolSchemaSupport.RemoveOptionalNulls(strictPatchArguments, runtimePatchSchema);
                AssertEqual(3, ((JObject)((JArray)strictPatchArguments["patch"])[0]).Properties().Count(),
                    "exact patch arguments remain unchanged by strict normalization");
            });
        }

        private static void AgentSupportsSelectableToolResultRoles()
        {
            var call = new AgentToolCall
            {
                Id = "call_1",
                Name = "excel.read_range",
                Arguments = new Dictionary<string, object> { ["range"] = "A1" }
            };
            var command = new ToolCommand { ToolId = call.Name, ToolCallId = call.Id };
            var result = ToolResult.Ok("read", "{\"value\":1}");

            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer })
            {
                var callMessage = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, role);
                var resultMessage = AgentJsonProtocol.CreateToolResultMessage(command, result, role);
                AssertTrue(callMessage.ToolCalls.Count == 0, role + " uses JSON envelope history");
                AssertTrue(callMessage.Content.IndexOf("\"status\"", StringComparison.Ordinal) < 0,
                    role + " replays only message and tool_calls");
                AssertEqual(AgentResponseProtocol.CurrentVersion, callMessage.ResponseProtocolVersion,
                    role + " stores response protocol version");
                AssertEqual(AgentResponseStatuses.InProgress, callMessage.ResponseStatus,
                    role + " stores response status");
                AssertEqual(role, resultMessage.Role, role + " result role");
                AssertContains(resultMessage.Content, "TOOL_RESULT:", role + " result prefix");
            }

            var nativeCall = AgentJsonProtocol.CreateToolCallMessage(call, "Reading.", null, ToolResultRoles.Tool);
            var nativeResult = AgentJsonProtocol.CreateToolResultMessage(command, result, ToolResultRoles.Tool);
            var api = new LlmMessageBuilder().Build(new[] { nativeCall, nativeResult }, new AppSettings());
            var assistant = (JObject)api.Messages[0];
            var toolMessage = (JObject)api.Messages[1];
            AssertEqual("assistant", (string)assistant["role"], "native call role");
            AssertEqual(AgentResponseProtocol.CurrentVersion, nativeCall.ResponseProtocolVersion,
                "native call stores response protocol version");
            AssertEqual(AgentResponseStatuses.InProgress, nativeCall.ResponseStatus,
                "native call stores response status");
            AssertEqual("call_1", (string)assistant.SelectToken("tool_calls[0].id"), "native call id");
            AssertEqual("tool", (string)toolMessage["role"], "native result role");
            AssertEqual("call_1", (string)toolMessage["tool_call_id"], "native result matches call");
            AssertTrue(string.IsNullOrWhiteSpace((string)toolMessage["name"]) == false, "native tool name is API-safe");
        }

        private static void AgentJsonSchemaFallbackIsRequestLocal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var formats = new List<string>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    formats.Add(options.ResponseFormat);
                    if (formats.Count == 1)
                    {
                        throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "json_schema unsupported");
                    }
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"status\":\"completed\",\"message\":\"Done.\",\"tool_calls\":[]}"
                    });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    FallbackToJsonObject = true
                };
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Test.", NewSession(adapter), NewContext(adapter), settings, new ToolDefinition[0],
                    null, null, null, CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "fallback completes request");
                AssertEqual(2, formats.Count, "fallback makes one retry");
                AssertEqual(LlmResponseFormats.JsonSchema, formats[0], "selected format is tried first");
                AssertEqual(LlmResponseFormats.JsonObject, formats[1], "fallback uses json_object");
                AssertEqual(AgentResponseModes.JsonSchema, settings.AgentResponseMode, "saved selection is unchanged");
            });
        }

        private static void AgentToolResultDataIsBounded()
        {
            var command = new ToolCommand { ToolId = "excel.read_range", ToolCallId = "call_large" };
            var data = JsonConvert.SerializeObject(new { value = new string('x', 50000) + "TOOL_RESULT_END" });
            var toolResult = ToolResult.Ok("read", data);
            var result = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, toolResult, 256));

            AssertTrue(result.SelectToken("data.truncated").Value<bool>(), "oversized data is marked truncated");
            AssertTrue(result.SelectToken("data.original_chars").Value<int>() > 49000, "original size retained");
            AssertTrue(((string)result.SelectToken("data.preview") ?? string.Empty).Length < 1000, "preview is bounded");
            AssertEqual("call_large", (string)result["tool_call_id"], "tool call id retained");

            var resourceSession = new ChatSession();
            var resourceArtifact = ToolResultResourceService.ExternalizeIfNeeded(
                resourceSession,
                command,
                toolResult,
                256,
                new AppSettings());
            AssertTrue(resourceArtifact != null && resourceArtifact.Kind == ChatArtifactKinds.ToolResult,
                "oversized generic result becomes a tool-result resource");
            var resourceEnvelope = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, toolResult, 256));
            var resourceUri = (string)resourceEnvelope.SelectToken("resources[0].uri");
            AssertEqual(ArtifactUri(resourceSession, resourceArtifact), resourceUri,
                "bounded envelope exposes the exact durable result reference");
            AssertEqual("result", (string)resourceEnvelope.SelectToken("resources[0].relation"),
                "externalized full result is distinguished from other produced resources");
            AssertContains((string)resourceEnvelope.SelectToken("data.hint"), "common.resources_read",
                "bounded envelope tells the model how to read the externalized result");
            var firstPage = ReadResource(
                new ResourceGatewayService(), resourceSession, resourceUri, ResourceRepresentations.Text, null, 32000).Result;
            var secondPage = ReadResource(
                new ResourceGatewayService(), resourceSession, resourceUri, ResourceRepresentations.Text, firstPage.NextCursor, 32000).Result;
            AssertContains(firstPage.Text + secondPage.Text, "TOOL_RESULT_END",
                "externalized result remains pageable through the resource gateway");
            WithTempPaths(paths =>
            {
                resourceSession.Host = "Excel";
                resourceSession.DocumentKey = "tool-result-resource";
                resourceSession.DocumentTitle = "ToolResult.xlsx";
                resourceSession.Messages.Add(new ChatMessage
                {
                    Role = "developer",
                    Content = "TOOL_RESULT resource",
                    ProtocolMessage = true,
                    RunId = "run_tool_result",
                    ResourceRefs = new List<ResourceRef> { ArtifactReference(resourceSession, resourceArtifact) }
                });
                new ChatStore(paths).Save(resourceSession);
                var durable = new ChatStore(paths).Load(
                    resourceSession.Host,
                    resourceSession.DocumentKey,
                    resourceSession.Id);
                var durableArtifact = durable.Artifacts.Single(item => item.Id == resourceArtifact.Id);
                AssertTrue(!string.IsNullOrWhiteSpace(durableArtifact.ContentSha256),
                    "tool-result resource body is externalized to CAS");
                AssertContains(durableArtifact.InlineText, "TOOL_RESULT_END",
                    "tool-result resource body survives event replay and CAS hydration");
            });

            var producedArtifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.Markdown,
                Title = "Produced resource",
                InlineText = "produced"
            };
            resourceSession.Artifacts.Add(producedArtifact);
            var resultWithProducedResource = ToolResult.Ok("read", data);
            resultWithProducedResource.ModelResourceRefs = new[] { ArtifactReference(resourceSession, producedArtifact) };
            var externalizedAlongsideProduced = ToolResultResourceService.ExternalizeIfNeeded(
                resourceSession, command, resultWithProducedResource, 256, new AppSettings());
            AssertTrue(externalizedAlongsideProduced != null && resultWithProducedResource.ModelResourceRefs.Count == 2,
                "a produced-resource reference does not suppress externalization of an independent oversized result");

            var chartSession = new ChatSession
            {
                Host = "Excel",
                DocumentKey = "chart-resource",
                DocumentTitle = "Chart.xlsx"
            };
            var chartData = JsonConvert.SerializeObject(new
            {
                type = "rnassistant.chart",
                title = "Sales",
                rows = new[] { new { month = "Jan", value = 10 } }
            });
            var chartResult = ToolResult.Ok("chart", chartData);
            var chartArtifact = ToolResultResourceService.ExternalizeIfNeeded(
                chartSession, command, chartResult, 10000, new AppSettings());
            AssertEqual(ChatArtifactKinds.Chart, chartArtifact == null ? null : chartArtifact.Kind,
                "chart result becomes its specialized resource even when it fits inline");
            var chartEnvelope = JObject.Parse(AgentJsonProtocol.BuildToolResult(command, chartResult, 10000));
            AssertEqual(ArtifactUri(chartSession, chartArtifact), (string)chartEnvelope.SelectToken("resources[0].uri"),
                "chart URI is available to the next model step");
            AssertEqual("result", (string)chartEnvelope.SelectToken("resources[0].relation"),
                "chart result resource has an explicit relation");
            AssertEqual(ChatArtifactKinds.Chart, (string)chartEnvelope.SelectToken("resources[0].kind"),
                "chart result resource exposes its specialized kind");
            AssertEqual(true, (bool?)chartEnvelope.SelectToken("data.externalized"),
                "chart result body is reference-only in model history");
            AssertTrue(chartEnvelope.ToString(Formatting.None).IndexOf("\"month\":\"Jan\"", StringComparison.Ordinal) < 0,
                "chart body is absent from the model tool-result envelope");
            AssertEqual(chartArtifact.Id, ToolResultResourceService.ExternalizeIfNeeded(
                chartSession, command, chartResult, 10000, new AppSettings()).Id,
                "chart result externalization is idempotent for an existing exact reference");
            var chartActivity = AgentTranscript.CreateToolActivity(command, chartResult, "tool");
            AssertEqual(true, (bool?)JObject.Parse(chartActivity.DataJson)["externalized"],
                "durable chart activity keeps a resource pointer instead of duplicate chart data");
            AssertEqual(ArtifactUri(chartSession, chartArtifact),
                (string)JObject.Parse(chartActivity.DataJson).SelectToken("resource.uri"),
                "durable chart activity points at the exact chart revision");
            chartSession.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Activity = chartActivity,
                ResourceRefs = chartResult.ModelResourceRefs.ToList()
            });
            WithTempPaths(paths =>
            {
                new ChatStore(paths).Save(chartSession);
                var events = File.ReadAllText(SessionEventFile(paths, chartSession));
                AssertTrue(events.IndexOf("\"month\":\"Jan\"", StringComparison.Ordinal) < 0,
                    "chart body is absent from the durable conversation event");
                var durable = new ChatStore(paths).Load(
                    chartSession.Host,
                    chartSession.DocumentKey,
                    chartSession.Id);
                AssertEqual(1, durable.Artifacts.Count(item => item.Kind == ChatArtifactKinds.Chart),
                    "chart storage projection reuses the pre-dispatch resource without a duplicate");
                AssertContains(durable.Messages.Single().Activity.DataJson, "\"month\":\"Jan\"",
                    "chart UI projection rehydrates the body from CAS");
            });

            var skillCommand = new ToolCommand { ToolId = CapabilityDiscoveryExecutor.ReadToolId, ToolCallId = "call_skill_large" };
            var skillData = JsonConvert.SerializeObject(new { kind = "skill", loaded = true, bodyMarkdown = new string('x', 50000) });
            var boundedSkill = JObject.Parse(AgentJsonProtocol.BuildToolResult(skillCommand, ToolResult.Ok("read", skillData), 256));
            AssertEqual(true, (bool)boundedSkill.SelectToken("data.truncated"), "oversized skill data is marked truncated");
            AssertTrue(boundedSkill.SelectToken("data.loaded") == null, "truncated skill does not retain top-level loaded evidence");
            AssertTrue(ToolResultResourceService.ExternalizeIfNeeded(
                    new ChatSession(), skillCommand, ToolResult.Ok("read", skillData), 256, new AppSettings()) == null,
                "trusted skill evidence is not duplicated into an untrusted artifact");

            var nestedData = JsonConvert.SerializeObject(new { value = new string('x', 200000) });
            var pipeline = AgentTranscript.CreateToolActivity(command, ToolResult.Ok("pipeline", JsonConvert.SerializeObject(new
            {
                steps = new[]
                {
                    new { id = "nested", toolId = "excel.read_range", success = true, dataJson = nestedData }
                }
            })), "tool");
            AssertEqual(1, pipeline.Children.Count, "pipeline child retained");
            AssertContains(pipeline.Children[0].DataJson, "truncated", "nested pipeline data is bounded");
            AssertTrue(pipeline.Children[0].DataJson.Length < 10000, "nested pipeline preview is bounded");
        }

        private static void AgentToolResultFitsRemainingPromptBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.inspect", ToolResult.Ok(
                    "large read",
                    JsonConvert.SerializeObject(new { value = new string('x', 150000) })));
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.inspect", "schema_large_inspect"),
                    "{\"status\":\"in_progress\",\"message\":\"Читаю.\",\"tool_calls\":[{\"id\":\"call_large\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Диапазон результата нужно сузить.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    ContextWindowOverrideTokens = 12000,
                    MaxTokens = 512
                };
                var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.inspect")
                    .Concat(executor.GetControllerTools())
                    .ToList();

                var turn = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "List sheets.", NewSession(adapter), NewContext(adapter), settings, tools,
                    null, null, null, null, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Диапазон результата нужно сузить.", turn.AssistantText, "agent continues after bounded result");
                AssertEqual(3, calls.Count, "schema read, data read, and final model calls");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "\"truncated\":true", "bounded marker reaches model");
                var estimated = ModelContextBudget.EstimateMessagesTokens(calls[1]) +
                    ModelContextBudget.EstimateRequestOptionsTokens(new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject });
                AssertTrue(estimated <= ModelContextBudget.InputBudgetTokens(settings), "next prompt stays within budget");
            });
        }

        private static void ModelCompatibilityAcceptsExactSentinels()
        {
            var responses = new Queue<string>(new[]
            {
                "ROLE_OK",
                "{\"status\":\"in_progress\",\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"A\"}}]}",
                "{\"status\":\"completed\",\"message\":\"RESULT_OK\",\"tool_calls\":[]}"
            });
            var requests = new List<Tuple<List<ChatMessage>, LlmRequestOptions>>();
            LlmCompletionDelegate completion = (providerSettings, messages, options, stream, cancellationToken) =>
            {
                requests.Add(Tuple.Create(messages.ToList(), options));
                return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
            };

            var settings = new AppSettings
            {
                SystemPromptRole = "system",
                AgentResponseMode = AgentResponseModes.JsonSchema,
                ToolResultRole = ToolResultRoles.Tool
            };
            var result = new ModelCompatibilityService(completion).TestAsync(settings, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Compatible, "exact compatibility flow accepted");
            AssertTrue(result.Checks.All(check => check.Passed), "all exact probes pass");
            AssertEqual("system", result.InstructionRole, "selected instruction role reported");
            AssertEqual(AgentResponseModes.JsonSchema, result.ResponseMode, "selected response mode reported");
            AssertEqual(ToolResultRoles.Tool, result.ToolResultRole, "selected tool result role reported");
            AssertEqual(LlmResponseFormats.JsonSchema, requests[1].Item2.ResponseFormat, "compatibility uses selected schema mode");
            AssertTrue(requests[2].Item1.Any(message => string.Equals(message.Role, "tool", StringComparison.Ordinal)),
                "compatibility uses selected tool role");
            AssertTrue(requests[2].Item1.Any(message => message.ToolCalls != null && message.ToolCalls.Count == 1),
                "compatibility sends matched assistant tool call");
        }

        private static void ModelCompatibilityRejectsLooseResponses()
        {
            var responses = new Queue<string>(new[]
            {
                "Any non-empty response",
                "{\"status\":\"in_progress\",\"message\":\"TOOL_OK\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"compat.echo\",\"arguments\":{\"value\":\"WRONG\"}}]}",
                "{\"status\":\"completed\",\"message\":\"Any final message\",\"tool_calls\":[]}"
            });
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });

            var result = new ModelCompatibilityService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(!result.Compatible, "loose compatibility flow rejected");
            AssertTrue(result.Checks.All(check => !check.Passed), "each loose probe fails");
        }

        private static void ModelConnectionProbeReportsTimings()
        {
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                AssertEqual(16, settings.MaxTokens, "probe output is bounded");
                AssertEqual(false, options.ReasoningEnabled.Value, "probe disables reasoning");
                options.DiagnosticProgress(new LlmRequestDiagnosticUpdate
                {
                    RequestId = "probe-1",
                    Phase = LlmRequestDiagnosticPhases.Completed,
                    Model = settings.Model,
                    StreamRequested = settings.StreamResponses,
                    ElapsedMs = 25,
                    PreparationMs = 2,
                    ResponseHeadersMs = 15,
                    FirstChunkMs = 20,
                    TotalMs = 25,
                    StatusCode = 200
                });
                return Task.FromResult(new LlmCompletionResult { Content = "PONG" });
            };

            var result = new ModelConnectionTestService(completion).TestAsync(new AppSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertTrue(result.Success, "connection probe succeeds on non-empty response");
            AssertEqual("probe-1", result.Diagnostics.RequestId, "probe diagnostics retained");
            AssertEqual(20L, result.Diagnostics.FirstChunkMs.Value, "first chunk timing retained");
            AssertEqual(200, result.Diagnostics.StatusCode.Value, "HTTP status retained");
        }

        private static void ModelDiagnosticsStreamReportsFirstChunk()
        {
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"PONG\"}}]}\n\ndata: [DONE]\n";
            var firstChunkCount = 0;
            LlmCompletionResult result;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse)))
            {
                result = LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                    stream,
                    null,
                    CancellationToken.None,
                    null,
                    () => firstChunkCount++).GetAwaiter().GetResult();
            }

            AssertEqual("PONG", result.Content, "stream content parsed");
            AssertEqual(1, firstChunkCount, "first stream chunk reported once");
        }

        private static void ModelDiagnosticsTrackerReportsOneTerminalLifecycle()
        {
            var requestUpdates = new List<LlmRequestDiagnosticUpdate>();
            var globalUpdates = new List<LlmRequestDiagnosticUpdate>();
            var tracker = new LlmRequestDiagnosticsTracker(
                new AppSettings { Model = "diagnostic-model", StreamResponses = true },
                requestUpdates.Add,
                globalUpdates.Add,
                null);

            tracker.Sending(123);
            tracker.Headers(202);
            tracker.FirstChunk();
            tracker.FirstChunk();
            tracker.Completed();
            tracker.Failed(new InvalidOperationException("ignored after completion"));

            AssertEqual(
                "preparing,sending,headers,first_chunk,completed",
                string.Join(",", requestUpdates.Select(update => update.Phase).ToArray()),
                "diagnostic lifecycle phases");
            AssertEqual(requestUpdates.Count, globalUpdates.Count, "request and global diagnostics receive each phase");
            AssertEqual(123L, requestUpdates.Last().RequestBytes.Value, "request size retained");
            AssertEqual(202, requestUpdates.Last().StatusCode.Value, "response status retained");
            AssertTrue(requestUpdates.Last().TotalMs.HasValue, "terminal duration retained");

            var cancelledUpdates = new List<LlmRequestDiagnosticUpdate>();
            var cancelled = new LlmRequestDiagnosticsTracker(new AppSettings(), cancelledUpdates.Add, null, null);
            cancelled.Failed(new OperationCanceledException("cancelled"));
            AssertEqual(LlmRequestDiagnosticPhases.Cancelled, cancelledUpdates.Last().Phase, "cancellation is terminal phase");

            var failedUpdates = new List<LlmRequestDiagnosticUpdate>();
            var failed = new LlmRequestDiagnosticsTracker(new AppSettings(), failedUpdates.Add, null, null);
            failed.Failed(new LlmRequestException(LlmFailureKind.Timeout, "timeout"));
            AssertEqual(LlmRequestDiagnosticPhases.Failed, failedUpdates.Last().Phase, "request error is failed phase");
            AssertEqual(LlmFailureKind.Timeout, failedUpdates.Last().FailureKind.Value, "failure kind retained");
        }
    }
}
