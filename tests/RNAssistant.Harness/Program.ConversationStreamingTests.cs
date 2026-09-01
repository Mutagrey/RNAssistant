using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                    "</think>{\"mes",
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

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Ответь потоково.",
                    session,
                    NewContext(adapter),
                    new AppSettings { StreamResponses = true },
                    new ToolCatalogEntry[0],
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
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "streamed response status");
                AssertEqual(thinking, session.Messages.Last().ReasoningContent, "thinking remains separate in history");
            });
        }

        private static void ConversationStreamsProviderReasoning()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var sse = string.Join("", new[]
                {
                    BuildDeltaSse("reasoning_content", "Размышление "),
                    BuildDeltaSse("reasoning", "продолжается."),
                    BuildDeltaSse("content", "{\"message\":\"Ответ"),
                    BuildDeltaSse("content", ".\",\"tool_calls\":[]}"),
                    "data: [DONE]\n\n"
                });
                LlmCompletionDelegate completion = (settings, messages, options, streamProgress, cancellationToken) =>
                    Task.FromResult(LlmResponseParser.ParseStreamingResponse(sse, streamProgress));
                var progress = new List<Tuple<string, string, ChatActivity>>();
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.ReasoningEnabled = true;

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Ответь с reasoning.",
                    session,
                    NewContext(adapter),
                    new AppSettings { StreamResponses = true },
                    new ToolCatalogEntry[0],
                    (phase, message, activity) => progress.Add(Tuple.Create(phase, message, activity)))
                    .GetAwaiter().GetResult();

                var reasoning = progress
                    .Where(item => item.Item3 != null && item.Item3.Kind == "reasoning")
                    .Select(item => item.Item3.ResultMessage)
                    .ToArray();
                AssertEqual("Размышление продолжается.", string.Concat(reasoning),
                    "provider reasoning fields are streamed separately");
                AssertEqual("Ответ.", result.AssistantText, "provider reasoning does not enter visible message");
                AssertEqual("Размышление продолжается.", session.Messages.Last().ReasoningContent,
                    "provider reasoning is persisted separately");
            });
        }

        private static void ConversationStreamResetsBetweenAttempts()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"status\":\"in_progress\",\"message\":\"Жил-был потоковый черновик.\",\"tool_calls\":[]}",
                    "{\"message\":\"Исправлено.\",\"tool_calls\":[]}"
                });
                var thoughts = new Queue<string>(new[] { "Первая мысль.", "Исправленная мысль." });
                var calls = 0;
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var traces = new List<LlmTraceRecord>();
                LlmCompletionDelegate completion = (settings, messages, options, streamProgress, cancellationToken) =>
                {
                    calls += 1;
                    requests.Add(messages.ToList());
                    if (options.TraceSink == null) options.TraceSink = record => traces.Add(record);
                    var response = responses.Dequeue();
                    streamProgress(new LlmStreamUpdate { ReasoningDelta = thoughts.Dequeue() });
                    streamProgress(new LlmStreamUpdate { ContentDelta = response });
                    streamProgress(new LlmStreamUpdate { Completed = true });
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = response,
                        ReasoningContent = calls == 1 ? "Первая мысль." : "Исправленная мысль."
                    });
                };
                var progressEvents = new List<Tuple<string, string, ChatActivity>>();
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.ReasoningEnabled = true;

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Исправь формат.",
                    session,
                    NewContext(adapter),
                    new AppSettings { StreamResponses = true, MaxAgentFormatRetries = 2 },
                    new ToolCatalogEntry[0],
                    (phase, message, activity) => progressEvents.Add(Tuple.Create(phase, message, activity)))
                    .GetAwaiter().GetResult();

                var streamEvents = progressEvents
                    .Where(item => item.Item1 == "streaming")
                    .Select(item => item.Item2)
                    .ToList();
                var reasoning = progressEvents
                    .Where(item => item.Item3 != null && item.Item3.Kind == "reasoning")
                    .Select(item => item.Item3.ResultMessage)
                    .ToList();

                AssertEqual(2, calls, "status/tool_calls mismatch gets one repair request");
                AssertContains(requests[1].Last().Content, "unsupported root field: status",
                    "repair receives the first parser rejection");
                AssertEqual(2, traces.Count, "rejected and accepted parser verdicts are traced");
                AssertEqual("rejected", traces[0].Type, "trace identifies the rejected response");
                AssertContains(traces[0].PayloadJson, "Жил-был потоковый черновик.",
                    "trace preserves the first rejected payload");
                AssertContains(traces[0].Error, "unsupported root field: status",
                    "trace preserves the exact parser error");
                AssertEqual("accepted", traces[1].Type, "repair acceptance has its own marker");
                AssertTrue(traces[1].ResponseStatus == null, "accepted v3 marker has no model-declared lifecycle status");
                AssertTrue(traces[1].PayloadJson == null, "accepted marker does not duplicate model content");
                AssertEqual(4, streamEvents.Count, "each attempt emits reset and visible message");
                AssertEqual(string.Empty, streamEvents[0], "first attempt reset");
                AssertEqual("Жил-был потоковый черновик.", streamEvents[1], "first attempt message");
                AssertEqual(string.Empty, streamEvents[2], "repair attempt reset");
                AssertEqual("Исправлено.", streamEvents[3], "repaired attempt message");
                AssertTrue(reasoning.SequenceEqual(new[] { "Первая мысль.", "Исправленная мысль." }),
                    "thinking is isolated per model attempt");
                AssertTrue(!progressEvents.Any(item =>
                    (item.Item2 ?? string.Empty).IndexOf("исправляет структуру", StringComparison.OrdinalIgnoreCase) >= 0),
                    "internal format repair is not exposed as UI status");
                AssertEqual("Исправлено.", result.AssistantText, "repaired response completes");
                AssertEqual("Исправленная мысль.", session.Messages.Last().ReasoningContent,
                    "only accepted thinking is persisted");
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

        private static string BuildDeltaSse(string name, string value)
        {
            return "data: " + new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["delta"] = new JObject { [name] = value }
                })
            }.ToString(Formatting.None) + "\n\n";
        }
    }
}
