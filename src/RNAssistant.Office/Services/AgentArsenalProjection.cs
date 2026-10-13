using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal static class AgentArsenalProjection
    {
        internal static AgentArsenal Build(ChatSession session, AgentArsenal receipt,
            IReadOnlyList<ArsenalSkill> metadata, IReadOnlyList<ToolCatalogEntry> previewCatalog)
        {
            var view = receipt == null ? new AgentArsenal() :
                JsonConvert.DeserializeObject<AgentArsenal>(JsonConvert.SerializeObject(receipt));
            var selected = session.LastRun?.SelectedSkills ?? new List<SelectedSkill>();
            view.Selected = selected.Select(item =>
            {
                var old = view.Selected.FirstOrDefault(value => value.Id == item.Id && value.Revision == item.Revision);
                var source = metadata.FirstOrDefault(value => value.Id == item.Id);
                return new ArsenalSkill { Id = item.Id, Name = source?.Name ?? item.Id,
                    Description = source?.Description, Revision = item.Revision,
                    Status = source == null || source.Revision != item.Revision ? "unavailable" : old?.Status ?? "pending",
                    Reason = source == null || source.Revision != item.Revision ? "Выбранная версия недоступна; требуется новое подключение." : old?.Reason,
                    Tools = item.Tools.Select(tool => tool.Id).ToList(), References = source?.References ?? new List<string>() };
            }).ToList();
            if (receipt == null)
            {
                // A metadata-only preview; no handlers, hashes or bodies are loaded.
                var policy = ConversationRunPolicy.For(session.Mode, session.MutationPolicy);
                var catalog = policy.SelectTools(previewCatalog);
                view.Policy = policy.Mode + "/" + policy.MutationPolicy;
                view.Tools = CallableToolPack.Create(policy.Mode, session.Host, session.LastRun?.RunId, catalog).Tools
                    .Select(tool => new ArsenalTool { Id = tool.Id, Name = tool.Name, Description = tool.Description,
                        Origin = "direct", RequiresConfirmation = tool.Policy.RequiresConfirmation }).ToList();
                var ids = new HashSet<string>(catalog.Select(tool => tool.Id));
                view.Available = metadata.Select(skill => new ArsenalSkill { Id = skill.Id, Name = skill.Name,
                    Description = skill.Description, Revision = skill.Revision, Tools = skill.Tools, References = skill.References,
                    Status = skill.Tools.All(ids.Contains) ? "available" : "unavailable",
                    Reason = skill.Tools.All(ids.Contains) ? null : "Нет необходимых инструментов в текущем режиме или среде." }).ToList();
            }
            return view;
        }
    }
}
