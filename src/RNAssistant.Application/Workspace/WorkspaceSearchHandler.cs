using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Runtime
{
    internal sealed class WorkspaceSearchHandler : IReadOnlyToolHandler, IToolContinuationOwner
    {
        private readonly WorkspaceFileResourceProvider _files;
        private readonly ResourceTextSearch _search = new ResourceTextSearch();
        private readonly IReadOnlyToolHandler _office;
        private readonly IReadOnlyToolHandler _browser;
        private readonly ResourceSearchObservation _observation;
        public WorkspaceSearchHandler(WorkspaceFileResourceProvider files, IReadOnlyToolHandler office = null, ResourceSearchObservation observation = null, IReadOnlyToolHandler browser = null) { _files = files; _office = office; _observation = observation; _browser = browser; }
        public bool OwnsResult(string runId, string callId) => _search.OwnsResult(runId, callId) ||
            (_office as IToolContinuationOwner)?.OwnsResult(runId, callId) == true || (_browser as IToolContinuationOwner)?.OwnsResult(runId, callId) == true;

        public bool CanContinue(IDictionary<string, object> arguments)
        {
            var request = ResourceModelSearchSchema.Parse(arguments);
            var owner = request.Type == "browser" ? _browser as IToolContinuationOwner :
                request.Type == "office" || request.Scope == "vba" ? _office as IToolContinuationOwner : _search;
            return owner?.CanContinue(arguments) == true;
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                var request = ResourceModelSearchSchema.Parse(context.Arguments);
                if (request.Type == "browser")
                {
                    if (_browser == null) return Failure("browser_unavailable", "Browser is unavailable in this run.");
                    return _browser.ExecuteAsync(context, token);
                }
                if (request.Type == "office" || request.Scope == "vba")
                {
                    if (_office == null) return Failure("resource_search_unsupported", "No Office document is bound to this run.");
                    return _office.ExecuteAsync(context, token);
                }
                // Empty directory-search pages are advanced inside the runtime, not by the model.
                // The budget and exact-source revalidation remain in ResourceTextSearch.
                var page = _search.SearchWithAutomaticPaging(request, _files.SearchSources(request, token), token);
                _search.RecordResult(context.Execution.RunId, context.Execution.Call.Id, page);
                _observation?.Record(request, page, context.Execution.RunId + ":" + context.Execution.StepId);
                return Return(ToolResult.Ok(page.ScanComplete && page.SourceComplete ? "Selected text scope searched completely." :
                    "Search is incomplete; inspect reasons or continue. Absence is not established.",
                    JsonConvert.SerializeObject(page), page.ExactReferences));
            }
            catch (ResourceRequestException ex) { return Failure(ex.ErrorCode, ex.Message); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { return Failure("resource_search_unavailable", ex.Message); }
        }
        private static Task<ToolHandlerResult> Failure(string code, string message)
        { return Task.FromResult(new ToolHandlerResult(ToolResult.Error(message, JsonConvert.SerializeObject(new SearchFailure { Code = code })), ToolEffectEvidence.None,
            recovery: code == "resource_search_stale" ? new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.FreshContinuation) : null)); }
        private sealed class SearchFailure { [JsonProperty("code")] public string Code { get; set; } }
        private static Task<ToolHandlerResult> Return(ToolResult result)
        { return Task.FromResult(new ToolHandlerResult(result, ToolEffectEvidence.None)); }
    }
}
