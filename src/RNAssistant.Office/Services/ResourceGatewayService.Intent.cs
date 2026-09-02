using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        private const int IntentPageSize = 50;
        private const int MaximumIntentResources = 1000;
        private const int MaximumIntentResults = 20;
        private const int IntentSnippetCharacters = 600;

        public ResourceIntentFindResult Find(
            ChatSession session,
            string query,
            string scope)
        {
            query = (query ?? string.Empty).Trim();
            scope = NormalizeIntentScope(scope);
            var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceTruncated = false;
            var plans = IntentListPlans().ToList();
            if (!string.Equals(scope, "all", StringComparison.Ordinal) &&
                !plans.Any(plan => ScopeMatches(scope, plan.Scope)))
            {
                unavailable.Add(scope);
            }
            var states = EnumerateIntentResources(
                session, plans, unavailable, null, ref sourceTruncated);
            AssignIntentTargets(states);

            Dictionary<string, ResourceSearchMatch> matches = null;
            if (query.Length > 0)
            {
                matches = SearchIntentResources(
                    session, query, scope, unavailable, ref sourceTruncated);
            }

            var selected = states
                .Where(state => ScopeMatches(scope, state.Scope))
                .Where(state => query.Length == 0 ||
                    matches.ContainsKey(state.Reference.Uri) ||
                    IntentMetadata(state).IndexOf(
                        query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(state => matches != null &&
                    matches.ContainsKey(state.Reference.Uri))
                .ThenByDescending(state => query.Length == 0 &&
                    string.Equals(scope, "vba", StringComparison.Ordinal) &&
                    string.Equals(state.Type, "VBA project", StringComparison.Ordinal))
                .ThenBy(state => state.Target, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var shown = selected.Take(MaximumIntentResults)
                .Select(state => ProjectIntentCandidate(
                    state,
                    matches == null ? null : Match(matches, state.Reference.Uri)))
                .ToList();
            var resultTruncated = sourceTruncated || selected.Count > shown.Count;
            return new ResourceIntentFindResult
            {
                Scope = scope,
                Query = query.Length == 0 ? null : query,
                Items = shown,
                Total = selected.Count,
                Complete = !resultTruncated && unavailable.Count == 0,
                Empty = selected.Count == 0 && unavailable.Count == 0,
                Partial = unavailable.Count > 0,
                RefineQuery = resultTruncated,
                UnavailableScopes = unavailable
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ResourceRefs = shown.Select(item => item.Reference).ToList()
            };
        }

        public ResourceIntentTarget ResolveIntentTarget(
            ChatSession session,
            string target)
        {
            target = (target ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                throw new ResourceRequestException(
                    "A semantic resource target is required.",
                    "resource_target_required",
                    true);
            }
            var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new Dictionary<string, ResourceRequestException>(
                StringComparer.OrdinalIgnoreCase);
            var truncated = false;
            var states = EnumerateIntentResources(
                session, IntentListPlans().ToList(), unavailable, failures, ref truncated);
            AssignIntentTargets(states);
            var matches = states.Where(state => string.Equals(
                    state.Target, target, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (matches.Count > 1)
            {
                throw new ResourceRequestException(
                    "The selected resource target is ambiguous. Run common.resources_find again and choose one exact returned target.",
                    "resource_target_ambiguous",
                    false);
            }
            if (matches.Count == 0)
            {
                ResourceRequestException failure;
                if (failures.TryGetValue(IntentTargetScope(target), out failure))
                    throw failure;
                var suffix = unavailable.Count > 0
                    ? " Some semantic scopes are currently unavailable: " +
                        string.Join(", ", unavailable.OrderBy(value => value).ToArray()) + "."
                    : string.Empty;
                throw new ResourceRequestException(
                    "Resource target is no longer available: " + target +
                    ". Run common.resources_find and choose one exact returned target." + suffix,
                    "resource_target_not_found",
                    true);
            }
            var match = matches[0];
            return new ResourceIntentTarget
            {
                Target = match.Target,
                Type = match.Type,
                Scope = match.Scope,
                Descriptor = match.Descriptor,
                Reference = new ResourceRef(
                    match.Reference.Uri,
                    match.Reference.Revision)
            };
        }

        private List<ResourceIntentState> EnumerateIntentResources(
            ChatSession session,
            IEnumerable<ResourceIntentPlan> plans,
            ISet<string> unavailable,
            IDictionary<string, ResourceRequestException> failures,
            ref bool truncated)
        {
            var states = new List<ResourceIntentState>();
            foreach (var plan in plans ?? new ResourceIntentPlan[0])
            {
                if (states.Count >= MaximumIntentResources)
                {
                    truncated = true;
                    break;
                }
                try
                {
                    var cursor = string.Empty;
                    do
                    {
                        var page = WithProvider(plan.Provider, session, delegate
                        {
                            return plan.Provider.List(
                                session, plan.Kind, cursor, IntentPageSize);
                        });
                        foreach (var descriptor in page.Items ??
                            new List<ResourceDescriptor>())
                        {
                            AddIntentState(states, descriptor);
                            if (states.Count >= MaximumIntentResources)
                            {
                                truncated = true;
                                break;
                            }
                        }
                        if (states.Count >= MaximumIntentResources) break;
                        cursor = page.NextCursor;
                    }
                    while (!string.IsNullOrWhiteSpace(cursor));
                }
                catch (Exception ex) when (IsIntentAvailabilityFailure(ex))
                {
                    unavailable.Add(plan.Scope);
                    var resourceFailure = ex as ResourceRequestException;
                    if (failures != null && resourceFailure != null &&
                        !failures.ContainsKey(plan.Scope))
                    {
                        failures.Add(plan.Scope, resourceFailure);
                    }
                }
            }
            return states
                .GroupBy(state => state.Reference.Uri, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private Dictionary<string, ResourceSearchMatch> SearchIntentResources(
            ChatSession session,
            string query,
            string scope,
            ISet<string> unavailable,
            ref bool truncated)
        {
            var matches = new Dictionary<string, ResourceSearchMatch>(
                StringComparer.Ordinal);
            foreach (var plan in IntentSearchPlans(scope))
            {
                try
                {
                    var result = WithProvider(plan.Provider, session, delegate
                    {
                        return plan.Provider.Search(
                            session,
                            query,
                            plan.Kind,
                            MaximumIntentResults,
                            IntentSnippetCharacters);
                    });
                    truncated = truncated || result.ScanTruncated;
                    foreach (var match in result.Matches ??
                        new List<ResourceSearchMatch>())
                    {
                        if (match == null || match.Reference == null ||
                            string.IsNullOrWhiteSpace(match.Reference.Uri) ||
                            matches.ContainsKey(match.Reference.Uri)) continue;
                        matches.Add(match.Reference.Uri, match);
                    }
                }
                catch (Exception ex) when (IsIntentAvailabilityFailure(ex))
                {
                    unavailable.Add(plan.Scope);
                }
            }
            return matches;
        }

        private IEnumerable<ResourceIntentPlan> IntentListPlans()
        {
            foreach (var provider in _registry.All())
            {
                if (string.Equals(provider.Id,
                    ChatArtifactResourceProvider.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ResourceIntentPlan(provider, null, "conversation");
                    yield return new ResourceIntentPlan(provider, ChatHtmlResourceCatalog.FileKind, "html");
                    yield return new ResourceIntentPlan(provider, ChatHtmlResourceCatalog.DataKind, "html");
                }
                else if (string.Equals(provider.Id,
                    LiveDocumentResourceProvider.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ResourceIntentPlan(provider, null, "document");
                    yield return new ResourceIntentPlan(provider, LiveDocumentResourceProvider.SelectionKind, "selection");
                }
                else if (string.Equals(provider.Id,
                    VbaResourceProvider.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ResourceIntentPlan(provider, null, "vba");
                    yield return new ResourceIntentPlan(provider, VbaResourceProvider.BackupKind, "backups");
                }
                else
                {
                    yield return new ResourceIntentPlan(provider, null, "conversation");
                }
            }
        }

        private IEnumerable<ResourceIntentPlan> IntentSearchPlans(string scope)
        {
            foreach (var plan in IntentListPlans())
            {
                if (!ScopeMatches(scope, plan.Scope)) continue;
                if (string.Equals(plan.Scope, "backups", StringComparison.Ordinal) &&
                    string.Equals(scope, "all", StringComparison.Ordinal)) continue;
                yield return plan;
            }
        }

        private static void AddIntentState(
            ICollection<ResourceIntentState> states,
            ResourceDescriptor descriptor)
        {
            if (descriptor == null || descriptor.Reference == null ||
                string.IsNullOrWhiteSpace(descriptor.Reference.Uri)) return;
            var type = IntentType(descriptor);
            states.Add(new ResourceIntentState
            {
                Descriptor = descriptor,
                Reference = new ResourceRef(
                    descriptor.Reference.Uri,
                    descriptor.Reference.Revision),
                Type = type,
                Scope = IntentScope(descriptor, type),
                BaseTarget = IntentBaseTarget(descriptor)
            });
        }

        private static void AssignIntentTargets(
            IEnumerable<ResourceIntentState> states)
        {
            foreach (var group in (states ?? new ResourceIntentState[0])
                .GroupBy(state => state.BaseTarget,
                    StringComparer.OrdinalIgnoreCase))
            {
                var values = group.ToList();
                if (values.Count == 1)
                {
                    values[0].Target = values[0].BaseTarget;
                    continue;
                }
                foreach (var state in values)
                {
                    state.Target = state.BaseTarget +
                        (state.Descriptor.CreatedUtc.HasValue
                            ? " [created " + state.Descriptor.CreatedUtc.Value
                                .ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'") + "]"
                            : " [" + state.Scope + "]");
                }
            }
        }

        private static ResourceIntentCandidate ProjectIntentCandidate(
            ResourceIntentState state,
            ResourceSearchMatch match)
        {
            return new ResourceIntentCandidate
            {
                Target = state.Target,
                Type = state.Type,
                Scope = state.Scope,
                Title = state.Descriptor.Title,
                MimeType = state.Descriptor.MimeType,
                Mutable = state.Descriptor.Mutable,
                ByteLength = state.Descriptor.ByteLength,
                CreatedUtc = state.Descriptor.CreatedUtc,
                Representations = (state.Descriptor.Representations ??
                    new List<string>()).ToList(),
                MatchRepresentation = match == null ? null : match.Representation,
                Snippet = match == null ? null : match.Snippet,
                Reference = new ResourceRef(
                    state.Reference.Uri,
                    state.Reference.Revision)
            };
        }

        private static string IntentMetadata(ResourceIntentState state)
        {
            return string.Join("\n", new[]
            {
                state.Target,
                state.Type,
                state.Scope,
                state.Descriptor.Title,
                state.Descriptor.MimeType,
                string.Join(" ", (state.Descriptor.Metadata ??
                    new Dictionary<string, string>()).Values.ToArray())
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        internal static string IntentBaseTarget(ResourceDescriptor descriptor)
        {
            descriptor = descriptor ?? new ResourceDescriptor();
            return IntentType(descriptor) + ": " +
                (string.IsNullOrWhiteSpace(descriptor.Title)
                    ? "Untitled"
                    : descriptor.Title.Trim());
        }

        internal static string IntentTarget(
            ResourceDescriptor descriptor,
            bool duplicate)
        {
            var value = IntentBaseTarget(descriptor);
            if (!duplicate) return value;
            var type = IntentType(descriptor);
            return value + (descriptor != null && descriptor.CreatedUtc.HasValue
                ? " [created " + descriptor.CreatedUtc.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss'Z'") + "]"
                : " [" + IntentScope(descriptor, type) + "]");
        }

        private static ResourceSearchMatch Match(
            IDictionary<string, ResourceSearchMatch> matches,
            string uri)
        {
            ResourceSearchMatch match;
            return matches != null && matches.TryGetValue(uri, out match)
                ? match
                : null;
        }

        private static string NormalizeIntentScope(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) return "all";
            switch (value)
            {
                case "all":
                case "conversation":
                case "document":
                case "selection":
                case "vba":
                case "html":
                case "backups":
                    return value;
                default:
                    throw new ResourceRequestException(
                        "Unknown semantic resource scope: " + value + ".",
                        "resource_scope_unknown",
                        false);
            }
        }

        private static bool ScopeMatches(string requested, string actual)
        {
            return string.Equals(requested, "all", StringComparison.Ordinal) ||
                string.Equals(requested, actual, StringComparison.Ordinal);
        }

        private static string IntentTargetScope(string target)
        {
            var separator = (target ?? string.Empty).IndexOf(": ", StringComparison.Ordinal);
            var type = separator < 0 ? string.Empty : target.Substring(0, separator);
            switch (type)
            {
                case "document": return "document";
                case "selection": return "selection";
                case "VBA project":
                case "VBA module": return "vba";
                case "VBA backup": return "backups";
                case "HTML file":
                case "HTML data":
                case "HTML workspace": return "html";
                default: return "conversation";
            }
        }

        internal static string IntentType(ResourceDescriptor descriptor)
        {
            var kind = (descriptor.Kind ?? string.Empty).Trim().ToLowerInvariant();
            switch (kind)
            {
                case LiveDocumentResourceProvider.DocumentKind: return "document";
                case LiveDocumentResourceProvider.SelectionKind: return "selection";
                case VbaResourceProvider.ProjectKind: return "VBA project";
                case VbaResourceProvider.ComponentKind: return "VBA module";
                case VbaResourceProvider.BackupKind: return "VBA backup";
                case ChatHtmlResourceCatalog.FileKind: return "HTML file";
                case ChatHtmlResourceCatalog.DataKind: return "HTML data";
                case ChatArtifactKinds.PlanDocument: return "plan";
                case ChatArtifactKinds.TaskList: return "task list";
                case ChatArtifactKinds.HtmlWorkspace: return "HTML workspace";
                case ChatArtifactKinds.Image: return "image";
                case ChatArtifactKinds.Chart: return "chart";
                case ChatArtifactKinds.Markdown: return "note";
                case ChatArtifactKinds.File:
                case ChatArtifactKinds.Attachment: return "attachment";
                default: return "conversation resource";
            }
        }

        private static string IntentScope(
            ResourceDescriptor descriptor,
            string type)
        {
            if (string.Equals(type, "document", StringComparison.Ordinal)) return "document";
            if (string.Equals(type, "selection", StringComparison.Ordinal)) return "selection";
            if (string.Equals(type, "VBA backup", StringComparison.Ordinal)) return "backups";
            if (string.Equals(type, "VBA module", StringComparison.Ordinal) ||
                string.Equals(type, "VBA project", StringComparison.Ordinal)) return "vba";
            if (string.Equals(type, "HTML file", StringComparison.Ordinal) ||
                string.Equals(type, "HTML data", StringComparison.Ordinal) ||
                string.Equals(type, "HTML workspace", StringComparison.Ordinal)) return "html";
            return "conversation";
        }

        private static bool IsIntentAvailabilityFailure(Exception ex)
        {
            var resource = ex as ResourceRequestException;
            if (resource != null &&
                (string.Equals(resource.ErrorCode, "tool_mutation_busy", StringComparison.Ordinal) ||
                 string.Equals(resource.ErrorCode, "tool_mutation_lock_unavailable", StringComparison.Ordinal)))
            {
                return false;
            }
            return ex is ResourceRequestException ||
                ex is InvalidOperationException ||
                ex is KeyNotFoundException;
        }

        private sealed class ResourceIntentPlan
        {
            public IResourceProvider Provider { get; private set; }
            public string Kind { get; private set; }
            public string Scope { get; private set; }

            public ResourceIntentPlan(
                IResourceProvider provider,
                string kind,
                string scope)
            {
                Provider = provider;
                Kind = kind;
                Scope = scope;
            }
        }

        private sealed class ResourceIntentState
        {
            public ResourceDescriptor Descriptor { get; set; }
            public ResourceRef Reference { get; set; }
            public string Type { get; set; }
            public string Scope { get; set; }
            public string BaseTarget { get; set; }
            public string Target { get; set; }
        }
    }

    internal sealed class ResourceIntentFindResult
    {
        [Newtonsoft.Json.JsonProperty("scope")]
        public string Scope { get; set; }
        [Newtonsoft.Json.JsonProperty("query")]
        public string Query { get; set; }
        [Newtonsoft.Json.JsonProperty("items")]
        public List<ResourceIntentCandidate> Items { get; set; }
        [Newtonsoft.Json.JsonProperty("total")]
        public int Total { get; set; }
        [Newtonsoft.Json.JsonProperty("complete")]
        public bool Complete { get; set; }
        [Newtonsoft.Json.JsonProperty("empty")]
        public bool Empty { get; set; }
        [Newtonsoft.Json.JsonProperty("partial")]
        public bool Partial { get; set; }
        [Newtonsoft.Json.JsonProperty("refineQuery")]
        public bool RefineQuery { get; set; }
        [Newtonsoft.Json.JsonProperty("unavailableScopes")]
        public List<string> UnavailableScopes { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public List<ResourceRef> ResourceRefs { get; set; }
    }

    internal sealed class ResourceIntentCandidate
    {
        [Newtonsoft.Json.JsonProperty("target")]
        public string Target { get; set; }
        [Newtonsoft.Json.JsonProperty("type")]
        public string Type { get; set; }
        [Newtonsoft.Json.JsonProperty("scope")]
        public string Scope { get; set; }
        [Newtonsoft.Json.JsonProperty("title")]
        public string Title { get; set; }
        [Newtonsoft.Json.JsonProperty("mimeType")]
        public string MimeType { get; set; }
        [Newtonsoft.Json.JsonProperty("mutable")]
        public bool Mutable { get; set; }
        [Newtonsoft.Json.JsonProperty("byteLength")]
        public long? ByteLength { get; set; }
        [Newtonsoft.Json.JsonProperty("createdUtc")]
        public DateTime? CreatedUtc { get; set; }
        [Newtonsoft.Json.JsonProperty("representations")]
        public List<string> Representations { get; set; }
        [Newtonsoft.Json.JsonProperty("matchRepresentation")]
        public string MatchRepresentation { get; set; }
        [Newtonsoft.Json.JsonProperty("snippet")]
        public string Snippet { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public ResourceRef Reference { get; set; }
    }

    internal sealed class ResourceIntentTarget
    {
        public string Target { get; set; }
        public string Type { get; set; }
        public string Scope { get; set; }
        public ResourceDescriptor Descriptor { get; set; }
        public ResourceRef Reference { get; set; }
    }
}
