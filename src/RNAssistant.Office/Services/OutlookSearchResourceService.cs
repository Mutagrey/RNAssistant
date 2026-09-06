using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal sealed class OutlookSearchResourceService
    {
        private readonly ResourceGatewayService _gateway;
        internal OutlookSearchResourceService(ResourceGatewayService gateway)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); }

        internal ToolHandlerResult Search(ChatSession session, IDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new OutlookSearchMailRequest {
                    Query = ToolArgumentReader.String(arguments, "query", string.Empty),
                    Mode = ToolArgumentReader.String(arguments, "mode", "literal"),
                    MatchCase = ToolArgumentReader.Boolean(arguments, "matchCase", false),
                    WholeWord = ToolArgumentReader.Boolean(arguments, "wholeWord", false),
                    Fields = ToolArgumentReader.String(arguments, "fields", "subject,sender,body"),
                    MaxItems = Math.Max(1, Math.Min(OutlookService.MaxItems, ToolArgumentReader.Int32(arguments, "maxItems", 100))),
                    MaxResults = ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ContextChars = ToolArgumentReader.Int32(arguments, "contextChars", 80) };
                if (string.IsNullOrWhiteSpace(request.Query)) return Failure("query is required.", "invalid_arguments");
                HashSet<string> fields;
                var validation = OutlookService.Fields(request.Fields, out fields);
                if (validation != null) return new ToolHandlerResult(RuntimeResult.Error(validation.Message, validation.DataJson), ToolEffectEvidence.None);
                TextPatternEngine.Find(string.Empty, request.Query, new TextPatternOptions {
                    Mode = request.Mode, MatchCase = request.MatchCase, WholeWord = request.WholeWord }, 1, 0);
                var target = _gateway.ResolveIntentTarget(session, "Outlook search scope: latest:" +
                    request.MaxItems.ToString(CultureInfo.InvariantCulture) + (fields.Contains("body") ? "+body" : ""));
                var read = _gateway.Read(session, new ResourceReadRequest {
                    Reference = new ResourceRef(target.Reference.Uri), Representation = "text", MaxChars = 256 }).Result;
                var payload = read.CompleteViewPayload;
                if (payload == null || payload.ByteLength > 4L * OutlookService.MaxBodyChars)
                    return Failure("The exact Outlook search snapshot is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
                cancellationToken.ThrowIfCancellationRequested();
                var json = ResourceSnapshotReadService.ReadPayload(_gateway.Authority.Payloads, payload);
                if (json.Length > OutlookService.MaxBodyChars)
                    return Failure("Reduce maxItems for this search.", "RESOURCE_SNAPSHOT_TOO_LARGE");
                var outcome = OutlookService.SearchMail(JsonConvert.DeserializeObject<OutlookSearchSnapshot>(json), request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (outcome.Status != OutlookOutcomeStatus.Ok)
                    return new ToolHandlerResult(RuntimeResult.Error(outcome.Message, outcome.DataJson), ToolEffectEvidence.None);
                read.Payload = payload; read.Coverage = ResourceCoverage.Whole(); read.Complete = true;
                read.Truncated = false; read.Offset = 0; read.ReturnedCharacters = json.Length; read.NextCursor = null;
                return new ToolHandlerResult(RuntimeResult.Ok(outcome.Message, outcome.DataJson, new[] { read.Resource.Reference }),
                    ToolEffectEvidence.None, resourceEvidence: _gateway.Evidence(session, read));
            }
            catch (ResourceRequestException error) { return Failure(error.Message, error.ErrorCode); }
            catch (TextPatternException error) { return Failure(error.Message, error.ErrorCode); }
            catch (JsonException) { return Failure("Invalid exact Outlook search snapshot.", "RESOURCE_SNAPSHOT_UNAVAILABLE"); }
        }

        private static ToolHandlerResult Failure(string message, string code)
        { return new ToolHandlerResult(RuntimeResult.Error(message, JsonConvert.SerializeObject(
            new Dictionary<string, object> { { "code", code }, { "retryable", false } })), ToolEffectEvidence.None); }
    }
}
