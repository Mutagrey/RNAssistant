using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceReadToolHandler : ResourceToolHandlerBase
    {
        internal const int IntentReadCharacters = 8000;
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ReadToolId,
            "Read-only: Read one semantic target returned by common.resources_find. Use action=next only to continue the same target after hasMore=true. Exact URI, revision, cursor, and page size remain runtime-owned. Media is hydrated only for the next model step and base64 is never embedded in JSON.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.read.intent.v1");
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
            var action = ToolArgumentReader.String(
                context.Arguments, "action", "read").Trim().ToLowerInvariant();
            ResourceReadSelection selection;
            ResourceReadProjection projection;
            if (action == "next")
            {
                var continuation = FindContinuation(target);
                var requestedRepresentation = ToolArgumentReader.String(
                    context.Arguments, "representation", string.Empty).Trim();
                if (requestedRepresentation.Length > 0 && !string.Equals(
                    requestedRepresentation,
                    continuation.Representation,
                    StringComparison.Ordinal))
                {
                    throw new ResourceRequestException(
                        "A continuation cannot change representation. Restart with action=read.",
                        "resource_continuation_representation_changed",
                        false);
                }
                selection = ReadNext(continuation);
                projection = Project(
                    selection,
                    continuation.Target,
                    continuation.Type,
                    continuation.Scope,
                    continuation.ProgressCharacters);
            }
            else
            {
                var selected = Gateway.ResolveIntentTarget(Session, target);
                selection = Gateway.Read(
                    Session,
                    new ResourceReadRequest
                    {
                        Reference = selected.Reference,
                        Representation = ToolArgumentReader.String(
                            context.Arguments, "representation", "auto"),
                        Cursor = string.Empty,
                        MaxChars = IntentReadCharacters
                    });
                projection = Project(
                    selection,
                    selected.Target,
                    selected.Type,
                    selected.Scope,
                    0);
            }
            var result = RuntimeResult.Ok(
                projection.HasMore
                    ? "Resource chunk read. Call the same target with action=next to continue."
                    : "Resource representation read.",
                Serialize(projection),
                selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return Completed(result);
        }

        private ResourceReadSelection ReadNext(
            ResourceReadContinuation continuation)
        {
            var cursor = string.Empty;
            var consumed = 0;
            while (consumed < continuation.ProgressCharacters)
            {
                var replayed = Gateway.Read(
                    Session,
                    new ResourceReadRequest
                    {
                        Reference = continuation.Reference,
                        Representation = continuation.Representation,
                        Cursor = cursor,
                        MaxChars = IntentReadCharacters
                    });
                var page = replayed.Result;
                if (page == null || page.ReturnedCharacters <= 0 ||
                    consumed + page.ReturnedCharacters > continuation.ProgressCharacters ||
                    string.IsNullOrWhiteSpace(page.NextCursor) &&
                    consumed + page.ReturnedCharacters < continuation.ProgressCharacters)
                {
                    throw new ResourceRequestException(
                        "Stored semantic continuation no longer matches the exact resource read chain. Restart with action=read.",
                        "resource_continuation_invalid",
                        false);
                }
                consumed += page.ReturnedCharacters;
                cursor = page.NextCursor;
            }
            if (string.IsNullOrWhiteSpace(cursor))
            {
                throw new ResourceRequestException(
                    "The previous resource result has no remaining content.",
                    "resource_continuation_complete",
                    false);
            }
            return Gateway.Read(
                Session,
                new ResourceReadRequest
                {
                    Reference = continuation.Reference,
                    Representation = continuation.Representation,
                    Cursor = cursor,
                    MaxChars = IntentReadCharacters
                });
        }

        private ResourceReadContinuation FindContinuation(string target)
        {
            foreach (var message in (Session.Messages ?? new List<ChatMessage>())
                .Where(item => item != null)
                .Reverse())
            {
                ToolResultWireReadResult wire;
                string error;
                if (!ToolResultHistoryReader.TryRead(message, out wire, out error) ||
                    !string.Equals(wire.Name, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal) ||
                    wire.Result.Status != RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok ||
                    string.IsNullOrWhiteSpace(wire.Result.DataJson)) continue;
                JObject data;
                try
                {
                    data = JObject.Parse(wire.Result.DataJson);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (!string.Equals((string)data["kind"], "resource-read", StringComparison.Ordinal) ||
                    !string.Equals((string)data["target"], target, StringComparison.Ordinal)) continue;
                if ((bool?)data["complete"] == true)
                {
                    throw new ResourceRequestException(
                        "The latest read for this target is already complete. Use action=read to start again.",
                        "resource_continuation_complete",
                        false);
                }
                var reference = wire.Result.Resources.FirstOrDefault(item =>
                    item != null && !string.IsNullOrWhiteSpace(item.Uri));
                var progress = (int?)data["progressCharacters"] ?? 0;
                var representation = (string)data["representation"];
                if (reference == null || progress <= 0 ||
                    string.IsNullOrWhiteSpace(representation)) break;
                return new ResourceReadContinuation
                {
                    Target = target,
                    Type = (string)data["type"],
                    Scope = (string)data["scope"],
                    Representation = representation,
                    ProgressCharacters = progress,
                    Reference = new ResourceRef(reference.Uri, reference.Revision)
                };
            }
            throw new ResourceRequestException(
                "No incomplete accepted read exists for this target. Start with action=read.",
                "resource_continuation_missing",
                false);
        }

        private static ResourceReadProjection Project(
            ResourceReadSelection selection,
            string target,
            string type,
            string scope,
            int previousCharacters)
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
                ReturnedCharacters = result.ReturnedCharacters,
                TotalCharacters = result.TotalCharacters,
                ProgressCharacters = previousCharacters + result.ReturnedCharacters,
                Complete = result.Complete,
                HasMore = !result.Complete,
                HydratedForNextModelStep = result.HydratedForNextModelStep,
                RawContentIncluded = result.RawContentIncluded
            };
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"Exact readable target returned by common.resources_find.\",\"minLength\":1,\"maxLength\":1000}," +
                "\"action\":{\"type\":\"string\",\"description\":\"Use read to start or restart; use next only after hasMore=true for this target.\",\"enum\":[\"read\",\"next\"],\"default\":\"read\"}," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Meaningful representation to read; omission selects the preferred form. Omit it for action=next.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\"]}" +
                "},\"required\":[\"target\"],\"additionalProperties\":false}";
        }

        private sealed class ResourceReadContinuation
        {
            public string Target { get; set; }
            public string Type { get; set; }
            public string Scope { get; set; }
            public string Representation { get; set; }
            public int ProgressCharacters { get; set; }
            public ResourceRef Reference { get; set; }
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
            [JsonProperty("returnedCharacters")]
            public int ReturnedCharacters { get; set; }
            [JsonProperty("totalCharacters")]
            public int TotalCharacters { get; set; }
            [JsonProperty("progressCharacters")]
            public int ProgressCharacters { get; set; }
            [JsonProperty("complete")]
            public bool Complete { get; set; }
            [JsonProperty("hasMore")]
            public bool HasMore { get; set; }
            [JsonProperty("hydratedForNextModelStep")]
            public bool HydratedForNextModelStep { get; set; }
            [JsonProperty("rawContentIncluded")]
            public bool RawContentIncluded { get; set; }
        }
    }
}
