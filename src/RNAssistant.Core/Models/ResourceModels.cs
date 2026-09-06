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
        public const string Raw = "raw";
        public const string Image = "image";
        public const string Thumbnail = "thumbnail";
        public const string RenderPage = "render-page";
        public const string PageThumbnail = "page-thumbnail";
    }

    public sealed class ResourceRef
    {
        [Newtonsoft.Json.JsonProperty("uri")]
        public string Uri { get; private set; }
        [Newtonsoft.Json.JsonProperty("revision")]
        public string Revision { get; private set; }

        [Newtonsoft.Json.JsonIgnore]
        public string RevisionId
        {
            get { return Revision; }
        }

        [Newtonsoft.Json.JsonIgnore]
        public ResourceIdentity Identity
        {
            get { return string.IsNullOrWhiteSpace(Uri) ? null : new ResourceIdentity(Uri); }
        }

        [Newtonsoft.Json.JsonIgnore]
        public bool IsExact { get { return !string.IsNullOrWhiteSpace(Uri) && !string.IsNullOrWhiteSpace(Revision); } }

        [Newtonsoft.Json.JsonConstructor]
        public ResourceRef(string uri, string revision = null)
        {
            Uri = uri;
            Revision = revision;
        }

        public ResourceRef Copy()
        {
            return new ResourceRef(Uri, Revision);
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
        [Newtonsoft.Json.JsonProperty("payload", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public PayloadRef Payload { get; set; }
        [Newtonsoft.Json.JsonProperty("coverage", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public ResourceCoverage Coverage { get; set; }
        [Newtonsoft.Json.JsonProperty("capabilities")]
        public List<string> Capabilities { get; set; }
        [Newtonsoft.Json.JsonProperty("viewCapabilities")]
        public List<ResourceViewCapability> ViewCapabilities { get; set; }
        [Newtonsoft.Json.JsonProperty("tracking")]
        public string Tracking { get; set; }
        [Newtonsoft.Json.JsonProperty("schema", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public ResourceRef Schema { get; set; }
        [Newtonsoft.Json.JsonProperty("dependencies")]
        public List<ResourceDependency> Dependencies { get; set; }
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
            Capabilities = new List<string>();
            ViewCapabilities = new List<ResourceViewCapability>();
            Dependencies = new List<ResourceDependency>();
        }
    }

    public sealed class ResourceReadRequest
    {
        public const int MinimumCharacters = 128;
        public const int DefaultCharacters = 2048;
        public const int MaximumCharacters = 32000;

        [Newtonsoft.Json.JsonProperty("reference")]
        public ResourceRef Reference { get; set; }
        [Newtonsoft.Json.JsonProperty("representation")]
        public string Representation { get; set; }
        [Newtonsoft.Json.JsonProperty("cursor")]
        public string Cursor { get; set; }
        [Newtonsoft.Json.JsonProperty("maxChars")]
        public int MaxChars { get; set; }
        [Newtonsoft.Json.JsonProperty("maxRows")]
        public int MaxRows { get; set; }
        [Newtonsoft.Json.JsonProperty("path")]
        public string ViewPath { get; set; }
        [Newtonsoft.Json.JsonProperty("fields")]
        public List<string> Fields { get; set; }
        [Newtonsoft.Json.JsonProperty("rowOffset")]
        public int RowOffset { get; set; }
    }

    public sealed class ResourceReadResult
    {
        [Newtonsoft.Json.JsonIgnore]
        public long? AuthorityGeneration { get; set; }
        // Provider read-back ownership only, never proof of consumer observation.
        [Newtonsoft.Json.JsonIgnore]
        public PayloadRef CompleteViewPayload { get; set; }
        [Newtonsoft.Json.JsonProperty("resource")]
        public ResourceDescriptor Resource { get; set; }
        [Newtonsoft.Json.JsonProperty("representation")]
        public string Representation { get; set; }
        [Newtonsoft.Json.JsonProperty("text")]
        public string Text { get; set; }
        [Newtonsoft.Json.JsonProperty("table", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public ResourceTableBatch Table { get; set; }
        [Newtonsoft.Json.JsonProperty("binary", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public ResourceBinaryView Binary { get; set; }
        [Newtonsoft.Json.JsonProperty("contentSha256")]
        public string ContentSha256 { get; set; }
        [Newtonsoft.Json.JsonProperty("payload", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public PayloadRef Payload { get; set; }
        [Newtonsoft.Json.JsonProperty("coverage", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public ResourceCoverage Coverage { get; set; }
        [Newtonsoft.Json.JsonProperty("leaseId", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string LeaseId { get; set; }
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

    public sealed class ResourceBinaryView
    {
        [Newtonsoft.Json.JsonProperty("payload")] public PayloadRef Payload { get; set; }
        [Newtonsoft.Json.JsonProperty("width")] public int Width { get; set; }
        [Newtonsoft.Json.JsonProperty("height")] public int Height { get; set; }
        [Newtonsoft.Json.JsonProperty("pageIndex")] public int? PageIndex { get; set; }
        [Newtonsoft.Json.JsonProperty("pageCount")] public int? PageCount { get; set; }
    }

    public sealed class ResourceTableColumn
    {
        [Newtonsoft.Json.JsonProperty("key")] public string Key { get; set; }
        [Newtonsoft.Json.JsonProperty("label")] public string Label { get; set; }
        [Newtonsoft.Json.JsonProperty("type")] public string Type { get; set; }
    }

    public sealed class ResourceTableBatch
    {
        [Newtonsoft.Json.JsonProperty("columns")] public IReadOnlyList<ResourceTableColumn> Columns { get; set; }
        [Newtonsoft.Json.JsonProperty("rows")] public IReadOnlyList<IDictionary<string, object>> Rows { get; set; }
        [Newtonsoft.Json.JsonProperty("totalRows")] public int TotalRows { get; set; }
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
        [Newtonsoft.Json.JsonIgnore]
        public IReadOnlyList<ResourceEvidence> Evidence { get; set; }
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
        // Provider captures, including zero-match scans. Never part of search output.
        [Newtonsoft.Json.JsonIgnore]
        public List<ResourceReadResult> Scans { get; set; }
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
            Scans = new List<ResourceReadResult>();
        }
    }
}
