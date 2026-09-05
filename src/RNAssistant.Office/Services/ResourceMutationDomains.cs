using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Services
{
    // Domain intent algebra only. Currentness is exclusively EvidenceStateReducer's job.
    internal interface IResourceMutationDomain
    {
        bool Owns(string operation);
        IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot);
    }

    internal static class ResourceMutationDomains
    {
        private static readonly IResourceMutationDomain[] Domains = {
            new VbaResourceMutationDomain(), new CatalogResourceMutationDomain(),
            new ResourceDefinitionMutationDomain(),
            new ConversationResourceMutationDomain(), new OfficeResourceMutationDomain() };

        internal static ResourceAuthorityScopeId Scope(ResourceAuthorityService authority, ChatSession session, string operation)
        {
            if (new CatalogResourceMutationDomain().Owns(operation)) return new ResourceAuthorityScopeId("catalog", "local");
            return authority.Scope(session, !new ConversationResourceMutationDomain().Owns(operation) && !ResourceDefinitionToolHandler.Owns(operation));
        }

        internal static IReadOnlyList<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            return Domains.First(domain => domain.Owns(operation)).Impacts(scope, operation, arguments, snapshot)
                .GroupBy(impact => impact.Identity.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        }

        internal static string Argument(IDictionary<string, object> arguments, string key)
        {
            object value;
            return arguments != null && arguments.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }

        internal static string Provider(ResourceIdentity identity)
        {
            ResourceAddress address;
            return ResourceUri.TryParse(identity.Uri, out address) ? address.Provider : string.Empty;
        }
    }

    internal sealed class VbaResourceMutationDomain : IResourceMutationDomain
    {
        public bool Owns(string operation) { return operation.StartsWith("common.vba_", StringComparison.Ordinal); }
        internal static IEnumerable<string> Modules(IDictionary<string, object> arguments)
        {
            object modules;
            if (arguments != null && arguments.TryGetValue("modules", out modules) && modules is IEnumerable<string>)
                foreach (var name in (IEnumerable<string>)modules)
                    if (!string.IsNullOrWhiteSpace(name)) yield return VbaReader.NormalizeModuleName(name);
            foreach (var key in new[] { "moduleName", "newModuleName" })
            {
                var value = ResourceMutationDomains.Argument(arguments, key);
                if (!string.IsNullOrWhiteSpace(value)) yield return VbaReader.NormalizeModuleName(value);
            }
        }
        public IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            foreach (var module in Modules(arguments))
                yield return new ResourceImpact(VbaResourceProvider.ComponentIdentity(scope.Id, module), ResourceImpactRelation.Exact);
            yield return new ResourceImpact(new ResourceIdentity(ResourceUri.Create("vba", scope.Id, "project")),
                ResourceImpactRelation.ContainerMembership);
            if (!Modules(arguments).Any())
                foreach (var head in snapshot.Heads.Values.Where(head => ResourceMutationDomains.Provider(head.Identity) == "vba" &&
                    head.Identity.Uri.Contains("/component/")))
                    yield return new ResourceImpact(head.Identity, ResourceImpactRelation.Exact);
            foreach (var head in snapshot.Heads.Values.Where(head => ResourceMutationDomains.Provider(head.Identity) == "context"))
                yield return new ResourceImpact(head.Identity, ResourceImpactRelation.DependsOn);
        }
    }

    internal sealed class CatalogResourceMutationDomain : IResourceMutationDomain
    {
        public bool Owns(string operation)
        {
            return operation.StartsWith("common.tools_", StringComparison.Ordinal) ||
                operation.StartsWith("common.skills_", StringComparison.Ordinal) || operation.StartsWith("common.prompts_", StringComparison.Ordinal);
        }
        public IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            var kind = operation.StartsWith("common.tools_", StringComparison.Ordinal) ? "tools" :
                operation.StartsWith("common.skills_", StringComparison.Ordinal) ? "skills" : "prompts";
            yield return new ResourceImpact(new ResourceIdentity(ResourceUri.Create("catalog", kind)),
                ResourceImpactRelation.CatalogGeneration);
        }
    }

    internal sealed class ConversationResourceMutationDomain : IResourceMutationDomain
    {
        internal static string StateName(string operation)
        {
            return operation.StartsWith("common.html_", StringComparison.Ordinal) ? "html-workspace" :
                operation.StartsWith("common.plan_", StringComparison.Ordinal) ? "plan-document" :
                operation.StartsWith("common.task_", StringComparison.Ordinal) ? "task-list" : null;
        }
        public bool Owns(string operation)
        {
            return operation.StartsWith("common.html_", StringComparison.Ordinal) ||
                operation.StartsWith("common.plan_", StringComparison.Ordinal) || operation.StartsWith("common.task_", StringComparison.Ordinal) ||
                operation == "excel.create_chat_chart";
        }
        public IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            yield return new ResourceImpact(ResourceStateProvider.Identity(scope, StateName(operation) ?? "artifacts"),
                ResourceImpactRelation.ContainerMembership);
        }
    }

    internal sealed class ResourceDefinitionMutationDomain : IResourceMutationDomain
    {
        public bool Owns(string operation) { return ResourceDefinitionToolHandler.Owns(operation); }
        public IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            yield return new ResourceImpact(ResourceStateProvider.Identity(scope, ResourceDefinitionToolHandler.NodeName(operation, arguments)),
                operation == ResourceDefinitionToolHandler.Publish ? ResourceImpactRelation.CatalogGeneration : ResourceImpactRelation.Exact);
        }
    }

    internal sealed class OfficeResourceMutationDomain : IResourceMutationDomain
    {
        public bool Owns(string operation) { return true; }
        public IEnumerable<ResourceImpact> Impacts(ResourceAuthorityScopeId scope, string operation,
            IDictionary<string, object> arguments, ResourceAuthoritySnapshot snapshot)
        {
            yield return new ResourceImpact(new ResourceIdentity(ResourceUri.Create("document", scope.Id, "root")), ResourceImpactRelation.Subtree);
            var arbitrary = operation == VbaToolCatalog.RunMacro || !new[] { "excel.", "word.", "powerpoint.", "outlook." }
                .Any(prefix => operation.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var head in snapshot.Heads.Values)
            {
                var provider = ResourceMutationDomains.Provider(head.Identity);
                if (provider == "document" || provider == "context" || provider == "excel" ||
                    arbitrary && provider == "vba" && !head.Identity.Uri.Contains("/backup/"))
                    yield return new ResourceImpact(head.Identity, ResourceImpactRelation.Intersects);
            }
        }
    }
}
