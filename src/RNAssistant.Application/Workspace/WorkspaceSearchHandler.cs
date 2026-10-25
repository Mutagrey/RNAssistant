using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Runtime
{
    internal sealed class WorkspaceSearchHandler : IReadOnlyToolHandler
    {
        private readonly WorkspaceFileResourceProvider _files;
        private readonly ResourceTextSearch _search = new ResourceTextSearch();
        private readonly IReadOnlyToolHandler _office;
        public WorkspaceSearchHandler(WorkspaceFileResourceProvider files, IReadOnlyToolHandler office = null) { _files = files; _office = office; }
        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                var request = ResourceModelSearchSchema.Parse(context.Arguments);
                if (request.Type == "office" || request.Scope == "vba")
                {
                    if (_office == null) return Failure("resource_search_unsupported", "No Office document is bound to this run.");
                    return _office.ExecuteAsync(context, token);
                }
                var page = _search.Search(request, _files.SearchSources(request, token), token);
                return Return(ToolResult.Ok(page.ScanComplete && page.SourceComplete ? "Selected text scope searched completely." :
                    "Search is incomplete; inspect reasons or continue. Absence is not established.",
                    JsonConvert.SerializeObject(page), page.ExactReferences));
            }
            catch (ResourceRequestException ex) { return Failure(ex.ErrorCode, ex.Message); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { return Failure("resource_search_unavailable", ex.Message); }
        }
        private static Task<ToolHandlerResult> Failure(string code, string message)
        { return Return(ToolResult.Error(message, JsonConvert.SerializeObject(new SearchFailure { Code = code }))); }
        private sealed class SearchFailure { [JsonProperty("code")] public string Code { get; set; } }
        private static Task<ToolHandlerResult> Return(ToolResult result)
        { return Task.FromResult(new ToolHandlerResult(result, ToolEffectEvidence.None)); }
    }
}
