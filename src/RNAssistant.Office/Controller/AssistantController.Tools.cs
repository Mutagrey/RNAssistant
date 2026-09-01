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

        public SkillLibraryResponse GetSkills()
        {
            return SkillLibraryResponse.From(
                _skillCatalog.GetVisibleSkills());
        }

        public SkillLibraryMutationResponse SaveSkills(
            SaveSkillsPayload payload)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                var mutations = ValidateSkillLibraryPayload(payload);
                var results = new List<SkillMutationResultDto>();
                foreach (var mutation in mutations)
                {
                    var result = _toolExecutor.ExecuteSkillLibraryMutation(
                        mutation);
                    results.Add(SkillMutationResultDto.From(result));
                    if (result.Outcome.Status !=
                        SkillAuthoringOutcomeStatus.Ok) break;
                }
                return new SkillLibraryMutationResponse
                {
                    Type = SkillLibraryMutationResponse.ContractType,
                    ContractVersion =
                        SkillLibraryResponse.CurrentContractVersion,
                    Results = results,
                    Library = GetSkills()
                };
            }
        }

        public SkillReferenceResponse ReadSkillReference(
            SkillReferencePayload payload)
        {
            ValidateSkillReferencePayload(payload);
            var read = _toolExecutor.ReadSkillLibraryReference(
                payload.SkillId, payload.Path,
                payload.ExpectedPackageRevision);
            return new SkillReferenceResponse
            {
                Type = SkillReferenceResponse.ContractType,
                ContractVersion =
                    SkillLibraryResponse.CurrentContractVersion,
                Result = SkillMutationResultDto.Read(
                    read.Package.Id, read.Reference.Path,
                    read.Package.Revision),
                Skill = SkillPackageDto.From(read.Package),
                Path = read.Reference.Path,
                Content = read.Content,
                Deleted = false,
                Reference = SkillReferenceDto.From(read.Reference)
            };
        }

        public SkillReferenceResponse SaveSkillReference(
            SaveSkillReferencePayload payload)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                ValidateSkillReferencePayload(payload);
                var result = _toolExecutor
                    .ExecuteSkillLibraryReferenceMutation(
                        "upsert", payload.SkillId, payload.Path,
                        payload.Content,
                        payload.ExpectedPackageRevision);
                return SkillReferenceMutationResult(
                    result, payload.Path, payload.Content, false);
            }
        }

        public SkillReferenceResponse DeleteSkillReference(
            SkillReferencePayload payload)
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                ValidateSkillReferencePayload(payload);
                var result = _toolExecutor
                    .ExecuteSkillLibraryReferenceMutation(
                        "delete", payload.SkillId, payload.Path,
                        null, payload.ExpectedPackageRevision);
                return SkillReferenceMutationResult(
                    result, payload.Path, null, true);
            }
        }

        private static IReadOnlyList<SkillLibraryCoreMutation>
            ValidateSkillLibraryPayload(SaveSkillsPayload payload)
        {
            if (payload == null ||
                !string.Equals(payload.Type,
                    SaveSkillsPayload.ContractType,
                    StringComparison.Ordinal) ||
                payload.ContractVersion !=
                    SkillLibraryResponse.CurrentContractVersion)
            {
                throw new InvalidOperationException(
                    "Unsupported Skill Library mutation contract.");
            }
            var source = payload.Mutations ??
                new List<SkillCoreMutationPayload>();
            if (source.Count > 256)
                throw new InvalidOperationException(
                    "Skill Library mutation limit exceeded: 256.");
            var baseIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var targetIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var result = new List<SkillLibraryCoreMutation>();
            foreach (var item in source)
            {
                if (item == null ||
                    !string.Equals(item.Kind, "upsert",
                        StringComparison.Ordinal) &&
                    !string.Equals(item.Kind, "delete",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Skill Library mutation kind is invalid.");
                }
                var baseId = item.BaseId ?? string.Empty;
                var expected = item.ExpectedRevision ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(baseId) &&
                    !baseIds.Add(baseId))
                {
                    throw new InvalidOperationException(
                        "Duplicate Skill Library base id: " + baseId);
                }
                if (string.Equals(item.Kind, "delete",
                    StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(baseId) ||
                        string.IsNullOrWhiteSpace(expected))
                    {
                        throw new InvalidOperationException(
                            "Skill delete requires baseId and expectedRevision.");
                    }
                    result.Add(new SkillLibraryCoreMutation
                    {
                        Kind = item.Kind,
                        BaseId = baseId,
                        ExpectedRevision = expected
                    });
                    continue;
                }
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    !targetIds.Add(item.Id))
                {
                    throw new InvalidOperationException(
                        "Skill upsert id is missing or duplicated: " +
                        (item.Id ?? string.Empty));
                }
                if (string.IsNullOrWhiteSpace(baseId) !=
                    string.IsNullOrWhiteSpace(expected))
                {
                    throw new InvalidOperationException(
                        "Existing skill upsert requires both baseId and expectedRevision; a new skill requires neither.");
                }
                result.Add(new SkillLibraryCoreMutation
                {
                    Kind = item.Kind,
                    BaseId = baseId,
                    ExpectedRevision = expected,
                    Intended = new SkillDefinition
                    {
                        Id = item.Id,
                        Host = item.Host,
                        Name = item.Name,
                        Description = item.Description,
                        Version = item.Version,
                        BodyMarkdown = item.BodyMarkdown,
                        Enabled = item.Enabled,
                        BuiltIn = false
                    }
                });
            }
            return result;
        }

        private static void ValidateSkillReferencePayload(
            SkillReferencePayload payload)
        {
            if (payload == null || !string.Equals(payload.Type,
                    SkillReferencePayload.ContractType,
                    StringComparison.Ordinal) ||
                payload.ContractVersion !=
                    SkillLibraryResponse.CurrentContractVersion ||
                string.IsNullOrWhiteSpace(payload.SkillId) ||
                string.IsNullOrWhiteSpace(
                    payload.ExpectedPackageRevision))
            {
                throw new InvalidOperationException(
                    "Unsupported or incomplete skill reference contract.");
            }
        }

        private static SkillReferenceResponse SkillReferenceMutationResult(
            SkillManualMutationResult result,
            string path,
            string content,
            bool deleted)
        {
            var package = result == null ? null : result.Package;
            var reference = package == null ? null :
                package.References.FirstOrDefault(item => item != null &&
                    string.Equals(item.Path, path,
                        StringComparison.OrdinalIgnoreCase));
            return new SkillReferenceResponse
            {
                Type = SkillReferenceResponse.ContractType,
                ContractVersion =
                    SkillLibraryResponse.CurrentContractVersion,
                Result = SkillMutationResultDto.From(result),
                Skill = SkillPackageDto.From(package),
                Path = reference == null ? path : reference.Path,
                Content = deleted || result == null ||
                    result.Outcome.Status != SkillAuthoringOutcomeStatus.Ok
                        ? null : content ?? string.Empty,
                Deleted = deleted && result != null &&
                    result.Outcome.Status == SkillAuthoringOutcomeStatus.Ok,
                Reference = SkillReferenceDto.From(reference)
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
            return string.Equals(toolId, HtmlWorkspaceToolCatalog.UpsertToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.ApplyPatchToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.DeleteToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.SetActiveToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.BindDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.RefreshDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlWorkspaceToolCatalog.FreezeDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolCatalog.CreateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolCatalog.UpdateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, TaskListToolCatalog.CloseToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.CreateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.UpdateToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.RestoreToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, PlanDocumentToolCatalog.DeleteToolId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
