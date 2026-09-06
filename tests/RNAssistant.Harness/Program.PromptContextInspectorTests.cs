using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RuntimeToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PromptContextInspectorBuildsAgentSnapshot()
        {
            WithTempPaths(paths =>
            {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            session.Mode = ChatModes.Agent;
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Проверь таблицу." });
            var call = new AgentToolCall
            {
                Id = "call_1",
                Name = ResourceToolCatalog.ReadToolId,
                Arguments = new Dictionary<string, object> { ["target"] = "Excel range: Data!A1:B4" }
            };
            var callMessage = AgentJsonProtocol.CreateToolCallMessage(call, string.Empty, null,
                ToolResultRoles.User, FixtureCallOrigin("inspector-step"));
            callMessage.RunId = "run-1";
            session.Messages.Add(callMessage);
            var resultMessage = AgentJsonProtocol.CreateToolResultMessage(
                new ToolInvocation { ToolCallId = call.Id, ToolId = call.Name },
                RuntimeToolResult.Ok("Read"), ToolResultRoles.User);
            resultMessage.RunId = "run-1";
            session.Messages.Add(resultMessage);
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant", Content = "Диапазон прочитан.",
                ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion
            });
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "plan_r1",
                Kind = ChatArtifactKinds.TaskList,
                Title = "План проверки",
                InlineText = "{\"steps\":[]}"
            });
            var context = NewContext(adapter);
            context.Notes.Add(new ContextNote
            {
                Id = "note-1",
                Kind = "selection",
                Title = "Выделение",
                Reference = "A1:B4",
                Text = "Revenue 100; Cost 40"
            });
            var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                .Where(tool => tool.Id == "excel.add_sheet").Concat(ResourceToolCatalog.GetControllerTools())
                .ToList();
            var skills = new[]
            {
                new SkillDefinition
                {
                    Id = "common.audit",
                    Name = "Audit",
                    Description = "Checks workbook calculations.",
                    Enabled = true
                }
            };
            var settings = new AppSettings
            {
                ContextWindowOverrideTokens = 32768,
                AgentResponseMode = AgentResponseModes.JsonSchema
            };

            var result = new PromptContextInspectorService(adapter, paths).Inspect(
                session,
                context,
                settings,
                tools,
                skills,
                new ChatAttachment[0],
                "Найди расхождения.",
                false);

            AssertTrue(result.UsedTokens > 0, "inspector estimates prompt tokens");
            AssertTrue(result.Sections.Any(section => section.Id == "tool_instructions"), "separate tool prompt cost is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "skill_instructions"), "separate skill prompt cost is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "capabilities"),
                "compact exact-id capability catalog is visible without eager schemas");
            AssertTrue(result.Sections.Any(section => section.Id == "tools"),
                "deterministic Excel core schemas are visible in the callable pack");
            AssertTrue(result.Sections.Any(section => section.Id == "format_repair_reserve" && section.Tokens > 0),
                "inspector exposes the same bounded repair reserve used by admission");
            AssertTrue(result.Sections.Any(section => section.Id == "continuation_reserve" &&
                section.Tokens == ModelContextBudget.ContinuationReserveTokens(settings)),
                "inspector exposes the same continuation reserve used by admission");
            var capabilities = result.Sections.Single(section => section.Id == "capabilities");
            AssertTrue(capabilities.Items.Any(item => item.Kind == "tool"), "tool ids are visible in the unified catalog");
            AssertTrue(capabilities.Items.Any(item => item.Kind == "skill"), "skill ids are visible in the unified catalog");
            AssertTrue(result.Sections.Any(section => section.Id == "tool_history"), "tool protocol history is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "document_context"), "document context is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "artifacts"), "artifact index is visible");
            AssertEqual(result.UsedTokens, result.Sections.Where(section => section.Included).Sum(section => section.Tokens),
                "included section totals match prompt estimate");
            AssertTrue(result.RawRequestJson == null, "raw request is not built by default");
            AssertTrue(result.ResourceContextReceipt != null, "inspector exposes the same frozen compiler receipt");
            });
        }

        private static void PromptContextInspectorRawJsonIsOptIn()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.Messages.Add(new ChatMessage { Role = "user", Content = "История" });
                var service = new PromptContextInspectorService(adapter, FixturePaths.Value, executor.ResourceAuthority, executor.Payloads);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var publication = executor.CaptureSkills();

                var compact = service.Inspect(
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    tools,
                    new SkillDefinition[0],
                    new ChatAttachment[0],
                    "Новый вопрос",
                    false, publication);
                var raw = service.Inspect(
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    tools,
                    new SkillDefinition[0],
                    new ChatAttachment[0],
                    "Новый вопрос",
                    true, publication);

                AssertTrue(compact.RawRequestJson == null, "compact inspection skips raw serialization");
                AssertEqual(new SkillCatalogSnapshot(new SkillDefinition[0], publication.Generation).Generation,
                    raw.ResourceContextReceipt.SkillGeneration, "inspector preserves the same published generation seed as the run compiler");
                AssertContains(raw.RawRequestJson, "Новый вопрос", "raw structure is generated explicitly");
                AssertTrue(raw.Sections.Any(section => section.Id == "tools"),
                    "chat inspector shows read-only resource schemas");
                AssertTrue(!raw.Sections.Any(section => section.Id == "skills"),
                    "chat inspector excludes skills");
                AssertContains(raw.RawRequestJson, "common.resources_read", "chat raw request includes resource reads");
                AssertTrue(raw.RawRequestJson.IndexOf("excel.inspect", StringComparison.OrdinalIgnoreCase) < 0,
                    "chat raw request excludes Office tools");
                AssertContains(raw.RawRequestJson, "json_object", "chat raw request includes structured response format");
            });
        }

        private static void PromptContextInspectorIsolatesConcurrentSettings()
        {
            WithTempPaths(paths =>
            {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            session.Mode = ChatModes.Chat;
            session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 4000) });
            var context = NewContext(adapter);
            var baseSettings = new AppSettings
            {
                AutoCalibrateTokenEstimate = false,
                TokenEstimateMultiplier = 1
            };
            var scaledSettings = new AppSettings
            {
                AutoCalibrateTokenEstimate = false,
                TokenEstimateMultiplier = 2
            };
            var expectedBase = new PromptContextInspectorService(adapter, paths).Inspect(
                session, context, baseSettings, new ToolCatalogEntry[0], new SkillDefinition[0],
                new ChatAttachment[0], "question", false).UsedTokens;
            var expectedScaled = new PromptContextInspectorService(adapter, paths).Inspect(
                session, context, scaledSettings, new ToolCatalogEntry[0], new SkillDefinition[0],
                new ChatAttachment[0], "question", false).UsedTokens;
            AssertTrue(expectedScaled > expectedBase, "test settings produce distinct estimates");

            var service = new PromptContextInspectorService(adapter, paths);
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
                {
                    start.Wait();
                    var settings = index % 2 == 0 ? baseSettings : scaledSettings;
                    return service.Inspect(
                        session, context, settings, new ToolCatalogEntry[0], new SkillDefinition[0],
                        new ChatAttachment[0], "question", false).UsedTokens;
                })).ToArray();
                start.Set();
                Task.WaitAll(tasks);
                for (var index = 0; index < tasks.Length; index++)
                {
                    AssertEqual(index % 2 == 0 ? expectedBase : expectedScaled, tasks[index].Result,
                        "parallel inspection keeps its own token settings");
                }
            }
            });
        }
    }
}
