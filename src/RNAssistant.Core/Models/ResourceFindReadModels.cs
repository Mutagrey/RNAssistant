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
        [JsonIgnore] public bool Empty { get { return Complete && Items.Count == 0; } }
        [JsonIgnore] public bool Partial { get { return UnavailableScopes.Count > 0; } }
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
        [JsonProperty("snapshotTarget", NullValueHandling = NullValueHandling.Ignore)] public string SnapshotTarget { get; set; }
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
        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore)] public int? Version { get; set; }
        [JsonProperty("historical", NullValueHandling = NullValueHandling.Ignore)] public bool? Historical { get; set; }
        [JsonProperty("startLine", NullValueHandling = NullValueHandling.Ignore)] public int? StartLine { get; set; }
        [JsonProperty("requestedLines", NullValueHandling = NullValueHandling.Ignore)] public int? RequestedLines { get; set; }
        [JsonProperty("startChar", NullValueHandling = NullValueHandling.Ignore)] public int? StartChar { get; set; }
        [JsonProperty("requestedCharacters", NullValueHandling = NullValueHandling.Ignore)] public int? RequestedCharacters { get; set; }
        [JsonProperty("requestedOffset", NullValueHandling = NullValueHandling.Ignore)] public int? RequestedOffset { get; set; }
        [JsonProperty("requestedRows", NullValueHandling = NullValueHandling.Ignore)] public int? RequestedRows { get; set; }
        [JsonProperty("selectionComplete", NullValueHandling = NullValueHandling.Ignore)] public bool? SelectionComplete { get; set; }
        [JsonProperty("section", NullValueHandling = NullValueHandling.Ignore)] public string Section { get; set; }
        [JsonProperty("procedure", NullValueHandling = NullValueHandling.Ignore)] public ResourceProcedureSelection Procedure { get; set; }
        [JsonProperty("partNumber", NullValueHandling = NullValueHandling.Ignore)] public int? PartNumber { get; set; }
        [JsonProperty("partCount", NullValueHandling = NullValueHandling.Ignore)] public int? PartCount { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "resource-read";
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("snapshotTarget", NullValueHandling = NullValueHandling.Ignore)] public string SnapshotTarget { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("scope")] public string Scope { get; set; }
        [JsonProperty("representation")] public string Representation { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("table", NullValueHandling = NullValueHandling.Ignore)] public ResourceTableBatch Table { get; set; }
        [JsonProperty("tree", NullValueHandling = NullValueHandling.Ignore)] public ResourceTree Tree { get; set; }
        [JsonProperty("coverage")] public ResourceCoverage Coverage { get; set; }
        [JsonProperty("sourceCoverage", NullValueHandling = NullValueHandling.Ignore)] public ResourceCoverage SourceCoverage { get; set; }
        [JsonProperty("sourceEndChar", NullValueHandling = NullValueHandling.Ignore)] public int? SourceEndChar { get; set; }
        [JsonProperty("sourceTotalLines", NullValueHandling = NullValueHandling.Ignore)] public int? SourceTotalLines { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("returnedCharacters")] public int ReturnedCharacters { get; set; }
        [JsonProperty("totalCharacters")] public int TotalCharacters { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("sourceComplete", NullValueHandling = NullValueHandling.Ignore)] public bool? SourceComplete { get; set; }
        [JsonProperty("hydratedForNextModelStep")] public bool HydratedForNextModelStep { get; set; }
        [JsonProperty("rawContentIncluded")] public bool RawContentIncluded { get; set; }
        [JsonProperty("references", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyList<SkillReferenceProjection> References { get; set; }

        public static ResourceReadProjection From(ResourceReadResult result, string target, string type, string scope)
        {
            if (result == null) throw new System.ArgumentNullException(nameof(result));
            return new ResourceReadProjection { Target = target, Type = type, Scope = scope,
                Representation = result.Representation, Text = result.Text, Table = result.Table,
                Coverage = result.Coverage, Offset = result.Offset,
                ReturnedCharacters = result.ReturnedCharacters, TotalCharacters = result.TotalCharacters,
                Complete = result.Complete, SourceComplete = result.SourceComplete, Procedure = result.Procedure,
                SourceEndChar = result.SourceEndChar, SourceTotalLines = result.SourceTotalLines,
                PartNumber = result.PartNumber, PartCount = result.PartCount,
                HydratedForNextModelStep = result.HydratedForNextModelStep,
                RawContentIncluded = result.RawContentIncluded };
        }

        public static ResourceReadProjection FromLines(ResourceReadResult result, string target,
            string type, string scope, int startLine, int requestedLines)
        {
            if (startLine < 1 || requestedLines < 1 || result?.Complete != false ||
                !HasExactSelectedText(result))
                throw new System.InvalidOperationException("A complete bounded line selection requires exact excerpt coverage.");
            var projection = From(result, target, type, scope);
            projection.StartLine = startLine;
            projection.RequestedLines = requestedLines;
            projection.SelectionComplete = true;
            return projection;
        }

        public static ResourceReadProjection FromSelectedSourceRange(ResourceReadResult result, string target,
            string type, string scope, int start, int end)
        {
            return FromSelectedSourceRange(result, target, type, scope,
                new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: end));
        }

        public static ResourceReadProjection FromSelectedSourceRange(ResourceReadResult result, string target,
            string type, string scope, ResourceCoverage sourceCoverage)
        {
            if (sourceCoverage == null ||
                (sourceCoverage.Kind != ResourceCoverageKinds.CharacterRange &&
                 sourceCoverage.Kind != ResourceCoverageKinds.LineRange) ||
                !sourceCoverage.Start.HasValue || !sourceCoverage.End.HasValue ||
                result?.Resource?.Reference?.IsExact != true ||
                (result.Representation != ResourceRepresentations.Text &&
                 result.Representation != ResourceRepresentations.Source) || !result.Complete ||
                result.Truncated || result.Text == null || result.ReturnedCharacters != result.Text.Length)
                throw new System.InvalidOperationException("A source range requires a complete exact selected text view.");
            var projection = From(result, target, type, scope);
            projection.SourceCoverage = sourceCoverage;
            projection.SelectionComplete = true;
            projection.SourceComplete = false;
            return projection;
        }

        public static ResourceReadProjection FromSection(ResourceReadResult result, string target,
            string type, string scope, string section)
        {
            if (string.IsNullOrWhiteSpace(section) || result?.Complete != true || result.Truncated ||
                !HasExactSelectedText(result))
                throw new System.InvalidOperationException("A complete section selection requires exact excerpt coverage.");
            var projection = From(result, target, type, scope);
            projection.Section = section;
            projection.SelectionComplete = true;
            projection.Complete = false;
            return projection;
        }

        public static ResourceReadProjection FromProcedure(ResourceReadResult result, string target,
            string type, string scope)
        {
            if (result?.Procedure == null || result.Resource?.Reference?.IsExact != true || result.Complete ||
                (result.Representation != ResourceRepresentations.Source && result.Representation != ResourceRepresentations.Text) ||
                !HasExactSelectedText(result) || result.Coverage.Start != result.Procedure.StartChar || result.Coverage.End != result.Procedure.EndChar)
                throw new System.InvalidOperationException("A procedure requires exact partial source coverage.");
            var projection = From(result, target, type, scope);
            projection.SelectionComplete = true;
            projection.SourceComplete = false;
            return projection;
        }

        public static ResourceReadProjection FromTree(ResourceReadResult result, string target,
            string type, string scope, string pointer, int depth, int offset, int limit)
        {
            if (result?.Resource?.Reference?.IsExact != true || result.Representation != ResourceRepresentations.Tree ||
                result.Tree == null || result.Coverage?.Kind != result.Tree.Coverage().Kind ||
                result.Coverage.Path != pointer || result.Coverage.Start != offset ||
                result.Coverage.End != (long)offset + result.Tree.ReturnedChildren ||
                result.Tree.Pointer != pointer || result.Tree.Depth != depth ||
                result.Tree.Offset != offset || result.Tree.Limit != limit || result.Complete ||
                result.SourceComplete != false)
                throw new System.InvalidOperationException("A structured tree requires exact partial pointer coverage.");
            var projection = From(result, target, type, scope);
            projection.Text = null; // The retained tree body is an internal evidence payload.
            projection.Tree = result.Tree;
            projection.SelectionComplete = true;
            return projection;
        }

        public static ResourceReadProjection FromCharacters(ResourceReadResult result, string target,
            string type, string scope, int startChar, int requestedCharacters)
        {
            if (startChar < 0 || requestedCharacters < 1 ||
                result?.Complete != false || !HasExactSelectedText(result) ||
                result.Offset != startChar || result.TotalCharacters < startChar ||
                result.Coverage.End != System.Math.Min((long)result.TotalCharacters,
                    (long)startChar + requestedCharacters))
                throw new System.InvalidOperationException("A complete character selection requires exact requested excerpt coverage.");
            var projection = From(result, target, type, scope);
            projection.StartChar = startChar;
            projection.RequestedCharacters = requestedCharacters;
            projection.SelectionComplete = true;
            return projection;
        }

        public static ResourceReadProjection FromPart(ResourceReadResult result, string target,
            string type, string scope, int partNumber)
        {
            if (partNumber < 1 || result?.PartNumber != partNumber ||
                !result.PartCount.HasValue || result.PartCount.Value < partNumber ||
                result.Complete || !HasExactSelectedText(result))
                throw new System.InvalidOperationException("A complete text part requires matching part metadata and exact excerpt coverage.");
            var projection = From(result, target, type, scope);
            projection.SelectionComplete = true;
            return projection;
        }

        public static ResourceReadProjection FromTable(ResourceReadResult result, string target,
            string type, string scope, int offset, int requestedRows)
        {
            if (offset < 0 || requestedRows < 1 || result?.Table?.Rows == null ||
                result.Coverage?.Kind != ResourceCoverageKinds.RecordRange ||
                result.Offset != offset || result.Coverage.Start != offset ||
                result.Coverage.End != (long)offset + result.Table.Rows.Count ||
                result.Table.Rows.Count > requestedRows ||
                result.Table.TotalRows < result.Coverage.End ||
                result.Complete != (result.Coverage.End == result.Table.TotalRows) ||
                result.Truncated == result.Complete)
                throw new System.InvalidOperationException("A bounded table selection requires exact row coverage and consistent completion metadata.");
            var projection = From(result, target, type, scope);
            projection.RequestedOffset = offset;
            projection.RequestedRows = requestedRows;
            projection.SelectionComplete = result.Coverage.End ==
                System.Math.Min((long)result.Table.TotalRows, (long)offset + requestedRows);
            projection.Complete = false;
            return projection;
        }

        private static bool HasExactSelectedText(ResourceReadResult result)
        {
            return result?.Text != null && result.Coverage?.Kind == ResourceCoverageKinds.CharacterRange &&
                result.Coverage.Start == result.Offset &&
                result.Coverage.End == (long)result.Offset + result.Text.Length &&
                result.ReturnedCharacters == result.Text.Length &&
                result.TotalCharacters >= result.Coverage.End;
        }
    }

    public sealed class SkillReferenceProjection
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("byteLength")] public long ByteLength { get; set; }
    }

    public sealed class ResourceWithinSearchPage
    {
        [JsonIgnore] public ResourceRef ExactReference { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("matches")] public List<ResourceWithinSearchItem> Matches { get; set; } = new List<ResourceWithinSearchItem>();
        [JsonProperty("totalMatches")] public int TotalMatches { get; set; }
        [JsonProperty("scanComplete")] public bool ScanComplete { get; set; }
        [JsonProperty("sourceComplete")] public bool SourceComplete { get; set; }
    }

    public sealed class ResourceWithinSearchItem
    {
        [JsonProperty("snippet")] public string Snippet { get; set; }
        [JsonProperty("partNumber", NullValueHandling = NullValueHandling.Ignore)] public int? PartNumber { get; set; }
        [JsonProperty("sectionTitle", NullValueHandling = NullValueHandling.Ignore)] public string SectionTitle { get; set; }
    }

    public sealed class ResourceTextOutline
    {
        [JsonIgnore] public ResourceRef ExactReference { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("partCount")] public int PartCount { get; set; }
        [JsonProperty("totalLines")] public int TotalLines { get; set; }
        [JsonProperty("nextPart", NullValueHandling = NullValueHandling.Ignore)] public int? NextPart { get; set; }
        [JsonProperty("sourceComplete")] public bool SourceComplete { get; set; }
        [JsonProperty("symbolLanguage", NullValueHandling = NullValueHandling.Ignore)] public string SymbolLanguage { get; set; }
        [JsonProperty("symbolsComplete")] public bool SymbolsComplete { get; set; }
        [JsonProperty("parts")] public List<ResourceTextOutlinePart> Parts { get; set; } = new List<ResourceTextOutlinePart>();
    }

    public sealed class ResourceTextOutlinePart
    {
        [JsonProperty("partNumber")] public int PartNumber { get; set; }
        [JsonProperty("firstLine")] public int FirstLine { get; set; }
        [JsonProperty("lastLine")] public int LastLine { get; set; }
        [JsonProperty("characters")] public int Characters { get; set; }
        [JsonProperty("symbols")] public List<ResourceTextOutlineSymbol> Symbols { get; set; } = new List<ResourceTextOutlineSymbol>();
    }

    public sealed class ResourceTextOutlineSymbol
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("line", NullValueHandling = NullValueHandling.Ignore)] public int? Line { get; set; }
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

        // Enrich the fresh observation before recording the accepted tool result.
        // Never reconstruct request intent from an adaptive provider's chosen window.
        public ResourceReadObservation WithReadMetadata(ResourceReadProjection projection,
            ResourceCoverage requestedCoverage, string requestedSection = null, int? requestedPartNumber = null,
            string requestedProcedure = null, string requestedProcedureKind = null,
            int? requestedDepth = null)
        {
            if (projection == null) throw new System.ArgumentNullException(nameof(projection));
            var metadata = new ResourceReadMetadata(requestedCoverage, requestedSection, requestedPartNumber,
                projection.SourceCoverage, projection.SelectionComplete, projection.Complete, projection.SourceComplete,
                requestedProcedure, requestedProcedureKind, requestedDepth);
            return new ResourceReadObservation(Result, Evidence.Select(item =>
                item.Resource.Uri == Result.Resource.Reference.Uri && item.Resource.Revision == Result.Resource.Reference.Revision &&
                item.View == Result.Representation
                    ? new ResourceEvidence(item.EvidenceId, item.ScopeId, item.Resource, item.View, item.Coverage,
                        item.Complete, item.AuthorityGeneration, item.Payload, item.Dependencies, item.ObservedAt,
                        item.SourceEventId, item.Immutable, item.ContentSha256, metadata)
                    : item).ToArray());
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

        public void RequireCompleteExactBytes()
        {
            if (!Result.Complete || Result.Resource?.Reference?.IsExact != true || Result.Bytes == null ||
                Result.Coverage?.Kind != ResourceCoverageKinds.Whole || Result.CompleteViewPayload == null ||
                Result.CompleteViewPayload.ByteLength != Result.Bytes.LongLength ||
                Result.ContentSha256 != Result.CompleteViewPayload.Sha256 ||
                !Evidence.Any(item => item != null && item.Complete && item.View == Result.Representation &&
                    item.Coverage?.Kind == ResourceCoverageKinds.Whole &&
                    item.Resource?.Uri == Result.Resource.Reference.Uri && item.Resource.Revision == Result.Resource.Reference.Revision &&
                    item.Payload?.Sha256 == Result.ContentSha256 && item.Payload.ByteLength == Result.Bytes.LongLength &&
                    item.ContentSha256 == Result.ContentSha256))
                throw new System.InvalidOperationException("Complete byte read requires matching exact whole-view evidence.");
        }

        // Select from one authoritative snapshot. Retain only delivered bytes as
        // observation evidence; a local whole capture is not a model whole read.
        public ResourceReadObservation SelectTextWindow(int maximumCharacters,
            System.Func<string, PayloadRef> retainText, int? startLine = null, int? lineCount = null,
            int? startChar = null, int? charCount = null)
        {
            RequireCompleteExactText();
            if (maximumCharacters < 1 || retainText == null)
                throw new System.ArgumentException("A positive context budget and payload owner are required.");
            if (startLine.HasValue != lineCount.HasValue || startChar.HasValue != charCount.HasValue ||
                startLine.HasValue && startChar.HasValue)
                throw new ResourceRequestException("Supply one complete line or character selector.", "resource_window_invalid", false);
            var source = Result.Text;
            if (!startLine.HasValue && !startChar.HasValue && source.Length <= maximumCharacters) return this;
            var start = startChar ?? 0;
            var end = (int)System.Math.Min(source.Length, (long)start + (charCount ?? maximumCharacters));
            if (startChar.HasValue && (start < 0 || start > source.Length || charCount < 1 || charCount > maximumCharacters))
                throw new ResourceRequestException("Character selection must fit the source and model budget.", "resource_chars_invalid", false);
            if (startLine.HasValue)
            {
                if (startLine < 1 || lineCount < 1)
                    throw new ResourceRequestException("Line selectors must be positive.", "resource_lines_invalid", false);
                long line = 1;
                start = startLine == 1 ? 0 : -1;
                end = source.Length;
                for (var i = 0; i < source.Length; i++)
                {
                    if (source[i] != '\r' && source[i] != '\n') continue;
                    if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                    line++;
                    if (line == startLine) start = i + 1;
                    if (line == (long)startLine + lineCount) { end = i + 1; break; }
                }
                if (start < 0) throw new ResourceRequestException("startLine is past the end of the source.", "resource_lines_invalid", false);
                if (end - start > maximumCharacters)
                    throw new ResourceRequestException("Selected lines exceed the model budget; reduce lineCount or select characters.", "resource_lines_too_large", false);
            }
            if (!startLine.HasValue && !startChar.HasValue && end > 0 && end < source.Length &&
                char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end])) end--;
            if (start > 0 && start < source.Length && char.IsHighSurrogate(source[start - 1]) && char.IsLowSurrogate(source[start]) ||
                end > 0 && end < source.Length && char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end]))
                throw new ResourceRequestException("Character selection splits a UTF-16 pair.", "resource_chars_invalid", false);
            var text = source.Substring(start, end - start);
            var payload = retainText(text);
            var coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: start, end: end);
            var selected = new ResourceReadResult { Resource = Result.Resource, Representation = Result.Representation,
                Text = text, Offset = start, ReturnedCharacters = text.Length, TotalCharacters = source.Length,
                ContentSha256 = Result.ContentSha256, Coverage = coverage, Complete = false, Truncated = true,
                SourceComplete = false, SourceEndChar = source.Length, RawContentIncluded = true, Payload = payload };
            var evidence = Evidence.Where(item => item.Resource.Uri == Result.Resource.Reference.Uri &&
                item.Resource.Revision == Result.Resource.Reference.Revision && item.View == Result.Representation)
                .Select(item => new ResourceEvidence("ev_" + System.Guid.NewGuid().ToString("N"), item.ScopeId,
                    item.Resource, item.View, coverage, false, item.AuthorityGeneration, payload, item.Dependencies,
                    item.ObservedAt, item.SourceEventId, item.Immutable, item.ContentSha256)).ToArray();
            return new ResourceReadObservation(selected, evidence);
        }

    }

}
