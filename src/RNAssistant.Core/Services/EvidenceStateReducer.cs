using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public interface IResourceImpactMatcher
    {
        bool Supports(ResourceCoverage coverage);
        bool Intersects(ResourceCoverage observation, ResourceCoverage impact);
    }

    // Coverage matchers are pure domain semantics. Unsupported precision fails conservatively.
    public sealed class OrdinalResourceImpactMatcher : IResourceImpactMatcher
    {
        public bool Supports(ResourceCoverage coverage)
        {
            return coverage != null && (coverage.Kind == ResourceCoverageKinds.LineRange ||
                coverage.Kind == ResourceCoverageKinds.RecordRange || coverage.Kind == ResourceCoverageKinds.PageRange ||
                coverage.Kind == ResourceCoverageKinds.TimeRange || coverage.Kind == ResourceCoverageKinds.CharacterRange);
        }
        public bool Intersects(ResourceCoverage observation, ResourceCoverage impact)
        {
            return impact == null || observation.Kind != impact.Kind || !observation.Start.HasValue ||
                !observation.End.HasValue || !impact.Start.HasValue || !impact.End.HasValue ||
                observation.Start.Value <= impact.End.Value && impact.Start.Value <= observation.End.Value;
        }
    }

    public sealed class ExcelResourceImpactMatcher : IResourceImpactMatcher
    {
        public bool Supports(ResourceCoverage coverage) { return coverage != null && coverage.Kind == ResourceCoverageKinds.CellRange; }
        public bool Intersects(ResourceCoverage observation, ResourceCoverage impact)
        {
            if (impact == null || impact.Kind != ResourceCoverageKinds.CellRange) return true;
            int[] first, second;
            string firstSheet, secondSheet;
            if (!TryRange(observation.Address, out firstSheet, out first) ||
                !TryRange(impact.Address, out secondSheet, out second)) return true;
            if (firstSheet.Length > 0 && secondSheet.Length > 0 &&
                !string.Equals(firstSheet, secondSheet, StringComparison.OrdinalIgnoreCase)) return false;
            return first[0] <= second[2] && second[0] <= first[2] && first[1] <= second[3] && second[1] <= first[3];
        }
        private static bool TryRange(string address, out string sheet, out int[] bounds)
        {
            sheet = string.Empty;
            bounds = null;
            address = (address ?? string.Empty).Replace("$", string.Empty);
            var separator = address.LastIndexOf('!');
            if (separator >= 0) { sheet = address.Substring(0, separator).Trim('\''); address = address.Substring(separator + 1); }
            var parts = address.Split(':');
            if (parts.Length > 2) return false;
            int r1, c1, r2, c2;
            if (!Cell(parts[0], out r1, out c1) || !Cell(parts[parts.Length - 1], out r2, out c2)) return false;
            bounds = new[] { Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2) };
            return true;
        }
        private static bool Cell(string text, out int row, out int column)
        {
            row = column = 0;
            var index = 0;
            foreach (var value in text.ToUpperInvariant())
            {
                if (value < 'A' || value > 'Z') break;
                if (column > 16384) return false;
                column = column * 26 + value - 'A' + 1;
                index++;
            }
            return column > 0 && column <= 16384 && index < text.Length &&
                int.TryParse(text.Substring(index), out row) && row > 0 && row <= 1048576;
        }
    }

    public sealed class EvidenceStateReducer
    {
        private readonly IReadOnlyList<IResourceImpactMatcher> _matchers;
        public EvidenceStateReducer(IEnumerable<IResourceImpactMatcher> matchers = null)
        {
            _matchers = (matchers ?? new IResourceImpactMatcher[] {
                new ExcelResourceImpactMatcher(), new OrdinalResourceImpactMatcher() }).ToArray();
        }

        public EvidenceProjection Reduce(ResourceEvidence evidence, ResourceAuthoritySnapshotSet authorities)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            var scope = authorities == null ? null : authorities.Get(evidence.ScopeId);
            if (scope == null) return Result(evidence, EvidenceState.Unavailable, "authority scope unavailable");
            var head = scope.GetHead(evidence.Resource.Identity);
            if (head == null && !evidence.Immutable) return Result(evidence, EvidenceState.Unknown, "resource head not observed");
            if (head != null)
            {
                if (head.Knowledge == HeadKnowledge.Unknown) return Result(evidence, EvidenceState.Unknown, head.Cause ?? "head unknown");
                if (head.Knowledge == HeadKnowledge.Unavailable) return Result(evidence, EvidenceState.Unavailable, head.Cause ?? "resource unavailable");
                if (!string.Equals(head.Revision.Revision, evidence.Resource.Revision, StringComparison.Ordinal))
                    return Result(evidence, EvidenceState.Superseded, "head advanced");
            }
            foreach (var commit in scope.Commits.Where(item => item.NewGeneration > evidence.AuthorityGeneration))
            {
                var effect = commit.Effect;
                if (effect == null || effect.Outcome == ResourceEffectOutcome.VerifiedNoChange ||
                    effect.Outcome == ResourceEffectOutcome.FailedNoEffect) continue;
                if (!effect.Impacts.Any(impact => Affects(evidence, impact))) continue;
                return Result(evidence, effect.Outcome == ResourceEffectOutcome.UnknownAfterDispatch
                    ? EvidenceState.Unknown : EvidenceState.Superseded, "coverage intersects " + effect.Operation);
            }
            foreach (var dependency in evidence.Dependencies)
            {
                if (dependency.Kind == "immutable-snapshot") continue;
                var dependencyHead = authorities.Snapshots.Values.Select(item => item.GetHead(dependency.Resource.Identity))
                    .FirstOrDefault(item => item != null);
                if (dependencyHead == null || dependencyHead.Knowledge == HeadKnowledge.Unknown)
                    return Result(evidence, EvidenceState.Unknown, "dependency head unknown");
                if (dependencyHead.Knowledge == HeadKnowledge.Unavailable)
                    return Result(evidence, EvidenceState.Unavailable, "dependency unavailable");
                if (!string.Equals(dependencyHead.Revision.Revision, dependency.Resource.Revision, StringComparison.Ordinal))
                    return Result(evidence, EvidenceState.Superseded, "dependency changed");
            }
            return Result(evidence, EvidenceState.Current, "exact observation current");
        }

        private bool Affects(ResourceEvidence evidence, ResourceImpact impact)
        {
            if (impact == null) return false;
            var same = evidence.Resource.Identity.Equals(impact.Identity);
            if (!same && impact.Relation == ResourceImpactRelation.Subtree)
                same = evidence.Resource.Uri.StartsWith(impact.Identity.Uri.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (!same) return false;
            var matcher = _matchers.FirstOrDefault(item => item.Supports(evidence.Coverage));
            return matcher == null || matcher.Intersects(evidence.Coverage, impact.Coverage);
        }
        private static EvidenceProjection Result(ResourceEvidence evidence, EvidenceState state, string reason)
        { return new EvidenceProjection(evidence, state, reason); }
    }
}
