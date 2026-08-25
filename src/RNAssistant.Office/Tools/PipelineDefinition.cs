using System;
using System.Collections.Generic;
using System.Linq;
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
                root = JObject.Parse(json ?? string.Empty, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (JsonException ex)
            {
                error = "Invalid pipeline JSON for " + (ownerId ?? string.Empty) + ": " + ex.Message;
                return false;
            }

            var unknownRoot = root.Properties().FirstOrDefault(property =>
                !string.Equals(property.Name, "version", StringComparison.Ordinal) &&
                !string.Equals(property.Name, "steps", StringComparison.Ordinal));
            if (unknownRoot != null)
            {
                error = "Pipeline contains unsupported root property: " + unknownRoot.Name + ".";
                return false;
            }
            var version = root["version"];
            if (version != null &&
                (version.Type != JTokenType.Integer || !string.Equals(version.ToString(Formatting.None), "1", StringComparison.Ordinal)))
            {
                error = "Pipeline version must be the JSON integer 1.";
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

                var unknownStep = step.Properties().FirstOrDefault(property =>
                    !string.Equals(property.Name, "id", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "toolId", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "arguments", StringComparison.Ordinal));
                if (unknownStep != null)
                {
                    error = "Pipeline step " + (index + 1) + " contains unsupported property: " + unknownStep.Name + ".";
                    return false;
                }

                var toolIdToken = step["toolId"];
                var toolId = toolIdToken != null && toolIdToken.Type == JTokenType.String
                    ? ((string)toolIdToken ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    error = "Pipeline step has no toolId: " + (index + 1) + ".";
                    return false;
                }

                var idToken = step["id"];
                if (idToken != null && idToken.Type != JTokenType.String)
                {
                    error = "Pipeline step id must be a JSON string: " + (index + 1) + ".";
                    return false;
                }
                var id = ((string)idToken ?? toolId).Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    error = "Pipeline step id cannot be blank: " + (index + 1) + ".";
                    return false;
                }
                if (id.Length > 128 || toolId.Length > 128)
                {
                    error = "Pipeline step id and toolId must not exceed 128 characters: " + (index + 1) + ".";
                    return false;
                }
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
                var arguments = argumentsToken as JObject;
                var duplicateArgument = arguments == null
                    ? null
                    : arguments.Properties()
                        .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(group => group.Count() > 1);
                if (duplicateArgument != null)
                {
                    error = "Pipeline step arguments contain duplicate names that differ only by case: " +
                        duplicateArgument.Key + ".";
                    return false;
                }

                result.Steps.Add(new PipelineStepDefinition
                {
                    Id = id,
                    ToolId = toolId,
                    Arguments = arguments ?? new JObject()
                });
            }

            definition = result;
            return true;
        }
    }
}
