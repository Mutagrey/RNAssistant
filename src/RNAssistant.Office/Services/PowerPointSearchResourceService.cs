using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal sealed class PowerPointSearchResourceService
    {
        private readonly ResourceGatewayService _gateway;
        internal PowerPointSearchResourceService(ResourceGatewayService gateway)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); }

        internal ToolHandlerResult Search(ChatSession session, IDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new PowerPointReplaceRequest {
                    Scope = ToolArgumentReader.String(arguments, "scope", "deck").Trim().ToLowerInvariant(),
                    SlideIndex = ToolArgumentReader.Int32(arguments, "slideIndex", 0),
                    IncludeNotes = ToolArgumentReader.Boolean(arguments, "includeNotes", true),
                    Find = ToolArgumentReader.String(arguments, "query", string.Empty),
                    Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                    MatchCase = ToolArgumentReader.Boolean(arguments, "matchCase", false),
                    WholeWord = ToolArgumentReader.Boolean(arguments, "wholeWord", false) };
                if (string.IsNullOrWhiteSpace(request.Find) || request.SlideIndex < 0)
                    return Failure("query is required and slideIndex cannot be negative.", "invalid_arguments");
                if (request.Scope != "deck" && request.Scope != "slide")
                    return Failure("scope must be deck or slide.", "powerpoint_scope_invalid");
                TextPatternEngine.Find(string.Empty, request.Find, new TextPatternOptions {
                    Mode = request.Mode, MatchCase = request.MatchCase, WholeWord = request.WholeWord }, 1, 0);
                var scope = request.SlideIndex == 0 ? "deck" : "slide:" + request.SlideIndex.ToString(CultureInfo.InvariantCulture);
                var target = _gateway.ResolveIntentTarget(session, "PowerPoint search scope: " + scope + (request.IncludeNotes ? "+notes" : ""));
                var read = _gateway.Read(session, new ResourceReadRequest {
                    Reference = new ResourceRef(target.Reference.Uri), Representation = "text", MaxChars = 256 }).Result;
                var payload = read.CompleteViewPayload;
                if (payload == null || payload.ByteLength > 4L * PowerPointService.MaximumTextCharacters)
                    return Failure("The exact PowerPoint search snapshot is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
                cancellationToken.ThrowIfCancellationRequested();
                var json = ResourceSnapshotReadService.ReadPayload(_gateway.Authority.Payloads, payload);
                if (json.Length > PowerPointService.MaximumTextCharacters)
                    return Failure("Choose a smaller PowerPoint search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE");
                var outcome = PowerPointService.Search(JsonConvert.DeserializeObject<PowerPointSearchSnapshot>(json), request,
                    ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ToolArgumentReader.Int32(arguments, "contextChars", 80), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (outcome.Status != PowerPointOutcomeStatus.Ok)
                    return new ToolHandlerResult(RuntimeResult.Error(outcome.Message, outcome.DataJson), ToolEffectEvidence.None);
                read.Payload = payload; read.Coverage = ResourceCoverage.Whole(); read.Complete = true;
                read.Truncated = false; read.Offset = 0; read.ReturnedCharacters = json.Length; read.NextCursor = null;
                return new ToolHandlerResult(RuntimeResult.Ok(outcome.Message, outcome.DataJson, new[] { read.Resource.Reference }),
                    ToolEffectEvidence.None, resourceEvidence: _gateway.Evidence(session, read));
            }
            catch (ResourceRequestException error) { return Failure(error.Message, error.ErrorCode); }
            catch (TextPatternException error) { return Failure(error.Message, error.ErrorCode); }
            catch (JsonException) { return Failure("Invalid exact PowerPoint search snapshot.", "RESOURCE_SNAPSHOT_UNAVAILABLE"); }
        }

        private static ToolHandlerResult Failure(string message, string code)
        { return new ToolHandlerResult(RuntimeResult.Error(message, JsonConvert.SerializeObject(
            new Dictionary<string, object> { { "code", code }, { "retryable", false } })), ToolEffectEvidence.None); }
    }
}
