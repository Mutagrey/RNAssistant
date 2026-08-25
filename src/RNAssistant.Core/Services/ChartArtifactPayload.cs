using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Services
{
    internal static class ChartArtifactPayload
    {
        public static bool TryParse(string json, out JObject chart)
        {
            chart = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                chart = JObject.Parse(json);
                var type = (string)chart["type"] ?? (string)chart["Type"];
                if (string.Equals(type, "rnassistant.chart", StringComparison.OrdinalIgnoreCase)) return true;
                chart = null;
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
