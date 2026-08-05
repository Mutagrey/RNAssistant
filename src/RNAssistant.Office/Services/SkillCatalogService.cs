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

        public List<SkillDefinition> SelectRelevantSkills(string userText, DocumentContext context, int maxCount)
        {
            var query = Normalize(userText);
            var scored = new List<ScoredSkill>();
            foreach (var skill in GetVisibleSkills().Where(s => s.Enabled))
            {
                var score = Score(skill, query);
                if (score > 0)
                {
                    scored.Add(new ScoredSkill { Skill = skill, Score = score });
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Skill.Host)
                .ThenBy(s => s.Skill.Id)
                .Take(maxCount <= 0 ? 5 : maxCount)
                .Select(s => s.Skill)
                .ToList();
        }

        private bool IsVisible(SkillDefinition skill)
        {
            return skill != null &&
                (string.Equals(skill.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(skill.Host, "Common", StringComparison.OrdinalIgnoreCase));
        }

        private int Score(SkillDefinition skill, string query)
        {
            var score = 0;
            var haystack = Normalize((skill.Id ?? string.Empty) + " " + (skill.Name ?? string.Empty) + " " + (skill.Description ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(query) && ContainsAnyWord(query, haystack))
            {
                score += 2;
            }

            foreach (var tag in skill.Tags ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(query) && query.IndexOf(Normalize(tag), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 3;
                }
            }

            if (!string.IsNullOrWhiteSpace(query) &&
                !string.IsNullOrWhiteSpace(skill.Host) &&
                query.IndexOf(Normalize(skill.Host), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 1;
            }

            if (string.Equals(skill.Id, "common.task_planning", StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }

            return score;
        }

        private static bool ContainsAnyWord(string query, string haystack)
        {
            foreach (var word in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4 && haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').ToLowerInvariant();
        }

        private sealed class ScoredSkill
        {
            public SkillDefinition Skill { get; set; }
            public int Score { get; set; }
        }
    }
}
