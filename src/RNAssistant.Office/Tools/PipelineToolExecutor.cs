using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class PipelineToolExecutor
    {
        internal delegate ToolResult CommandRunner(ToolCommand command, int depth, bool dryRun, bool manualRun, CancellationToken cancellationToken);

        public ToolResult Execute(
            ToolDefinition tool,
            ToolCommand command,
            int depth,
            bool dryRun,
            bool manualRun,
            CommandRunner runCommand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PipelineDefinition pipeline;
            string parseError;
            if (!PipelineDefinitionParser.TryParse(tool.Id, tool.PipelineJson, out pipeline, out parseError))
            {
                return ToolResult.Fail(parseError, null, "invalid_pipeline", false);
            }

            var stepResults = new Dictionary<string, ToolResult>(StringComparer.OrdinalIgnoreCase);
            var output = new List<object>();
            foreach (var step in pipeline.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nested = new ToolCommand { ToolId = step.ToolId };
                foreach (var property in step.Arguments.Properties())
                {
                    nested.Arguments[property.Name] = ResolvePipelineValue(property.Value, command.Arguments, stepResults);
                }

                var result = runCommand(nested, depth + 1, dryRun, manualRun, cancellationToken) ?? ToolResult.Fail("Pipeline step returned no result.");
                stepResults[step.Id] = result;
                output.Add(new { id = step.Id, toolId = step.ToolId, success = result.Success, status = result.Status, errorCode = result.ErrorCode, retryable = result.Retryable, message = result.Message, dataJson = result.DataJson });

                if (!result.Success)
                {
                    var message = "Pipeline step failed: " + step.Id + ". " + result.Message;
                    var dataJson = JsonConvert.SerializeObject(new { toolId = tool.Id, dryRun = dryRun, steps = output });
                    return output.Count > 1
                        ? ToolResult.PartialFailure(message, dataJson, "pipeline_partial_failure")
                        : ToolResult.Fail(message, dataJson, result.ErrorCode ?? "pipeline_step_failed", result.Retryable);
                }
            }

            return ToolResult.Ok((dryRun ? "Dry run completed: " : "Pipeline executed: ") + tool.Id, JsonConvert.SerializeObject(new { toolId = tool.Id, dryRun = dryRun, steps = output }));
        }

        private static object ResolvePipelineValue(JToken token, IDictionary<string, object> inputArgs, IDictionary<string, ToolResult> stepResults)
        {
            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.None);

            return ReplacePlaceholders(value, inputArgs, stepResults);
        }

        private static string ReplacePlaceholders(string value, IDictionary<string, object> inputArgs, IDictionary<string, ToolResult> stepResults)
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
                    ToolResult step;
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
