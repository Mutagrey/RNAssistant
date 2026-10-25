using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    public sealed class ResourceTextSearchRequest
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("directory")] public string Directory { get; set; }
        [JsonProperty("recursive")] public bool Recursive { get; set; }
        [JsonProperty("includeGlobs")] public string[] IncludeGlobs { get; set; }
        [JsonProperty("excludeGlobs")] public string[] ExcludeGlobs { get; set; }
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("mode")] public string Mode { get; set; } = "literal";
        [JsonProperty("matchCase")] public bool MatchCase { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; } = 20;
        [JsonProperty("continuation")] public ResourceSearchPosition Continuation { get; set; }
    }

    public sealed class ResourceSearchPosition
    {
        [JsonProperty("afterTarget")] public string AfterTarget { get; set; }
        [JsonProperty("afterLine")] public int AfterLine { get; set; }
        [JsonProperty("afterColumn")] public int AfterColumn { get; set; }
    }

    // Request-local readers, supplied by existing resource owners. Never authority stores.
    public sealed class ResourceSearchSource
    {
        public string Target { get; set; }
        public Func<CancellationToken, ResourceSearchText> Open { get; set; }
    }

    public sealed class ResourceSearchText : IDisposable
    {
        public TextReader Reader { get; set; }
        public ResourceRef Reference { get; set; }
        public bool SourceComplete { get; set; } = true;
        public void Dispose() { Reader?.Dispose(); }
    }

    public sealed class ResourceSearchSources
    {
        public List<ResourceSearchSource> Items { get; set; } = new List<ResourceSearchSource>();
        public bool Complete { get; set; } = true;
        public List<string> Unavailable { get; set; } = new List<string>();
    }
}
