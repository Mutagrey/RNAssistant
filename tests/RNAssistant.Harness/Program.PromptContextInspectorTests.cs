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

        private static void PromptContextInspectorRawSerializationIsBounded()
        {
            const int limit = 512000;
            Func<string, string> baseline = content => Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                mode = "chat", model = "model", estimated = true,
                messages = new[] { new { role = "user", content, protocolMessage = false, toolCalls = new object[0], attachments = new object[0] } }
            }, Newtonsoft.Json.Formatting.Indented);
            var overhead = baseline("").Length;
            var start = baseline("").IndexOf("\"content\": \"", StringComparison.Ordinal) + "\"content\": \"".Length;
            foreach (var content in new[] { "quote \" newline\n emoji 🦊", new string('x', limit - overhead - 1),
                new string('x', limit - overhead), new string('x', limit - overhead + 1),
                new string('\n', limit * 2), new string('x', limit - start - 1) + "🦊tail" })
            {
                var expected = baseline(content);
                var isTruncated = expected.Length > limit;
                var length = Math.Min(expected.Length, limit);
                if (isTruncated && char.IsHighSurrogate(expected[length - 1])) length--;
                expected = expected.Substring(0, length) + (isTruncated ? "\n\n[structure truncated]" : "");
                bool truncated;
                var actual = PromptContextInspectorService.BuildRawRequest("chat", "model",
                    new[] { new ChatMessage { Role = "user", Content = content } }, null, out truncated);
                AssertEqual(isTruncated, truncated, "exact preview boundary flag");
                AssertEqual(expected, actual, "bounded JSON preserves the original serialized prefix");
                AssertTrue(new System.Text.UTF8Encoding(false, true).GetByteCount(actual) <= PromptContextInspectorDownloadService.MaximumBytes,
                    "Unicode-safe preview fits existing download admission");
            }
            bool stopped;
            PromptContextInspectorService.BuildRawRequest("chat", "model", InspectorOversizedThenThrow(), null, out stopped);
            AssertTrue(stopped, "preview stops before enumerating the remaining request");
        }

        private static IEnumerable<ChatMessage> InspectorOversizedThenThrow()
        {
            yield return new ChatMessage { Role = "user", Content = new string('\n', 4 * 1024 * 1024) };
            throw new InvalidOperationException("Inspector must not enumerate messages after the preview limit.");
        }

        private static void PromptContextInspectorExactDownload()
        {
            var session = new ChatSession { Id = "inspector-chat" };
            using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
            {
                var service = new PromptContextInspectorDownloadService(data);
                foreach (var raw in new[] { "", "\uFEFF{\"text\":\"" + new string('語', 180000) + "😀\"}" })
                {
                    var captures = 0;
                    var result = service.Open(session, () =>
                    {
                        captures++;
                        return new PromptContextInspectorResponse { ChatId = session.Id, RawRequestJson = raw, RawTruncated = true };
                    }, CancellationToken.None);
                    AssertEqual(1, captures, "exactly one capture");
                    AssertTrue(result.RawRequestJson == null && result.RawTruncated, "only metadata survives capture");
                    var json = Newtonsoft.Json.Linq.JObject.FromObject(result);
                    AssertTrue(json["rawRequestJson"] == null && json["rawData"] != null, "metadata-only bridge shape");
                    using (var bytes = new System.IO.MemoryStream())
                    {
                        for (var offset = 0; offset < result.RawData.Payload.ByteLength;)
                        {
                            var count = (int)Math.Min(result.RawData.MaxChunkBytes, result.RawData.Payload.ByteLength - offset);
                            string mime;
                            var chunk = data.ReadDownload(result.RawData.LeaseId, offset, count, CancellationToken.None, out mime);
                            AssertEqual("text/plain; charset=utf-8", mime, "inert preview MIME");
                            bytes.Write(chunk, 0, chunk.Length);
                            offset += chunk.Length;
                        }
                        AssertEqual(raw, new System.Text.UTF8Encoding(false, true).GetString(bytes.ToArray()), "exact UTF-8 source, including BOM");
                    }
                    data.Close(session.Id, PromptContextInspectorDownloadService.Owner, result.RawData.LeaseId);
                }
            }
        }

        private static void PromptContextInspectorDownloadGuards()
        {
            var session = new ChatSession { Id = "inspector-guards" };
            using (var data = new ResourceDataPlaneService(new ResourceGatewayService()))
            {
                var service = new PromptContextInspectorDownloadService(data);
                var captures = 0;
                Func<PromptContextInspectorResponse> capture = () =>
                {
                    captures++;
                    return new PromptContextInspectorResponse { ChatId = session.Id, RawRequestJson = "{}" };
                };
                var first = service.Open(session, capture, CancellationToken.None);
                var second = service.Open(session, capture, CancellationToken.None);
                RuntimeThrows<ResourceRequestException>(() => service.Open(session, capture, CancellationToken.None));
                AssertEqual(2, captures, "reservation refuses before expensive inspection");
                data.Close(session.Id, PromptContextInspectorDownloadService.Owner, first.RawData.LeaseId);
                data.Close(session.Id, PromptContextInspectorDownloadService.Owner, second.RawData.LeaseId);
                RuntimeThrows<OperationCanceledException>(() => service.Open(session, capture, new CancellationToken(true)));
                AssertEqual(2, captures, "cancelled request does not capture");
                foreach (var invalid in new[] {
                    new PromptContextInspectorResponse { ChatId = "other", RawRequestJson = "{}" },
                    new PromptContextInspectorResponse { ChatId = session.Id },
                    new PromptContextInspectorResponse { ChatId = session.Id, RawRequestJson = new string('x', PromptContextInspectorDownloadService.MaximumBytes + 1) }
                })
                {
                    RuntimeThrows<InvalidOperationException>(() => service.Open(session, () => invalid, CancellationToken.None));
                    AssertTrue(invalid.RawRequestJson == null, "failed capture clears transient body");
                }
                var cancelled = new PromptContextInspectorResponse { ChatId = session.Id, RawRequestJson = "{}" };
                using (var cancel = new CancellationTokenSource())
                    RuntimeThrows<OperationCanceledException>(() => service.Open(session, () => { cancel.Cancel(); return cancelled; }, cancel.Token));
                AssertTrue(cancelled.RawRequestJson == null, "cancel after capture clears body");
                first = service.Open(session, capture, CancellationToken.None);
                second = service.Open(session, capture, CancellationToken.None);
                AssertTrue(first.RawData != null && second.RawData != null, "failed captures release both download slots");
            }
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
