using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ConversationHistoryReadsAcceptedForms()
        {
            var call = new AgentToolCall { Id = "history_call", Name = "removed.old_tool",
                Arguments = new Dictionary<string, object> { ["at"] = "2026-08-28T12:34:56Z" } };
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var message = AgentJsonProtocol.CreateToolCallMessage(call, "Accepted step.", null, role, FixtureCallOrigin());
                message.ExcludeFromModelContext = true;
                var before = JsonConvert.SerializeObject(message);
                var parsed = ConversationResponseHistoryReader.Read(message);
                AssertTrue(parsed.Success, "current accepted history form reads: " + role);
                AssertEqual(call.Id, parsed.Response.ToolCalls.Single().Id, "runtime metadata preserves the exact accepted id");
                AssertEqual(call.Name, parsed.Response.ToolCalls.Single().Name, "native history preserves the exact canonical name");
                var arguments = JsonConvert.DeserializeObject<JObject>(parsed.Response.ToolCalls[0].ArgumentsJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                AssertEqual("2026-08-28T12:34:56Z", (string)arguments["at"], "accepted ISO argument remains exact text");
                var wire = ModelProtocolWire.Write(parsed.Response.Message,
                    new[] { new ConversationToolCall { Name = call.Name, Arguments = arguments } });
                AssertTrue(JObject.Parse(wire)["tool_calls"][0]["id"] == null, "history identity never becomes a model-generated wire field");
                AssertTrue(!ParseV4(wire, V4ReadTool()).Success, "history reading does not grant execution authority");
                arguments["at"] = "changed copy";
                AssertEqual(before, JsonConvert.SerializeObject(message), "history reader does not rewrite its source record");
            }
            var final = AgentTranscript.CreateAssistantMessage(V4Envelope(V4Call()), null, null, AgentResponseStatuses.Refused);
            var plain = ConversationResponseHistoryReader.Read(final);
            AssertTrue(plain.Success && plain.Response.ToolCalls.Count == 0, "plain final text is not sniffed as a tool envelope");
            AssertEqual(final.Content, plain.Response.Message, "JSON-looking final text remains exact text");
        }

        private static void ConversationHistoryRejectsAmbiguousRecords()
        {
            Func<ChatMessage> jsonMessage = () => AgentJsonProtocol.CreateToolCallMessage(
                new AgentToolCall { Id = "call_1", Name = "test.read" }, "Read.", null, ToolResultRoles.User, FixtureCallOrigin());
            Func<ChatMessage> nativeMessage = () => AgentJsonProtocol.CreateToolCallMessage(
                new AgentToolCall { Id = "call_1", Name = "test.read" }, "Read.", null, ToolResultRoles.Tool, FixtureCallOrigin());
            var missingId = jsonMessage();
            missingId.ToolCallId = null;
            AssertTrue(!ConversationResponseHistoryReader.Read(missingId).Success, "missing runtime identity fails closed");
            var missingOrigin = jsonMessage();
            missingOrigin.AcceptedCallOrigin = null;
            AssertTrue(!ConversationResponseHistoryReader.Read(missingOrigin).Success, "missing raw-attempt mapping fails closed");
            var mismatched = nativeMessage();
            mismatched.ToolCallId = "other";
            AssertTrue(!ConversationResponseHistoryReader.Read(mismatched).Success, "native/runtime metadata id mismatch fails closed");
            mismatched = jsonMessage();
            mismatched.ToolName = "another.tool";
            AssertTrue(!ConversationResponseHistoryReader.Read(mismatched).Success, "metadata/body name mismatch fails closed");
            foreach (var version in new[] { 0, 1, 2, 3, AgentResponseProtocol.CurrentVersion + 1 })
            {
                var unknown = jsonMessage();
                unknown.ResponseProtocolVersion = version;
                AssertTrue(!ConversationResponseHistoryReader.Read(unknown).Success, "old/unknown protocol is not guessed");
            }
            var legacy = jsonMessage();
            var legacyBody = JObject.Parse(legacy.Content);
            legacyBody["tool_calls"][0]["id"] = "model_owned";
            legacy.Content = legacyBody.ToString(Formatting.None);
            AssertTrue(!ConversationResponseHistoryReader.Read(legacy).Success, "v4 marker cannot silently use a v3 body");
            var noCanonicalName = nativeMessage();
            noCanonicalName.ToolName = null;
            AssertTrue(!ConversationResponseHistoryReader.Read(noCanonicalName).Success, "native name cannot replace missing canonical metadata");
            var batch = nativeMessage();
            batch.ToolCalls.Add(new LlmToolCall { Id = "second", Name = "test.read", ArgumentsJson = "{}" });
            AssertTrue(!ConversationResponseHistoryReader.Read(batch).Success, "ambiguous native batch is not partially adapted");
            foreach (var arguments in new[] { "{", "[]", "{\"at\":01}", "{}},{\"name\":\"test.read\",\"arguments\":{}" })
            {
                var invalid = nativeMessage();
                invalid.ToolCalls[0].ArgumentsJson = arguments;
                AssertTrue(!ConversationResponseHistoryReader.Read(invalid).Success, "native argument JSON cannot change envelope structure");
            }
            var diagnostic = jsonMessage();
            diagnostic.Activity = new ChatActivity { Kind = "model_response" };
            AssertTrue(!ConversationResponseHistoryReader.Read(diagnostic).Success, "diagnostics are not accepted responses");
            var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "call_1", ToolId = "test.read" }, RNAssistant.Core.Tools.Contracts.ToolResult.Ok("ok"));
            result.ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion;
            AssertTrue(!ConversationResponseHistoryReader.Read(result).Success, "tool results are not assistant responses");
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var first = jsonMessage();
            var second = jsonMessage();
            second.ToolCallId = "call_2";
            second.AcceptedCallOrigin = first.AcceptedCallOrigin;
            session.Messages.Add(first);
            session.Messages.Add(second);
            var rejected = false;
            try { ConversationProtocolContext.EnsureCurrentHistory(session); }
            catch (InvalidOperationException) { rejected = true; }
            AssertTrue(rejected, "one raw call position cannot map to two accepted identities");
        }

        private static ToolCatalogEntry V4ReadTool(string id = "test.read")
        {
            return new ToolCatalogEntry
            {
                Id = id,
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"query\":{\"type\":\"string\",\"description\":\"Query.\",\"minLength\":1}," +
                    "\"limit\":{\"type\":\"integer\",\"description\":\"Optional limit.\",\"minimum\":1,\"maximum\":50,\"default\":10}," +
                    "\"at\":{\"type\":\"string\",\"description\":\"Optional timestamp.\"}" +
                    "},\"required\":[\"query\"],\"additionalProperties\":false}"
            };
        }

        private static ToolCatalogEntry V4StructuredReadTool()
        {
            var tool = V4ReadTool();
            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"description\":\"Query.\",\"minLength\":1}," +
                "\"at\":{\"type\":\"string\",\"description\":\"Optional timestamp.\"}," +
                "\"items\":{\"type\":\"array\",\"description\":\"Optional structured values.\",\"items\":{" +
                    "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name.\"}}," +
                    "\"required\":[\"name\"],\"additionalProperties\":false}}" +
                "},\"required\":[\"query\"],\"additionalProperties\":false}";
            return tool;
        }

        private static JObject V4Call(string name = "test.read", JObject arguments = null)
        {
            return new JObject { ["name"] = name,
                ["arguments"] = arguments ?? new JObject { ["query"] = "A" } };
        }

        private static string V4Envelope(params JObject[] calls)
        {
            return new JObject
            {
                ["message"] = "Читаю.",
                ["action"] = calls == null || calls.Length == 0 ? "done" : "tool",
                ["tool_calls"] = new JArray(calls ?? new JObject[0])
            }.ToString(Formatting.None);
        }

        private static string V4EnvelopeWithFinal(bool final, params JObject[] calls)
        {
            return new JObject
            {
                ["message"] = "Читаю.",
                ["action"] = final ? "done" : "tool",
                ["tool_calls"] = new JArray(calls ?? new JObject[0])
            }.ToString(Formatting.None);
        }

        private static ConversationResponseParseResult ParseV4(string json, params ToolCatalogEntry[] tools)
        {
            return new ConversationResponseParser().Parse(json, tools, tools,
                new ModelProtocolCallContext(tools.Select(tool => tool.Id)));
        }

        private static void ConversationV4RoundTripsWithoutStatus()
        {
            foreach (var message in new[] { "Готово.", "", "  ", "Не удалось выполнить.\nНужен доступ.", "He said \"done\" \\ / \t" })
            {
                var json = new JObject { ["message"] = message, ["action"] = "done", ["tool_calls"] = new JArray() }.ToString(Formatting.None);
                var parsed = ParseV4(json);
                AssertTrue(parsed.Success, "v7 accepts a string message without interpreting its wording");
                AssertEqual(message, parsed.Response.Message, "message remains exact");
                AssertTrue(parsed.Response.Final, "final answer intent is explicit");
                AssertTrue(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(parsed.Response.ToJson())), "status-free final round trip");
            }
            var tool = V4StructuredReadTool();
            var callJson = V4Envelope(V4Call(arguments: new JObject
            {
                ["query"] = "\\u0061", ["at"] = "2026-08-28T12:34:56Z",
                ["items"] = new JArray(new JObject { ["name"] = "nested" })
            }));
            var call = ParseV4(callJson, tool);
            AssertTrue(call.Success, "v4 tool parses");
            AssertEqual(typeof(JObject), typeof(ConversationToolCall).GetProperty("Arguments").PropertyType,
                "validated arguments remain one JSON tree until canonical ArgumentsJson");
            AssertTrue(call.Response.ToolCalls[0].Arguments.Parent == null,
                "the retained argument tree does not keep the discarded response DOM alive");
            AssertTrue(call.Response.ToolCalls[0].Arguments["at"].Type == JTokenType.String,
                "ISO text is not silently converted to DateTime");
            AssertTrue(call.Response.ToolCalls[0].Arguments["items"] is JArray &&
                call.Response.ToolCalls[0].Arguments["items"][0] is JObject,
                "nested arrays and objects remain native JSON tokens");
            AssertTrue(!call.Response.Final, "tool turns are not final answers");
            var silent = JObject.Parse(callJson);
            silent["message"] = "";
            AssertTrue(ParseV4(silent.ToString(), tool).Success, "a tool call does not require visible narration");
            AssertTrue(JToken.DeepEquals(JObject.Parse(callJson), JObject.Parse(call.Response.ToJson())), "call round trip preserves arguments without assigning ids");
            AssertTrue(typeof(ConversationResponse).GetProperty("Status") == null, "v7 DTO has no model or universal status");
            foreach (var action in new[] { "done", "blocked", "needs_input" })
            {
                var envelope = ModelProtocolWire.WriteAction("Observed result; next decision is explicit.", action);
                var parsed = ParseV4(envelope);
                AssertTrue(parsed.Success && parsed.Response.Action == action && parsed.Response.ToolCalls.Count == 0,
                    "v7 preserves no-call action: " + action);
                AssertEqual(envelope, parsed.Response.ToJson(), "no-call action round trips exactly");
            }
            AssertTrue(!ParseV4("{\"message\":\"Wait\",\"action\":\"continue\",\"tool_calls\":[{}]}").Success,
                "progress cannot conceal a call");
            AssertTrue(!ParseV4("{\"message\":\"Observed fact.\",\"action\":\"continue\",\"tool_calls\":[]}").Success,
                "no-call progress is excluded even with a concrete message");
            AssertTrue(!ParseV4("{\"message\":\"\",\"action\":\"blocked\",\"tool_calls\":[]}").Success,
                "a blocked outcome must explain the remaining work");
        }

        private static void SimpleAgentEndsNoToolTurn()
        {
            foreach (var mode in new[] { ChatModes.Agent, ChatModes.Plan })
            foreach (var action in new[] { ConversationResponse.DoneAction, ConversationResponse.BlockedAction, ConversationResponse.NeedsInputAction })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var requests = 0;
                var session = NewSession(adapter);
                session.Mode = mode;
                var result = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    requests++;
                    return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAction("Ответ пользователю.", action) });
                }).ExecuteAsync(mode, "Запрос пользователя.", session, NewContext(adapter), new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                    .GetAwaiter().GetResult();
                AssertEqual(1, requests, "a no-call response ends the turn without self-prompting: " + mode);
                AssertEqual(action == ConversationResponse.DoneAction ? AgentResponseStatuses.Completed :
                    action == ConversationResponse.BlockedAction ? AgentResponseStatuses.Blocked : AgentResponseStatuses.AwaitingUser,
                    result.ResponseStatus, "the terminal reason is retained independently of prose");
                AssertEqual("model_" + action, result.RunViewState.Reason, "done, blocked and needs_input remain distinct");
                var terminal = session.Messages.Last(message => message.Role == "assistant" && message.Activity == null);
                var parsed = ConversationResponseHistoryReader.Read(terminal);
                AssertTrue(parsed.Success && parsed.Response.Action == action, "history preserves the terminal decision");
            });
        }

        private static void SimpleAgentRejectsContinueThenWaitsForReply()
        {
            foreach (var responseMode in new[] { AgentResponseModes.JsonSchema, AgentResponseModes.JsonObject })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var responses = new Queue<string>(new[] {
                    "{\"message\":\"I will continue.\",\"action\":\"continue\",\"tool_calls\":[]}",
                    ModelProtocolWire.WriteAction("Какой источник использовать?", ConversationResponse.NeedsInputAction),
                    V4Envelope(V4Call("common.capabilities_search", new JObject { ["query"] = "markdown" })),
                    ModelProtocolWire.WriteAction("Готово.", ConversationResponse.DoneAction) });
                var requests = new List<JObject>();
                var session = NewSession(adapter);
                var settings = new AppSettings { AgentResponseMode = responseMode, ToolResultRole = ToolResultRoles.Tool };
                var service = CreateConversationRunService(adapter, executor, (currentSettings, messages, options, stream, token) => {
                    requests.Add(LlmClient.BuildRequestBody(currentSettings,
                        new LlmMessageBuilder().Build(messages, currentSettings, options).Messages, 100, options));
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var waiting = service.ExecuteAsync(ChatModes.Agent, "Изучи источник.", session, NewContext(adapter), settings, catalog, null)
                    .GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.AwaitingUser, waiting.ResponseStatus, "repair may ask a question instead of claiming completion");
                AssertEqual("model_needs_input", waiting.RunViewState.Reason, "question is not done");
                AssertEqual(2, requests.Count, "one rejected response and repair, then wait for actual input");
                var original = (JArray)requests[0]["messages"];
                var repaired = (JArray)requests[1]["messages"];
                AssertTrue(JToken.DeepEquals(original, new JArray(repaired.Take(original.Count))), "repair keeps the frozen request intact");
                AssertContains((string)repaired.Last()["content"], "FORMAT_REPAIR:", "unsupported continue receives protocol repair");
                AssertContains((string)repaired.Last()["content"], ModelProtocolWire.ActionGuidance, "repair carries the shared control contract");
                var repairBody = JObject.Parse(((string)repaired.Last()["content"]).Substring("FORMAT_REPAIR:\n".Length));
                AssertContains((string)repairBody["instruction"], ModelProtocolWire.MessageGuidance, "repair does not reintroduce per-call narration");
                AssertTrue(!session.Messages.Any(message => (message.Content ?? "").Contains("I will continue.")),
                    "rejected narrative never enters durable chat history");
                if (responseMode == AgentResponseModes.JsonSchema)
                {
                    AssertTrue(JToken.DeepEquals(new JArray("tool", "done", "blocked", "needs_input"),
                        requests[0].SelectToken("response_format.json_schema.schema.properties.action.enum")), "schema exposes only four actions");
                    AssertEqual(ModelProtocolWire.MessageGuidance,
                        (string)requests[0].SelectToken("response_format.json_schema.schema.properties.message.description"),
                        "actual strict wire shares optional progress guidance with repair and defaults");
                    AssertContains((string)requests[0].SelectToken("response_format.json_schema.schema.description"),
                        ModelProtocolWire.ActionGuidance, "actual strict wire shares the completion decision rules");
                }
                var completed = service.ExecuteAsync(ChatModes.Agent, "Используй Markdown.", session, NewContext(adapter), settings, catalog, null)
                    .GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, completed.ResponseStatus, "user reply resumes work through a real tool step");
                AssertEqual(4, requests.Count, "user reply and tool result each drive one request");
                AssertContains(requests[2]["messages"].ToString(), "Какой источник использовать?", "next turn retains the question");
                AssertContains(requests[2]["messages"].ToString(), "Используй Markdown.", "next turn contains the actual answer");
                AssertTrue(!requests.Any(request => request["messages"].ToString().Contains("RUNTIME_FEEDBACK:")), "no narrative feedback path remains");
            });
        }

        private static void ConversationV4RejectsUnknownRootFields()
        {
            foreach (var field in new[] { "status", "phase", "completed", "retry", "verified", "Message", "extra" })
            {
                var root = JObject.Parse(V4Envelope());
                root[field] = "completed";
                var parsed = ParseV4(root.ToString(Formatting.None));
                AssertTrue(!parsed.Success && parsed.Response == null, "unknown root field rejected: " + field);
                AssertContains(parsed.Error, field, "unknown field diagnostic");
            }
            AssertTrue(!ParseV4(V4EnvelopeWithFinal(true, V4Call()), V4ReadTool()).Success,
                "final answer intent cannot accompany a tool call");
            AssertTrue(!ParseV4("{\"message\":\"thinking\",\"action\":\"tool\",\"tool_calls\":[]}").Success,
                "non-final intent requires a real tool call");
            AssertTrue(!ParseV4("{\"message\":\"old\",\"final\":true,\"tool_calls\":[]}").Success,
                "v5 envelope is not silently interpreted as v7");
            foreach (var json in new[] { "{}", "{\"message\":\"x\"}", "{\"message\":\"x\",\"action\":\"done\"}",
                "{\"message\":\"x\",\"tool_calls\":[]}", "{\"tool_calls\":[]}",
                "{\"message\":null,\"action\":\"done\",\"tool_calls\":[]}", "{\"message\":1,\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"final\":null,\"tool_calls\":[]}", "{\"message\":\"x\",\"final\":\"true\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"final\":1,\"tool_calls\":[]}",
                "{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":null}", "{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":{}}" })
                AssertTrue(!ParseV4(json).Success, "missing/wrong root type rejected: " + json);
        }

        private static void ConversationV4RejectsMalformedJson()
        {
            foreach (var json in new[]
            {
                "", "<html>Blocked</html>", "content rejected by protection", "[]", "null",
                "```json\n{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[]}\n```",
                "text {\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[]} {}",
                "{\"message\":\"x\",\"message\":\"y\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"x\",/*comment*/\"action\":\"done\",\"tool_calls\":[]}",
                "{'message':'x','final':true,'tool_calls':[]}",
                "{message:\"x\",\"action\":\"done\",\"tool_calls\":[]}",
                "{true:\"x\",\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[],}",
                "{\"message\":\"line\nbreak\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"bad\\'escape\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"bad\\u12xx\",\"action\":\"done\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"action\":\"done\",\"tool_calls\":[}",
                "{\"message\":\"x\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"test.read\",\"arguments\":{\"query\":\"A\",\"limit\":NaN}}]}"
            }) AssertTrue(!ParseV4(json, V4ReadTool()).Success, "non-JSON or incomplete envelope rejected: " + json);

            foreach (var number in new[] { "01", "+1", ".5", "0x10", "undefined", "Infinity", "1e999", "999999999999999999999999999999999" })
            {
                var json = V4Envelope(V4Call(arguments: new JObject { ["query"] = "A", ["limit"] = "NUMBER" }))
                    .Replace("\"NUMBER\"", number);
                AssertTrue(!ParseV4(json, V4ReadTool()).Success, "non-JSON/non-finite number rejected: " + number);
            }
            var escaped = ParseV4("{\"message\":\"\\u0410\\/\\b\\f\\n\\r\\t\",\"action\":\"done\",\"tool_calls\":[]}");
            AssertTrue(escaped.Success, "standard Unicode and control escapes are accepted");
            var exactSource = "line 1\nconst escaped = \"\\n\";\nconst regex = /\\d+\\\\path/;";
            var exactWire = V4Envelope(V4Call(arguments: new JObject { ["query"] = exactSource }));
            AssertContains(exactWire, "line 1\\nconst escaped", "real source line break has one outer JSON escape");
            AssertContains(exactWire, "\\\"\\\\n\\\"", "literal source backslash survives as two outer JSON backslashes");
            var exact = ParseV4(exactWire, V4ReadTool());
            AssertTrue(exact.Success, "escaped source argument is accepted");
            AssertEqual(exactSource,
                (string)exact.Response.ToolCalls.Single().Arguments["query"],
                "one JSON decode preserves real lines and literal source backslashes");
            var nested = V4Envelope(V4Call(arguments: new JObject { ["query"] = "A", ["deep"] = "NESTED" }))
                .Replace("\"NESTED\"", new string('[', 70) + "0" + new string(']', 70));
            AssertTrue(!ParseV4(nested, V4ReadTool()).Success, "excessive nesting is a typed parse failure");
        }

        private static void ConversationV4RequiresExactCallShape()
        {
            foreach (var field in new[] { "name", "arguments" })
            {
                var call = V4Call();
                call.Remove(field);
                AssertTrue(!ParseV4(V4Envelope(call), V4ReadTool()).Success, "missing call field rejected: " + field);
            }
            foreach (var arguments in new JToken[] { JValue.CreateNull(), new JValue("{}"), new JArray(), new JValue(3) })
            {
                var call = V4Call();
                call["arguments"] = arguments;
                AssertTrue(!ParseV4(V4Envelope(call), V4ReadTool()).Success, "arguments must be an object");
            }
            foreach (var field in new[] { "id", "retry", "verified", "phase" })
            {
                var extra = V4Call();
                extra[field] = "model_owned";
                AssertTrue(!ParseV4(V4Envelope(extra), V4ReadTool()).Success, "extra call field rejected: " + field);
            }
            AssertTrue(!ParseV4(V4Envelope(V4Call(name: "")), V4ReadTool()).Success, "blank name rejected");
            AssertTrue(!ParseV4(V4Envelope(V4Call(arguments: new JObject { ["query"] = "A", ["Query"] = "B" })), V4ReadTool()).Success,
                "ambiguous argument names rejected before normalization");
            AssertTrue(!ParseV4(V4Envelope(V4Call(arguments: new JObject
                {
                    ["query"] = "A",
                    ["items"] = new JArray(new JObject { ["name"] = "A", ["Name"] = "B" })
                })), V4StructuredReadTool()).Success,
                "nested names that differ only by case fail schema validation");
            AssertTrue(!ParseV4(V4Envelope(V4Call()).Replace("\"query\":\"A\"", "\"query\":\"A\",\"query\":\"B\""), V4ReadTool()).Success,
                "duplicate nested JSON property rejected");
        }

        private static void ConversationV4RequiresCallableAuthority()
        {
            var loaded = V4ReadTool();
            var unloaded = V4ReadTool("test.other");
            var parser = new ConversationResponseParser();
            var result = parser.Parse(V4Envelope(V4Call(name: unloaded.Id)), new[] { loaded }, new[] { loaded, unloaded }, new ModelProtocolCallContext(new[] { loaded.Id }));
            AssertTrue(!result.Success, "known but unloaded tool cannot execute");
            AssertContains(result.Error, "Tool schema is not loaded", "unloaded tool diagnosis");
            AssertContains(result.Error, "common.capabilities_read", "explicit read recovery");
            AssertContains(result.Error, "\"id\":\"test.other\"", "recovery names exact capability");
            foreach (var name in new[] { "test.unknown", "TEST.READ", "test.read " })
            {
                result = ParseV4(V4Envelope(V4Call(name: name)), loaded);
                AssertTrue(!result.Success, "unknown/case-mismatched tool rejected");
                AssertContains(result.Error, "Unknown tool", "unknown name diagnosis");
            }
            loaded.ArgumentSchemaJson = "{}";
            AssertTrue(!ParseV4(V4Envelope(V4Call()), loaded).Success, "malformed callable schema cannot grant authority");
            AssertTrue(!parser.Parse(V4Envelope(), new ToolCatalogEntry[0], new ToolCatalogEntry[0], null).Success,
                "local safety context must be explicit, even if empty");
            AssertTrue(!parser.Parse(V4Envelope(), new ToolCatalogEntry[0], new ToolCatalogEntry[0], new ModelProtocolCallContext(null)).Success,
                "batch safety authority must be explicit");
        }

        private static void ConversationV4KeepsIdenticalCallsWithoutIds()
        {
            var tool = V4ReadTool();
            var raw = V4Envelope(V4Call(), V4Call());
            var first = ParseV4(raw, tool);
            AssertTrue(first.Success, "identical model requests are not misclassified as duplicate identity");
            AssertEqual(2, first.Response.ToolCalls.Count, "both requested calls are preserved without deduplication");
            AssertTrue(JToken.DeepEquals(JObject.Parse(raw), JObject.Parse(first.Response.ToJson())), "wire payload is unchanged by identity bookkeeping");
            AssertTrue(ParseV4(raw, tool).Success, "a later step may request the same content without ID-triggered repair");
            AssertTrue(typeof(ConversationToolCall).GetProperty("Id") == null, "model-facing DTO cannot assign execution identity");
            var rejected = ParseV4(V4Envelope(V4Call(), V4Call("unknown")), tool);
            AssertTrue(!rejected.Success && rejected.Response == null, "invalid complete response yields no partial accepted calls");
        }

        private static void ConversationV4BatchesOnlyExplicitReadOnlyCalls()
        {
            var read = V4ReadTool();
            var parser = new ConversationResponseParser();
            var calls = V4Envelope(V4Call(arguments: new JObject { ["query"] = "first" }),
                V4Call(arguments: new JObject { ["query"] = "second" }));
            var accepted = ParseV4(calls, read);
            AssertTrue(accepted.Success, "independent read-only calls may be batched");
            AssertTrue(accepted.Response.ToolCalls.Select(call => (string)call.Arguments["query"]).SequenceEqual(new[] { "first", "second" }), "call order is preserved");
            AssertTrue(!parser.Parse(calls, new[] { read }, new[] { read }, new ModelProtocolCallContext(new string[0])).Success,
                "absence of flags alone does not establish batch safety");
            AssertResponseBatchSchema(true, calls, new[] { read }, new ModelProtocolCallContext(new[] { read.Id }));
            AssertResponseBatchSchema(false, calls, new[] { read }, new ModelProtocolCallContext(new string[0]));

            foreach (var kind in new[] { "document", "local", "confirmation", "external", "unclassified" })
            {
                var tool = V4ReadTool("test.action");
                tool.MutatesDocument = kind == "document";
                tool.MutatesLocalState = kind == "local";
                tool.RequiresConfirmation = kind == "confirmation";
                tool.Policy = kind == "document" || kind == "local"
                    ? new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" })
                    : kind == "confirmation"
                        ? new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" })
                        : kind == "external"
                            ? new ToolPolicy(ToolEffect.External, ToolVerification.None, false, false, new[] { "agent" })
                            : new ToolPolicy(ToolEffect.Unclassified, ToolVerification.None, false, false, new[] { "agent" });
                // Explicit IDs cannot override contradictory flags or policy.
                var batchSafe = new[] { read.Id, tool.Id };
                var context = new ModelProtocolCallContext(batchSafe);
                var catalog = new[] { read, tool };
                AssertResponseBatchSchema(true, calls, catalog, context);
                AssertResponseBatchSchema(true, V4Envelope(), catalog, context);
                AssertResponseBatchSchema(true, V4Envelope(V4Call(tool.Id)), catalog, context);
                AssertResponseBatchSchema(false, V4Envelope(V4Call(tool.Id), V4Call(tool.Id)), catalog, context);
                foreach (var batch in new[]
                {
                    V4Envelope(V4Call(tool.Id), V4Call(read.Id)),
                    V4Envelope(V4Call(read.Id), V4Call(tool.Id))
                })
                {
                    var result = parser.Parse(batch, new[] { read, tool }, new[] { read, tool }, new ModelProtocolCallContext(batchSafe));
                    AssertTrue(!result.Success, kind + " cannot be batched, regardless of position");
                    AssertContains(result.Error, "one at a time", "singleton diagnosis");
                    AssertResponseBatchSchema(false, batch, catalog, context);
                }
                if (kind == "document" || kind == "local")
                    AssertTrue(!parser.Parse(V4Envelope(V4Call(tool.Id), V4Call(tool.Id)),
                        new[] { tool }, new[] { tool }, new ModelProtocolCallContext(new[] { tool.Id })).Success,
                        kind + " cannot batch two mutations even with forged batch context");
                AssertTrue(parser.Parse(V4Envelope(V4Call(name: tool.Id)), new[] { tool }, new[] { tool }, new ModelProtocolCallContext(batchSafe)).Success,
                    kind + " singleton is valid protocol, not execution permission");
            }
        }

        private static void ConversationV4ValidatesArgumentsBeforeAcceptance()
        {
            foreach (var arguments in new[]
            {
                new JObject(), new JObject { ["query"] = 5 }, new JObject { ["query"] = "" },
                new JObject { ["query"] = JValue.CreateNull() }, new JObject { ["query"] = "A", ["limit"] = 51 },
                new JObject { ["query"] = "A", ["limit"] = "10" }, new JObject { ["query"] = "A", ["extra"] = true }
            })
            {
                var parsed = ParseV4(V4Envelope(V4Call(arguments: arguments)), V4ReadTool());
                AssertTrue(!parsed.Success && parsed.Response == null, "argument contract violation cannot be accepted");
                AssertContains(parsed.Error, "Invalid arguments", "schema violation diagnosis");
            }
            var strictNulls = ParseV4(V4Envelope(V4Call(arguments: new JObject
            {
                ["query"] = "A", ["limit"] = JValue.CreateNull(), ["at"] = JValue.CreateNull()
            })), V4ReadTool());
            AssertTrue(strictNulls.Success, "structured-output optional nulls are accepted");
            AssertEqual(1, strictNulls.Response.ToolCalls[0].Arguments.Count, "optional nulls removed, no execution defaults applied by protocol");
        }

        private static void ConversationV4BoundsCallCount()
        {
            var tool = V4ReadTool();
            var calls = Enumerable.Range(0, 32).Select(i => V4Call()).ToArray();
            AssertTrue(ParseV4(V4Envelope(calls), tool).Success, "32 read-only calls accepted");
            var oversized = V4Envelope(calls.Concat(new[] { V4Call() }).ToArray());
            AssertTrue(!ParseV4(oversized, tool).Success, "33 calls rejected");
        }

        private static void SimpleAgentPromptContainsToolsAndSkills()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = OfficeToolCatalog.ForHost(adapter.HostName).Where(tool => tool.Id == "excel.add_sheet")
                .Concat(ResourceToolCatalog.GetControllerTools()).ToList();
            var skills = new[]
            {
                new SkillDefinition
                {
                    Id = "common.test",
                    Name = "Test",
                    Description = "Test workflow",
                    BodyMarkdown = "Follow TEST_SKILL_SENTINEL.",
                    Enabled = true
                }
            };
            var messages = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).BuildPreview(
                ChatModes.Agent,
                "Create a report.", adapter, tools, skills, new DocumentContext(), new AppSettings(),
                NewSession(adapter), null);
            var prompt = FlattenSimple(messages);
            AssertContains(prompt, "\"type\":\"function\"", "native-like tool JSON");
            AssertContains(prompt, "schemaLoaded", "runtime identifies callable schemas in the compact context");
            AssertContains(prompt, "excel.add_sheet", "first tool present");
            AssertContains(prompt, "common.resources_read", "resource reader present");
            AssertContains(prompt, "common.test", "skill id present");
            AssertContains(prompt, "Test workflow", "skill description present");
            AssertTrue(prompt.IndexOf(SkillRevision.Compute(skills[0]), StringComparison.Ordinal) < 0,
                "skill revision remains outside model catalog");
            AssertContains(prompt, "\"bodyChars\":27", "skill body size present");
            AssertContains(prompt, "\"referenceCount\":0", "skill reference count present");
            AssertTrue(prompt.IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0, "full skill is not in catalog");
            AssertContains(prompt, "common.capabilities_read", "unified capability loading guidance present");
            AssertContains(prompt, "\"kind\":\"skill\"", "skill kind is explicit in compact catalog");
            AssertContains(prompt, "metadata only", "catalog is not mistaken for loaded skill instructions");
            AssertContains(prompt, "SELECTED_SKILL", "task selection in the next request is explicit");
            AssertContains(prompt, "Batch only independent local reads", "shared multi-tool guidance present");
            AssertContains(prompt, "data.truncated=true", "bounded tool-result guidance present");
            AssertTrue(prompt.IndexOf("ROUTE:", StringComparison.OrdinalIgnoreCase) < 0, "no route wrapper");
            AssertTrue(prompt.IndexOf("NEXT_ACTION_POLICY", StringComparison.OrdinalIgnoreCase) < 0, "no action heuristic");
        }

        private static void AgentContinuesWithLocalToolsForClosedDocument()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.DocumentKey = "closed-doc";
                session.DocumentTitle = "Closed.xlsx";
                var context = new DocumentContext
                {
                    Host = session.Host,
                    DocumentKey = session.DocumentKey,
                    Title = session.DocumentTitle
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var responses = new Queue<string>(new[]
                {
                    LoadSkillResponse("common.html_workspace_authoring"),
                    "{\"message\":\"Создаю локальный HTML.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.html_workspace_write_file\",\"arguments\":{\"path\":\"index.html\",\"content\":\"<main>Offline</main>\"}}]}",
                    "{\"message\":\"Локальный HTML готов.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteWithApprovalsAsync(
                    ChatModes.Agent,
                    "Создай HTML без обращения к Excel.",
                    session,
                    context,
                    new AppSettings(),
                    tools,
                    null,
                    null,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual("Локальный HTML готов.", result.AssistantText, "closed-document local result");
                AssertTrue(session.HtmlWorkspace != null && session.HtmlWorkspace.Files.Any(file =>
                    file != null && string.Equals(file.Path, "index.html", StringComparison.OrdinalIgnoreCase)),
                    "closed-document HTML file saved");
                var prompt = FlattenSimple(calls[0]);
                AssertTrue(prompt.IndexOf("closed-doc", StringComparison.Ordinal) < 0,
                    "internal document identity stays outside model context");
                AssertContains(prompt, "\"title\":\"Closed.xlsx\"", "archived document title in prompt");
                AssertContains(prompt, "\"office_tools_available\":false", "Office availability in prompt");
                AssertTrue(prompt.IndexOf("\"vba_project_target\":", StringComparison.Ordinal) < 0,
                    "closed document exposes no directly readable live VBA project target");
                AssertContains(prompt, "\"id\":\"common.html_workspace_write_file\"",
                    "exact local HTML capability remains discoverable");
                AssertTrue(prompt.IndexOf("\"name\":\"common.html_workspace_write_file\"", StringComparison.OrdinalIgnoreCase) < 0,
                    "local HTML schema is not injected before discovery");
                AssertContains(FlattenSimple(calls[1]), "\"name\":\"common.html_workspace_write_file\"",
                    "exact local HTML schema is available after read");
                AssertTrue(prompt.IndexOf("excel.read_range", StringComparison.OrdinalIgnoreCase) < 0,
                    "Office tools omitted for a closed document");
                AssertContains(prompt, "common.html_data_bind",
                    "resource binding remains available for local resources when Office is closed");
                AssertEqual(0, adapter.TotalBackendCallCount, "local tool does not enter Office adapter");

                var blocked = executor.ExecuteManual(
                    Command(ResourceToolCatalog.ReadToolId, "target", "Excel range: Data!A1:B2"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("active_document_changed", blocked.ErrorCode,
                    "closed-document Office call remains guarded");
                AssertEqual(0, adapter.TotalBackendCallCount, "guarded Office tool never reaches adapter");
            });
        }

        private static void SimpleAgentInspectsFullSkillThroughTool()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var skill = new SkillDefinition
                {
                    Id = "common.test",
                    Name = "Test",
                    Description = "Test workflow",
                    Version = "2.0.0",
                    BodyMarkdown = "Follow TEST_SKILL_SENTINEL.\n" + new string('x', 15000) + "\nTEST_SKILL_END.",
                    Enabled = true
                };
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю подходящий skill.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.capabilities_read\",\"arguments\":{\"id\":\"common.test\"}}]}",
                    "{\"message\":\"Повторное чтение для проверки контекста.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.capabilities_read\",\"arguments\":{\"id\":\"common.test\"}}]}",
                    "{\"message\":\"Инструкции учтены.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var published = executor.ExecuteManual(Command("common.skills_upsert", "id", skill.Id,
                    "host", "Common", "name", skill.Name, "description", skill.Description, "version", skill.Version,
                    "bodyMarkdown", skill.BodyMarkdown, "enabled", true), tools, new AppSettings(), false, true, session);
                AssertEqual("ok", published.Status, "skill fixture crosses the publication barrier");
                skill = executor.CaptureSkills().Skills.Single(item => item.Id == skill.Id);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Do the test workflow.", session, NewContext(adapter), new AppSettings(),
                    tools, null, null, null, null, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Инструкции учтены.", result.AssistantText, "skill-assisted response");
                var revision = SkillRevision.Compute(skill);
                AssertTrue(FlattenSimple(calls[0]).IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0,
                    "first request contains only catalog");
                AssertTrue(FlattenSimple(calls[0]).IndexOf(revision, StringComparison.Ordinal) < 0,
                    "catalog hides skill revision");
                var replay = FlattenSimple(calls.Last());
                AssertTrue(!replay.Contains("resource_evidence_stale"),
                    "repeated skill load does not turn the earlier complete read into a stale error");
                AssertContains(replay, "TEST_SKILL_SENTINEL", "full instructions returned by tool");
                AssertContains(replay, "TEST_SKILL_END", "skill body is not cut by the generic tool-result limit");
                AssertContains(replay, "\"format\":\"markdown\"", "loaded skill format");
                AssertContains(replay, "\"version\":\"2.0.0\"", "loaded skill version");
                AssertTrue(replay.IndexOf(revision, StringComparison.Ordinal) < 0,
                    "model result hides skill revision");
                AssertContains(replay, "\"loaded\":true", "loaded skill result is explicit");
                AssertContains(replay, "\"complete\":true", "loaded skill body is complete");
                AssertContains(replay, "common.skills_load",
                    "source read directs selection through the canonical loader");
                AssertEqual(0, session.LastRun.SelectedSkills.Count, "source inspection does not select a skill");
                AssertTrue(replay.IndexOf("\"truncated\":true", StringComparison.Ordinal) < 0,
                    "loaded skill is not duplicated into a truncated result");
                var durableMessage = session.Messages.Last(message => string.Equals(
                    message.ToolName, CapabilityToolCatalog.ReadToolId, StringComparison.Ordinal));
                ToolResultWireReadResult durable;
                string durableError;
                AssertTrue(ToolResultHistoryReader.TryRead(durableMessage, out durable, out durableError),
                    "durable skill result remains readable");
                AssertContains(durable.Result.DataJson, "\"payload_externalized\":true", "large skill result is reference-first");
                AssertTrue(durableMessage.ResultPayload != null && !durableMessage.Content.Contains("TEST_SKILL_SENTINEL"),
                    "history does not duplicate the full skill payload");
                var hydrated = HistoricalContextProjector.Project(durableMessage);
                hydrated.Content = executor.Payloads.ReadText(durableMessage.ResultPayload.ToBlobReference());
                AssertTrue(ToolResultHistoryReader.TryRead(hydrated, out durable, out durableError),
                    "exact CAS skill result remains readable");
                AssertContains(durable.Result.DataJson, "\"revision\":\"" + revision + "\"",
                    "durable skill result retains exact revision");
                var exactSkill = SkillPublicationService.SkillResource(skill);
                AssertTrue(durableMessage.ResourceEvidence.Any(evidence => evidence.Resource.Uri == exactSkill.Uri &&
                    evidence.Resource.Revision == exactSkill.Revision),
                    "durable skill evidence pins the committed publication");
            });
        }

        private static void SimpleAgentPublishesNewSkillAndToolInSameRun()
        {
            Func<string, JObject, string> call = (name, arguments) => new JObject
            {
                ["message"] = "Продолжаю проверку пакета.",
                ["action"] = "tool",
                ["tool_calls"] = new JArray(new JObject { ["name"] = name, ["arguments"] = arguments })
            }.ToString(Formatting.None);
            const string skillId = "excel.in_run_skill";
            const string toolId = "excel.in_run_tool";
            const string body = "# In-run skill\n\nIN_RUN_SKILL_SENTINEL";
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    call("common.skills_load", new JObject { ["id"] = "common.skill_authoring" }),
                    call("common.skills_upsert", new JObject {
                        ["id"] = skillId, ["host"] = "Excel", ["name"] = "In-run skill",
                        ["description"] = "Test newly published skill visibility.",
                        ["version"] = "1.0.0", ["bodyMarkdown"] = body, ["enabled"] = true }),
                    call("common.capabilities_read", new JObject { ["id"] = skillId }),
                    call("common.skills_load", new JObject { ["id"] = skillId }),
                    "{\"message\":\"Скилл создан и загружен.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Create and load a skill in this run.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 8 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus,
                    "same-run skill creation completes: " + result.AssistantText);
                AssertEqual(5, requests.Count, "skill creation, source read and selection reach distinct requests");
                AssertContains(FlattenSimple(requests[2]), "\"id\":\"" + skillId + "\"",
                    "new skill is discoverable after its publication in the same run");
                AssertContains(FlattenSimple(requests[3]), "IN_RUN_SKILL_SENTINEL",
                    "new skill source is readable through the capability reader");
                AssertContains(FlattenSimple(requests[4]), "IN_RUN_SKILL_SENTINEL",
                    "newly selected instructions reach the next request");
                AssertTrue(session.LastRun.SelectedSkills.Any(item => item.Id == skillId),
                    "same-run selection remains in durable run state");
            });

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var authored = CustomTool("Excel", toolId);
                authored.Components[0].Code = authored.Components[0].Code
                    .Replace("' <RNAssistantTool>", "<RNAssistantTool>")
                    .Replace("' {\"protocolVersion\"", "{\"protocolVersion\"")
                    .Replace("' </RNAssistantTool>", "</RNAssistantTool>");
                var responses = new Queue<string>(new[]
                {
                    call("common.skills_load", new JObject { ["id"] = "common.tool_authoring" }),
                    call("common.tools_upsert", new JObject {
                        ["id"] = toolId, ["components"] = ToolComponentsPayload(authored) }),
                    call("common.resources_read", new JObject { ["target"] = "tool source: " + toolId }),
                    call("common.capabilities_read", new JObject { ["id"] = toolId }),
                    "{\"message\":\"Инструмент создан и его схема прочитана.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, requestOptions, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Create and inspect a tool in this run.", NewSession(adapter), NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 8 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus,
                    "same-run tool creation completes: " + result.AssistantText);
                AssertEqual(5, requests.Count, "tool creation, source read and exact schema read reach distinct requests");
                AssertContains(FlattenSimple(requests[2]), "\"id\":\"" + toolId + "\"",
                    "new tool is discoverable after its publication in the same run");
                AssertContains(FlattenSimple(requests[3]), "Public Function Run",
                    "new tool source is readable through the resource reader");
                AssertContains(FlattenSimple(requests[4]), "\"name\":\"" + toolId + "\"",
                    "new tool schema is readable through the capability reader");
                AssertContains(FlattenSimple(requests[4]), "\"parameters\":",
                    "new direct tool retains its exact schema after admission");
            });
        }

        private static void SimpleAgentPromptSkipsInvalidToolSchema()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = new[]
            {
                new ToolCatalogEntry
                {
                    Id = "excel.good",
                    Description = "Good",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"
                },
                new ToolCatalogEntry
                {
                    Id = "excel.bad",
                    Description = "Bad",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}"
                }
            };
            var prompt = FlattenSimple(new ModelContextCompiler(projection: ModelToolResultProjection.Instance).BuildPreview(
                ChatModes.Agent,
                "Test", adapter, tools, null, new DocumentContext(), new AppSettings(), NewSession(adapter), null));
            AssertContains(prompt, "excel.good", "valid tool included");
            AssertTrue(prompt.IndexOf("excel.bad", StringComparison.OrdinalIgnoreCase) < 0, "invalid tool excluded");
        }

        private static void StrictToolSchemaValidatesMetadataAndConstraints()
        {
            var tool = new ToolCatalogEntry
            {
                Id = "common.strict_test",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\",\"description\":\"Item count.\",\"default\":2,\"minimum\":1,\"maximum\":3}},\"required\":[],\"additionalProperties\":false}"
            };
            Newtonsoft.Json.Linq.JObject schema;
            string error;
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "strict schema parses");

            var arguments = new Newtonsoft.Json.Linq.JObject();
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out error), "default is applied");
            AssertEqual(2L, Convert.ToInt64(arguments["count"]), "declared default value");
            arguments["count"] = Newtonsoft.Json.Linq.JValue.CreateNull();
            ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out error), "strict-output null is treated as omitted");
            AssertEqual(2L, Convert.ToInt64(arguments["count"]), "runtime reapplies the code-owned default after null removal");
            arguments["count"] = 4;
            AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "maximum is enforced");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":[\"string\",\"null\"],\"description\":\"Nullable value.\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "explicitly nullable schema parses");
            arguments = new Newtonsoft.Json.Linq.JObject { ["value"] = Newtonsoft.Json.Linq.JValue.CreateNull() };
            ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            AssertTrue(arguments.Property("value") != null, "explicitly allowed null is preserved");
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "explicit null remains valid");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "undocumented argument is rejected");
            AssertContains(error, "description", "missing description diagnostic");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\",\"description\":\"Count.\",\"minimum\":\"bad\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "malformed numeric constraint is rejected at catalog load");
            AssertContains(error, "minimum", "malformed constraint diagnostic");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"description\":\"Mode.\",\"const\":\"safe\"}},\"required\":[\"mode\"],\"additionalProperties\":false}";
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "const schema parses");
            arguments = new Newtonsoft.Json.Linq.JObject { ["mode"] = "unsafe" };
            AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "const is enforced at runtime");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"description\":\"Finite value.\"}},\"required\":[\"value\"],\"additionalProperties\":false}";
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "finite number schema parses");
            arguments = new Newtonsoft.Json.Linq.JObject { ["value"] = double.NaN };
            AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "non-finite numbers are rejected");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name.\",\"pattern\":\"^[a-z]+$\"}},\"required\":[\"name\"],\"additionalProperties\":false}";
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "bounded string pattern schema parses");
            arguments = new Newtonsoft.Json.Linq.JObject { ["name"] = "UPPER" };
            AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), "pattern is enforced at runtime");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name.\",\"format\":\"date\"}},\"required\":[\"name\"],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "unsupported schema assertions are rejected instead of being ignored locally");
            AssertContains(error, "unsupported schema keyword", "unsupported schema keyword diagnostic");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name.\"},\"Name\":{\"type\":\"string\",\"description\":\"Ambiguous name.\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "case-colliding schema properties are rejected");
            AssertContains(error, "differ only by case", "case-colliding schema diagnostic");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":[\"string\",{}],\"description\":\"Name.\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error),
                "object in type array is rejected without throwing");
            AssertContains(error, "type is invalid",
                "object-shaped type has a normal schema diagnostic");

        }

        private static void ControllerToolCatalogUsesStrictSchemas()
        {
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost(host), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                    var session = NewSession(adapter);
                    foreach (var tool in executor.GetControllerTools())
                    {
                        Newtonsoft.Json.Linq.JObject schema;
                        string error;
                        AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), host + "/" + tool.Id + ": " + error);
                        var variants = schema["anyOf"] is Newtonsoft.Json.Linq.JArray
                            ? ((Newtonsoft.Json.Linq.JArray)schema["anyOf"]).OfType<Newtonsoft.Json.Linq.JObject>().ToArray()
                            : new[] { schema };
                        foreach (var variant in variants)
                        {
                            var arguments = MinimalValidArguments(variant);
                            string argumentError;
                            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError), host + "/" + tool.Id + " variant: " + argumentError);
                            var command = new ToolInvocation { ToolId = tool.Id };
                            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
                            var result = executor.ExecuteManual(command, catalog, new AppSettings { AutoConfirmToolActions = true }, true, true, session);
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "unknown_tool", StringComparison.OrdinalIgnoreCase), host + "/" + tool.Id + " dispatch is registered");
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "invalid_arguments", StringComparison.OrdinalIgnoreCase), host + "/" + tool.Id + " published branch reaches its handler");
                        }
                        var responseSchema = ConversationResponseSchemaBuilder.Build(new[] { tool }, new ModelProtocolCallContext(new string[0]));
                        AssertTrue(!string.IsNullOrWhiteSpace(responseSchema), host + "/" + tool.Id + " structured response schema");
                    }
                });
            }
        }

        private static void SimpleAgentExecutesToolAndReceivesJsonResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Добавляю лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"message\":\"Лист Report создан.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    AssertEqual(LlmResponseFormats.JsonObject, options.ResponseFormat, "single response format");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = CreateConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                var result = service.ExecuteWithApprovalsAsync(
                    ChatModes.Agent,
                    "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual("Лист Report создан.", result.AssistantText, "final response");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "successful write keeps the accepted model status");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "successful write completes the current run");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus, "final status enters accepted history");
                AssertRunViewState(result, session, "clean", 1, 0, 0);
                AssertTrue(adapter.HasSheet("Report"), "tool executed");
                AssertEqual(1, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "one write dispatch");
                AssertEqual(3, calls.Count, "schema read, execution, and final model turns");
                AssertContains(FlattenSimple(calls[0]),
                    "\"function\":{\"name\":\"excel.add_sheet\"",
                    "Excel core schema is complete before optional discovery");
                AssertContains(FlattenSimple(calls[1]), "\"kind\":\"tool-schema\"", "schema evidence reaches model");
                var finalRequest = FlattenSimple(calls[2]);
                AssertContains(finalRequest, "TOOL_RESULT", "tool result label");
                AssertContains(finalRequest, "\"status\":\"ok\"", "tool result status ok");
                AssertContains(finalRequest, "\"name\":\"excel.add_sheet\"", "tool result name");
                AssertContains(finalRequest, "\"message\":", "tool result message");
            });
        }

        // Phase 1C: loop completion and the model's text cannot certify an external effect.
        private static void SimpleAgentCharacterizesCompletedAfterWriteError()
        {
            var mixedCounts = new RNAssistant.Core.Agent.ToolCounts(writeOk: 1, writeError: 1);
            var mixed = new RNAssistant.Core.Agent.RunSummary("run", "turn", RNAssistant.Core.Agent.RunLifecycle.Completed,
                mixedCounts, 2, 2, "Результат исправлен.", "completed", null);
            var warning = ConversationRunProjection.AssistantMessage(mixed);
            AssertEqual(1, mixed.ToolCounts.WriteError, "successful later attempts do not erase historical errors");
            AssertTrue(!warning.Contains("Успешное применение всех изменений не подтверждено"),
                "error counts alone do not claim an unresolved final failure");
            AssertContains(warning, "а не число нерешённых проблем", "mixed outcomes remain attempt history");
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.QueueExcelSheetApplyFailure(
                    "Write rejected before the effect.", "write_rejected", false);
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Добавляю лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"message\":\"Лист Report создан.\",\"action\":\"done\",\"tool_calls\":[],\"executionSummary\":{\"ExecutionHealth\":\"clean\",\"WriteOk\":1000}}",
                    "{\"message\":\"Лист Report создан.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "excel.add_sheet");
                AssertEqual(false, (bool)write["success"], "write failure is preserved");
                AssertEqual("write_rejected", (string)write["errorCode"], "actual failure code is preserved");
                AssertTrue(!adapter.HasSheet("Report"), "the claimed sheet was not created");
                AssertEqual(1, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "failed write is not retried");
                AssertContains(FlattenSimple(requests.Last()), "\"status\":\"error\"",
                    "the final model request saw the failed tool result");
                AssertContains(FlattenSimple(requests.Last()), "Write rejected before the effect.",
                    "the folded error retains its actionable message");
                AssertContains(requests.Last().Last().Content, "unsupported root field: executionSummary", "model cannot inject runtime health into v7");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "loop completion is independent of execution health");
                AssertRunViewState(result, session, "errors", 0, 1, 0);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model completed is accepted after write error");
                AssertContains(result.AssistantText, "операций записи завершились ошибкой",
                    "runtime annotates a false success claim with authoritative write failure evidence");
                AssertContains(result.AssistantText, "История попыток:", "counts describe past attempts");
                AssertContains(result.AssistantText, "а не число нерешённых проблем", "attempt counts cannot infer current task failure");
                AssertContains(session.Messages.Last().Content, "последующее исправление конкретной операции ими не определяется",
                    "historical errors do not assert that the final task is still unresolved");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus, "false completion enters accepted history");
            });
        }

        private static void SimpleAgentCharacterizesCompletedAfterWriteUnknown()
        {
            WithTempPaths(paths =>
            {
                const string before = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string intended = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.VbaModuleCode = before;
                adapter.VbaWriteTransform = code => code.Replace("\"after\"", "\"diverged\"");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю текущий модуль.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"VBA module: Module1\",\"representation\":\"source\"}}]}",
                    "{\"message\":\"Загружаю процедуру редактирования VBA.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.skills_load\",\"arguments\":{\"id\":\"common.vba_code_editing\"}}]}",
                    new JObject
                    {
                        ["message"] = "Обновляю модуль.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    "{\"message\":\"Проверяю модуль после неподтверждённой записи.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"VBA module: Module1\",\"representation\":\"source\"}}]}",
                    "{\"message\":\"Модуль Module1 обновлён.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Обнови модуль Module1.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "common.vba_write_module");
                AssertEqual(false, (bool)write["success"], "unverified effect is not a successful tool result");
                AssertEqual("unknown", (string)write["status"], "typed manual projection preserves unknown status");
                AssertEqual("vba_mutation_unknown", (string)write["errorCode"], "real journal classified the divergent effect");
                AssertEqual(false, (bool)write["retryable"], "unknown write cannot be retried automatically");
                var activityData = session.Messages.Where(message => message.Activity != null &&
                    message.Activity.ToolId == "common.vba_write_module")
                    .Select(message => message.Activity.DataJson).Last();
                var writeData = JObject.Parse(activityData);
                AssertTrue(writeData["journalStatus"] == null,
                    "internal journal status does not leak into activity data");
                AssertTrue(!string.IsNullOrWhiteSpace((string)writeData["mutationId"]),
                    "unknown evidence retains mutation correlation");
                AssertEqual(VbaMutationStatuses.Unknown,
                    journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status, "durable journal also records unknown");
                AssertContains(adapter.VbaModuleCode, "\"diverged\"", "fake host state matches neither before nor intended");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "unknown write is dispatched once");
                AssertContains(FlattenSimple(requests.Last()), "\"status\":\"unknown\"", "model receives the unknown tool result");
                AssertContains(FlattenSimple(requests.Last()), "Final VBA state is unknown", "model receives the exact verification warning");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "loop completion is independent of execution health");
                AssertRunViewState(result, session, "unknown", 0, 0, 1);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model completed is accepted after inspecting the unknown write");
                AssertContains(result.AssistantText, "состояние 1 операций записи осталось неизвестным",
                    "runtime annotates an unverified mutation claim");
                AssertContains(session.Messages.Last().Content, "Успешное применение всех изменений не подтверждено",
                    "authoritative unknown-effect notice enters accepted history");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus, "unknown and completed coexist in history");
            });
        }

        private static void CausalTraceCorrelatesMutation(string outcome, int invalidAttempts)
        {
            WithTempPaths(paths =>
            {
                const string before = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string intended = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.VbaModuleCode = before;
                if (outcome == "unknown") adapter.VbaWriteTransform = code => code.Replace("\"after\"", "\"diverged\"");
                if (outcome == "error")
                    adapter.QueueVbaActionResult(
                        FakeVbaOperation.ReplaceModule,
                        VbaBackendActionResult.Error(
                            "Write rejected.", null,
                            "write_rejected", false));
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var store = new ChatStore(paths);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord
                {
                    RunId = "trace-run", TurnId = "trace-turn", Status = "running",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow
                };
                store.Save(session);
                var responses = new Queue<string>(Enumerable.Repeat("REJECTED_TRACE_SENTINEL", invalidAttempts).Concat(new[]
                {
                    LoadSkillResponse("common.vba_code_editing"),
                    new JObject
                    {
                        ["message"] = "Update module.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    ModelProtocolWire.WriteAction("The target cannot be inspected with available tools.",
                        outcome == "unknown" ? ConversationResponse.BlockedAction : ConversationResponse.DoneAction)
                }));
                var trace = new ModelTracePersistenceService(EventStore(store));
                var requestCount = 0;
                var rawByAttempt = new Dictionary<string, string>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    AssertTrue(!FlattenSimple(messages.ToList()).Contains("REJECTED_TRACE_SENTINEL"),
                        "rejected payload is absent from every subsequent prompt");
                    trace.Configure(options);
                    var requestId = "transport-" + (++requestCount);
                    // Fake transport records; exact real HTTP materialization has its own existing test.
                    options.TraceSink(new LlmTraceRecord
                    {
                        Type = "request", RequestId = requestId, Purpose = "agent",
                        PayloadJson = "{\"fake_transport\":true}", PayloadContentType = "application/json"
                    });
                    var content = responses.Dequeue();
                    rawByAttempt.Add(options.TraceModelAttemptId, content);
                    options.TraceSink(new LlmTraceRecord
                    {
                        Type = "response", RequestId = requestId, Purpose = "agent",
                        PayloadJson = content, PayloadContentType = "application/json"
                    });
                    return Task.FromResult(new LlmCompletionResult { Content = content });
                };
                ChatTurnResult result;
                using (RunCausalTrace.Begin(EventStore(store), session))
                {
                    result = CreateConversationRunService(adapter, executor, completion).ExecuteWithApprovalsAsync(
                        ChatModes.Agent, "Update Module1.", session, NewContext(adapter),
                        new AppSettings { AutoConfirmToolActions = false, MaxAgentFormatRetries = 20 },
                        OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                        .GetAwaiter().GetResult();
                }
                store.Save(session);
                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                var requests = events.Where(item => item.Type == SessionEventTypes.LlmRequest).ToList();
                var rejected = events.Where(item => item.Type == SessionEventTypes.AgentResponseRejected).ToList();
                var accepted = events.Where(item => item.Type == "model.response.accepted").ToList();
                AssertEqual(invalidAttempts + 3, requestCount, "trace introduces no retry or model request");
                AssertEqual(invalidAttempts, rejected.Count, "every rejected attempt has a diagnostic event");
                AssertEqual(3, accepted.Count, "only valid skill/write/final responses are accepted");
                AssertEqual(requestCount, requests.Select(item => (string)item.Data["ModelAttemptId"]).Distinct().Count(),
                    "each completion attempt has a distinct id");
                AssertTrue(requests.All(item => !string.IsNullOrWhiteSpace((string)item.Data["ModelAttemptId"])), "attempt ids exist");
                AssertEqual(1, requests.Take(invalidAttempts + 1).Select(item => (string)item.Data["StepId"]).Distinct().Count(),
                    "repair attempts keep the same logical step");
                AssertEqual(3, requests.Select(item => (string)item.Data["StepId"]).Distinct().Count(), "next iteration starts a new step");
                foreach (var verdict in rejected.Concat(accepted))
                {
                    var request = requests.Single(item => item.StepId == (string)verdict.Data["RequestId"]);
                    AssertEqual((string)request.Data["ModelAttemptId"], (string)verdict.Data["ModelAttemptId"], "verdict links its exact attempt");
                    AssertEqual((string)request.Data["StepId"], (string)verdict.Data["StepId"], "verdict retains logical step");
                    AssertTrue(request.Sequence < verdict.Sequence, "prepared request precedes parser verdict");
                }
                var acceptedCall = session.Messages.Single(message => message.Role == "assistant" &&
                    message.ToolName == "common.vba_write_module" && message.AcceptedCallOrigin != null);
                var callId = acceptedCall.ToolCallId;
                var acceptedWrite = accepted.Single(item => (string)item.Data["ModelAttemptId"] == acceptedCall.AcceptedCallOrigin.ModelAttemptId);
                AssertTrue(accepted.All(item => !((JArray)item.Data["ToolCallIds"]).Any()), "protocol verdict never assigns execution IDs");
                AssertEqual(2, events.Count(item => item.Type == "tool.execution.started" && (string)item.Data["ToolCallId"] == callId),
                    "preparation and explicit confirmation have distinct boundaries");
                var toolStart = events.Last(item => item.Type == "tool.execution.started" && (string)item.Data["ToolCallId"] == callId);
                var toolEnd = events.Last(item => item.Type == "tool.execution.completed" && (string)item.Data["ToolCallId"] == callId);
                foreach (var message in session.Messages.Where(item => item.AcceptedCallOrigin != null))
                {
                    var origin = message.AcceptedCallOrigin;
                    var raw = events.Single(item => item.Type == SessionEventTypes.LlmResponse &&
                        (string)item.Data["ModelAttemptId"] == origin.ModelAttemptId);
                    var rawText = store.ReadEventPayload(raw);
                    AssertEqual(rawByAttempt[origin.ModelAttemptId], rawText, "raw accepted response is never rewritten to inject IDs");
                    AssertEqual((string)raw.Data["StepId"], origin.StepId, "origin retains the actual model step");
                    var sourceCall = JObject.Parse(rawText)["tool_calls"][origin.CallIndex];
                    AssertEqual(message.ToolName, (string)sourceCall["name"], "index identifies the original call");
                    AssertTrue(sourceCall["id"] == null, "model did not supply execution identity");
                    var mapped = events.First(item => item.Type == SessionEventTypes.SessionCommit &&
                        ((JArray)item.Data["Operations"]).Any(operation =>
                            (string)operation["Data"]?["Value"]?["ToolCallId"] == message.ToolCallId &&
                            operation["Data"]?["Value"]?["AcceptedCallOrigin"] != null));
                    var mappedOperation = ((JArray)mapped.Data["Operations"]).OfType<JObject>().Single(operation =>
                        (string)operation["Data"]?["Value"]?["ToolCallId"] == message.ToolCallId &&
                        operation["Data"]?["Value"]?["AcceptedCallOrigin"] != null);
                    AssertEqual(SessionOperationTypes.ToolCallRecorded, (string)mappedOperation["Type"],
                        "writer classifies runtime-owned accepted origin independently of native tool-call shape");
                    var start = events.First(item => item.Type == "tool.execution.started" &&
                        (string)item.Data["ToolCallId"] == message.ToolCallId);
                    AssertTrue(raw.Sequence < mapped.Sequence && mapped.Sequence < start.Sequence,
                        "raw attempt and accepted position-to-ID mapping are durable before tool entry");
                }
                AssertEqual((string)acceptedWrite.Data["StepId"], toolStart.StepId, "accepted response and tool use one step id");
                AssertTrue(acceptedWrite.Sequence < toolStart.Sequence && toolStart.Sequence < toolEnd.Sequence, "accepted call precedes execution");
                var mutation = journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                var effects = events.Where(item => item.Type.StartsWith("domain.effect.", StringComparison.Ordinal)).ToList();
                AssertEqual("domain.effect.prepared,domain.effect.dispatched,domain.effect.verified",
                    string.Join(",", effects.Select(item => item.Type)), "domain boundaries preserve order");
                AssertTrue(effects.All(item => toolStart.Sequence < item.Sequence && item.Sequence < toolEnd.Sequence), "domain lies inside tool execution");
                foreach (var effect in effects)
                {
                    AssertEqual(mutation.Prepared.MutationId, (string)effect.Data["MutationId"], "trace links the real journal id");
                    AssertEqual(mutation.Prepared.StepId, effect.StepId, "journal and tool step agree");
                    AssertEqual(callId, (string)effect.Data["ToolCallId"], "domain links runtime allocated call");
                    AssertEqual(mutation.Prepared.RunId, (string)effect.Data["JournalRunId"], "journal origin remains explicit");
                }
                var causal = new EventStreamTrajectoryQuery().QueryView(events, new TrajectoryViewQueryRequest
                {
                    View = TrajectoryViews.RunCausal,
                    TurnId = "trace-turn",
                    PageSize = 200
                }).Rows;
                AssertTrue(causal.Select(item => item.FirstSequence).SequenceEqual(
                    causal.Select(item => item.FirstSequence).OrderBy(value => value)),
                    "causal projection keeps actual writer evidence chronological");
                var mappedCall = causal.Single(item => item.Kind == SessionOperationTypes.ToolCallRecorded && item.ToolCallId == callId);
                AssertEqual(acceptedCall.AcceptedCallOrigin.ModelAttemptId, mappedCall.ModelAttemptId,
                    "causal projection joins the actual accepted call to its raw attempt");
                AssertTrue(causal.Any(item => item.Kind == "domain.effect.verified" &&
                    item.MutationId == mutation.Prepared.MutationId),
                    "causal projection exposes actual journal verification evidence");
                var expected = outcome == "unknown" ? VbaMutationStatuses.Unknown : outcome == "error" ? VbaMutationStatuses.NotApplied : VbaMutationStatuses.Committed;
                AssertEqual(expected, (string)effects.Last().Data["Status"], "trace reports actual domain assessment without promoting unknown");
                AssertEqual(expected, mutation.Terminal.Status, "durable journal agrees with assessment");
                foreach (var item in requests.Concat(rejected).Concat(accepted).Concat(effects).Concat(new[] { toolStart, toolEnd }))
                {
                    AssertEqual(session.Id, item.SessionId, "session correlation");
                    AssertEqual("trace-run", item.RunId, "run correlation");
                    AssertEqual("trace-turn", item.TurnId, "turn correlation");
                    AssertEqual(adapter.RuntimeDocumentKey, (string)item.Data["DocumentRuntimeId"], "runtime document correlation");
                }
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "exactly one write dispatch");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "runtime lifecycle remains completed");
                AssertTrue(!FlattenSimple(store.Load(session.Id).Messages).Contains("REJECTED_TRACE_SENTINEL"), "trace never enters accepted history");
            });
        }

        private static void SimpleAgentCharacterizesCompletedWithoutWrite()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    calls++;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Лист Report создан.\",\"action\":\"done\",\"tool_calls\":[]}"
                    });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(1, calls, "terminal no-call response stops the loop");
                AssertEqual(0, result.ToolResults.Count, "there is no tool effect evidence");
                AssertEqual(0, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "no requested write was dispatched");
                AssertTrue(!adapter.HasSheet("Report"), "model text did not create a sheet");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "a no-write response may finish the loop");
                AssertRunViewState(result, session, "clean", 0, 0, 0);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "no-write response carries model completed");
                AssertEqual("Лист Report создан.", session.Messages.Last().Content, "unsupported mutation claim reaches visible history");
            });
        }

        private static void AssertRunViewState(
            ChatTurnResult result, ChatSession session, string health, int writeOk, int writeError, int writeUnknown)
        {
            var view = JObject.FromObject(result)["RunViewState"] as JObject;
            AssertTrue(view != null, "typed run view state is required independently of model completed");
            AssertEqual(health, (string)view["ExecutionHealth"], "runtime and effect evidence own execution health");
            AssertEqual(writeOk, (int)view["VerifiedWrites"] + (int)view["NoChangeWrites"] +
                (int)view["UnverifiedWrites"], "successful write count");
            AssertEqual(writeError, (int)view["FailedCalls"], "definite failed call count");
            AssertEqual(writeUnknown + (int)view["UnverifiedWrites"], (int)view["UnknownEffects"], "uncertain effect count");
            AssertTrue(JToken.DeepEquals(view, JObject.FromObject(session.Messages.Last())["RunViewState"]),
                "visible final message retains typed runtime projection independently of its narrative");
        }

        private static void SimpleAgentPromptIsRequestLocal()
        {
            var publishedSettings = new AppSettings
            {
                SystemPrompt = "SYSTEM_PROMPT_SENTINEL",
                AgentToolsPrompt = "TOOLS_PROMPT_SENTINEL",
                AgentSkillsPrompt = "SKILLS_PROMPT_SENTINEL"
            };
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), publishedSettings,
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю листы.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Готово.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord
                {
                    RunId = Guid.NewGuid().ToString("N"),
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey,
                    StartedUtc = DateTime.UtcNow
                };
                var settings = publishedSettings.Clone();
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "List sheets.", session, NewContext(adapter), settings,
                    OfficeToolCatalog.ForHost(adapter.HostName).ToList(), null).GetAwaiter().GetResult();

                AssertEqual("Готово.", result.AssistantText, "agent completed");
                AssertEqual(2, requests.Count, "two model requests");
                foreach (var request in requests)
                {
                    AssertEqual(1, request.Count(message =>
                        (message.Content ?? string.Empty).IndexOf("SYSTEM_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0),
                        "system prompt appears once per request");
                    AssertEqual(1, request.Count(message =>
                        (message.Content ?? string.Empty).IndexOf("RUNTIME_CONTEXT:", StringComparison.Ordinal) >= 0),
                        "runtime context appears once per request");
                    var materialized = FlattenSimple(request);
                    var generalIndex = materialized.IndexOf("SYSTEM_PROMPT_SENTINEL", StringComparison.Ordinal);
                    var toolsIndex = materialized.IndexOf("TOOLS_PROMPT_SENTINEL", StringComparison.Ordinal);
                    var skillsIndex = materialized.IndexOf("SKILLS_PROMPT_SENTINEL", StringComparison.Ordinal);
                    var runtimeIndex = materialized.IndexOf("RUNTIME_CONTEXT:", StringComparison.Ordinal);
                    AssertTrue(generalIndex >= 0 && generalIndex < toolsIndex && toolsIndex < skillsIndex && skillsIndex < runtimeIndex,
                        "general, tool, skill, and runtime prompt sections keep stable order");
                    AssertTrue(request.Any(message =>
                        (message.Content ?? string.Empty).IndexOf("\"office_tools_available\":true", StringComparison.Ordinal) >= 0),
                        "runtime context exposes document availability");
                    AssertTrue(!request.Any(message =>
                        (message.Content ?? string.Empty).IndexOf("html_workspace_preferred", StringComparison.Ordinal) >= 0),
                        "runtime context has no separate HTML preference");
                }
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf("SYSTEM_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("TOOLS_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("SKILLS_PROMPT_SENTINEL", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("RUNTIME_CONTEXT:", StringComparison.Ordinal) >= 0),
                    "prompt is not persisted in chat history");
            });
        }

        private static void SimpleAgentRepairsInvalidResponse()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string invalid = "PLAIN_INVALID_RESPONSE_SENTINEL";
                var responses = new Queue<LlmCompletionResult>(new[]
                {
                    new LlmCompletionResult { Content = invalid, ReasoningContent = "INVALID_REASONING_SENTINEL" },
                    new LlmCompletionResult { Content = "{\"message\":\"Не могу выполнить этот запрос.\",\"action\":\"done\",\"tool_calls\":[]}" }
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(responses.Dequeue());
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Restricted request.", session, NewContext(adapter), new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(2, requests.Count, "one repair request");
                AssertEqual("Не могу выполнить этот запрос.", result.AssistantText, "formatted refusal accepted");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model-authored refusal text only ends its loop; it is not provider refusal");
                var repair = requests[1].Last();
                AssertContains(repair.Content, "FORMAT_REPAIR", "repair instruction added");
                AssertContains(repair.Content, "message (string)", "refusal can remain final message text without a status field");
                AssertTrue(FlattenSimple(requests[1]).IndexOf(invalid, StringComparison.Ordinal) < 0,
                    "invalid raw response is not copied into repair prompt");
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf(invalid, StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("FORMAT_REPAIR", StringComparison.Ordinal) >= 0 ||
                    (message.ReasoningContent ?? string.Empty).IndexOf("INVALID_REASONING_SENTINEL", StringComparison.Ordinal) >= 0),
                    "invalid completion and repair instruction are not persisted");
            });
        }

        private static void SimpleAgentUsesStatusFreeResponse()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string invalidPair = "{\"status\":\"in_progress\",\"message\":\"Проверяю листы...\",\"action\":\"done\",\"tool_calls\":[]}";
                var responses = new Queue<string>(new[]
                {
                    invalidPair,
                    LoadToolSchemaResponse("excel.inspect"),
                    "{\"message\":\"Проверяю листы.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Список листов проверен.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Проверь листы.", session, NewContext(adapter), new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(4, requests.Count, "structural repair, schema discovery, and tool continuation");
                AssertContains(requests[1].Last().Content, "unsupported root field: status", "repair rejects the old envelope instead of adapting it");
                AssertEqual("Список листов проверен.", result.AssistantText, "run completes after the actual tool call");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "legacy lifecycle projection means only model loop ended");
                AssertEqual(AgentResponseProtocol.CurrentVersion, session.Messages.Last().ResponseProtocolVersion,
                    "terminal response protocol version is persisted");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus,
                    "terminal response status is persisted");
                AssertTrue(!session.Messages.Any(message => string.Equals(message.Content, invalidPair, StringComparison.Ordinal)),
                    "rejected status mismatch is not persisted");

                var terminalCases = new[]
                {
                    "Укажите лист", "Документ недоступен.", "Не могу выполнить запрос.", "", "  "
                };
                foreach (var terminalCase in terminalCases)
                {
                    var terminalSession = NewSession(adapter);
                    var terminalService = CreateConversationRunService(
                        adapter,
                        executor,
                        (settings, messages, options, stream, cancellationToken) => Task.FromResult(
                            new LlmCompletionResult
                            {
                                Content = JsonConvert.SerializeObject(new
                                {
                                    message = terminalCase,
                                    action = "done",
                                    tool_calls = new object[0]
                                })
                            }));
                    var terminalResult = terminalService.ExecuteAsync(
                        ChatModes.Agent,
                        "Продолжай.",
                        terminalSession,
                        NewContext(adapter),
                        new AppSettings(),
                        OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                        null).GetAwaiter().GetResult();
                    AssertEqual(terminalCase, terminalResult.AssistantText, "message is preserved without classification or trimming");
                    AssertEqual(AgentResponseStatuses.Completed, terminalResult.ResponseStatus,
                        "runtime projection does not infer a status from wording");
                    AssertEqual(RunViewLifecycles.Completed, terminalResult.RunViewState.Lifecycle,
                        "final empty calls end the model loop");
                    AssertEqual(AgentResponseProtocol.CurrentVersion, terminalResult.ResponseProtocolVersion,
                        "final record carries the active protocol version");
                    AssertTrue(ConversationResponseHistoryReader.Read(terminalSession.Messages.Last()).Success,
                        "actual final history is a valid v7 form even with empty or question-like text");
                }

                var limitedSession = NewSession(adapter);
                var limitedService = CreateConversationRunService(
                    adapter,
                    executor,
                    (settings, messages, options, stream, cancellationToken) => Task.FromResult(
                        new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Проверяю ресурсы.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_find\",\"arguments\":{}}]}",
                            PromptTokens = 5
                        }));
                var limitedResult = limitedService.ExecuteAsync(
                    ChatModes.Agent,
                    "Проверяй до лимита.",
                    limitedSession,
                    NewContext(adapter),
                    new AppSettings { MaxAgentIterations = 1 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Failed, limitedResult.RunViewState.Lifecycle,
                    "runtime step limit is not projected as model-declared completion");
                AssertTrue(string.IsNullOrWhiteSpace(limitedResult.ResponseStatus),
                    "runtime step limit has no synthetic model response status");
                AssertEqual("step_limit_reached", limitedSession.Messages.Last().Activity.ExecutionStatus,
                    "runtime step limit is stored as a diagnostic outcome");
                AssertEqual(5, limitedSession.Messages.Sum(message => message.PromptTokens ?? 0),
                    "runtime diagnostic does not duplicate prior model usage");
            });
        }

        private static void SimpleAgentRepairsNoToolNonFinal()
        {
            foreach (var responseMode in new[] { AgentResponseModes.JsonSchema, AgentResponseModes.JsonObject })
            foreach (var toolResultRole in new[] { ToolResultRoles.Tool, ToolResultRoles.User })
            foreach (var needsTool in new[] { false, true })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string promise = "Сейчас подготовлю CSS.";
                const string answer = "Добавьте после исходных стилей:\n```css\nbutton { border-radius: 8px; }\n```";
                var responses = new Queue<string>();
                responses.Enqueue(new JObject { ["message"] = promise, ["action"] = "tool", ["tool_calls"] = new JArray() }.ToString());
                if (needsTool)
                    responses.Enqueue(V4Envelope(V4Call("common.capabilities_search", new JObject { ["query"] = "CSS" })));
                responses.Enqueue(ModelProtocolWire.WriteAction(answer, ConversationResponse.DoneAction));
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    needsTool ? "Найди доступные инструменты CSS и предложи скругление button." : "Предложи CSS для скругления button.",
                    session, NewContext(adapter), new AppSettings { AgentResponseMode = responseMode, ToolResultRole = toolResultRole },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    null).GetAwaiter().GetResult();

                AssertEqual(needsTool ? 3 : 2, requests.Count, "repair either delivers the answer or continues through a real tool result");
                AssertContains(FlattenSimple(requests[1]), "FORMAT_REPAIR:",
                    "repair reaches the model before accepting an answer");
                AssertContains(FlattenSimple(requests[1]), "action must be tool with calls",
                    "repair explains the exact invalid combination");
                AssertContains(FlattenSimple(requests[1]), "Never relabel a promise as done",
                    "format repair cannot instruct the model to abandon unfinished work by changing only action");
                AssertContains(FlattenSimple(requests[1]), ModelProtocolWire.ActionGuidance,
                    "both response formats receive the same work-or-answer decision rules");
                AssertTrue(!session.Messages.Any(message => (message.Content ?? string.Empty).Contains(promise)),
                    "invalid no-tool response is not accepted into durable history");
                if (needsTool)
                {
                    var outputs = session.Messages.Where(message => !string.IsNullOrWhiteSpace(message.ToolCallId) &&
                        message.ToolCalls.Count == 0 && message.Activity == null && message.Role != "assistant").ToList();
                    AssertEqual(1, outputs.Count, "the corrected call produces exactly one accepted result");
                    AssertContains(FlattenSimple(requests[2]), "common.capabilities_search", "next step receives the accepted tool exchange");
                }
                AssertEqual(answer, result.AssistantText, "terminal response delivers the actual code without scheduling another model request");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle,
                    "action=done with no calls ends the run");
            });
        }

        private static void SimpleAgentFailedRepairDoesNotPolluteContext()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "PROTECTION_RESPONSE_" + requests.Count,
                        ReasoningContent = "INVALID_DIAGNOSTIC_REASONING"
                    });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Do something.", session, NewContext(adapter), new AppSettings { MaxAgentFormatRetries = 20 },
                    OfficeToolCatalog.ForHost(adapter.HostName).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(20, requests.Count, "twenty total responses including the initial request");
                AssertContains(result.AssistantText, "после 20 попыток", "diagnostic counts total protocol responses");
                AssertEqual(RunViewLifecycles.Failed, result.RunViewState.Lifecycle, "all invalid responses fail the run");
                AssertRunViewState(result, session, "clean", 0, 0, 0);
                AssertTrue(string.IsNullOrWhiteSpace(result.ResponseStatus), "no accepted model status after exhausted repair");
                AssertEqual(0, result.ToolResults.Count, "invalid responses never execute tools");
                AssertTrue(session.Messages.Last().Activity != null, "diagnostic activity recorded");
                AssertTrue(session.Messages.Last().ExcludeFromModelContext, "diagnostic excluded from replay");
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf("PROTECTION_RESPONSE_", StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("FORMAT_REPAIR", StringComparison.Ordinal) >= 0 ||
                    (message.ReasoningContent ?? string.Empty).IndexOf("INVALID_DIAGNOSTIC_REASONING", StringComparison.Ordinal) >= 0),
                    "failed completions do not enter stored context");
                AssertTrue(!requests.SelectMany(request => request).Any(message =>
                    (message.Content ?? string.Empty).Contains("PROTECTION_RESPONSE_") ||
                    (message.ReasoningContent ?? string.Empty).Contains("INVALID_DIAGNOSTIC_REASONING")),
                    "rejected responses do not enter later repair requests");
            });
        }

        private static void SimpleAgentRepairsOnTwentiethAttempt()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(requests.Count < 20
                        ? new LlmCompletionResult
                        {
                            Content = "PROTECTION_RESPONSE_" + requests.Count,
                            ReasoningContent = "REJECTED_REASONING"
                        }
                        : new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Ответ принят.\",\"action\":\"done\",\"tool_calls\":[]}",
                            ReasoningContent = "ACCEPTED_REASONING"
                        });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Ответь на вопрос.", session, NewContext(adapter),
                    new AppSettings { MaxAgentFormatRetries = 20 },
                    OfficeToolCatalog.ForHost(adapter.HostName).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(20, requests.Count, "nineteen protection responses followed by one valid response");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "twentieth request can complete the run");
                AssertRunViewState(result, session, "clean", 0, 0, 0);
                AssertEqual(0, result.ToolResults.Count, "repair attempts do not dispatch tools");
                var accepted = session.Messages.Where(message => message.Role == "assistant" && !message.ExcludeFromModelContext).ToList();
                AssertEqual(1, accepted.Count, "only one assistant response enters accepted history");
                AssertEqual("Ответ принят.", accepted[0].Content, "accepted message is preserved");
                AssertEqual("ACCEPTED_REASONING", accepted[0].ReasoningContent, "only accepted provider reasoning is retained");
                AssertTrue(!session.Messages.Concat(requests.SelectMany(request => request)).Any(message =>
                    (message.Content ?? string.Empty).Contains("PROTECTION_RESPONSE_") ||
                    (message.ReasoningContent ?? string.Empty).Contains("REJECTED_REASONING")),
                    "all nineteen rejected attempts stay out of accepted history and requests");
                AssertTrue(!session.Messages.Any(message => (message.Content ?? string.Empty).Contains("FORMAT_REPAIR")),
                    "repair instructions are ephemeral");
                var initialPrompt = FlattenSimple(requests[0]);
                foreach (var request in requests.Skip(1))
                {
                    AssertEqual(1, request.Count(message => (message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal)),
                        "each retry carries one current repair instruction");
                    AssertEqual(initialPrompt, FlattenSimple(request.Take(request.Count - 1)), "repair starts from the same clean accepted prompt");
                }
            });
        }

        private static void SimpleAgentClampsFormatRepairLimit()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult { Content = "INVALID" });
                };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Do something.", NewSession(adapter), NewContext(adapter), new AppSettings { MaxAgentFormatRetries = 99 },
                    OfficeToolCatalog.ForHost(adapter.HostName).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(20, calls, "at most twenty protocol responses including the initial request");
                AssertContains(result.AssistantText, "после 20 попыток", "clamped total-attempt diagnostic");
            });
        }

        private static void SimpleAgentExposesSafeVbaEditingTools()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                IReadOnlyList<ChatMessage> request = null;
                LlmRequestOptions requestOptions = null;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    request = messages.ToList();
                    requestOptions = options;
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"message\":\"Готово.\",\"action\":\"done\",\"tool_calls\":[]}" });
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var promptSettings = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Inspect VBA.", NewSession(adapter), NewContext(adapter),
                    promptSettings, tools, null, null,
                    BuiltInSkillProvider.GetSkills(adapter))
                    .GetAwaiter().GetResult();

                AssertTrue(request != null, "agent request reaches model boundary: " + result.AssistantText);
                var estimated = ModelContextBudget.EstimateMessagesTokens(request, promptSettings) +
                    ModelContextBudget.EstimateRequestOptionsTokens(requestOptions, promptSettings) +
                    ModelProtocolClient.EstimateFormatRepairOverheadTokens(promptSettings);
                AssertTrue(ModelContextBudget.InputBudgetTokens(promptSettings) - estimated >=
                    ModelContextBudget.ContinuationReserveTokens(promptSettings),
                    "minimal Excel/VBA editing core keeps the shared continuation reserve");
                var prompt = FlattenSimple(request);
                AssertContains(prompt, "\"name\":\"common.resources_find\"", "semantic resource discovery exposed");
                AssertContains(prompt, "\"name\":\"common.resources_read\"", "resource reads exposed");
                AssertTrue(prompt.IndexOf("\"name\":\"common.resources_list\"", StringComparison.Ordinal) < 0 &&
                    prompt.IndexOf("\"name\":\"common.resources_resolve\"", StringComparison.Ordinal) < 0 &&
                    prompt.IndexOf("\"name\":\"common.resources_search\"", StringComparison.Ordinal) < 0,
                    "retired resource transport tools are absent");
                AssertContains(prompt, "\"name\":\"common.capabilities_search\"", "unified capability search exposed");
                AssertContains(prompt, "\"name\":\"common.capabilities_read\"", "unified exact capability loading exposed");
                AssertContains(prompt, "\"id\":\"common.vba_apply_patch\"", "exact VBA mutation id is in compact catalog");
                AssertContains(prompt, "\"id\":\"common.vba_code_editing\"", "exact VBA skill id is in compact catalog");
                AssertTrue(prompt.IndexOf("\"id\":\"common.vba_inspect\"", StringComparison.Ordinal) < 0,
                    "invented VBA capability id is absent from the authoritative catalog");
                var callableNames = ResponseSchemaCallNames(requestOptions.ResponseSchemaJson)
                    .ToList();
                AssertTrue(!callableNames.Contains("common.vba_apply_patch", StringComparer.OrdinalIgnoreCase) &&
                    !callableNames.Contains("common.vba_write_module", StringComparer.OrdinalIgnoreCase),
                    "VBA editing mutations require a selected skill before they are callable");
                foreach (var optionalId in new[]
                {
                    "common.vba_restore_backup", "common.vba_rename_module",
                    "common.vba_delete_module", "common.office_run_macro"
                })
                {
                    AssertTrue(!callableNames.Contains(optionalId, StringComparer.OrdinalIgnoreCase),
                        "explicit VBA intent is not callable before exact admission: " + optionalId);
                    AssertContains(prompt, "\"id\":\"" + optionalId + "\"",
                        "compact catalog keeps the exact optional VBA intent discoverable");
                }
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_create_module\"", StringComparison.Ordinal) < 0,
                    "redundant create alias is hidden from the model");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_replace_text\"", StringComparison.Ordinal) < 0,
                    "redundant replace alias is hidden from the model");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_read_lines\"", StringComparison.Ordinal) < 0,
                    "VBA range-read alias is removed");
                AssertTrue(prompt.IndexOf("\"name\":\"common.vba_read_module\"", StringComparison.Ordinal) < 0 &&
                    prompt.IndexOf("\"name\":\"common.vba_search_code\"", StringComparison.Ordinal) < 0 &&
                    prompt.IndexOf("\"name\":\"common.vba_list_backups\"", StringComparison.Ordinal) < 0,
                    "VBA discovery and reads use only the shared resource contract");
                AssertTrue(prompt.IndexOf("\"expectedCodeSha256\"", StringComparison.Ordinal) < 0,
                    "model-facing VBA schemas do not require a hash argument");
                AssertTrue(prompt.IndexOf("\"name\":\"excel.vba_read_module\"", StringComparison.Ordinal) < 0,
                    "raw host VBA read backend remains hidden");
                AssertTrue(prompt.IndexOf("\"name\":\"excel.vba_replace_module\"", StringComparison.Ordinal) < 0,
                    "raw whole-module backend remains hidden");
                AssertContains(prompt, "\"id\":\"common.office_run_macro\"", "host-neutral arbitrary macro execution is discoverable by exact id");
                AssertTrue(prompt.IndexOf("\"id\":\"excel.run_macro\"", StringComparison.Ordinal) < 0,
                    "host macro backend is hidden from the compact catalog");
            });
        }

        private static void AgentPreservesVbaResourceEvidenceWithinBudget()
        {
            RunVbaResourceIntentScenario(6000);
        }

        private static void SimpleAgentR61RoutineExcelAvoidsSchemaDiscovery()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(
                OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string userText = "List workbook sheets.";
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю листы.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Листы прочитаны.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var requestOptions = new List<LlmRequestOptions>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    requestOptions.Add(options);
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var settings = new AppSettings
                {
                    AgentResponseMode = AgentResponseModes.JsonSchema,
                    ContextWindowOverrideTokens = 65536,
                    MaxTokens = 1024
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var context = NewContext(adapter);

                var runnable = ConversationRunService.PrepareToolsForMode(ChatModes.Agent, tools);
                CapabilityCatalogService.BindReadSchema(runnable, null);
                var currentPack = CallableToolPack.Create(
                    ChatModes.Agent, adapter.HostName, "r61-eval", runnable);
                var legacyCoreIds = new HashSet<string>(
                    currentPack.Tools.Select(tool => tool.Id), StringComparer.Ordinal);
                legacyCoreIds.UnionWith(OfficeToolCatalog.ForHost(adapter.HostName).Select(tool => tool.Id));
                var legacyCore = runnable.Where(tool => legacyCoreIds.Contains(tool.Id)).ToArray();
                var composer = new ModelContextCompiler(projection: ModelToolResultProjection.Instance);
                var currentMessages = composer.BuildPreview(
                    ChatModes.Agent, userText, adapter, currentPack.Tools, null, context,
                    settings, session, null, false, 60000,
                    currentPack.CapabilityContext(null));
                var legacyMessages = composer.BuildPreview(
                    ChatModes.Agent, userText, adapter, legacyCore, null, context,
                    settings, session, null, false, 60000,
                    CapabilityCatalogService.BuildPromptCatalog(runnable, null, legacyCore));
                var currentOptions = ConversationModelSession.BuildRequestOptions(
                    ChatModes.Agent, settings.AgentResponseMode, currentPack.Tools,
                    new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(runnable)), session, null);
                var legacyOptions = ConversationModelSession.BuildRequestOptions(
                    ChatModes.Agent, settings.AgentResponseMode, legacyCore,
                    new ModelProtocolCallContext(ConversationProtocolContext.SequentialBatchIds(runnable)), session, null);
                var repairReserve = ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings);
                var continuationReserve = ModelContextBudget.ContinuationReserveTokens(settings);
                var currentTokens = ModelContextBudget.EstimateAdmittedRequestTokens(
                    currentMessages, currentOptions, settings, repairReserve, continuationReserve);
                var legacyTokens = ModelContextBudget.EstimateAdmittedRequestTokens(
                    legacyMessages, legacyOptions, settings, repairReserve, continuationReserve);
                AssertEqual(6, currentPack.Tools.Count,
                    "Excel Agent core contains five bootstrap tools and inspection");
                AssertEqual(20, legacyCore.Length,
                    "counterfactual Excel core retains all fifteen Excel schemas");
                AssertTrue(currentTokens < legacyTokens,
                    "R61 core lowers deterministic initial input estimate; before=" +
                    legacyTokens + ", after=" + currentTokens);

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, userText, session, context, settings, tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual("Листы прочитаны.", result.AssistantText,
                    "routine Excel task completes with the reduced core");
                AssertEqual(2, requests.Count,
                    "routine Excel read uses one execution step and one final response");
                AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                    operation == FakeOfficeAdapter.ExcelInspectOperation),
                    "routine Excel read dispatches once");
                AssertEqual(1, result.RunViewState.SuccessfulReads,
                    "routine Excel read retains typed effect evidence");
                AssertTrue(requests.SelectMany(items => items).All(message =>
                    !string.Equals(message.ToolName, CapabilityToolCatalog.ReadToolId, StringComparison.Ordinal)),
                    "routine Excel task adds no capability-schema call");
                AssertTrue(requests.SelectMany(items => items).All(message =>
                    !(message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal)),
                    "routine Excel task needs no format repair");
                AssertTrue(ResponseSchemaCallNames(requestOptions[0].ResponseSchemaJson)
                    .Any(name => name == "excel.inspect"),
                    "routine Excel intent remains callable on the initial step");
            });
        }

        private static void AgentReadsLargeVbaResourceWholeWithinBudget()
        {
            RunVbaResourceIntentScenario(9000);
        }

        private static void AgentRejectsCallerOwnedVbaContinuationState()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    ModelProtocolWire.Write("Invalid caller state.", new[]
                    {
                        new ConversationToolCall
                        {
                            Name = ResourceToolCatalog.ReadToolId,
                            Arguments = new JObject
                            {
                                ["uri"] = "rna://vba/guessed/module",
                                ["cursor"] = "guessed",
                                ["maxChars"] = 1000
                            }
                        }
                    }),
                    ModelProtocolWire.Write("Нужна семантическая цель.", new ConversationToolCall[0])
                });
                var modelCalls = 0;
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    modelCalls++;
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Read guessed VBA continuation state.",
                    session,
                    NewContext(adapter),
                    new AppSettings(),
                    OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList(),
                    null,
                    null,
                    BuiltInSkillProvider.GetSkills(adapter)).GetAwaiter().GetResult();

                AssertEqual("Нужна семантическая цель.", result.AssistantText,
                    "model repair can recover with no tool dispatch");
                AssertEqual(2, modelCalls, "caller-owned URI/cursor/page size is rejected before acceptance");
                var counts = session.LastRun.KernelState.Summary.ToolCounts;
                AssertEqual(0, counts.ReadOk + counts.ReadError + counts.WriteOk +
                    counts.WriteError + counts.WriteUnknown,
                    "invalid plumbing arguments never become an accepted tool call");
            });
        }

        private static void RunVbaResourceIntentScenario(int sourceCharacters)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string moduleName = "BudgetModule";
                const string sourceMarker = "VBA_RESOURCE_SOURCE_SENTINEL";
                var prefix = "Option Explicit\n' " + sourceMarker + "\n";
                var source = prefix + new string('x', Math.Max(0, sourceCharacters - prefix.Length));
                adapter.SetVbaModule(moduleName, source, "StdModule");

                var calls = new List<Tuple<IReadOnlyList<ChatMessage>, LlmRequestOptions>>();
                string semanticTarget = null;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(Tuple.Create((IReadOnlyList<ChatMessage>)messages.ToList(), options));
                    if (calls.Count == 1)
                    {
                        return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.Write(
                            "Ищу VBA-модули.", new[]
                            {
                                new ConversationToolCall
                                {
                                    Name = ResourceToolCatalog.FindToolId,
                                    Arguments = new JObject { ["scope"] = "vba", ["query"] = moduleName }
                                }
                            }) });
                    }

                    if (calls.Count == 2)
                    {
                        var wire = LastToolResult(messages, ResourceToolCatalog.FindToolId);
                        AssertEqual("ok", (string)wire["status"], "VBA semantic find remains successful");
                        var data = wire["data"] as JObject;
                        AssertTrue(data != null && data["items"] is JArray,
                            "VBA find data is preserved instead of a transport truncation wrapper");
                        var component = ((JArray)data["items"]).OfType<JObject>().Single(item =>
                            string.Equals((string)item["title"], moduleName, StringComparison.OrdinalIgnoreCase));
                        semanticTarget = (string)component["target"];
                        AssertEqual("VBA module: " + moduleName, semanticTarget,
                            "VBA find exposes a readable target without provider vocabulary");
                        AssertTrue(component["reference"] == null && component["provider"] == null && component["kind"] == null,
                            "VBA candidate hides URI, revision, provider, and provider kind");
                        AssertTrue(wire["resources"] == null,
                            "model find result omits exact root resource evidence");
                        return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.Write(
                            "Читаю исходник.", new[]
                            {
                                new ConversationToolCall
                                {
                                    Name = ResourceToolCatalog.ReadToolId,
                                    Arguments = new JObject
                                    {
                                        ["target"] = semanticTarget,
                                        ["representation"] = ResourceRepresentations.Source
                                    }
                                }
                            }) });
                    }

                    if (calls.Count == 3)
                    {
                        var readWire = LastToolResult(messages, ResourceToolCatalog.ReadToolId);
                        AssertEqual("ok", (string)readWire["status"],
                            "first VBA source page remains successful: " + readWire.ToString(Formatting.None));
                        var readData = readWire["data"] as JObject;
                        AssertTrue(readData != null && readData["resource"] == null && readData["nextCursor"] == null,
                            "VBA read hides exact reference and continuation plumbing");
                        AssertEqual(source, (string)readData["text"],
                            "the complete VBA source reaches the model in one result");
                        AssertContains((string)readData["text"], sourceMarker, "VBA source reaches the model");
                        AssertTrue(readWire["resources"] == null,
                            "model VBA read omits the exact root resource reference");
                        AssertEqual(true, (bool)readData["complete"],
                            "whole resource read is complete");
                        AssertTrue(readData["hasMore"] == null && readData["progressCharacters"] == null,
                            "whole read exposes no model continuation state");
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = ModelProtocolWire.Write("VBA прочитан.", new ConversationToolCall[0])
                        });
                    }
                    throw new InvalidOperationException("Unexpected model step in whole VBA resource scenario.");
                };

                var settings = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema,
                    ContextWindowOverrideTokens = 40000 };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Прочитай VBA-модуль " + moduleName + ".",
                    session,
                    NewContext(adapter),
                    settings,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    null,
                    null,
                    BuiltInSkillProvider.GetSkills(adapter)).GetAwaiter().GetResult();

                var requestTotals = string.Join(",", calls.Select(request =>
                    ModelContextBudget.EstimateAdmittedRequestTokens(
                        request.Item1,
                        request.Item2,
                        settings,
                        ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings),
                        ModelContextBudget.ContinuationReserveTokens(settings))).ToArray());
                AssertEqual("VBA прочитан.", result.AssistantText,
                    "VBA resource loop completes; admitted requests=" + requestTotals);
                AssertEqual(3, calls.Count,
                    "semantic find, one whole read, and final response use the expected model steps");
                var durableResults = session.Messages.Where(message => message != null &&
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                    message.AcceptedCallOrigin == null &&
                    (string.Equals(message.ToolName, ResourceToolCatalog.FindToolId, StringComparison.Ordinal) ||
                     string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal)))
                    .ToList();
                AssertEqual(2, durableResults.Count,
                    "durable history retains find and one complete read result");
                foreach (var durableMessage in durableResults)
                {
                    ToolResultWireReadResult durable;
                    string durableError;
                    AssertTrue(ToolResultHistoryReader.TryRead(
                        durableMessage, out durable, out durableError),
                        "durable resource result remains strict: " + durableError);
                    AssertTrue(durable.Result.Resources.Count > 0,
                        "durable resource result retains exact runtime evidence");
                }
                foreach (var request in calls)
                {
                    var admitted = ModelContextBudget.EstimateAdmittedRequestTokens(
                        request.Item1,
                        request.Item2,
                        settings,
                        ModelProtocolClient.EstimateFormatRepairOverheadTokens(settings),
                        ModelContextBudget.ContinuationReserveTokens(settings));
                    AssertTrue(admitted <= ModelContextBudget.InputBudgetTokens(settings),
                        "every VBA resource request retains repair and continuation reserves");
                }
            });
        }

        private static void SimpleAgentLoadsAndRunsArbitraryMacro()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.office_run_macro"),
                    "{\"message\":\"Запускаю выбранный макрос.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.office_run_macro\",\"arguments\":{\"macroName\":\"Module1.MigrateApiKey\",\"arguments\":[\"value\",2,true]}}]}",
                    "{\"message\":\"Макрос выполнен.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var requestOptions = new List<LlmRequestOptions>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    requestOptions.Add(options);
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteWithApprovalsAsync(
                    ChatModes.Agent, "Run Module1.MigrateApiKey with arguments.", session, NewContext(adapter),
                    new AppSettings
                    {
                        AgentResponseMode = AgentResponseModes.JsonSchema,
                        AutoConfirmToolActions = true,
                        MaxAgentIterations = 4
                    }, tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "schema load, macro execution, and final response");
                var initialCallable = ResponseSchemaCallNames(requestOptions[0].ResponseSchemaJson).ToArray();
                var admittedCallable = ResponseSchemaCallNames(requestOptions[1].ResponseSchemaJson).ToArray();
                AssertTrue(!initialCallable.Contains("common.office_run_macro", StringComparer.OrdinalIgnoreCase) &&
                    admittedCallable.Contains("common.office_run_macro", StringComparer.OrdinalIgnoreCase),
                    "arbitrary macro becomes callable only on the model step after exact schema admission");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.RunMacro), "macro executes once");
                AssertEqual("Module1.MigrateApiKey", adapter.RanMacros.Single(), "arbitrary exact macro name reaches the adapter");
                AssertEqual("[\"value\",2,true]", JsonConvert.SerializeObject(
                    ((VbaRunMacroRequest)adapter.SingleVbaCall(
                        FakeVbaOperation.RunMacro).Request).Arguments),
                    "public native arguments are serialized only at the hidden backend boundary");
                AssertContains(FlattenSimple(calls[1]), "\"kind\":\"tool-schema\"", "macro schema evidence reaches execution step");
                AssertRunViewState(result, session, "unknown", 0, 0, 1);
                AssertContains(result.AssistantText, "Макрос выполнен.", "macro result returns to the model");
                AssertContains(result.AssistantText, "состояние 1 операций записи осталось неизвестным",
                    "unknown macro effect remains explicit in the user-visible result");
            });
        }

        private static void SimpleAgentVbaEditsAfterCompaction()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                const string module = "ContextModule";
                var source = "Option Explicit\r\nSub Main()\r\nDebug.Print \"before\"\r\nEnd Sub\r\n" +
                    string.Join("\r\n", Enumerable.Range(0, 2200).Select(index => "' preserved comment " + index));
                AssertTrue(source.Length > 32000, "fixture crosses the internal resource cursor boundary");
                var after = source.Replace("\"before\"", "\"after\"");
                var session = NewSession(adapter);
                var phase = 0;
                var step = 0;
                Func<string, JObject, string> call = (name, arguments) => ModelProtocolWire.Write("VBA source", new[] {
                    new ConversationToolCall { Name = name, Arguments = arguments } });
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, token) =>
                {
                    step++;
                    string response;
                    if (step == 1)
                        response = LoadSkillResponse("common.vba_code_editing");
                    else if (phase == 0 && step == 2)
                        response = call(VbaToolCatalog.WriteModule, new JObject {
                            ["moduleName"] = module, ["code"] = source, ["mode"] = "createOnly" });
                    else if (phase == 0 && step == 3)
                    {
                        AssertEqual("ok", (string)LastToolResult(messages, VbaToolCatalog.WriteModule)["status"],
                            "creation has verified source before the next read");
                        response = call(ResourceToolCatalog.ReadToolId, new JObject {
                            ["target"] = "VBA module: " + module, ["representation"] = "source" });
                    }
                    else if (phase == 0 && step == 4)
                    {
                        var read = LastToolResult(messages, ResourceToolCatalog.ReadToolId);
                        AssertEqual("ok", (string)read["status"], "large source reads across internal exact pages");
                        AssertEqual(source, (string)read["data"]["text"], "whole source preserves exact CRLF bytes");
                        response = ModelProtocolWire.Write("Created and read.", new ConversationToolCall[0]);
                    }
                    else if (phase == 1 && step == 2)
                    {
                        AssertTrue(messages.Any(message => message.SyntheticResourceObservation &&
                            message.ResourceEvidence.Count > 0 && (message.Content ?? string.Empty).Contains(source)),
                            "compaction restores the actual complete source with current evidence");
                        response = call(VbaToolCatalog.WriteModule, new JObject {
                            ["moduleName"] = module, ["code"] = after, ["mode"] = "updateOnly" });
                    }
                    else if (phase == 1 && step == 3)
                    {
                        AssertEqual("ok", (string)LastToolResult(messages, VbaToolCatalog.WriteModule)["status"],
                            "restored source authorizes a whole-module edit without another read");
                        response = ModelProtocolWire.Write("Updated.", new ConversationToolCall[0]);
                    }
                    else if (phase == 2 && step == 2)
                    {
                        AssertTrue(!messages.Any(message => (message.Content ?? string.Empty).Contains(after)),
                            "large source is omitted from the constrained request");
                        response = call(VbaToolCatalog.WriteModule, new JObject { ["moduleName"] = module,
                            ["code"] = "Option Explicit\r\nSub Main()\r\nDebug.Print \"final\"\r\nEnd Sub", ["mode"] = "updateOnly" });
                    }
                    else if (phase == 2 && step == 3)
                    {
                        var rejected = LastToolResult(messages, VbaToolCatalog.WriteModule);
                        AssertEqual("error", (string)rejected["status"], "missing source blocks a destructive reconstruction");
                        AssertContains(rejected.ToString(), "vba_snapshot_refresh_required", "source guard explains the refusal");
                        AssertTrue(!rejected.ToString().Contains("preserved comment"),
                            "large-source recovery does not pin synthetic head/tail excerpts in the request");
                        AssertEqual(after, adapter.GetVbaModuleCode(module), "refusal preserves the full module");
                        response = call(ResourceToolCatalog.ReadToolId, new JObject { ["target"] = "VBA module: " + module,
                            ["representation"] = "source", ["startLine"] = 1, ["lineCount"] = 4 });
                    }
                    else if (phase == 2 && step == 4)
                    {
                        var read = LastToolResult(messages, ResourceToolCatalog.ReadToolId);
                        AssertEqual("ok", (string)read["status"], "bounded source recovery fits the constrained request");
                        AssertContains((string)read["data"]["text"], "\"after\"", "excerpt has the current exact patch anchor");
                        AssertEqual(false, (bool)read["data"]["complete"], "excerpt does not claim whole-module authority");
                        response = call(VbaToolCatalog.ApplyPatch, new JObject { ["moduleName"] = module,
                            ["patch"] = new JArray(new JObject { ["find"] = "\"after\"", ["text"] = "\"final\"" }) });
                    }
                    else if (phase == 2 && step == 5)
                    {
                        AssertEqual("ok", (string)LastToolResult(messages, VbaToolCatalog.ApplyPatch)["status"],
                            "exact patch works without full source in model context");
                        response = ModelProtocolWire.Write("Patched.", new ConversationToolCall[0]);
                    }
                    else throw new InvalidOperationException("Unexpected VBA context recovery step: " + phase + "/" + step);
                    return Task.FromResult(new LlmCompletionResult { Content = response });
                };
                var service = CreateConversationRunService(adapter, executor, completion);
                var settings = new AppSettings { AutoConfirmToolActions = true,
                    ContextWindowOverrideTokens = 131072, MaxTokens = 16384 };
                var tools = executor.GetControllerTools().ToList();
                var created = service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Create and inspect ContextModule.", session,
                    NewContext(adapter), settings, tools, null).GetAwaiter().GetResult();
                AssertEqual(4, step, "skill selection, creation and read complete without repairs: " + created.AssistantText);
                AssertEqual(source, adapter.GetVbaModuleCode(module), "creation preserves the complete source");

                Action compact = () => {
                    var through = session.Messages.Last();
                    var checkpoint = new ContextCheckpoint { ThroughMessageId = through.Id,
                        SummaryMarkdown = "ContextModule was saved; continue with the requested edit." };
                    checkpoint.Claims.Add(new StructuredContextClaim { ClaimId = "vba-context-summary", Kind = "interpretation",
                        Text = checkpoint.SummaryMarkdown, SourceRoles = new List<string> { "assistant" },
                        SourceMessageIds = new List<string> { through.Id } });
                    session.Artifacts.Add(new ChatArtifact { Id = checkpoint.Id, Kind = ChatArtifactKinds.Compaction,
                        MimeType = "application/json", InlineText = JsonConvert.SerializeObject(checkpoint, Formatting.None) });
                    session.ContextCheckpoints.Add(checkpoint);
                    session.ActiveContextCheckpointId = checkpoint.Id;
                };
                compact();
                phase = 1;
                step = 0;
                var continued = service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Change before to after in ContextModule.", session,
                    NewContext(adapter), settings, tools, null).GetAwaiter().GetResult();
                AssertEqual(3, step, "post-compaction edit needs only skill selection and no extra source read: " + continued.AssistantText);
                AssertEqual(after, adapter.GetVbaModuleCode(module), "edit preserves all unrelated source after compaction");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.CreateModule), "creation dispatches once");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "modification dispatches once");
                compact();
                phase = 2;
                step = 0;
                // Keep room for the production prompts and causal frames while
                // forcing the full module body out of the request.
                settings.ContextWindowOverrideTokens = 32768;
                settings.MaxTokens = 1024;
                continued = service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Change after to final in ContextModule using a focused edit.",
                    session, NewContext(adapter), settings, tools, null).GetAwaiter().GetResult();
                AssertEqual(5, step, "small-context refusal recovers through skill selection, one excerpt and one patch: " + continued.AssistantText);
                AssertEqual(after.Replace("\"after\"", "\"final\""), adapter.GetVbaModuleCode(module),
                    "small-context patch preserves all unrelated source");
                AssertEqual(2, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "bounded recovery writes exactly once");
            });
        }

        private static void SimpleAgentVbaRefreshReleasesRejectedWrite()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                const string before = "Option Explicit\nSub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string after = "Option Explicit\nSub Main()\nDebug.Print \"after\"\nEnd Sub";
                adapter.SetVbaModule("UniLoader", before, "StdModule");
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "vba-refresh",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow };
                Func<string, JObject, string> call = (name, arguments) => ModelProtocolWire.Write("", new[]
                { new ConversationToolCall { Name = name, Arguments = arguments } });
                var read = call(ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "VBA module: UniLoader", ["representation"] = "source" });
                var write = call("common.vba_write_module",
                    new JObject { ["moduleName"] = "UniLoader", ["code"] = after, ["mode"] = "updateOnly" });
                var responses = new Queue<string>(new[] { read, LoadSkillResponse("common.vba_code_editing"),
                    write, read, write, ModelProtocolWire.WriteAction("Изменение подтверждено.", ConversationResponse.DoneAction) });
                var requests = 0;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    requests++;
                    if (requests == 3) adapter.SetVbaModule("UniLoader", before.Replace("before", "external"), "StdModule");
                    if (requests == 4)
                    {
                        var failed = LastToolResult(messages.ToList(), "common.vba_write_module");
                        AssertEqual("error", (string)failed["status"], "external drift rejects the first whole write");
                        AssertEqual("RefreshRequired", (string)failed["data"]["recovery"]["retryPolicy"],
                            "small diagnostic source still requires a canonical read");
                        AssertEqual("whole", (string)failed["data"]["recovery"]["readScope"], "recovery names the actual guard requirement");
                        AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "stale write never dispatches");
                    }
                    AssertTrue(responses.Count > 0, "refresh must not create a repair loop");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var result = service.ExecuteAsync(ChatModes.Agent, "Замени вывод UniLoader на after.", session,
                    NewContext(adapter), new AppSettings { AutoConfirmToolActions = true },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                    .GetAwaiter().GetResult();
                AssertEqual(6, requests, "fresh read releases an explicit retry of the same arguments: " + result.AssistantText);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "recovery completes the run");
                AssertEqual(after, adapter.GetVbaModuleCode("UniLoader"), "the reconciled whole write is applied");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "only the refreshed write dispatches");
                AssertTrue(!session.Messages.Any(m => m.ExecutionProgress?.Outcome == ToolExecutionOutcome.NotDispatched),
                    "no stale failure-cache rejection follows the successful refresh");
            });
        }

        private static void SimpleAgentVbaSchemaInspectionDoesNotLoop(bool tightBudget)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                const string before = "Option Explicit\nSub Main()\nIf True Then\nDebug.Print 1\nEnd Sub";
                const string after = "Option Explicit\nSub Main()\nIf True Then\nDebug.Print 1\nEnd If\nEnd Sub";
                adapter.SetVbaModule("UniLoader", before, "StdModule");
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "vba-inspection",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey, StartedUtc = DateTime.UtcNow };
                Func<string, JObject, string> call = (name, arguments) => ModelProtocolWire.Write("", new[]
                { new ConversationToolCall { Name = name, Arguments = arguments } });
                var schemaRead = call(CapabilityToolCatalog.ReadToolId, new JObject { ["id"] = "common.vba_apply_patch" });
                var script = new List<string> {
                    call(ResourceToolCatalog.ReadToolId, new JObject {
                        ["target"] = VbaResourceProvider.ProjectSemanticTarget(adapter.DocumentTitle), ["representation"] = "structure" }),
                    call(ResourceToolCatalog.ReadToolId, new JObject { ["target"] = "VBA module: UniLoader", ["representation"] = "source" }),
                    schemaRead,
                    LoadSkillResponse("common.vba_code_editing") };
                if (!tightBudget) script.AddRange(new[] {
                    schemaRead,
                    call("common.vba_apply_patch", new JObject { ["moduleName"] = "UniLoader", ["patch"] = new JArray(
                        new JObject { ["find"] = "End Sub", ["text"] = "End If\nEnd Sub" }) }),
                    ModelProtocolWire.Write("Исходник исправлен; компиляция Office не проверялась.", new ConversationToolCall[0])
                });
                else script.Add(ModelProtocolWire.Write("Навык не подключён: контекст не вмещает полный запрос.", new ConversationToolCall[0]));
                var responses = new Queue<string>(script);
                var requests = 0;
                LlmCompletionDelegate complete = (settings, messages, options, stream, token) =>
                {
                    requests++;
                    AssertTrue(responses.Count > 0, "schema inspection must not trigger a repair loop");
                    var callable = session.LastContextReceipt.Arsenal.Tools.Select(tool => tool.Id).ToArray();
                    AssertEqual(!tightBudget && requests >= 5, callable.Contains("common.vba_apply_patch"),
                        "only skill selection exposes patch on the next request");
                    if (requests == 1)
                        AssertTrue(FlattenSimple(messages).Contains("Inspect the bound project before asking"),
                            "source discovery guidance is present before a skill is selected");
                    if (requests == 4 || requests == 6)
                    {
                        var read = LastToolResult(messages.ToList(), CapabilityToolCatalog.ReadToolId);
                        AssertEqual("ok", (string)read["status"], "schema inspection succeeds before and after selection");
                        AssertContains(read.ToString(), "common.vba_code_editing", "inspection identifies the real selecting skill");
                        AssertEqual("skill-tool", (string)read["data"]["kind"], "inspection returns bounded selection guidance");
                        AssertTrue(read["data"]["descriptor"] == null && read["data"]["loaded"] == null,
                            "discovery neither duplicates selected schemas nor claims admission");
                        AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "inspection grants no mutation");
                    }
                    if (tightBudget && requests == 5)
                    {
                        var load = LastToolResult(messages.ToList(), SkillLoadToolHandler.ToolId);
                        AssertEqual("error", (string)load["status"], "oversized selection is refused before activation");
                        AssertContains((string)load["message"], "previous selection remains active", "load preserves the usable surface");
                        AssertEqual(0, session.LastRun.SelectedSkills.Count, "candidate never becomes durable selection");
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var requestSettings = new AppSettings { AutoConfirmToolActions = true, ContextWindowOverrideTokens = 32768 };
                // The default 32K request now fits. Retain a deliberate full-request admission refusal
                // independently of how much prose the built-in prompts happen to contain.
                var userRequest = "Исправь незакрытый If в UniLoader." +
                    (tightBudget ? "\nRequired fixture context: " + new string('x', 32000) : "");
                var result = CreateConversationRunService(adapter, executor, complete).ExecuteAsync(
                    ChatModes.Agent, userRequest, session, NewContext(adapter),
                    requestSettings,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                    .GetAwaiter().GetResult();
                AssertEqual(tightBudget ? 5 : 7, requests, "discovery/load reaches a usable next request: " + result.AssistantText);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "patch run completes: " + result.AssistantText);
                AssertEqual(tightBudget ? before : after, adapter.GetVbaModuleCode("UniLoader"), "only admitted patch can change source");
                AssertEqual(tightBudget ? 0 : 1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "no invented dispatch or retry");
                AssertEqual(tightBudget ? 1 : 0, session.LastRun.KernelState.Summary.ToolCounts.ReadError, "only the actual admission refusal is counted");
                AssertEqual(tightBudget ? 0 : 1, session.LastRun.SelectedSkills.Count, "only admitted selections persist");
                if (tightBudget)
                {
                    session = JsonConvert.DeserializeObject<ChatSession>(JsonConvert.SerializeObject(session));
                    var resumedCalls = 0;
                    var resumed = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                    {
                        resumedCalls++;
                        return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.Write("Можно продолжать.", new ConversationToolCall[0]) });
                    }).ExecuteAsync(ChatModes.Agent, "Продолжим без этого навыка.", session, NewContext(adapter),
                        new AppSettings { ContextWindowOverrideTokens = 32768 },
                        OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                    AssertEqual(1, resumedCalls, "restored next user turn can reach the model");
                    AssertEqual(AgentResponseStatuses.Completed, resumed.ResponseStatus, "refused selection does not trap the chat");
                }
            });
        }

        private static void SimpleAgentVbaBatchReadCannotAuthorizeStaleOverwrite()
        {
            WithTempPaths(paths =>
            {
                const string before = "Option Explicit\nSub Main()\nEnd Sub";
                const string patched = "Option Explicit\nSub Main()\nDebug.Print \"started\"\nEnd Sub";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.VbaModuleCode = before;
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var session = NewSession(adapter);
                var responses = new Queue<string>(new[]
                {
                    LoadSkillResponse("common.vba_code_editing"),
                    new JObject
                    {
                        ["message"] = "Добавляю диагностику и обновляю заголовок.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(
                            new JObject { ["name"] = "common.vba_apply_patch", ["arguments"] = new JObject
                            {
                                ["moduleName"] = "Module1", ["patch"] = new JArray(new JObject
                                { ["find"] = "Sub Main()", ["text"] = "Sub Main()\nDebug.Print \"started\"" })
                            } },
                            new JObject { ["name"] = "common.resources_read", ["arguments"] = new JObject
                            { ["target"] = "VBA module: Module1", ["representation"] = "source" } },
                            new JObject { ["name"] = "common.vba_write_module", ["arguments"] = new JObject
                            { ["moduleName"] = "Module1", ["code"] = "' Version 2\n" + before, ["mode"] = "updateOnly" } })
                    }.ToString(Formatting.None),
                    new JObject
                    {
                        ["message"] = "Добавляю диагностику отдельным действием.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject { ["name"] = "common.vba_apply_patch", ["arguments"] = new JObject
                        {
                            ["moduleName"] = "Module1", ["patch"] = new JArray(new JObject
                            { ["find"] = "Sub Main()", ["text"] = "Sub Main()\nDebug.Print \"started\"" })
                        } })
                    }.ToString(Formatting.None),
                    new JObject
                    {
                        ["message"] = "Читаю текущий модуль после записи.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject { ["name"] = "common.resources_read", ["arguments"] = new JObject
                        { ["target"] = "VBA module: Module1", ["representation"] = "source" } })
                    }.ToString(Formatting.None),
                    new JObject
                    {
                        ["message"] = "Сохраняю заголовок вместе с прочитанной диагностикой.",
                        ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["name"] = "common.vba_write_module", ["arguments"] = new JObject
                            { ["moduleName"] = "Module1", ["code"] = "' Version 2\n" + patched, ["mode"] = "updateOnly" }
                        })
                    }.ToString(Formatting.None),
                    "{\"message\":\"Проверка завершена.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var modelRequests = 0;
                var sourceRequests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    modelRequests++;
                    sourceRequests.Add(messages.ToList());
                    if (modelRequests == 3)
                    {
                        AssertEqual(before, adapter.VbaModuleCode, "rejected batch performs no VBA mutation");
                        AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                            "protocol repair precedes every dispatch");
                    }
                    if (modelRequests == 4)
                    {
                        AssertEqual(patched, adapter.VbaModuleCode, "first singleton patch is applied before read");
                        AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "only the patch dispatched");
                        AssertEqual(1, journal.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                            "rejected batch creates no VBA preparation");
                    }
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Добавь Debug.Print, сохрани его при обновлении заголовка.",
                    session, NewContext(adapter), new AppSettings { AutoConfirmToolActions = true },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null)
                    .GetAwaiter().GetResult();
                AssertEqual(6, modelRequests, "both mutations reach their next model decision");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model completes after both writes");
                AssertEqual(patched,
                    (string)LastToolResult(sourceRequests[4], ResourceToolCatalog.ReadToolId)["data"]["text"],
                    "next model decision sees the changed VBA source through its exact read");
                AssertEqual("ok", (string)LastToolResult(sourceRequests.Last(), VbaToolCatalog.WriteModule)["status"],
                    "the write's superseded input observation cannot turn its verified result into an error");
                var writes = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Where(item => (string)item["toolId"] == "common.vba_write_module").ToList();
                AssertEqual(1, writes.Count, "rejected batch contributes no accepted write result");
                AssertEqual("ok", (string)writes[0]["status"], "fresh response can use delivered source evidence");
                AssertEqual("' Version 2\n" + patched, adapter.VbaModuleCode, "new write retains diagnostic code");
                AssertEqual(2, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "only patch and fresh write dispatch");
                var mutations = journal.ListMutations(adapter.HostName, adapter.DocumentKey);
                AssertEqual(2, mutations.Count, "two real writes have journal evidence");
                AssertTrue(mutations.All(item => item.Terminal.Status == VbaMutationStatuses.Committed),
                    "both applied writes have verified read-back and durable terminals");
            });
        }

        private static void SimpleAgentExecutesMultipleToolsSequentially()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Создаю первый лист.\",\"action\":\"tool\",\"tool_calls\":[" +
                    "{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"First\"}}]}",
                    "{\"message\":\"Первый лист создан; создаю второй.\",\"action\":\"tool\",\"tool_calls\":[" +
                    "{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Second\"}}]}",
                    "{\"message\":\"Оба листа созданы.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                IReadOnlyList<ChatMessage> beforeSecondWrite = null;
                IReadOnlyList<ChatMessage> finalRequest = null;
                var progressActivities = new List<ChatActivity>();
                var callCount = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    callCount += 1;
                    if (callCount == 3) beforeSecondWrite = messages.ToList();
                    if (callCount == 4) finalRequest = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Создай листы First и Second.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), (phase, message, activity) =>
                    {
                        if (activity != null) progressActivities.Add(activity);
                    }).GetAwaiter().GetResult();

                AssertEqual("Оба листа созданы.", result.AssistantText, "multi-tool final response");
                AssertTrue(adapter.HasSheet("First") && adapter.HasSheet("Second"), "both tools executed");
                AssertEqual(4, callCount, "schema read, two separate writes and final response");
                AssertEqual(2, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "each accepted write executes once");
                AssertEqual("excel.add_sheet", adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 2].ToolId, "first execution recorded");
                AssertEqual("First", Convert.ToString(adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 2].Arguments["name"]), "first call order");
                AssertEqual("Second", Convert.ToString(adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 1].Arguments["name"]), "second call order");
                AssertContains(FlattenSimple(beforeSecondWrite), "Added sheet: First",
                    "first result reaches the model before the second write is authored");
                var replay = FlattenSimple(finalRequest);
                AssertContains(replay, "Added sheet: First", "first write result reaches the final request");
                AssertContains(replay, "Added sheet: Second", "second write result reaches the final request");
                var activities = session.Messages
                    .Where(message => message != null && message.Activity != null && message.Activity.Kind == "tool" &&
                        string.Equals(message.Activity.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.Activity)
                    .ToList();
                AssertEqual(2, activities.Count, "two visible tool activities");
                var executedIds = activities.Select(activity => activity.ToolCallId).ToArray();
                AssertEqual(2, executedIds.Distinct().Count(), "separate writes receive different runtime IDs");
                AssertContains(replay, "Added sheet: First", "first committed mutation is folded into model history");
                AssertContains(replay, "Added sheet: Second", "second committed mutation is folded into model history");
                AssertTrue(!string.IsNullOrWhiteSpace(activities[0].StepId), "model step id stored");
                AssertTrue(activities[0].StepId != activities[1].StepId,
                    "each write belongs to its own accepted model step");
                AssertEqual("Создаю первый лист.", activities[0].StepMessage, "first step message is stored");
                var marker = progressActivities.First(activity => activity.Kind == "step" &&
                    string.Equals(activity.Title, "Создаю первый лист.", StringComparison.Ordinal));
                var running = progressActivities.First(activity => activity.Kind == "tool" && activity.Status == "running" &&
                    string.Equals(activity.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase));
                AssertEqual(marker.StepId, running.StepId, "live tool belongs to visible model step");
            });
        }

        private static void SimpleAgentConfirmationPreservesExecutionHealth(string initialHealth)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                if (initialHealth == "unknown")
                    adapter.ExcelSheetThrowAfterMutation = true;
                else
                    adapter.QueueExcelSheetApplyFailure(
                        "Write did not report success", "write_rejected", false);
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Добавляю лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    LoadSkillResponse("common.skill_authoring"),
                    "{\"message\":\"Сохраняю skill.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    ModelProtocolWire.WriteAction("Проверка листа недоступна; skill сохранён.",
                        initialHealth == "unknown" ? ConversationResponse.BlockedAction : ConversationResponse.DoneAction),
                    "{\"message\":\"Обычный новый ответ.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                    Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }));
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "initial", TurnId = "turn", Status = "running",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion };
                var settingsForRun = new AppSettings { AutoConfirmToolActions = false };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(ChatModes.Agent, "Создай лист и skill.", session, NewContext(adapter),
                    settingsForRun, tools, null, (pendingSession, command, result) => "pending").GetAwaiter().GetResult();
                AssertTrue(first.WaitingForConfirmation, "the first mutation also requires approval");
                first = service.ConfirmAsync("pending", PendingCommand(session), session,
                    new ConversationRunInput(settingsForRun, NewContext(adapter), tools), null,
                    (pendingSession, command, result) => "pending").GetAwaiter().GetResult();
                AssertTrue(first.WaitingForConfirmation, "real loop stops at confirmation");
                AssertEqual(initialHealth, first.RunViewState.ExecutionHealth, "pending cannot erase earlier execution evidence");
                AssertEqual(0, first.RunViewState.VerifiedWrites + first.RunViewState.UnverifiedWrites, "pending mutation is not a successful write");
                session.LastRun.RunId = "continuation";
                var confirmed = PendingCommand(session);
                var final = service.ConfirmAsync("pending", confirmed, session,
                    new ConversationRunInput(settingsForRun, NewContext(adapter), tools), null).GetAwaiter().GetResult();
                AssertRunViewState(final, session, initialHealth, 1,
                    initialHealth == "errors" ? 1 : 0, initialHealth == "unknown" ? 1 : 0);
                AssertEqual(RunViewLifecycles.Completed, final.RunViewState.Lifecycle, "completed lifecycle does not erase errors or unknown");
                var previousFinal = session.Messages.Last();
                var next = service.ExecuteAsync(ChatModes.Agent, "Ответь без действий.", session, NewContext(adapter),
                    settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertRunViewState(next, session, "clean", 0, 0, 0);
                AssertEqual(initialHealth, previousFinal.RunViewState.ExecutionHealth,
                    "new turn does not rewrite earlier evidence");
            });
        }

        private static void SimpleAgentConfirmationReplaysOnlyFinalResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadSkillResponse("common.skill_authoring"),
                    "{\"message\":\"Создаю skill.\",\"action\":\"tool\",\"tool_calls\":[" +
                    "{\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Skill сохранён.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = CreateConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { Status = "running", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion };
                var settings = new AppSettings { AutoConfirmToolActions = false, SystemPromptRole = "user" };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(
                    ChatModes.Agent,
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_1").GetAwaiter().GetResult();

                AssertContains(first.AssistantText, "Создаю", "waiting response returned");
                AssertEqual("clean", first.RunViewState.ExecutionHealth, "confirmation itself is not a tool error");
                AssertEqual(0, first.RunViewState.VerifiedWrites + first.RunViewState.UnverifiedWrites, "waiting is not an applied mutation");
                AssertTrue(!session.Messages.Any(message => message.ProtocolMessage &&
                    (message.Content ?? string.Empty).IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0),
                    "waiting result not replayed");
                var skillCallId = session.LastRun.KernelState.Summary.PendingConfirmation.Call.Id;
                AssertEqual(skillCallId, session.Messages.Last(message => message.Activity != null).Activity.ToolCallId,
                    "pending activity keeps tool call id");
                var pendingActivity = session.Messages.Last(message => message.Activity != null).Activity;
                var expectedCatalogFingerprint = ToolPackSnapshotFactory.ExecutionFingerprint(
                    ConversationRunService.PrepareToolsForRun(tools),
                    "common.skills_upsert");
                AssertEqual(expectedCatalogFingerprint, pendingActivity.ConfirmationCatalogSha256,
                    "pending activity persists executable tool fingerprint");
                var changedTools = tools.Select(tool => tool.Clone()).ToList();
                changedTools.First(tool => string.Equals(
                    tool.Id, "common.skills_upsert", StringComparison.OrdinalIgnoreCase)).ArgumentSchemaJson =
                        "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
                AssertTrue(!string.Equals(
                        expectedCatalogFingerprint,
                        ToolPackSnapshotFactory.ExecutionFingerprint(
                            ConversationRunService.PrepareToolsForRun(changedTools),
                            "common.skills_upsert"),
                        StringComparison.OrdinalIgnoreCase),
                    "tool fingerprint changes with a replaced executable definition");
                AssertEqual(2, session.LastRun.IterationsUsed, "confirmation stores iteration cursor after discovery");
                AssertEqual(2, session.LastRun.ToolStepsUsed, "schema read and pending action consume logical tool steps");
                var confirmedCommand = PendingCommand(session);
                var final = service.ConfirmAsync("pending_1", confirmedCommand, session,
                    new ConversationRunInput(settings, NewContext(adapter), tools), null).GetAwaiter().GetResult();

                AssertEqual("Skill сохранён.", final.AssistantText, "continued final response");
                AssertRunViewState(final, session, "clean", 1, 0, 0);
                AssertEqual(3, session.LastRun.IterationsUsed, "confirmation continuation keeps cumulative iteration budget");
                AssertEqual(2, session.LastRun.ToolStepsUsed, "confirmed result replaces reserved logical tool step");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "RUNTIME_CONTEXT", "user-role continuation keeps runtime context");
                AssertEqual(2, calls[2].Count(message =>
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                    !string.Equals(message.Role, "assistant", StringComparison.Ordinal)),
                    "schema evidence and completed mutation remain separate typed results");
                AssertEqual(1, calls[2].Count(message => message.ToolName == "common.skills_upsert" &&
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                    !string.Equals(message.Role, "assistant", StringComparison.Ordinal)),
                    "confirmed mutation is delivered once");
                AssertTrue(calls[2].Any(message => message.ToolName == "common.skills_upsert" &&
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion),
                    "confirmed result is retained as a typed tool result");
                AssertContains(replay, "common.skills_upsert", "folded confirmation keeps its tool identity");
                const string runtimeMarker = "RUNTIME_CONTEXT:\n";
                var runtimeMessage = calls[2].First(message => (message.Content ?? string.Empty)
                    .IndexOf(runtimeMarker, StringComparison.Ordinal) >= 0);
                var runtimeContext = JObject.Parse(runtimeMessage.Content.Substring(
                    runtimeMessage.Content.IndexOf(runtimeMarker, StringComparison.Ordinal) + runtimeMarker.Length));
                AssertTrue(((JArray)runtimeContext["capabilities"]["items"])
                        .OfType<JObject>().Any(item =>
                            (string)item["id"] == "common.skills_upsert" &&
                            (bool?)item["schemaLoaded"] == true),
                    "selected skill keeps its unchanged authoring schema after publication");
                AssertTrue(((JArray)runtimeContext["capabilities"]["items"])
                        .OfType<JObject>().Any(item =>
                            (string)item["id"] == "common.test" && (string)item["kind"] == "skill"),
                    "confirmed creation appears in the next exact catalog");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "no stale waiting result");
                var replayMessages = calls[2].ToList();
                var userIndex = replayMessages.FindIndex(message => message.Role == "user" && !message.ProtocolMessage &&
                    (message.Content ?? string.Empty).Contains("Create a test skill."));
                var resultIndex = replayMessages.FindIndex(message => message.ToolName == "common.skills_upsert" &&
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                    !string.Equals(message.Role, "assistant", StringComparison.Ordinal));
                AssertTrue(userIndex >= 0 && userIndex < resultIndex,
                    "user request precedes the completed tool result in replay");
            });
        }

        private static void SimpleAgentVbaConfirmationResumesPreparedState(bool externalDrift)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode =
                    "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var patch = new JArray(new JObject { ["find"] = "\"old\"", ["text"] = "\"new\"" });
                var expectedSource = "Sub Main()\nDebug.Print \"new\"\nEnd Sub";
                if (!externalDrift)
                {
                    adapter.VbaModuleCode = "Sub Main()\n" + string.Join("\n", Enumerable.Range(0, 100)
                        .Select(index => "Debug.Print \"old_" + index + "\"")) + "\nEnd Sub";
                    expectedSource = adapter.VbaModuleCode.Replace("old_", "new_");
                    patch = new JArray(Enumerable.Range(0, 100).Select(index => new JObject {
                        ["find"] = "\"old_" + index + "\"", ["text"] = "\"new_" + index + "\"" }));
                }
                var responses = new Queue<string>(new[]
                {
                    LoadSkillResponse("common.vba_code_editing"),
                    new JObject { ["message"] = "Обновляю VBA.", ["action"] = "tool",
                        ["tool_calls"] = new JArray(new JObject { ["name"] = "common.vba_apply_patch",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["patch"] = patch } }) }.ToString(),
                    externalDrift
                        ? "{\"message\":\"Изменение отклонено как устаревшее.\",\"action\":\"done\",\"tool_calls\":[]}"
                        : "{\"message\":\"Изменение сохранено.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion =
                    (completionSettings, messages, options, stream, token) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult
                        { Content = responses.Dequeue() });
                };
                var service = CreateConversationRunService(
                    adapter, executor, completion);
                var session = NewSession(adapter);
                var settings = new AppSettings
                    { AutoConfirmToolActions = false, ContextWindowOverrideTokens = 131072, MaxTokens = 16384 };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(
                    ChatModes.Agent,
                    "Замени old на new в Module1.",
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, command, result) =>
                        "pending-vba-native").GetAwaiter().GetResult();

                AssertTrue(first.WaitingForConfirmation,
                    "native VBA pauses after prepared live-state validation");
                var pending = session.LastRun.KernelState.Summary
                    .PendingConfirmation;
                AssertTrue(!string.IsNullOrWhiteSpace(
                        pending.PreparedStateJson),
                    "kernel owns the durable prepared VBA state");
                var activity = session.Messages.Last(message =>
                    message.Activity != null &&
                    message.Activity.ToolCallId == pending.Call.Id).Activity;
                var preview = activity.ResultPayload == null ? activity.DataJson :
                    executor.Payloads.ReadText(activity.ResultPayload.ToBlobReference());
                AssertContains(preview, "operations",
                    "confirmation activity carries the prepared preview");
                AssertTrue(!string.IsNullOrWhiteSpace(
                        activity.ConfirmationCatalogSha256),
                    "native pending activity carries the exact catalog fingerprint");
                var store = new ChatStore(FixturePaths.Value);
                var loaded = store.Load(session.Id);
                AssertTrue(!string.IsNullOrWhiteSpace(loaded.LastRun.KernelState
                        .Summary.PendingConfirmation.PreparedStateJson),
                    "prepared state survives canonical event replay");
                var confirmedCommand = PendingCommand(loaded);
                AssertTrue(string.IsNullOrWhiteSpace(
                        confirmedCommand.RuntimeGuardJson),
                    "confirmation command has no compatibility guard field");

                if (externalDrift) adapter.VbaModuleCode =
                    "Sub Main()\nDebug.Print \"external\"\nEnd Sub";
                var final = service.ConfirmAsync(
                    "pending-vba-native",
                    confirmedCommand,
                    loaded,
                    new ConversationRunInput(
                        settings, NewContext(adapter), tools),
                    null).GetAwaiter().GetResult();

                if (!externalDrift)
                {
                    AssertEqual(3, calls.Count, "confirmation reaches exactly one next model decision");
                    AssertEqual(AgentResponseStatuses.Completed, final.ResponseStatus, "confirmed mutation completes");
                    var terminal = loaded.Messages.Last(message => message.ToolName == VbaToolCatalog.ApplyPatch &&
                        message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion && message.Role != "assistant");
                    ToolResultWireReadResult terminalWire;
                    string terminalError;
                    AssertTrue(ToolResultHistoryReader.TryRead(terminal, out terminalWire, out terminalError), "confirmed patch result is readable");
                    AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, terminalWire.Result.Status,
                        "confirmed patch outcome: " + terminalWire.Result.Message);
                    AssertRunViewState(final, loaded, "clean", 1, 0, 0);
                    AssertEqual(expectedSource, adapter.VbaModuleCode, "confirmed large patch saves the intended module");
                    var afterSource = calls.Last().SingleOrDefault(message => message.SyntheticResourceObservation);
                    AssertTrue(afterSource != null, "the first request after confirmation has an after-source observation");
                    AssertContains(afterSource.Content, expectedSource,
                        "the first request after confirmation contains complete changed source");
                    AssertContains(afterSource.Content, "VBA module: Module1", "archival keeps the source associated with its target");
                    AssertTrue(loaded.Messages.Any(message => message.ToolName == VbaToolCatalog.ApplyPatch &&
                        message.ResultPayload != null), "large patch result exercises CAS archival");
                    var frame = calls.Last().SingleOrDefault(message =>
                        message.ToolCallId == terminal.ToolCallId &&
                        message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                        !string.Equals(message.Role, "assistant", StringComparison.Ordinal));
                    AssertTrue(frame != null && frame.Content.Contains("\"status\":\"ok\""),
                        "the first request after confirmation contains the verified terminal result");
                    AssertContains(frame.Content, "Module1", "terminal effect remains associated with its module");
                    var apiMessages = new LlmMessageBuilder().Build(calls.Last(), settings).Messages
                        .Select(item => JObject.FromObject(item)).ToList();
                    AssertTrue(apiMessages.Any(item => (string)item["content"] == afterSource.Content) &&
                        apiMessages.Any(item => (string)item["content"] == frame.Content),
                        "provider message serialization preserves the complete after-source and terminal result");
                    AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "confirmed VBA dispatches once");
                    return;
                }
                AssertContains(final.AssistantText,
                    "Изменение отклонено как устаревшее.",
                    "model receives the terminal stale result after confirmation");
                AssertContains(final.AssistantText,
                    "1 операций записи завершились ошибкой",
                    "failed confirmed write remains explicit in the user-visible result");
                AssertContains(FlattenSimple(calls.Last()),
                    "The VBA module changed after the source was inspected",
                    "confirmed execution exposes the guarded stale-state outcome without runtime internals");
                AssertContains(adapter.VbaModuleCode, "external",
                    "stale confirmed call does not overwrite live VBA");
                AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "stale guard blocks the backend dispatch boundary");
            });
        }

        private static void SimpleAgentConfirmationFailureContinues()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Очищаю ячейку.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.clear_range\",\"arguments\":{\"address\":\"A1\",\"clearWhat\":\"values\"}}]}",
                    "{\"message\":\"Определение инструмента изменилось; действие не выполнено.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = CreateConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { Status = "running", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion };
                var settings = new AppSettings { AutoConfirmToolActions = false };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                tools.Single(tool => tool.Id == "excel.clear_range").Scope = "document";
                service.ExecuteAsync(
                    ChatModes.Agent,
                    "Очисти A1.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_failure").GetAwaiter().GetResult();

                var command = PendingCommand(session);
                var callsBeforeConfirmation = adapter.TotalBackendCallCount;
                var changed = tools.Select(tool => tool.Clone()).ToList();
                changed.Single(tool => tool.Id == "excel.clear_range").Description += " revised";
                var final = service.ConfirmAsync("pending_failure", command, session,
                    new ConversationRunInput(settings, NewContext(adapter), changed), null).GetAwaiter().GetResult();

                AssertContains(final.AssistantText,
                    "Определение инструмента изменилось; действие не выполнено.",
                    "agent continues after confirmed policy drift");
                AssertEqual(2, calls.Count, "confirmed policy drift triggers the next model turn");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "Tool policy changed; request a new call.",
                    "fingerprint failure is replayed without dispatch");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "waiting result is not replayed after failure");
                AssertEqual(callsBeforeConfirmation, adapter.TotalBackendCallCount,
                    "changed policy blocks confirmed dispatch");
            });
        }

        private static void NativeReadBatchKeepsPairedReplay()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var calls = 0;
                string[] acceptedIds = null;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    calls++;
                    if (calls == 1) return Task.FromResult(new LlmCompletionResult { Content =
                        "{\"message\":\"Read twice\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_find\",\"arguments\":{}},{\"name\":\"common.resources_find\",\"arguments\":{}}]}" });
                    if (calls == 3) return Task.FromResult(new LlmCompletionResult { Content =
                        "{\"message\":\"Done\",\"action\":\"done\",\"tool_calls\":[]}" });
                    var accepted = messages.Where(message => message.Role == "assistant" &&
                        message.ToolName == ResourceToolCatalog.FindToolId && message.AcceptedCallOrigin != null).ToList();
                    AssertEqual(2, accepted.Count, "both identical read positions remain in history");
                    var ids = accepted.Select(message => message.ToolCallId).ToArray();
                    AssertEqual(2, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "runtime allocates distinct IDs");
                    if (acceptedIds == null) acceptedIds = ids;
                    AssertEqual(string.Join(",", acceptedIds), string.Join(",", ids), "reload never reallocates accepted IDs");
                    for (var index = 0; index < accepted.Count; index++)
                    {
                        AssertEqual(ids[index], accepted[index].ToolCalls.Single().Id, "native history uses runtime ID");
                        AssertEqual(index, accepted[index].AcceptedCallOrigin.CallIndex, "batch index identifies raw position");
                        AssertEqual(accepted[0].AcceptedCallOrigin.ModelAttemptId, accepted[index].AcceptedCallOrigin.ModelAttemptId,
                            "both calls originate in one model attempt");
                    }
                    var exchange = messages.Where(message => message.ProtocolMessage && ids.Contains(message.ToolCallId)).ToList();
                    AssertEqual(string.Join(",", ids.SelectMany(id => new[] { "assistant:" + id, "tool:" + id })),
                        string.Join(",", exchange.Select(message => message.Role + ":" + message.ToolCallId)),
                        "native tool calls stay paired in both live and reloaded request history");
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"message\":\"Done\",\"action\":\"done\",\"tool_calls\":[]}" });
                });
                var session = NewSession(adapter);
                var settingsForRun = new AppSettings { ToolResultRole = ToolResultRoles.Tool };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(ChatModes.Agent, "Read twice", session, NewContext(adapter), settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertEqual(2, first.RunViewState.SuccessfulReads, "both independent reads execute once");
                session = AssertKernelReplay(session);
                AssertEqual(2, session.Messages.Count(message => message.ToolName == ResourceToolCatalog.FindToolId &&
                    message.AcceptedCallOrigin != null), "both accepted calls survive durable replay");
                var next = service.ExecuteAsync(ChatModes.Agent, "Summarize", session, NewContext(adapter), settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Completed, next.RunViewState.Lifecycle,
                    "next turn can replay the persisted batch: " + next.RunViewState.Reason + " / " + next.AssistantText);
                AssertEqual(3, calls, "one next-turn model request");
            });
        }

        private static void RuntimeIdsPreserveCompleteHtml(string resultRole)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var html = "<!doctype html>\r\n<html lang=\"ru\"><body>\n" +
                    string.Concat(Enumerable.Range(0, 240).Select(index =>
                        "<p data-row=\"" + index + "\">Точный текст &amp; символы: \\n \\t \\\\</p>\r\n")) +
                    "<script>const stamp = '2026-08-28T12:34:56.000Z'; const path = 'C:\\\\reports';</script>\n" +
                    "<footer>END_OF_COMPLETE_HTML</footer></body></html>";
                var rawWrite = ModelProtocolWire.Write("Save complete HTML.", new[]
                {
                    new ConversationToolCall
                    {
                        Name = HtmlWorkspaceToolCatalog.WriteFileToolId,
                        Arguments = new JObject
                        {
                            ["path"] = "report.html", ["content"] = html
                        }
                    }
                });
                var responses = new Queue<string>(new[]
                {
                    LoadSkillResponse("common.html_workspace_authoring"), rawWrite, rawWrite,
                    ModelProtocolWire.Write("Done.", new ConversationToolCall[0])
                });
                var requestCount = 0;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    requestCount++;
                    AssertTrue(!messages.Any(message => (message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal)),
                        "execution identity never triggers regeneration of HTML");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var session = NewSession(adapter);
                var result = service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Save complete HTML.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentFormatRetries = 1,
                        ToolResultRole = resultRole, ContextWindowOverrideTokens = 131072 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "valid calls complete without repair");
                AssertEqual(4, requestCount, "two independently accepted writes require no extra model attempt");
                var accepted = session.Messages.Where(message => message.Role == "assistant" &&
                    message.ToolName == HtmlWorkspaceToolCatalog.WriteFileToolId && message.AcceptedCallOrigin != null).ToList();
                AssertEqual(2, accepted.Count, "identical payloads are not deduplicated or rejected as ID collisions");
                AssertEqual(2, accepted.Select(message => message.ToolCallId).Distinct().Count(), "repeated writes have distinct runtime IDs");
                foreach (var message in accepted)
                {
                    AssertTrue(AcceptedCallPayloadService.IsExternalizedCall(message),
                        "large HTML arguments use the reference-first accepted-call contract");
                    var parsed = ConversationResponseHistoryReader.Read(
                        AcceptedCallPayloadService.Hydrate(message, executor.Payloads));
                    AssertTrue(parsed.Success, "accepted HTML history is valid");
                    AssertEqual(html, (string)JObject.Parse(parsed.Response.ToolCalls.Single().ArgumentsJson)["content"],
                        "ID assignment preserves every HTML character in history");
                }
                AssertTrue(session.HtmlWorkspace.Files.Any(file => file.Path == "report.html"), "live HTML file exists");
                AssertEqual(html, session.HtmlWorkspace.Files.Single(file => file.Path == "report.html").Content,
                    "executor receives the complete original HTML");
                var replayed = AssertKernelReplay(session);
                AssertTrue(replayed.HtmlWorkspace.Files.Any(file => file.Path == "report.html"), "replayed HTML file exists: " + JsonConvert.SerializeObject(replayed.HtmlWorkspaceRecovery));
                AssertEqual(html, replayed.HtmlWorkspace.Files.Single(file => file.Path == "report.html").Content,
                    "durable replay retains the full HTML and its accepted IDs");
            });
        }

        private static void ModelRefusalIsTerminalInAgentAndPlan()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = calls == 1 ? string.Empty : ModelProtocolWire.Write("Must not execute", new[]
                        {
                            new ConversationToolCall { Name = ResourceToolCatalog.FindToolId }
                        }),
                        RefusalContent = "Запрос отклонён провайдером."
                    });
                };

                var agentSession = NewSession(adapter);
                var agent = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Restricted request.", agentSession, NewContext(adapter), new AppSettings(),
                    new ToolCatalogEntry[0], (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", agent.AssistantText, "agent refusal text");
                AssertEqual(RunViewLifecycles.Failed, agent.RunViewState.Lifecycle, "native refusal is locally classified by kernel");
                AssertKernelReplay(agentSession);
                AssertEqual(AgentResponseStatuses.Refused, agent.ResponseStatus,
                    "provider refusal maps from explicit transport metadata");
                AssertEqual(AgentResponseProtocol.CurrentVersion, agentSession.Messages.Last().ResponseProtocolVersion,
                    "provider refusal stores the current response protocol version");
                AssertEqual(1, calls, "agent refusal does not enter format repair");

                var chatSession = NewSession(adapter);
                chatSession.Mode = ChatModes.Plan;
                var chat = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Plan,
                    "Restricted request.", chatSession, NewContext(adapter), new AppSettings(),
                    executor.GetControllerTools().ToList(), (Action<string, string, ChatActivity>)null)
                    .GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", chat.AssistantText, "chat refusal text");
                AssertEqual(RunViewLifecycles.Failed, chat.RunViewState.Lifecycle, "Chat uses the same runtime failure classification");
                AssertKernelReplay(chatSession);
                AssertEqual(AgentResponseStatuses.Refused, chat.ResponseStatus,
                    "Chat provider refusal uses the same explicit terminal status");
                AssertEqual(2, calls, "chat refusal does not enter format repair");
                AssertEqual(0, chat.ToolResults.Count, "native refusal prevents tools even if provider also sent JSON content");

                var emptyCalls = 0;
                var emptyService = CreateConversationRunService(adapter, executor,
                    (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    emptyCalls += 1;
                    return Task.FromResult(new LlmCompletionResult { Content = string.Empty });
                });
                var emptySession = NewSession(adapter);
                emptySession.Mode = ChatModes.Plan;
                var empty = emptyService.ExecuteAsync(
                    ChatModes.Plan,
                    "Empty response.", emptySession, NewContext(adapter),
                    new AppSettings { MaxAgentFormatRetries = 1 }, executor.GetControllerTools().ToList(), null)
                    .GetAwaiter().GetResult();
                AssertContains(empty.AssistantText, "Ответ модели не выполнен", "chat reports bounded structured-response failure");
                AssertEqual(1, emptyCalls, "Chat limit includes the initial empty response");
            });
        }

        private static void UnifiedOfficeRequiresExactApproval()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var responses = new Queue<string>(new[] {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Add sheet\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"ApprovedSheet\"}}]}",
                    "{\"message\":\"Add another sheet\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"SecondApprovedSheet\"}}]}",
                    ModelProtocolWire.WriteAction("Done", ConversationResponse.DoneAction)
                });
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                    Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }));
                var session = NewSession(adapter);
                var configuration = new AppSettings { AutoConfirmToolActions = false };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var result = service.ExecuteAsync(ChatModes.Agent, "Create sheet", session, NewContext(adapter),
                    configuration, tools, null, (pendingSession, command, registration) => "approval").GetAwaiter().GetResult();
                AssertTrue(result.WaitingForConfirmation && !adapter.HasSheet("ApprovedSheet"), "default Ask cannot dispatch");
                AssertEqual(2, responses.Count, "waiting does not call the model");
                configuration.AutoConfirmToolActions = true;
                session = new ChatStore(FixturePaths.Value).Load(session.Id);
                AssertTrue(!session.LastRun.ConversationPolicy.AutoConfirmToolActions && !adapter.HasSheet("ApprovedSheet"), "setting change and restart cannot approve pending work");
                var commandToConfirm = PendingCommand(session);
                session.MutationPolicy = MutationPolicy.Deny;
                RuntimeThrows<InvalidOperationException>(() => service.ConfirmAsync("approval", commandToConfirm, session,
                    new ConversationRunInput(configuration, NewContext(adapter), tools), null).GetAwaiter().GetResult());
                AssertTrue(!adapter.HasSheet("ApprovedSheet"), "changed permission cannot approve");
                session.MutationPolicy = MutationPolicy.Ask;
                var final = service.ConfirmAsync("approval", commandToConfirm, session,
                    new ConversationRunInput(configuration, NewContext(adapter), tools), null,
                    (pendingSession, command, registration) => "second-approval").GetAwaiter().GetResult();
                AssertTrue(adapter.HasSheet("ApprovedSheet") && final.WaitingForConfirmation && !adapter.HasSheet("SecondApprovedSheet"),
                    "the resumed run still requires approval of each subsequent call");
                final = service.ConfirmAsync("second-approval", PendingCommand(session), session,
                    new ConversationRunInput(configuration, NewContext(adapter), tools), null).GetAwaiter().GetResult();
                AssertTrue(adapter.HasSheet("SecondApprovedSheet") && !final.WaitingForConfirmation, "each exact approval dispatches once");
                AssertEqual(2, adapter.ExcelSheetRequests.Count(request => request.ToolId == "excel.add_sheet"), "two approved mutations");
            });
        }

        private static void AutoConfirmationNativeMultistepAndUnknown()
        {
            foreach (var unknown in new[] { false, true })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.ExcelSheetThrowAfterMutation = unknown;
                Func<string, string> add = name => new JObject { ["message"] = "Add sheet", ["action"] = "tool",
                    ["tool_calls"] = new JArray(new JObject { ["name"] = "excel.add_sheet", ["arguments"] = new JObject { ["name"] = name } })
                }.ToString(Formatting.None);
                var responses = new Queue<string>(new[] { LoadToolSchemaResponse("excel.add_sheet"),
                    add("AutoOne"), add(unknown ? "AutoOne" : "AutoTwo") });
                if (unknown) responses.Enqueue("{\"message\":\"Проверяю созданный лист.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"Excel range: AutoOne!A1\",\"representation\":\"text\"}}]}");
                responses.Enqueue(ModelProtocolWire.WriteAction("Done", ConversationResponse.DoneAction));
                var configuration = new AppSettings { AutoConfirmToolActions = true };
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    configuration.AutoConfirmToolActions = false;
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var result = service.ExecuteAsync(ChatModes.Agent, "Create sheets", session, NewContext(adapter),
                    configuration, tools, null).GetAwaiter().GetResult();
                AssertTrue(!result.WaitingForConfirmation && responses.Count == 0, "captured auto-confirm completes multiple steps without registrar");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "inspection permits completion without replay");
                AssertEqual(unknown ? 1 : 2, adapter.ExcelSheetRequests.Count(request => request.ToolId == "excel.add_sheet"), "unknown effect is never redispatched");
                AssertEqual(unknown ? RunViewHealth.Unknown : RunViewHealth.Clean, result.RunViewState.ExecutionHealth, "auto-approval does not certify effects");
                var loaded = new ChatStore(FixturePaths.Value).Load(session.Id);
                AssertTrue(loaded.LastRun.ConversationPolicy.AutoConfirmToolActions, "durable run keeps opt-in after settings changed");
                responses.Enqueue(add("NextRun"));
                var next = service.ExecuteAsync(ChatModes.Agent, "Create next sheet", session, NewContext(adapter),
                    configuration, tools, null, (pendingSession, command, registration) => "next-approval").GetAwaiter().GetResult();
                AssertTrue(next.WaitingForConfirmation && !adapter.HasSheet("NextRun"), "the next run returns to Ask after disabling");
            });
        }

        private static void RetiredChatCannotDispatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var calls = 0;
                var session = NewSession(adapter);
                session.FormatVersion = 6;
                session.Mode = ChatModes.Chat;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) => {
                    calls++;
                    return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAction("Unexpected", ConversationResponse.DoneAction) });
                });
                var count = session.Messages.Count;
                RuntimeThrows<InvalidOperationException>(() => service.ExecuteAsync(ChatModes.Chat, "Continue", session,
                    NewContext(adapter), new AppSettings(), executor.GetControllerTools().ToList(), null).GetAwaiter().GetResult());
                AssertEqual(0, calls, "retired history never reaches the model");
                AssertEqual(count, session.Messages.Count, "refusal does not append user input");
            });
        }

        private static void ChatRereadsReferencedArtifactOnDemand()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Agent;
                session.MutationPolicy = MutationPolicy.Deny;
                var artifact = new ChatArtifact
                {
                    Id = "reference_note",
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Reference note",
                    MimeType = "text/markdown",
                    Revision = 1,
                    InlineText = "RESOURCE_REPLAY_SENTINEL"
                };
                session.Artifacts.Add(artifact);
                var uri = ArtifactUri(session, artifact);
                var target = executor.ResourceGateway.Find(
                    session, "Reference note", "conversation").Items.Single().Target;
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю заметку.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"" + target + "\",\"representation\":\"text\"}}]}",
                    "{\"message\":\"Первый ответ.\",\"action\":\"done\",\"tool_calls\":[]}",
                    "{\"message\":\"Перечитываю заметку.\",\"action\":\"tool\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"" + target + "\",\"representation\":\"text\"}}]}",
                    "{\"message\":\"Второй ответ.\",\"action\":\"done\",\"tool_calls\":[]}"
                });
                var captured = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    captured.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var service = CreateConversationRunService(adapter, executor, completion);

                var first = service.ExecuteAsync(
                    ChatModes.Agent, "Что в заметке?", session, NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();
                foreach (var message in session.Messages.Where(message => message != null && message.ProtocolMessage))
                {
                    message.ExcludeFromModelContext = true;
                }
                var second = service.ExecuteAsync(
                    ChatModes.Agent, "Проверь ту же заметку ещё раз.", session, NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual("Первый ответ.", first.AssistantText, "first artifact answer");
                AssertEqual("Второй ответ.", second.AssistantText, "second artifact answer");
                AssertContains(FlattenSimple(captured[0]), target, "first request keeps the semantic target");
                AssertTrue(FlattenSimple(captured[0]).IndexOf(uri, StringComparison.Ordinal) < 0,
                    "resource index does not teach the model an exact URI argument");
                AssertTrue(FlattenSimple(captured[0]).IndexOf("RESOURCE_REPLAY_SENTINEL", StringComparison.Ordinal) < 0,
                    "artifact body is absent before an explicit read");
                AssertContains(FlattenSimple(captured[1]), "RESOURCE_REPLAY_SENTINEL", "first read returns the body");
                AssertContains(FlattenSimple(captured[2]), target, "later request still knows the semantic target");
                AssertTrue(FlattenSimple(captured[2]).IndexOf("RESOURCE_REPLAY_SENTINEL", StringComparison.Ordinal) < 0,
                    "later request retains the reference after prior read evidence leaves context");
                AssertContains(FlattenSimple(captured[3]), "RESOURCE_REPLAY_SENTINEL", "later turn can read the body again");
            });
        }

        private static LlmCompletionResult CompactionReply(IEnumerable<ChatMessage> messages, string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(FlattenSimple(messages), "\"sourceId\":\"([^\"]+)\"");
            AssertTrue(match.Success, "compaction supplies runtime source provenance");
            return new LlmCompletionResult { Content = new JObject { ["claims"] = new JArray(new JObject {
                ["kind"] = "interpretation", ["text"] = text, ["sourceIds"] = new JArray(match.Groups[1].Value) }) }.ToString() };
        }

        private static void CompactionPreservesClaimKindsAndRoles()
        {
            var responseMode = AgentResponseModes.JsonSchema;
            var scope = new ResourceAuthorityScopeId("document", "claim-fixture");
            var reference = new ResourceRef("rna://vba/claim-fixture/module", "r1");
            var sourceAuthority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(reference, 1) }) }),
                "tools", new SkillCatalogSnapshot(null), new SchemaRegistrySnapshot(null), 0);
            Func<ChatSession> fresh = () =>
            {
                var session = new ChatSession();
                for (var i = 0; i < 8; i++) session.Messages.Add(new ChatMessage {
                    Role = i % 2 == 0 ? "user" : "assistant", Content = i % 2 == 0 ? "Keep original formatting." : "I propose a simplified layout." });
                return session;
            };
            Func<ChatSession, string, string, ContextCheckpoint> compact = (session, kind, selector) =>
            {
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    var sources = messages.SelectMany(entry => (entry.Content ?? "").Split('\n'))
                        .Where(line => line.StartsWith("{\"sourceId\":", StringComparison.Ordinal)).Select(JObject.Parse).ToList();
                    var source = sources.FirstOrDefault(item => (string)item["role"] == selector || (string)item["kind"] == selector);
                    AssertTrue(source != null, "compaction includes selected source " + selector + ": " + string.Join(",", sources.Select(item => (string)item["role"] ?? (string)item["kind"])));
                    var draft = new JObject { ["claims"] = new JArray(new JObject {
                        ["kind"] = kind, ["text"] = "Preserve the layout constraint.", ["sourceIds"] = new JArray(source["sourceId"]) }) };
                    var strict = responseMode == AgentResponseModes.JsonSchema;
                    AssertEqual(strict ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject, options.ResponseFormat,
                        "compaction honors the selected response mode");
                    var schema = JObject.Parse(messages.Last().Content.Split('\n')[1]);
                    var wire = LlmClient.BuildRequestBody(settings, new LlmMessageBuilder().Build(messages, settings, options).Messages, 100, options);
                    AssertTrue(strict ? JToken.DeepEquals(schema, wire.SelectToken("response_format.json_schema.schema"))
                        : wire.SelectToken("response_format.json_schema") == null,
                        "actual compaction wire uses the exact source-bound schema only in strict mode");
                    string error;
                    var allowed = selector == "user" && (kind == "constraint" || kind == "decision" || kind == "interpretation") ||
                        selector == "tool" && kind == "observation";
                    AssertEqual(allowed, ToolSchemaSupport.ValidateArguments((JObject)draft.DeepClone(), schema, false, out error),
                        "schema enforces the same source promotion boundary as the parser: " + error);
                    var invalid = (JObject)draft.DeepClone();
                    invalid["claims"][0]["sourceIds"] = new JArray("not-an-included-source");
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(invalid, schema, false, out error), "unknown source cannot be generated");
                    if (selector == "tool")
                    {
                        var userSource = sources.First(item => (string)item["role"] == "user");
                        invalid["claims"][0]["kind"] = "decision";
                        invalid["claims"][0]["sourceIds"] = new JArray(userSource["sourceId"], source["sourceId"]);
                        AssertTrue(!ToolSchemaSupport.ValidateArguments(invalid, schema, false, out error),
                            "mixed user/tool provenance cannot promote an observation to a decision");
                    }
                    AssertTrue(ModelContextBudget.EstimateMessagesTokens(messages, settings) +
                        ModelContextBudget.EstimateRequestOptionsTokens(options, settings) <= ModelContextBudget.InputBudgetTokens(settings),
                        "compaction admission includes strict response-schema cost");
                    return Task.FromResult(new LlmCompletionResult { Content = draft.ToString() });
                };
                return new ContextCompactionService(completion).EnsureWithinBudgetAsync(session, new AppSettings { AgentResponseMode = responseMode }, null, true, null, CancellationToken.None,
                    authoritySnapshot: sourceAuthority).GetAwaiter().GetResult();
            };
            var user = fresh();
            var saved = compact(user, "constraint", "user");
            AssertEqual("constraint", saved.Claims.Single().Kind, "user constraint retains its type");
            AssertEqual("user", saved.Claims.Single().SourceRoles.Single(), "source roles come from runtime transcript");
            var toolSession = fresh();
            var invocation = new ToolInvocation { ToolCallId = "claim-call", ToolId = "test.claim_source" };
            var toolCall = new ChatMessage { Role = "assistant", ProtocolMessage = true,
                RunId = "claim-run",
                ToolCallId = invocation.ToolCallId, ToolName = invocation.ToolId,
                ToolCalls = new List<LlmToolCall> { new LlmToolCall { Id = invocation.ToolCallId, Name = invocation.ToolId, Type = "function", ArgumentsJson = "{}" } } };
            var toolResult = AgentJsonProtocol.CreateToolResultMessage(invocation,
                new ToolResultMaterialization(RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Observed data", "{\"value\":1}"),
                    resourceEvidence: new[] { new ResourceEvidence("claim-evidence", scope, reference, "text", ResourceCoverage.Whole(), true, 1) }),
                int.MaxValue, ToolResultRoles.Developer);
            toolResult.RunId = toolCall.RunId;
            toolSession.Messages.InsertRange(0, new[] { toolCall, toolResult });
            var roleFailure = RuntimeThrows<InvalidOperationException>(() => compact(toolSession, "decision", "tool"));
            AssertContains(roleFailure.Message, "Claim kind", "tool result in a developer envelope cannot support a user decision");
            var observation = compact(toolSession, "observation", "tool");
            AssertEqual("tool", observation.Claims.Single().SourceRoles.Single(), "wire envelope does not determine source role");
            AssertEqual(reference.Revision, observation.Claims.Single().Evidence.Single().Resource.Revision, "observation preserves exact evidence");
            observation.Claims.Single().Text = "Observed source result.";
            var advanced = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Known(new ResourceRef(reference.Uri, "r2"), 2) }) }),
                "tools", new SkillCatalogSnapshot(null), new SchemaRegistrySnapshot(null), 0);
            var afterChange = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(advanced, new ChatMessage[0], new[] {
                new ChatMessage { Role = "assistant", ContextClaims = new List<StructuredContextClaim> { saved.Claims.Single(), observation.Claims.Single() } } },
                null, new ToolCatalogEntry[0], new AppSettings(), 1024);
            AssertTrue(!afterChange.Messages.Single().Content.Contains("Observed source result."), "source revision advance excludes the observation claim");
            AssertContains(afterChange.Messages.Single().Content, "\"kind\":\"constraint\"", "unrelated user constraint survives source revision advance");
            foreach (var kind in new[] { "constraint", "decision", "observation", "unsupported" })
            {
                var session = fresh();
                var failure = RuntimeThrows<InvalidOperationException>(() => compact(session, kind, "assistant"));
                if (kind != "unsupported") AssertContains(failure.Message, "Claim kind", "source-role rejection is explicit");
                AssertEqual(0, session.ContextCheckpoints.Count, "unsupported source promotion publishes no checkpoint");
            }
            var interpreted = fresh();
            var original = compact(interpreted, "interpretation", "user");
            interpreted.Messages.AddRange(fresh().Messages);
            RuntimeThrows<InvalidOperationException>(() => compact(interpreted, "decision", "interpretation"));
            AssertEqual(original.Id, interpreted.ActiveContextCheckpointId, "recompaction cannot upgrade a prior interpretation or replace the last valid checkpoint");
            responseMode = AgentResponseModes.JsonObject;
            compact(fresh(), "constraint", "user");
            var typed = JsonConvert.DeserializeObject<StructuredContextClaim>(JsonConvert.SerializeObject(saved.Claims.Single()));
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "tools", new SkillCatalogSnapshot(null), new SchemaRegistrySnapshot(null), 0);
            var message = new ChatMessage { Role = "assistant", ContextClaims = new List<StructuredContextClaim> { typed } };
            var compiled = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(authority, new ChatMessage[0], new[] { message }, null,
                new ToolCatalogEntry[0], new AppSettings(), 1024);
            AssertContains(compiled.Messages.Single().Content, "\"kind\":\"constraint\"", "model projection preserves type after persistence");
            AssertTrue(!compiled.Messages.Single().Content.Contains(typed.SourceMessageIds.Single()), "durable source ids remain runtime-owned");
            typed.Kind = null;
            var rejected = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(authority, new ChatMessage[0], new[] { message }, null,
                new ToolCatalogEntry[0], new AppSettings(), 1024);
            AssertTrue(!rejected.Messages.Single().Content.Contains(typed.Text), "untyped claims cannot replay as current context");
            saved.PromptVersion = "context-claims-v3";
            AssertTrue(ContextCompactionService.ActiveCheckpoint(user) == null, "old checkpoint is preserved but skipped");
        }

        private static void CompactionUsesStructuredSourceClaims()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(CompactionReply(messages, "Goal preserved; first step complete."));
            };
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var compactedArtifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.Image,
                Title = "Compacted reference",
                MimeType = "image/png"
            };
            session.Artifacts.Add(compactedArtifact);
            var compactedReference = ArtifactReference(session, compactedArtifact);
            var activityArtifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.Chart,
                Title = "Compacted activity reference",
                MimeType = "application/vnd.rnassistant.chart+json"
            };
            session.Artifacts.Add(activityArtifact);
            var activityReference = ArtifactReference(session, activityArtifact);
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "Create a report.",
                ResourceRefs = new List<ResourceRef> { compactedReference }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                ExcludeFromModelContext = true,
                Activity = new ChatActivity { Kind = "tool", Status = "completed" },
                ResourceRefs = new List<ResourceRef> { activityReference }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Inspecting.",
                ProtocolMessage = true,
                RunId = "run_tool",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "call_1",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_2",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT_2\"}"
                    }
                }
            });
            foreach (var callId in new[] { "call_1", "call_2" })
            {
                var toolResult = AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolCallId = callId, ToolId = "excel.read_range" },
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read"), ToolResultRoles.Tool);
                toolResult.RunId = "run_tool";
                session.Messages.Add(toolResult);
            }
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "I will inspect the data." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the original formatting." });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });

            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("not a claim") })
            {
                var artifactCount = session.Artifacts.Count;
                LlmCompletionDelegate malformed = (settings, messages, options, stream, cancellationToken) =>
                {
                    var response = JObject.Parse(CompactionReply(messages, "Valid claim alongside malformed entry.").Content);
                    ((JArray)response["claims"]).Add(invalid.DeepClone());
                    return Task.FromResult(new LlmCompletionResult { Content = response.ToString() });
                };
                RuntimeThrows<InvalidOperationException>(() => new ContextCompactionService(malformed).EnsureWithinBudgetAsync(
                    session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult());
                AssertEqual(artifactCount, session.Artifacts.Count, "malformed extraction does not publish a partial checkpoint");
                AssertTrue(session.ContextCheckpoints.Count == 0, "invalid array entries are rejected rather than silently dropped");
            }

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created");
            AssertEqual("Goal preserved; first step complete.", checkpoint.SummaryMarkdown, "summary used directly");
            var request = FlattenSimple(captured);
            AssertContains(request, "\"required\":[\"claims\"]", "structured claims schema requested");
            AssertTrue(checkpoint.Claims.Count == 1 && checkpoint.Claims[0].SourceMessageIds.Count > 0, "claim carries runtime-owned provenance");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT", "native tool arguments preserved for compaction");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT_2", "all native tool calls preserved for compaction");
            AssertTrue(request.IndexOf("call_1", StringComparison.Ordinal) < 0 &&
                request.IndexOf("call_2", StringComparison.Ordinal) < 0 &&
                request.IndexOf(session.Messages[0].Id, StringComparison.Ordinal) < 0,
                "compaction uses request-local source aliases and omits runtime call/message ids");
            AssertContains(request, "source-1",
                "compaction retains request-local provenance aliases");
            AssertTrue(request.IndexOf("\"goals\"", StringComparison.Ordinal) < 0, "no fixed summary sections");
            AssertContains(
                ContextCompactionService.BuildActiveWindow(session)[0].Content,
                "Reuse complete current skill bodies included in this request",
                "compaction reuses restored current bodies without repeated loading");
            var activeCheckpointMessage = ContextCompactionService.BuildActiveWindow(session)[0];
            AssertTrue(activeCheckpointMessage.ResourceRefs.Any(reference => reference.Uri == compactedReference.Uri),
                "compaction deterministically carries exact resource references into the active window");
            AssertTrue(activeCheckpointMessage.ResourceRefs.Any(reference => reference.Uri == activityReference.Uri),
                "compaction carries resources produced by excluded presentation activities");
            AssertTrue(HistoricalContextProjector.Project(activeCheckpointMessage).Content.IndexOf(
                    compactedReference.Uri, StringComparison.Ordinal) < 0,
                "compacted resource identity remains runtime metadata, not model-facing text");
        }

        private static void CompactionPreservesToolProtocolPairs()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(CompactionReply(messages, "Earlier context."));
            };
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Messages.Add(new ChatMessage { Role = "user", Content = "First request." });
            var beforePair = new ChatMessage { Role = "assistant", Content = "First answer." };
            session.Messages.Add(beforePair);
            var call = new ChatMessage
            {
                Role = "assistant",
                Content = "Reading.",
                ProtocolMessage = true,
                RunId = "run_pair",
                ToolCallId = "call_pair",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "call_pair",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_pair_missing",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_MISSING_ARGUMENT\"}"
                    }
                }
            };
            session.Messages.Add(call);
            var result = AgentJsonProtocol.CreateToolResultMessage(
                new ToolInvocation { ToolCallId = "call_pair", ToolId = "excel.read_range" },
                RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read"), ToolResultRoles.Developer);
            result.RunId = "run_pair";
            session.Messages.Add(result);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Done." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Recent request." });

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created without splitting pair");
            AssertEqual(beforePair.Id, checkpoint.ThroughMessageId, "checkpoint stops before tool call");
            AssertTrue(FlattenSimple(captured).IndexOf("PAIR_ARGUMENT", StringComparison.Ordinal) < 0,
                "tool call is not summarized without its result");
            AssertTrue(FlattenSimple(captured).IndexOf("PAIR_MISSING_ARGUMENT", StringComparison.Ordinal) < 0,
                "multi-call envelope is not summarized with a missing result");
            var replay = ContextCompactionService.BuildActiveWindow(session);
            var callIndex = replay.FindIndex(message => message != null && message.ToolCallId == "call_pair");
            var resultIndex = replay.FindIndex(message => message != null &&
                (message.Content ?? string.Empty).StartsWith("TOOL_RESULT:", StringComparison.Ordinal));
            AssertTrue(callIndex >= 0 && resultIndex == callIndex + 1, "tool call and result remain adjacent in replay tail");

            IReadOnlyList<ChatMessage> danglingCaptured = null;
            LlmCompletionDelegate danglingCompletion = (settings, messages, options, stream, cancellationToken) =>
            {
                danglingCaptured = messages.ToList();
                return Task.FromResult(CompactionReply(messages, "Safe prefix."));
            };
            var dangling = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            dangling.Messages.Add(new ChatMessage { Role = "user", Content = "Old request." });
            dangling.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old answer." });
            var safeThrough = new ChatMessage { Role = "user", Content = "Next request." };
            dangling.Messages.Add(safeThrough);
            dangling.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "Running.",
                ProtocolMessage = true,
                RunId = "interrupted-run",
                ToolCallId = "dangling-call",
                ToolCalls = new List<LlmToolCall>
                {
                    new LlmToolCall
                    {
                        Id = "dangling-call",
                        Type = "function",
                        Name = "excel.read_range",
                        ArgumentsJson = "{\"marker\":\"DANGLING_ARGUMENT\"}"
                    }
                }
            });
            dangling.Messages.Add(new ChatMessage { Role = "assistant", Content = "Interrupted diagnostic." });
            dangling.Messages.Add(new ChatMessage { Role = "user", Content = "Continue safely." });

            var danglingCheckpoint = new ContextCompactionService(danglingCompletion).EnsureWithinBudgetAsync(
                dangling, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(danglingCheckpoint != null, "checkpoint created before dangling call");
            AssertEqual(safeThrough.Id, danglingCheckpoint.ThroughMessageId, "checkpoint excludes call without result");
            AssertTrue(FlattenSimple(danglingCaptured).IndexOf("DANGLING_ARGUMENT", StringComparison.Ordinal) < 0,
                "dangling tool call is not summarized");

            // CLI presents adjacent pairs, but the durable accepted call still
            // owns the whole batch. A budget boundary after its first result
            // must not discard the original call for the second result.
            var batchSession = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var task = new ChatMessage { Role = "user", Content = "KEEP_EXACT_TASK: read both sources." };
            var safeBatchThrough = new ChatMessage { Role = "assistant", Content = "Earlier context." };
            var batch = HistoricalContextProjector.Project(call);
            batch.ToolCallId = null;
            var batchResults = batch.ToolCalls.Select(item =>
            {
                var output = AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolCallId = item.Id, ToolId = item.Name },
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read"), ToolResultRoles.Tool);
                output.RunId = batch.RunId;
                return output;
            }).ToList();
            batchSession.Messages.AddRange(new[] { task, safeBatchThrough, batch });
            batchSession.Messages.AddRange(batchResults);
            var adjacent = new List<ChatMessage> { task, safeBatchThrough };
            for (var index = 0; index < batch.ToolCalls.Count; index++)
            {
                var single = HistoricalContextProjector.Project(batch);
                single.Id = batch.Id + ":" + index;
                single.ToolCalls = new List<LlmToolCall> { batch.ToolCalls[index] };
                single.ToolCallId = batch.ToolCalls[index].Id;
                adjacent.Add(single);
                adjacent.Add(batchResults[index]);
            }
            var frozen = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            var batchCheckpoint = new ContextCompactionRuntime(completion).EnsureWithinBudgetAsync(
                batchSession, new AppSettings(), null, true, null, CancellationToken.None,
                frozen, new ToolCatalogEntry[0], replayFacts: adjacent).GetAwaiter().GetResult();
            AssertTrue(batchCheckpoint != null, "safe durable prefix can be compacted");
            AssertEqual(safeBatchThrough.Id, batchCheckpoint.ThroughMessageId,
                "checkpoint cannot split an accepted batch after presentation split it into pairs");
            var batchReplay = ContextCompactionRuntime.BuildActiveWindow(batchSession);
            AssertTrue(batchReplay.Any(item => item.Id == task.Id && item.Content == task.Content),
                "current user instruction survives verbatim even when the summary omits it");
            AssertTrue(batchReplay.Contains(batch) && batchResults.All(batchReplay.Contains),
                "both accepted batch results retain their original call after compaction");

            // Current-source filtering can fold an older exchange into a typed
            // terminal receipt. Its correlation ID does not make it a new call.
            var receiptSession = new ChatSession();
            var afterReceipts = new ChatMessage { Role = "assistant", Content = "Both earlier operations completed." };
            var recentTask = new ChatMessage { Role = "user", Content = "Continue the exact remaining task." };
            var recentAnswer = new ChatMessage { Role = "assistant", Content = "Inspecting the next source." };
            receiptSession.Messages.AddRange(new[] { task, safeBatchThrough, batch });
            receiptSession.Messages.AddRange(batchResults);
            receiptSession.Messages.AddRange(new[] { afterReceipts, recentTask, recentAnswer });
            var receiptFacts = new List<ChatMessage> { task, safeBatchThrough };
            foreach (var output in batchResults)
            {
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(output, out wire, out error), "terminal result is readable");
                var completed = ModelContextCompiler.CompleteOperation(output, wire);
                AssertTrue(!ToolProtocolMessages.IsCall(completed), "completed receipt is not an unanswered call");
                AssertTrue(ToolProtocolMessages.Uses(completed, new HashSet<string> { output.ToolCallId }),
                    "completed receipt keeps its exact exchange correlation");
                receiptFacts.Add(completed);
            }
            receiptFacts.AddRange(new[] { afterReceipts, recentTask, recentAnswer });
            var receiptCheckpoint = new ContextCompactionRuntime(completion).EnsureWithinBudgetAsync(
                receiptSession, new AppSettings(), null, true, null, CancellationToken.None,
                frozen, new ToolCatalogEntry[0], replayFacts: receiptFacts).GetAwaiter().GetResult();
            AssertEqual(afterReceipts.Id, receiptCheckpoint.ThroughMessageId,
                "compaction progresses beyond typed completed receipts while respecting the original complete batch");
            AssertTrue(ContextCompactionRuntime.BuildActiveWindow(receiptSession).Any(item => item.Id == recentTask.Id),
                "receipt compaction retains the exact current user instruction");

            var oversized = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            oversized.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 500000) });
            oversized.Messages.Add(new ChatMessage { Role = "assistant", Content = "Recent answer." });
            var oversizedCheckpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                oversized, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(oversizedCheckpoint == null, "oversized message is not partially marked as summarized");
        }

        private static string FlattenSimple(IEnumerable<ChatMessage> messages)
        {
            return string.Join("\n", (messages ?? new ChatMessage[0])
                .Where(message => message != null)
                .Select(message => message.Content ?? string.Empty)
                .ToArray());
        }

        private static JObject LastToolResult(IEnumerable<ChatMessage> messages, string toolId)
        {
            var message = (messages ?? new ChatMessage[0]).Last(item => item != null && item.ProtocolMessage &&
                string.Equals(item.ToolName, toolId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            var content = message.Content ?? string.Empty;
            const string prefix = "TOOL_RESULT:\n";
            return JObject.Parse(content.StartsWith(prefix, StringComparison.Ordinal)
                ? content.Substring(prefix.Length)
                : content);
        }
    }
}
