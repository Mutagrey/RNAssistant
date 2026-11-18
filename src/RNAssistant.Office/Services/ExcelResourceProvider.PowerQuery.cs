using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        internal const string QueryKind = "excel-query";
        internal const string QueryPrefix = "Excel query: ";
        internal const int MaximumQuerySourceCharacters = 1000000;
        internal const int MaximumQueries = 1000;

        internal ResourceDescriptor ResolveQuery(ChatSession session, string name)
        { return _scope.Read(session, () => DescribeQuery(session, ReadQuerySnapshot(ValidateQueryName(name)))); }

        internal ExcelPowerQuerySnapshot CaptureQuery(ChatSession session, string name)
        { return _scope.Read(session, () => ReadQuerySnapshot(ValidateQueryName(name))); }

        internal ExcelPowerQueryBindingsSnapshot CaptureQueryBindings(ChatSession session, string name)
        {
            return _scope.Read(session, () => QueryRead(() => RequirePowerQuery().ReadBindings(ValidateQueryName(name))));
        }

        internal static ResourceIdentity QueryIdentity(string documentAuthorityId, string name)
        { return new ResourceIdentity(ResourceUri.Create(ProviderName, documentAuthorityId, "query", ValidateQueryName(name))); }

        internal static ResourceIdentity QueryStatusIdentity(string documentAuthorityId, string name)
        { return new ResourceIdentity(ResourceUri.Create(ProviderName, documentAuthorityId, "query", ValidateQueryName(name), "status")); }

        internal static bool IsQueryStatusUri(string uri)
        {
            var address = ResourceUri.Parse(uri);
            return address.Provider == ProviderName && address.Segments.Count == 4 &&
                address.Segments[1] == "query" && address.Segments[3] == "status";
        }

        internal ResourceDescriptor ResolveQueryStatus(ChatSession session, string uri)
        {
            var address = ResourceUri.Parse(uri);
            if (!IsQueryStatusUri(uri) || session == null || string.IsNullOrWhiteSpace(session.DocumentAuthorityId) ||
                address.Segments[0] != session.DocumentAuthorityId)
                throw Error("RESOURCE_ACCESS_DENIED", "Power Query status belongs to one exact document authority scope.");
            // Only immutable cached owner metadata is inspected. Query existence,
            // native liveness and source/data currentness are not inferred here.
            ExcelPowerQueryExecutionService.For(_powerQueryAdapter).RequireSession(session);
            var name = ValidateQueryName(address.Segments[2]);
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(QueryStatusIdentity(session.DocumentAuthorityId, name).Uri),
                Provider = ProviderName, Kind = "excel-query-status", Title = name + " — operation status",
                CurrentTarget = QueryPrefix + name, MimeType = "application/json; charset=utf-8",
                Mutable = true, Tracking = "session-observed"
            };
            descriptor.Representations.Add("status"); descriptor.Capabilities.Add("read");
            descriptor.Metadata["queryName"] = name;
            descriptor.Metadata["queryExistence"] = "not_checked";
            descriptor.Metadata["observation"] = "session-owned operation history; no live Excel probe";
            return descriptor;
        }

        internal static string QueryNameFromTarget(string target)
        {
            if (target == null || !target.StartsWith(QueryPrefix, StringComparison.Ordinal))
                throw Error("RESOURCE_TARGET_INVALID", "Use the exact Excel query: <name> target returned by core.find.");
            return ValidateQueryName(target.Substring(QueryPrefix.Length));
        }

        internal static string ValidateQueryName(string name)
        {
            // Preserve Excel/M spelling. Never case-fold query identifiers.
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl) || name != name.Trim() ||
                name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                throw Error("RESOURCE_TARGET_INVALID", "Use one exact Power Query name of at most 255 characters without surrounding whitespace, control characters, slashes or dot-only path names.");
            return name;
        }

        private IExcelPowerQueryBackend RequirePowerQuery()
        {
            if (PowerQueryBackend == null)
                throw Error("RESOURCE_PROVIDER_UNAVAILABLE", "Power Query is unavailable in this bound Excel session.");
            return PowerQueryBackend;
        }

        private static T QueryRead<T>(Func<T> action)
        {
            try { return action(); }
            catch (ExcelPowerQueryBackendException ex)
            { throw new ResourceRequestException(ex.Message, ex.ErrorCode, ex.Retryable); }
        }

        private ExcelPowerQuerySnapshot ReadQuerySnapshot(string name)
        {
            var query = QueryRead(() => RequirePowerQuery().ReadQuery(name));
            if (query == null || query.QueryName != name || query.Formula == null || string.IsNullOrEmpty(query.QueryIdentity))
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "Power Query did not return the exact requested definition.");
            if (query.Formula.Length > MaximumQuerySourceCharacters)
                throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Power Query source exceeds the local one-million-character capture bound.");
            return query;
        }

        private ResourceListPage ListQueries(ChatSession session, string cursor, int limit)
        {
            if (PowerQueryBackend == null) return PageNamedResources(new List<ResourceDescriptor>(), QueryKind, cursor, limit);
            var snapshot = QueryRead(() => RequirePowerQuery().ListQueries(MaximumQueries));
            if (snapshot == null || snapshot.Items == null)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "Power Query catalog is unavailable.");
            var descriptors = new List<ResourceDescriptor>();
            var unavailable = 0;
            foreach (var query in snapshot.Items)
            {
                if (query == null) { unavailable++; continue; }
                try { descriptors.Add(DescribeQuery(session, query.QueryName, query.Description)); }
                catch (ResourceRequestException ex) when (ex.ErrorCode == "RESOURCE_TARGET_INVALID") { unavailable++; }
            }
            if (unavailable > 0)
                foreach (var descriptor in descriptors)
                {
                    descriptor.Metadata["unavailableQueryNames"] = unavailable.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    descriptor.Metadata["unavailableQueryNamesReason"] = "Some query names cannot be represented by the supported exact semantic/URI route.";
                }
            var page = PageNamedResources(descriptors, QueryKind, cursor, limit, snapshot.IsTruncated || unavailable > 0);
            page.UnavailableResources = unavailable;
            page.TotalIsExact = !snapshot.IsTruncated && unavailable == 0;
            return page;
        }

        private ResourceDescriptor DescribeQuery(ChatSession session, ExcelPowerQuerySnapshot query)
        { return DescribeQuery(session, query.QueryName, query.Description); }

        private ResourceDescriptor DescribeQuery(ChatSession session, string name, string description)
        {
            name = ValidateQueryName(name);
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(QueryIdentity(_scope.DocumentToken(session), name).Uri),
                Provider = ProviderName, Kind = QueryKind, Title = name,
                CurrentTarget = QueryPrefix + name,
                MimeType = "text/x-powerquery; charset=utf-8", Mutable = true, Tracking = "externally-observed"
            };
            descriptor.Representations.AddRange(new[] { ResourceRepresentations.Source, ResourceRepresentations.Metadata, "bindings", "dependencies", "status" });
            descriptor.Capabilities.AddRange(new[] { "read", "patch", "write" });
            descriptor.Metadata["queryName"] = name;
            descriptor.Metadata["description"] = description ?? string.Empty;
            descriptor.Metadata["scope"] = "document";
            descriptor.Metadata["language"] = "powerquery-m";
            descriptor.Metadata["returnType"] = "unknown";
            descriptor.Metadata["execution"] = "Reading or editing source does not refresh data.";
            return descriptor;
        }

        private ResourceReadSelection ReadQuery(ChatSession session, ResourceReadRequest request, string name)
        {
            var view = string.IsNullOrWhiteSpace(request.Representation) || request.Representation == "auto"
                ? ResourceRepresentations.Source : request.Representation;
            if (view != ResourceRepresentations.Source &&
                view != ResourceRepresentations.Metadata && view != "bindings" && view != "dependencies")
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "Power Query supports source, metadata, bindings, dependencies and observed status. Read the loaded Excel table separately for data.");
            var query = ReadQuerySnapshot(name);
            var descriptor = DescribeQuery(session, query);
            string text;
            if (view == ResourceRepresentations.Source)
            {
                text = query.Formula;
            }
            else if (view == "bindings")
                text = JsonConvert.SerializeObject(PowerQueryBindingsView.From(QueryRead(() => RequirePowerQuery().ReadBindings(name))));
            else if (view == "dependencies")
                text = JsonConvert.SerializeObject(new PowerQueryAnalysisView { Target = QueryPrefix + name });
            else
                text = JsonConvert.SerializeObject(new PowerQueryMetadataView {
                    Target = QueryPrefix + name, Name = name, Description = query.Description,
                    SourceCharacters = query.Formula.Length, Capabilities = QueryRead(() => RequirePowerQuery().GetCapabilities()) });
            // The retained whole view and the delivered view must use identical
            // payload metadata when the shared journal registers them again.
            if (view != ResourceRepresentations.Source) descriptor.MimeType = "application/json; charset=utf-8";
            var selection = SelectCapture(request, descriptor, text, view, descriptor.MimeType);
            // M source is the exact delivered body, unlike Excel's structured
            // projections. Preserve its coverage while allowing the shared
            // model context owner to admit the observed text for later edits.
            selection.Result.RawContentIncluded = view == ResourceRepresentations.Source;
            return selection;
        }
    }

    internal sealed class PowerQueryMetadataView
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("sourceCharacters")] public int SourceCharacters { get; set; }
        [JsonProperty("returnType")] public string ReturnType { get { return "unknown"; } }
        [JsonProperty("capabilities")] public ExcelPowerQueryCapabilities Capabilities { get; set; }
        [JsonProperty("execution")] public string Execution { get { return "not_executed"; } }
    }

    internal sealed class PowerQueryAnalysisView
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("syntax")] public string Syntax { get { return "not_checked"; } }
        [JsonProperty("nameResolution")] public string NameResolution { get { return "not_checked"; } }
        [JsonProperty("dependencies")] public string Dependencies { get { return "not_checked"; } }
        [JsonProperty("sourceAccess")] public string SourceAccess { get { return "not_checked"; } }
        [JsonProperty("complete")] public bool Complete { get { return false; } }
        [JsonProperty("execution")] public string Execution { get { return "not_executed"; } }
        [JsonProperty("reason")] public string Reason { get { return "Name, dependency and source-access analysis is unavailable. Use excel.query.validate with kind=source for optional offline syntax analysis. This resource read does not run the parser or evaluate M."; } }
    }

    internal sealed class PowerQueryBindingsView
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("ambiguous")] public bool Ambiguous { get; set; }
        [JsonProperty("unavailableReason")] public string UnavailableReason { get; set; }
        [JsonProperty("connections")] public IReadOnlyList<PowerQueryConnectionView> Connections { get; set; }
        [JsonProperty("loads")] public IReadOnlyList<PowerQueryLoadView> Loads { get; set; }
        internal static PowerQueryBindingsView From(ExcelPowerQueryBindingsSnapshot bindings)
        {
            if (bindings == null) throw new ResourceRequestException("Power Query bindings are unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE", false);
            return new PowerQueryBindingsView {
                Target = ExcelResourceProvider.QueryPrefix + bindings.QueryName, Complete = bindings.IsComplete,
                Ambiguous = bindings.IsAmbiguous, UnavailableReason = bindings.UnavailableReason,
                Connections = (bindings.Connections ?? new ExcelPowerQueryConnectionBinding[0]).Select(item =>
                    new PowerQueryConnectionView { Name = item.ConnectionName, InDataModel = item.InDataModel }).ToArray(),
                Loads = (bindings.Loads ?? new ExcelPowerQueryLoadBinding[0]).Select(PowerQueryLoadView.From).ToArray()
            };
        }
    }

    internal sealed class PowerQueryConnectionView
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("inDataModel")] public bool InDataModel { get; set; }
    }

    internal sealed class PowerQueryLoadView
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("table")] public string Table { get; set; }
        [JsonProperty("address")] public string Address { get; set; }
        [JsonProperty("connection")] public string Connection { get; set; }
        [JsonProperty("rows")] public int? Rows { get; set; }
        [JsonProperty("columns")] public int? Columns { get; set; }
        [JsonProperty("refreshing")] public bool? Refreshing { get; set; }
        internal static PowerQueryLoadView From(ExcelPowerQueryLoadBinding binding)
        {
            return binding == null ? null : new PowerQueryLoadView {
                Target = "Excel table: " + binding.Table, Sheet = binding.Sheet, Table = binding.Table,
                Address = binding.Range, Connection = binding.ConnectionName, Rows = binding.Rows,
                Columns = binding.Columns, Refreshing = binding.Refreshing
            };
        }
    }
}
