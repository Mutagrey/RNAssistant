using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    public sealed class SkillCatalogService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Func<SkillCatalogSnapshot> _published;
        private readonly object _sync = new object();
        private string _sourceGeneration;
        private SkillCatalogSnapshot _snapshot;

        public SkillCatalogService(IOfficeApplicationAdapter adapter, Func<SkillCatalogSnapshot> published)
        {
            _adapter = adapter;
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

        internal SkillCatalogSnapshot SelectPublished(SkillCatalogSnapshot published)
        { return new SkillCatalogSnapshot(BuildVisible(published.Skills), published.Generation); }

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
                (string.Equals(skill.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(skill.Host, "Common", StringComparison.OrdinalIgnoreCase));
        }
    }
}
