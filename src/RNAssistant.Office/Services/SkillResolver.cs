using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class SkillResolution
    {
        public List<SkillDefinition> Skills { get; set; }
        public string Error { get; set; }

        public bool Success { get { return string.IsNullOrWhiteSpace(Error); } }

        public SkillResolution()
        {
            Skills = new List<SkillDefinition>();
        }
    }

    internal static class SkillResolver
    {
        public static SkillResolution Resolve(
            IEnumerable<SkillDefinition> catalog,
            IEnumerable<string> requestedIds,
            string taskText = null)
        {
            var result = new SkillResolution();
            var byId = (catalog ?? new SkillDefinition[0])
                .Where(skill => skill != null && skill.Enabled && !string.IsNullOrWhiteSpace(skill.Id))
                .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var requested = (requestedIds ?? new string[0])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Any(id => string.Equals(id, "common.skill_authoring", StringComparison.OrdinalIgnoreCase)) &&
                AgentText.ContainsAny(taskText, "executable", "исполня", "tool", "инструмент", "pipeline", "vba"))
            {
                requested.Add("common.tool_authoring");
            }

            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in requested)
            {
                string error;
                if (!AddWithDependencies(id, byId, result.Skills, visiting, added, out error))
                {
                    result.Error = error;
                    result.Skills.Clear();
                    return result;
                }
            }

            for (var left = 0; left < result.Skills.Count; left++)
            {
                for (var right = left + 1; right < result.Skills.Count; right++)
                {
                    if (Conflicts(result.Skills[left], result.Skills[right]))
                    {
                        result.Error = "Skill conflict: " + result.Skills[left].Id + " and " + result.Skills[right].Id + ".";
                        result.Skills.Clear();
                        return result;
                    }
                }
            }
            return result;
        }

        public static List<string> ReadIds(IDictionary<string, object> arguments)
        {
            object raw;
            if (arguments == null || !arguments.TryGetValue("ids", out raw) || raw == null)
            {
                return new List<string>();
            }
            var array = raw as JArray;
            if (array != null)
            {
                return array.Select(value => Convert.ToString(value)).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            }
            var strings = raw as IEnumerable<string>;
            if (strings != null)
            {
                return strings.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            }
            var enumerable = raw as IEnumerable;
            if (!(raw is string) && enumerable != null)
            {
                var result = new List<string>();
                foreach (var value in enumerable)
                {
                    var text = Convert.ToString(value);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
                return result;
            }
            var rawText = Convert.ToString(raw) ?? string.Empty;
            if (raw is string && rawText.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var parsed = JArray.Parse(rawText);
                    return parsed.Select(value => Convert.ToString(value))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                }
                catch (Newtonsoft.Json.JsonException)
                {
                }
            }
            return rawText
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        public static bool ValidateDefinition(IEnumerable<SkillDefinition> catalog, SkillDefinition candidate, out string error)
        {
            error = null;
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
            {
                error = "Skill id is required.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(candidate.Description))
            {
                error = "Skill description must state what the skill does and when it applies.";
                return false;
            }
            if ((candidate.Requires ?? new List<string>()).Any(id => string.Equals(id, candidate.Id, StringComparison.OrdinalIgnoreCase)) ||
                (candidate.Conflicts ?? new List<string>()).Any(id => string.Equals(id, candidate.Id, StringComparison.OrdinalIgnoreCase)))
            {
                error = "A skill cannot require or conflict with itself.";
                return false;
            }

            var validationCandidate = candidate.Enabled ? candidate : CloneEnabled(candidate);
            var merged = (catalog ?? new SkillDefinition[0])
                .Where(skill => skill != null && !string.Equals(skill.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { validationCandidate })
                .ToList();
            var ids = new HashSet<string>(merged.Where(skill => skill.Enabled).Select(skill => skill.Id), StringComparer.OrdinalIgnoreCase);
            var unknownReference = (candidate.Requires ?? new List<string>())
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id) && !ids.Contains(id));
            if (!string.IsNullOrWhiteSpace(unknownReference))
            {
                error = "Skill metadata references an unknown or disabled skill: " + unknownReference + ".";
                return false;
            }
            var resolution = Resolve(merged, new[] { candidate.Id });
            if (!resolution.Success)
            {
                error = resolution.Error;
                return false;
            }
            if (!candidate.Enabled)
            {
                var dependent = (catalog ?? new SkillDefinition[0]).FirstOrDefault(skill => skill != null && skill.Enabled &&
                    !string.Equals(skill.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                    (skill.Requires ?? new List<string>()).Any(id => string.Equals(id, candidate.Id, StringComparison.OrdinalIgnoreCase)));
                if (dependent != null)
                {
                    error = "Skill " + candidate.Id + " is required by enabled skill " + dependent.Id + ".";
                    return false;
                }
            }
            return true;
        }

        private static SkillDefinition CloneEnabled(SkillDefinition source)
        {
            return new SkillDefinition
            {
                Id = source.Id,
                Host = source.Host,
                Name = source.Name,
                Description = source.Description,
                Version = source.Version,
                Tags = new List<string>(source.Tags ?? new List<string>()),
                AppliesTo = new List<string>(source.AppliesTo ?? new List<string>()),
                Requires = new List<string>(source.Requires ?? new List<string>()),
                Conflicts = new List<string>(source.Conflicts ?? new List<string>()),
                ToolCapabilities = new List<string>(source.ToolCapabilities ?? new List<string>()),
                Resources = new List<string>(source.Resources ?? new List<string>()),
                TrustLevel = source.TrustLevel,
                BodyMarkdown = source.BodyMarkdown,
                StoragePath = source.StoragePath,
                Enabled = true,
                BuiltIn = source.BuiltIn
            };
        }

        public static List<SkillDefinition> ActiveSkills(ChatSession session, IEnumerable<SkillDefinition> catalog)
        {
            var catalogList = (catalog ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList();
            var visibleIds = new HashSet<string>(catalogList.Select(skill => skill.Id), StringComparer.OrdinalIgnoreCase);
            var requested = (session == null ? null : session.ActiveSkillIds) ?? new List<string>();
            var validRequested = requested.Where(id => !string.IsNullOrWhiteSpace(id) && visibleIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (session != null && validRequested.Count != requested.Count)
            {
                session.ActiveSkillIds = validRequested;
            }
            var resolved = Resolve(catalogList, validRequested);
            return resolved.Success ? resolved.Skills : new List<SkillDefinition>();
        }

        public static SkillResolution Activate(
            ChatSession session,
            IEnumerable<SkillDefinition> catalog,
            IEnumerable<string> requestedIds,
            string mode,
            string taskText = null)
        {
            var catalogList = (catalog ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList();
            var existing = session == null || session.ActiveSkillIds == null
                ? new List<string>()
                : session.ActiveSkillIds;
            var ids = requestedIds ?? new string[0];
            if (string.Equals(mode, "add", StringComparison.OrdinalIgnoreCase))
            {
                var visibleIds = new HashSet<string>(catalogList.Select(skill => skill.Id), StringComparer.OrdinalIgnoreCase);
                ids = existing.Where(id => !string.IsNullOrWhiteSpace(id) && visibleIds.Contains(id)).Concat(ids);
            }
            var resolution = Resolve(catalogList, ids, string.IsNullOrWhiteSpace(taskText) ? LatestUserText(session) : taskText);
            if (!resolution.Success || session == null) return resolution;
            var selected = resolution.Skills.Select(skill => skill.Id).ToList();
            session.ActiveSkillIds = selected;
            return resolution;
        }

        public static List<ToolDefinition> FilterTools(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> catalog,
            IEnumerable<SkillDefinition> activeSkills)
        {
            var skillList = (catalog ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList();
            if (skillList.Count == 0) return (tools ?? new ToolDefinition[0]).Where(tool => tool != null).ToList();
            var activeIds = new HashSet<string>((activeSkills ?? new SkillDefinition[0]).Where(skill => skill != null).Select(skill => skill.Id), StringComparer.OrdinalIgnoreCase);
            var result = new List<ToolDefinition>();
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null) continue;
                if (string.Equals(tool.Id, "common.skills_load", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(tool);
                    continue;
                }
                var owners = skillList.Where(skill => (skill.ToolCapabilities ?? new List<string>()).Any(capability => MatchesCapability(tool.Id, capability))).ToList();
                if (tool.BuiltIn)
                {
                    owners = owners.Where(skill => skill.BuiltIn).ToList();
                }
                if (owners.Count == 0 || owners.Any(skill => activeIds.Contains(skill.Id))) result.Add(tool);
            }
            return result;
        }

        public static void ActivateExplicitMentions(ChatSession session, string userText, IEnumerable<SkillDefinition> catalog)
        {
            if (session == null || string.IsNullOrWhiteSpace(userText)) return;
            var ids = (catalog ?? new SkillDefinition[0])
                .Where(skill => skill != null && !string.IsNullOrWhiteSpace(skill.Id) &&
                    userText.IndexOf(skill.Id, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(skill => skill.Id)
                .ToList();
            if (ids.Count == 0) return;
            Activate(session, catalog, ids, "add", userText);
        }

        private static string LatestUserText(ChatSession session)
        {
            return session == null || session.Messages == null
                ? string.Empty
                : session.Messages.LastOrDefault(message => message != null && !message.ProtocolMessage &&
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        }

        private static bool AddWithDependencies(
            string id,
            IDictionary<string, SkillDefinition> byId,
            ICollection<SkillDefinition> result,
            ISet<string> visiting,
            ISet<string> added,
            out string error)
        {
            error = null;
            if (added.Contains(id)) return true;
            SkillDefinition skill;
            if (!byId.TryGetValue(id, out skill))
            {
                error = "Unknown or disabled skill: " + id + ".";
                return false;
            }
            if (!visiting.Add(id))
            {
                error = "Cyclic skill dependency at " + id + ".";
                return false;
            }
            foreach (var dependency in skill.Requires ?? new List<string>())
            {
                if (!AddWithDependencies(dependency, byId, result, visiting, added, out error)) return false;
            }
            visiting.Remove(id);
            if (added.Add(id)) result.Add(skill);
            return true;
        }

        private static bool Conflicts(SkillDefinition left, SkillDefinition right)
        {
            return (left.Conflicts ?? new List<string>()).Any(id => string.Equals(id, right.Id, StringComparison.OrdinalIgnoreCase)) ||
                (right.Conflicts ?? new List<string>()).Any(id => string.Equals(id, left.Id, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesCapability(string toolId, string capability)
        {
            if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(capability)) return false;
            capability = capability.Trim();
            return capability.EndsWith("_", StringComparison.Ordinal)
                ? toolId.StartsWith(capability, StringComparison.OrdinalIgnoreCase)
                : toolId.IndexOf(capability, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
