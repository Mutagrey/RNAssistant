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
                AssertEqual(call.Name, parsed.Response.ToolCalls.Single().Name, "canonical name is never reconstructed from an API alias");
                var arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(parsed.Response.ToolCalls[0].ArgumentsJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                AssertEqual("2026-08-28T12:34:56Z", arguments["at"] as string, "accepted ISO argument remains exact text");
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
            AssertTrue(!ConversationResponseHistoryReader.Read(noCanonicalName).Success, "provider-safe name cannot reconstruct a canonical id");
            var batch = nativeMessage();
            batch.ToolCalls.Add(new LlmToolCall { Id = "second", Name = "rna_test_read", ArgumentsJson = "{}" });
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
            var result = AgentJsonProtocol.CreateToolResultMessage(new ToolCommand { ToolCallId = "call_1", ToolId = "test.read" }, RNAssistant.Core.Tools.Contracts.ToolResult.Ok("ok"));
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

        private static ToolDefinition V4ReadTool(string id = "test.read")
        {
            return new ToolDefinition
            {
                Id = id,
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"query\":{\"type\":\"string\",\"description\":\"Query.\",\"minLength\":1}," +
                    "\"limit\":{\"type\":\"integer\",\"description\":\"Optional limit.\",\"minimum\":1,\"maximum\":50,\"default\":10}," +
                    "\"at\":{\"type\":\"string\",\"description\":\"Optional timestamp.\"}" +
                    "},\"required\":[\"query\"],\"additionalProperties\":false}"
            };
        }

        private static JObject V4Call(string name = "test.read", JObject arguments = null)
        {
            return new JObject { ["name"] = name,
                ["arguments"] = arguments ?? new JObject { ["query"] = "A" } };
        }

        private static string V4Envelope(params JObject[] calls)
        {
            return new JObject { ["message"] = "Читаю.", ["tool_calls"] = new JArray(calls) }.ToString(Formatting.None);
        }

        private static ConversationResponseParseResult ParseV4(string json, params ToolDefinition[] tools)
        {
            return new ConversationResponseParser().Parse(json, tools, tools,
                new ModelProtocolCallContext(tools.Select(tool => tool.Id)));
        }

        private static void ConversationV4RoundTripsWithoutStatus()
        {
            foreach (var message in new[] { "Готово.", "", "  ", "Не удалось выполнить.\nНужен доступ.", "He said \"done\" \\ / \t" })
            {
                var json = new JObject { ["message"] = message, ["tool_calls"] = new JArray() }.ToString(Formatting.None);
                var parsed = ParseV4(json);
                AssertTrue(parsed.Success, "v4 accepts a string message without interpreting its wording");
                AssertEqual(message, parsed.Response.Message, "message remains exact");
                AssertTrue(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(parsed.Response.ToJson())), "status-free final round trip");
            }
            var tool = V4ReadTool();
            var callJson = V4Envelope(V4Call(arguments: new JObject
            {
                ["query"] = "\\u0061", ["at"] = "2026-08-28T12:34:56Z"
            }));
            var call = ParseV4(callJson, tool);
            AssertTrue(call.Success, "v4 tool parses");
            AssertTrue(call.Response.ToolCalls[0].Arguments["at"] is string, "ISO text is not silently converted to DateTime");
            AssertTrue(JToken.DeepEquals(JObject.Parse(callJson), JObject.Parse(call.Response.ToJson())), "call round trip preserves arguments without assigning ids");
            AssertTrue(typeof(ConversationResponse).GetProperty("Status") == null, "v4 DTO has no model or universal status");
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
            foreach (var json in new[] { "{}", "{\"message\":\"x\"}", "{\"tool_calls\":[]}",
                "{\"message\":null,\"tool_calls\":[]}", "{\"message\":1,\"tool_calls\":[]}",
                "{\"message\":\"x\",\"tool_calls\":null}", "{\"message\":\"x\",\"tool_calls\":{}}" })
                AssertTrue(!ParseV4(json).Success, "missing/wrong root type rejected: " + json);
        }

        private static void ConversationV4RejectsMalformedJson()
        {
            foreach (var json in new[]
            {
                "", "<html>Blocked</html>", "content rejected by protection", "[]", "null",
                "```json\n{\"message\":\"x\",\"tool_calls\":[]}\n```",
                "text {\"message\":\"x\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"tool_calls\":[]} {}",
                "{\"message\":\"x\",\"message\":\"y\",\"tool_calls\":[]}",
                "{\"message\":\"x\",/*comment*/\"tool_calls\":[]}",
                "{'message':'x','tool_calls':[]}",
                "{message:\"x\",\"tool_calls\":[]}",
                "{true:\"x\",\"message\":\"x\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"tool_calls\":[],}",
                "{\"message\":\"line\nbreak\",\"tool_calls\":[]}",
                "{\"message\":\"bad\\'escape\",\"tool_calls\":[]}",
                "{\"message\":\"bad\\u12xx\",\"tool_calls\":[]}",
                "{\"message\":\"x\",\"tool_calls\":[}",
                "{\"message\":\"x\",\"tool_calls\":[{\"name\":\"test.read\",\"arguments\":{\"query\":\"A\",\"limit\":NaN}}]}"
            }) AssertTrue(!ParseV4(json, V4ReadTool()).Success, "non-JSON or incomplete envelope rejected: " + json);

            foreach (var number in new[] { "01", "+1", ".5", "0x10", "undefined", "Infinity", "1e999", "999999999999999999999999999999999" })
            {
                var json = V4Envelope(V4Call(arguments: new JObject { ["query"] = "A", ["limit"] = "NUMBER" }))
                    .Replace("\"NUMBER\"", number);
                AssertTrue(!ParseV4(json, V4ReadTool()).Success, "non-JSON/non-finite number rejected: " + number);
            }
            var escaped = ParseV4("{\"message\":\"\\u0410\\/\\b\\f\\n\\r\\t\",\"tool_calls\":[]}");
            AssertTrue(escaped.Success, "standard Unicode and control escapes are accepted");
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
            AssertTrue(!parser.Parse(V4Envelope(), new ToolDefinition[0], new ToolDefinition[0], null).Success,
                "local safety context must be explicit, even if empty");
            AssertTrue(!parser.Parse(V4Envelope(), new ToolDefinition[0], new ToolDefinition[0], new ModelProtocolCallContext(null)).Success,
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

            foreach (var kind in new[] { "document", "local", "confirmation", "external", "unclassified" })
            {
                var tool = V4ReadTool("test.action");
                tool.MutatesDocument = kind == "document";
                tool.MutatesLocalState = kind == "local";
                tool.RequiresConfirmation = kind == "confirmation";
                // External/effect classification belongs to trusted execution authority,
                // not the name or legacy booleans. Missing classification fails closed.
                var batchSafe = kind == "external" || kind == "unclassified" ? new[] { read.Id } : new[] { read.Id, tool.Id };
                foreach (var batch in new[]
                {
                    V4Envelope(V4Call(tool.Id), V4Call(read.Id)),
                    V4Envelope(V4Call(read.Id), V4Call(tool.Id))
                })
                {
                    var result = parser.Parse(batch, new[] { read, tool }, new[] { read, tool }, new ModelProtocolCallContext(batchSafe));
                    AssertTrue(!result.Success, kind + " cannot be batched, regardless of position");
                    AssertContains(result.Error, "one at a time", "singleton diagnosis");
                }
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
            var tools = adapter.GetBuiltInTools().Where(tool => tool.Id == "excel.add_sheet" || tool.Id == "excel.read_range").ToList();
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
            var messages = new ConversationPromptComposer().BuildMessages(
                ChatModes.Agent,
                "Create a report.", adapter, tools, skills, new DocumentContext(), new AppSettings(),
                NewSession(adapter), null);
            var prompt = FlattenSimple(messages);
            AssertContains(prompt, "\"type\":\"function\"", "native-like tool JSON");
            AssertContains(prompt, "\"description\":\"Worksheet name; omit only when the active sheet is intended.\"", "argument description present");
            AssertContains(prompt, "excel.add_sheet", "first tool present");
            AssertContains(prompt, "excel.read_range", "second tool present");
            AssertContains(prompt, "common.test", "skill id present");
            AssertContains(prompt, "Test workflow", "skill description present");
            AssertContains(prompt, "\"revision\":\"" + SkillRevision.Compute(skills[0]) + "\"", "skill revision present");
            AssertContains(prompt, "\"bodyChars\":27", "skill body size present");
            AssertContains(prompt, "\"referenceCount\":0", "skill reference count present");
            AssertTrue(prompt.IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0, "full skill is not in catalog");
            AssertContains(prompt, "common.capabilities_read", "unified capability loading guidance present");
            AssertContains(prompt, "\"kind\":\"skill\"", "skill kind is explicit in compact catalog");
            AssertContains(prompt, "metadata only", "catalog is not mistaken for loaded skill instructions");
            AssertContains(prompt, "`loaded=true`", "skill loading state is explicit");
            AssertContains(prompt, "Return several calls", "multi-tool guidance present");
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.html_workspace_upsert"),
                    "{\"message\":\"Создаю локальный HTML.\",\"tool_calls\":[{\"name\":\"common.html_workspace_upsert\",\"arguments\":{\"resourceType\":\"file\",\"name\":\"index.html\",\"content\":\"<main>Offline</main>\"}}]}",
                    "{\"message\":\"Локальный HTML готов.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                AssertContains(prompt, "\"key\":\"closed-doc\"", "archived document identity in prompt");
                AssertContains(prompt, "\"office_tools_available\":false", "Office availability in prompt");
                AssertContains(prompt, "\"id\":\"common.html_workspace_upsert\"",
                    "exact local HTML capability remains discoverable");
                AssertTrue(prompt.IndexOf("\"name\":\"common.html_workspace_upsert\"", StringComparison.OrdinalIgnoreCase) < 0,
                    "local HTML schema is not injected before discovery");
                AssertContains(FlattenSimple(calls[1]), "\"name\":\"common.html_workspace_upsert\"",
                    "exact local HTML schema is available after read");
                AssertTrue(prompt.IndexOf("excel.read_range", StringComparison.OrdinalIgnoreCase) < 0,
                    "Office tools omitted for a closed document");
                AssertTrue(prompt.IndexOf("common.html_data_bind", StringComparison.OrdinalIgnoreCase) < 0,
                    "Office-backed HTML binding omitted for a closed document");
                AssertEqual(0, adapter.Executed.Count, "local tool does not enter Office adapter");

                var blocked = executor.Execute(
                    Command("excel.read_range", "sheet", "Data", "address", "A1:B2"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("active_document_changed", blocked.ErrorCode,
                    "closed-document Office call remains guarded");
                AssertEqual(0, adapter.Executed.Count, "guarded Office tool never reaches adapter");
            });
        }

        private static void SimpleAgentLoadsFullSkillThroughTool()
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
                    "{\"message\":\"Читаю подходящий skill.\",\"tool_calls\":[{\"name\":\"common.capabilities_read\",\"arguments\":{\"id\":\"common.test\"}}]}",
                    "{\"message\":\"Инструкции учтены.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Do the test workflow.", NewSession(adapter), NewContext(adapter), new AppSettings(),
                    tools, null, null, null, new[] { skill }, CancellationToken.None, true).GetAwaiter().GetResult();

                AssertEqual("Инструкции учтены.", result.AssistantText, "skill-assisted response");
                var revision = SkillRevision.Compute(skill);
                AssertTrue(FlattenSimple(calls[0]).IndexOf("TEST_SKILL_SENTINEL", StringComparison.Ordinal) < 0,
                    "first request contains only catalog");
                AssertContains(FlattenSimple(calls[0]), "\"revision\":\"" + revision + "\"", "catalog carries skill revision");
                var replay = FlattenSimple(calls[1]);
                AssertContains(replay, "TEST_SKILL_SENTINEL", "full instructions returned by tool");
                AssertContains(replay, "TEST_SKILL_END", "skill body is not cut by the generic tool-result limit");
                AssertContains(replay, "\"format\":\"markdown\"", "loaded skill format");
                AssertContains(replay, "\"version\":\"2.0.0\"", "loaded skill version");
                AssertContains(replay, "\"revision\":\"" + revision + "\"", "loaded skill revision matches catalog");
                AssertContains(replay, "\"loaded\":true", "loaded skill result is explicit");
                AssertContains(replay, "\"complete\":true", "loaded skill body is complete");
                AssertContains(replay, "\"toolSchemasLoadedByThisRead\":false",
                    "skill result explicitly keeps referenced tool schemas unloaded");
                AssertContains(replay, "common.capabilities_read",
                    "skill result gives adjacent tool-schema loading guidance");
                AssertTrue(replay.IndexOf("\"truncated\":true", StringComparison.Ordinal) < 0,
                    "loaded skill is not duplicated into a truncated result");
            });
        }

        private static void SimpleAgentPromptSkipsInvalidToolSchema()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var tools = new[]
            {
                new ToolDefinition
                {
                    Id = "excel.good",
                    Description = "Good",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"
                },
                new ToolDefinition
                {
                    Id = "excel.bad",
                    Description = "Bad",
                    Enabled = true,
                    AgentCanRun = true,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[],\"additionalProperties\":false}"
                }
            };
            var prompt = FlattenSimple(new ConversationPromptComposer().BuildMessages(
                ChatModes.Agent,
                "Test", adapter, tools, null, new DocumentContext(), new AppSettings(), NewSession(adapter), null));
            AssertContains(prompt, "excel.good", "valid tool included");
            AssertTrue(prompt.IndexOf("excel.bad", StringComparison.OrdinalIgnoreCase) < 0, "invalid tool excluded");
        }

        private static void StrictToolSchemaValidatesMetadataAndConstraints()
        {
            var tool = new ToolDefinition
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

            var tableCommand = new ToolCommand();
            tableCommand.Arguments["values"] = new Newtonsoft.Json.Linq.JArray(
                new Newtonsoft.Json.Linq.JArray("A", "B"),
                new Newtonsoft.Json.Linq.JArray("C", "D"),
                new Newtonsoft.Json.Linq.JArray("E", "F"));
            ResolvedTableArguments table;
            AssertTrue(TableArgumentResolver.TryResolve(tableCommand, 2, 2, out table, out error), "table dimensions are inferred");
            AssertEqual(3, table.Rows, "inferred table rows");
            AssertEqual(2, table.Columns, "inferred table columns");
            tableCommand.Arguments["rows"] = 2;
            AssertTrue(!TableArgumentResolver.TryResolve(tableCommand, 2, 2, out table, out error), "undersized explicit table dimensions fail before COM");
            AssertContains(error, "omit", "table dimension recovery hint");

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
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "unsupported schema assertions are rejected instead of being ignored locally");
            AssertContains(error, "unsupported schema keyword", "unsupported schema keyword diagnostic");

            tool.ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name.\"},\"Name\":{\"type\":\"string\",\"description\":\"Ambiguous name.\"}},\"required\":[],\"additionalProperties\":false}";
            AssertTrue(!ToolSchemaSupport.TryParse(tool, out schema, out error), "case-colliding schema properties are rejected");
            AssertContains(error, "differ only by case", "case-colliding schema diagnostic");

        }

        private static void ControllerToolCatalogUsesStrictSchemas()
        {
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
            {
                WithTempExecutor(FakeOfficeAdapter.ForHost(host), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var catalog = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
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
                            var command = new ToolCommand { ToolId = tool.Id };
                            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
                            var result = executor.Execute(command, catalog, new AppSettings { AutoConfirmToolActions = true }, true, true, session);
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "unknown_tool", StringComparison.OrdinalIgnoreCase), host + "/" + tool.Id + " dispatch is registered");
                            AssertTrue(result == null || !string.Equals(result.ErrorCode, "invalid_arguments", StringComparison.OrdinalIgnoreCase), host + "/" + tool.Id + " published branch reaches its handler");
                        }
                        var responseSchema = ConversationResponseSchemaBuilder.Build(new[] { tool });
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
                    "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
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
                var result = service.ExecuteAsync(
                    ChatModes.Agent,
                    "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

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
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.QueueExcelSheetApplyFailure(
                    "Write rejected before the effect.", "write_rejected", false);
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"message\":\"Лист Report создан.\",\"tool_calls\":[],\"executionSummary\":{\"ExecutionHealth\":\"clean\",\"WriteOk\":1000}}",
                    "{\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
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
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "excel.add_sheet");
                AssertEqual(false, (bool)write["success"], "write failure is preserved");
                AssertEqual("write_rejected", (string)write["errorCode"], "actual failure code is preserved");
                AssertTrue(!adapter.HasSheet("Report"), "the claimed sheet was not created");
                AssertEqual(1, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "failed write is not retried");
                AssertContains(FlattenSimple(requests.Last()), "\"status\":\"error\"", "the final model request saw the error");
                AssertContains(requests.Last().Last().Content, "unsupported root field: executionSummary", "model cannot inject runtime health into v4");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "loop completion is independent of execution health");
                AssertRunViewState(result, session, "errors", 0, 1, 0);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model completed is accepted after write error");
                AssertEqual("Лист Report создан.", result.AssistantText, "false mutation claim is not filtered");
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
                    LoadToolSchemaResponse("common.vba_write_module"),
                    new JObject
                    {
                        ["message"] = "Обновляю модуль.",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    "{\"message\":\"Модуль Module1 обновлён.\",\"tool_calls\":[]}"
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
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "common.vba_write_module");
                AssertEqual(false, (bool)write["success"], "unverified effect is not a successful tool result");
                AssertEqual("partial_failure", (string)write["status"], "current unknown transport is partial_failure");
                AssertEqual("vba_mutation_unknown", (string)write["errorCode"], "real journal classified the divergent effect");
                AssertEqual(false, (bool)write["retryable"], "unknown write cannot be retried automatically");
                var writeData = JObject.Parse((string)write["dataJson"]);
                AssertTrue(writeData["journalStatus"] == null,
                    "internal journal status does not leak into model-facing data");
                AssertTrue(!string.IsNullOrWhiteSpace((string)writeData["mutationId"]),
                    "unknown evidence retains mutation correlation");
                AssertEqual(VbaMutationStatuses.Unknown,
                    journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status, "durable journal also records unknown");
                AssertContains(adapter.VbaModuleCode, "\"diverged\"", "fake host state matches neither before nor intended");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.vba_replace_module"), "unknown write is dispatched once");
                AssertContains(FlattenSimple(requests.Last()), "vba_mutation_unknown", "model receives unknown effect evidence");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "loop completion is independent of execution health");
                AssertRunViewState(result, session, "unknown", 0, 0, 1);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "model completed is accepted after unknown write");
                AssertEqual("Модуль Module1 обновлён.", result.AssistantText, "unverified mutation claim survives");
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
                if (outcome == "error") adapter.QueueResult("excel.vba_replace_module",
                    ToolResult.Fail("Write rejected.", null, "write_rejected", false));
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
                    LoadToolSchemaResponse("common.vba_write_module"),
                    new JObject
                    {
                        ["message"] = "Update module.",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    "{\"message\":\"Done.\",\"tool_calls\":[]}"
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
                    result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                        ChatModes.Agent, "Update Module1.", session, NewContext(adapter),
                        new AppSettings { AutoConfirmToolActions = true, MaxAgentFormatRetries = 20 },
                        adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null)
                        .GetAwaiter().GetResult();
                }
                store.Save(session);
                var events = store.ReadEvents(session.Host, session.DocumentKey, session.Id);
                var requests = events.Where(item => item.Type == SessionEventTypes.LlmRequest).ToList();
                var rejected = events.Where(item => item.Type == SessionEventTypes.AgentResponseRejected).ToList();
                var accepted = events.Where(item => item.Type == "model.response.accepted").ToList();
                AssertEqual(invalidAttempts + 3, requestCount, "trace introduces no retry or model request");
                AssertEqual(invalidAttempts, rejected.Count, "every rejected attempt has a diagnostic event");
                AssertEqual(3, accepted.Count, "only valid schema/write/final responses are accepted");
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
                var toolStart = events.Single(item => item.Type == "tool.execution.started" && (string)item.Data["ToolCallId"] == callId);
                var toolEnd = events.Single(item => item.Type == "tool.execution.completed" && (string)item.Data["ToolCallId"] == callId);
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
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.vba_replace_module"), "exactly one write dispatch");
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
                        Content = "{\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
                    });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

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
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю листы.\",\"tool_calls\":[{\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Готово.\",\"tool_calls\":[]}"
                });
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var settings = new AppSettings
                {
                    SystemPrompt = "SYSTEM_PROMPT_SENTINEL",
                    AgentToolsPrompt = "TOOLS_PROMPT_SENTINEL",
                    AgentSkillsPrompt = "SKILLS_PROMPT_SENTINEL"
                };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "List sheets.", session, NewContext(adapter), settings,
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

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
                    new LlmCompletionResult { Content = "{\"message\":\"Не могу выполнить этот запрос.\",\"tool_calls\":[]}" }
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
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

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
                const string invalidPair = "{\"status\":\"in_progress\",\"message\":\"Проверяю листы...\",\"tool_calls\":[]}";
                var responses = new Queue<string>(new[]
                {
                    invalidPair,
                    LoadToolSchemaResponse("excel.inspect"),
                    "{\"message\":\"Проверяю листы.\",\"tool_calls\":[{\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"message\":\"Список листов проверен.\",\"tool_calls\":[]}"
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
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

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
                                    tool_calls = new object[0]
                                })
                            }));
                    var terminalResult = terminalService.ExecuteAsync(
                        ChatModes.Agent,
                        "Продолжай.",
                        terminalSession,
                        NewContext(adapter),
                        new AppSettings(),
                        adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                        null).GetAwaiter().GetResult();
                    AssertEqual(terminalCase, terminalResult.AssistantText, "message is preserved without classification or trimming");
                    AssertEqual(AgentResponseStatuses.Completed, terminalResult.ResponseStatus,
                        "runtime projection does not infer a status from wording");
                    AssertEqual(RunViewLifecycles.Completed, terminalResult.RunViewState.Lifecycle,
                        "empty calls end the model loop");
                    AssertEqual(AgentResponseProtocol.CurrentVersion, terminalResult.ResponseProtocolVersion,
                        "final record carries the active protocol version");
                    AssertTrue(ConversationResponseHistoryReader.Read(terminalSession.Messages.Last()).Success,
                        "actual final history is a valid v4 form even with empty or question-like text");
                }

                var limitedSession = NewSession(adapter);
                var limitedService = CreateConversationRunService(
                    adapter,
                    executor,
                    (settings, messages, options, stream, cancellationToken) => Task.FromResult(
                        new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Проверяю ресурсы.\",\"tool_calls\":[{\"name\":\"common.resources_list\",\"arguments\":{}}]}",
                            PromptTokens = 5
                        }));
                var limitedResult = limitedService.ExecuteAsync(
                    ChatModes.Agent,
                    "Проверяй до лимита.",
                    limitedSession,
                    NewContext(adapter),
                    new AppSettings { MaxAgentIterations = 1 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
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
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

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
                            Content = "{\"message\":\"Ответ принят.\",\"tool_calls\":[]}",
                            ReasoningContent = "ACCEPTED_REASONING"
                        });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Ответь на вопрос.", session, NewContext(adapter),
                    new AppSettings { MaxAgentFormatRetries = 20 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

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
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

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
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"message\":\"Готово.\",\"tool_calls\":[]}" });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
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
                    "mandatory Excel/VBA core keeps the shared continuation reserve");
                var prompt = FlattenSimple(request);
                AssertContains(prompt, "\"name\":\"common.resources_list\"", "resource discovery exposed");
                AssertContains(prompt, "\"name\":\"common.resources_read\"", "resource reads exposed");
                AssertContains(prompt, "\"name\":\"common.resources_search\"", "resource search exposed");
                AssertContains(prompt, "\"name\":\"common.capabilities_search\"", "unified capability search exposed");
                AssertContains(prompt, "\"name\":\"common.capabilities_read\"", "unified exact capability loading exposed");
                AssertContains(prompt, "\"id\":\"common.vba_apply_patch\"", "exact VBA mutation id is in compact catalog");
                AssertContains(prompt, "\"id\":\"common.vba_code_editing\"", "exact VBA skill id is in compact catalog");
                AssertTrue(prompt.IndexOf("\"id\":\"common.vba_inspect\"", StringComparison.Ordinal) < 0,
                    "invented VBA capability id is absent from the authoritative catalog");
                var callableNames = JObject.Parse(requestOptions.ResponseSchemaJson)
                    .SelectTokens("properties.tool_calls.items.anyOf[*].properties.name.const")
                    .Select(token => (string)token)
                    .ToList();
                AssertTrue(callableNames.Contains("common.vba_apply_patch", StringComparer.OrdinalIgnoreCase) &&
                    callableNames.Contains("common.vba_write_module", StringComparer.OrdinalIgnoreCase) &&
                    callableNames.Contains("common.vba_delete_module", StringComparer.OrdinalIgnoreCase),
                    "public VBA mutation schemas are complete in the VBA core");
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
                AssertTrue(callableNames.Contains("common.office_run_macro", StringComparer.OrdinalIgnoreCase),
                    "public macro schema is complete in the VBA core");
            });
        }

        private static void AgentPreservesVbaResourceEvidenceWithinBudget()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string moduleName = "BudgetModule";
                const string sourceMarker = "VBA_RESOURCE_SOURCE_SENTINEL";
                var source = "Option Explicit\n' " + sourceMarker + "\n" +
                    string.Concat(Enumerable.Range(0, 420).Select(index =>
                        "Public Sub BudgetLine" + index + "()\nDebug.Print \"" + index + "\"\nEnd Sub\n"));
                adapter.SetVbaModule(moduleName, source, "StdModule");

                var calls = new List<Tuple<IReadOnlyList<ChatMessage>, LlmRequestOptions>>();
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
                                    Name = ResourceToolCatalog.ListToolId,
                                    Arguments = new Dictionary<string, object> { ["provider"] = VbaResourceProvider.ProviderName }
                                }
                            }) });
                    }

                    if (calls.Count == 2)
                    {
                        var wire = LastToolResult(messages, ResourceToolCatalog.ListToolId);
                        AssertEqual("ok", (string)wire["status"], "VBA resource list remains successful");
                        var data = wire["data"] as JObject;
                        AssertTrue(data != null && data["items"] is JArray,
                            "VBA list data is preserved instead of a transport truncation wrapper");
                        var component = ((JArray)data["items"]).OfType<JObject>().Single(item =>
                            string.Equals((string)item["title"], moduleName, StringComparison.OrdinalIgnoreCase));
                        var reference = component["reference"] as JObject;
                        AssertTrue(reference != null && !string.IsNullOrWhiteSpace((string)reference["uri"]),
                            "VBA component exposes its exact resource URI");
                        AssertTrue(wire["resources"] is JArray && ((JArray)wire["resources"]).OfType<JObject>()
                            .Any(item => string.Equals((string)item["uri"], (string)reference["uri"], StringComparison.Ordinal)),
                            "listed VBA URIs are also exact Tool Result resources");
                        return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.Write(
                            "Читаю исходник.", new[]
                            {
                                new ConversationToolCall
                                {
                                    Name = ResourceToolCatalog.ReadToolId,
                                    Arguments = new Dictionary<string, object>
                                    {
                                        ["uri"] = (string)reference["uri"],
                                        ["representation"] = ResourceRepresentations.Source,
                                        ["maxChars"] = 8000
                                    }
                                }
                            }) });
                    }

                    var readWire = LastToolResult(messages, ResourceToolCatalog.ReadToolId);
                    AssertEqual("ok", (string)readWire["status"],
                        "VBA source read remains successful: " + readWire.ToString(Formatting.None));
                    var readData = readWire["data"] as JObject;
                    AssertTrue(readData != null && readData["resource"] is JObject,
                        "VBA source metadata is not replaced by a transport truncation wrapper");
                    AssertContains((string)readData["text"], sourceMarker, "VBA source reaches the model");
                    AssertTrue(!string.IsNullOrWhiteSpace((string)readData["nextCursor"]),
                        "bounded VBA source keeps its exact continuation cursor");
                    AssertTrue(readWire["resources"] is JArray && ((JArray)readWire["resources"]).Count == 1,
                        "VBA source read retains the exact root resource reference");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = ModelProtocolWire.Write("VBA прочитан.", new ConversationToolCall[0])
                    });
                };

                var settings = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema };
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Прочитай VBA-модуль " + moduleName + ".",
                    NewSession(adapter),
                    NewContext(adapter),
                    settings,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    null,
                    null,
                    BuiltInSkillProvider.GetSkills(adapter)).GetAwaiter().GetResult();

                AssertEqual("VBA прочитан.", result.AssistantText, "VBA resource loop completes");
                AssertEqual(3, calls.Count, "list, source read, and final response use three model steps");
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
                    "{\"message\":\"Запускаю выбранный макрос.\",\"tool_calls\":[{\"name\":\"common.office_run_macro\",\"arguments\":{\"macroName\":\"Module1.MigrateApiKey\",\"arguments\":[\"value\",2,true]}}]}",
                    "{\"message\":\"Макрос выполнен.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Run Module1.MigrateApiKey with arguments.", NewSession(adapter), NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 }, tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "schema load, macro execution, and final response");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.run_macro"), "macro executes once");
                AssertEqual("Module1.MigrateApiKey", adapter.RanMacros.Single(), "arbitrary exact macro name reaches the adapter");
                AssertEqual("[\"value\",2,true]", Convert.ToString(adapter.Executed.Single(command => command.ToolId == "excel.run_macro").Arguments["argumentsJson"]),
                    "public native arguments are serialized only at the hidden backend boundary");
                AssertContains(FlattenSimple(calls[1]), "\"kind\":\"tool-schema\"", "macro schema evidence reaches execution step");
                AssertEqual("Макрос выполнен.", result.AssistantText, "macro result returns to the model");
            });
        }

        private static void SimpleAgentExecutesMultipleToolsSequentially()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet"),
                    "{\"message\":\"Создаю два независимых листа.\",\"tool_calls\":[" +
                    "{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"First\"}}," +
                    "{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Second\"}}]}",
                    "{\"message\":\"Создаю первый лист.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"First\"}}]}",
                    "{\"message\":\"Создаю второй лист.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Second\"}}]}",
                    "{\"message\":\"Оба листа созданы.\",\"tool_calls\":[]}"
                });
                IReadOnlyList<ChatMessage> secondTurn = null;
                var progressActivities = new List<ChatActivity>();
                var callCount = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    callCount += 1;
                    if (callCount == 3)
                    {
                        AssertContains(messages.Last().Content, "one at a time", "unsafe write batch is repaired");
                        AssertTrue(!adapter.HasSheet("First") && !adapter.HasSheet("Second"), "rejected batch executes no partial tool calls");
                    }
                    if (callCount == 5) secondTurn = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Создай листы First и Second.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), (phase, message, activity) =>
                    {
                        if (activity != null) progressActivities.Add(activity);
                    }).GetAwaiter().GetResult();

                AssertEqual("Оба листа созданы.", result.AssistantText, "multi-tool final response");
                AssertTrue(adapter.HasSheet("First") && adapter.HasSheet("Second"), "both tools executed");
                AssertEqual(5, callCount, "one rejected batch, schema read, two singleton writes and final response");
                AssertEqual(2, adapter.ExcelSheetRequests.Count(command => command.ToolId == "excel.add_sheet"), "each accepted write executes once");
                AssertEqual("excel.add_sheet", adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 2].ToolId, "first execution recorded");
                AssertEqual("First", Convert.ToString(adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 2].Arguments["name"]), "first call order");
                AssertEqual("Second", Convert.ToString(adapter.ExcelSheetRequests[adapter.ExcelSheetRequests.Count - 1].Arguments["name"]), "second call order");
                var replay = FlattenSimple(secondTurn);
                AssertEqual(3, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1,
                    "schema result and two execution results replayed");
                var activities = session.Messages
                    .Where(message => message != null && message.Activity != null && message.Activity.Kind == "tool" &&
                        string.Equals(message.Activity.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.Activity)
                    .ToList();
                AssertEqual(2, activities.Count, "two visible tool activities");
                var executedIds = activities.Select(activity => activity.ToolCallId).ToArray();
                AssertEqual(2, executedIds.Distinct().Count(), "singleton writes receive different runtime IDs");
                foreach (var id in executedIds) AssertContains(replay, id, "executed call ID is replayed");
                AssertTrue(!string.IsNullOrWhiteSpace(activities[0].StepId), "model step id stored");
                AssertTrue(activities[0].StepId != activities[1].StepId, "singleton writes belong to separate model steps");
                AssertEqual("Создаю первый лист.", activities[0].StepMessage, "only accepted step message is stored");
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
                    "{\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    LoadToolSchemaResponse("common.skills_upsert"),
                    "{\"message\":\"Сохраняю skill.\",\"tool_calls\":[{\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Все изменения применены.\",\"tool_calls\":[]}",
                    "{\"message\":\"Обычный новый ответ.\",\"tool_calls\":[]}"
                });
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                    Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }));
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "initial", TurnId = "turn", Status = "running",
                    ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion };
                var settingsForRun = new AppSettings { AutoConfirmToolActions = false };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(ChatModes.Agent, "Создай лист и skill.", session, NewContext(adapter),
                    settingsForRun, tools, null, (pendingSession, command, result) => "pending").GetAwaiter().GetResult();
                AssertTrue(first.WaitingForConfirmation, "real loop stops at confirmation");
                AssertEqual(initialHealth, first.RunViewState.ExecutionHealth, "pending cannot erase earlier execution evidence");
                AssertEqual(0, first.RunViewState.VerifiedWrites + first.RunViewState.UnverifiedWrites, "pending mutation is not a successful write");
                session.LastRun.RunId = "continuation";
                var confirmed = PendingCommand(session);
                var final = service.ConfirmAsync("pending", confirmed, session,
                    new ConversationRunInput(settingsForRun, NewContext(adapter), tools), null).GetAwaiter().GetResult();
                AssertRunViewState(final, session, "unknown", 1,
                    initialHealth == "errors" ? 1 : 0, initialHealth == "unknown" ? 1 : 0);
                AssertEqual(RunViewLifecycles.Completed, final.RunViewState.Lifecycle, "completed lifecycle does not erase errors or unknown");
                var previousFinal = session.Messages.Last();
                var next = service.ExecuteAsync(ChatModes.Agent, "Ответь без действий.", session, NewContext(adapter),
                    settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertRunViewState(next, session, "clean", 0, 0, 0);
                AssertEqual("unknown", previousFinal.RunViewState.ExecutionHealth, "new turn does not rewrite earlier evidence");
            });
        }

        private static void SimpleAgentConfirmationReplaysOnlyFinalResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.skills_upsert"),
                    "{\"message\":\"Создаю skill.\",\"tool_calls\":[" +
                    "{\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Skill сохранён.\",\"tool_calls\":[]}"
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
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
                AssertRunViewState(final, session, "unknown", 1, 0, 0);
                AssertEqual(3, session.LastRun.IterationsUsed, "confirmation continuation keeps cumulative iteration budget");
                AssertEqual(2, session.LastRun.ToolStepsUsed, "confirmed result replaces reserved logical tool step");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "RUNTIME_CONTEXT", "user-role continuation keeps runtime context");
                AssertEqual(2, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1,
                    "schema evidence and confirmed result replayed");
                AssertContains(replay, "\"status\":\"ok\"", "confirmed result replayed");
                const string runtimeMarker = "RUNTIME_CONTEXT:\n";
                var runtimeMessage = calls[2].First(message => (message.Content ?? string.Empty)
                    .IndexOf(runtimeMarker, StringComparison.Ordinal) >= 0);
                var runtimeContext = JObject.Parse(runtimeMessage.Content.Substring(
                    runtimeMessage.Content.IndexOf(runtimeMarker, StringComparison.Ordinal) + runtimeMarker.Length));
                AssertTrue(((JArray)runtimeContext["capabilities"]["optionalSchemas"])
                        .OfType<JObject>().Any(item => (string)item["id"] == "common.skills_upsert"),
                    "confirmation rematerializes the durable optional schema");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "no stale waiting result");
                var replayMessages = calls[2].ToList();
                var userIndex = replayMessages.FindIndex(message => message.Role == "user" && !message.ProtocolMessage &&
                    (message.Content ?? string.Empty).Contains("Create a test skill."));
                var callIndex = replayMessages.FindIndex(message => message.Role == "assistant" && message.ToolCallId == skillCallId);
                var resultIndex = replayMessages.FindIndex(message => message.Role != "assistant" && message.ToolCallId == skillCallId);
                AssertTrue(userIndex >= 0 && userIndex < callIndex && callIndex < resultIndex,
                    "user request, accepted call and matching result keep their order in replay");
            });
        }

        private static void SimpleAgentConfirmationFailureContinues()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.skills_upsert"),
                    "{\"message\":\"Создаю skill.\",\"tool_calls\":[{\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.failure_test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"message\":\"Skill уже существует; выберу другой id.\",\"tool_calls\":[]}"
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
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                service.ExecuteAsync(
                    ChatModes.Agent,
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_failure").GetAwaiter().GetResult();

                var command = PendingCommand(session);
                var changed = tools.Select(tool => tool.Clone()).ToList();
                changed.Single(tool => tool.Id == "common.skills_upsert").Description += " revised";
                changed.Single(tool => tool.Id == "common.skills_upsert").RequiresConfirmation = false;
                var final = service.ConfirmAsync("pending_failure", command, session,
                    new ConversationRunInput(settings, NewContext(adapter), changed), null).GetAwaiter().GetResult();

                AssertEqual("Skill уже существует; выберу другой id.", final.AssistantText, "agent continues after confirmed failure");
                AssertEqual(3, calls.Count, "schema discovery and confirmed failure trigger the next model turn");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "\"status\":\"error\"", "confirmed failure replayed");
                AssertContains(replay, "pending_tool_catalog_changed", "fingerprint failure is replayed without dispatch");
                AssertContains(replay, "TOOL_PACK_RESTORE_STATE",
                    "changed admitted schema fails closed visibly without hiding the terminal result");
                AssertContains(replay, "tool_pack_schema_changed", "restore diagnostic identifies descriptor drift");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "waiting result is not replayed after failure");
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
                        "{\"message\":\"Read twice\",\"tool_calls\":[{\"name\":\"common.resources_list\",\"arguments\":{}},{\"name\":\"common.resources_list\",\"arguments\":{}}]}" });
                    var accepted = messages.Where(message => message.Role == "assistant" &&
                        message.ToolName == "common.resources_list" && message.AcceptedCallOrigin != null).ToList();
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
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"message\":\"Done\",\"tool_calls\":[]}" });
                });
                var session = NewSession(adapter);
                var settingsForRun = new AppSettings { ToolResultRole = ToolResultRoles.Tool };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(ChatModes.Agent, "Read twice", session, NewContext(adapter), settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertEqual(2, first.RunViewState.SuccessfulReads, "both independent reads execute once");
                session = AssertKernelReplay(session);
                var next = service.ExecuteAsync(ChatModes.Agent, "Summarize", session, NewContext(adapter), settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Completed, next.RunViewState.Lifecycle, "next turn can replay the persisted batch");
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
                        Name = HtmlArtifactToolExecutor.UpsertToolId,
                        Arguments = new Dictionary<string, object>
                        {
                            ["resourceType"] = "file", ["name"] = "report.html", ["content"] = html, ["setActive"] = true
                        }
                    }
                });
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse(HtmlArtifactToolExecutor.UpsertToolId), rawWrite, rawWrite,
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
                var result = service.ExecuteAsync(ChatModes.Agent, "Save complete HTML.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentFormatRetries = 1,
                        ToolResultRole = resultRole, ContextWindowOverrideTokens = 131072 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "valid calls complete without repair");
                AssertEqual(4, requestCount, "two independently accepted writes require no extra model attempt");
                var accepted = session.Messages.Where(message => message.Role == "assistant" &&
                    message.ToolName == HtmlArtifactToolExecutor.UpsertToolId && message.AcceptedCallOrigin != null).ToList();
                AssertEqual(2, accepted.Count, "identical payloads are not deduplicated or rejected as ID collisions");
                AssertEqual(2, accepted.Select(message => message.ToolCallId).Distinct().Count(), "repeated writes have distinct runtime IDs");
                foreach (var message in accepted)
                {
                    var parsed = ConversationResponseHistoryReader.Read(message);
                    AssertTrue(parsed.Success, "accepted HTML history is valid");
                    AssertEqual(html, (string)JObject.Parse(parsed.Response.ToolCalls.Single().ArgumentsJson)["content"],
                        "ID assignment preserves every HTML character in history");
                }
                AssertEqual(html, session.HtmlWorkspace.Files.Single(file => file.Path == "report.html").Content,
                    "executor receives the complete original HTML");
                var replayed = AssertKernelReplay(session);
                AssertEqual(html, replayed.HtmlWorkspace.Files.Single(file => file.Path == "report.html").Content,
                    "durable replay retains the full HTML and its accepted IDs");
            });
        }

        private static void ModelRefusalIsTerminalInAgentAndChat()
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
                            new ConversationToolCall { Name = "common.resources_list" }
                        }),
                        RefusalContent = "Запрос отклонён провайдером."
                    });
                };

                var agentSession = NewSession(adapter);
                var agent = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Restricted request.", agentSession, NewContext(adapter), new AppSettings(),
                    new ToolDefinition[0], (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", agent.AssistantText, "agent refusal text");
                AssertEqual(RunViewLifecycles.Failed, agent.RunViewState.Lifecycle, "native refusal is locally classified by kernel");
                AssertKernelReplay(agentSession);
                AssertEqual(AgentResponseStatuses.Refused, agent.ResponseStatus,
                    "provider refusal maps from explicit transport metadata");
                AssertEqual(AgentResponseProtocol.CurrentVersion, agentSession.Messages.Last().ResponseProtocolVersion,
                    "provider refusal stores the current response protocol version");
                AssertEqual(1, calls, "agent refusal does not enter format repair");

                var chatSession = NewSession(adapter);
                chatSession.Mode = ChatModes.Chat;
                var chat = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
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
                emptySession.Mode = ChatModes.Chat;
                var empty = emptyService.ExecuteAsync(
                    ChatModes.Chat,
                    "Empty response.", emptySession, NewContext(adapter),
                    new AppSettings { MaxAgentFormatRetries = 1 }, executor.GetControllerTools().ToList(), null)
                    .GetAwaiter().GetResult();
                AssertContains(empty.AssistantText, "Ответ модели не выполнен", "chat reports bounded structured-response failure");
                AssertEqual(1, emptyCalls, "Chat limit includes the initial empty response");
            });
        }

        private static void ChatUsesReadOnlyResourceLoop()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Проверяю доступные ресурсы.\",\"tool_calls\":[{\"name\":\"common.resources_list\",\"arguments\":{}}]}",
                    "{\"message\":\"Ресурсы доступны.\",\"tool_calls\":[]}"
                });
                var captured = new List<IReadOnlyList<ChatMessage>>();
                var capturedOptions = new List<LlmRequestOptions>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    captured.Add(messages.ToList());
                    capturedOptions.Add(options);
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                var allTools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var spoofedResource = executor.GetControllerTools()
                    .Single(tool => tool.Id == ResourceToolCatalog.ReadToolId)
                    .Clone();
                spoofedResource.BuiltIn = false;
                AssertEqual(0, ConversationRunService.PrepareToolsForMode(
                    ChatModes.Chat, new[] { spoofedResource }).Count,
                    "chat rejects a non-built-in resource id spoof");
                try
                {
                    CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                        ChatModes.Agent,
                        "mismatched mode", session, NewContext(adapter), new AppSettings(), allTools, null)
                        .GetAwaiter().GetResult();
                    throw new InvalidOperationException("mode mismatch unexpectedly reached the model");
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.IndexOf("unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0) throw;
                    AssertContains(ex.Message, "does not match", "persisted Chat mode is the policy boundary");
                }
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Какие ресурсы доступны?", session, NewContext(adapter), new AppSettings(),
                    allTools, null).GetAwaiter().GetResult();

                AssertEqual("Ресурсы доступны.", result.AssistantText, "chat final response");
                AssertEqual(1, result.ToolResults.Count, "chat executes one read-only resource tool");
                AssertEqual(2, captured.Count, "resource result returns to the same conversation loop");
                var firstPrompt = FlattenSimple(captured[0]);
                AssertContains(firstPrompt, "RUNTIME_CONTEXT", "chat receives runtime context");
                AssertContains(firstPrompt, "common.resources_list", "chat receives resource discovery");
                AssertContains(firstPrompt, "common.resources_resolve", "chat receives resource resolution");
                AssertContains(firstPrompt, "common.resources_search", "chat receives resource search");
                AssertContains(firstPrompt, "common.resources_read", "chat receives resource reads");
                AssertTrue(firstPrompt.IndexOf("excel.inspect", StringComparison.OrdinalIgnoreCase) < 0,
                    "chat excludes Office tools");
                AssertTrue(firstPrompt.IndexOf("common.capabilities_read", StringComparison.OrdinalIgnoreCase) < 0 &&
                    firstPrompt.IndexOf("common.capabilities_search", StringComparison.OrdinalIgnoreCase) < 0,
                    "chat excludes capability discovery");
                AssertContains(firstPrompt, "\"capabilities\":{\"items\":[]", "chat has no capability catalog");
                AssertContains(FlattenSimple(captured[1]), "TOOL_RESULT:", "resource result is replayed");
                AssertEqual(ChatModes.Chat, capturedOptions[0].TracePurpose, "chat trace purpose");
                AssertEqual(LlmResponseFormats.JsonObject, capturedOptions[0].ResponseFormat,
                    "chat uses structured response format");
            });
        }

        private static void ChatRereadsReferencedArtifactOnDemand()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
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
                var responses = new Queue<string>(new[]
                {
                    "{\"message\":\"Читаю заметку.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + uri + "\",\"representation\":\"text\"}}]}",
                    "{\"message\":\"Первый ответ.\",\"tool_calls\":[]}",
                    "{\"message\":\"Перечитываю заметку.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + uri + "\",\"representation\":\"text\"}}]}",
                    "{\"message\":\"Второй ответ.\",\"tool_calls\":[]}"
                });
                var captured = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    captured.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var service = CreateConversationRunService(adapter, executor, completion);

                var first = service.ExecuteAsync(
                    ChatModes.Chat, "Что в заметке?", session, NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();
                foreach (var message in session.Messages.Where(message => message != null && message.ProtocolMessage))
                {
                    message.ExcludeFromModelContext = true;
                }
                var second = service.ExecuteAsync(
                    ChatModes.Chat, "Проверь ту же заметку ещё раз.", session, NewContext(adapter), new AppSettings(), tools, null)
                    .GetAwaiter().GetResult();

                AssertEqual("Первый ответ.", first.AssistantText, "first artifact answer");
                AssertEqual("Второй ответ.", second.AssistantText, "second artifact answer");
                AssertContains(FlattenSimple(captured[0]), uri, "first request keeps the canonical reference");
                AssertTrue(FlattenSimple(captured[0]).IndexOf("RESOURCE_REPLAY_SENTINEL", StringComparison.Ordinal) < 0,
                    "artifact body is absent before an explicit read");
                AssertContains(FlattenSimple(captured[1]), "RESOURCE_REPLAY_SENTINEL", "first read returns the body");
                AssertContains(FlattenSimple(captured[2]), uri, "later request still knows the canonical reference");
                AssertTrue(FlattenSimple(captured[2]).IndexOf("RESOURCE_REPLAY_SENTINEL", StringComparison.Ordinal) < 0,
                    "later request retains the reference after prior read evidence leaves context");
                AssertContains(FlattenSimple(captured[3]), "RESOURCE_REPLAY_SENTINEL", "later turn can read the body again");
            });
        }

        private static void SimpleCompactionUsesOneSummaryField()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult
                {
                    Content = "{\"summary\":\"Goal preserved; first step complete.\"}"
                });
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
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_2",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"range\":\"COMPACTION_TOOL_ARGUMENT_2\"}"
                    }
                }
            });
            foreach (var callId in new[] { "call_1", "call_2" })
            {
                var toolResult = AgentJsonProtocol.CreateToolResultMessage(
                    new ToolCommand { ToolCallId = callId, ToolId = "excel.read_range" },
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read"), ToolResultRoles.Tool);
                toolResult.RunId = "run_tool";
                session.Messages.Add(toolResult);
            }
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "I will inspect the data." });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the original formatting." });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });

            var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue(checkpoint != null, "checkpoint created");
            AssertEqual("Goal preserved; first step complete.", checkpoint.SummaryMarkdown, "summary used directly");
            var request = FlattenSimple(captured);
            AssertContains(request, "\"required\":[\"summary\"]", "single-field schema requested");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT", "native tool arguments preserved for compaction");
            AssertContains(request, "COMPACTION_TOOL_ARGUMENT_2", "all native tool calls preserved for compaction");
            AssertTrue(request.IndexOf("\"goals\"", StringComparison.Ordinal) < 0, "no fixed summary sections");
            AssertContains(
                ContextCompactionService.BuildActiveWindow(session)[0].Content,
                "Skill bodies or reference chunks present only in compacted earlier context are unavailable",
                "compacted context invalidates skill body loading");
            var activeCheckpointMessage = ContextCompactionService.BuildActiveWindow(session)[0];
            AssertTrue(activeCheckpointMessage.ResourceRefs.Any(reference => reference.Uri == compactedReference.Uri),
                "compaction deterministically carries exact resource references into the active window");
            AssertTrue(activeCheckpointMessage.ResourceRefs.Any(reference => reference.Uri == activityReference.Uri),
                "compaction carries resources produced by excluded presentation activities");
            AssertContains(HistoricalContextProjector.Project(activeCheckpointMessage).Content, compactedReference.Uri,
                "compacted resource remains visible even when the model summary omits its URI");
        }

        private static void CompactionPreservesToolProtocolPairs()
        {
            IReadOnlyList<ChatMessage> captured = null;
            LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
            {
                captured = messages.ToList();
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"Earlier context.\"}" });
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
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_ARGUMENT\"}"
                    },
                    new LlmToolCall
                    {
                        Id = "call_pair_missing",
                        Type = "function",
                        Name = "rna_excel_read_range",
                        ArgumentsJson = "{\"marker\":\"PAIR_MISSING_ARGUMENT\"}"
                    }
                }
            };
            session.Messages.Add(call);
            var result = AgentJsonProtocol.CreateToolResultMessage(
                new ToolCommand { ToolCallId = "call_pair", ToolId = "excel.read_range" },
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
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"Safe prefix.\"}" });
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
                        Name = "rna_excel_read_range",
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
