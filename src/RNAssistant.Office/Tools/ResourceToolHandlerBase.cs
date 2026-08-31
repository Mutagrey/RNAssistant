using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    // One synchronous operation root covers provider routing and any nested live
    // document read. The gateway remains the data-plane owner.
    internal abstract class ResourceToolHandlerBase : IToolHandler
    {
        protected ResourceGatewayService Gateway { get; private set; }
        protected ChatSession Session { get; private set; }

        protected ResourceToolHandlerBase(ResourceGatewayService gateway, ChatSession session)
        {
            Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            Session = session;
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Session == null)
                return Failure("Resource tools require an active chat session.", "resource_session_required", false);
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    context.MarkDispatchPossible();
                    return Task.FromResult(Execute(context));
                }
            }
            catch (KeyNotFoundException ex) { return Failure(ex.Message, "resource_not_found", false); }
            catch (ResourceRequestException ex) { return Failure(ex.Message, ex.ErrorCode, ex.Retryable); }
            catch (InvalidOperationException ex) { return Failure(ex.Message, "resource_request_invalid", true); }
        }

        protected abstract ToolHandlerResult Execute(ToolHandlerContext context);

        protected static ToolHandlerResult Completed(RuntimeResult result)
        {
            return new ToolHandlerResult(result, ToolEffectEvidence.None);
        }

        protected static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        protected static ResourceRef[] ExactReferences(IEnumerable<ResourceRef> references)
        {
            return (references ?? new ResourceRef[0])
                .Where(reference => reference != null && !string.IsNullOrWhiteSpace(reference.Uri))
                .GroupBy(reference => reference.Uri + "\n" + (reference.Revision ?? string.Empty),
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }

        private static Task<ToolHandlerResult> Failure(string message, string code, bool retryable)
        {
            return Task.FromResult(Completed(RuntimeResult.Error(message,
                JsonConvert.SerializeObject(new { code, retryable }))));
        }
    }
}
