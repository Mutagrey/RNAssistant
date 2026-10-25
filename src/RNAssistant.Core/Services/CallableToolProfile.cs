using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Services
{
    // Selects direct request inputs from an already published host catalog.
    // This is neither a registry nor execution authority. SkillTaskService owns
    // selected dependencies; the caller owns validation/persistence of admissions.
    public sealed class CallableToolProfile
    {
        private readonly bool _allDirect;
        private readonly HashSet<string> _initialIds;

        private CallableToolProfile(string id, bool allDirect, IEnumerable<string> initialIds)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A profile ID is required.", nameof(id));
            Id = id;
            _allDirect = allDirect;
            _initialIds = new HashSet<string>(initialIds ?? new string[0], StringComparer.Ordinal);
        }

        public string Id { get; }

        public static CallableToolProfile Explicit(string id, IEnumerable<string> initialIds)
        {
            return new CallableToolProfile(id, false, initialIds);
        }

        public static CallableToolProfile AllDirect(string id)
        {
            return new CallableToolProfile(id, true, null);
        }

        public IReadOnlyList<ToolCatalogEntry> Select(
            IReadOnlyList<ToolCatalogEntry> catalog, IEnumerable<string> admittedIds = null)
        {
            var active = new HashSet<string>(_initialIds, StringComparer.Ordinal);
            if (admittedIds != null) active.UnionWith(admittedIds);
            // Preserve catalog order and exact entries. Availability/mode filtering
            // belongs to catalog publication; never infer permissions from exposure.
            return (catalog ?? new ToolCatalogEntry[0])
                .Where(tool => tool.Exposure != ToolExposure.Skill &&
                    (_allDirect || active.Contains(tool.Id))).ToArray();
        }
    }
}
