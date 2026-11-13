using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Application
{
    public sealed partial class PluginApplicationService
    {
        internal IReadOnlyList<WorkspaceToolContribution> CaptureTools(WorkspaceDescriptor workspace, ChatSession session = null)
        {
            var tools = new List<WorkspaceToolContribution>();
            lock (_sync)
            {
                foreach (var plugin in _store.List().Where(item => !item.Hidden))
                {
                    if (plugin.SourceWorkspaceId == workspace.WorkspaceId)
                    {
                        tools.Add(Tool("plugins.check", "Validate and retain this run's source snapshot. Does not save a version, activate code or verify business behavior.",
                            EmptySchema(), false, new PluginTool(this, plugin.PluginId, "check", null, null, session?.Id, workspace.WorkspaceId)));
                        tools.Add(Tool("plugins.save", "Save this run's checked/current source as a version. Does not activate code or grant data access. Returns a semantic version ordinal.",
                            EmptySchema(), true, new PluginTool(this, plugin.PluginId, "save", null, null, session?.Id, workspace.WorkspaceId)));
                        tools.Add(Tool("plugins.versions", "List up to 12 saved versions by ordinal/label, newest first. Check snapshots are not saved versions.",
                            JObject.Parse("{\"type\":\"object\",\"properties\":{\"before\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"List versions older than this ordinal.\"}},\"required\":[],\"additionalProperties\":false}"),
                            false, new PluginTool(this, plugin.PluginId, "versions", null, null, session?.Id, workspace.WorkspaceId)));
                        tools.Add(Tool("plugins.apply", "Activate this run's last saved version, or an explicitly selected saved ordinal/unique label. Requires Save first when version is omitted. Source edits and other actors' saves do not retarget the selected version.",
                            JObject.Parse("{\"type\":\"object\",\"properties\":{\"version\":{\"type\":\"string\",\"description\":\"Saved version ordinal as text (for example 3), or unique label from plugins.versions.\"}},\"required\":[],\"additionalProperties\":false}"),
                            true, new PluginTool(this, plugin.PluginId, "apply", null, null, session?.Id, workspace.WorkspaceId)));
                    }
                    if (!plugin.Enabled || plugin.ActiveReleaseId == null) continue;
                    _store.VersionState(plugin.PluginId);
                    var active = RequireRegistration(plugin.PluginId);
                    var release = _store.ReadSavedRelease(plugin.PluginId, active.ActiveSavedVersionId);
                    foreach (var command in PluginPackageFiles.ParseManifest(release.ManifestJson).Commands)
                        tools.Add(Tool("plugins." + plugin.PluginId + "." + command.Id, command.Description, command.ArgumentsSchema, command.WritesData,
                            new PluginTool(this, plugin.PluginId, command.Id, active.ActiveReleaseId, active.ActiveSavedVersionId, session?.Id, workspace.WorkspaceId), true));
                }
            }
            return tools;
        }
        private static JObject EmptySchema()
        { return JObject.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"); }
        private static WorkspaceToolContribution Tool(string id, string description, JObject schema, bool write, IToolHandler handler, bool execute = false)
        {
            var policy = new ToolPolicy(write ? ToolEffect.Write : ToolEffect.Read, write ? ToolVerification.Tool : ToolVerification.None,
                write, false, write ? new[] { ChatModes.Agent } : new[] { ChatModes.Agent, ChatModes.Chat, ChatModes.Plan }, write ? 1 : 0, execute);
            return new WorkspaceToolContribution { Entry = new ToolCatalogEntry { Id = id, Name = id, Description = description,
                ArgumentSchemaJson = schema.ToString(Formatting.None), Policy = policy, Binding = new ToolBinding(id, scope: ((PluginTool)handler).BindingScope, host: "Common"),
                Host = "Common", Enabled = true, BuiltIn = true, AgentCanRun = true, MutatesLocalState = write, RequiresConfirmation = write }, Handler = handler };
        }
        private sealed class PluginTool : IManagedMutationToolHandler, IReadOnlyToolHandler, IPreparableToolHandler
        {
            private readonly PluginApplicationService _service;
            private readonly string _plugin, _command, _release, _version, _chat, _workspace;
            public string BindingScope { get { return _release == null ? "plugin-source-v2:" + _plugin : "plugin-version:" + _version; } }
            public PluginTool(PluginApplicationService service, string plugin, string command, string release, string version, string chat, string workspace)
            { _service = service; _plugin = plugin; _command = command; _release = release; _version = version; _chat = chat; _workspace = workspace; }
            public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_service._sync)
                {
                    _service.RequireOpen(); _service.PruneSavedIntents();
                    if (!_service.RunOwns(_plugin, _chat, context.Execution.RunId, _workspace))
                        throw new PluginException("plugin_run_changed", "This operation requires the exact current workspace session/run.");
                    if (_service._savedPreparations.Count >= 128) throw new PluginException("plugin_preparation_limit", "Finish pending operations before preparing another.");
                    var prepared = new SavedPreparation { Plugin = _plugin, Workspace = _workspace, Chat = _chat, Run = context.Execution.RunId,
                        Operation = "plugin_" + Guid.NewGuid().ToString("N") };
                    if (_release == null && _command == "save")
                    {
                        var actor = "run:" + _chat + ":" + prepared.Run;
                        var candidate = _service._candidates.SingleOrDefault(item => item.Value.Package.PluginId == _plugin && item.Value.Actor == actor);
                        prepared.Candidate = candidate.Key ?? _service.Check(_plugin, actor).CandidateId;
                        prepared.Content = _service.RequireCandidate(_plugin, prepared.Candidate).Package.PackageSha256;
                        prepared.Catalog = _service._store.VersionState(_plugin).StateRevision;
                    }
                    else if (_release == null && _command == "apply")
                    {
                        object semantic; context.Arguments.TryGetValue("version", out semantic);
                        prepared.Version = _service.SelectSavedVersion(_plugin, _chat, prepared.Run, semantic?.ToString()).VersionId;
                        var active = _service.RequireRegistration(_plugin);
                        prepared.ActiveVersion = active.ActiveSavedVersionId; prepared.ActiveRelease = active.ActiveReleaseId;
                        prepared.ActiveRevision = active.ActiveRevision;
                    }
                    else if (_release != null)
                    {
                        var active = _service.RequireRegistration(_plugin);
                        if (!active.Enabled || active.ActiveReleaseId != _release || active.ActiveSavedVersionId != _version)
                            throw new PluginException("plugin_version_changed", "Refresh the command catalog before continuing.");
                        prepared.Version = _version; prepared.ActiveRevision = active.ActiveRevision;
                        PluginDataGrant grant;
                        if (_service._data.TryGetValue(_plugin + ":" + _release, out grant))
                        { grant.Lifetime.Token.ThrowIfCancellationRequested(); prepared.Project = grant.ProjectId; }
                    }
                    _service._savedPreparations.Add(prepared.Operation, prepared);
                    return Task.FromResult(new ToolPreparationResult(ToolResult.Ok("Plugin operation prepared."), JsonConvert.SerializeObject(prepared)));
                }
            }
            public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
            {
                PluginInstance instance = null; var possible = false; var admitted = false; SavedPreparation prepared = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    lock (_service._sync)
                    {
                        var supplied = JsonConvert.DeserializeObject<SavedPreparation>(context.PreparedStateJson ?? "{}");
                        if (supplied.Contract != 2 || supplied.Operation == null || !_service._savedPreparations.TryGetValue(supplied.Operation, out prepared) ||
                            JsonConvert.SerializeObject(supplied) != JsonConvert.SerializeObject(prepared) || prepared.Plugin != _plugin || prepared.Chat != _chat ||
                            prepared.Run != context.Execution.RunId || !_service.RunOwns(_plugin, _chat, prepared.Run, _workspace))
                            throw new PluginException("plugin_preparation_missing", "The preparation expired or uses the old candidate contract. Prepare a new operation.");
                        admitted = true;
                    if (_release == null && _command == "check")
                        {
                            var checkedPackage = _service.Check(_plugin, "run:" + _chat + ":" + prepared.Run);
                            return new ToolHandlerResult(ToolResult.Ok("Checked snapshot retained; Save is required before Apply.", JsonConvert.SerializeObject(
                                new PluginCheckSummary { Files = checkedPackage.Files, Bytes = checkedPackage.Bytes, Checks = checkedPackage.Checks })), ToolEffectEvidence.None);
                        }
                        if (_release == null && _command == "versions")
                        {
                            object before; context.Arguments.TryGetValue("before", out before);
                            var bound = before == null ? long.MaxValue : Convert.ToInt64(before);
                            string cursor = null; var entries = new List<SavedVersionRecord>();
                            do { var page = _service._store.ListVersions(_plugin, cursor, 12); cursor = page.Cursor;
                                entries.AddRange(page.Items.Where(item => item.Ordinal < bound)); } while (entries.Count < 12 && cursor != null);
                            return new ToolHandlerResult(ToolResult.Ok("Saved versions.", JsonConvert.SerializeObject(entries.Take(12).Select(item =>
                                new PluginSavedVersionSummary { Ordinal = item.Ordinal, Label = item.Label, CreatedUtc = item.CreatedUtc,
                                    Active = item.VersionId == _service.RequireRegistration(_plugin).ActiveSavedVersionId }))), ToolEffectEvidence.None);
                        }
                        if (_release == null && (_command == "save" || _command == "apply"))
                        {
                            if (_command == "save" && _service.RequireCandidate(_plugin, prepared.Candidate).Actor != "run:" + _chat + ":" + prepared.Run)
                                throw new PluginException("plugin_candidate_owner", "The checked intent changed before saving.");
                            Action dispatch = () => { context.MarkDispatchPossible(); possible = true; };
                            var result = _command == "save"
                                ? _service.SavePluginVersion(_plugin, prepared.Catalog, prepared.Content, prepared.Operation,
                                    new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = _chat, RunId = prepared.Run }, dispatch, token)
                                : _service.ActivatePluginVersion(_plugin, prepared.Version, prepared.ActiveRevision, prepared.ActiveVersion,
                                    prepared.ActiveRelease, prepared.Operation, dispatch, token);
                            return new ToolHandlerResult(ToolResult.Ok(result.Outcome, JsonConvert.SerializeObject(new PluginSavedVersionSummary {
                                Ordinal = result.Version.Ordinal, Label = result.Version.Label, CreatedUtc = result.Version.CreatedUtc,
                                Active = _service.RequireRegistration(_plugin).ActiveSavedVersionId == result.VersionId })),
                                result.Outcome == "AlreadySaved" ? ToolEffectEvidence.VerifiedNoChange : ToolEffectEvidence.VerifiedChange);
                        }
                        if (_service.RequireRegistration(_plugin).ActiveRevision != prepared.ActiveRevision)
                            throw new PluginException("plugin_version_changed", "The active publication changed after preparation.");
                        instance = _service.CaptureCommandContext(_plugin, prepared.Project, true);
                        if (instance.Release.ReleaseId != _release || instance.SavedVersionId != _version)
                            throw new PluginException("plugin_version_changed", "The captured saved entry changed. Refresh the tool catalog.");
                    }
                    var resultCommand = await _service.InvokeAsync(instance, _command, context.Execution.Call.ArgumentsJson, token,
                        onDispatch: () => { context.MarkDispatchPossible(); possible = true; });
                    var writes = instance.Manifest.Commands.Single(value => value.Id == _command).WritesData;
                    return new ToolHandlerResult(ToolResult.Ok("Command completed.", resultCommand.Json),
                        writes ? (resultCommand.HadVerifiedWrites ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange) : ToolEffectEvidence.None);
                }
                catch (Exception error)
                {
                    var saved = error as SavedVersionException;
                    var unknown = saved != null ? saved.MayHavePublished : possible || (error as PluginException)?.Code == "effect_unknown";
                    var message = error is PluginException || saved != null ? error.Message : "The plugin operation could not be verified. Inspect the current source/version before trying again.";
                    return new ToolHandlerResult(unknown ? ToolResult.Unknown(message) : ToolResult.Error(message),
                        unknown ? ToolEffectEvidence.Unknown : possible ? ToolEffectEvidence.VerifiedNoChange : ToolEffectEvidence.None);
                }
                finally
                {
                    if (admitted && prepared != null) lock (_service._sync) _service._savedPreparations.Remove(prepared.Operation);
                    if (instance != null) await _service.CloseInstanceAsync(instance.Id);
                }
            }
        }
    }
}
