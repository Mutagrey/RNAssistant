using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
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
                System.Func<string, JObject, string, string> execute = (tool, arguments, message) =>
                    ModelProtocolWire.WriteAcceptedCall("Perform the next requested operation.", message,
                        new ConversationToolCall { Name = tool, Arguments = arguments });
                var read = execute("common.resources_read", new JObject {
                    ["target"] = "Excel range: Data!A2:B2", ["representation"] = "text" },
                    "Проверяю фактическое содержимое диапазона.");
                var clear = execute("excel.clear_range", new JObject {
                    ["sheet"] = "Data", ["address"] = "A2:B2" }, "Очищаю диапазон.");
                System.Func<string, string> loadSchema = id => execute(CapabilityToolCatalog.ReadToolId,
                    new JObject { ["id"] = id }, "Загружаю схему инструмента.");
                var premature = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                    "The clear has not been inspected yet.", "", results: new ConversationResultClaim[0]);
                var responses = new Queue<string>(new[] {
                    read,
                    loadSchema("excel.clear_range"),
                    clear,
                    premature,
                    clear,
                    read,
                    loadSchema("excel.add_sheet"),
                    execute("excel.add_sheet", new JObject { ["name"] = "Report" },
                        "Диапазон пуст. Создаю оставшийся лист.")
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var finishCount = 0;
                var settings = new AppSettings {
                    AutoConfirmToolActions = true, ToolResultRole = role, AgentResponseMode = format,
                    ContextWindowOverrideTokens = 65536,
                    SystemPrompt = "Perform the requested work with the supplied tools."
                };
                var session = NewSession(adapter);
                var service = CreateConversationRunService(adapter, executor, (configuration, messages, options, stream, token) => {
                    requests.Add(messages.ToList());
                    if (responses.Count > 0)
                        return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                    if (++finishCount != 1) throw new System.InvalidOperationException("Unexpected extra finish request.");
                    var clearResult = session.Messages.Last(item => item.ToolName == "excel.clear_range" &&
                        item.ExecutionProgress?.Outcome == RNAssistant.Core.Tools.ToolExecutionOutcome.Unknown);
                    var inspected = session.Messages.Last(item => item.ToolName == "common.resources_read" &&
                        item.ExecutionProgress?.Outcome == RNAssistant.Core.Tools.ToolExecutionOutcome.Ok);
                    var sheet = session.Messages.Last(item => item.ToolName == "excel.add_sheet" &&
                        item.ExecutionProgress?.Outcome == RNAssistant.Core.Tools.ToolExecutionOutcome.Ok);
                    var finish = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                        "Inspection observed an empty range and the sheet write completed.",
                        "Диапазон проверен чтением; лист Report создан.", results: new[] {
                            new ConversationResultClaim("Direct clear verification was uncertain; a later read observed an empty range.",
                                new[] { clearResult.ExecutionReference.Alias, inspected.ExecutionReference.Alias }, ConversationAssessment.Unverified),
                            new ConversationResultClaim("Report sheet was created.",
                                new[] { sheet.ExecutionReference.Alias }, ConversationAssessment.Satisfied)
                        });
                    return Task.FromResult(new LlmCompletionResult { Content = finish });
                });
                var result = service.ExecuteAsync(ChatModes.Agent, "Очисти Data!A2:B2 и создай Report.",
                    session, NewContext(adapter), settings,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(0, responses.Count, "the production loop continues through inspection and remaining work: " +
                    result.RunViewState.Reason + " / " + result.AssistantText);
                AssertEqual(1, finishCount, "one evidence-citing finish follows inspection");
                AssertEqual("model_finish", result.RunViewState.Reason, "finish follows successful inspection");
                AssertEqual(RunViewHealth.Unknown, result.RunViewState.ExecutionHealth, "inspection never erases attempt uncertainty");
                AssertEqual(RunGoalVerification.NotAssessed, result.RunViewState.GoalVerification,
                    "model claims do not certify the user goal");
                AssertEqual(2, result.RunViewState.ModelResults.Count, "finish retains both model assessments");
                AssertEqual("unverified", result.RunViewState.ModelResults[0].Assessment,
                    "the clear's direct verification remains uncertain");
                AssertEqual("satisfied", result.RunViewState.ModelResults[1].Assessment,
                    "the separate sheet result cites its verified write");
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
                AssertTrue(!requests[6].Any(message => (message.Content ?? "").Contains(ModelProtocolWire.UnknownEffectGuidance)),
                    "unknown-effect reminder ends after inspection; reflection budget may remain: " + role + "/" + format);
                AssertContains(result.AssistantText, "Report создан", "the final answer includes the completed remaining work");
                var loaded = new ChatStore(FixturePaths.Value).Load(session.Id);
                AssertEqual(2, loaded.Messages.Count(message => message.ToolName == "common.resources_read" &&
                    message.ExecutionProgress?.IndependentLocalRead == true), "durable read policy supports continuation replay");
                AssertTrue(!loaded.Messages.Any(message => message.Role == "assistant" && message.ProtocolMessage &&
                    message.Content == premature),
                    "the rejected empty completion is not accepted history");
            });
        }
    }
}
