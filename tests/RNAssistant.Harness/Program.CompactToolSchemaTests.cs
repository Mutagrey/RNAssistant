using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CompactExcelFormatSchemaPreservesArguments()
        {
            var tool = OfficeToolCatalog.ForHost("Excel").Single(item => item.Id == "excel.range.format");
            JObject schema;
            string error;
            AssertTrue(ToolSchemaSupport.TryParse(tool, out schema, out error), "compact format schema parses: " + error);
            var before = schema.ToString(Formatting.None);
            var options = JObject.Parse("{numberFormat:'#,##0',bold:false,italic:true,fillColor:'#FFF2CC',fontColor:'#1F1F1F',horizontalAlignment:'center',autoFit:'both'}");
            // Reconstruct the shipped full branches independently of the new
            // required-fragment expansion; property definitions are unchanged.
            var legacy = (JObject)schema.DeepClone();
            legacy["anyOf"] = new JArray(options.Properties().Select(option => new JObject
            {
                ["type"] = "object", ["properties"] = schema["properties"].DeepClone(),
                ["required"] = new JArray(option.Name), ["additionalProperties"] = false
            }));
            var legacyTool = tool.Clone();
            legacyTool.ArgumentSchemaJson = legacy.ToString(Formatting.None);
            var prompt = ToolSchemaSupport.ForPrompt(schema);
            AssertTrue(JToken.DeepEquals(schema, prompt), "prompt retains the common closed object and requirement fragments");
            AssertTrue(prompt.ToString(Formatting.None).Length < ToolSchemaSupport.ForPrompt(legacy).ToString(Formatting.None).Length / 3,
                "repeated field definitions are removed from the prompt schema");
            var context = new ModelProtocolCallContext(new string[0]);
            var parser = new ConversationResponseParser();
            var strict = ToolSchemaSupport.ForStructuredOutput(schema);
            AssertTrue(JToken.DeepEquals(ToolSchemaSupport.ForStructuredOutput(legacy), strict),
                "strict output keeps exactly the previous constraints and nullable fields");
            foreach (var mode in new[] { AgentResponseModes.JsonObject, AgentResponseModes.JsonSchema })
            {
                var oldRequest = ModelProtocolWire.CreateRequestOptions(mode, new[] { legacyTool }, context);
                var newRequest = ModelProtocolWire.CreateRequestOptions(mode, new[] { tool }, context);
                AssertEqual(oldRequest.ResponseSchemaJson, newRequest.ResponseSchemaJson,
                    mode + " preserves the actual provider response contract");
            }

            Action<JObject> compare = arguments =>
            {
                foreach (var defaults in new[] { false, true })
                {
                    var oldArgs = (JObject)arguments.DeepClone();
                    var newArgs = (JObject)arguments.DeepClone();
                    var oldValid = ToolSchemaSupport.ValidateArguments(oldArgs, legacy, defaults, out error);
                    var newValid = ToolSchemaSupport.ValidateArguments(newArgs, schema, defaults, out error);
                    AssertEqual(oldValid, newValid, "runtime acceptance unchanged: " + arguments);
                    if (oldValid) AssertTrue(JToken.DeepEquals(oldArgs, newArgs), "accepted arguments/defaults unchanged");
                }
                var wire = ModelProtocolWire.WriteAcceptedCall("Validate format arguments.", "",
                    new ConversationToolCall { Name = tool.Id, Arguments = arguments });
                var oldParsed = parser.Parse(wire, new[] { legacyTool }, new[] { legacyTool }, context);
                var newParsed = parser.Parse(wire, new[] { tool }, new[] { tool }, context);
                AssertEqual(oldParsed.Success, newParsed.Success, "parser acceptance unchanged: " + arguments);
                if (oldParsed.Success)
                    AssertTrue(JToken.DeepEquals(oldParsed.Response.ToolCalls[0].Arguments, newParsed.Response.ToolCalls[0].Arguments),
                        "optional-null normalization preserves exact intent");
            };
            var names = options.Properties().Select(item => item.Name).ToArray();
            for (var mask = 0; mask < 1 << names.Length; mask++)
            {
                var arguments = new JObject { ["sheet"] = "Data", ["address"] = "A1" };
                for (var bit = 0; bit < names.Length; bit++)
                    if ((mask & (1 << bit)) != 0) arguments[names[bit]] = options[names[bit]].DeepClone();
                AssertEqual(mask != 0, ToolSchemaSupport.ValidateArguments((JObject)arguments.DeepClone(), schema, false, out error),
                    "at least one formatting option, including false, is required");
                compare(arguments);
                var strictArguments = (JObject)arguments.DeepClone();
                foreach (var name in names) if (strictArguments[name] == null) strictArguments[name] = JValue.CreateNull();
                AssertEqual(mask != 0, ToolSchemaSupport.ValidateArguments(strictArguments, strict, false, out error),
                    "strict output requires a real option, not all-null placeholders");
                compare(strictArguments);
                arguments.Remove("sheet"); arguments.Remove("address");
                compare(arguments);
            }
            foreach (var json in new[] {
                "{}", "{bold:'false'}", "{bold:0}", "{bold:null}", "{numberFormat:null}",
                "{autoFit:'all'}", "{horizontalAlignment:'justify'}", "{bold:false,unknown:true}",
                "{bold:false,sheet:1}", "{bold:false,address:[]}", "{bold:false,italic:null}",
                "{numberFormat:'',bold:false,autoFit:null}" }) compare(JObject.Parse(json));
            foreach (var property in ((JObject)schema["properties"]).Properties())
            foreach (var invalid in new JToken[] { new JObject(), new JArray(), new JValue(42) })
            {
                var arguments = (JObject)options.DeepClone(); arguments[property.Name] = invalid.DeepClone();
                AssertTrue(!ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error),
                    "shared property types reject invalid " + property.Name);
                compare(arguments);
            }
            AssertEqual(before, schema.ToString(Formatting.None), "validation and projections leave source unchanged");
            Console.WriteLine("format schema chars: runtime " + legacy.ToString(Formatting.None).Length + " -> " + before.Length +
                "; prompt " + ToolSchemaSupport.ForPrompt(legacy).ToString(Formatting.None).Length + " -> " + prompt.ToString(Formatting.None).Length +
                "; strict unchanged " + strict.ToString(Formatting.None).Length + "; revision " + CapabilityCatalogService.Revision(tool));

            var inherited = (JObject)schema.DeepClone(); inherited["required"] = new JArray("address");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(JObject.Parse("{bold:false}"), inherited, false, out error),
                "branch requirements retain parent requirements");
            AssertTrue(ToolSchemaSupport.ValidateArguments(JObject.Parse("{address:'A1',bold:false}"), inherited, false, out error),
                "parent and branch requirements can be satisfied together");
            inherited["properties"]["address"]["default"] = "A1";
            var withDefault = JObject.Parse("{bold:false}");
            AssertTrue(ToolSchemaSupport.ValidateArguments(withDefault, inherited, true, out error) && (string)withDefault["address"] == "A1",
                "shared parent defaults are applied only when requested");

            foreach (var mutation in new Action<JObject>[] {
                value => value["anyOf"][0]["required"] = new JArray("unknown"),
                value => value["anyOf"][0]["required"] = new JArray(42),
                value => value["anyOf"][0]["required"] = new JArray("bold", "bold") })
            {
                var invalid = (JObject)schema.DeepClone(); mutation(invalid);
                var invalidTool = tool.Clone(); invalidTool.ArgumentSchemaJson = invalid.ToString(Formatting.None);
                JObject parsed;
                AssertTrue(!ToolSchemaSupport.TryParse(invalidTool, out parsed, out error), "malformed required fragments fail schema admission");
            }
        }
    }
}
