using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Tools
{
    public sealed class SkillLoadToolHandler : IReadOnlyToolHandler
    {
        public const string ToolId = "common.skills_load";
        public static ToolBinding Binding { get { return new ToolBinding("conversation.skill-load.v1"); } }
        private readonly ChatSession _session;
        private readonly Func<SkillCatalogSnapshot> _skills;
        private readonly Func<ToolPackSnapshot> _tools;
        private readonly AppSettings _settings;

        public SkillLoadToolHandler(ChatSession session, Func<SkillCatalogSnapshot> skills,
            Func<ToolPackSnapshot> tools, AppSettings settings)
        { _session = session; _skills = skills; _tools = tools; _settings = settings; }

        public static ToolCatalogEntry Entry()
        {
            return new ToolCatalogEntry { Id = ToolId, Name = "Подключить навык", Host = "Common", Scope = "session",
                Description = "Select a published skill for this task. Call alone, one skill per response. The next request includes its complete instructions and compatible tools. Resource reads and viewing SKILL.md do not select skills. Loading grants no mutation permission.",
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
                    _settings, context.Execution.Call.Id);
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Ok("Skill selected for the next request: " + selected.Name,
                    new JObject { ["id"] = selected.Id, ["status"] = "selected_for_next_request", ["notice"] = selected.Reason,
                        ["tools"] = new JArray(selected.Tools), ["references"] = new JArray(selected.References) }.ToString(Formatting.None))));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(new ToolHandlerResult(RuntimeResult.Error(ex.Message,
                    "{\"code\":\"skill_unavailable\"}")));
            }
        }
    }
}
