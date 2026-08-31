using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceReadToolHandler : ResourceToolHandlerBase
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ReadToolId,
            "Read-only: Read one exact resource representation by canonical URI. Text is bounded and pageable; media is hydrated only for the next model step and base64 is never embedded in JSON. Continue only with nextCursor from the immediately preceding read of the same URI, revision, and representation. After resource_revision_changed, restart that URI/representation with both cursor and revision omitted.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.read.v1");
        private readonly Action<string, IReadOnlyList<ChatAttachment>> _captureAttachments;

        internal ResourceReadToolHandler(ResourceGatewayService gateway, ChatSession session,
            Action<string, IReadOnlyList<ChatAttachment>> captureAttachments)
            : base(gateway, session)
        {
            _captureAttachments = captureAttachments;
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var selection = Gateway.Read(
                Session,
                new ResourceReadRequest
                {
                    Reference = new ResourceRef(
                        ToolArgumentReader.String(context.Arguments, "uri", string.Empty),
                        ToolArgumentReader.String(context.Arguments, "revision", null)),
                    Representation = ToolArgumentReader.String(context.Arguments, "representation", "auto"),
                    Cursor = ToolArgumentReader.String(context.Arguments, "cursor", string.Empty),
                    MaxChars = ToolArgumentReader.Int32(context.Arguments, "maxChars", 8000)
                });
            var result = RuntimeResult.Ok("Resource representation read.",
                Serialize(selection.Result), selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return Completed(result);
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"uri\":{\"type\":\"string\",\"description\":\"Exact canonical URI from resources_list/search/resolve.\",\"minLength\":1,\"maxLength\":1000}," +
                "\"revision\":{\"type\":\"string\",\"description\":\"Optional exact revision returned with the resource reference. Mutable reads fail if it has changed.\",\"minLength\":1,\"maxLength\":128}," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read; auto selects the provider's preferred bounded form.\",\"enum\":[\"auto\",\"metadata\",\"text\",\"structure\",\"source\",\"media\"],\"default\":\"auto\"}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Optional continuation: copy nextCursor only from the immediately preceding resources_read result for this exact uri, revision, and representation. Omit it for the first chunk or when nextCursor is absent. After resource_revision_changed omit both cursor and revision. Never reuse another result's cursor or calculate an offset.\",\"maxLength\":256}," +
                "\"maxChars\":{\"type\":\"integer\",\"description\":\"Maximum text characters returned.\",\"minimum\":128,\"maximum\":32000,\"default\":8000}" +
                "},\"required\":[\"uri\"],\"additionalProperties\":false}";
        }
    }
}
