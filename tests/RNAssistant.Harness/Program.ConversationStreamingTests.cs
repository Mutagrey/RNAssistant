using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ConversationStreamExtractorHandlesChunkedJson()
        {
            var extractor = new ConversationMessageStreamExtractor();
            var chunks = new[]
            {
                "\uFEFF {\"tool_calls\":[],\"meta\":{\"message\":\"ignore\"},\"mes",
                "sage\":\"Line 1\\nquote: \\\"",
                "ok\\\" \\\\ slash \\uD83D",
                "\\uDE00\"}"
            };
            var visible = string.Concat(chunks.Select(extractor.Add).ToArray()) + extractor.Complete();

            AssertEqual("Line 1\nquote: \"ok\" \\ slash 😀", visible,
                "extractor decodes only the chunked root message");
            AssertEqual(string.Empty, extractor.Add("  \r\n"), "trailing whitespace produces no delta");
        }

        private static void ConversationStreamsMessageAndThinking()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var thinking = new string('р', 300);
                var responseChunks = new[]
                {
                    "<think>" + thinking,
                    "</think>{\"meta\":{\"message\":\"скрыто\"},\"mes",
                    "sage\":\"Привет\\nмир \\uD83D",
                    "\\uDE00\",\"tool_calls\":[]}"
                };
                LlmCompletionDelegate completion = (settings, messages, options, streamProgress, cancellationToken) =>
                {
                    AssertEqual(true, options.ReasoningEnabled.Value, "thinking mode reaches model request");
                    return Task.FromResult(LlmResponseParser.ParseStreamingResponse(
                        BuildContentSse(responseChunks), streamProgress));
                };
                var progress = new List<Tuple<string, string, ChatActivity>>();
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.ReasoningEnabled = true;

                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Ответь потоково.",
                    session,
                    NewContext(adapter),
                    new AppSettings { StreamResponses = true },
                    new ToolDefinition[0],
                    (phase, message, activity) => progress.Add(Tuple.Create(phase, message, activity)))
                    .GetAwaiter().GetResult();

                var streamed = progress
                    .Where(item => item.Item1 == "streaming" && !string.IsNullOrEmpty(item.Item2))
                    .Select(item => item.Item2)
                    .ToArray();
                var reasoning = progress
                    .Where(item => item.Item3 != null && item.Item3.Kind == "reasoning")
                    .Select(item => item.Item3)
                    .ToList();
                AssertEqual("Привет\nмир 😀", string.Concat(streamed), "stream exposes decoded message only");
                AssertTrue(streamed.All(value => value.IndexOf("tool_calls", StringComparison.Ordinal) < 0 &&
                    value.IndexOf("message", StringComparison.Ordinal) < 0), "raw envelope stays hidden");
                AssertTrue(progress.Any(item => item.Item1 == "streaming" && item.Item2 == string.Empty),
                    "model step starts with a stream reset");
                AssertEqual(thinking, string.Concat(reasoning.Select(item => item.ResultMessage).ToArray()),
                    "chunked think content is preserved");
                AssertTrue(reasoning.Any(item => item.Status == "running"), "long thinking is shown before completion");
                AssertEqual("completed", reasoning.Last().Status, "thinking receives a terminal update");
                AssertEqual("Привет\nмир 😀", result.AssistantText, "final message matches streamed projection");
                AssertEqual(thinking, session.Messages.Last().ReasoningContent, "thinking remains separate in history");
            });
        }

        private static void ConversationStreamResetsBetweenAttempts()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Черновик\",\"tool_calls\":\"invalid\"}",
                    "{\"message\":\"Исправлено.\",\"tool_calls\":[]}"
                });
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, streamProgress, cancellationToken) =>
                {
                    calls += 1;
                    var response = responses.Dequeue();
                    streamProgress(new LlmStreamUpdate { ContentDelta = response });
                    streamProgress(new LlmStreamUpdate { Completed = true });
                    return Task.FromResult(new LlmCompletionResult { Content = response });
                };
                var streamEvents = new List<string>();
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;

                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Исправь формат.",
                    session,
                    NewContext(adapter),
                    new AppSettings { StreamResponses = true, MaxAgentFormatRetries = 1 },
                    new ToolDefinition[0],
                    (phase, message, activity) =>
                    {
                        if (phase == "streaming") streamEvents.Add(message);
                    }).GetAwaiter().GetResult();

                AssertEqual(2, calls, "invalid envelope gets one repair request");
                AssertEqual(4, streamEvents.Count, "each attempt emits reset and visible message");
                AssertEqual(string.Empty, streamEvents[0], "first attempt reset");
                AssertEqual("Черновик", streamEvents[1], "first attempt message");
                AssertEqual(string.Empty, streamEvents[2], "repair attempt reset");
                AssertEqual("Исправлено.", streamEvents[3], "repaired attempt message");
                AssertEqual("Исправлено.", result.AssistantText, "repaired response completes");
            });
        }

        private static string BuildContentSse(IEnumerable<string> chunks)
        {
            var builder = new StringBuilder();
            foreach (var chunk in chunks ?? new string[0])
            {
                builder.Append("data: ").Append(JsonConvert.SerializeObject(new
                {
                    choices = new[] { new { delta = new { content = chunk } } }
                })).Append("\n\n");
            }
            return builder.Append("data: [DONE]\n\n").ToString();
        }
    }
}
