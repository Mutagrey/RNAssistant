using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools;

internal static class Program
{
    private static readonly ToolCatalogEntry[] Tools = CreateTools();
    private static readonly ModelProtocolCallContext Context = new ModelProtocolCallContext(new[] { "probe.read" });
    private static readonly List<JObject> Cases = new List<JObject>();
    private static int _assertions;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--validate")
            {
                var parsed = Parse(File.ReadAllText(args[1]));
                Console.WriteLine(parsed.Success ? ModelProtocolWire.Write(parsed.Response) : parsed.ErrorCode + ": " + parsed.Error);
                return parsed.Success ? 0 : 1;
            }
            Check(args.Length == 0 || args.Length == 2 && args[0] == "--export", "usage: [--export directory | --validate response.json]");
            Forms();
            Calls();
            JsonSyntax();
            WriterAndStream();
            Repair();
            if (args.Length == 2)
            {
                Directory.CreateDirectory(args[1]);
                File.WriteAllText(Path.Combine(args[1], "schema.json"), ConversationResponseSchemaBuilder.Build(Tools, Context));
                File.WriteAllText(Path.Combine(args[1], "schema-no-tools.json"), ConversationResponseSchemaBuilder.Build(new ToolCatalogEntry[0], Context));
                File.WriteAllText(Path.Combine(args[1], "cases.json"), new JArray(Cases).ToString(Formatting.Indented));
                File.WriteAllText(Path.Combine(args[1], "guidance.txt"), ModelProtocolWire.ActionGuidance + " " + ModelProtocolWire.MessageGuidance);
            }
            Console.WriteLine("PASS protocol v8: " + Cases.Count + " response cases; " + _assertions + " assertions (codec, authority, immutable snapshots, streaming, repair content).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }

    private static void Forms()
    {
        foreach (var action in new[] { "execute", "reflection", "finish" })
        {
            var valid = Envelope(action);
            Case(action, valid);
            Case(action + " empty message", Change(valid, "message", ""));
            foreach (var name in new[] { "action", "reason", "message" })
            {
                var code = name == "action" ? "invalid_action" : "missing_" + name;
                Case(action + " missing " + name, Remove(valid, name), code);
                Case(action + " null " + name, Change(valid, name, JValue.CreateNull()), code);
                Case(action + " wrong type " + name, Change(valid, name, new JObject()), code);
            }
            foreach (var field in new[] { "tool_calls", "decision", "results", "id", "final", "status", "criterion_ref" })
                if (valid[field] == null) Case(action + " extra " + field, Change(valid, field, JValue.CreateNull()), "wrong_action_fields");
            foreach (var blank in new[] { "", " \t\n", "\u0085\u00a0\u1680\u2000\u2028\u202f\u205f\u3000" })
                Case(action + " blank reason " + blank.Length, Change(valid, "reason", blank), "missing_reason");
            // U+FEFF is not Char.IsWhiteSpace; independent schema must agree.
            Case(action + " FEFF reason", Change(valid, "reason", "\ufeff"));
            var reordered = new JObject(valid.Properties().Reverse().Select(property => new JProperty(property.Name, property.Value.DeepClone())));
            Case(action + " reordered", reordered);
        }
        foreach (var old in new[] { "tool", "done", "blocked", "needs_input", "Execute", "", "finish " })
            Case("invalid action " + old, Change(Envelope("finish"), "action", old), "invalid_action");
        Case("v7 envelope", new JObject { ["message"] = "later", ["action"] = "done", ["tool_calls"] = new JArray() }, "invalid_action");
        foreach (var value in new JToken[] { JValue.CreateNull(), new JArray(), new JValue(0), new JValue(" \n") })
            Case("bad decision " + value.Type, Change(Envelope("reflection"), "decision", value), "missing_decision");
        Case("missing decision", Remove(Envelope("reflection"), "decision"), "missing_decision");
        Case("missing results", Remove(Envelope("finish"), "results"), "missing_finish_results");
        foreach (var value in new JToken[] { JValue.CreateNull(), new JObject(), new JValue("[]") })
            Case("bad results " + value.Type, Change(Envelope("finish"), "results", value), "missing_finish_results");
        foreach (var assessment in new[] { "satisfied", "partially_satisfied", "unverified", "not_satisfied" })
        {
            Case("claim " + assessment, Finish(Claim(assessment)));
            Case("claim without refs " + assessment, Finish(Change(Claim(assessment), "evidence", new JArray())));
        }
        Case("one terminal supports distinct claims", Finish(Claim("satisfied"), Change(Claim("unverified"), "summary", "Another claim")));
        // Local shape validation deliberately cannot resolve whether this terminal exists.
        Case("unknown evidence is kernel concern", Finish(Change(Claim("unverified"), "evidence", new JArray("foreign-call"))));
        foreach (var field in new[] { "summary", "evidence", "assessment" })
        {
            var code = field == "assessment" ? "invalid_assessment" : "invalid_finish_results";
            Case("claim missing " + field, Finish(Remove(Claim("satisfied"), field)), code);
            Case("claim null " + field, Finish(Change(Claim("satisfied"), field, JValue.CreateNull())), code);
        }
        foreach (var bad in new JToken[] { JValue.CreateNull(), new JArray(), new JValue("C1") })
            Case("bad claim " + bad.Type, Finish(bad), "invalid_finish_results");
        Case("criterion forbidden", Finish(Change(Claim("satisfied"), "criterion", "C1")), "invalid_finish_results");
        Case("empty summary", Finish(Change(Claim("satisfied"), "summary", " \t")), "invalid_finish_results");
        foreach (var bad in new JToken[] { new JValue("call-1"), new JArray(" "), new JArray(1), new JArray(JValue.CreateNull()) })
            Case("bad evidence " + bad.ToString(Formatting.None), Finish(Change(Claim("satisfied"), "evidence", bad)), "invalid_finish_results");
        Case("duplicate evidence", Finish(Change(Claim("satisfied"), "evidence", new JArray("call-1", "call-1"))), "duplicate_evidence");
        foreach (var bad in new JToken[] { new JValue("ok"), new JValue("Satisfied"), new JValue(0) })
            Case("bad assessment " + bad, Finish(Change(Claim("satisfied"), "assessment", bad)), "invalid_assessment");
    }

    private static void Calls()
    {
        var execute = Envelope("execute");
        Case("missing calls", Remove(execute, "tool_calls"), "empty_execute_calls");
        foreach (var value in new JToken[] { new JArray(), JValue.CreateNull(), new JObject(), new JValue("[]") })
            Case("empty calls " + value.Type, Change(execute, "tool_calls", value), "empty_execute_calls");
        foreach (var count in new[] { 2, 32, 33 })
            Case(count + " reads", Change(execute, "tool_calls", new JArray(Enumerable.Range(0, count).Select(i => Call("probe.read")))), count > 32 ? "too_many_calls" : null);
        Case("single mutation", Change(execute, "tool_calls", new JArray(Call("probe.write"))));
        Case("mutation batch", Change(execute, "tool_calls", new JArray(Call("probe.write"), Call("probe.write"))), "invalid_batch");
        Case("mixed batch", Change(execute, "tool_calls", new JArray(Call("probe.read"), Call("probe.write"))), "invalid_batch");
        foreach (var field in new[] { "id", "tool_call_id", "status" })
            Case("call extra " + field, Change(execute, "tool_calls", new JArray(Change(Call("probe.read"), field, "model-id"))), "invalid_tool_call");
        foreach (var field in new[] { "name", "arguments" })
        {
            Case("call missing " + field, Change(execute, "tool_calls", new JArray(Remove(Call("probe.read"), field))), "invalid_tool_call");
            Case("call null " + field, Change(execute, "tool_calls", new JArray(Change(Call("probe.read"), field, JValue.CreateNull()))), "invalid_tool_call");
        }
        Case("call array arguments", Change(execute, "tool_calls", new JArray(Change(Call("probe.read"), "arguments", new JArray()))), "invalid_tool_call");
        Case("unknown tool", Change(execute, "tool_calls", new JArray(Call("probe.unknown"))), "unknown_tool");
        Case("schema not loaded", Change(execute, "tool_calls", new JArray(Call("probe.unloaded"))), "schema_not_loaded");
        foreach (var args in new[] { new JObject(), new JObject { ["kind"] = "wrong" }, new JObject { ["kind"] = "sheets", ["extra"] = 1 }, new JObject { ["arguments"] = new JObject { ["kind"] = "sheets" } } })
            Case("bad dynamic args " + args, Change(execute, "tool_calls", new JArray(Change(Call("probe.read"), "arguments", args))), "invalid_arguments");
        var optional = Change(Call("probe.optional"), "arguments", new JObject { ["kind"] = "sheets", ["limit"] = JValue.CreateNull() });
        var response = Case("nullable optional transport", Change(execute, "tool_calls", new JArray(optional)));
        Check(response.ToolCalls[0].Arguments.Property("limit") == null, "optional null is removed before runtime");
        // Existing strict transport requires nullable optionals; json_object may omit them.
        Case("omitted optional logical form", Change(execute, "tool_calls", new JArray(Call("probe.optional"))), schemaValid: false);
        Check(!Parse(execute.ToString(Formatting.None), null).Success, "missing local authority fails closed");
        var strictContext = new ModelProtocolCallContext(new string[0]);
        Check(!Parse(Change(execute, "tool_calls", new JArray(Call("probe.read"), Call("probe.read"))).ToString(Formatting.None), strictContext).Success, "read name does not grant batching");
        var bad = new ToolCatalogEntry { Id = "probe.broken", ArgumentSchemaJson = "{}" };
        var parsed = ModelProtocolWire.Parse(Change(execute, "tool_calls", new JArray(Call(bad.Id))).ToString(Formatting.None), new[] { bad }, new[] { bad }, Context);
        Check(parsed.ErrorCode == "invalid_tool_schema", "invalid captured schema rejected");
        var empty = ModelProtocolWire.Parse(execute.ToString(Formatting.None), new ToolCatalogEntry[0], new ToolCatalogEntry[0], Context);
        Check(empty.ErrorCode == "unknown_tool", "execute unavailable with empty catalog");
    }

    private static void JsonSyntax()
    {
        var json = Envelope("finish").ToString(Formatting.None);
        foreach (var raw in new[] { "```json\n" + json + "\n```", json + json, json + " trailing", json.Insert(1, "// comment\n"), json.Insert(json.Length - 1, ","), json.Replace("\"action\"", "'action'"), json.Replace("\"action\"", "action"), json.Replace("\"reason\":\"Reason\"", "\"reason\":NaN"), json.Replace("\"reason\":\"Reason\"", "\"reason\":undefined"), json.Insert(1, "\"reason\":\"duplicate\","), json.Replace("Reason", "bad\\x41"), json.Replace("Reason", "line\nbreak") })
            Case("strict json " + Cases.Count, raw, "invalid_json", syntaxOnly: true);
        var duplicateArg = Envelope("execute").ToString(Formatting.None).Replace("\"kind\":\"sheets\"", "\"kind\":\"sheets\",\"kind\":\"sheets\"");
        Case("duplicate argument", duplicateArg, "invalid_json", syntaxOnly: true);
        var nested = new string('[', 70) + "0" + new string(']', 70);
        Case("depth limit", json.Replace("\"results\":[]", "\"results\":" + nested), "invalid_json", syntaxOnly: true);
    }

    private static void WriterAndStream()
    {
        Check(ConversationResponse.ProtocolVersion == 8, "integer protocol version");
        foreach (var value in new[] { Envelope("execute"), Envelope("reflection"), Finish(Claim("unverified")) })
        {
            value["message"] = "Готово\n\\path \"{}\" 😀";
            var response = Parse(value.ToString(Formatting.None)).Response;
            var wire = ModelProtocolWire.Write(response);
            var root = JObject.Parse(wire);
            Check(root.Properties().First().Name == "action" && root.Properties().Skip(1).First().Name == "reason" && root.Properties().Last().Name == "message", "canonical field order " + response.Action);
            Check(ModelProtocolWire.Write(Parse(wire).Response) == wire, "round trip " + response.Action);
            Stream(wire, response.Message);
            value["message"] = "";
            Stream(value.ToString(Formatting.None), "");
        }
        Stream("{\"action\":\"reflection\",\"reason\":\"message hidden\",\"decision\":\"hidden\",\"message\":\"\\uD83D\\uDE00\"}", "😀");
        Stream("{\"action\":\"execute\",\"reason\":\"hidden\",\"tool_calls\":[{\"name\":\"probe.read\",\"arguments\":{\"message\":\"hidden { }\"}}],\"message\":\"visible\"}", "visible");
        var draft = Call("probe.read");
        var call = new ConversationToolCall { Name = (string)draft["name"], Arguments = (JObject)draft["arguments"] };
        var snapshot = new ConversationResponse("execute", "Reason", "", calls: new[] { call });
        var before = snapshot.ToJson();
        call.Arguments["kind"] = "changed";
        snapshot.ToolCalls[0].Arguments["kind"] = "changed-again";
        snapshot.ToolCalls[0].Name = "changed";
        Check(snapshot.ToJson() == before, "accepted call snapshot is immutable");
        Throws(() => new ConversationResponse("finish", "Reason", "", results: null), "writer cannot omit results");
        Throws(() => new ConversationResponse("execute", "Reason", "", calls: new ConversationToolCall[0]), "writer cannot emit empty execute");
        Throws(() => new ConversationResponse("reflection", "Reason", "", decision: " "), "writer cannot omit decision");
        Throws(() => new ConversationResponse("reflection", "Reason", "", decision: "read", calls: new ConversationToolCall[0]), "writer cannot leak empty calls to reflection");
        Throws(() => new ConversationResultClaim("ok", new[] { "call-1", "call-1" }, ConversationAssessment.Satisfied), "claim cannot duplicate evidence");
    }

    private static void Stream(string raw, string expected)
    {
        for (var split = 0; split <= raw.Length; split++)
        {
            var reader = new ConversationMessageStreamExtractor();
            var output = reader.Add(raw.Substring(0, split)) + reader.Add(raw.Substring(split)) + reader.Complete();
            Check(output == expected && reader.MessageCompleted, "stream at split " + split);
        }
        var incremental = new ConversationMessageStreamExtractor();
        var beforeMessage = raw.LastIndexOf("\"message\":", StringComparison.Ordinal);
        var all = "";
        for (var i = 0; i < raw.Length; i++)
        {
            var part = incremental.Add(raw[i].ToString());
            if (i < beforeMessage) Check(part == "", "reason/decision/calls do not stream");
            all += part;
        }
        Check(all + incremental.Complete() == expected, "character streaming");
    }

    private static void Repair()
    {
        foreach (ConversationResponseError kind in Enum.GetValues(typeof(ConversationResponseError)))
        {
            if (kind == ConversationResponseError.None) continue;
            var content = ModelProtocolWire.FormatRepairContent(kind, new string('x', 2000), 2, 3);
            var root = JObject.Parse(content.Substring("FORMAT_REPAIR:\n".Length));
            Check((string)root["code"] == ConversationResponseParseResult.Code(kind), "repair exact code " + kind);
            Check(((string)root["error"]).Length == 1024 + "...[truncated]".Length, "bounded repair error");
            Check((int)root["attempt"] == 2 && (int)root["max_attempts"] == 3, "repair preserves budget information");
            Check(((string)root["instruction"]).Contains(ModelProtocolWire.ActionGuidance), "repair uses canonical guidance");
            Check(!content.Contains("conversation-response-v7") && !content.Contains("action=done"), "no old wire instructions");
        }
    }

    private static ConversationResponse Case(string name, JObject value, string code = null, bool? schemaValid = null)
    { return Case(name, value.ToString(Formatting.None), code, false, schemaValid); }

    private static ConversationResponse Case(string name, string raw, string code = null, bool syntaxOnly = false, bool? schemaValid = null)
    {
        var parsed = Parse(raw);
        Check(parsed.ErrorCode == code && parsed.Success == (code == null), name + ": expected " + (code ?? "accepted") + ", got " + parsed.ErrorCode + " " + parsed.Error);
        Cases.Add(new JObject { ["name"] = name, ["raw"] = raw, ["parser_valid"] = code == null, ["schema_valid"] = schemaValid ?? code == null, ["syntax_only"] = syntaxOnly, ["error_code"] = code });
        return parsed.Response;
    }

    private static ConversationResponseParseResult Parse(string raw)
    { return Parse(raw, Context); }
    private static ConversationResponseParseResult Parse(string raw, ModelProtocolCallContext context)
    { return ModelProtocolWire.Parse(raw, Tools, Tools.Concat(new[] { new ToolCatalogEntry { Id = "probe.unloaded", Exposure = ToolExposure.Skill } }), context); }
    private static JObject Change(JObject value, string name, JToken field)
    { var clone = (JObject)value.DeepClone(); clone[name] = field.DeepClone(); return clone; }
    private static JObject Remove(JObject value, string name)
    { var clone = (JObject)value.DeepClone(); clone.Property(name).Remove(); return clone; }
    private static JObject Envelope(string action)
    {
        var root = new JObject { ["action"] = action, ["reason"] = "Reason" };
        if (action == "execute") root["tool_calls"] = new JArray(Call("probe.read"));
        else if (action == "reflection") root["decision"] = "Read the current state before changing it.";
        else root["results"] = new JArray();
        root["message"] = "";
        return root;
    }
    private static JObject Call(string name)
    { return new JObject { ["name"] = name, ["arguments"] = new JObject { ["kind"] = "sheets" } }; }
    private static JObject Claim(string assessment)
    { return new JObject { ["summary"] = "Sheet inspected", ["evidence"] = new JArray("call-1"), ["assessment"] = assessment }; }
    private static JObject Finish(params JToken[] claims)
    { return Change(Envelope("finish"), "results", new JArray(claims)); }
    private static void Check(bool condition, string name)
    { _assertions++; if (!condition) throw new InvalidOperationException(name); }
    private static void Throws(Action action, string name)
    { try { action(); } catch (ArgumentException) { _assertions++; return; } throw new InvalidOperationException(name); }
    private static ToolCatalogEntry[] CreateTools()
    {
        var schema = new JObject
        {
            ["type"] = "object", ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("sheets", "charts"), ["description"] = "Object kind to inspect." }
            },
            ["required"] = new JArray("kind"), ["additionalProperties"] = false
        };
        var optionalSchema = (JObject)schema.DeepClone();
        optionalSchema["properties"]["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10, ["description"] = "Maximum items." };
        return new[]
        {
            new ToolCatalogEntry { Id = "probe.read", ArgumentSchemaJson = schema.ToString(Formatting.None) },
            new ToolCatalogEntry { Id = "probe.write", MutatesDocument = true, ArgumentSchemaJson = schema.ToString(Formatting.None) },
            new ToolCatalogEntry { Id = "probe.optional", ArgumentSchemaJson = optionalSchema.ToString(Formatting.None) }
        };
    }
}
