using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceReadToolHandler : ResourceToolHandlerBase
    {
        private const int InternalReadCharacters = ResourceReadRequest.MaximumCharacters;
        private const int MaximumWholeReadCharacters = ChatArtifactLimits.MaximumTextCharacters;
        private const int MaximumWholeReadPages = 128;
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ReadToolId,
            "Read-only: Read a semantic target supplied by RUNTIME_CONTEXT or common.resources_find. table/records return bounded row coverage with optional semantic path, fields, offset and limit. Text/source/structure require one complete representation; oversized requests fail explicitly. For a project-wide VBA request, read RUNTIME_CONTEXT.document.vba_project_target with representation=structure first. Exact URI, revision, cursor and guards remain runtime-owned. Media is hydrated only for the next model step; base64 is never embedded in JSON.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.read.intent.v2");
        private readonly Action<string, IReadOnlyList<ChatAttachment>> _captureAttachments;

        internal ResourceReadToolHandler(ResourceGatewayService gateway, ChatSession session,
            Action<string, IReadOnlyList<ChatAttachment>> captureAttachments)
            : base(gateway, session)
        {
            _captureAttachments = captureAttachments;
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var target = ToolArgumentReader.String(
                context.Arguments, "target", string.Empty).Trim();
            var selected = Gateway.ResolveIntentTarget(Session, target);
            var representation = ToolArgumentReader.String(context.Arguments, "representation", "auto");
            var structured = representation == "table" || representation == "records";
            if (!structured && new[] { "limit", "offset", "path", "fields" }.Any(context.Arguments.ContainsKey))
                throw new ResourceRequestException("Structural selectors require representation=table or records.", "RESOURCE_VIEW_UNSUPPORTED", false);
            var selection = structured
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = selected.Reference, Representation = representation,
                    MaxRows = ToolArgumentReader.Int32(context.Arguments, "limit", 500),
                    RowOffset = ToolArgumentReader.Int32(context.Arguments, "offset", 0),
                    ViewPath = ToolArgumentReader.String(context.Arguments, "path", "$"),
                    Fields = Fields(context.Arguments) })
                : ReadWhole(selected.Reference, representation);
            var projection = Project(
                selection,
                selected.Target,
                selected.Type,
                selected.Scope);
            var result = RuntimeResult.Ok(
                structured ? "Bounded exact structural view read." : "Complete resource representation read.",
                Serialize(projection),
                selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: Gateway.Evidence(Session, selection.Result));
        }

        private ResourceReadSelection ReadWhole(
            ResourceRef reference,
            string representation)
        {
            var cursor = string.Empty;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var text = new StringBuilder();
            var references = new List<ResourceRef>();
            var attachments = new List<ChatAttachment>();
            var related = new List<ResourceRef>();
            ResourceReadResult first = null;
            ResourceReadResult last = null;
            var hydratedForNextModelStep = false;
            var rawContentIncluded = false;
            var pages = 0;
            while (true)
            {
                pages++;
                if (pages > MaximumWholeReadPages)
                {
                    throw new ResourceRequestException(
                        "The provider exceeded the bounded internal page count. No partial content was returned to the model.",
                        "resource_whole_read_incomplete",
                        false);
                }
                var page = Gateway.Read(
                    Session,
                    new ResourceReadRequest
                    {
                        Reference = reference,
                        Representation = first == null
                            ? representation
                            : first.Representation,
                        Cursor = cursor,
                        MaxChars = InternalReadCharacters
                    });
                if (page == null || page.Result == null)
                {
                    throw new InvalidOperationException(
                        "Resource provider returned no read result.");
                }
                var result = page.Result;
                if (first == null)
                {
                    first = result;
                }
                else if (!string.Equals(
                    first.Representation,
                    result.Representation,
                    StringComparison.Ordinal))
                {
                    throw WholeReadFailure(
                        "Resource provider changed representation during one whole read.");
                }
                if (first.Resource.Reference.Revision != result.Resource.Reference.Revision)
                    throw WholeReadFailure("Resource provider crossed exact revisions during one whole read.");
                var pageText = result.Text ?? string.Empty;
                if (result.Offset != text.Length ||
                    result.ReturnedCharacters != pageText.Length ||
                    result.TotalCharacters < 0 ||
                    first.TotalCharacters != result.TotalCharacters)
                {
                    throw WholeReadFailure(
                        "Resource provider returned a non-contiguous whole-read page.");
                }
                if (result.TotalCharacters > MaximumWholeReadCharacters ||
                    text.Length + pageText.Length > MaximumWholeReadCharacters)
                {
                    throw new ResourceRequestException(
                        "The complete resource representation exceeds the " +
                        MaximumWholeReadCharacters +
                        "-character whole-read safety bound. Use a narrower semantic resource or a domain-specific read.",
                        "resource_whole_read_too_large",
                        false);
                }
                text.Append(pageText);
                references.AddRange(page.ResourceRefs ?? new ResourceRef[0]);
                attachments.AddRange(page.ModelAttachments ?? new ChatAttachment[0]);
                related.AddRange(result.Related ?? new List<ResourceRef>());
                hydratedForNextModelStep = hydratedForNextModelStep ||
                    result.HydratedForNextModelStep;
                rawContentIncluded = rawContentIncluded || result.RawContentIncluded;
                last = result;
                if (result.Complete)
                {
                    if (result.Truncated ||
                        !string.IsNullOrWhiteSpace(result.NextCursor) ||
                        text.Length != result.TotalCharacters)
                    {
                        throw WholeReadFailure(
                            "Resource provider marked an incomplete representation as complete.");
                    }
                    break;
                }
                cursor = result.NextCursor;
                if (!result.Truncated || pageText.Length == 0 ||
                    text.Length >= result.TotalCharacters ||
                    string.IsNullOrWhiteSpace(cursor) ||
                    !seenCursors.Add(cursor))
                {
                    throw new ResourceRequestException(
                        "The provider could not materialize this representation completely. No partial content was returned to the model.",
                        "resource_whole_read_incomplete",
                        false);
                }
            }
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = first.Resource,
                    Representation = first.Representation,
                    Text = text.ToString(),
                    ContentSha256 = last.ContentSha256,
                    AuthorityGeneration = first.AuthorityGeneration,
                    Offset = 0,
                    ReturnedCharacters = text.Length,
                    TotalCharacters = last.TotalCharacters,
                    Complete = true,
                    Truncated = false,
                    HydratedForNextModelStep = hydratedForNextModelStep,
                    RawContentIncluded = rawContentIncluded,
                    Related = related
                        .Where(item => item != null &&
                            !string.IsNullOrWhiteSpace(item.Uri))
                        .GroupBy(item => item.Uri + "\n" +
                            (item.Revision ?? string.Empty), StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList()
                },
                ModelAttachments = attachments
                    .Where(item => item != null)
                    .GroupBy(item => item.Id ?? string.Empty, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList(),
                ResourceRefs = references
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Uri))
                    .GroupBy(item => item.Uri + "\n" + (item.Revision ?? string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList()
            };
        }

        private static ResourceRequestException WholeReadFailure(string message)
        {
            return new ResourceRequestException(
                message + " No partial content was returned to the model.",
                "resource_whole_read_invalid",
                false);
        }

        private static ResourceReadProjection Project(
            ResourceReadSelection selection,
            string target,
            string type,
            string scope)
        {
            if (selection == null || selection.Result == null)
                throw new InvalidOperationException("Resource provider returned no read result.");
            var result = selection.Result;
            return new ResourceReadProjection
            {
                Kind = "resource-read",
                Target = target,
                Type = type,
                Scope = scope,
                Representation = result.Representation,
                Text = result.Text,
                Table = result.Table,
                Coverage = result.Coverage,
                Offset = result.Offset,
                ReturnedCharacters = result.ReturnedCharacters,
                TotalCharacters = result.TotalCharacters,
                Complete = result.Complete,
                HydratedForNextModelStep = result.HydratedForNextModelStep,
                RawContentIncluded = result.RawContentIncluded
            };
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Exact readable target supplied by RUNTIME_CONTEXT or returned by common.resources_find.\",\"minLength\":1,\"maxLength\":1000}," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Meaningful view; table/records return bounded rows with explicit coverage.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\",\"table\",\"records\"]}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum rows in a table/records batch.\",\"minimum\":1,\"maximum\":5000},\"offset\":{\"type\":\"integer\",\"description\":\"Zero-based table/records row offset.\",\"minimum\":0}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Explicit JSON property path for a record array, default $.\",\"maxLength\":256},\"fields\":{\"type\":\"array\",\"description\":\"Structural field keys to project.\",\"maxItems\":128,\"items\":{\"type\":\"string\",\"maxLength\":128}}" +
                "},\"required\":[\"target\"],\"additionalProperties\":false}";
        }

        private static List<string> Fields(IDictionary<string, object> arguments)
        {
            object value;
            if (!arguments.TryGetValue("fields", out value) || value == null) return null;
            return JsonConvert.DeserializeObject<List<string>>(JsonConvert.SerializeObject(value));
        }

        private sealed class ResourceReadProjection
        {
            [JsonProperty("kind")]
            public string Kind { get; set; }
            [JsonProperty("target")]
            public string Target { get; set; }
            [JsonProperty("type")]
            public string Type { get; set; }
            [JsonProperty("scope")]
            public string Scope { get; set; }
            [JsonProperty("representation")]
            public string Representation { get; set; }
            [JsonProperty("text")]
            public string Text { get; set; }
            [JsonProperty("table", NullValueHandling = NullValueHandling.Ignore)]
            public ResourceTableBatch Table { get; set; }
            [JsonProperty("coverage")]
            public ResourceCoverage Coverage { get; set; }
            [JsonProperty("offset")]
            public int Offset { get; set; }
            [JsonProperty("returnedCharacters")]
            public int ReturnedCharacters { get; set; }
            [JsonProperty("totalCharacters")]
            public int TotalCharacters { get; set; }
            [JsonProperty("complete")]
            public bool Complete { get; set; }
            [JsonProperty("hydratedForNextModelStep")]
            public bool HydratedForNextModelStep { get; set; }
            [JsonProperty("rawContentIncluded")]
            public bool RawContentIncluded { get; set; }
        }
    }
}
