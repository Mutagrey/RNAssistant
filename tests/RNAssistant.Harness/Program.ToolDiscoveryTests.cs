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
        private static void ToolDiscoveryIsBoundedAndLoadsExactSchema()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var catalog = ConversationRunService.PrepareToolsForRun(
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()));
                var namespaces = executor.Execute(
                    new ToolCommand { ToolId = ToolDiscoveryExecutor.ListToolId },
                    catalog,
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(namespaces.Success, "namespace discovery succeeds");
                AssertContains(namespaces.DataJson, "\"kind\":\"tool-namespaces\"", "namespace result kind");
                AssertContains(namespaces.DataJson, "\"id\":\"excel\"", "host namespace listed");
                AssertTrue(namespaces.DataJson.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "namespace discovery returns no schemas");

                var list = executor.Execute(
                    Command(ToolDiscoveryExecutor.ListToolId, "namespace", "excel", "limit", 1),
                    catalog,
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(list.Success, "namespace metadata page succeeds");
                var listData = JObject.Parse(list.DataJson);
                AssertEqual(1, ((JArray)listData["items"]).Count, "metadata page respects limit");
                AssertEqual(false, (bool)listData["schemasLoaded"], "metadata does not load schemas");
                AssertTrue(listData.SelectToken("items[0].schemaLoaded") != null,
                    "metadata explicitly marks schema as unloaded");
                AssertTrue(list.DataJson.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "metadata page contains no parameter schema");

                var search = executor.Execute(
                    Command(ToolDiscoveryExecutor.SearchToolId, "query", "sheet", "limit", 2),
                    catalog,
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(search.Success, "tool metadata search succeeds");
                AssertTrue(((JArray)JObject.Parse(search.DataJson)["items"]).Count <= 2,
                    "search result respects its bound");
                AssertTrue(search.DataJson.IndexOf("\"parameters\"", StringComparison.Ordinal) < 0,
                    "search contains no exact schemas");

                var read = executor.Execute(
                    Command(ToolDiscoveryExecutor.ReadToolId, "id", "excel.add_sheet"),
                    catalog,
                    new AppSettings(),
                    false,
                    false);
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
                    ToolDiscoveryExecutor.Revision(catalog.Single(tool => tool.Id == "excel.add_sheet")),
                    (string)data["revision"],
                    "schema revision is deterministic");
            });
        }

        private static void ProgressiveAgentRequiresExactToolRead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Добавляю сразу.\",\"tool_calls\":[{\"id\":\"unloaded\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Progressive\"}}]}",
                    LoadToolSchemaResponse("excel.add_sheet", "schema_progressive"),
                    "{\"message\":\"Добавляю после загрузки схемы.\",\"tool_calls\":[{\"id\":\"loaded\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Progressive\"}}]}",
                    "{\"message\":\"Лист создан.\",\"tool_calls\":[]}"
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
                    MaxAgentFormatRetries = 1,
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
                AssertTrue(options[0].ResponseSchemaJson.IndexOf("excel.add_sheet", StringComparison.OrdinalIgnoreCase) < 0,
                    "initial strict response schema omits unloaded tool");
                AssertContains(options[2].ResponseSchemaJson, "excel.add_sheet",
                    "strict response schema includes exact loaded tool");
                AssertContains(FlattenSimple(requests[1]), "Unknown tool: excel.add_sheet",
                    "local parser explains unloaded tool during repair");
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
            var command = Command(ToolDiscoveryExecutor.ReadToolId, "id", toolId);
            command.ToolCallId = callId;
            var result = executor.Execute(command, catalog, new AppSettings(), false, false);
            AssertTrue(result.Success, "schema read succeeds for " + toolId);
            return AgentJsonProtocol.CreateToolResultMessage(command, result, ToolResultRoles.User);
        }
    }
}
