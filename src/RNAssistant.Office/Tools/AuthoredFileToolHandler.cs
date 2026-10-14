using System;
using System.IO;
using System.Linq;
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
    internal sealed class AuthoredFileToolHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        private readonly AuthoredFileService _service;
        private readonly ChatSession _session;
        internal AuthoredFileToolHandler(AuthoredFileService service, ChatSession session)
        { _service = service ?? throw new ArgumentNullException(nameof(service)); _session = session; }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var intent = _service.Prepare(_session, context);
                return Task.FromResult(new ToolPreparationResult(RuntimeResult.Ok("File intent prepared."), JsonConvert.SerializeObject(intent)));
            }
            catch (ResourceRequestException error) { return Rejected(error.ErrorCode, error.Message); }
            catch (InvalidDataException error) { return Rejected("file_intent_invalid", error.Message); }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (DocumentAccessGate.BeginOperation())
            {
                var saved = _service.Save(_session, context);
                var artifact = saved.Artifact;
                var descriptor = new ResourceDescriptor { Kind = artifact.Kind, Title = artifact.Title, CreatedUtc = artifact.CreatedUtc };
                descriptor.Metadata["authoredFile"] = "true";
                var target = ResourceGatewayService.IntentTarget(descriptor);
                var data = JsonConvert.SerializeObject(new AuthoredFileResult {
                    Target = target, Name = artifact.Title,
                    Bytes = artifact.ContentByteLength ?? 0,
                    Changes = saved.Changes.Select(change => new AuthoredFileModelChange {
                        Removed = change.Removed, Inserted = change.Inserted,
                        Before = change.Before, After = change.After }).ToList() });
                return Task.FromResult(context.Complete(new ToolHandlerResult(
                    RuntimeResult.Ok("File saved and verified as a new revision.", data,
                        new[] { ChatResourceUri.CreateArtifactRevision(_session, artifact) }), ToolEffectEvidence.VerifiedChange)));
            }
        }

        private static Task<ToolPreparationResult> Rejected(string code, string message)
        {
            return Task.FromResult(new ToolPreparationResult(RuntimeResult.Error(message,
                JsonConvert.SerializeObject(new AuthoredFileError { Code = code })), null,
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
        }
    }

    internal sealed class AuthoredFileResult
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("bytes")] public long Bytes { get; set; }
        [JsonProperty("changes")] public System.Collections.Generic.List<AuthoredFileModelChange> Changes { get; set; }
    }
    internal sealed class AuthoredFileModelChange
    {
        [JsonProperty("removed")] public int Removed { get; set; }
        [JsonProperty("inserted")] public int Inserted { get; set; }
        [JsonProperty("before")] public string Before { get; set; }
        [JsonProperty("after")] public string After { get; set; }
    }
    internal sealed class AuthoredFileError
    { [JsonProperty("code")] public string Code { get; set; } }
}
