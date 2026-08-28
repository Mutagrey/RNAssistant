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
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolDefinition V3ReadTool(string id = "test.read")
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

        private static JObject V3Call(string id = "call_1", string name = "test.read", JObject arguments = null)
        {
            return new JObject { ["id"] = id, ["name"] = name,
                ["arguments"] = arguments ?? new JObject { ["query"] = "A" } };
        }

        private static string V3Envelope(params JObject[] calls)
        {
            return new JObject { ["message"] = "Читаю.", ["tool_calls"] = new JArray(calls) }.ToString(Formatting.None);
        }

        private static ConversationResponseParseResult ParseV3(string json, params ToolDefinition[] tools)
        {
            return new ConversationResponseParser().Parse(json, tools, tools,
                new string[0], tools.Select(tool => tool.Id));
        }

        private static void ConversationV3RoundTripsWithoutStatus()
        {
            foreach (var message in new[] { "Готово.", "", "  ", "Не удалось выполнить.\nНужен доступ.", "He said \"done\" \\ / \t" })
            {
                var json = new JObject { ["message"] = message, ["tool_calls"] = new JArray() }.ToString(Formatting.None);
                var parsed = ParseV3(json);
                AssertTrue(parsed.Success, "v3 accepts a string message without interpreting its wording");
                AssertEqual(message, parsed.Response.Message, "message remains exact");
                AssertTrue(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(parsed.Response.ToJson())), "status-free final round trip");
            }
            var tool = V3ReadTool();
            var callJson = V3Envelope(V3Call(arguments: new JObject
            {
                ["query"] = "\\u0061", ["at"] = "2026-08-28T12:34:56Z"
            }));
            var call = ParseV3(callJson, tool);
            AssertTrue(call.Success, "v3 tool parses");
            AssertTrue(call.Response.ToolCalls[0].Arguments["at"] is string, "ISO text is not silently converted to DateTime");
            AssertTrue(JToken.DeepEquals(JObject.Parse(callJson), JObject.Parse(call.Response.ToJson())), "call round trip preserves ids and arguments");
            AssertTrue(typeof(ConversationResponse).GetProperty("Status") == null, "v3 DTO has no model or universal status");
        }

        private static void ConversationV3RejectsUnknownRootFields()
        {
            foreach (var field in new[] { "status", "phase", "completed", "retry", "verified", "Message", "extra" })
            {
                var root = JObject.Parse(V3Envelope());
                root[field] = "completed";
                var parsed = ParseV3(root.ToString(Formatting.None));
                AssertTrue(!parsed.Success && parsed.Response == null, "unknown root field rejected: " + field);
                AssertContains(parsed.Error, field, "unknown field diagnostic");
            }
            foreach (var json in new[] { "{}", "{\"message\":\"x\"}", "{\"tool_calls\":[]}",
                "{\"message\":null,\"tool_calls\":[]}", "{\"message\":1,\"tool_calls\":[]}",
                "{\"message\":\"x\",\"tool_calls\":null}", "{\"message\":\"x\",\"tool_calls\":{}}" })
                AssertTrue(!ParseV3(json).Success, "missing/wrong root type rejected: " + json);
        }

        private static void ConversationV3RejectsMalformedJson()
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
                "{\"message\":\"x\",\"tool_calls\":[{\"id\":\"a\",\"name\":\"test.read\",\"arguments\":{\"query\":\"A\",\"limit\":NaN}}]}"
            }) AssertTrue(!ParseV3(json, V3ReadTool()).Success, "non-JSON or incomplete envelope rejected: " + json);

            foreach (var number in new[] { "01", "+1", ".5", "0x10", "undefined", "Infinity", "1e999", "999999999999999999999999999999999" })
            {
                var json = V3Envelope(V3Call(arguments: new JObject { ["query"] = "A", ["limit"] = "NUMBER" }))
                    .Replace("\"NUMBER\"", number);
                AssertTrue(!ParseV3(json, V3ReadTool()).Success, "non-JSON/non-finite number rejected: " + number);
            }
            var escaped = ParseV3("{\"message\":\"\\u0410\\/\\b\\f\\n\\r\\t\",\"tool_calls\":[]}");
            AssertTrue(escaped.Success, "standard Unicode and control escapes are accepted");
            var nested = V3Envelope(V3Call(arguments: new JObject { ["query"] = "A", ["deep"] = "NESTED" }))
                .Replace("\"NESTED\"", new string('[', 70) + "0" + new string(']', 70));
            AssertTrue(!ParseV3(nested, V3ReadTool()).Success, "excessive nesting is a typed parse failure");
        }

        private static void ConversationV3RequiresExactCallShape()
        {
            foreach (var field in new[] { "id", "name", "arguments" })
            {
                var call = V3Call();
                call.Remove(field);
                AssertTrue(!ParseV3(V3Envelope(call), V3ReadTool()).Success, "missing call field rejected: " + field);
            }
            foreach (var arguments in new JToken[] { JValue.CreateNull(), new JValue("{}"), new JArray(), new JValue(3) })
            {
                var call = V3Call();
                call["arguments"] = arguments;
                AssertTrue(!ParseV3(V3Envelope(call), V3ReadTool()).Success, "arguments must be an object");
            }
            var extra = V3Call();
            extra["retry"] = true;
            AssertTrue(!ParseV3(V3Envelope(extra), V3ReadTool()).Success, "extra call fields rejected");
            AssertTrue(!ParseV3(V3Envelope(V3Call(id: "  ")), V3ReadTool()).Success, "blank id rejected");
            AssertTrue(!ParseV3(V3Envelope(V3Call(name: "")), V3ReadTool()).Success, "blank name rejected");
            AssertTrue(!ParseV3(V3Envelope(V3Call(arguments: new JObject { ["query"] = "A", ["Query"] = "B" })), V3ReadTool()).Success,
                "ambiguous argument names rejected before normalization");
            AssertTrue(!ParseV3(V3Envelope(V3Call()).Replace("\"query\":\"A\"", "\"query\":\"A\",\"query\":\"B\""), V3ReadTool()).Success,
                "duplicate nested JSON property rejected");
        }

        private static void ConversationV3RequiresCallableAuthority()
        {
            var loaded = V3ReadTool();
            var unloaded = V3ReadTool("test.other");
            var parser = new ConversationResponseParser();
            var result = parser.Parse(V3Envelope(V3Call(name: unloaded.Id)), new[] { loaded }, new[] { loaded, unloaded }, new string[0], new[] { loaded.Id });
            AssertTrue(!result.Success, "known but unloaded tool cannot execute");
            AssertContains(result.Error, "Tool schema is not loaded", "unloaded tool diagnosis");
            AssertContains(result.Error, "common.capabilities_read", "explicit read recovery");
            AssertContains(result.Error, "\"id\":\"test.other\"", "recovery names exact capability");
            foreach (var name in new[] { "test.unknown", "TEST.READ", "test.read " })
            {
                result = ParseV3(V3Envelope(V3Call(name: name)), loaded);
                AssertTrue(!result.Success, "unknown/case-mismatched tool rejected");
                AssertContains(result.Error, "Unknown tool", "unknown name diagnosis");
            }
            loaded.ArgumentSchemaJson = "{}";
            AssertTrue(!ParseV3(V3Envelope(V3Call()), loaded).Success, "malformed callable schema cannot grant authority");
            AssertTrue(!parser.Parse(V3Envelope(), new ToolDefinition[0], new ToolDefinition[0], null, new string[0]).Success,
                "accepted run context must be explicit, even if empty");
            AssertTrue(!parser.Parse(V3Envelope(), new ToolDefinition[0], new ToolDefinition[0], new string[0], null).Success,
                "batch safety authority must be explicit");
        }

        private static void ConversationV3RejectsAcceptedRunIdReuse()
        {
            var tool = V3ReadTool();
            var parser = new ConversationResponseParser();
            var accepted = new HashSet<string>(new[] { "call_old" });
            var contextBefore = accepted.ToArray();
            var duplicate = parser.Parse(V3Envelope(V3Call("CALL_OLD")), new[] { tool }, new[] { tool }, accepted, new[] { tool.Id });
            AssertTrue(!duplicate.Success, "accepted run ids cannot recur with different casing");
            AssertContains(duplicate.Error, "accepted run", "run duplicate diagnosis");
            AssertTrue(!ParseV3(V3Envelope(V3Call(), V3Call("CALL_1")), tool).Success, "response-local ids are unique too");
            var rejected = parser.Parse(V3Envelope(V3Call("call_new"), V3Call("call_bad", "unknown")), new[] { tool }, new[] { tool }, accepted, new[] { tool.Id });
            AssertTrue(!rejected.Success && rejected.Response == null, "reject whole response, never partial calls");
            var corrected = parser.Parse(V3Envelope(V3Call("call_new")), new[] { tool }, new[] { tool }, accepted, new[] { tool.Id });
            AssertTrue(corrected.Success, "rejected attempts do not reserve ids");
            AssertTrue(accepted.SequenceEqual(contextBefore), "parsing never mutates the accepted-run id source");
            accepted.Add("call_new");
            AssertTrue(!parser.Parse(corrected.Response.ToJson(), new[] { tool }, new[] { tool }, accepted, new[] { tool.Id }).Success,
                "caller-accepted id is rejected in the next step");
            AssertTrue(ParseV3(corrected.Response.ToJson(), tool).Success, "fresh run has its own id scope");
        }

        private static void ConversationV3BatchesOnlyExplicitReadOnlyCalls()
        {
            var read = V3ReadTool();
            var parser = new ConversationResponseParser();
            var calls = V3Envelope(V3Call("first"), V3Call("second"));
            var accepted = ParseV3(calls, read);
            AssertTrue(accepted.Success, "independent read-only calls may be batched");
            AssertTrue(accepted.Response.ToolCalls.Select(call => call.Id).SequenceEqual(new[] { "first", "second" }), "call order is preserved");
            AssertTrue(!parser.Parse(calls, new[] { read }, new[] { read }, new string[0], new string[0]).Success,
                "absence of flags alone does not establish batch safety");

            foreach (var kind in new[] { "document", "local", "confirmation", "external", "unclassified" })
            {
                var tool = V3ReadTool("test.action");
                tool.MutatesDocument = kind == "document";
                tool.MutatesLocalState = kind == "local";
                tool.RequiresConfirmation = kind == "confirmation";
                // External/effect classification belongs to trusted execution authority,
                // not the name or legacy booleans. Missing classification fails closed.
                var batchSafe = kind == "external" || kind == "unclassified" ? new[] { read.Id } : new[] { read.Id, tool.Id };
                foreach (var batch in new[]
                {
                    V3Envelope(V3Call("a", tool.Id), V3Call("b", read.Id)),
                    V3Envelope(V3Call("a", read.Id), V3Call("b", tool.Id))
                })
                {
                    var result = parser.Parse(batch, new[] { read, tool }, new[] { read, tool }, new string[0], batchSafe);
                    AssertTrue(!result.Success, kind + " cannot be batched, regardless of position");
                    AssertContains(result.Error, "one at a time", "singleton diagnosis");
                }
                AssertTrue(parser.Parse(V3Envelope(V3Call(name: tool.Id)), new[] { tool }, new[] { tool }, new string[0], batchSafe).Success,
                    kind + " singleton is valid protocol, not execution permission");
            }
        }

        private static void ConversationV3ValidatesArgumentsBeforeAcceptance()
        {
            foreach (var arguments in new[]
            {
                new JObject(), new JObject { ["query"] = 5 }, new JObject { ["query"] = "" },
                new JObject { ["query"] = JValue.CreateNull() }, new JObject { ["query"] = "A", ["limit"] = 51 },
                new JObject { ["query"] = "A", ["limit"] = "10" }, new JObject { ["query"] = "A", ["extra"] = true }
            })
            {
                var parsed = ParseV3(V3Envelope(V3Call(arguments: arguments)), V3ReadTool());
                AssertTrue(!parsed.Success && parsed.Response == null, "argument contract violation cannot be accepted");
                AssertContains(parsed.Error, "Invalid arguments", "schema violation diagnosis");
            }
            var strictNulls = ParseV3(V3Envelope(V3Call(arguments: new JObject
            {
                ["query"] = "A", ["limit"] = JValue.CreateNull(), ["at"] = JValue.CreateNull()
            })), V3ReadTool());
            AssertTrue(strictNulls.Success, "structured-output optional nulls are accepted");
            AssertEqual(1, strictNulls.Response.ToolCalls[0].Arguments.Count, "optional nulls removed, no execution defaults applied by protocol");
        }

        private static void ConversationV3BoundsCallCount()
        {
            var tool = V3ReadTool();
            var calls = Enumerable.Range(0, 32).Select(i => V3Call("call_" + i)).ToArray();
            AssertTrue(ParseV3(V3Envelope(calls), tool).Success, "32 read-only calls accepted");
            var oversized = V3Envelope(calls.Concat(new[] { V3Call("last") }).ToArray());
            AssertTrue(!ParseV3(oversized, tool).Success, "33 calls rejected");
            var legacy = JObject.Parse(oversized);
            legacy["status"] = "in_progress";
            AssertTrue(!ConversationResponseV2Adapter.Read(legacy.ToString(Formatting.None)).Success, "read adapter keeps the same envelope bound");
        }

        private static void ConversationV2AdapterDiscardsStatus()
        {
            foreach (var status in new[] { "completed", "in_progress", "awaiting_user", "blocked", "refused", "planned" })
            {
                foreach (var v3 in new[] { V3Envelope(), V3Envelope(V3Call()) })
                {
                    var legacy = JObject.Parse(v3);
                    legacy["status"] = status;
                    var adapted = ConversationResponseV2Adapter.Read(legacy.ToString(Formatting.None));
                    AssertTrue(adapted.Success, "v2 status is only a discriminator, not lifecycle truth: " + status);
                    AssertTrue(JToken.DeepEquals(JObject.Parse(v3), JObject.Parse(adapted.Response.ToJson())),
                        "v2 adapter derives continuation only from calls and writes status-free v3");
                    AssertTrue(!ParseV3(legacy.ToString(Formatting.None), V3ReadTool()).Success, "new parser never silently falls back to v2");
                }
            }
        }

        private static void ConversationV2AdapterDoesNotGrantToolAuthority()
        {
            var legacy = JObject.Parse(V3Envelope(V3Call(name: "removed.old_tool", arguments: new JObject { ["old_arg"] = 42 })));
            legacy["status"] = "completed";
            var adapted = ConversationResponseV2Adapter.Read(legacy.ToString(Formatting.None));
            AssertTrue(adapted.Success, "historical read does not require a current catalog/schema");
            AssertEqual("removed.old_tool", adapted.Response.ToolCalls[0].Name, "historical exact name preserved without alias");
            AssertTrue(!ParseV3(adapted.Response.ToJson(), V3ReadTool()).Success,
                "read conversion does not make a removed historical tool executable");
            foreach (var status in new JToken[] { new JValue("unknown"), JValue.CreateNull(), new JValue(2) })
            {
                legacy["status"] = status;
                AssertTrue(!ConversationResponseV2Adapter.Read(legacy.ToString(Formatting.None)).Success, "only identified v2 envelopes accepted");
            }
            legacy["status"] = "completed";
            legacy["verified"] = true;
            AssertTrue(!ConversationResponseV2Adapter.Read(legacy.ToString(Formatting.None)).Success, "read adapter rejects unknown root fields");
            AssertTrue(!ConversationResponseV2Adapter.Read(V3Envelope()).Success, "v3 is not autodetected as legacy");
        }

        private static void SimpleAgentParsesFinalJson()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"completed\",\"message\":\"Готово.\",\"tool_calls\":[]}",
                new ToolDefinition[0]);
            AssertTrue(parsed.Success, "final response parses");
            AssertEqual(AgentResponseStatuses.Completed, parsed.Response.Status, "final status");
            AssertEqual("Готово.", parsed.Response.Message, "final message");
            AssertEqual(0, parsed.Response.ToolCalls.Count, "final has no tool");
        }


        private static void SimpleAgentParsesToolCall()
        {
            var tool = new ToolDefinition { Id = "excel.add_sheet" };
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\",\"values\":[[\"A\"]]}}]}",
                new[] { tool });
            AssertTrue(parsed.Success, "tool response parses");
            AssertEqual(AgentResponseStatuses.InProgress, parsed.Response.Status, "tool response status");
            AssertEqual(1, parsed.Response.ToolCalls.Count, "one tool parsed");
            AssertEqual("excel.add_sheet", parsed.Response.ToolCalls[0].Name, "tool name");
            AssertEqual("Report", Convert.ToString(parsed.Response.ToolCalls[0].Arguments["name"]), "tool argument");
            AssertTrue(parsed.Response.ToolCalls[0].Arguments["values"] is Newtonsoft.Json.Linq.JArray,
                "structured tool argument remains native JSON");
        }

        private static void SimpleAgentRequiresCompleteUniqueEnvelope()
        {
            var parser = new AgentResponseParser();
            var tool = new ToolDefinition { Id = "excel.inspect" };
            var missingStatus = parser.Parse("{\"message\":\"Готово.\",\"tool_calls\":[]}", new[] { tool });
            AssertTrue(!missingStatus.Success, "status is required");
            AssertContains(missingStatus.Error, "status", "missing status diagnostic");

            var missingCalls = parser.Parse("{\"status\":\"completed\",\"message\":\"Готово.\"}", new[] { tool });
            AssertTrue(!missingCalls.Success, "tool_calls is required");
            AssertContains(missingCalls.Error, "tool_calls", "missing tool_calls diagnostic");

            var duplicate = parser.Parse(
                "{\"status\":\"in_progress\",\"message\":\"Inspecting.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\",\"Kind\":\"selection\"}}]}",
                new[] { tool });
            AssertTrue(!duplicate.Success, "case-insensitive duplicate arguments are rejected");
            AssertContains(duplicate.Error, "duplicate", "duplicate argument diagnostic");

            var duplicateJson = parser.Parse(
                "{\"status\":\"completed\",\"message\":\"First.\",\"message\":\"Second.\",\"tool_calls\":[]}",
                new[] { tool });
            AssertTrue(!duplicateJson.Success, "duplicate JSON properties are rejected");

            var unsupportedCallField = parser.Parse(
                "{\"status\":\"in_progress\",\"message\":\"Inspecting.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.inspect\",\"arguments\":{},\"retry\":true}]}",
                new[] { tool });
            AssertTrue(!unsupportedCallField.Success, "unsupported tool-call fields are rejected");
            AssertContains(unsupportedCallField.Error, "unsupported field", "unsupported tool-call field diagnostic");
        }

        private static void SimpleAgentParsesMultipleToolCalls()
        {
            var tools = new[]
            {
                new ToolDefinition { Id = "excel.inspect" },
                new ToolDefinition { Id = "excel.read_range" }
            };
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Inspecting.\",\"tool_calls\":[" +
                "{\"id\":\"call_sheets\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}," +
                "{\"id\":\"call_range\",\"name\":\"excel.read_range\",\"arguments\":{}}]}",
                tools);
            AssertTrue(parsed.Success, "multiple tool calls parse");
            AssertEqual(2, parsed.Response.ToolCalls.Count, "both tools parsed");
            AssertEqual("call_range", parsed.Response.ToolCalls[1].Id, "call order preserved");
        }

        private static void SimpleAgentRejectsBatchedConfirmationCalls()
        {
            var tool = new ToolDefinition
            {
                Id = "common.vba_apply_patch",
                RequiresConfirmation = true
            };
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Applying patches.\",\"tool_calls\":[" +
                "{\"id\":\"call_patch_1\",\"name\":\"common.vba_apply_patch\",\"arguments\":{}}," +
                "{\"id\":\"call_patch_2\",\"name\":\"common.vba_apply_patch\",\"arguments\":{}}]}",
                new[] { tool });

            AssertTrue(!parsed.Success, "confirmation calls cannot be batched");
            AssertContains(parsed.Error, "one at a time", "batch rejection explains recovery");
            AssertContains(parsed.Error, "TOOL_RESULT", "batch rejection requires fresh result");
        }

        private static void SimpleAgentRejectsToolCallWithoutMessage()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "tool step without visible message is rejected");
            AssertContains(parsed.Error, "non-empty message", "missing step message diagnostic");
        }

        private static void SimpleAgentRejectsDuplicateToolCallIds()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Inspecting.\",\"tool_calls\":[" +
                "{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}," +
                "{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "duplicate call ids rejected");
            AssertContains(parsed.Error, "unique", "duplicate id diagnostic");

            var reused = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Inspecting.\",\"tool_calls\":[{\"id\":\"call_same\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(reused.Success, "call ids may be reused in a later response");
        }

        private static void SimpleAgentRequiresExactToolNames()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Working.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"Excel.Inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "excel.inspect" } });
            AssertTrue(!parsed.Success, "case aliases are rejected");
            AssertContains(parsed.Error, "Unknown tool", "exact name diagnostic");

            var unloaded = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Working.\",\"tool_calls\":[{\"id\":\"call_2\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                new[] { new ToolDefinition { Id = "common.capabilities_read" } },
                new[]
                {
                    new ToolDefinition { Id = "common.capabilities_read" },
                    new ToolDefinition { Id = "excel.inspect" }
                });
            AssertTrue(!unloaded.Success, "known unloaded tool is rejected before schema evidence");
            AssertContains(unloaded.Error, "Tool schema is not loaded: excel.inspect", "unloaded tool diagnostic is state-aware");
            AssertContains(unloaded.Error, "common.capabilities_read", "unloaded tool diagnostic gives exact recovery");
        }

        private static void SimpleAgentRejectsMissingToolCallId()
        {
            var parsed = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Working.\",\"tool_calls\":[{\"name\":\"excel.add_sheet\",\"arguments\":{}}]}",
                new[] { new ToolDefinition { Id = "excel.add_sheet" } });
            AssertTrue(!parsed.Success, "tool call id is required");
            AssertContains(parsed.Error, "id, name", "missing id diagnostic");
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
                    LoadToolSchemaResponse("common.html_workspace_upsert", "schema_html"),
                    "{\"status\":\"in_progress\",\"message\":\"Создаю локальный HTML.\",\"tool_calls\":[{\"id\":\"call_html\",\"name\":\"common.html_workspace_upsert\",\"arguments\":{\"resourceType\":\"file\",\"name\":\"index.html\",\"content\":\"<main>Offline</main>\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Локальный HTML готов.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };

                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    Command("excel.read_range", "sheet", "Data", "range", "A1:B2"),
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
                    "{\"status\":\"in_progress\",\"message\":\"Читаю подходящий skill.\",\"tool_calls\":[{\"id\":\"call_skill\",\"name\":\"common.capabilities_read\",\"arguments\":{\"id\":\"common.test\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Инструкции учтены.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                        var responseSchema = AgentResponseSchemaBuilder.Build(new[] { tool });
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
                    LoadToolSchemaResponse("excel.add_sheet", "schema_add_sheet"),
                    "{\"status\":\"in_progress\",\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"call_add\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    AssertEqual(LlmResponseFormats.JsonObject, options.ResponseFormat, "single response format");
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new ConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                var result = service.ExecuteAsync(
                    ChatModes.Agent,
                    "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual("Лист Report создан.", result.AssistantText, "final response");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "successful write keeps the accepted model status");
                AssertEqual("completed", result.RunStatus, "successful write completes the current run");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus, "final status enters accepted history");
                AssertRuntimeExecutionSummary(result, session, "clean", 1, 0, 0);
                AssertTrue(adapter.HasSheet("Report"), "tool executed");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.add_sheet"), "one write dispatch");
                AssertEqual(3, calls.Count, "schema read, execution, and final model turns");
                AssertTrue(FlattenSimple(calls[0]).IndexOf(
                    "\"function\":{\"name\":\"excel.add_sheet\"", StringComparison.Ordinal) < 0,
                    "domain schema is absent before discovery");
                AssertContains(FlattenSimple(calls[1]), "\"kind\":\"tool-schema\"", "schema evidence reaches model");
                var finalRequest = FlattenSimple(calls[2]);
                AssertContains(finalRequest, "TOOL_RESULT", "tool result label");
                AssertContains(finalRequest, "\"ok\":true", "tool result ok");
                AssertContains(finalRequest, "\"name\":\"excel.add_sheet\"", "tool result name");
                AssertContains(finalRequest, "\"message\":", "tool result message");
            });
        }

        // Phase 1C: loop completion and the model's text cannot certify an external effect.
        private static void SimpleAgentCharacterizesCompletedAfterWriteError()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.QueueResult("excel.add_sheet",
                    ToolResult.Fail("Write rejected before the effect.", null, "write_rejected", false));
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet", "schema_failed_write"),
                    "{\"status\":\"in_progress\",\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"failed_write\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Лист Report создан.\",\"tool_calls\":[],\"executionSummary\":{\"ExecutionHealth\":\"clean\",\"WriteOk\":1000}}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "excel.add_sheet");
                AssertEqual(false, (bool)write["success"], "write failure is preserved");
                AssertEqual("write_rejected", (string)write["errorCode"], "actual failure code is preserved");
                AssertTrue(!adapter.HasSheet("Report"), "the claimed sheet was not created");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.add_sheet"), "failed write is not retried");
                AssertContains(FlattenSimple(requests.Last()), "\"ok\":false", "the final model request saw the error");
                AssertEqual("completed", result.RunStatus, "loop completion is independent of execution health");
                AssertRuntimeExecutionSummary(result, session, "errors", 0, 1, 0);
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
                    LoadToolSchemaResponse("common.vba_write_module", "schema_unknown_write"),
                    new JObject
                    {
                        ["status"] = "in_progress",
                        ["message"] = "Обновляю модуль.",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["id"] = "unknown_write", ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    "{\"status\":\"completed\",\"message\":\"Модуль Module1 обновлён.\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Обнови модуль Module1.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                var write = result.ToolResults.Select(item => JObject.FromObject(item))
                    .Single(item => (string)item["toolId"] == "common.vba_write_module");
                AssertEqual(false, (bool)write["success"], "unverified effect is not a successful tool result");
                AssertEqual("partial_failure", (string)write["status"], "current unknown transport is partial_failure");
                AssertEqual("vba_mutation_unknown", (string)write["errorCode"], "real journal classified the divergent effect");
                AssertEqual(false, (bool)write["retryable"], "unknown write cannot be retried automatically");
                AssertEqual("unknown", (string)JObject.Parse((string)write["dataJson"])["journalStatus"], "unknown evidence reaches the loop");
                AssertEqual(VbaMutationStatuses.Unknown,
                    journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status, "durable journal also records unknown");
                AssertContains(adapter.VbaModuleCode, "\"diverged\"", "fake host state matches neither before nor intended");
                AssertEqual(1, adapter.Executed.Count(command => command.ToolId == "excel.vba_replace_module"), "unknown write is dispatched once");
                AssertContains(FlattenSimple(requests.Last()), "vba_mutation_unknown", "model receives unknown effect evidence");
                AssertEqual("completed", result.RunStatus, "loop completion is independent of execution health");
                AssertRuntimeExecutionSummary(result, session, "unknown", 0, 0, 1);
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
                    LoadToolSchemaResponse("common.vba_write_module", "trace-schema"),
                    new JObject
                    {
                        ["status"] = "in_progress", ["message"] = "Update module.",
                        ["tool_calls"] = new JArray(new JObject
                        {
                            ["id"] = "trace-write", ["name"] = "common.vba_write_module",
                            ["arguments"] = new JObject { ["moduleName"] = "Module1", ["code"] = intended }
                        })
                    }.ToString(Formatting.None),
                    "{\"status\":\"completed\",\"message\":\"Done.\",\"tool_calls\":[]}"
                }));
                var trace = new ModelTracePersistenceService(store);
                var requestCount = 0;
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
                    options.TraceSink(new LlmTraceRecord
                    {
                        Type = "response", RequestId = requestId, Purpose = "agent",
                        PayloadJson = content, PayloadContentType = "application/json"
                    });
                    return Task.FromResult(new LlmCompletionResult { Content = content });
                };
                ChatTurnResult result;
                using (RunCausalTrace.Begin(store, session))
                {
                    result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                var acceptedWrite = accepted.Single(item => ((JArray)item.Data["ToolCallIds"]).Values<string>().Contains("trace-write"));
                var toolStart = events.Single(item => item.Type == "tool.execution.started" && (string)item.Data["ToolCallId"] == "trace-write");
                var toolEnd = events.Single(item => item.Type == "tool.execution.completed" && (string)item.Data["ToolCallId"] == "trace-write");
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
                    AssertEqual("trace-write", (string)effect.Data["ToolCallId"], "domain links original call");
                    AssertEqual(mutation.Prepared.RunId, (string)effect.Data["JournalRunId"], "journal origin remains explicit");
                }
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
                AssertEqual("completed", result.RunStatus, "Phase 1B preserves existing outcome; Phase 1C owns the guard");
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
                        Content = "{\"status\":\"completed\",\"message\":\"Лист Report создан.\",\"tool_calls\":[]}"
                    });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Создай лист Report.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(1, calls, "terminal no-call response stops the loop");
                AssertEqual(0, result.ToolResults.Count, "there is no tool effect evidence");
                AssertEqual(0, adapter.Executed.Count(command => command.ToolId == "excel.add_sheet"), "no requested write was dispatched");
                AssertTrue(!adapter.HasSheet("Report"), "model text did not create a sheet");
                AssertEqual("completed", result.RunStatus, "a no-write response may finish the loop");
                AssertRuntimeExecutionSummary(result, session, "clean", 0, 0, 0);
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "no-write response carries model completed");
                AssertEqual("Лист Report создан.", session.Messages.Last().Content, "unsupported mutation claim reaches visible history");
            });
        }

        private static void AssertRuntimeExecutionSummary(
            ChatTurnResult result, ChatSession session, string health, int writeOk, int writeError, int writeUnknown)
        {
            var summary = JObject.FromObject(result)["ExecutionSummary"] as JObject;
            AssertTrue(summary != null, "runtime execution summary is required independently of model completed");
            AssertEqual(health, (string)summary["ExecutionHealth"], "runtime owns execution health");
            AssertEqual(writeOk, (int)summary["WriteOk"], "confirmed write count");
            AssertEqual(writeError, (int)summary["WriteError"], "definite write error count");
            AssertEqual(writeUnknown, (int)summary["WriteUnknown"], "uncertain write count");
            AssertTrue(JToken.DeepEquals(summary, JObject.FromObject(session.Messages.Last())["ExecutionSummary"]),
                "visible final message retains runtime summary independently of its text/status");
        }

        private static void SimpleAgentPromptIsRequestLocal()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var responses = new Queue<string>(new[]
                {
                    "{\"status\":\"in_progress\",\"message\":\"Читаю листы.\",\"tool_calls\":[{\"id\":\"call_sheets\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Готово.\",\"tool_calls\":[]}"
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
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    new LlmCompletionResult { Content = "{\"status\":\"refused\",\"message\":\"Не могу выполнить этот запрос.\",\"tool_calls\":[]}" }
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(responses.Dequeue());
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Restricted request.", session, NewContext(adapter), new AppSettings(),
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(2, requests.Count, "one repair request");
                AssertEqual("Не могу выполнить этот запрос.", result.AssistantText, "formatted refusal accepted");
                AssertEqual(AgentResponseStatuses.Refused, result.ResponseStatus, "formatted refusal status");
                var repair = requests[1].Last();
                AssertContains(repair.Content, "FORMAT_REPAIR", "repair instruction added");
                AssertContains(repair.Content, "refuse", "refusal can remain a final answer");
                AssertTrue(FlattenSimple(requests[1]).IndexOf(invalid, StringComparison.Ordinal) < 0,
                    "invalid raw response is not copied into repair prompt");
                AssertTrue(!session.Messages.Any(message =>
                    (message.Content ?? string.Empty).IndexOf(invalid, StringComparison.Ordinal) >= 0 ||
                    (message.Content ?? string.Empty).IndexOf("FORMAT_REPAIR", StringComparison.Ordinal) >= 0 ||
                    (message.ReasoningContent ?? string.Empty).IndexOf("INVALID_REASONING_SENTINEL", StringComparison.Ordinal) >= 0),
                    "invalid completion and repair instruction are not persisted");
            });
        }

        private static void SimpleAgentUsesExplicitResponseStatus()
        {
            var inspect = new ToolDefinition { Id = "excel.inspect" };
            var progressWithoutCall = new AgentResponseParser().Parse(
                "{\"status\":\"in_progress\",\"message\":\"Проверяю листы...\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(!progressWithoutCall.Success, "in_progress requires a call");
            AssertContains(progressWithoutCall.Error, "at least one", "missing call diagnostic");

            var terminalWithCall = new AgentResponseParser().Parse(
                "{\"status\":\"completed\",\"message\":\"Проверяю листы.\",\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"excel.inspect\",\"arguments\":{}}]}",
                new[] { inspect });
            AssertTrue(!terminalWithCall.Success, "terminal status rejects calls");
            AssertContains(terminalWithCall.Error, "empty", "terminal status mismatch diagnostic");

            var unknown = new AgentResponseParser().Parse(
                "{\"status\":\"done\",\"message\":\"Готово.\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(!unknown.Success, "unknown status is rejected");

            var proseIsNotAuthority = new AgentResponseParser().Parse(
                "{\"status\":\"completed\",\"message\":\"Проверяю листы...\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(proseIsNotAuthority.Success, "message wording does not override explicit status");
            var delayedReport = new AgentResponseParser().Parse(
                "{\"status\":\"completed\",\"message\":\"Анализ проекта завершен. Подготавливаю отчет о найденных проблемах и исправлениях.\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(delayedReport.Success, "mixed-language progress wording is not scanned");
            var awaiting = new AgentResponseParser().Parse(
                "{\"status\":\"awaiting_user\",\"message\":\"Укажите нужный лист\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(awaiting.Success, "awaiting_user needs no punctuation heuristic");
            var blocked = new AgentResponseParser().Parse(
                "{\"status\":\"blocked\",\"message\":\"Документ недоступен.\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(blocked.Success, "blocked terminal status parses");
            var refused = new AgentResponseParser().Parse(
                "{\"status\":\"refused\",\"message\":\"Не могу помочь с этим запросом.\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(refused.Success, "refused terminal status parses");
            var planned = new AgentResponseParser().Parse(
                "{\"status\":\"planned\",\"message\":\"План готов.\",\"tool_calls\":[]}",
                new[] { inspect });
            AssertTrue(!planned.Success, "planned is unavailable without runtime planning mode");
            AssertTrue(new AgentResponseParser().Parse(
                "{\"status\":\"planned\",\"message\":\"План готов.\",\"tool_calls\":[]}",
                new[] { inspect }, true).Success, "runtime-selected planning mode may accept planned");

            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string invalidPair = "{\"status\":\"in_progress\",\"message\":\"Проверяю листы...\",\"tool_calls\":[]}";
                var responses = new Queue<string>(new[]
                {
                    invalidPair,
                    LoadToolSchemaResponse("excel.inspect", "schema_inspect_after_repair"),
                    "{\"status\":\"in_progress\",\"message\":\"Проверяю листы.\",\"tool_calls\":[{\"id\":\"call_inspect\",\"name\":\"excel.inspect\",\"arguments\":{\"kind\":\"sheets\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Список листов проверен.\",\"tool_calls\":[]}"
                });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Проверь листы.", session, NewContext(adapter), new AppSettings(),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();

                AssertEqual(4, requests.Count, "structural repair, schema discovery, and tool continuation");
                AssertContains(requests[1].Last().Content, "at least one tool call", "repair identifies status mismatch");
                AssertEqual("Список листов проверен.", result.AssistantText, "run completes after the actual tool call");
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "terminal result status is explicit");
                AssertEqual(AgentResponseProtocol.CurrentVersion, session.Messages.Last().ResponseProtocolVersion,
                    "terminal response protocol version is persisted");
                AssertEqual(AgentResponseStatuses.Completed, session.Messages.Last().ResponseStatus,
                    "terminal response status is persisted");
                AssertTrue(!session.Messages.Any(message => string.Equals(message.Content, invalidPair, StringComparison.Ordinal)),
                    "rejected status mismatch is not persisted");

                var terminalCases = new[]
                {
                    new { Status = AgentResponseStatuses.AwaitingUser, Message = "Укажите лист" },
                    new { Status = AgentResponseStatuses.Blocked, Message = "Документ недоступен." },
                    new { Status = AgentResponseStatuses.Refused, Message = "Не могу выполнить запрос." }
                };
                foreach (var terminalCase in terminalCases)
                {
                    var terminalSession = NewSession(adapter);
                    var terminalService = new ConversationRunService(
                        adapter,
                        executor,
                        (settings, messages, options, stream, cancellationToken) => Task.FromResult(
                            new LlmCompletionResult
                            {
                                Content = JsonConvert.SerializeObject(new
                                {
                                    status = terminalCase.Status,
                                    message = terminalCase.Message,
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
                    AssertEqual(terminalCase.Status, terminalResult.ResponseStatus,
                        terminalCase.Status + " reaches the turn result");
                    AssertEqual(terminalCase.Status, terminalResult.RunStatus,
                        terminalCase.Status + " reaches the run projection");
                    AssertEqual(AgentResponseProtocol.CurrentVersion, terminalResult.ResponseProtocolVersion,
                        terminalCase.Status + " carries response protocol version");
                    AssertEqual(terminalCase.Status, terminalSession.Messages.Last().ResponseStatus,
                        terminalCase.Status + " is stored with the assistant message");
                }

                var limitedSession = NewSession(adapter);
                var limitedService = new ConversationRunService(
                    adapter,
                    executor,
                    (settings, messages, options, stream, cancellationToken) => Task.FromResult(
                        new LlmCompletionResult
                        {
                            Content = "{\"status\":\"in_progress\",\"message\":\"Проверяю ресурсы.\",\"tool_calls\":[{\"id\":\"limit_call\",\"name\":\"common.resources_list\",\"arguments\":{}}]}"
                        }));
                var limitedResult = limitedService.ExecuteAsync(
                    ChatModes.Agent,
                    "Проверяй до лимита.",
                    limitedSession,
                    NewContext(adapter),
                    new AppSettings { MaxAgentIterations = 1 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    null).GetAwaiter().GetResult();
                AssertEqual("failed", limitedResult.RunStatus,
                    "runtime step limit is not projected as model-declared completion");
                AssertTrue(string.IsNullOrWhiteSpace(limitedResult.ResponseStatus),
                    "runtime step limit has no synthetic model response status");
                AssertEqual("step_limit_reached", limitedSession.Messages.Last().Activity.ExecutionStatus,
                    "runtime step limit is stored as a diagnostic outcome");
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
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Do something.", session, NewContext(adapter), new AppSettings { MaxAgentFormatRetries = 20 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(20, requests.Count, "twenty total responses including the initial request");
                AssertContains(result.AssistantText, "после 20 попыток", "diagnostic counts total protocol responses");
                AssertEqual("failed", result.RunStatus, "all invalid responses fail the run");
                AssertRuntimeExecutionSummary(result, session, "clean", 0, 0, 0);
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
                            Content = "{\"status\":\"completed\",\"message\":\"Ответ принят.\",\"tool_calls\":[]}",
                            ReasoningContent = "ACCEPTED_REASONING"
                        });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent, "Ответь на вопрос.", session, NewContext(adapter),
                    new AppSettings { MaxAgentFormatRetries = 20 },
                    adapter.GetBuiltInTools().ToList(), null).GetAwaiter().GetResult();

                AssertEqual(20, requests.Count, "nineteen protection responses followed by one valid response");
                AssertEqual("completed", result.RunStatus, "twentieth request can complete the run");
                AssertRuntimeExecutionSummary(result, session, "clean", 0, 0, 0);
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
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    return Task.FromResult(new LlmCompletionResult { Content = "{\"status\":\"completed\",\"message\":\"Готово.\",\"tool_calls\":[]}" });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Inspect VBA.", NewSession(adapter), NewContext(adapter),
                    new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema }, tools, null, null,
                    BuiltInSkillProvider.GetSkills(adapter))
                    .GetAwaiter().GetResult();

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
                AssertTrue(!callableNames.Contains("common.vba_apply_patch", StringComparer.OrdinalIgnoreCase) &&
                    !callableNames.Contains("common.vba_write_module", StringComparer.OrdinalIgnoreCase) &&
                    !callableNames.Contains("common.vba_delete_module", StringComparer.OrdinalIgnoreCase),
                    "VBA mutation schemas are not eagerly injected");
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
                AssertTrue(!callableNames.Contains("common.office_run_macro", StringComparer.OrdinalIgnoreCase),
                    "macro schema remains progressive rather than eagerly injected");
            });
        }

        private static void SimpleAgentLoadsAndRunsArbitraryMacro()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.office_run_macro", "schema_run_macro"),
                    "{\"status\":\"in_progress\",\"message\":\"Запускаю выбранный макрос.\",\"tool_calls\":[{\"id\":\"call_macro\",\"name\":\"common.office_run_macro\",\"arguments\":{\"macroName\":\"Module1.MigrateApiKey\",\"arguments\":[\"value\",2,true]}}]}",
                    "{\"status\":\"completed\",\"message\":\"Макрос выполнен.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    LoadToolSchemaResponse("excel.add_sheet", "schema_add_sheet_batch"),
                    "{\"status\":\"in_progress\",\"message\":\"Создаю два независимых листа.\",\"tool_calls\":[" +
                    "{\"id\":\"call_first\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"First\"}}," +
                    "{\"id\":\"call_second\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Second\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Оба листа созданы.\",\"tool_calls\":[]}"
                });
                IReadOnlyList<ChatMessage> secondTurn = null;
                var progressActivities = new List<ChatActivity>();
                var callCount = 0;
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    callCount += 1;
                    if (callCount == 3) secondTurn = messages.ToList();
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var session = NewSession(adapter);
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Создай листы First и Second.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, MaxAgentIterations = 4 },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), (phase, message, activity) =>
                    {
                        if (activity != null) progressActivities.Add(activity);
                    }).GetAwaiter().GetResult();

                AssertEqual("Оба листа созданы.", result.AssistantText, "multi-tool final response");
                AssertTrue(adapter.HasSheet("First") && adapter.HasSheet("Second"), "both tools executed");
                AssertEqual("excel.add_sheet", adapter.Executed[adapter.Executed.Count - 2].ToolId, "first execution recorded");
                AssertEqual("First", Convert.ToString(adapter.Executed[adapter.Executed.Count - 2].Arguments["name"]), "first call order");
                AssertEqual("Second", Convert.ToString(adapter.Executed[adapter.Executed.Count - 1].Arguments["name"]), "second call order");
                var replay = FlattenSimple(secondTurn);
                AssertEqual(3, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1,
                    "schema result and two execution results replayed");
                AssertContains(replay, "call_first", "first call id replayed");
                AssertContains(replay, "call_second", "second call id replayed");
                var activities = session.Messages
                    .Where(message => message != null && message.Activity != null && message.Activity.Kind == "tool" &&
                        string.Equals(message.Activity.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.Activity)
                    .ToList();
                AssertEqual(2, activities.Count, "two visible tool activities");
                AssertTrue(!string.IsNullOrWhiteSpace(activities[0].StepId), "model step id stored");
                AssertEqual(activities[0].StepId, activities[1].StepId, "batch tools share one model step");
                AssertEqual("Создаю два независимых листа.", activities[0].StepMessage, "model step message stored");
                var marker = progressActivities.First(activity => activity.Kind == "step" &&
                    string.Equals(activity.Title, "Создаю два независимых листа.", StringComparison.Ordinal));
                var running = progressActivities.First(activity => activity.Kind == "tool" && activity.Status == "running" &&
                    string.Equals(activity.ToolId, "excel.add_sheet", StringComparison.OrdinalIgnoreCase));
                AssertEqual(marker.StepId, running.StepId, "live tool belongs to visible model step");
            });
        }

        private static void SimpleAgentConfirmationPreservesExecutionHealth(string initialHealth)
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.QueueResult("excel.add_sheet", ToolResult.Fail("Write did not report success", null,
                    initialHealth == "unknown" ? "tool_effect_uncertain" : "write_rejected", false));
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("excel.add_sheet", "schema_initial_write"),
                    "{\"status\":\"in_progress\",\"message\":\"Добавляю лист.\",\"tool_calls\":[{\"id\":\"write\",\"name\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}",
                    LoadToolSchemaResponse("common.skills_upsert", "schema_pending"),
                    "{\"status\":\"in_progress\",\"message\":\"Сохраняю skill.\",\"tool_calls\":[{\"id\":\"skill\",\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Все изменения применены.\",\"tool_calls\":[]}",
                    "{\"status\":\"completed\",\"message\":\"Обычный новый ответ.\",\"tool_calls\":[]}"
                });
                var service = new ConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                    Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }));
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "initial", TurnId = "turn", Status = "running" };
                var settingsForRun = new AppSettings { AutoConfirmToolActions = false };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(ChatModes.Agent, "Создай лист и skill.", session, NewContext(adapter),
                    settingsForRun, tools, null, (pendingSession, command, result) => "pending").GetAwaiter().GetResult();
                AssertTrue(first.WaitingForConfirmation, "real loop stops at confirmation");
                AssertEqual(initialHealth, first.ExecutionSummary.ExecutionHealth, "pending cannot erase earlier execution evidence");
                AssertEqual(0, first.ExecutionSummary.WriteOk, "pending mutation is not a successful write");
                var iterations = session.LastRun.IterationsUsed;
                var steps = session.LastRun.ToolStepsUsed;
                var prior = RunSummaryBuilder.ContinuationSeed(session);
                session.LastRun = new ChatRunRecord { RunId = "continuation", TurnId = "turn", ExecutionSummary = prior };
                var confirmed = new ToolCommand { ToolId = "common.skills_upsert", ToolCallId = "skill" };
                confirmed.Arguments["id"] = "common.test";
                confirmed.Arguments["description"] = "Test";
                confirmed.Arguments["bodyMarkdown"] = "# Test";
                var actual = executor.Execute(confirmed, tools, settingsForRun, false, true, session);
                AssertTrue(actual.Success, "confirmed local mutation actually succeeds");
                var builder = new RunSummaryBuilder(tools, prior);
                builder.Observe(confirmed, actual);
                builder.Publish(session);
                var final = service.ContinueAfterToolAsync(confirmed, actual, session, NewContext(adapter),
                    settingsForRun, tools, null, null, initialIterationsUsed: iterations,
                    initialToolStepsUsed: steps, summaryBuilder: builder).GetAwaiter().GetResult();
                AssertRuntimeExecutionSummary(final, session, initialHealth, 1,
                    initialHealth == "errors" ? 1 : 0, initialHealth == "unknown" ? 1 : 0);
                AssertEqual("completed", final.RunStatus, "completed lifecycle does not erase errors or unknown");
                var previousFinal = session.Messages.Last();
                var next = service.ExecuteAsync(ChatModes.Agent, "Ответь без действий.", session, NewContext(adapter),
                    settingsForRun, tools, null).GetAwaiter().GetResult();
                AssertRuntimeExecutionSummary(next, session, "clean", 0, 0, 0);
                AssertEqual(initialHealth, previousFinal.ExecutionSummary.ExecutionHealth, "new turn resets counts without rewriting earlier evidence");
            });
        }

        private static void SimpleAgentConfirmationReplaysOnlyFinalResult()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.skills_upsert", "schema_skills_upsert"),
                    "{\"status\":\"in_progress\",\"message\":\"Создаю skill.\",\"tool_calls\":[" +
                    "{\"id\":\"call_skill\",\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Skill сохранён.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new ConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { Status = "running" };
                var settings = new AppSettings { AutoConfirmToolActions = false, SystemPromptRole = "user" };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var first = service.ExecuteAsync(
                    ChatModes.Agent,
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_1").GetAwaiter().GetResult();

                AssertContains(first.AssistantText, "Создаю", "waiting response returned");
                AssertEqual("clean", first.ExecutionSummary.ExecutionHealth, "confirmation itself is not a tool error");
                AssertEqual(0, first.ExecutionSummary.WriteOk, "waiting is not an applied mutation");
                AssertTrue(!session.Messages.Any(message => message.ProtocolMessage &&
                    (message.Content ?? string.Empty).IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) >= 0),
                    "waiting result not replayed");
                AssertEqual("call_skill", session.Messages.Last(message => message.Activity != null).Activity.ToolCallId,
                    "pending activity keeps tool call id");
                var pendingActivity = session.Messages.Last(message => message.Activity != null).Activity;
                var expectedCatalogFingerprint = ConversationRunService.ToolExecutionFingerprint(
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
                        ConversationRunService.ToolExecutionFingerprint(
                            ConversationRunService.PrepareToolsForRun(changedTools),
                            "common.skills_upsert"),
                        StringComparison.OrdinalIgnoreCase),
                    "tool fingerprint changes with a replaced executable definition");
                AssertEqual(2, session.LastRun.IterationsUsed, "confirmation stores iteration cursor after discovery");
                AssertEqual(2, session.LastRun.ToolStepsUsed, "schema read and pending action consume logical tool steps");
                var initialIterationsUsed = session.LastRun.IterationsUsed;
                var initialToolStepsUsed = session.LastRun.ToolStepsUsed;
                foreach (var message in session.Messages)
                {
                    message.RunId = "initial_run";
                }

                var confirmedCommand = new ToolCommand { ToolId = "common.skills_upsert", ToolCallId = "call_skill" };
                confirmedCommand.Arguments["id"] = "common.test";
                var final = service.ContinueAfterToolAsync(
                    confirmedCommand,
                    ToolResult.Ok("Skill saved.", "{\"id\":\"common.test\"}"),
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    null,
                    null,
                    cancellationToken: CancellationToken.None,
                    initialIterationsUsed: initialIterationsUsed,
                    initialToolStepsUsed: initialToolStepsUsed).GetAwaiter().GetResult();

                AssertEqual("Skill сохранён.", final.AssistantText, "continued final response");
                AssertRuntimeExecutionSummary(final, session, "clean", 1, 0, 0);
                AssertEqual(3, session.LastRun.IterationsUsed, "confirmation continuation keeps cumulative iteration budget");
                AssertEqual(2, session.LastRun.ToolStepsUsed, "confirmed result replaces reserved logical tool step");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "RUNTIME_CONTEXT", "user-role continuation keeps runtime context");
                AssertEqual(2, replay.Split(new[] { "TOOL_RESULT:" }, StringSplitOptions.None).Length - 1,
                    "schema evidence and confirmed result replayed");
                AssertContains(replay, "\"ok\":true", "confirmed result replayed");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "no stale waiting result");
                AssertTrue(replay.IndexOf("Create a test skill.", StringComparison.Ordinal) < replay.IndexOf("call_skill", StringComparison.Ordinal),
                    "user request precedes tool call in replay");
                AssertTrue(replay.IndexOf("call_skill", StringComparison.Ordinal) < replay.LastIndexOf("TOOL_RESULT:", StringComparison.Ordinal),
                    "tool call precedes result in replay");
            });
        }

        private static void SimpleAgentConfirmationFailureContinues()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse("common.skills_upsert", "schema_skills_upsert_failure"),
                    "{\"status\":\"in_progress\",\"message\":\"Создаю skill.\",\"tool_calls\":[{\"id\":\"call_skill_failure\",\"name\":\"common.skills_upsert\",\"arguments\":{\"id\":\"common.failure_test\",\"description\":\"Test\",\"bodyMarkdown\":\"# Test\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Skill уже существует; выберу другой id.\",\"tool_calls\":[]}"
                });
                var calls = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (completionSettings, messages, options, stream, cancellationToken) =>
                {
                    calls.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var service = new ConversationRunService(adapter, executor, completion);
                var session = NewSession(adapter);
                var settings = new AppSettings { AutoConfirmToolActions = false };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                service.ExecuteAsync(
                    ChatModes.Agent,
                    "Create a test skill.", session, NewContext(adapter), settings, tools,
                    (Action<string, string, ChatActivity>)null,
                    (pendingSession, pendingCommand, result) => "pending_failure").GetAwaiter().GetResult();

                var command = new ToolCommand { ToolId = "common.skills_upsert", ToolCallId = "call_skill_failure" };
                command.Arguments["id"] = "common.failure_test";
                var failure = ToolResult.Fail(
                    "Skill already exists: common.failure_test.",
                    null,
                    "skill_already_exists",
                    false);
                var final = service.ContinueAfterToolAsync(
                    command,
                    failure,
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    null,
                    null).GetAwaiter().GetResult();

                AssertEqual("Skill уже существует; выберу другой id.", final.AssistantText, "agent continues after confirmed failure");
                AssertEqual(3, calls.Count, "schema discovery and confirmed failure trigger the next model turn");
                var replay = FlattenSimple(calls[2]);
                AssertContains(replay, "\"ok\":false", "confirmed failure replayed");
                AssertContains(replay, "skill_already_exists", "confirmed failure code replayed");
                AssertTrue(replay.IndexOf("waiting_confirmation", StringComparison.OrdinalIgnoreCase) < 0, "waiting result is not replayed after failure");
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
                        Content = string.Empty,
                        RefusalContent = "Запрос отклонён провайдером."
                    });
                };

                var agentSession = NewSession(adapter);
                var agent = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Restricted request.", agentSession, NewContext(adapter), new AppSettings(),
                    new ToolDefinition[0], (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", agent.AssistantText, "agent refusal text");
                AssertEqual(AgentResponseStatuses.Refused, agent.ResponseStatus,
                    "provider refusal maps from explicit transport metadata");
                AssertEqual(AgentResponseProtocol.CurrentVersion, agentSession.Messages.Last().ResponseProtocolVersion,
                    "provider refusal stores the current response protocol version");
                AssertEqual(1, calls, "agent refusal does not enter format repair");

                var chatSession = NewSession(adapter);
                chatSession.Mode = ChatModes.Chat;
                var chat = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Chat,
                    "Restricted request.", chatSession, NewContext(adapter), new AppSettings(),
                    executor.GetControllerTools().ToList(), (Action<string, string, ChatActivity>)null)
                    .GetAwaiter().GetResult();
                AssertEqual("Запрос отклонён провайдером.", chat.AssistantText, "chat refusal text");
                AssertEqual(AgentResponseStatuses.Refused, chat.ResponseStatus,
                    "Chat provider refusal uses the same explicit terminal status");
                AssertEqual(2, calls, "chat refusal does not enter format repair");

                var emptyCalls = 0;
                var emptyService = new ConversationRunService(adapter, executor,
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
                    "{\"status\":\"in_progress\",\"message\":\"Проверяю доступные ресурсы.\",\"tool_calls\":[{\"id\":\"chat_resources\",\"name\":\"common.resources_list\",\"arguments\":{}}]}",
                    "{\"status\":\"completed\",\"message\":\"Ресурсы доступны.\",\"tool_calls\":[]}"
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
                    .Single(tool => tool.Id == ResourceToolExecutor.ReadToolId)
                    .Clone();
                spoofedResource.BuiltIn = false;
                AssertEqual(0, ConversationRunService.PrepareToolsForMode(
                    ChatModes.Chat, new[] { spoofedResource }).Count,
                    "chat rejects a non-built-in resource id spoof");
                try
                {
                    new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                var result = new ConversationRunService(adapter, executor, completion).ExecuteAsync(
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
                    "{\"status\":\"in_progress\",\"message\":\"Читаю заметку.\",\"tool_calls\":[{\"id\":\"read_first\",\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + uri + "\",\"representation\":\"text\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Первый ответ.\",\"tool_calls\":[]}",
                    "{\"status\":\"in_progress\",\"message\":\"Перечитываю заметку.\",\"tool_calls\":[{\"id\":\"read_second\",\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + uri + "\",\"representation\":\"text\"}}]}",
                    "{\"status\":\"completed\",\"message\":\"Второй ответ.\",\"tool_calls\":[]}"
                });
                var captured = new List<IReadOnlyList<ChatMessage>>();
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                {
                    captured.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                };
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var service = new ConversationRunService(adapter, executor, completion);

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
            session.Messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = "call_1",
                Content = "{\"ok\":true,\"tool_call_id\":\"call_1\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_tool"
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = "call_2",
                Content = "{\"ok\":true,\"tool_call_id\":\"call_2\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_tool"
            });
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
            var result = new ChatMessage
            {
                Role = "developer",
                Content = "TOOL_RESULT:\n{\"ok\":true,\"tool_call_id\":\"call_pair\",\"name\":\"excel.read_range\"}",
                ProtocolMessage = true,
                RunId = "run_pair"
            };
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
    }
}
