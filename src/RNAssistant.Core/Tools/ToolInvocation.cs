using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Tools
{
    // Accepted local invocation. Model wire remains name + arguments only;
    // runtime correlation and guard fields are assigned locally.
    public sealed class ToolInvocation
    {
        public string ToolId { get; set; }
        public string Description { get; set; }
        public string ToolCallId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string RuntimeGuardJson { get; set; }

        [JsonIgnore]
        public string RuntimeStepId { get; set; }

        [JsonIgnore]
        public string ExpectedContentSha256 { get; set; }

        public ToolInvocation()
        {
            Arguments = new Dictionary<string, object>(
                System.StringComparer.OrdinalIgnoreCase);
        }
    }
}
