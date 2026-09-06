using System;
using System.Collections.Generic;
using System.Globalization;
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
            var plans = IntentPlansForScope(scope).ToList();
            if (!string.Equals(scope, "all", StringComparison.Ordinal) &&
                plans.Count == 0)
            {
                unavailable.Add(scope);
            }
            var states = EnumerateIntentResources(
                session, plans, unavailable, null, ref sourceTruncated);
            if ((scope == "all" || scope == "document") && query.IndexOf('!') > 0)
            {
                var excel = _registry.All().OfType<ExcelResourceProvider>().SingleOrDefault();
                if (excel != null) AddIntentState(states, WithProvider(excel, session, () => excel.ResolveRange(session, query)));
            }
            if ((scope == "all" || scope == "document") && query.StartsWith("Word range: ", StringComparison.Ordinal))
            {
                var word = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (word != null) AddIntentState(states, WithProvider(word, session, () => word.ResolveWordRange(session, query)));
            }
            if ((scope == "all" || scope == "document") && query.StartsWith("PowerPoint slide: ", StringComparison.Ordinal))
            {
                var powerPoint = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (powerPoint != null) AddIntentState(states, WithProvider(powerPoint, session, () => powerPoint.ResolvePowerPointSlide(session, query)));
            }
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
            if (IsRuntimeOwnedIntentTarget(target))
            {
                throw new ResourceRequestException(
                    "Exact resource URIs are runtime-owned and cannot be used as model targets. Run common.resources_find and pass one exact returned semantic target.",
                    "resource_target_runtime_owned",
                    true);
            }
            if (target.StartsWith("Word search scope: ", StringComparison.Ordinal))
            {
                var word = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (word == null) throw new ResourceRequestException("Word resource provider is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
                var descriptor = WithProvider(word, session, () => word.ResolveWordSearch(session, target.Substring(19)));
                return new ResourceIntentTarget { Target = IntentTarget(descriptor), Type = "Word search scope", Scope = "document",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }
            if (target.StartsWith("PowerPoint search scope: ", StringComparison.Ordinal))
            {
                var provider = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (provider == null) throw new ResourceRequestException("PowerPoint resource provider is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
                var descriptor = WithProvider(provider, session, () => provider.ResolvePowerPointSearch(session, target.Substring(25)));
                return new ResourceIntentTarget { Target = IntentTarget(descriptor), Type = "PowerPoint search scope", Scope = "document",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }
            if (target.StartsWith("Excel range: ", StringComparison.Ordinal))
            {
                var excel = _registry.All().OfType<ExcelResourceProvider>().SingleOrDefault();
                if (excel == null) throw new ResourceRequestException("Excel resource provider is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
                var descriptor = WithProvider(excel, session, () => excel.ResolveRange(session, target));
                return new ResourceIntentTarget { Target = IntentTarget(descriptor), Type = "Excel range", Scope = "document",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }
            if (target.StartsWith("PowerPoint slide: ", StringComparison.Ordinal))
            {
                var powerPoint = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (powerPoint == null) throw new ResourceRequestException("PowerPoint resource provider is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
                var descriptor = WithProvider(powerPoint, session, () => powerPoint.ResolvePowerPointSlide(session, target));
                return new ResourceIntentTarget { Target = IntentTarget(descriptor), Type = "PowerPoint slide", Scope = "document",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }
            if (target.StartsWith("Word range: ", StringComparison.Ordinal))
            {
                var word = _registry.All().OfType<LiveDocumentResourceProvider>().SingleOrDefault();
                if (word == null) throw new ResourceRequestException("Word resource provider is unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE", false);
                var descriptor = WithProvider(word, session, () => word.ResolveWordRange(session, target));
                return new ResourceIntentTarget { Target = IntentTarget(descriptor), Type = "Word range", Scope = "document",
                    Descriptor = descriptor, Reference = descriptor.Reference };
            }
            var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new Dictionary<string, ResourceRequestException>(
                StringComparer.OrdinalIgnoreCase);
            var truncated = false;
            var scope = IntentTargetScope(target);
            var states = EnumerateIntentResources(
                session, IntentPlansForScope(scope), unavailable, failures, ref truncated);
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
                if (failures.TryGetValue(scope, out failure))
                    throw failure;
                throw new ResourceRequestException(
                    "Resource target is no longer available: " + target +
                    ". Run common.resources_find and choose one exact returned target.",
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

        internal static bool IsRuntimeOwnedIntentTarget(string target)
        {
            return (target ?? string.Empty).Trim().StartsWith(
                "rna://", StringComparison.OrdinalIgnoreCase);
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
                        var page = List(session, plan.Provider.Id, plan.Kind, cursor, IntentPageSize);
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
                    var result = Search(session, plan.Provider.Id, query, plan.Kind, MaximumIntentResults, IntentSnippetCharacters);
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
                    if ((provider as LiveDocumentResourceProvider)?.IsWord == true)
                        yield return new ResourceIntentPlan(provider, LiveDocumentResourceProvider.WordSearchKind, "document");
                    if ((provider as LiveDocumentResourceProvider)?.IsPowerPoint == true)
                        yield return new ResourceIntentPlan(provider, LiveDocumentResourceProvider.PowerPointSearchKind, "document");
                    if ((provider as LiveDocumentResourceProvider)?.IsOutlook == true)
                        yield return new ResourceIntentPlan(provider, LiveDocumentResourceProvider.OutlookMailKind, "document");
                    yield return new ResourceIntentPlan(provider, LiveDocumentResourceProvider.SelectionKind, "selection");
                }
                else if (string.Equals(provider.Id,
                    VbaResourceProvider.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ResourceIntentPlan(provider, null, "vba");
                    yield return new ResourceIntentPlan(provider, VbaResourceProvider.BackupKind, "backups");
                }
                else if (provider is ExcelResourceProvider)
                {
                    yield return new ResourceIntentPlan(provider, null, "document");
                }
                else if (provider is CatalogResourceProvider)
                {
                    yield return new ResourceIntentPlan(provider, null, "catalogs");
                }
                else if (provider is ContextResourceProvider)
                {
                    yield return new ResourceIntentPlan(provider, ContextResourceProvider.DataKind, "conversation");
                    yield return new ResourceIntentPlan(provider, ContextResourceProvider.ObservationKind, "document");
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

        private IEnumerable<ResourceIntentPlan> IntentPlansForScope(string scope)
        {
            foreach (var plan in IntentListPlans())
            {
                if (ScopeMatches(scope, plan.Scope) ||
                    string.Equals(scope, "html", StringComparison.Ordinal) &&
                    string.Equals(plan.Provider.Id,
                        ChatArtifactResourceProvider.ProviderName,
                        StringComparison.OrdinalIgnoreCase) &&
                    plan.Kind == null)
                {
                    yield return plan;
                }
            }
        }

        private static void AddIntentState(
            ICollection<ResourceIntentState> states,
            ResourceDescriptor descriptor)
        {
            if (descriptor == null || descriptor.Reference == null ||
                string.IsNullOrWhiteSpace(descriptor.Reference.Uri)) return;
            if (states.Any(state => state.Reference.Uri == descriptor.Reference.Uri)) return;
            var type = IntentType(descriptor);
            states.Add(new ResourceIntentState
            {
                Descriptor = descriptor,
                Reference = new ResourceRef(
                    descriptor.Reference.Uri,
                    descriptor.Reference.Revision),
                Type = type,
                Scope = IntentScope(descriptor, type)
            });
        }

        private static void AssignIntentTargets(
            IEnumerable<ResourceIntentState> states)
        {
            foreach (var state in states ?? new ResourceIntentState[0])
                state.Target = IntentTarget(state.Descriptor);
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
                Evidence = match == null ? null : match.Evidence,
                Reference = new ResourceRef(
                    state.Reference.Uri,
                    match == null ? state.Reference.Revision : match.Reference.Revision)
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

        internal static string IntentTarget(ResourceDescriptor descriptor)
        {
            var value = IntentBaseTarget(descriptor);
            return descriptor != null && descriptor.CreatedUtc.HasValue
                ? value + " [created " + descriptor.CreatedUtc.Value
                    .ToUniversalTime().ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        CultureInfo.InvariantCulture) + "]"
                : value;
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
                case "catalogs":
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
                case "Excel range": return "document";
                case "Outlook mail":
                case "Outlook collection":
                case "PowerPoint slide":
                case "Word range": return "document";
                case "Word search scope": return "document";
                case "PowerPoint search scope": return "document";
                case "Office observation": return "document";
                case "catalog":
                case "tool source":
                case "prompt":
                case "prompt default":
                case "skill":
                case "skill reference": return "catalogs";
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
                case ContextResourceProvider.DataKind: return "context data";
                case ContextResourceProvider.ObservationKind: return "Office observation";
                case "catalog": return "catalog";
                case "tool-source": return "tool source";
                case "prompt": return "prompt";
                case "prompt-default": return "prompt default";
                case "skill": return "skill";
                case "skill-reference": return "skill reference";
                case LiveDocumentResourceProvider.DocumentKind: return "document";
                case ExcelResourceProvider.RangeKind: return "Excel range";
                case LiveDocumentResourceProvider.OutlookMailKind: return "Outlook mail";
                case LiveDocumentResourceProvider.OutlookCollectionKind: return "Outlook collection";
                case LiveDocumentResourceProvider.PowerPointSlideKind: return "PowerPoint slide";
                case LiveDocumentResourceProvider.WordRangeKind: return "Word range";
                case LiveDocumentResourceProvider.WordSearchKind: return "Word search scope";
                case LiveDocumentResourceProvider.PowerPointSearchKind: return "PowerPoint search scope";
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
            if (descriptor.Provider == "catalog") return "catalogs";
            if (string.Equals(type, "document", StringComparison.Ordinal) || type == "Excel range" || type == "Word range" || type == "Word search scope" || type == "PowerPoint search scope" || type == "PowerPoint slide" || type == "Outlook mail" || type == "Outlook collection" || type == "Office observation") return "document";
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
        [Newtonsoft.Json.JsonIgnore]
        public IReadOnlyList<ResourceEvidence> Evidence { get; set; }
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
