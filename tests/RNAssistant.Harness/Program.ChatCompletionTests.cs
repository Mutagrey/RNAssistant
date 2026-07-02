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
            var sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Check range. \"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Use tool.\"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"kind\\\":\\\"final\\\",\\\"intent\\\":\\\"answer\\\",\"}}]}\\n\\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"message\\\":\\\"ok\\\",\\\"steps\\\":[]}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}\\n\\n" +
                "data: [DONE]\\n\\n";

            var result = LlmClient.ParseStreamingResponse(sse.Replace("\\n", "\n"));

            AssertTrue(new AgentPlannerResponseParser().Parse(result.Content).Success, "stream strict planner JSON");
            AssertEqual("Check range. Use tool.", result.ReasoningContent, "stream reasoning");
            AssertEqual(2, result.ReasoningTokens.Value, "stream reasoning tokens");
            AssertEqual(10, result.PromptTokens.Value, "stream prompt tokens");
            AssertEqual(5, result.CompletionTokens.Value, "stream completion tokens");
            AssertEqual(15, result.TotalTokens.Value, "stream total tokens");
        }

        private static void LlmReasoningMetadataIsSeparated()
        {
            var result = LlmClient.ParseStreamingResponse(
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"check facts\",\"content\":\"{\\\"kind\\\":\\\"final\\\",\\\"intent\\\":\\\"answer\\\",\\\"message\\\":\\\"ok\\\",\\\"steps\\\":[]}\"}}]}\n\ndata: [DONE]\n\n");
            AssertEqual("check facts", result.ReasoningContent, "reasoning metadata");
            AssertTrue(result.Content.StartsWith("{\"kind\":\"final\"", StringComparison.Ordinal), "planner content");

            var embeddedThink = LlmClient.ParseCompletionResponse(
                "{\"choices\":[{\"message\":{\"reasoning_content\":\"provider reasoning\",\"content\":\"\\n<think>duplicate</think>{\\\"kind\\\":\\\"final\\\",\\\"intent\\\":\\\"answer\\\",\\\"message\\\":\\\"ok\\\",\\\"steps\\\":[]}\"}}]}");
            AssertEqual("provider reasoning", embeddedThink.ReasoningContent, "provider reasoning preserved");
            AssertEqual("not_json_object", new AgentPlannerResponseParser().Parse(embeddedThink.Content).ErrorCode, "embedded think tag rejected");
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
                LlmClient.ParseCompletionResponse(response.ToString(Formatting.None));
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
            var nativeCall = LlmClient.ParseCompletionResponse(nativeCallResponse.ToString(Formatting.None));
            AssertEqual("empty_response", new AgentPlannerResponseParser().Parse(nativeCall.Content).ErrorCode, "native tool calls ignored");
        }

        private static void LlmInvalidResponseEnvelopeIsReported()
        {
            try
            {
                LlmClient.ParseCompletionResponse("{\"error\":{\"message\":\"model unavailable\"}}");
                throw new InvalidOperationException("missing message response was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "no choices[0].message", "missing message error");
                AssertContains(ex.Message, "model unavailable", "endpoint error detail");
            }

            try
            {
                LlmClient.ParseCompletionResponse("not-json");
                throw new InvalidOperationException("invalid JSON response was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "not valid JSON", "invalid transport JSON error");
            }

            try
            {
                LlmClient.ParseStreamingResponse("data: not-json\n\n");
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
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
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
                        new ChatAttachment { FileName = "old.txt", Kind = "text", ExtractedText = "OLD_ATTACHMENT_SENTINEL" }
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
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
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
                    new AppSettings { ContextCharLimit = 8000 },
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
                    new AppSettings { ContextCharLimit = 8000, IncludeVbaContext = true },
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
                    new AppSettings { ContextCharLimit = 8000, IncludeVbaContext = false },
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
                new AppSettings { ContextCharLimit = 8000 },
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.",
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
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
    }
}
