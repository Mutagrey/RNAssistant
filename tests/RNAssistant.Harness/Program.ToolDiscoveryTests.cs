using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ToolDiscoveryIsCompleteAndLoadsExactSchema()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var catalog = ConversationRunService.PrepareToolsForRun(
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()));
                var skills = new[]
                {
                    new SkillDefinition
                    {
                        Id = "excel.review_workbook",
                        Name = "Review workbook",
                        Description = "Review workbook structure and formulas.",
                        BodyMarkdown = "# Review\n\nInspect the workbook carefully.",
                        Enabled = true
                    }
                };
                CapabilityDiscoveryExecutor.BindReadSchema(catalog, skills);

                var compact = CapabilityDiscoveryExecutor.BuildPromptCatalog(catalog, skills, catalog);
                AssertTrue(((JArray)compact["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.add_sheet" && (string)item["kind"] == "tool"),
                    "compact catalog contains exact tool ids with kind");
                AssertTrue(((JArray)compact["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.review_workbook" && (string)item["kind"] == "skill"),
                    "compact catalog contains exact skill ids with kind");
                AssertEqual(true, (bool)compact["idEnumEnforced"],
                    "bounded catalogs also constrain the reader schema to exact ids");
                var reader = catalog.Single(tool => tool.Id == CapabilityDiscoveryExecutor.ReadToolId);
                AssertContains(reader.ArgumentSchemaJson, "excel.add_sheet", "reader enum contains exact tool id");
                AssertContains(reader.ArgumentSchemaJson, "excel.review_workbook", "reader enum contains exact skill id");
                AssertTrue(!executor.GetControllerTools().Any(tool =>
                    string.Equals(tool.Id, "common.tools_read", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.tools_list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.tools_search", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Id, "common.skills_read", StringComparison.OrdinalIgnoreCase)),
                    "removed split discovery ids have no aliases");

                var search = executor.Execute(
                    Command(CapabilityDiscoveryExecutor.SearchToolId, "query", "excel.review_workbook", "limit", 2),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(search.Success, "capability metadata search succeeds");
                AssertTrue(((JArray)JObject.Parse(search.DataJson)["items"]).Count <= 2,
                    "search result respects its bound");
                AssertContains(search.DataJson, "\"kind\":\"skill\"", "search returns explicit capability kind");
                AssertTrue(search.DataJson.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "search contains no exact schemas");

                var read = executor.Execute(
                    Command(CapabilityDiscoveryExecutor.ReadToolId, "id", "excel.add_sheet"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(read.Success, "exact schema read succeeds");
                var data = JObject.Parse(read.DataJson);
                AssertEqual("tool-schema", (string)data["kind"], "exact result kind");
                AssertEqual(true, (bool)data["loaded"], "exact schema is load evidence");
                AssertEqual(true, (bool)data["complete"], "exact schema is complete");
                AssertEqual(false, (bool)data["truncated"], "exact schema is not truncated");
                AssertEqual("excel.add_sheet", (string)data.SelectToken("descriptor.function.name"),
                    "exact descriptor names the tool");
                AssertTrue(data.SelectToken("descriptor.function.parameters") is JObject,
                    "exact descriptor includes strict parameters");
                AssertEqual(
                    CapabilityDiscoveryExecutor.Revision(catalog.Single(tool => tool.Id == "excel.add_sheet")),
                    (string)data["revision"],
                    "schema revision is deterministic");

                var skillRead = executor.Execute(
                    Command(CapabilityDiscoveryExecutor.ReadToolId, "id", "excel.review_workbook"),
                    catalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(skillRead.Success, "same reader loads a skill");
                AssertEqual("skill", (string)JObject.Parse(skillRead.DataJson)["kind"],
                    "skill read returns discriminated kind");

                var collisionDetected = false;
                try
                {
                    CapabilityDiscoveryExecutor.ThrowOnCollision(catalog, new[]
                    {
                        new SkillDefinition { Id = "excel.add_sheet", Enabled = true }
                    });
                }
                catch (InvalidOperationException)
                {
                    collisionDetected = true;
                }
                AssertTrue(collisionDetected, "tool/skill id collisions fail closed");

                var largeCatalog = catalog.Concat(Enumerable.Range(0, 300).Select(index => new ToolDefinition
                {
                    Id = "excel.synthetic_" + index.ToString("D3"),
                    Host = "Excel",
                    Name = "Synthetic " + index,
                    Description = new string('d', 300) + " " + index,
                    ArgumentSchemaJson = EmptyFormalToolSchema,
                    BuiltIn = true,
                    Enabled = true,
                    AgentCanRun = true
                })).ToList();
                CapabilityDiscoveryExecutor.BindReadSchema(largeCatalog, skills);
                var completeCatalog = CapabilityDiscoveryExecutor.BuildPromptCatalog(
                    largeCatalog,
                    skills,
                    largeCatalog);
                AssertEqual(true, (bool)completeCatalog["complete"],
                    "large exact-id catalog is explicitly complete");
                AssertEqual(false, (bool)completeCatalog["truncated"],
                    "large exact-id catalog is never silently truncated");
                AssertEqual((int)completeCatalog["total"], ((JArray)completeCatalog["items"]).Count,
                    "every runnable tool and enabled skill is listed");
                AssertTrue(((JArray)completeCatalog["items"]).OfType<JObject>().Any(item =>
                    (string)item["id"] == "excel.synthetic_299"),
                    "complete prompt index contains the tail id");
                AssertTrue(completeCatalog["items"].ToString(Newtonsoft.Json.Formatting.None)
                        .IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "complete prompt index remains schema-free");
                var tailSearch = executor.Execute(
                    Command(CapabilityDiscoveryExecutor.SearchToolId, "query", "excel.synthetic_299"),
                    largeCatalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(tailSearch.Success, "optional search filters the complete catalog");
                AssertContains(tailSearch.DataJson, "excel.synthetic_299",
                    "search returns the exact tail capability id");
                AssertTrue(tailSearch.DataJson.IndexOf("schemaLoaded", StringComparison.Ordinal) < 0,
                    "search does not claim working-set state it cannot observe");
                var tailRead = executor.Execute(
                    Command(CapabilityDiscoveryExecutor.ReadToolId, "id", "excel.synthetic_299"),
                    largeCatalog,
                    new AppSettings(),
                    false,
                    false,
                    null,
                    AppSettings.DefaultMaxAgentToolSteps,
                    skills);
                AssertTrue(tailRead.Success, "exact reader loads a tail schema from a large catalog");
            });
        }

        private static void ProgressiveAgentRequiresExactToolRead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"status\":\"in_progress\",\"message\":\"Добавляю сразу.\",\"tool_calls\":[{\"id\":\"unloaded\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Progressive\"}}]}",
                    LoadToolSchemaResponse("excel.add_sheet", "schema_progressive"),
                    "{\"status\":\"in_progress\",\"message\":\"Добавляю после загрузки схемы.\",\"tool_calls\":[{\"id\":\"loaded\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Progressive\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Лист создан.\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var options = new List<LlmRequestOptions>();
                LlmCompletionDelegate completion = (completionSettings, messages, requestOptions, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    options.Add(requestOptions);
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    MaxAgentFormatRetries = 2,
                    AutoConfirmToolActions = true
                };
                var catalog = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Создай лист Progressive.",
                    NewSession(adapter),
                    NewContext(adapter),
                    settings,
                    catalog,
                    null).GetAwaiter().GetResult();

                AssertEqual("Лист создан.", result.AssistantText, "progressive run completes");
                AssertTrue(adapter.HasSheet("Progressive"), "loaded domain tool executes once");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.add_sheet"),
                    "unloaded attempt never executes");
                AssertEqual(4, requests.Count, "unloaded repair, schema read, execution, and final requests");
                var initialCallableNames = JObject.Parse(options[0].ResponseSchemaJson)
                    .SelectTokens("properties.tool_calls.items.anyOf[*].properties.name.const")
                    .Select(token => (string)token)
                    .ToList();
                AssertTrue(!initialCallableNames.Contains("excel.add_sheet", StringComparer.OrdinalIgnoreCase),
                    "initial strict response schema omits unloaded tool as a callable name");
                var loadedCallableNames = JObject.Parse(options[2].ResponseSchemaJson)
                    .SelectTokens("properties.tool_calls.items.anyOf[*].properties.name.const")
                    .Select(token => (string)token)
                    .ToList();
                AssertTrue(loadedCallableNames.Contains("excel.add_sheet", StringComparer.OrdinalIgnoreCase),
                    "strict response schema includes exact loaded tool as a callable name");
                AssertContains(FlattenSimple(requests[1]), "Tool schema is not loaded: excel.add_sheet",
                    "local parser distinguishes an unloaded known tool during repair");
                AssertContains(FlattenSimple(requests[1]), "common.capabilities_read",
                    "repair names the exact schema-loading action");
                AssertContains(FlattenSimple(requests[2]), "\"kind\":\"tool-schema\"",
                    "complete schema evidence reaches the next model step");
            });
        }

        private static void ProgressiveToolWorkingSetEvictsAndReplaysDeterministically()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var dynamicTools = Enumerable.Range(1, ProgressiveToolWorkingSet.MaximumDynamicSchemas + 1)
                    .Select(index => new ToolDefinition
                    {
                        Id = "excel.dynamic_" + index,
                        Host = "Excel",
                        Name = "Dynamic " + index,
                        Description = "Test dynamic schema " + index,
                        ArgumentSchemaJson = EmptyFormalToolSchema,
                        BuiltIn = true,
                        Enabled = true,
                        AgentCanRun = true
                    })
                    .ToList();
                var catalog = ConversationRunService.PrepareToolsForRun(
                    executor.GetControllerTools().Concat(dynamicTools));
                var workingSet = ProgressiveToolWorkingSet.Create(
                    ChatModes.Agent,
                    catalog,
                    new AppSettings());
                var evidence = new List<ChatMessage>();

                for (var index = 1; index <= ProgressiveToolWorkingSet.MaximumDynamicSchemas; index++)
                {
                    var message = ReadSchemaEvidence(executor, catalog, "excel.dynamic_" + index, "read_" + index);
                    evidence.Add(message);
                    IReadOnlyList<string> evicted;
                    AssertTrue(workingSet.ObserveReadResult(message, out evicted), "schema evidence " + index + " loads");
                    AssertEqual(0, evicted.Count, "working set has capacity for schema " + index);
                }

                var touch = AgentJsonProtocol.CreateToolCallMessage(
                    new AgentToolCall
                    {
                        Id = "touch_dynamic_1",
                        Name = "excel.dynamic_1",
                        Arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    },
                    "Using loaded schema.",
                    null,
                    ToolResultRoles.User);
                evidence.Add(touch);
                workingSet.Touch("excel.dynamic_1");

                var ninthId = "excel.dynamic_" + (ProgressiveToolWorkingSet.MaximumDynamicSchemas + 1);
                var ninth = ReadSchemaEvidence(executor, catalog, ninthId, "read_last");
                evidence.Add(ninth);
                IReadOnlyList<string> removed;
                AssertTrue(workingSet.ObserveReadResult(ninth, out removed), "new schema loads at capacity");
                AssertEqual(1, removed.Count, "one least-recent schema is evicted");
                AssertEqual("excel.dynamic_2", removed[0], "recently used schema survives LRU eviction");
                AssertTrue(workingSet.Tools.Any(tool => tool.Id == "excel.dynamic_1"), "touched schema remains active");
                AssertTrue(!workingSet.Tools.Any(tool => tool.Id == "excel.dynamic_2"), "least-recent schema is inactive");
                AssertTrue(workingSet.Tools.Any(tool => tool.Id == ninthId), "new schema is active");

                var replayed = ProgressiveToolWorkingSet.Create(
                    ChatModes.Agent,
                    catalog,
                    new AppSettings(),
                    evidence);
                AssertTrue(replayed.Tools.Any(tool => tool.Id == "excel.dynamic_1"),
                    "tool-call evidence restores LRU recency");
                AssertTrue(!replayed.Tools.Any(tool => tool.Id == "excel.dynamic_2"),
                    "replay produces the same eviction");
                AssertTrue(replayed.Tools.Any(tool => tool.Id == ninthId),
                    "replay restores latest exact schema");

                var revisedCatalog = catalog.Select(tool => tool.Clone()).ToList();
                revisedCatalog.Single(tool => tool.Id == ninthId).Description = "Changed revision";
                var revisionMismatch = ProgressiveToolWorkingSet.Create(
                    ChatModes.Agent,
                    revisedCatalog,
                    new AppSettings(),
                    new[] { ninth });
                AssertTrue(!revisionMismatch.Tools.Any(tool => tool.Id == ninthId),
                    "stale schema evidence cannot load a changed revision");
            });
        }

        private static ChatMessage ReadSchemaEvidence(
            OfficeToolExecutor executor,
            IReadOnlyList<ToolDefinition> catalog,
            string toolId,
            string callId)
        {
            var command = Command(CapabilityDiscoveryExecutor.ReadToolId, "id", toolId);
            command.ToolCallId = callId;
            var result = executor.Execute(command, catalog, new AppSettings(), false, false);
            AssertTrue(result.Success, "schema read succeeds for " + toolId);
            return AgentJsonProtocol.CreateToolResultMessage(command, result, ToolResultRoles.User);
        }
    }
}
