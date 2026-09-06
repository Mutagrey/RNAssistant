using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    // Search is a domain operation over an exact Gateway capture, never a second reader.
    internal sealed class WordSearchResourceService
    {
        private readonly ResourceGatewayService _gateway;
        internal WordSearchResourceService(ResourceGatewayService gateway)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); }

        internal ToolHandlerResult Find(ChatSession session, IDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = ToolArgumentReader.String(arguments, "scope", "main").Trim().ToLowerInvariant();
                var request = new WordReplaceRequest { Scope = scope,
                    Find = ToolArgumentReader.String(arguments, "query", string.Empty),
                    Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                    MatchCase = ToolArgumentReader.Boolean(arguments, "matchCase", false),
                    WholeWord = ToolArgumentReader.Boolean(arguments, "wholeWord", false) };
                if (string.IsNullOrWhiteSpace(request.Find)) return Failure("query is required.", "invalid_arguments");
                // Reject malformed regex/mode before materializing any Office text.
                TextPatternEngine.Find(string.Empty, request.Find, new TextPatternOptions {
                    Mode = request.Mode, MatchCase = request.MatchCase, WholeWord = request.WholeWord }, 1, 0);
                var target = _gateway.ResolveIntentTarget(session, "Word search scope: " + scope);
                var read = _gateway.Read(session, new ResourceReadRequest {
                    Reference = new ResourceRef(target.Reference.Uri), Representation = "text", MaxChars = 256 }).Result;
                var payload = read.CompleteViewPayload;
                if (payload == null || payload.ByteLength > 4L * WordService.MaximumTextCharacters)
                    return Failure("The exact Word search snapshot is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
                cancellationToken.ThrowIfCancellationRequested();
                var json = ResourceSnapshotReadService.ReadPayload(_gateway.Authority.Payloads, payload);
                if (json.Length > WordService.MaximumTextCharacters)
                    return Failure("The search snapshot exceeds the capture bound.", "RESOURCE_SNAPSHOT_TOO_LARGE");
                var snapshot = JsonConvert.DeserializeObject<WordSearchSnapshot>(json);
                var outcome = WordService.Find(snapshot, request,
                    ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ToolArgumentReader.Int32(arguments, "contextChars", 80), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (outcome.Status != WordOutcomeStatus.Ok)
                    return new ToolHandlerResult(RuntimeResult.Error(outcome.Message, outcome.DataJson), ToolEffectEvidence.None);
                read.Payload = payload; read.Coverage = ResourceCoverage.Whole(); read.Complete = true;
                read.Truncated = false; read.Offset = 0; read.ReturnedCharacters = json.Length; read.NextCursor = null;
                return new ToolHandlerResult(RuntimeResult.Ok(outcome.Message, outcome.DataJson, new[] { read.Resource.Reference }),
                    ToolEffectEvidence.None, resourceEvidence: _gateway.Evidence(session, read));
            }
            catch (ResourceRequestException error) { return Failure(error.Message, error.ErrorCode); }
            catch (TextPatternException error) { return Failure(error.Message, error.ErrorCode); }
            catch (JsonException) { return Failure("Invalid exact Word search snapshot.", "RESOURCE_SNAPSHOT_UNAVAILABLE"); }
        }

        private static ToolHandlerResult Failure(string message, string code)
        { return new ToolHandlerResult(RuntimeResult.Error(message, JsonConvert.SerializeObject(
            new Dictionary<string, object> { { "code", code }, { "retryable", false } })), ToolEffectEvidence.None); }
    }
}
