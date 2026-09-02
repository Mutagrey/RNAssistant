using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Tools
{
    internal static partial class SkillAuthoringCatalog
    {
        private static string UpsertSchema()
        {
            var properties = new JObject
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
                    ["description"] = "Existence policy for the skill core; upsert is normally sufficient.",
                    ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                    ["default"] = "upsert"
                }
            };
            properties["host"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Office host where the skill is visible.",
                ["enum"] = new JArray(
                    "Common", "Excel", "Word", "PowerPoint", "Outlook")
            };
            properties["name"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Human-readable skill name.",
                ["maxLength"] = 200
            };
            properties["description"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Concise catalog description used by the model to decide whether to load this skill.",
                ["maxLength"] = 4000
            };
            properties["version"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Human package version such as 1.0.0."
            };
            properties["bodyMarkdown"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Complete Markdown instructions for the skill core; references are written in separate calls.",
                ["maxLength"] = 500000
            };
            properties["enabled"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether the skill is enabled and appears in Agent context."
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string DeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = SkillIdProperty()
                },
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string ReferenceUpsertSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = SkillIdProperty(),
                    ["referencePath"] = ReferencePathProperty(),
                    ["referenceMarkdown"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Complete UTF-8 Markdown content for the reference.",
                        ["maxLength"] = SkillStore.MaximumSkillReferenceCharacters
                    },
                    ["mode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Existence policy; upsert is normally sufficient.",
                        ["enum"] = new JArray(
                            "upsert", "createOnly", "updateOnly"),
                        ["default"] = "upsert"
                    }
                },
                ["required"] = new JArray(
                    "id", "referencePath", "referenceMarkdown"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string ReferenceDeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = SkillIdProperty(),
                    ["referencePath"] = ReferencePathProperty()
                },
                ["required"] = new JArray("id", "referencePath"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static JObject SkillIdProperty()
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact stable custom skill id.",
                ["minLength"] = 1,
                ["maxLength"] = 128
            };
        }

        private static JObject ReferencePathProperty()
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact direct references/<name>.md path.",
                ["minLength"] = 1,
                ["maxLength"] = 260
            };
        }
    }
}
