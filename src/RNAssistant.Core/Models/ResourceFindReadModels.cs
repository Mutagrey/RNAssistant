using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Core.Models
{
    // Model-visible semantic discovery. References and descriptors remain runtime
    // metadata; a candidate target is the only address the model may copy to read.
    public sealed class ResourceFindPage
    {
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("items")] public List<ResourceFindCandidate> Items { get; set; } = new List<ResourceFindCandidate>();
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("empty")] public bool Empty { get; set; }
        [JsonProperty("partial")] public bool Partial { get; set; }
        [JsonProperty("refineQuery")] public bool RefineQuery { get; set; }
        [JsonProperty("availabilityHint", NullValueHandling = NullValueHandling.Ignore)]
        public string AvailabilityHint { get; set; }
        [JsonProperty("unavailableScopes")] public List<string> UnavailableScopes { get; set; } = new List<string>();
        [JsonIgnore] public List<ResourceRef> ResourceRefs { get; set; } = new List<ResourceRef>();
        [JsonProperty("directory", NullValueHandling = NullValueHandling.Ignore)] public string Directory { get; set; }
        [JsonProperty("scannedEntries", NullValueHandling = NullValueHandling.Ignore)] public int? ScannedEntries { get; set; }
    }


    public static class ResourceFindProjection
    {
        public static string Message(ResourceFindPage result)
        {
            if (result == null) throw new System.ArgumentNullException(nameof(result));
            return result.Empty ? "No resources matched the semantic scope." :
                result.Partial ? result.Items.Count > 0
                    ? "Resource find returned usable targets; some other sources were unavailable. Use a returned target directly and do not repeat the same find."
                    : "Resource find is incomplete because some sources were unavailable. Refine the scope or query before retrying." :
                !result.Complete ? "Resource find is incomplete; absence is not established." :
                "Resource find completed.";
        }

        public static string Serialize(ResourceFindPage result)
        {
            return JsonConvert.SerializeObject(result,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }
    }

    public sealed class ResourceFindCandidate
    {
        [JsonProperty("sectionTitle", NullValueHandling = NullValueHandling.Ignore)] public string SectionTitle { get; set; }
        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)] public string Description { get; set; }
        [JsonIgnore] public IReadOnlyList<ResourceEvidence> Evidence { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }
        [JsonProperty("mutable")] public bool Mutable { get; set; }
        [JsonProperty("byteLength")] public long? ByteLength { get; set; }
        [JsonProperty("createdUtc")] public System.DateTime? CreatedUtc { get; set; }
        [JsonProperty("representations")] public List<string> Representations { get; set; } = new List<string>();
        [JsonProperty("usage", NullValueHandling = NullValueHandling.Ignore)] public string Usage { get; set; }
        [JsonProperty("matchRepresentation")] public string MatchRepresentation { get; set; }
        [JsonProperty("snippet")] public string Snippet { get; set; }
        [JsonIgnore] public ResourceRef Reference { get; set; }
        [JsonIgnore] public ResourceDescriptor Descriptor { get; set; }
    }

    public sealed class ResourceReadProjection
    {
        [JsonProperty("startLine", NullValueHandling = NullValueHandling.Ignore)] public int? StartLine { get; set; }
        [JsonProperty("requestedLines", NullValueHandling = NullValueHandling.Ignore)] public int? RequestedLines { get; set; }
        [JsonProperty("section", NullValueHandling = NullValueHandling.Ignore)] public string Section { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "resource-read";
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("representation")] public string Representation { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("table", NullValueHandling = NullValueHandling.Ignore)] public ResourceTableBatch Table { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("returnedCharacters")] public int ReturnedCharacters { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("hydratedForNextModelStep")] public bool HydratedForNextModelStep { get; set; }
        [JsonProperty("rawContentIncluded")] public bool RawContentIncluded { get; set; }

        public static ResourceReadProjection From(ResourceReadResult result, string target, string type, string scope)
        {
            if (result == null) throw new System.ArgumentNullException(nameof(result));
            return new ResourceReadProjection { Target = target, Type = type, Scope = scope,
                Representation = result.Representation, Text = result.Text, Table = result.Table,
                Coverage = result.Coverage, Offset = result.Offset,
                ReturnedCharacters = result.ReturnedCharacters, TotalCharacters = result.TotalCharacters,
                Complete = result.Complete, HydratedForNextModelStep = result.HydratedForNextModelStep,
                RawContentIncluded = result.RawContentIncluded };
        }
    }

    // A provider read and the evidence delivered with that same observation.
    public sealed class ResourceReadObservation
    {
        public ResourceReadResult Result { get; private set; }
        public IReadOnlyList<ResourceEvidence> Evidence { get; private set; }

        public ResourceReadObservation(ResourceReadResult result, IReadOnlyList<ResourceEvidence> evidence)
        {
            Result = result ?? throw new System.ArgumentNullException(nameof(result));
            Evidence = evidence ?? new ResourceEvidence[0];
        }

        public void RequireCompleteExactText()
        {
            if (!Result.Complete || Result.Resource?.Reference?.IsExact != true ||
                Result.Coverage?.Kind != ResourceCoverageKinds.Whole ||
                Result.Representation != ResourceRepresentations.Text || Result.Text == null ||
                Result.Offset != 0 || Result.ReturnedCharacters != Result.Text.Length ||
                Result.TotalCharacters != Result.Text.Length ||
                Result.CompleteViewPayload == null || string.IsNullOrWhiteSpace(Result.ContentSha256) ||
                !Evidence.Any(item => item != null && item.Complete &&
                    item.View == ResourceRepresentations.Text &&
                    item.Coverage?.Kind == ResourceCoverageKinds.Whole &&
                    item.Resource?.Uri == Result.Resource.Reference.Uri &&
                    item.Resource.Revision == Result.Resource.Reference.Revision &&
                    item.Payload?.Sha256 == Result.CompleteViewPayload.Sha256 &&
                    item.Payload.ByteLength == Result.CompleteViewPayload.ByteLength &&
                    item.ContentSha256 == Result.ContentSha256))
                throw new System.InvalidOperationException("Complete text read requires matching exact whole-view evidence.");
        }

    }

}
