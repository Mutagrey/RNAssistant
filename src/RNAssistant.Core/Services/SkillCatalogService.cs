using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class SkillCatalogService
    {
        private readonly string _host;
        private readonly Func<SkillCatalogSnapshot> _published;
        private readonly object _sync = new object();
        private string _sourceGeneration;
        private SkillCatalogSnapshot _snapshot;

        public SkillCatalogService(string host, Func<SkillCatalogSnapshot> published)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _published = published ?? throw new ArgumentNullException(nameof(published));
        }

        public List<SkillDefinition> GetVisibleSkills()
        { return Capture().Skills.ToList(); }

        public SkillCatalogSnapshot Capture()
        {
            var published = _published();
            lock (_sync)
            {
                if (_sourceGeneration == published.Generation) return _snapshot;
                _snapshot = SelectPublished(published);
                _sourceGeneration = published.Generation;
                return _snapshot;
            }
        }

        public SkillCatalogSnapshot SelectPublished(SkillCatalogSnapshot published)
        { return new SkillCatalogSnapshot(BuildVisible(published.Skills), published.Generation); }

        public static ResourceFindPage Find(SkillCatalogSnapshot snapshot, string query, int limit = 50)
        {
            var matches = snapshot.Skills.Where(skill => skill.Enabled &&
                (string.IsNullOrWhiteSpace(query) || (skill.Id + " " + skill.Name + " " + skill.Description)
                    .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            var items = matches.Take(Math.Max(1, Math.Min(50, limit))).Select(skill => {
                var descriptor = SkillPublicationService.DescribeSkill(skill.Publication, skill, null);
                return new ResourceFindCandidate { Target = skill.Id, Type = "skill", Scope = "catalog",
                    Title = skill.Name, Description = skill.Description, MimeType = "text/markdown",
                    Mutable = false, Representations = new List<string> { "text" },
                    Usage = "Read with type=skill and this target before applying the instructions.",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }).ToList();
            return new ResourceFindPage { Scope = "catalog", Query = query, Items = items, Total = matches.Length,
                Complete = items.Count == matches.Length, Empty = matches.Length == 0,
                RefineQuery = items.Count < matches.Length,
                AvailabilityHint = items.Count < matches.Length ? "Skill catalog is bounded; narrow the query." : null,
                ResourceRefs = items.Select(item => item.Reference).ToList() };
        }

        private List<SkillDefinition> BuildVisible(IReadOnlyList<SkillDefinition> published)
        {
            var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in published.Where(IsVisible))
            {
                if (!string.IsNullOrWhiteSpace(skill.Id) && !result.ContainsKey(skill.Id))
                {
                    result[skill.Id] = skill;
                }
            }

            return result.Values.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }

        private bool IsVisible(SkillDefinition skill)
        {
            return skill != null &&
                (string.Equals(skill.Host, _host, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(skill.Host, "Common", StringComparison.OrdinalIgnoreCase));
        }
    }
}
