using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Tools
{
    // Pure next-request admission; the preview result is never execution evidence.
    public delegate string SkillSelectionAdmission(IReadOnlyList<SelectedSkill> selections, ChatMessage previewResult);

    public sealed class SkillLoadToolHandler : IReadOnlyToolHandler
    {
        public const string ToolId = "common.skills_load";
        public static ToolBinding Binding { get { return new ToolBinding("conversation.skill-load.v1"); } }
        private readonly ChatSession _session;
        private readonly Func<SkillCatalogSnapshot> _skills;
        private readonly Func<ToolPackSnapshot> _tools;
        private readonly AppSettings _settings;
        private readonly SkillSelectionAdmission _admit;

        public SkillLoadToolHandler(ChatSession session, Func<SkillCatalogSnapshot> skills,
            Func<ToolPackSnapshot> tools, AppSettings settings, SkillSelectionAdmission admit = null)
        { _session = session; _skills = skills; _tools = tools; _settings = settings; _admit = admit; }

        public static ToolCatalogEntry Entry()
        {
            return new ToolCatalogEntry { Id = ToolId, Name = "Подключить навык", Host = "Common", Scope = "session",
                Description = "Select or remove one published skill for this chat. Call alone; complete instructions and compatible tools take effect next request. Reuse current SELECTED_SKILL; load grants no mutation permission.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact published skill id.\",\"maxLength\":128},\"action\":{\"type\":\"string\",\"description\":\"Load a compatible procedure, or remove a selection to resolve a conflict. Takes effect next request.\",\"enum\":[\"load\",\"remove\"],\"default\":\"load\"}},\"required\":[\"id\"],\"additionalProperties\":false}",
                BuiltIn = true, Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, false, new[] { "agent", "plan" }),
                Binding = Binding };
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                object action;
                if (context.Arguments.TryGetValue("action", out action) && (string)action == "remove")
                {
                    if (_session?.LastRun == null) throw new InvalidOperationException("An active task is required.");
                    var id = (string)context.Arguments["id"];
                    _session.LastRun.SelectedSkills = (_session.LastRun.SelectedSkills ?? new System.Collections.Generic.List<SelectedSkill>())
                        .Where(item => item.Id != id).ToList();
                    return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Skill removed for the next request: " + id,
                        new JObject { ["id"] = id, ["status"] = "removed_for_next_request" }.ToString(Formatting.None))));
                }
                var selected = SkillTaskService.Select(_session, (string)context.Arguments["id"], _skills(), _tools(),
                    _settings, context.Execution.Call.Id, (candidate, pending) =>
                    {
                        if (_admit == null) return null;
                        var role = ToolResultRoles.Normalize(_settings.ToolResultRole);
                        var wire = ToolResultWire.Write(context.Execution.Call.Id, ToolId, SelectionResult(pending));
                        return _admit(candidate, new ChatMessage { Role = role, ToolResultRole = role,
                            ProtocolMessage = true, ToolCallId = context.Execution.Call.Id, ToolName = ToolId,
                            RunId = _session.LastRun.RunId, ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                            Content = role == ToolResultRoles.Tool ? wire : "TOOL_RESULT:\n" + wire });
                    });
                return Task.FromResult(new ToolHandlerResult(SelectionResult(selected)));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(ex.Message,
                    "{\"code\":\"skill_unavailable\"}"), recovery:
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan)));
            }
        }

        private static RuntimeResult SelectionResult(ArsenalSkill selected)
        {
            return RuntimeResult.Ok("Skill selected for the next request: " + selected.Name,
                new JObject { ["id"] = selected.Id, ["status"] = "selected_for_next_request", ["notice"] = selected.Reason,
                    ["tools"] = new JArray(selected.Tools), ["references"] = new JArray(selected.References) }.ToString(Formatting.None));
        }
    }
}
