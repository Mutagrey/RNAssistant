using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class UserQuestionToolCatalog
    {
        public const string AskToolId = "common.questions_ask";

        internal static IEnumerable<ToolCatalogEntry> GetTools()
        {
            var descriptor = new ToolDescriptor(AskToolId,
                "Plan mode: Present one to three key typed questions and stop until the user answers. Use only after read-only discovery cannot resolve a material decision.",
                Schema());
            var policy = new ToolPolicy(ToolEffect.Read,
                ToolVerification.None, false, false,
                new[] { "plan" }, 0);
            yield return ControllerToolCatalogEntry.CreateTypedProjection(
                descriptor, policy, name: "questions_ask", scope: "session",
                mutatesLocalState: true);
        }

        internal static void Validate(JArray questions)
        {
            if (questions.Count < 1 || questions.Count > 3) throw new InvalidOperationException("questions must contain 1-3 items.");
            var prompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in questions)
            {
                var question = token as JObject;
                if (question == null) throw new InvalidOperationException("Each question must be an object.");
                var header = ((string)question["header"] ?? string.Empty).Trim();
                var prompt = ((string)question["prompt"] ?? string.Empty).Trim();
                var selection = ((string)question["selection"] ?? string.Empty).Trim();
                var options = question["options"] as JArray;
                if (header.Length == 0) throw new InvalidOperationException("Question header is required.");
                if (prompt.Length == 0 || !prompts.Add(prompt))
                    throw new InvalidOperationException("Question prompts must be non-empty and unique.");
                if (selection != "single" && selection != "multiple") throw new InvalidOperationException("Question selection must be single or multiple.");
                if (options == null || options.Count < 2 || options.Count > 4) throw new InvalidOperationException("Each question needs 2-4 options.");
                var optionLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var optionToken in options)
                {
                    var option = optionToken as JObject;
                    var label = option == null ? string.Empty : ((string)option["label"] ?? string.Empty).Trim();
                    var description = option == null ? string.Empty : ((string)option["description"] ?? string.Empty).Trim();
                    if (option == null || label.Length == 0 || !optionLabels.Add(label) || description.Length == 0)
                        throw new InvalidOperationException("Option labels must be unique and labels/descriptions are required.");
                }
            }
        }

        internal static string Schema()
        {
            var option = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["label"] = new JObject { ["type"] = "string", ["description"] = "Short user-facing label." },
                    ["description"] = new JObject { ["type"] = "string", ["description"] = "One sentence explaining the impact." },
                    ["recommended"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Whether this is the recommended default." }
                },
                ["required"] = new JArray("label", "description"),
                ["additionalProperties"] = false
            };
            var question = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["header"] = new JObject { ["type"] = "string", ["description"] = "Short heading." },
                    ["prompt"] = new JObject { ["type"] = "string", ["description"] = "Material decision to ask." },
                    ["selection"] = new JObject { ["type"] = "string", ["enum"] = new JArray("single", "multiple"), ["description"] = "Whether one or several options may be selected." },
                    ["allowFreeText"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Allow an additional free-form answer." },
                    ["options"] = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 4, ["items"] = option, ["description"] = "Two to four mutually distinct answer choices." }
                },
                ["required"] = new JArray("header", "prompt", "selection", "options"),
                ["additionalProperties"] = false
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["questions"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 3, ["items"] = question, ["description"] = "One to three material decisions that block a reliable plan." } },
                ["required"] = new JArray("questions"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
