using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Models
{
    public static class ResourceRepresentations
    {
        public const string Metadata = "metadata";
        public const string Text = "text";
        public const string Structure = "structure";
        public const string Media = "media";
        public const string Source = "source";
    }

    public sealed class ResourceRef
    {
        [Newtonsoft.Json.JsonProperty("uri")]
        public string Uri { get; set; }
        [Newtonsoft.Json.JsonProperty("revision")]
        public string Revision { get; set; }

        public ResourceRef()
        {
        }

        public ResourceRef(string uri, string revision = null)
        {
            Uri = uri;
            Revision = revision;
        }
    }

    public sealed class ResourceDescriptor
    {
        [Newtonsoft.Json.JsonProperty("reference")]
        public ResourceRef Reference { get; set; }
        [Newtonsoft.Json.JsonProperty("provider")]
        public string Provider { get; set; }
        [Newtonsoft.Json.JsonProperty("kind")]
        public string Kind { get; set; }
        [Newtonsoft.Json.JsonProperty("title")]
        public string Title { get; set; }
        [Newtonsoft.Json.JsonProperty("mimeType")]
        public string MimeType { get; set; }
        [Newtonsoft.Json.JsonProperty("mutable")]
        public bool Mutable { get; set; }
        [Newtonsoft.Json.JsonProperty("byteLength")]
        public long? ByteLength { get; set; }
        [Newtonsoft.Json.JsonProperty("createdUtc")]
        public DateTime? CreatedUtc { get; set; }
        [Newtonsoft.Json.JsonProperty("contentSha256")]
        public string ContentSha256 { get; set; }
        [Newtonsoft.Json.JsonProperty("sourceMessageId")]
        public string SourceMessageId { get; set; }
        [Newtonsoft.Json.JsonProperty("parent")]
        public ResourceRef Parent { get; set; }
        [Newtonsoft.Json.JsonProperty("related")]
        public List<ResourceRef> Related { get; set; }
        [Newtonsoft.Json.JsonProperty("representations")]
        public List<string> Representations { get; set; }
        [Newtonsoft.Json.JsonProperty("metadata")]
        public Dictionary<string, string> Metadata { get; set; }

        public ResourceDescriptor()
        {
            Representations = new List<string>();
            Related = new List<ResourceRef>();
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class ResourceReadRequest
    {
        [Newtonsoft.Json.JsonProperty("reference")]
        public ResourceRef Reference { get; set; }
        [Newtonsoft.Json.JsonProperty("representation")]
        public string Representation { get; set; }
        [Newtonsoft.Json.JsonProperty("cursor")]
        public string Cursor { get; set; }
        [Newtonsoft.Json.JsonProperty("maxChars")]
        public int MaxChars { get; set; }
    }

    public sealed class ResourceReadResult
    {
        [Newtonsoft.Json.JsonProperty("resource")]
        public ResourceDescriptor Resource { get; set; }
        [Newtonsoft.Json.JsonProperty("representation")]
        public string Representation { get; set; }
        [Newtonsoft.Json.JsonProperty("text")]
        public string Text { get; set; }
        [Newtonsoft.Json.JsonProperty("contentSha256")]
        public string ContentSha256 { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public int Offset { get; set; }
        [Newtonsoft.Json.JsonProperty("returnedCharacters")]
        public int ReturnedCharacters { get; set; }
        [Newtonsoft.Json.JsonProperty("totalCharacters")]
        public int TotalCharacters { get; set; }
        [Newtonsoft.Json.JsonProperty("nextCursor")]
        public string NextCursor { get; set; }
        [Newtonsoft.Json.JsonProperty("complete")]
        public bool Complete { get; set; }
        [Newtonsoft.Json.JsonProperty("truncated")]
        public bool Truncated { get; set; }
        [Newtonsoft.Json.JsonProperty("hydratedForNextModelStep")]
        public bool HydratedForNextModelStep { get; set; }
        [Newtonsoft.Json.JsonProperty("rawContentIncluded")]
        public bool RawContentIncluded { get; set; }
        [Newtonsoft.Json.JsonProperty("related")]
        public List<ResourceRef> Related { get; set; }

        public ResourceReadResult()
        {
            Related = new List<ResourceRef>();
        }
    }

    public sealed class ResourceListPage
    {
        [Newtonsoft.Json.JsonProperty("provider")]
        public string Provider { get; set; }
        [Newtonsoft.Json.JsonProperty("providers")]
        public List<string> Providers { get; set; }
        [Newtonsoft.Json.JsonProperty("items")]
        public List<ResourceDescriptor> Items { get; set; }
        [Newtonsoft.Json.JsonProperty("total")]
        public int Total { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public string Cursor { get; set; }
        [Newtonsoft.Json.JsonProperty("nextCursor")]
        public string NextCursor { get; set; }
        [Newtonsoft.Json.JsonProperty("truncated")]
        public bool Truncated { get; set; }

        public ResourceListPage()
        {
            Providers = new List<string>();
            Items = new List<ResourceDescriptor>();
        }
    }

    public sealed class ResourceResolveResult
    {
        [Newtonsoft.Json.JsonProperty("resource")]
        public ResourceDescriptor Resource { get; set; }
        [Newtonsoft.Json.JsonProperty("complete")]
        public bool Complete { get; set; }
    }

    public sealed class ResourceSearchMatch
    {
        [Newtonsoft.Json.JsonProperty("reference")]
        public ResourceRef Reference { get; set; }
        [Newtonsoft.Json.JsonProperty("kind")]
        public string Kind { get; set; }
        [Newtonsoft.Json.JsonProperty("title")]
        public string Title { get; set; }
        [Newtonsoft.Json.JsonProperty("representation")]
        public string Representation { get; set; }
        [Newtonsoft.Json.JsonProperty("matchOffset")]
        public int MatchOffset { get; set; }
        [Newtonsoft.Json.JsonProperty("matchLength")]
        public int MatchLength { get; set; }
        [Newtonsoft.Json.JsonProperty("snippetOffset")]
        public int SnippetOffset { get; set; }
        [Newtonsoft.Json.JsonProperty("snippet")]
        public string Snippet { get; set; }
    }

    public sealed class ResourceSearchResult
    {
        [Newtonsoft.Json.JsonProperty("provider")]
        public string Provider { get; set; }
        [Newtonsoft.Json.JsonProperty("query")]
        public string Query { get; set; }
        [Newtonsoft.Json.JsonProperty("matches")]
        public List<ResourceSearchMatch> Matches { get; set; }
        [Newtonsoft.Json.JsonProperty("scannedCharacters")]
        public int ScannedCharacters { get; set; }
        [Newtonsoft.Json.JsonProperty("scanTruncated")]
        public bool ScanTruncated { get; set; }

        public ResourceSearchResult()
        {
            Matches = new List<ResourceSearchMatch>();
        }
    }
}
