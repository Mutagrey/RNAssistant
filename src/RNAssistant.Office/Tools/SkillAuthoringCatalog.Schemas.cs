using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    internal static partial class SkillAuthoringCatalog
    {
        private static string UpsertSchema()
        {
            var commonProperties = new JObject
            {
                ["id"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact stable custom skill id.",
                    ["minLength"] = 1,
                    ["maxLength"] = 128
                },
                ["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Existence policy for the selected core or reference resource; upsert is normally sufficient.",
                    ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                    ["default"] = "upsert"
                }
            };
            var coreProperties = (JObject)commonProperties.DeepClone();
            coreProperties["host"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Office host where the skill is visible.",
                ["enum"] = new JArray(
                    "Common", "Excel", "Word", "PowerPoint", "Outlook")
            };
            coreProperties["name"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Human-readable skill name.",
                ["maxLength"] = 200
            };
            coreProperties["description"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Concise catalog description used by the model to decide whether to load this skill.",
                ["maxLength"] = 4000
            };
            coreProperties["version"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Human package version such as 1.0.0."
            };
            coreProperties["bodyMarkdown"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Complete Markdown instructions for the skill core; references are written in separate calls.",
                ["maxLength"] = 500000
            };
            coreProperties["enabled"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether the skill is enabled and appears in Agent context."
            };

            var referenceProperties = (JObject)commonProperties.DeepClone();
            referenceProperties["referencePath"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact path references/<name>.md directly under references/; this call must contain no skill-core fields.",
                ["minLength"] = 1,
                ["maxLength"] = 260
            };
            referenceProperties["referenceMarkdown"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Complete UTF-8 Markdown content for referencePath.",
                ["maxLength"] = SkillStore.MaximumSkillReferenceCharacters
            };

            var allProperties = (JObject)coreProperties.DeepClone();
            allProperties["referencePath"] =
                referenceProperties["referencePath"].DeepClone();
            allProperties["referenceMarkdown"] =
                referenceProperties["referenceMarkdown"].DeepClone();
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = allProperties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = coreProperties,
                        ["required"] = new JArray("id"),
                        ["additionalProperties"] = false
                    },
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = referenceProperties,
                        ["required"] = new JArray(
                            "id", "referencePath", "referenceMarkdown"),
                        ["additionalProperties"] = false
                    }
                }
            }.ToString(Formatting.None);
        }

        private static string DeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact stable custom skill id.",
                        ["minLength"] = 1,
                        ["maxLength"] = 128
                    },
                    ["referencePath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact direct references/*.md path to delete; omit to delete the entire custom skill.",
                        ["maxLength"] = 260
                    }
                },
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }
    }
}
