using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AgentExcelUnknownContinuesThroughInspection()
        {
            foreach (var role in new[] { "tool", "user" })
            foreach (var format in new[] { AgentResponseModes.JsonObject, AgentResponseModes.JsonSchema })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                // Match the reported clear-range read-back mismatch: the clear
                // takes effect, but the mutation's own verification is inconclusive.
                adapter.ExcelRangeMutationReadTransform = snapshot => { snapshot.Satisfied = false; return snapshot; };
                const string read = "{\"message\":\"Проверяю фактическое содержимое диапазона.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"Excel range: Data!A2:B2\",\"representation\":\"text\"}}]}";
                const string clear = "{\"message\":\"Очищаю диапазон.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.clear_range\",\"arguments\":{\"sheet\":\"Data\",\"address\":\"A2:B2\"}}]}";
                var responses = new Queue<string>(new[] {
                    read,
                    LoadToolSchemaResponse("excel.clear_range"),
                    clear,
                    ModelProtocolWire.WriteAction("", ConversationResponse.DoneAction),
                    clear,
                    read,
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Диапазон пуст. Создаю оставшийся лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    ModelProtocolWire.WriteAction("Диапазон проверен чтением; лист Report создан.", ConversationResponse.DoneAction)
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var settings = new AppSettings {
                    AutoConfirmToolActions = true, ToolResultRole = role, AgentResponseMode = format,
                    ContextWindowOverrideTokens = 65536,
                    SystemPrompt = "Perform the requested work with the supplied tools."
                };
                var session = NewSession(adapter);
                var service = CreateConversationRunService(adapter, executor, (configuration, messages, options, stream, token) => {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var result = service.ExecuteAsync(ChatModes.Agent, "Очисти Data!A2:B2 и создай Report.",
                    session, NewContext(adapter), settings,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(0, responses.Count, "the production loop continues through inspection and remaining work: " +
                    result.RunViewState.Reason + " / " + result.AssistantText);
                AssertEqual("model_done", result.RunViewState.Reason, "done follows successful inspection");
                AssertEqual(RunViewHealth.Unknown, result.RunViewState.ExecutionHealth, "inspection never erases attempt uncertainty");
                AssertEqual(string.Empty, adapter.ExcelCellText("Data", "A2"), "clear took effect despite unknown verification");
                AssertTrue(adapter.HasSheet("Report"), "the remaining requested mutation executes");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(call => call == FakeOfficeAdapter.ExcelRangeMutationApplyOperation),
                    "the uncertain clear is dispatched exactly once");
                AssertContains(FlattenSimple(requests[3]), "excel_range_mutation_verification_failed", "the reported failure reaches the model");
                foreach (var index in new[] { 3, 4, 5 })
                {
                    AssertContains(requests[index].Last().Content, "COMPLETION_FEEDBACK:", "feedback is a required trailing context message");
                    AssertContains(requests[index].Last().Content, ModelProtocolWire.UnknownEffectGuidance,
                        "unknown and premature done both request inspection, even with custom prompts");
                }
                AssertContains(FlattenSimple(requests[5]), "may have had an effect", "blind replay is rejected before inspection");
                AssertTrue(!requests[6].Any(message => (message.Content ?? "").StartsWith("COMPLETION_FEEDBACK:")),
                    "the reminder ends after successful inspection");
                AssertContains(result.AssistantText, "Report создан", "the final answer includes the completed remaining work");
                var loaded = new ChatStore(FixturePaths.Value).Load(session.Id);
                AssertEqual(2, loaded.Messages.Count(message => message.ToolName == "common.resources_read" &&
                    message.ExecutionProgress?.IndependentLocalRead == true), "durable read policy supports continuation replay");
                AssertTrue(!loaded.Messages.Any(message => message.Role == "assistant" && message.ProtocolMessage &&
                    message.Content == ModelProtocolWire.WriteAction("", ConversationResponse.DoneAction)),
                    "the rejected empty completion is not accepted history");
            });
        }
    }
}
