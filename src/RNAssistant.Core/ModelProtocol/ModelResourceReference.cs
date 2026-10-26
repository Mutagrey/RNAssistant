using System;
using System.Globalization;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // An observation label, never an argument, permission or replacement ResourceRef.
    public sealed class ModelResourceReference
    {
        public ResourceAuthorityScopeId Scope { get; private set; }
        public ResourceRef Resource { get; private set; }
        public ModelReference Reference { get; private set; }
        public long Version { get; private set; }
        [JsonIgnore]
        public string Alias { get { return Reference.Alias + "@v" + Version.ToString(CultureInfo.InvariantCulture); } }
        [JsonIgnore]
        public string LogicalKey { get { return JsonConvert.SerializeObject(new[] { Scope.Kind, Scope.Id, Resource.Uri }); } }
        [JsonIgnore]
        public string ExactKey { get { return JsonConvert.SerializeObject(new[] { Scope.Kind, Scope.Id, Resource.Uri, Resource.Revision }); } }

        [JsonConstructor]
        public ModelResourceReference(ResourceAuthorityScopeId scope, ResourceRef resource, ModelReference reference, long version)
        {
            if (scope == null || resource == null || !resource.IsExact || reference?.Kind != ModelReferenceKind.Resource || version <= 0)
                throw new ArgumentException("An exact resource, typed resource reference and positive version are required.");
            Scope = scope; Resource = resource.Copy(); Reference = reference; Version = version;
        }

        public bool Matches(ResourceEvidence evidence)
        { return evidence != null && Scope.Equals(evidence.ScopeId) && Resource.Uri == evidence.Resource.Uri && Resource.Revision == evidence.Resource.Revision; }
    }
}
