using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed class PipelineToolExecutor
    {
        private const int MaxOutputMessageCharacters = 4000;
        private const int MaxOutputDataCharacters = 16000;

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
                output.Add(new
                {
                    id = step.Id,
                    toolId = step.ToolId,
                    success = result.Success,
                    status = result.Status,
                    errorCode = result.ErrorCode,
                    retryable = result.Retryable,
                    message = BoundOutput(result.Message, MaxOutputMessageCharacters),
                    dataJson = BoundOutput(result.DataJson, MaxOutputDataCharacters)
                });

                if (!result.Success)
                {
                    var message = "Pipeline step failed: " + step.Id + ". " + BoundOutput(result.Message, MaxOutputMessageCharacters);
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
            return ToolArgumentNormalizer.NormalizeToken(ResolvePipelineToken(token, inputArgs, stepResults));
        }

        private static string BoundOutput(string value, int maxCharacters)
        {
            value = value ?? string.Empty;
            return value.Length <= maxCharacters
                ? value
                : value.Substring(0, maxCharacters) + "\n...[truncated]";
        }

        private static JToken ResolvePipelineToken(JToken token, IDictionary<string, object> inputArgs, IDictionary<string, ToolResult> stepResults)
        {
            if (token == null || token.Type == JTokenType.Null) return JValue.CreateNull();
            if (token.Type == JTokenType.Object)
            {
                var result = new JObject();
                foreach (var property in ((JObject)token).Properties())
                {
                    result[property.Name] = ResolvePipelineToken(property.Value, inputArgs, stepResults);
                }
                return result;
            }
            if (token.Type == JTokenType.Array)
            {
                var result = new JArray();
                foreach (var item in (JArray)token)
                {
                    result.Add(ResolvePipelineToken(item, inputArgs, stepResults));
                }
                return result;
            }
            if (token.Type != JTokenType.String) return token.DeepClone();

            var value = token.Value<string>();

            var whole = Regex.Match(value ?? string.Empty, "^\\{\\{\\s*([^}]+)\\s*\\}\\}$");
            if (whole.Success)
            {
                object resolved;
                if (TryResolvePlaceholder(whole.Groups[1].Value.Trim(), inputArgs, stepResults, out resolved))
                {
                    var resolvedToken = resolved as JToken;
                    return resolvedToken == null
                        ? (resolved == null ? JValue.CreateNull() : JToken.FromObject(resolved))
                        : resolvedToken.DeepClone();
                }
            }

            return new JValue(ReplacePlaceholders(value, inputArgs, stepResults));
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
                object resolved;
                return TryResolvePlaceholder(key, inputArgs, stepResults, out resolved)
                    ? Convert.ToString(resolved, CultureInfo.InvariantCulture)
                    : match.Value;
            });
        }

        private static bool TryResolvePlaceholder(string key, IDictionary<string, object> inputArgs, IDictionary<string, ToolResult> stepResults, out object value)
        {
            value = null;
            if (key.StartsWith("args.", StringComparison.OrdinalIgnoreCase))
            {
                return inputArgs != null && inputArgs.TryGetValue(key.Substring(5), out value);
            }
            if (!key.StartsWith("steps.", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = key.Split('.');
            ToolResult step;
            if (parts.Length < 3 || stepResults == null || !stepResults.TryGetValue(parts[1], out step)) return false;
            if (string.Equals(parts[2], "message", StringComparison.OrdinalIgnoreCase)) value = step.Message ?? string.Empty;
            else if (string.Equals(parts[2], "dataJson", StringComparison.OrdinalIgnoreCase)) value = step.DataJson ?? string.Empty;
            else if (string.Equals(parts[2], "success", StringComparison.OrdinalIgnoreCase)) value = step.Success;
            else return false;
            return true;
        }
    }
}
