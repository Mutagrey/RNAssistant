using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void LlmStreamingResponseIsAggregated()
        {
            var decision = FinalBlock("ok");
            var split = decision.Length / 2;
            var sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Check range. \"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Use tool.\"}}]}\\n\\n" +
                "data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["content"] = decision.Substring(0, split) } }) }.ToString(Formatting.None) + "\\n\\n" +
                "data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["content"] = decision.Substring(split) } }), ["usage"] = new JObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15, ["completion_tokens_details"] = new JObject { ["reasoning_tokens"] = 2 } } }.ToString(Formatting.None) + "\\n\\n" +
                "data: [DONE]\\n\\n";

            var result = LlmResponseParser.ParseStreamingResponse(sse.Replace("\\n", "\n"));

            AssertTrue(new AgentPlannerResponseParser().Parse(result.Content).Success, "stream strict planner JSON");
            AssertEqual("Check range. Use tool.", result.ReasoningContent, "stream reasoning");
            AssertEqual(2, result.ReasoningTokens.Value, "stream reasoning tokens");
            AssertEqual(10, result.PromptTokens.Value, "stream prompt tokens");
            AssertEqual(5, result.CompletionTokens.Value, "stream completion tokens");
            AssertEqual(15, result.TotalTokens.Value, "stream total tokens");

            var updates = new List<LlmStreamUpdate>();
            var thinkStream =
                "data: {\"choices\":[{\"delta\":{\"content\":\"  <thi\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"nk>Inspect\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\" range.</thi\"}}]}\n\n" +
                "data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["content"] = "nk>" + decision } }), ["usage"] = new JObject { ["output_tokens_details"] = new JObject { ["reasoning_tokens"] = 7 } } }.ToString(Formatting.None) + "\n\n" +
                "data: [DONE]\n\n";
            var thinkResult = LlmResponseParser.ParseStreamingResponse(thinkStream, updates.Add);
            AssertEqual("Inspect range.", thinkResult.ReasoningContent, "split think stream reasoning");
            AssertTrue(new AgentPlannerResponseParser().Parse(thinkResult.Content).Success, "split think stream planner JSON");
            AssertEqual("Inspect range.", string.Concat(updates.Select(item => item.ReasoningDelta)), "think reasoning progress");
            AssertEqual(thinkResult.Content, string.Concat(updates.Select(item => item.ContentDelta)), "think content progress");
            AssertEqual(7, thinkResult.ReasoningTokens.Value, "output reasoning token alias");

            updates.Clear();
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(thinkStream)))
            {
                var sniffedResult = LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                    stream,
                    updates.Add,
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(thinkResult.Content, sniffedResult.Content, "stream body detected without content type");
                AssertTrue(updates.Any(item => item.Completed), "stream body completion progress");
                AssertEqual(sniffedResult.Content, string.Concat(updates.Select(item => item.ContentDelta)), "stream body live content progress");
            }

            updates.Clear();
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "{\"choices\":[{\"message\":{\"content\":\"ordinary json\"}}]}")))
            {
                var jsonResult = LlmResponseParser.ReadStreamingOrJsonResponseAsync(
                    stream,
                    updates.Add,
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("ordinary json", jsonResult.Content, "stream request accepts ordinary json fallback");
                AssertEqual(1, updates.Count, "ordinary json fallback reports completion only");
                AssertTrue(updates[0].Completed, "ordinary json fallback completion progress");
            }

            updates.Clear();
            var duplicateResult = LlmResponseParser.ParseStreamingResponse(
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"same\",\"content\":\"<think>same</think>Done\"}}]}\n\ndata: [DONE]\n\n",
                updates.Add);
            AssertEqual("same", duplicateResult.ReasoningContent, "stream metadata reasoning priority");
            AssertEqual("same", string.Concat(updates.Select(item => item.ReasoningDelta)), "stream duplicate reasoning suppressed");
            AssertEqual("Done", duplicateResult.Content, "stream duplicate think removed");

            var oversizedChunk = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["delta"] = new JObject
                        {
                            ["content"] = "<think>" + new string('s', 100001) + "</think>ok"
                        }
                    }
                }
            };
            var oversizedStream = LlmResponseParser.ParseStreamingResponse(
                "data: " + oversizedChunk.ToString(Formatting.None) + "\n\ndata: [DONE]\n\n");
            AssertEqual(100000, oversizedStream.ReasoningContent.Length, "stream reasoning storage limit");
            AssertTrue(oversizedStream.ReasoningTruncated, "stream reasoning truncation flag");
            AssertEqual("ok", oversizedStream.Content, "stream content survives reasoning truncation");
        }

        private static void LlmReasoningMetadataIsSeparated()
        {
            var decision = FinalBlock("ok");
            var result = LlmResponseParser.ParseStreamingResponse(
                "data: " + new JObject { ["choices"] = new JArray(new JObject { ["delta"] = new JObject { ["reasoning_content"] = "check facts", ["content"] = decision } }) }.ToString(Formatting.None) + "\n\ndata: [DONE]\n\n");
            AssertEqual("check facts", result.ReasoningContent, "reasoning metadata");
            AssertTrue(new AgentPlannerResponseParser().Parse(result.Content).Success, "decision content");

            var embeddedThink = LlmResponseParser.ParseCompletionResponse(
                new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["reasoning_content"] = "provider reasoning", ["content"] = "\n<think>duplicate</think>" + decision } }) }.ToString(Formatting.None));
            AssertEqual("provider reasoning", embeddedThink.ReasoningContent, "provider reasoning preserved");
            AssertTrue(new AgentPlannerResponseParser().Parse(embeddedThink.Content).Success, "duplicate think removed from planner content");

            var thinkOnly = LlmResponseParser.ParseCompletionResponse(
                "{\"choices\":[{\"message\":{\"content\":\" <think>local reasoning</think>Answer\"}}],\"usage\":{\"reasoning_tokens\":4}}");
            AssertEqual("local reasoning", thinkOnly.ReasoningContent, "leading think extracted");
            AssertEqual("Answer", thinkOnly.Content, "answer remains after think");
            AssertEqual(4, thinkOnly.ReasoningTokens.Value, "root reasoning token alias");

            var literalThink = LlmResponseParser.ParseCompletionResponse(
                "{\"choices\":[{\"message\":{\"content\":\"Answer with <think>literal</think> markup\"}}]}");
            AssertEqual("Answer with <think>literal</think> markup", literalThink.Content, "non-leading think preserved");

            var unclosedThink = LlmResponseParser.ParseCompletionResponse(
                "{\"choices\":[{\"message\":{\"content\":\"<think>unfinished reasoning\"}}]}");
            AssertEqual("unfinished reasoning", unclosedThink.ReasoningContent, "unclosed think treated as reasoning");
            AssertEqual(string.Empty, unclosedThink.Content, "unclosed think has no final content");

            var oversized = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["message"] = new JObject
                        {
                            ["content"] = "<think>" + new string('x', 100001) + "</think>ok"
                        }
                    }
                }
            };
            var truncated = LlmResponseParser.ParseCompletionResponse(oversized.ToString(Formatting.None));
            AssertEqual(100000, truncated.ReasoningContent.Length, "reasoning storage limit");
            AssertTrue(truncated.ReasoningTruncated, "reasoning truncation flag");
            AssertEqual("ok", truncated.Content, "content survives reasoning truncation");
        }

        private static void LlmAlternateCompletionFormatsAreRejected()
        {
            var canonical =
                "{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"ok\",\"steps\":[]}";
            var response = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["message"] = new JObject
                        {
                            ["content"] = new JArray
                            {
                                new JObject { ["type"] = "text", ["text"] = canonical.Substring(0, 30) },
                                new JObject
                                {
                                    ["type"] = "text",
                                    ["text"] = new JObject { ["value"] = canonical.Substring(30) }
                                }
                            }
                        }
                    }
                },
                ["usage"] = new JObject { ["input_tokens"] = 7, ["output_tokens"] = 3 }
            };
            try
            {
                LlmResponseParser.ParseCompletionResponse(response.ToString(Formatting.None));
                throw new InvalidOperationException("content parts were accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "content must be a string or null", "content parts rejected");
            }

            var nativeCallResponse = new JObject
            {
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["message"] = new JObject
                        {
                            ["content"] = null,
                            ["tool_calls"] = new JArray
                            {
                                new JObject
                                {
                                    ["function"] = new JObject
                                    {
                                        ["name"] = "excel.add_sheet",
                                        ["arguments"] = "{\"name\":\"Report\"}"
                                    }
                                }
                            }
                        }
                    }
                }
            };
            var nativeCall = LlmResponseParser.ParseCompletionResponse(nativeCallResponse.ToString(Formatting.None));
            AssertEqual("empty_response", new AgentPlannerResponseParser().Parse(nativeCall.Content).ErrorCode, "text parser does not infer native calls");
        }

        private static void PlainChatForwardsReasoningProgress()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var activities = new List<ChatActivity>();
                var reasoning = new string('r', 300);
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(
                        AppSettings settings,
                        IEnumerable<ChatMessage> messages,
                        LlmRequestOptions requestOptions,
                        Action<LlmStreamUpdate> streamProgress,
                        CancellationToken cancellationToken)
                    {
                        streamProgress(new LlmStreamUpdate { ReasoningDelta = reasoning });
                        streamProgress(new LlmStreamUpdate { ContentDelta = "Done." });
                        streamProgress(new LlmStreamUpdate { Completed = true });
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "Done.",
                            ReasoningContent = reasoning
                        });
                    });
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                service.ExecuteAsync(
                    "Hello",
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    delegate(string phase, string message, ChatActivity activity)
                    {
                        if (activity != null && string.Equals(activity.Kind, "reasoning", StringComparison.OrdinalIgnoreCase))
                        {
                            activities.Add(activity);
                        }
                    }).GetAwaiter().GetResult();

                AssertTrue(activities.Any(item => item.Status == "running" && item.ResultMessage == reasoning), "chat reasoning live progress");
                AssertTrue(activities.Any(item => item.Status == "completed"), "chat reasoning completion progress");
                AssertEqual(reasoning, session.Messages.Last().ReasoningContent, "chat reasoning stored");
            });
        }

        private static void LlmInvalidResponseEnvelopeIsReported()
        {
            try
            {
                LlmResponseParser.ParseCompletionResponse("{\"error\":{\"message\":\"model unavailable\"}}");
                throw new InvalidOperationException("missing message response was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "no choices[0].message", "missing message error");
                AssertContains(ex.Message, "model unavailable", "endpoint error detail");
            }

            try
            {
                LlmResponseParser.ParseCompletionResponse("not-json");
                throw new InvalidOperationException("invalid JSON response was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "not valid JSON", "invalid transport JSON error");
            }

            try
            {
                LlmResponseParser.ParseStreamingResponse("data: not-json\n\n");
                throw new InvalidOperationException("invalid stream chunk was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "stream chunk is not valid JSON", "invalid stream JSON error");
            }
        }

        private static void ChatPlannerIncludesRecentHistory()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var captured = new List<ChatMessage>();
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> streamProgress, CancellationToken cancellationToken)
                    {
                        captured = new List<ChatMessage>(messages);
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"kind\":\"final\",\"intent\":\"answer\",\"message\":\"done\",\"steps\":[]}"
                        });
                    });
                var session = NewSession(adapter);
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "Earlier question",
                    Attachments = new List<ChatAttachment>
                    {
                        new ChatAttachment { FileName = "old.txt", Kind = "text", ExtractedText = "OLD_ATTACHMENT_SENTINEL" },
                        new ChatAttachment { FileName = "old.png", Kind = "image" },
                        new ChatAttachment { FileName = "old.wav", Kind = "audio" }
                    }
                });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Earlier answer" });
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "INTERNAL_DIAGNOSTIC_SENTINEL",
                    Activity = new ChatActivity { Kind = "diagnostic", Status = "failed" }
                });
                var currentAttachment = new ChatAttachment
                {
                    FileName = "current.txt",
                    Kind = "text",
                    ExtractedText = "CURRENT_ATTACHMENT_SENTINEL"
                };
                service.ExecuteAsync(
                    "Follow up",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextWindowOverrideTokens = 32768 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    new[] { currentAttachment },
                    null).GetAwaiter().GetResult();

                AssertTrue(ContainsMessage(captured, "Earlier question"), "recent user history");
                AssertTrue(ContainsMessage(captured, "Earlier answer"), "recent assistant history");
                AssertTrue(!ContainsMessage(captured, "INTERNAL_DIAGNOSTIC_SENTINEL"), "internal activity excluded");
                AssertEqual(
                    1,
                    FlattenMessages(captured).Split(new[] { "Follow up" }, StringSplitOptions.None).Length - 1,
                    "active request is not duplicated");
                AssertEqual(1, captured.Sum(message => message.Attachments.Count(item => item.FileName == "old.txt")), "old attachment retained in history");
                AssertEqual(0, captured.Sum(message => message.Attachments.Count(item => item.FileName == "old.png")), "old image omitted from history");
                AssertEqual(0, captured.Sum(message => message.Attachments.Count(item => item.FileName == "old.wav")), "old audio omitted from history");
                AssertEqual(1, captured.Sum(message => message.Attachments.Count(item => item.FileName == "current.txt")), "current attachment included once");
            });
        }

        private static void ChatCompletionServiceRecordsProseResponse()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var capturedMessages = new List<ChatMessage>();
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> streamProgress, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        capturedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = FinalBlock("Done."),
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                var session = new ChatSession
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    Title = "New chat"
                };
                var context = new DocumentContext
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    Title = "Harness.xlsx"
                };
                context.Notes.Add(new ContextNote
                {
                    Id = "selection-1",
                    Host = "Excel",
                    Kind = "selection",
                    Title = "Selection",
                    Reference = "A1",
                    Text = "Selected cells"
                });
                context.Notes.Add(new ContextNote
                {
                    Id = "selection-duplicate",
                    Host = "Excel",
                    Kind = "selection",
                    Title = "Duplicate",
                    Reference = "A1",
                    Text = "DUPLICATE_CONTEXT_SENTINEL"
                });
                context.Notes.Add(new ContextNote
                {
                    Host = "Excel",
                    Kind = "note",
                    Title = "EMPTY_CONTEXT_SENTINEL",
                    Reference = "empty"
                });

                var result = service.ExecuteAsync(
                    "hello world",
                    session,
                    context,
                    new AppSettings(),
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual("hello world", session.Messages[0].Content, "user message");
                AssertEqual("Done.", session.Messages[1].Content, "assistant message");
                AssertEqual("New chat", session.Title, "session title");
                AssertTrue(ContainsMessage(capturedMessages, "User-added context:"), "context prompt captured");
                AssertTrue(!ContainsMessage(capturedMessages, "DUPLICATE_CONTEXT_SENTINEL"), "duplicate context excluded");
                AssertTrue(!ContainsMessage(capturedMessages, "EMPTY_CONTEXT_SENTINEL"), "empty context excluded");
            });
        }

        private static void ChatIncludesVbaContextWhenEnabled()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Sub Main()\nEnd Sub", "StdModule");
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, "Done.");
                var session = NewSession(adapter);

                service.ExecuteAsync(
                    "Analyze this workbook.",
                    session,
                    NewContext(adapter),
                    new AppSettings { IncludeVbaContext = true },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertTrue(calls.Count > 0, "llm call count");
                AssertContains(FlattenMessages(calls[0]), "Current VBA project snapshot", "vba prompt section");
                AssertContains(FlattenMessages(calls[0]), "Module1", "vba module name");
            });
        }

        private static void ChatVbaTaskAutoIncludesVbaContext()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Sub ExistingMacro()\nEnd Sub", "StdModule");
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, "Done.");
                var session = NewSession(adapter);

                service.ExecuteAsync(
                    "Review the VBA macro before changing it.",
                    session,
                    NewContext(adapter),
                    new AppSettings { IncludeVbaContext = false },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertTrue(calls.Count > 0, "llm call count");
                AssertContains(FlattenMessages(calls[0]), "Current VBA project snapshot", "auto vba prompt section");
                AssertContains(FlattenMessages(calls[0]), "Module1", "auto vba module name");
            });
        }

        private static void ChatCompletionServiceUsesDeferredSmartTitleSetting()
        {
            var requestedMessages = new List<ChatMessage>();
            var title = ChatTitleBuilder.GenerateLlmTitleAsync(
                new AppSettings(),
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.",
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, LlmRequestOptions requestOptions, Action<LlmStreamUpdate> streamProgress, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    requestedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                    AssertEqual(32, settings.MaxTokens, "title max tokens");
                    return Task.FromResult(new LlmCompletionResult { Content = "Продажи по месяцам." });
                },
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual("Продажи по месяцам", title, "llm title");
            AssertTrue(ContainsMessage(requestedMessages, "Запрос пользователя"), "title prompt contains user label");

            var fallbackSession = new ChatSession { Title = "New chat" };
            ChatTitleBuilder.ApplyFallback(
                fallbackSession,
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.");
            AssertEqual("Отчет по продажам создан и сохранен", fallbackSession.Title, "fallback title");
        }

        private static void ChatTitleBuilderTreatsLocalizedDraftTitlesAsAuto()
        {
            AssertTrue(ChatTitleBuilder.ShouldAssign(new ChatSession { Title = "New chat" }), "english draft title is auto");
            AssertTrue(ChatTitleBuilder.ShouldAssign(new ChatSession { Title = "Новый чат" }), "localized draft title is auto");
            AssertTrue(!ChatTitleBuilder.ShouldAssign(new ChatSession { Title = "Quarterly review" }), "custom title is preserved");
            AssertEqual("Как работает умное название чатов", ChatTitleBuilder.BuildDraftTitle("Проверь, как работает умное название чатов."), "draft title strips request prefix");
        }

        private static void GeneratedSmartTitleSurvivesCachedSessionSave()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new ChatStore(paths);
                var sessions = new ChatSessionService(adapter, store);
                var runs = new ChatRunRegistry();
                sessions.RunStateProvider = runs.Get;
                sessions.RunSessionsProvider = runs.Sessions;

                var draftTitle = ChatTitleBuilder.BuildDraftTitle("Сделай отчет по продажам за квартал");
                var session = store.Create(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    draftTitle);
                var sessionId = session.Id;
                sessions.SetActiveSession(session);

                using (runs.Start(sessionId, "run-title", session))
                {
                    AssertTrue(
                        sessions.TryApplyGeneratedTitle(
                            session.Host,
                            session.DocumentKey,
                            sessionId,
                            draftTitle,
                            "Продажи по кварталу"),
                        "smart title applied to running session");
                    AssertEqual("Продажи по кварталу", session.Title, "running session title updated");
                    AssertEqual(
                        "Продажи по кварталу",
                        sessions.GetChatSummaries(sessionId).First(item => item.Id == sessionId).Title,
                        "catalog uses live running session");
                }

                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Следующее сохранение" });
                store.Save(session);
                sessions.NotifySaved(session);
                AssertEqual("Продажи по кварталу", store.Load(sessionId).Title, "later save preserves smart title");

                session.Title = "Ручное название";
                store.Save(session);
                sessions.NotifySaved(session);
                AssertTrue(
                    !sessions.TryApplyGeneratedTitle(
                        session.Host,
                        session.DocumentKey,
                        sessionId,
                        draftTitle,
                        "Устаревшее название"),
                    "manual rename rejects stale smart title");
                AssertEqual("Ручное название", store.Load(sessionId).Title, "manual title preserved");
            });
        }
    }
}
