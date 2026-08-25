using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PromptContextInspectorBuildsAgentSnapshot()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            session.Mode = ChatModes.Agent;
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Проверь таблицу." });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                ProtocolMessage = true,
                RunId = "run-1",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "call_1",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"address\":\"A1:B4\"}"
                    }
                }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                ProtocolMessage = true,
                RunId = "run-1",
                ToolCallId = "call_1",
                ToolName = "excel.read_range",
                Content = "TOOL_RESULT: {\"ok\":true,\"tool_call_id\":\"call_1\",\"name\":\"excel.read_range\",\"status\":\"completed\",\"message\":\"Read\"}"
            });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Диапазон прочитан." });
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "plan_r1",
                Kind = ChatArtifactKinds.Plan,
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
            var tools = adapter.GetBuiltInTools()
                .Where(tool => tool.Id == "excel.read_range" || tool.Id == "excel.add_sheet")
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

            var result = new PromptContextInspectorService(adapter, null).Inspect(
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
            AssertTrue(result.Sections.Any(section => section.Id == "tools"), "tool schemas are visible");
            AssertTrue(result.Sections.Any(section => section.Id == "skills"), "skill catalog is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "tool_history"), "tool protocol history is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "document_context"), "document context is visible");
            AssertTrue(result.Sections.Any(section => section.Id == "artifacts"), "artifact index is visible");
            AssertEqual(result.UsedTokens, result.Sections.Where(section => section.Included).Sum(section => section.Tokens),
                "included section totals match prompt estimate");
            AssertTrue(result.RawRequestJson == null, "raw request is not built by default");
        }

        private static void PromptContextInspectorRawJsonIsOptIn()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            session.Mode = ChatModes.Chat;
            session.Messages.Add(new ChatMessage { Role = "user", Content = "История" });
            var service = new PromptContextInspectorService(adapter, null);

            var compact = service.Inspect(
                session,
                NewContext(adapter),
                new AppSettings(),
                new ToolDefinition[0],
                new SkillDefinition[0],
                new ChatAttachment[0],
                "Новый вопрос",
                false);
            var raw = service.Inspect(
                session,
                NewContext(adapter),
                new AppSettings(),
                new ToolDefinition[0],
                new SkillDefinition[0],
                new ChatAttachment[0],
                "Новый вопрос",
                true);

            AssertTrue(compact.RawRequestJson == null, "compact inspection skips raw serialization");
            AssertContains(raw.RawRequestJson, "Новый вопрос", "raw structure is generated explicitly");
            AssertTrue(!raw.Sections.Any(section => section.Id == "tools" || section.Id == "skills"),
                "chat mode has no agent catalogs");
        }

        private static void PromptContextInspectorIsolatesConcurrentSettings()
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
            var expectedBase = new PromptContextInspectorService(adapter, null).Inspect(
                session, context, baseSettings, new ToolDefinition[0], new SkillDefinition[0],
                new ChatAttachment[0], "question", false).UsedTokens;
            var expectedScaled = new PromptContextInspectorService(adapter, null).Inspect(
                session, context, scaledSettings, new ToolDefinition[0], new SkillDefinition[0],
                new ChatAttachment[0], "question", false).UsedTokens;
            AssertTrue(expectedScaled > expectedBase, "test settings produce distinct estimates");

            var service = new PromptContextInspectorService(adapter, null);
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
                {
                    start.Wait();
                    var settings = index % 2 == 0 ? baseSettings : scaledSettings;
                    return service.Inspect(
                        session, context, settings, new ToolDefinition[0], new SkillDefinition[0],
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
        }
    }
}
