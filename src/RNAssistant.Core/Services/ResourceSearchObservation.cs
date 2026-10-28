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
        public void Record(ResourceTextSearchRequest request, ResourceWithinSearchPage page, string step = null)
        {
            _reference = null;
            if (string.IsNullOrWhiteSpace(request.Target) || !page.ScanComplete || !page.SourceComplete ||
                !page.TotalMatchesExact || page.ExactReferences.Count != 1) return;
            _target = request.Target; _query = request.Query; _mode = request.Mode; _matchCase = request.MatchCase;
            _count = page.TotalMatches; _reference = page.ExactReferences.Single().Copy(); _step = step;
        }
        public ResourceRef Require(string target, string query, string mode, bool matchCase, int count, string step = null)
        {
            if (_reference == null || _target != target || _query != query || _mode != (mode ?? "literal") ||
                _matchCase != matchCase || _count != count || _step != null && _step == step)
                throw new ResourceRequestException("Complete core.search on this target with identical query/mode/matchCase before replacing; use its exact totalMatches.", "replacement_search_required", false);
            return _reference.Copy();
        }
    }
}
