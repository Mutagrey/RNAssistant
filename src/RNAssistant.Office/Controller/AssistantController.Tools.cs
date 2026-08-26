using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public IReadOnlyList<ToolDefinition> GetTools()
        {
            return _toolCatalog.GetVisibleTools();
        }

        public IReadOnlyList<ToolDefinition> SaveTools(IEnumerable<ToolDefinition> tools)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                var customTools = (tools ?? new ToolDefinition[0]).Where(s =>
                    s != null && !s.BuiltIn && !string.Equals(s.Scope, "document", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var tool in customTools)
                {
                    var validation = _toolExecutor.ValidateToolDefinition(tool);
                    if (!validation.Success)
                    {
                        throw new InvalidOperationException(validation.Message);
                    }
                }
                _toolStore.Save(customTools, _adapter.HostName);
                return GetTools();
            }
        }

        public IReadOnlyList<SkillDefinition> GetSkills()
        {
            return _skillCatalog.GetVisibleSkills();
        }

        public IReadOnlyList<SkillDefinition> SaveSkills(IEnumerable<SkillDefinition> skills)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                var custom = (skills ?? new SkillDefinition[0]).Where(s => s != null && !s.BuiltIn).ToList();
                var builtInIds = new HashSet<string>(
                    _skillCatalog.GetVisibleSkills().Where(s => s.BuiltIn).Select(s => s.Id),
                    StringComparer.OrdinalIgnoreCase);
                var collision = custom.FirstOrDefault(s => builtInIds.Contains(s.Id ?? string.Empty));
                if (collision != null) throw new InvalidOperationException("Built-in skill id is reserved: " + collision.Id);
                _skillStore.Save(custom, _adapter.HostName);
                return GetSkills();
            }
        }

        public SkillReferenceResponse ReadSkillReference(string skillId, string path)
        {
            var skill = RequireCustomSkill(skillId);
            string content;
            string error;
            SkillReferenceMetadata metadata;
            if (!_skillStore.TryReadReference(skill, path, out content, out metadata, out error))
            {
                throw new InvalidOperationException(error);
            }
            return SkillReferenceResult(skill, metadata, content, false);
        }

        public SkillReferenceResponse SaveSkillReference(string skillId, string path, string content)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                var skill = RequireCustomSkill(skillId);
                string error;
                SkillReferenceMetadata metadata;
                if (!_skillStore.TrySaveReference(skill, path, content, out metadata, out error))
                {
                    throw new InvalidOperationException(error);
                }
                return SkillReferenceResult(RequireCustomSkill(skillId), metadata, content ?? string.Empty, false);
            }
        }

        public SkillReferenceResponse DeleteSkillReference(string skillId, string path)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                var skill = RequireCustomSkill(skillId);
                string normalizedPath;
                string error = null;
                if (!RNAssistant.Core.Storage.SkillStore.TryNormalizeReferencePath(path, out normalizedPath) ||
                    !_skillStore.TryDeleteReference(skill, normalizedPath, out error))
                {
                    throw new InvalidOperationException(error ?? "Invalid skill reference path.");
                }
                return SkillReferenceResult(RequireCustomSkill(skillId), new SkillReferenceMetadata
                {
                    Path = normalizedPath,
                    ByteLength = 0,
                    Revision = string.Empty
                }, null, true);
            }
        }

        private SkillDefinition RequireCustomSkill(string id)
        {
            var skill = _skillStore.Load().FirstOrDefault(item => item != null &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (skill == null) throw new InvalidOperationException("Custom skill not found: " + (id ?? string.Empty));
            return skill;
        }

        private static SkillReferenceResponse SkillReferenceResult(
            SkillDefinition skill,
            SkillReferenceMetadata reference,
            string content,
            bool deleted)
        {
            return new SkillReferenceResponse
            {
                SkillId = skill == null ? string.Empty : skill.Id,
                Path = reference == null ? string.Empty : reference.Path,
                Content = content,
                Deleted = deleted,
                PackageRevision = SkillRevision.Compute(skill),
                Reference = reference,
                References = skill == null || skill.References == null
                    ? new List<SkillReferenceMetadata>()
                    : new List<SkillReferenceMetadata>(skill.References)
            };
        }

        public ToolResult RunTool(
            string toolId,
            IDictionary<string, object> arguments,
            bool dryRun,
            Action<string, string> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var settings = _settingsService.Load();
            var session = LoadSession(null);
            var tools = _toolCatalog.GetVisibleTools().Where(s => s.Enabled).ToList();
            var command = new ToolCommand { ToolId = toolId };
            foreach (var pair in arguments ?? new Dictionary<string, object>())
            {
                command.Arguments[pair.Key] = pair.Value;
            }

            ReportProgress(progress, dryRun ? "checking" : "executing", (dryRun ? "Проверяю tool: " : "Исполняю tool: ") + toolId);
            if (dryRun)
            {
                return _toolExecutor.Execute(
                    command,
                    tools,
                    settings,
                    true,
                    true,
                    OfficeToolExecutor.CreateIsolatedManualSession(session),
                    cancellationToken);
            }

            if (!_toolExecutor.RequiresSessionLeaseForManualRun(toolId, tools))
            {
                // Read-only library checks do not modify chat state and may safely use an
                // isolated snapshot while the active chat is waiting on the model/tools.
                var manualSnapshot = OfficeToolExecutor.CreateIsolatedManualSession(session);
                return _toolExecutor.Execute(command, tools, settings, false, true, manualSnapshot, cancellationToken);
            }

            if (_chatRuns.IsExternallyRunning(session.Id))
            {
                return ToolResult.Fail(
                    "A mutating library tool cannot run while this chat is active. Read-only tools can still be tested; stop the chat before testing document or local-state mutations.",
                    null,
                    "manual_tool_chat_busy",
                    true);
            }

            return WithReservedSession(session, current =>
            {
                var result = _toolExecutor.Execute(command, tools, settings, false, true, current, cancellationToken);
                if (IsSessionArtifactTool(toolId))
                {
                    SaveSessionChanges(current);
                }
                return result;
            });
        }

        private static void ReportProgress(Action<string, string> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message);
            }
        }

        private static bool IsSessionArtifactTool(string toolId)
        {
            return string.Equals(toolId, HtmlArtifactToolExecutor.UpsertToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.DeleteToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.SetActiveToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.BindDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.RefreshDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.FreezeDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolExecutor.CreateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolExecutor.UpdateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolExecutor.CloseToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolExecutor.CreateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolExecutor.UpdateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolExecutor.DeleteToolId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
