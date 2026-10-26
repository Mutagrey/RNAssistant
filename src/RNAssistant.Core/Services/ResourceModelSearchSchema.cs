using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class ResourceModelSearchSchema
    {
        public const string Description = "Read-only literal or line-oriented .NET regex search. Select one target or a scope. Results contain bounded snippets and 1-based UTF-16 line/column coordinates; no language parser is required. Workspace scope searches only the selected directory (root when omitted), unless recursive=true includes descendants. Only scanComplete and sourceComplete together establish absence within the selected target, depth and filters. Continue with the returned semantic continuation and identical arguments; changed sources or lost continuation state require a fresh search. Search is not a complete source read or patch authorization.";

        public static string Create(bool workspace, bool office)
        {
            var variants = new JArray();
            variants.Add(Branch("target", new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1000 }, false));
            if (workspace) variants.Add(Branch("scope", new JObject { ["type"] = "string", ["enum"] = new JArray("workspace") }, true));
            if (office)
            {
                var scope = Branch("scope", new JObject { ["type"] = "string", ["enum"] = workspace ? new JArray("vba") : new JArray("vba", "html") }, false);
                variants.Add(scope);
                if (workspace)
                {
                    var target = Branch("target", new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1000 }, false);
                    ((JObject)target["properties"])["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("office") };
                    ((JArray)target["required"]).Add("type");
                    variants.Add(target);
                }
            }
            return new JObject { ["type"] = "object", ["properties"] = new JObject(),
                ["required"] = new JArray(), ["additionalProperties"] = false, ["anyOf"] = variants }.ToString(Formatting.None);
        }

        private static JObject Branch(string selector, JObject definition, bool workspace)
        {
            var fields = new JObject {
                [selector] = definition,
                ["query"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 500 },
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("literal", "regex"), ["default"] = "literal" },
                ["matchCase"] = new JObject { ["type"] = "boolean" },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50 },
                ["continuation"] = new JObject { ["type"] = "object", ["properties"] = new JObject {
                    ["afterTarget"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["afterLine"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["afterColumn"] = new JObject { ["type"] = "integer", ["minimum"] = 0 } },
                    ["required"] = new JArray("afterTarget", "afterLine", "afterColumn"), ["additionalProperties"] = false }
            };
            if (workspace) AddPathFilters(fields);
            return new JObject { ["type"] = "object", ["properties"] = fields,
                ["required"] = new JArray(selector, "query"), ["additionalProperties"] = false };
        }

        public static void AddPathFilters(JObject fields)
        {
            fields["directory"] = new JObject { ["type"] = "string", ["maxLength"] = 1000 };
            fields["recursive"] = new JObject { ["type"] = "boolean", ["default"] = false };
            foreach (var name in new[] { "includeGlobs", "excludeGlobs" })
                fields[name] = new JObject { ["type"] = "array", ["maxItems"] = 16,
                    ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                    ["description"] = "Workspace-relative glob: *, ?, ** and **/. No negation or character classes." };
        }

        public static ResourceTextSearchRequest Parse(System.Collections.Generic.IDictionary<string, object> arguments)
        {
            var value = JObject.FromObject(arguments);
            foreach (var property in value.Properties().Where(x => x.Value.Type == JTokenType.Null).ToArray()) property.Remove();
            return value.ToObject<ResourceTextSearchRequest>();
        }
    }
}
