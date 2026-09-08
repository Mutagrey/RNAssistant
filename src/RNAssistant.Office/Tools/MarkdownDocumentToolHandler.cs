using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class MarkdownDocumentToolHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        private readonly MarkdownDocumentService _service;
        private readonly ChatSession _session;
        internal MarkdownDocumentToolHandler(MarkdownDocumentService service, ChatSession session)
        { _service = service ?? throw new ArgumentNullException(nameof(service)); _session = session; }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var intent = _service.Prepare(_session, context);
                return Task.FromResult(new ToolPreparationResult(RuntimeResult.Ok("Markdown target prepared."), JsonConvert.SerializeObject(intent)));
            }
            catch (ResourceRequestException ex) { return Rejected(ex.ErrorCode, ex.Message); }
            catch (InvalidDataException ex) { return Rejected("markdown_document_unavailable", ex.Message); }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (DocumentAccessGate.BeginOperation())
            {
                var artifact = _service.Save(_session, context);
                var target = ResourceGatewayService.IntentTarget(new ResourceDescriptor {
                    Kind = artifact.Kind, Title = artifact.Title, CreatedUtc = artifact.CreatedUtc });
                var data = JsonConvert.SerializeObject(new MarkdownDocumentResult {
                    Target = target, Title = artifact.Title, Version = artifact.Revision,
                    Characters = artifact.InlineText.Length });
                return Task.FromResult(context.Complete(new ToolHandlerResult(
                    RuntimeResult.Ok("Markdown document saved as a new revision.", data,
                        new[] { ChatResourceUri.CreateArtifactRevision(_session, artifact) }), ToolEffectEvidence.VerifiedChange)));
            }
        }

        private static Task<ToolPreparationResult> Rejected(string code, string message)
        {
            return Task.FromResult(new ToolPreparationResult(RuntimeResult.Error(message,
                JsonConvert.SerializeObject(new MarkdownDocumentError { Code = code })), null,
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
        }
    }

    internal sealed class MarkdownDocumentResult
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("characters")] public int Characters { get; set; }
    }
    internal sealed class MarkdownDocumentError
    {
        [JsonProperty("code")] public string Code { get; set; }
    }
}
