using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    // First native tool: domain behavior has no catalog, confirmation or wire logic.
    internal sealed class ResourceListToolHandler : IToolHandler
    {
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolExecutor.ListToolId,
            "Read-only: Discover providers or list bounded resource metadata from one provider. If multiple providers exist, omit provider once to receive their ids, then select one. Bodies are never returned. Continue only with nextCursor from the same result and the identical provider/kind query.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.list.v1");
        private readonly ResourceGatewayService _gateway;
        private readonly ChatSession _session;

        internal ResourceListToolHandler(ResourceGatewayService gateway, ChatSession session)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _session = session;
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session == null)
                return Failure("Resource tools require an active chat session.", "resource_session_required", false);
            try
            {
                context.MarkDispatchPossible();
                var data = _gateway.List(_session,
                    ToolArgumentReader.String(context.Arguments, "provider", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "kind", string.Empty),
                    ToolArgumentReader.String(context.Arguments, "cursor", string.Empty),
                    ToolArgumentReader.Int32(context.Arguments, "limit", 20));
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Resources listed.",
                    JsonConvert.SerializeObject(data, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })),
                    ToolEffectEvidence.None));
            }
            catch (KeyNotFoundException ex) { return Failure(ex.Message, "resource_not_found", false); }
            catch (ResourceRequestException ex) { return Failure(ex.Message, ex.ErrorCode, ex.Retryable); }
            catch (InvalidOperationException ex) { return Failure(ex.Message, "resource_request_invalid", true); }
        }

        private static Task<ToolHandlerResult> Failure(string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(message,
                JsonConvert.SerializeObject(new { code, retryable })), ToolEffectEvidence.None));
        }

        private static string Parameters()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"provider\":{\"type\":\"string\",\"description\":\"Optional exact provider id; omit when only one provider is available.\",\"maxLength\":64}," +
                "\"kind\":{\"type\":\"string\",\"description\":\"Optional exact resource kind filter.\",\"maxLength\":64}," +
                "\"cursor\":{\"type\":\"string\",\"description\":\"Optional continuation: copy nextCursor only from the immediately preceding resources_list result with the identical provider and kind. Omit it for the first page, after changing any filter, or when nextCursor is absent. Never use a resources_read cursor.\",\"maxLength\":256}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum metadata rows.\",\"minimum\":1,\"maximum\":50,\"default\":20}" +
                "},\"required\":[],\"additionalProperties\":false}";
        }

    }
}
