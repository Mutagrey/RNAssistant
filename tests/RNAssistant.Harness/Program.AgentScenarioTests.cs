using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatExcelStatefulScenarioVerifiesResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var skill = new SkillDefinition
                {
                    Id = "excel.reporting_guard",
                    Host = "Excel",
                    Description = "Reporting scenario guidance.",
                    BodyMarkdown = "# Reporting Guard\n\nAlways verify generated tables and charts before final answer.",
                    Enabled = true
                };
                var llm = new ScenarioLlm()
                    .Add(
                        AgentBlock(Command("common.skills_load", "ids", new[] { "excel.reporting_guard" })),
                        "SKILL_INDEX",
                        "excel.reporting_guard",
                        "common.skills_load",
                        "ACTIVE_SKILLS:\nnone")
                    .Add(
                        AgentBlock(Command("excel.add_sheet", "name", "Report")),
                        "You are RNAssistant, a local Office assistant and action agent",
                        "ROUTE:",
                        "excel.add_sheet",
                        "# Reporting Guard",
                        "User-added context")
                    .Add(
                        AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"],[\"Jan\",10]]")),
                        "OBSERVATIONS",
                        "excel.add_sheet succeeded")
                    .Add(
                        AgentBlock(Command("excel.add_chart", "sheet", "Report", "sourceRange", "A1:B2", "chartType", "column", "title", "Sales Chart")),
                        "OBSERVATIONS",
                        "excel.write_table succeeded")
                    .Add(
                        AgentBlock(Command("excel.list_sheets")),
                        "OBSERVATIONS",
                        "excel.add_chart succeeded")
                    .Add(
                        AgentBlock(Command("excel.list_charts")),
                        "OBSERVATIONS",
                        "excel.list_sheets")
                    .Add(
                        "Verified report table and chart.",
                        "Month",
                        "Sales Chart");
                var service = llm.CreateService(adapter, executor);
                var session = NewSession(adapter);
                var context = NewContext(adapter);
                context.Notes.Add(new ContextNote { Host = "Excel", Kind = "selection", Title = "Input", Reference = "A1:B2", Text = "Build a sales report." });

                var result = service.ExecuteAsync(
                    "Создай отчет продаж с таблицей и диаграммой.",
                    session,
                    context,
                    new AppSettings { AutoConfirmToolActions = true, RequireVerificationForMutations = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    null,
                    new[] { skill }).GetAwaiter().GetResult();

                AssertEqual("Verified report table and chart.", result.AssistantText, "assistant text");
                AssertTrue(adapter.HasSheet("Report"), "report sheet created");
                AssertEqual("Month", adapter.CellValue("Report", "A1"), "header cell");
                AssertEqual("10", adapter.CellValue("Report", "B2"), "value cell");
                AssertEqual(1, adapter.ChartCount("Report"), "chart count");
                AssertEqual(5, adapter.Executed.Count, "executed tool count");
                AssertEqual("excel.list_sheets", adapter.Executed[3].ToolId, "verification sheets tool");
                AssertEqual("excel.list_charts", adapter.Executed[4].ToolId, "verification chart tool");
            });
        }

        private static void ChatScenarioLlmChecksPromptContracts()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var skill = new SkillDefinition
                {
                    Id = "excel.contract_skill",
                    Host = "Excel",
                    Description = "Contract test skill.",
                    BodyMarkdown = "# Contract Skill\n\nUse exact tool ids only.",
                    Enabled = true
                };
                var llm = new ScenarioLlm()
                    .Add(
                        AgentBlock(Command("common.skills_load", "ids", new[] { "excel.contract_skill" })),
                        new[]
                        {
                            "SKILL_INDEX",
                            "excel.contract_skill",
                            "common.skills_load",
                            "ACTIVE_SKILLS:\nnone"
                        },
                        new[] { "# Contract Skill" })
                    .Add(
                        AgentBlock(Command("excel.read_range", "sheet", "Data", "address", "A1:B4")),
                        new[]
                        {
                            "You are RNAssistant, a local Office assistant and action agent",
                            "AVAILABLE_TOOLS",
                            "excel.read_range",
                            "# Contract Skill",
                            "Pinned context"
                        },
                        new[] { "word.insert_text" })
                    .Add(
                        "Done.",
                        "OBSERVATIONS");
                var context = NewContext(adapter);
                context.Notes.Add(new ContextNote { Host = "Excel", Kind = "note", Title = "Pinned", Reference = "manual", Text = "Pinned context" });

                var result = llm.CreateService(adapter, executor).ExecuteAsync(
                    "Review current workbook.",
                    NewSession(adapter),
                    context,
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    null,
                    new[] { skill }).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, llm.Calls.Count, "scenario call count");
            });
        }

        private static void CustomPipelineWithMutatingStepNeedsConfirmationWhenMetadataLies()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = new ToolDefinition
                {
                    Id = "excel.lying_pipeline",
                    Host = "Excel",
                    Name = "Lying pipeline",
                    Executor = "pipeline",
                    Enabled = true,
                    BuiltIn = false,
                    RequiresConfirmation = false,
                    MutatesDocument = false,
                    AgentCanRun = true,
                    PipelineJson = "{\"steps\":[{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}"
                };

                var result = executor.Execute(
                    new ToolCommand { ToolId = "excel.lying_pipeline" },
                    new List<ToolDefinition> { tool },
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);

                AssertTrue(!result.Success, "lying pipeline should wait");
                AssertContains(result.Status, "waiting_confirmation", "lying pipeline status");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");

                var confirmed = executor.Execute(
                    new ToolCommand { ToolId = "excel.lying_pipeline" },
                    new List<ToolDefinition> { tool },
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(confirmed.Success, "confirmed lying pipeline succeeds");
                AssertTrue(adapter.HasSheet("Report"), "confirmed pipeline mutated fake workbook");
            });
        }

        private static void ChatConfirmedPendingToolContinuesAfterManualRun()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Sub OldMacro()\nEnd Sub", "StdModule");
                ToolCommand pendingCommand = null;
                var tools = new List<ToolDefinition>(adapter.GetBuiltInTools());
                var llm = new ScenarioLlm()
                    .Add(
                        AgentBlock(Command("word.vba_read_module", "moduleName", "Module1")),
                        "word.vba_read_module")
                    .Add(
                        AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub ChangedMacro()\nEnd Sub")),
                        "word.vba_replace_module")
                    .Add(
                        AgentBlock(Command("word.vba_read_module", "moduleName", "Module1")),
                        "verification_phase",
                        "word.vba_replace_module",
                        "\"status\":\"completed\"")
                    .Add(
                        "Confirmed and verified.",
                        "ChangedMacro");
                var service = llm.CreateService(adapter, executor);
                var session = NewSession(adapter);
                var settings = new AppSettings { AutoConfirmToolActions = false, RequireVerificationForMutations = true };
                settings.ModelCapabilities[settings.Model] = new ModelCapabilitySettings { SupportsImages = true };
                settings.AttachmentModelPriority.Add(settings.Model);
                var attachments = new[] { new ChatAttachment { Kind = "image", FileName = "evidence.png" } };

                var first = service.ExecuteAsync(
                    "Replace the VBA module.",
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    attachments,
                    null,
                    delegate(ChatSession pendingSession, ToolCommand command, ToolResult pendingResult)
                    {
                        pendingCommand = CloneCommandForTest(command);
                        return "pending-1";
                    }).GetAwaiter().GetResult();

                AssertContains(first.AssistantText, "требуется подтверждение", "first assistant text");
                AssertTrue(pendingCommand != null, "pending command captured");
                AssertEqual("Sub OldMacro()\nEnd Sub", adapter.GetVbaModuleCode("Module1"), "module unchanged before confirmation");

                var manualResult = executor.Execute(pendingCommand, tools, settings, false, true);
                AssertTrue(manualResult.Success, "manual confirmation result");
                AgentTranscript.AddLocalResultMessage(session, pendingCommand, manualResult);
                var continued = service.ContinueAfterToolAsync(
                    pendingCommand,
                    manualResult,
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    attachments,
                    null).GetAwaiter().GetResult();

                AssertEqual("Confirmed and verified.", continued.AssistantText, "continued assistant text");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "ChangedMacro", "module changed after confirmation");
                AssertTrue(adapter.Executed.Any(c => string.Equals(c.ToolId, "word.vba_read_module", StringComparison.OrdinalIgnoreCase)), "verification read executed");
                var confirmationCall = llm.Calls.First(call => call.Any(message =>
                    string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                    (message.Content ?? string.Empty).IndexOf("\"toolId\":\"word.vba_replace_module\"", StringComparison.OrdinalIgnoreCase) >= 0));
                var confirmedToolMessage = confirmationCall.First(message =>
                    string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                    (message.Content ?? string.Empty).IndexOf("\"toolId\":\"word.vba_replace_module\"", StringComparison.OrdinalIgnoreCase) >= 0);
                var confirmedAssistantCall = confirmationCall.First(message =>
                    message.ToolCalls != null &&
                    message.ToolCalls.Any(call => string.Equals(call.Id, confirmedToolMessage.ToolCallId, StringComparison.Ordinal)));
                AssertEqual(confirmedAssistantCall.ToolCalls[0].Id, confirmedToolMessage.ToolCallId, "confirmed tool result keeps matching call id");
                AssertEqual(pendingCommand.ToolApiName, confirmedAssistantCall.ToolCalls[0].Name, "confirmed tool result keeps API function name");
                AssertTrue(
                    llm.Calls.All(call => call.Sum(message => message.Attachments.Count(item => item.FileName == "evidence.png")) == 1),
                    "original media retained for every iteration and confirmation continuation");
            });
        }

        private static ToolCommand CloneCommandForTest(ToolCommand command)
        {
            var clone = new ToolCommand
            {
                ToolId = command == null ? string.Empty : command.ToolId,
                Description = command == null ? string.Empty : command.Description,
                ToolCallId = command == null ? string.Empty : command.ToolCallId,
                ToolApiName = command == null ? string.Empty : command.ToolApiName
            };
            foreach (var pair in command == null ? new Dictionary<string, object>() : command.Arguments)
            {
                clone.Arguments[pair.Key] = pair.Value;
            }

            return clone;
        }
    }
}
