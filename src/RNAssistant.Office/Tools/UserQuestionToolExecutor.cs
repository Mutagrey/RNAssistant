using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class UserQuestionToolExecutor
    {
        public const string AskToolId = "common.questions_ask";

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(AskToolId, "Common",
                "Plan mode: Present one to three key typed questions and stop until the user answers. Use only after read-only discovery cannot resolve a material decision.",
                Schema(), mutatesLocalState: true, name: "questions_ask", scope: "session");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command)
        {
            if (command == null || !string.Equals(command.ToolId, AskToolId, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail("Unknown question tool.");
            try
            {
                var raw = ToolArgumentReader.String(command.Arguments, "questions", "[]");
                var questions = JArray.Parse(raw);
                Validate(questions);
                return ToolResult.AwaitingUser("Ответьте на ключевые вопросы плана.", new JObject
                {
                    ["type"] = "rnassistant.questions",
                    ["questionSetId"] = "questions_" + Guid.NewGuid().ToString("N"),
                    ["questions"] = questions
                }.ToString(Formatting.None));
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                return ToolResult.Fail(ex.Message, null, "invalid_questions", true);
            }
        }

        private static void Validate(JArray questions)
        {
            if (questions.Count < 1 || questions.Count > 3) throw new InvalidOperationException("questions must contain 1-3 items.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in questions)
            {
                var question = token as JObject;
                if (question == null) throw new InvalidOperationException("Each question must be an object.");
                var id = ((string)question["id"] ?? string.Empty).Trim();
                var header = ((string)question["header"] ?? string.Empty).Trim();
                var prompt = ((string)question["prompt"] ?? string.Empty).Trim();
                var selection = ((string)question["selection"] ?? string.Empty).Trim();
                var options = question["options"] as JArray;
                if (id.Length == 0 || !ids.Add(id)) throw new InvalidOperationException("Question ids must be non-empty and unique.");
                if (header.Length == 0) throw new InvalidOperationException("Question header is required.");
                if (prompt.Length == 0) throw new InvalidOperationException("Question prompt is required.");
                if (selection != "single" && selection != "multiple") throw new InvalidOperationException("Question selection must be single or multiple.");
                if (options == null || options.Count < 2 || options.Count > 4) throw new InvalidOperationException("Each question needs 2-4 options.");
                var optionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var optionToken in options)
                {
                    var option = optionToken as JObject;
                    var optionId = option == null ? string.Empty : ((string)option["id"] ?? string.Empty).Trim();
                    var label = option == null ? string.Empty : ((string)option["label"] ?? string.Empty).Trim();
                    var description = option == null ? string.Empty : ((string)option["description"] ?? string.Empty).Trim();
                    if (option == null || optionId.Length == 0 || !optionIds.Add(optionId) || label.Length == 0 || description.Length == 0)
                        throw new InvalidOperationException("Option ids must be unique and option labels/descriptions are required.");
                }
            }
        }

        private static string Schema()
        {
            var option = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable option id." },
                    ["label"] = new JObject { ["type"] = "string", ["description"] = "Short user-facing label." },
                    ["description"] = new JObject { ["type"] = "string", ["description"] = "One sentence explaining the impact." },
                    ["recommended"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Whether this is the recommended default." }
                },
                ["required"] = new JArray("id", "label", "description"),
                ["additionalProperties"] = false
            };
            var question = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = "string", ["description"] = "Stable question id." },
                    ["header"] = new JObject { ["type"] = "string", ["description"] = "Short heading." },
                    ["prompt"] = new JObject { ["type"] = "string", ["description"] = "Material decision to ask." },
                    ["selection"] = new JObject { ["type"] = "string", ["enum"] = new JArray("single", "multiple"), ["description"] = "Whether one or several options may be selected." },
                    ["allowFreeText"] = new JObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Allow an additional free-form answer." },
                    ["options"] = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 4, ["items"] = option, ["description"] = "Two to four mutually distinct answer choices." }
                },
                ["required"] = new JArray("id", "header", "prompt", "selection", "options"),
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
