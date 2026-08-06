using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Tools
{
    internal sealed class PipelineDefinition
    {
        public List<PipelineStepDefinition> Steps { get; private set; }

        public PipelineDefinition()
        {
            Steps = new List<PipelineStepDefinition>();
        }
    }

    internal sealed class PipelineStepDefinition
    {
        public string Id { get; set; }
        public string ToolId { get; set; }
        public JObject Arguments { get; set; }
    }

    internal static class PipelineDefinitionParser
    {
        private const int MaxSteps = 50;

        public static bool TryParse(string ownerId, string json, out PipelineDefinition definition, out string error)
        {
            definition = null;
            error = null;
            JObject root;
            try
            {
                root = JObject.Parse(json ?? string.Empty);
            }
            catch (JsonException ex)
            {
                error = "Invalid pipeline JSON for " + (ownerId ?? string.Empty) + ": " + ex.Message;
                return false;
            }

            var tokens = root["steps"] as JArray;
            if (tokens == null || tokens.Count == 0)
            {
                error = "Pipeline has no steps; at least one step is required: " + (ownerId ?? string.Empty);
                return false;
            }
            if (tokens.Count > MaxSteps)
            {
                error = "Pipeline exceeds the maximum of " + MaxSteps + " steps: " + (ownerId ?? string.Empty);
                return false;
            }

            var result = new PipelineDefinition();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < tokens.Count; index++)
            {
                var step = tokens[index] as JObject;
                if (step == null)
                {
                    error = "Pipeline step " + (index + 1) + " must be a JSON object.";
                    return false;
                }

                var toolId = ((string)step["toolId"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    error = "Pipeline step has no toolId: " + (index + 1) + ".";
                    return false;
                }

                var id = ((string)step["id"] ?? toolId).Trim();
                if (!ids.Add(id))
                {
                    error = "Pipeline step id must be unique: " + id;
                    return false;
                }

                var argumentsToken = step["arguments"];
                if (argumentsToken != null && argumentsToken.Type != JTokenType.Null && !(argumentsToken is JObject))
                {
                    error = "Pipeline step arguments must be a JSON object: " + id;
                    return false;
                }

                result.Steps.Add(new PipelineStepDefinition
                {
                    Id = id,
                    ToolId = toolId,
                    Arguments = argumentsToken as JObject ?? new JObject()
                });
            }

            definition = result;
            return true;
        }
    }
}
