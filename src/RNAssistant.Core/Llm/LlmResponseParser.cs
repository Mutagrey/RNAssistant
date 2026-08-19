using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Llm
{
    internal static class LlmResponseParser
    {
        private const int MaxStoredReasoningChars = 24000;
        private const int MaxStoredRefusalChars = 12000;
        private const int MaxStoredContentChars = 16 * 1024 * 1024;
        private const int MaxToolArgumentsChars = 4 * 1024 * 1024;
        private const int MaxToolCalls = 64;
        private const int MaxUsageJsonChars = 64 * 1024;

        internal static LlmCompletionResult ParseStreamingResponse(string sse)
        {
            return ParseStreamingResponse(sse, null);
        }

        internal static LlmCompletionResult ParseStreamingResponse(string sse, Action<LlmStreamUpdate> streamProgress)
        {
            var state = new StreamingCompletionState(streamProgress);
            using (var reader = new StringReader(sse ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (ProcessStreamingLine(line, state)) break;
                }
            }
            var result = state.ToResult();
            ReportCompleted(streamProgress);
            return result;
        }

        internal static LlmCompletionResult ParseCompletionResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                throw new InvalidOperationException("LLM response body is empty.");
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(responseJson.TrimStart('\uFEFF'));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("LLM response is not valid JSON: " + ex.Message, ex);
            }

            var message = parsed.SelectToken("choices[0].message") as JObject;
            if (message == null)
            {
                var error = parsed.SelectToken("error.message");
                var suffix = error != null && error.Type == JTokenType.String
                    ? " Endpoint error: " + error.Value<string>()
                    : string.Empty;
                throw new InvalidOperationException("LLM response has no choices[0].message." + suffix);
            }

            return BuildCompletionResult(message, parsed["usage"] as JObject);
        }

        internal static async Task<LlmCompletionResult> ReadStreamingOrJsonResponseAsync(
            Stream stream,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken)
        {
            return await ReadStreamingOrJsonResponseAsync(stream, streamProgress, cancellationToken, null).ConfigureAwait(false);
        }

        internal static async Task<LlmCompletionResult> ReadStreamingOrJsonResponseAsync(
            Stream stream,
            Action<LlmStreamUpdate> streamProgress,
            CancellationToken cancellationToken,
            Action<string> rawJsonProgress)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            var state = new StreamingCompletionState(streamProgress);
            var bufferedJson = new StringBuilder();
            bool? isEventStream = null;
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            using (cancellationToken.Register(streamState => ((Stream)streamState).Dispose(), stream))
            {
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (line.Length > MaxStoredContentChars)
                        {
                            throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response line exceeds the safety limit.");
                        }

                        if (!isEventStream.HasValue && !string.IsNullOrWhiteSpace(line))
                        {
                            var probe = line.TrimStart('\uFEFF').TrimStart();
                            if (!string.IsNullOrWhiteSpace(probe))
                            {
                                isEventStream = IsEventStreamLine(probe);
                            }
                        }

                        if (isEventStream == true)
                        {
                            if (ProcessStreamingLine(line, state, rawJsonProgress)) break;
                        }
                        else if (isEventStream == false)
                        {
                            if (bufferedJson.Length + line.Length + 2 > MaxStoredContentChars)
                            {
                                throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response exceeds the safety limit.");
                            }
                            if (bufferedJson.Length > 0) bufferedJson.AppendLine();
                            bufferedJson.Append(line);
                        }
                    }
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            if (isEventStream != true)
            {
                var bufferedResponse = bufferedJson.ToString();
                if (rawJsonProgress != null) rawJsonProgress(bufferedResponse);
                var jsonResult = ParseCompletionResponse(bufferedResponse);
                ReportCompleted(streamProgress);
                return jsonResult;
            }

            var result = state.ToResult();
            ReportCompleted(streamProgress);
            return result;
        }

        private static void ReportCompleted(Action<LlmStreamUpdate> streamProgress)
        {
            if (streamProgress != null)
            {
                streamProgress(new LlmStreamUpdate { Completed = true });
            }
        }

        private static bool IsEventStreamLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(":", StringComparison.Ordinal);
        }

        private static bool ProcessStreamingLine(string line, StreamingCompletionState state)
        {
            return ProcessStreamingLine(line, state, null);
        }

        private static bool ProcessStreamingLine(string line, StreamingCompletionState state, Action<string> rawJsonProgress)
        {
            if (state == null || string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var data = trimmed.Substring(5).Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (data.Length == 0)
            {
                return false;
            }

            try
            {
                if (rawJsonProgress != null) rawJsonProgress(data);
                var chunk = JObject.Parse(data.TrimStart('\uFEFF'));
                state.Add(chunk);
                return false;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("LLM stream chunk is not valid JSON: " + ex.Message, ex);
            }
        }

        private sealed class StreamingCompletionState
        {
            private readonly StringBuilder _content = new StringBuilder();
            private readonly StringBuilder _reasoning = new StringBuilder();
            private readonly StringBuilder _embeddedReasoning = new StringBuilder();
            private readonly StringBuilder _refusal = new StringBuilder();
            private readonly Action<LlmStreamUpdate> _progress;
            private readonly ThinkStreamSplitter _thinkSplitter;
            private readonly IDictionary<int, StreamingToolCallState> _toolCalls = new SortedDictionary<int, StreamingToolCallState>();
            private JObject _usage;
            private bool _reasoningTruncated;
            private bool _embeddedReasoningTruncated;
            private bool _embeddedProgressReported;

            public StreamingCompletionState(Action<LlmStreamUpdate> progress = null)
            {
                _progress = progress;
                _thinkSplitter = new ThinkStreamSplitter(AddContent, AddEmbeddedReasoning);
            }

            public void Add(JObject chunk)
            {
                if (chunk == null)
                {
                    return;
                }

                var usage = chunk["usage"] as JObject;
                if (usage != null)
                {
                    _usage = usage;
                }

                var delta = chunk.SelectToken("choices[0].delta") as JObject;
                if (delta == null)
                {
                    return;
                }

                var content = ReadStringToken(delta["content"], "choices[0].delta.content");
                if (!string.IsNullOrEmpty(content))
                {
                    _thinkSplitter.Add(content);
                }

                var reasoning = ReadStringToken(
                    delta["reasoning_content"] ?? delta["reasoning"],
                    "choices[0].delta.reasoning");
                if (!string.IsNullOrEmpty(reasoning))
                {
                    AddReasoning(reasoning);
                }

                var refusal = ReadStringToken(delta["refusal"], "choices[0].delta.refusal");
                if (!string.IsNullOrEmpty(refusal))
                {
                    AddRefusal(refusal);
                }

                var toolCalls = delta["tool_calls"] as JArray;
                if (toolCalls != null)
                {
                    foreach (var token in toolCalls.OfType<JObject>())
                    {
                        var index = token["index"] == null ? 0 : token["index"].Value<int>();
                        StreamingToolCallState call;
                        if (!_toolCalls.TryGetValue(index, out call))
                        {
                            if (_toolCalls.Count >= MaxToolCalls)
                            {
                                throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response contains too many tool calls.");
                            }
                            call = new StreamingToolCallState();
                            _toolCalls[index] = call;
                        }
                        call.Add(token);
                    }
                }
            }

            public LlmCompletionResult ToResult()
            {
                _thinkSplitter.Complete();
                var reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : _embeddedReasoning.ToString();
                var message = new JObject
                {
                    ["content"] = _content.ToString(),
                    ["reasoning_content"] = reasoning,
                    ["refusal"] = _refusal.ToString()
                };
                if (_toolCalls.Count > 0)
                {
                    message["tool_calls"] = new JArray(_toolCalls.Values.Select(call => call.ToJson()));
                }
                var result = BuildCompletionResult(message, _usage);
                result.ReasoningTruncated = _reasoning.Length > 0
                    ? _reasoningTruncated
                    : _embeddedReasoningTruncated;
                return result;
            }

            private void AddContent(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }
                if (_content.Length + value.Length > MaxStoredContentChars)
                {
                    throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response content exceeds the safety limit.");
                }
                _content.Append(value);
                if (_progress != null)
                {
                    _progress(new LlmStreamUpdate { ContentDelta = value });
                }
            }

            private void AddReasoning(string value)
            {
                var stored = AppendLimited(_reasoning, value, ref _reasoningTruncated);
                if (!_embeddedProgressReported && _progress != null && stored.Length > 0)
                {
                    _progress(new LlmStreamUpdate { ReasoningDelta = stored });
                }
            }

            private void AddEmbeddedReasoning(string value)
            {
                var stored = AppendLimited(_embeddedReasoning, value, ref _embeddedReasoningTruncated);
                if (_reasoning.Length == 0 && _progress != null && stored.Length > 0)
                {
                    _embeddedProgressReported = true;
                    _progress(new LlmStreamUpdate { ReasoningDelta = stored });
                }
            }

            private void AddRefusal(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }
                var remaining = MaxStoredRefusalChars - _refusal.Length;
                if (remaining <= 0)
                {
                    return;
                }
                _refusal.Append(value.Length <= remaining ? value : value.Substring(0, remaining));
            }

            private static string AppendLimited(StringBuilder target, string value, ref bool truncated)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }
                var remaining = MaxStoredReasoningChars - target.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    return string.Empty;
                }
                var stored = value.Length <= remaining ? value : value.Substring(0, remaining);
                target.Append(stored);
                if (stored.Length < value.Length)
                {
                    truncated = true;
                }
                return stored;
            }
        }

        private sealed class StreamingToolCallState
        {
            private readonly StringBuilder _name = new StringBuilder();
            private readonly StringBuilder _arguments = new StringBuilder();
            private string _id;
            private string _type;

            public void Add(JObject token)
            {
                if (token == null) return;
                if (token["id"] != null) _id = BoundedMetadata((string)token["id"]);
                if (token["type"] != null) _type = BoundedMetadata((string)token["type"]);
                var function = token["function"] as JObject;
                if (function == null) return;
                if (function["name"] != null)
                {
                    var name = (string)function["name"] ?? string.Empty;
                    if (_name.Length + name.Length > 1024)
                    {
                        throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM tool name exceeds the safety limit.");
                    }
                    _name.Append(name);
                }
                if (function["arguments"] != null)
                {
                    var arguments = (string)function["arguments"] ?? string.Empty;
                    if (_arguments.Length + arguments.Length > MaxToolArgumentsChars)
                    {
                        throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM tool arguments exceed the safety limit.");
                    }
                    _arguments.Append(arguments);
                }
            }

            private static string BoundedMetadata(string value)
            {
                value = value ?? string.Empty;
                if (value.Length > 1024)
                {
                    throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM tool metadata exceeds the safety limit.");
                }
                return value;
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["id"] = _id,
                    ["type"] = string.IsNullOrWhiteSpace(_type) ? "function" : _type,
                    ["function"] = new JObject
                    {
                        ["name"] = _name.ToString(),
                        ["arguments"] = _arguments.Length == 0 ? "{}" : _arguments.ToString()
                    }
                };
            }
        }

        private sealed class ThinkStreamSplitter
        {
            private const string OpenTag = "<think>";
            private const string CloseTag = "</think>";
            private readonly Action<string> _content;
            private readonly Action<string> _reasoning;
            private readonly StringBuilder _pending = new StringBuilder();
            private int _state;

            public ThinkStreamSplitter(Action<string> content, Action<string> reasoning)
            {
                _content = content;
                _reasoning = reasoning;
            }

            public void Add(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }
                if (_state == 2)
                {
                    _content(value);
                    return;
                }
                if (_pending.Length + value.Length > MaxStoredContentChars)
                {
                    throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response content exceeds the safety limit.");
                }
                _pending.Append(value);
                Process(false);
            }

            public void Complete()
            {
                Process(true);
            }

            private void Process(bool completed)
            {
                if (_state == 0)
                {
                    var value = _pending.ToString();
                    var leading = 0;
                    while (leading < value.Length && char.IsWhiteSpace(value[leading]))
                    {
                        leading++;
                    }
                    var candidate = value.Substring(leading);
                    if (candidate.Length < OpenTag.Length &&
                        OpenTag.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) && !completed)
                    {
                        return;
                    }
                    if (candidate.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Remove(0, leading + OpenTag.Length);
                        _state = 1;
                    }
                    else
                    {
                        _pending.Clear();
                        _state = 2;
                        _content(value);
                        return;
                    }
                }

                if (_state != 1)
                {
                    return;
                }
                var reasoning = _pending.ToString();
                var closeIndex = reasoning.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0)
                {
                    if (closeIndex > 0)
                    {
                        _reasoning(reasoning.Substring(0, closeIndex));
                    }
                    var remainder = reasoning.Substring(closeIndex + CloseTag.Length);
                    _pending.Clear();
                    _state = 2;
                    if (remainder.Length > 0)
                    {
                        _content(remainder);
                    }
                    return;
                }

                var emitLength = completed ? reasoning.Length : Math.Max(0, reasoning.Length - (CloseTag.Length - 1));
                if (emitLength > 0)
                {
                    _reasoning(reasoning.Substring(0, emitLength));
                    _pending.Remove(0, emitLength);
                }
            }
        }

        private static int? ReadInt(JObject obj, params string[] names)
        {
            if (obj == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var token = obj[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.Value<int>();
                }
            }

            return null;
        }

        private static string ReadAssistantContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }

            var content = ReadStringToken(message["content"], "choices[0].message.content");
            if (content.Length > MaxStoredContentChars)
            {
                throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response content exceeds the safety limit.");
            }
            return content;
        }

        private static string ReadReasoningContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }
            var token = message["reasoning_content"] ?? message["reasoning"];
            var value = ReadStringToken(token, "choices[0].message.reasoning");
            return value.Length > MaxStoredReasoningChars ? value.Substring(0, MaxStoredReasoningChars) : value;
        }

        private static string ReadRefusalContent(JObject message)
        {
            if (message == null)
            {
                return string.Empty;
            }
            var value = ReadStringToken(message["refusal"], "choices[0].message.refusal");
            return value.Length > MaxStoredRefusalChars ? value.Substring(0, MaxStoredRefusalChars) : value;
        }

        private static bool IsReasoningTruncated(JObject message)
        {
            if (message == null)
            {
                return false;
            }
            var token = message["reasoning_content"] ?? message["reasoning"];
            return token != null && token.Type == JTokenType.String &&
                (token.Value<string>() ?? string.Empty).Length > MaxStoredReasoningChars;
        }

        private static int? ReadReasoningTokens(JObject usage)
        {
            var details = usage == null ? null : usage["completion_tokens_details"] as JObject;
            var value = ReadInt(details, "reasoning_tokens");
            if (value.HasValue)
            {
                return value;
            }
            details = usage == null ? null : usage["output_tokens_details"] as JObject;
            return ReadInt(details, "reasoning_tokens") ?? ReadInt(usage, "reasoning_tokens");
        }

        private static LlmCompletionResult BuildCompletionResult(JObject message, JObject usage)
        {
            var content = ReadAssistantContent(message);
            string embeddedReasoning;
            bool embeddedTruncated;
            content = ExtractLeadingThink(content, out embeddedReasoning, out embeddedTruncated);
            var providerReasoning = ReadReasoningContent(message);
            var promptTokens = ReadInt(usage, "prompt_tokens", "input_tokens");
            var completionTokens = ReadInt(usage, "completion_tokens", "output_tokens");
            var totalTokens = ReadInt(usage, "total_tokens");
            if (totalTokens == null && promptTokens != null && completionTokens != null)
            {
                totalTokens = promptTokens.Value + completionTokens.Value;
            }
            return new LlmCompletionResult
            {
                Content = content,
                RefusalContent = ReadRefusalContent(message),
                ToolCalls = ReadToolCalls(message),
                ReasoningContent = providerReasoning.Length > 0 ? providerReasoning : embeddedReasoning,
                ReasoningTokens = ReadReasoningTokens(usage),
                ReasoningTruncated = providerReasoning.Length > 0
                    ? IsReasoningTruncated(message)
                    : embeddedTruncated,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                UsageJson = BoundedUsageJson(usage)
            };
        }

        private static List<LlmToolCall> ReadToolCalls(JObject message)
        {
            var result = new List<LlmToolCall>();
            var calls = message == null ? null : message["tool_calls"] as JArray;
            if (calls != null && calls.Count > MaxToolCalls)
            {
                throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM response contains too many tool calls.");
            }
            foreach (var token in calls == null ? new JObject[0] : calls.OfType<JObject>())
            {
                var function = token["function"] as JObject;
                if (function == null) continue;
                var id = (string)token["id"] ?? string.Empty;
                var type = (string)token["type"] ?? "function";
                var name = (string)function["name"] ?? string.Empty;
                var arguments = (string)function["arguments"] ?? "{}";
                if (id.Length > 1024 || type.Length > 1024 || name.Length > 1024)
                {
                    throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM tool metadata exceeds the safety limit.");
                }
                if (arguments.Length > MaxToolArgumentsChars)
                {
                    throw new LlmRequestException(LlmFailureKind.ResponseTooLarge, "LLM tool arguments exceed the safety limit.");
                }
                result.Add(new LlmToolCall
                {
                    Id = id,
                    Type = type,
                    Name = name,
                    ArgumentsJson = arguments
                });
            }
            return result;
        }

        private static string ExtractLeadingThink(string content, out string reasoning, out bool truncated)
        {
            reasoning = string.Empty;
            truncated = false;
            if (string.IsNullOrEmpty(content))
            {
                return content ?? string.Empty;
            }
            var leading = 0;
            while (leading < content.Length && char.IsWhiteSpace(content[leading]))
            {
                leading++;
            }
            const string openTag = "<think>";
            const string closeTag = "</think>";
            if (content.IndexOf(openTag, leading, StringComparison.OrdinalIgnoreCase) != leading)
            {
                return content;
            }
            var reasoningStart = leading + openTag.Length;
            var closeIndex = content.IndexOf(closeTag, reasoningStart, StringComparison.OrdinalIgnoreCase);
            var reasoningLength = closeIndex < 0
                ? content.Length - reasoningStart
                : closeIndex - reasoningStart;
            truncated = reasoningLength > MaxStoredReasoningChars;
            reasoning = content.Substring(reasoningStart, Math.Min(reasoningLength, MaxStoredReasoningChars));
            return closeIndex < 0 ? string.Empty : content.Substring(closeIndex + closeTag.Length);
        }

        private static string BoundedUsageJson(JObject usage)
        {
            if (usage == null) return null;
            var json = usage.ToString(Formatting.None);
            if (json.Length <= MaxUsageJsonChars) return json;
            return new JObject
            {
                ["truncated"] = true,
                ["originalChars"] = json.Length
            }.ToString(Formatting.None);
        }

        private static string ReadStringToken(JToken token, string field)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return string.Empty;
            }
            if (token.Type == JTokenType.String)
            {
                return token.Value<string>() ?? string.Empty;
            }
            throw new InvalidOperationException(field + " must be a string or null.");
        }

    }
}
