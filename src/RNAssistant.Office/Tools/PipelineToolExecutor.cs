using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class PipelineToolExecutor
    {
        internal delegate SkillResult CommandRunner(SkillCommand command, IReadOnlyList<SkillDefinition> skills, AppSettings settings, int depth, bool dryRun, bool manualRun);

        public SkillResult Execute(
            SkillDefinition tool,
            SkillCommand command,
            IReadOnlyList<SkillDefinition> skills,
            AppSettings settings,
            int depth,
            bool dryRun,
            bool manualRun,
            CommandRunner runCommand)
        {
            if (string.IsNullOrWhiteSpace(tool.PipelineJson))
            {
                return SkillResult.Fail("Tool has no pipeline: " + tool.Id);
            }

            JObject pipeline;
            try
            {
                pipeline = JObject.Parse(tool.PipelineJson);
            }
            catch (JsonException ex)
            {
                return SkillResult.Fail("Invalid pipeline JSON for " + tool.Id + ": " + ex.Message);
            }

            var steps = pipeline["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                return SkillResult.Fail("Pipeline has no steps: " + tool.Id);
            }

            var stepResults = new Dictionary<string, SkillResult>(StringComparer.OrdinalIgnoreCase);
            var output = new List<object>();
            foreach (var stepToken in steps)
            {
                var step = stepToken as JObject;
                if (step == null)
                {
                    continue;
                }

                var toolId = (string)(step["toolId"] ?? step["skillId"] ?? step["id"]);
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    return SkillResult.Fail("Pipeline step has no toolId.");
                }

                var stepId = (string)step["id"];
                if (string.IsNullOrWhiteSpace(stepId))
                {
                    stepId = toolId;
                }

                var nested = new SkillCommand { SkillId = toolId };
                var args = step["arguments"] as JObject;
                if (args != null)
                {
                    foreach (var property in args.Properties())
                    {
                        nested.Arguments[property.Name] = ResolvePipelineValue(property.Value, command.Arguments, stepResults);
                    }
                }

                var result = runCommand(nested, skills, settings, depth + 1, dryRun, manualRun);
                stepResults[stepId] = result;
                output.Add(new { id = stepId, toolId = toolId, success = result.Success, message = result.Message, dataJson = result.DataJson });

                if (!result.Success)
                {
                    return SkillResult.Fail("Pipeline step failed: " + stepId + ". " + result.Message);
                }
            }

            return SkillResult.Ok((dryRun ? "Dry run completed: " : "Pipeline executed: ") + tool.Id, JsonConvert.SerializeObject(new { toolId = tool.Id, dryRun = dryRun, steps = output }));
        }

        private static object ResolvePipelineValue(JToken token, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);

            return ReplacePlaceholders(value, inputArgs, stepResults);
        }

        private static string ReplacePlaceholders(string value, IDictionary<string, object> inputArgs, IDictionary<string, SkillResult> stepResults)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return Regex.Replace(value, "\\{\\{\\s*([^}]+)\\s*\\}\\}", match =>
            {
                var key = match.Groups[1].Value.Trim();
                if (key.StartsWith("args.", StringComparison.OrdinalIgnoreCase))
                {
                    object arg;
                    return inputArgs != null && inputArgs.TryGetValue(key.Substring(5), out arg) && arg != null
                        ? Convert.ToString(arg)
                        : string.Empty;
                }

                if (key.StartsWith("steps.", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = key.Split('.');
                    SkillResult step;
                    if (parts.Length >= 3 && stepResults != null && stepResults.TryGetValue(parts[1], out step))
                    {
                        if (string.Equals(parts[2], "message", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Message ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "dataJson", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.DataJson ?? string.Empty;
                        }

                        if (string.Equals(parts[2], "success", StringComparison.OrdinalIgnoreCase))
                        {
                            return step.Success ? "true" : "false";
                        }
                    }
                }

                return match.Value;
            });
        }
    }
}
