using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal static class VbaPackageToolAdapter
    {
        public static VbaPackageSourceDefinition ToSource(ToolDefinition tool)
        {
            if (tool == null) return null;
            return new VbaPackageSourceDefinition
            {
                Id = tool.Id,
                Host = tool.Host,
                Code = tool.Code,
                StoragePath = tool.StoragePath,
                Readme = tool.Readme,
                Components = (tool.Components ?? new List<VbaToolComponent>())
                    .Where(component => component != null)
                    .Select(component => new VbaPackageSourceComponent
                    {
                        Name = component.Name,
                        Type = component.Type,
                        FileName = component.FileName,
                        Code = component.Code
                    })
                    .ToList()
            };
        }
    }

}
