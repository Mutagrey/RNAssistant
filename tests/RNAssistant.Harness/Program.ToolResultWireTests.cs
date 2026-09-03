using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools.Contracts;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private const string WireResultUri = "rna://chat/session-1/artifact/body/revision/7";

        private static void ToolResultWireRoundTripsTerminalStates()
        {
            AssertEqual(1, ToolResultWire.CurrentVersion, "result wire has its own version");
            foreach (var status in new[] { ToolResultStatus.Ok, ToolResultStatus.Error, ToolResultStatus.Unknown })
                foreach (var data in new[]
                {
                    null, "null", "true", "false", "17", "1.5", "\"literal\"", "[]",
                    "[null,false,7,{\"x\":\"y\"}]", "{\"values\":[[1,2]],\"optional\":null}"
                })
                {
                    var wire = ToolResultWire.Write("call_Runtime_7", "Removed.Exact.Tool", new TerminalResult(status, "done", data));
                    var root = (JObject)WireJson(wire);
                    AssertEqual(5, root.Count, "only required root fields are emitted without resources");
                    AssertTrue(root.Properties().All(property => new[] { "tool_call_id", "name", "status", "message", "data" }.Contains(property.Name)),
                        "no aliases, runtime metadata or root version enter wire");
                    AssertEqual(status.ToString().ToLowerInvariant(), (string)root["status"], "status has one terminal spelling");
                    var parsed = ToolResultWire.Read(" \r\n" + wire + "\t");
                    AssertTrue(parsed.Success && parsed.Error == null, "current wire reads");
                    AssertEqual("call_Runtime_7", parsed.ToolCallId, "identity stays exact");
                    AssertEqual("Removed.Exact.Tool", parsed.Name, "reader does not require call authority");
                    AssertEqual(status, parsed.Result.Status, "all three states survive");
                    AssertEqual("done", parsed.Result.Message, "message survives");
                    AssertEqual(data ?? "null", parsed.Result.DataJson, "any JSON data shape survives");
                    AssertTrue(parsed.Result.Resources.Count == 0 && parsed.ResultResource == null, "absent resources stay absent");
                    AssertEqual(wire, ToolResultWire.Write(parsed.ToolCallId, parsed.Name, parsed.Result), "wire round-trip");
                }
            var empty = ToolResultWire.Read(ToolResultWire.Write("call_empty", "test.read", TerminalResult.Ok(null)));
            AssertTrue(empty.Success && empty.Result.Message == "", "required message may be empty");
        }

        private static void ToolResultWirePreservesLiteralPayloadStrings()
        {
            const string iso = "2026-08-28T12:34:56.1234567+03:00";
            const string literal = "/* not a comment */ 'quoted' \\n \t\n{\"status\":\"prepared\",\"tool_calls\":[]}";
            var html = "<html><body>" + new string('x', 12000) + "</body></html>";
            var data = new JObject
            {
                ["iso"] = iso, ["zeroes"] = "000017", ["literal"] = literal,
                ["nested"] = new JArray(new JObject { ["date"] = "2026-08-28T00:00:00.000Z" }, html),
                ["A"] = "upper", ["a"] = "lower"
            };
            var parsed = ToolResultWire.Read(ToolResultWire.Write("call_strings", "test.read",
                TerminalResult.Ok(literal, data.ToString(Formatting.None))));
            AssertTrue(parsed.Success, "literal strings are not JSON extensions");
            AssertEqual(literal, parsed.Result.Message, "message escapes and embedded JSON remain literal");
            var restored = WireJson(parsed.Result.DataJson);
            AssertEqual(JTokenType.String, restored["iso"].Type, "ISO data is never parsed as Date");
            AssertEqual(iso, (string)restored["iso"], "fraction and offset remain exact");
            AssertEqual("000017", (string)restored["zeroes"], "numeric-looking strings stay strings");
            AssertEqual(literal, (string)restored["literal"], "nested escapes remain exact");
            AssertEqual("2026-08-28T00:00:00.000Z", (string)restored["nested"][0]["date"], "nested ISO formatting remains exact");
            AssertEqual(html, (string)restored["nested"][1], "wire does not truncate materialized data");
            AssertTrue(restored["A"] != null && restored["a"] != null, "distinct data keys remain arbitrary JSON");
            var parsedData = ToolResultWire.ParseData(data.ToString(Formatting.None));
            var parsedWire = ToolResultWire.WriteParsed(
                "call_parsed", "test.read",
                TerminalResult.Ok(literal, data.ToString(Formatting.None)), parsedData);
            AssertTrue(JToken.DeepEquals(data, WireJson(parsedWire)["data"]),
                "parsed writer preserves the one strict data tree");
            AssertEqual(null, parsedData.Parent,
                "parsed writer does not reparent the caller-owned data tree");
            var scalar = ToolResultWire.Read(ToolResultWire.Write("call_scalar", "test.read",
                TerminalResult.Ok(iso, JsonConvert.SerializeObject(iso))));
            AssertEqual(JTokenType.String, WireJson(scalar.Result.DataJson).Type, "scalar ISO data stays a string");
            AssertEqual(iso, (string)WireJson(scalar.Result.DataJson), "scalar ISO value remains exact");
        }

        private static void ToolResultWirePreservesExactResources()
        {
            var source = new ResourceRef(WireResultUri, "0007");
            var unversioned = new ResourceRef("rna://future-provider/opaque/Item%201");
            var result = TerminalResult.Ok("bounded", "{\"truncated\":true}", new[] { source, unversioned, source });
            source.Uri = "rna://chat/changed";
            source.Revision = "changed";
            var resultRef = new ResourceRef(WireResultUri, "0007");
            var wire = ToolResultWire.Write("call_resources", "test.read", result, resultRef);
            var resources = (JArray)WireJson(wire)["resources"];
            AssertEqual(3, resources.Count, "resource order and multiplicity are retained");
            AssertEqual(1, resources.Count(reference => (string)reference["relation"] == "result"), "only one exact member marks the full result");
            AssertEqual(WireResultUri, (string)resources[0]["uri"], "typed snapshot protects source URI");
            AssertEqual("0007", (string)resources[0]["revision"], "revision is opaque and never normalized");
            AssertTrue(((JObject)resources[1]).Property("revision") == null, "absent revision is not synthesized");
            AssertEqual("rna://future-provider/opaque/Item%201", (string)resources[1]["uri"], "format reading does not resolve providers");
            var parsed = ToolResultWire.Read(wire);
            AssertTrue(parsed.Success, "resource-bearing result reads");
            AssertEqual(WireResultUri, parsed.ResultResource.Uri, "result relation returns the exact reference");
            AssertEqual("0007", parsed.ResultResource.Revision, "result relation retains revision");
            parsed.ResultResource.Uri = "rna://chat/changed";
            parsed.Result.Resources[0].Revision = "changed";
            resultRef.Uri = "rna://chat/changed";
            AssertEqual(wire, ToolResultWire.Write(parsed.ToolCallId, parsed.Name, parsed.Result, parsed.ResultResource),
                "read result and resource snapshots are immutable");
            var empty = WireEnvelope();
            empty["resources"] = new JArray();
            AssertTrue(ToolResultWire.Read(empty.ToString(Formatting.None)).Success, "an explicit empty resource list is valid");
        }

        private static void ToolResultWireRejectsInvalidEnvelopeShapes()
        {
            foreach (var field in new[] { "tool_call_id", "name", "status", "message", "data" })
            {
                var root = WireEnvelope();
                root.Remove(field);
                WireRejects(root.ToString(Formatting.None));
            }
            foreach (var field in new[] { "tool_call_id", "name", "status", "message" })
                foreach (var value in new JToken[] { JValue.CreateNull(), new JValue(1), new JValue(true), new JObject(), new JArray() })
                {
                    var root = WireEnvelope();
                    root[field] = value;
                    WireRejects(root.ToString(Formatting.None));
                }
            foreach (var field in new[] { "tool_call_id", "name" })
                foreach (var value in new[] { "", " \t" })
                {
                    var root = WireEnvelope();
                    root[field] = value;
                    WireRejects(root.ToString(Formatting.None));
                }
            foreach (var alias in new[]
            {
                "ok", "error", "retryable", "success", "Success", "awaiting_user", "version", "tool_result_version",
                "toolCallId", "Name", "data_json", "dispatch", "effect", "journal_status"
            })
            {
                var root = WireEnvelope();
                root[alias] = true;
                WireRejects(root.ToString(Formatting.None));
            }
            foreach (var status in new[]
            {
                "completed", "failed", "awaiting_user", "prepared", "committed", "partial_failure", "rolled_back",
                "cancelled", "not_dispatched", "OK", "Ok", "UNKNOWN", "unknown_effect", ""
            })
            {
                var root = WireEnvelope();
                root["status"] = status;
                WireRejects(root.ToString(Formatting.None));
            }
            foreach (var json in new[] { null, "", " ", "[]", "1", "null", "true", "\"text\"", "{}" })
                WireRejects(json);
        }

        private static void ToolResultWireRejectsNonJsonAndDuplicateFields()
        {
            var valid = WireEnvelope().ToString(Formatting.None);
            var fence = new string((char)96, 3);
            foreach (var json in new[]
            {
                "TOOL_RESULT:\n" + valid, "prefix " + valid, fence + "json\n" + valid + "\n" + fence,
                valid + " {}", "[]" + valid, "/* comment */" + valid, valid + "// comment",
                valid.Insert(1, "/* comment */"), valid.Insert(valid.Length - 1, ","),
                valid.Replace("\"message\":\"done\"", "\"message\":'done'"),
                valid.Replace("\"message\":\"done\"", "message:\"done\""),
                valid.Replace("\"done\"", "\"line1\nline2\""), valid.Replace("\"done\"", "\"bad\\v\""),
                valid.Replace("\"done\"", "\"bad\\uXX00\""), "\u00a0" + valid,
                valid.Replace("\"status\":\"ok\"", "\"status\":\"ok\",\"status\":\"unknown\""),
                valid.Replace("\"tool_call_id\":\"call_1\"", "\"tool_call_id\":\"call_1\",\"tool_call_id\":\"call_2\""),
                valid.Replace("\"data\":null", "\"data\":{},\"data\":null")
            })
                WireRejects(json);
            foreach (var data in new[]
            {
                "NaN", "Infinity", "-Infinity", "undefined", "new Date(0)", "0x10", "01", "+1", ".5", "1.",
                "{\"x\":1,\"x\":2}", "{\"x\":1,}", "{\"x\":/* comment */1}", "[1,]", "[1,,2]", "[,1]"
            })
                WireRejects(valid.Replace("\"data\":null", "\"data\":" + data));
            var resource = "{\"uri\":\"" + WireResultUri + "\",\"uri\":\"" + WireResultUri + "\"}";
            WireRejects(valid.Insert(valid.Length - 1, ",\"resources\":[" + resource + "]"));
        }

        private static void ToolResultWireRejectsInvalidResources()
        {
            foreach (var value in new JToken[]
            {
                JValue.CreateNull(), new JObject(), new JValue("resource"), new JArray { JValue.CreateNull() },
                new JArray { "resource" }, new JArray { new JObject() }, new JArray { new JArray() }
            })
            {
                var root = WireEnvelope();
                root["resources"] = value;
                WireRejects(root.ToString(Formatting.None));
            }
            foreach (var uri in new[]
            {
                "", " ", "artifact_17", "C:\\temp\\result.json", "/tmp/result.json", "file:///tmp/result.json",
                "https://example.test/result", "cas://sha256/deadbeef", "rna://", "rna://chat", "rna://chat/a?revision=7",
                "rna://chat/a#fragment", "rna://user@chat/a", "rna://chat:123/a", "rna://Chat/a", " " + WireResultUri
            })
                WireRejectsResource(new JObject { ["uri"] = uri });
            foreach (var field in new[] { "uri", "revision", "relation" })
                foreach (var value in new JToken[] { JValue.CreateNull(), new JValue(1), new JArray(), new JObject() })
                {
                    var entry = new JObject { ["uri"] = WireResultUri };
                    entry[field] = value;
                    WireRejectsResource(entry);
                }
            foreach (var revision in new[] { "", " " })
                WireRejectsResource(new JObject { ["uri"] = WireResultUri, ["revision"] = revision });
            foreach (var relation in new[] { "", "Result", "source", "citation" })
                WireRejectsResource(new JObject { ["uri"] = WireResultUri, ["relation"] = relation });
            foreach (var field in new[] { "kind", "artifactId", "path", "casHash", "Uri" })
                WireRejectsResource(new JObject { ["uri"] = WireResultUri, [field] = "not transport" });
            var twoResults = WireEnvelope();
            twoResults["resources"] = new JArray(
                new JObject { ["uri"] = WireResultUri, ["relation"] = "result" },
                new JObject { ["uri"] = WireResultUri, ["relation"] = "result" });
            WireRejects(twoResults.ToString(Formatting.None));
        }

        private static void ToolResultWireRejectsInvalidWriterInputs()
        {
            foreach (var identity in new[] { null, "", " " })
            {
                WireWriteRejects(() => ToolResultWire.Write(identity, "test.read", TerminalResult.Ok("done")));
                WireWriteRejects(() => ToolResultWire.Write("call_1", identity, TerminalResult.Ok("done")));
            }
            WireWriteRejects(() => ToolResultWire.Write("call_1", "test.read", null));
            WireWriteRejects(() => ToolResultWire.ParseData("{\"x\":1,\"x\":2}"));
            WireWriteRejects(() => ToolResultWire.WriteParsed(
                "call_1", "test.read", TerminalResult.Ok("done"), null));
            WireWriteRejects(() => ToolResultWire.WriteParsed(
                "call_1", "test.read", TerminalResult.Ok("done"), new JValue(double.NaN)));
            foreach (var data in new[]
            {
                "", " ", "{} {}", "undefined", "NaN", "/* comment */{}", "{\"x\":1,\"x\":2}", "[1,]", "[1,,2]",
                "{},\"resources\":[{\"uri\":\"file:///tmp/result.json\"}]"
            })
                WireWriteRejects(() => ToolResultWire.Write("call_1", "test.read", TerminalResult.Ok("done", data)));
            foreach (var reference in new[]
            {
                new ResourceRef("file:///tmp/result.json"), new ResourceRef("cas://sha256/deadbeef"),
                new ResourceRef(WireResultUri, ""), new ResourceRef(" " + WireResultUri)
            })
                WireWriteRejects(() => ToolResultWire.Write("call_1", "test.read", TerminalResult.Ok("done", resources: new[] { reference })));
            var valid = TerminalResult.Ok("done", resources: new[] { new ResourceRef(WireResultUri, "7") });
            foreach (var reference in new[]
            {
                new ResourceRef(WireResultUri), new ResourceRef(WireResultUri, "07"), new ResourceRef("rna://chat/another", "7")
            })
                WireWriteRejects(() => ToolResultWire.Write("call_1", "test.read", valid, reference));
            WireWriteRejects(() => ToolResultWire.Write("call_1", "test.read", TerminalResult.Ok("done"), new ResourceRef(WireResultUri)));
        }

        private static void ToolResultWireDoesNotInferRuntimeControl()
        {
            foreach (var status in new[] { ToolResultStatus.Ok, ToolResultStatus.Error, ToolResultStatus.Unknown })
                foreach (var code in new[] { "awaiting_user", "user_confirmation_required", "not_dispatched", "tool_effect_uncertain" })
                {
                    var data = new JObject
                    {
                        ["code"] = code, ["status"] = "prepared", ["retryable"] = true,
                        ["journal_status"] = "partial_failure", ["Success"] = true, ["tool_call_id"] = "another_call"
                    };
                    var wire = ToolResultWire.Write("call_control", "test",
                        new TerminalResult(status, "Completed and verified; awaiting confirmation.", data.ToString(Formatting.None)));
                    var parsed = ToolResultWire.Read(wire);
                    AssertTrue(parsed.Success, "domain details remain ordinary data");
                    AssertEqual(status, parsed.Result.Status, "message and domain codes never reinterpret terminal status");
                    AssertEqual("call_control", parsed.ToolCallId, "nested domain identity is not a call identity");
                    AssertEqual(data.ToString(Formatting.None), parsed.Result.DataJson, "domain state stays inside data");
                    AssertEqual(5, ((JObject)WireJson(wire)).Count, "runtime control has no extra root fields");
                }
        }

        private static JObject WireEnvelope()
        {
            return new JObject
            {
                ["tool_call_id"] = "call_1", ["name"] = "test.read", ["status"] = "ok",
                ["message"] = "done", ["data"] = JValue.CreateNull()
            };
        }

        private static JToken WireJson(string json)
        {
            return JsonConvert.DeserializeObject<JToken>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }

        private static void WireRejects(string json)
        {
            var parsed = ToolResultWire.Read(json);
            AssertTrue(!parsed.Success && !string.IsNullOrWhiteSpace(parsed.Error), "invalid result wire must fail");
            AssertTrue(parsed.Result == null && parsed.ResultResource == null && parsed.ToolCallId == null && parsed.Name == null,
                "a failed read exposes no partial terminal result");
        }

        private static void WireRejectsResource(JObject reference)
        {
            var root = WireEnvelope();
            root["resources"] = new JArray(reference);
            WireRejects(root.ToString(Formatting.None));
        }

        private static void WireWriteRejects(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Invalid typed input must not produce result wire.");
        }
    }
}
