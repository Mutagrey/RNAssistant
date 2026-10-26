using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    // Shared display summaries for owned result shapes; never effect inference.
    public static class ToolResultSummaryProjection
    {
        public static RNAssistant.Core.Models.ToolResultSummary Summarize(string toolId, string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return null;
            if (toolId != "common.resources_find" && toolId != "common.resources_read" && toolId != "common.resources_search" &&
                toolId != CapabilityToolCatalog.SearchToolId && toolId != CapabilityToolCatalog.ReadToolId) return null;
            try
            {
                var data = JObject.Parse(dataJson);
                if (toolId == "common.resources_find") return SummarizeFind(data);
                if (toolId == "common.resources_read") return SummarizeRead(data);
                if (toolId == "common.resources_search" && data["matches"] is JArray matches)
                    return new RNAssistant.Core.Models.ToolResultSummary { Kind = "resource-search", ReturnedItems = matches.Count,
                        Complete = data.Value<bool?>("scanComplete") == true && data.Value<bool?>("sourceComplete") == true && data.Value<bool?>("matchesComplete") == true,
                        Partial = (data["unavailableScopes"] as JArray)?.Count > 0 };
                if (toolId == CapabilityToolCatalog.SearchToolId) return CapabilityCatalogService.SummarizeSearch(data);
                return CapabilityCatalogService.SummarizeRead(data);
            }
            catch (JsonException) { return null; }
            catch (System.OverflowException) { return null; }
        }

        public static void ReadErrorMetadata(string data, out string code,
            out bool? retryable)
        {
            code = null;
            retryable = null;
            if (string.IsNullOrWhiteSpace(data)) return;
            try
            {
                var root = JsonConvert.DeserializeObject<JObject>(data,
                    new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None
                    });
                if (root == null) return;
                if (root["code"] != null &&
                    root["code"].Type == JTokenType.String)
                    code = (string)root["code"];
                if (root["retryable"] != null &&
                    root["retryable"].Type == JTokenType.Boolean)
                    retryable = (bool)root["retryable"];
            }
            catch (JsonException) { }
        }
        private static ToolResultSummary SummarizeFind(JObject data)
        {
            var items = data?["items"] as JArray;
            if (items == null || data["complete"]?.Type != JTokenType.Boolean) return null;
            return new ToolResultSummary {
                Kind = "resource-find",
                ReturnedItems = items.Count,
                TotalItems = data["total"]?.Type == JTokenType.Integer ? data.Value<int?>("total") : null,
                Complete = data.Value<bool>("complete"),
                Partial = (data["unavailableScopes"] as JArray)?.Count > 0
            };
        }

        private static ToolResultSummary SummarizeRead(JObject data)
        {
            if (data?["kind"]?.Type != JTokenType.String ||
                (string)data["kind"] != "resource-read" ||
                data["representation"]?.Type != JTokenType.String ||
                data["complete"]?.Type != JTokenType.Boolean) return null;
            var representation = data.Value<string>("representation");
            if (representation.Length > 32) return null;
            var rows = (data["table"] as JObject)?["rows"] as JArray;
            var characters = data["returnedCharacters"]?.Type == JTokenType.Integer
                ? data.Value<int?>("returnedCharacters") : null;
            return new ToolResultSummary {
                Kind = "resource-read",
                Representation = representation,
                ReturnedCharacters = characters,
                ReturnedRows = rows?.Count,
                HydratedForNextModelStep = data["hydratedForNextModelStep"]?.Type == JTokenType.Boolean &&
                    data.Value<bool>("hydratedForNextModelStep"),
                Complete = data.Value<bool>("complete")
            };
        }

    }
}
