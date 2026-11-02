using System;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    // Disposable exact search receipt, scoped to the current runtime. Restart requires a new search.
    // It authorizes only the identical replacement query, never a whole-source overwrite.
    public sealed class ResourceSearchObservation
    {
        private string _target, _query, _mode, _step;
        private bool _matchCase;
        private int _count;
        private ResourceRef _reference;
        private string _unavailable = "No completed target search is retained in this runtime.";
        public void Record(ResourceTextSearchRequest request, ResourceWithinSearchPage page, string step = null)
        {
            _reference = null;
            _unavailable = string.IsNullOrWhiteSpace(request.Target)
                ? "The last search used a scope; replacement requires a search of one exact target."
                : "The last target search was incomplete; follow its continuation or narrow the target.";
            if (string.IsNullOrWhiteSpace(request.Target) || !page.ScanComplete || !page.SourceComplete ||
                !page.TotalMatchesExact || page.ExactReferences.Count != 1) return;
            _target = request.Target; _query = request.Query; _mode = request.Mode; _matchCase = request.MatchCase;
            _count = page.TotalMatches; _reference = page.ExactReferences.Single().Copy(); _step = step;
        }
        public ResourceRef Require(string target, string query, string mode, bool matchCase, int count, string step = null)
        {
            string mismatch = _reference == null ? _unavailable :
                _target != target ? "target differs from the last completed search." :
                _query != query ? "query differs from the last completed search." :
                _mode != (mode ?? "literal") ? "mode differs from the last completed search (" + _mode + ")." :
                _matchCase != matchCase ? "matchCase differs from the last completed search (" + _matchCase.ToString().ToLowerInvariant() + ")." :
                _count != count ? "expectedMatches differs from the last completed search totalMatches=" + _count + "." :
                _step != null && _step == step ? "Search and replacement are in the same model response; wait for the search result first." : null;
            if (mismatch != null)
                throw new ResourceRequestException(mismatch + " Complete core.search on this target with identical query/mode/matchCase, then use its exact totalMatches in the next response.", "replacement_search_required", false);
            return _reference.Copy();
        }
    }
}
