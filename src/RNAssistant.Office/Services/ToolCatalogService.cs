using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ToolCatalogService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolStore _toolStore;

        public ToolCatalogService(IOfficeApplicationAdapter adapter, OfficeToolExecutor toolExecutor, ToolStore toolStore)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _toolStore = toolStore;
        }

        public List<ToolDefinition> GetVisibleTools()
        {
            var result = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in _adapter.GetBuiltInTools() ?? new ToolDefinition[0])
            {
                result[skill.Id] = skill;
            }

            foreach (var tool in _toolExecutor.GetControllerTools())
            {
                result[tool.Id] = tool;
            }

            foreach (var tool in _toolStore.Load().Where(s =>
                string.Equals(s.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, "Common", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrWhiteSpace(tool.Id))
                {
                    result[tool.Id] = tool;
                }
            }

            return result.Values.OrderBy(s => s.Host).ThenBy(s => s.Id).ToList();
        }
    }
}
