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
        private readonly SkillStore _skillStore;

        public SkillCatalogService(IOfficeApplicationAdapter adapter, SkillStore skillStore)
        {
            _adapter = adapter;
            _skillStore = skillStore;
        }

        public List<SkillDefinition> GetVisibleSkills()
        {
            var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in BuiltInSkillProvider.GetSkills(_adapter).Where(IsVisible))
            {
                result[skill.Id] = skill;
            }

            foreach (var skill in _skillStore.Load().Where(IsVisible))
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
