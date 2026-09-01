using System.Collections.Generic;
using Newtonsoft.Json;

namespace RNAssistant.Core.Tools
{
    // Mutable catalog/package projection. Execution authority is captured only
    // as immutable ToolDescriptor/ToolPolicy/ToolBinding registrations.
    public sealed class ToolCatalogEntry
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ArgumentSchemaJson { get; set; }
        public string Executor { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool MutatesDocument { get; set; }
        public bool MutatesLocalState { get; set; }
        public bool CanSourceHtmlData { get; set; }
        public bool AgentCanRun { get; set; }

        [JsonIgnore]
        public ToolPolicy Policy { get; set; }

        [JsonIgnore]
        public ToolBinding Binding { get; set; }

        public string Code { get; set; }
        public string Readme { get; set; }
        public string StoragePath { get; set; }
        public bool Enabled { get; set; }
        public bool BuiltIn { get; set; }
        public int RiskLevel { get; set; }
        public string UseWhen { get; set; }
        public string DoNotUseWhen { get; set; }
        public string CapabilityStatus { get; set; }
        public string Limitations { get; set; }
        public string PackageVersion { get; set; }
        public string EntryPoint { get; set; }
        public List<string> ArgumentOrder { get; set; }
        public List<ToolPackageComponentDefinition> Components { get; set; }
        public string Scope { get; set; }
        public string InstallationStatus { get; set; }

        public ToolCatalogEntry()
        {
            Enabled = true;
            Executor = "builtin";
            ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
            AgentCanRun = true;
            CapabilityStatus = "available";
            PackageVersion = "1.0.0";
            ArgumentOrder = new List<string>();
            Components = new List<ToolPackageComponentDefinition>();
            Scope = "global";
        }

        public ToolCatalogEntry Clone()
        {
            var clone = (ToolCatalogEntry)MemberwiseClone();
            clone.ArgumentOrder = new List<string>(
                ArgumentOrder ?? new List<string>());
            clone.Components = new List<ToolPackageComponentDefinition>();
            foreach (var component in Components ??
                new List<ToolPackageComponentDefinition>())
            {
                clone.Components.Add(component == null
                    ? null : component.Clone());
            }
            return clone;
        }
    }

    public sealed class ToolPackageComponentDefinition
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string FileName { get; set; }
        public string Code { get; set; }
        public string CodeSha256 { get; set; }

        public ToolPackageComponentDefinition Clone()
        {
            return (ToolPackageComponentDefinition)MemberwiseClone();
        }
    }
}
