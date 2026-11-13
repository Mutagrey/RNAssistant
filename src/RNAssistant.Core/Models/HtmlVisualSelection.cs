using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Runtime provenance, stored with the attachment rather than in untrusted DOM evidence.
    public sealed class HtmlVisualSourcePage
    {
        public string WorkspaceId { get; set; }
        public string Path { get; set; }
        public string ContentRevision { get; set; }
        public string SavedVersionId { get; set; }
        public string Identity(ResourceRef source)
        {
            if (source?.IsExact != true || string.IsNullOrEmpty(Path) || Path.Length > 2048 ||
                string.IsNullOrEmpty(ContentRevision) || ContentRevision.Length > 256)
                throw new System.InvalidOperationException("The exact annotation page is unavailable.");
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return "page_" + System.BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    JsonConvert.SerializeObject(new[] { source.Uri, source.Revision, WorkspaceId, Path, ContentRevision, SavedVersionId })))).Replace("-", "").ToLowerInvariant();
        }
        public HtmlVisualSourcePage Copy() => (HtmlVisualSourcePage)MemberwiseClone();
    }

    // One bounded DOM evidence contract for HTML and plugin previews. Source
    // identity is supplied by the trusted runtime, never by this payload.
    public sealed class HtmlVisualSelection
    {
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("stableUiId")] public string StableUiId { get; set; }
        [JsonProperty("attributes")] public Dictionary<string, string> Attributes { get; set; }
        [JsonProperty("selector")] public string Selector { get; set; }
        [JsonProperty("ancestry")] public List<HtmlVisualAncestor> Ancestry { get; set; }
        [JsonProperty("textExcerpt")] public string TextExcerpt { get; set; }
        [JsonProperty("rect")] public HtmlVisualRect Rect { get; set; }
        [JsonProperty("viewport")] public HtmlVisualViewport Viewport { get; set; }
    }

    // A read-only projection of the immutable selection body for the composer.
    public sealed class HtmlVisualAnnotation
    {
        [JsonProperty("selection")] public HtmlVisualSelection Selection { get; set; }
        [JsonProperty("comment")] public string Comment { get; set; }
        [JsonProperty("annotationNumber")] public int AnnotationNumber { get; set; }
    }

    public sealed class HtmlVisualAncestor
    {
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("attributes")] public Dictionary<string, string> Attributes { get; set; }
    }

    public sealed class HtmlVisualRect
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
    }

    public sealed class HtmlVisualViewport
    {
        [JsonProperty("width")] public double Width { get; set; }
        [JsonProperty("height")] public double Height { get; set; }
        [JsonProperty("dpr")] public double Dpr { get; set; }
    }
}
