using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    internal sealed class ExcelSearchResourceService
    {
        private readonly ResourceGatewayService _gateway;
        internal ExcelSearchResourceService(ResourceGatewayService gateway)
        { _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway)); }

        internal ToolHandlerResult Find(ChatSession session, IDictionary<string, object> arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new ExcelFindRequest {
                    Sheet = ToolArgumentReader.String(arguments, "sheet", string.Empty),
                    Address = ToolArgumentReader.String(arguments, "address", string.Empty),
                    Scope = ToolArgumentReader.String(arguments, "scope", string.Empty),
                    Query = ToolArgumentReader.String(arguments, "query", string.Empty),
                    Mode = ExcelFindReplaceService.NormalizeMode(ToolArgumentReader.String(arguments, "mode", "literal")),
                    LookIn = ExcelFindReplaceService.NormalizeFindLookIn(ToolArgumentReader.String(arguments, "lookIn", "values")),
                    MatchCase = ToolArgumentReader.Boolean(arguments, "matchCase", false),
                    WholeWord = ToolArgumentReader.Boolean(arguments, "wholeWord", false),
                    MaxResults = ToolArgumentReader.Int32(arguments, "maxResults", 50),
                    ContextChars = ToolArgumentReader.Int32(arguments, "contextChars", 80) };
                request.Scope = ExcelFindReplaceService.NormalizeScope(request.Scope, request.Sheet, request.Address, "workbook");
                if (string.IsNullOrWhiteSpace(request.Query) || request.Scope == null || request.Mode == null || request.LookIn == null)
                    return Failure("A valid query, scope, mode and lookIn are required.", "invalid_arguments");
                TextPatternEngine.Find(string.Empty, request.Query, new TextPatternOptions {
                    Mode = request.Mode, MatchCase = request.MatchCase, WholeWord = request.WholeWord }, 1, 0);
                var target = _gateway.ResolveIntentTarget(session, "Excel search scope: " + ExcelResourceProvider.SearchTitle(
                    new ExcelCellScopeRequest { Scope = request.Scope, Sheet = request.Sheet, Address = request.Address }));
                var read = _gateway.Read(session, new ResourceReadRequest {
                    Reference = new ResourceRef(target.Reference.Uri), Representation = "text", MaxChars = 256 }).Result;
                var payload = read.CompleteViewPayload;
                if (payload == null || payload.ByteLength > 4L * ExcelFindReplaceService.MaximumSearchCharacters)
                    return Failure("The exact Excel search snapshot is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
                cancellationToken.ThrowIfCancellationRequested();
                var json = ResourceSnapshotReadService.ReadPayload(_gateway.Authority.Payloads, payload);
                if (json.Length > ExcelFindReplaceService.MaximumSearchCharacters)
                    return Failure("Choose a smaller Excel search scope.", "RESOURCE_SNAPSHOT_TOO_LARGE");
                var outcome = ExcelFindReplaceService.Find(JsonConvert.DeserializeObject<ExcelSearchSnapshot>(json), request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!outcome.Success) return new ToolHandlerResult(RuntimeResult.Error(outcome.Message, outcome.DataJson), ToolEffectEvidence.None);
                read.Payload = payload; read.Coverage = ResourceCoverage.Whole(); read.Complete = true;
                read.Truncated = false; read.Offset = 0; read.ReturnedCharacters = json.Length; read.NextCursor = null;
                return new ToolHandlerResult(RuntimeResult.Ok(outcome.Message, outcome.DataJson, new[] { read.Resource.Reference }),
                    ToolEffectEvidence.None, resourceEvidence: _gateway.Evidence(session, read));
            }
            catch (ResourceRequestException error) { return Failure(error.Message, error.ErrorCode); }
            catch (TextPatternException error) { return Failure(error.Message, error.ErrorCode); }
            catch (JsonException) { return Failure("Invalid exact Excel search snapshot.", "RESOURCE_SNAPSHOT_UNAVAILABLE"); }
        }

        private static ToolHandlerResult Failure(string message, string code)
        { return new ToolHandlerResult(RuntimeResult.Error(message, JsonConvert.SerializeObject(
            new Dictionary<string, object> { { "code", code }, { "retryable", false } })), ToolEffectEvidence.None); }
    }
}
