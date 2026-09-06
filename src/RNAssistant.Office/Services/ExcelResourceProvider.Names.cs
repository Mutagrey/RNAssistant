using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        internal const string NameKind = "excel-defined-name";

        internal ResourceDescriptor ResolveName(ChatSession session, string name)
        { return _scope.Read(session, () => DescribeName(session, FindName(NormalizeName(name)))); }

        private ExcelInspectSnapshot CaptureNameCatalog()
        {
            var snapshot = _reader.CaptureStructure("names");
            if (snapshot.Truncated)
                throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "The bounded defined-name catalog is incomplete.");
            return snapshot;
        }

        private ResourceListPage ListNames(ChatSession session, string cursor, int limit)
        { return PageNamedResources(CaptureNameCatalog().Names.Select(name => DescribeName(session, name)).ToList(), NameKind, cursor, limit); }

        private ExcelNameSnapshot FindName(string key)
        {
            var matches = CaptureNameCatalog().Names.Where(name => NormalizeName(name.Name) == key).Take(2).ToList();
            if (matches.Count != 1)
                throw Error(matches.Count == 0 ? "RESOURCE_TARGET_NOT_FOUND" : "RESOURCE_TARGET_AMBIGUOUS",
                    "Use one exact defined name, including its sheet qualification when present.");
            return matches[0];
        }

        private ResourceDescriptor DescribeName(ChatSession session, ExcelNameSnapshot name)
        {
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(ResourceUri.Create(Id, _scope.DocumentToken(session), "name", NormalizeName(name.Name))),
                Provider = Id, Kind = NameKind, Title = name.Name, MimeType = "application/json", Mutable = true, Tracking = "externally-observed" };
            descriptor.Capabilities.Add("read");
            descriptor.Representations.Add("metadata");
            descriptor.Metadata["targetKind"] = name.TargetKind.ToString();
            descriptor.Metadata["refersToPreview"] = ContextNormalizer.TrimForContext(name.RefersTo, 240);
            if (name.TargetKind == ExcelNameTargetKind.BoundRange)
            {
                descriptor.Representations.AddRange(new[] { "text", "formulas", "structure", "table", "records" });
                descriptor.Metadata["sheet"] = name.Sheet; descriptor.Metadata["address"] = name.Address;
                descriptor.Metadata["recordsPath"] = "$.range.values";
                descriptor.Metadata["maximumSnapshotCells"] = ExcelReadService.MaxReadCells.ToString();
            }
            return descriptor;
        }

        private ResourceReadSelection ReadName(ChatSession session, ResourceReadRequest request, string key)
        {
            var name = FindName(key);
            var descriptor = DescribeName(session, name);
            var view = request.Representation;
            if (string.IsNullOrWhiteSpace(view) || view == "auto") view = name.TargetKind == ExcelNameTargetKind.BoundRange ? "text" : "metadata";
            if (view == "metadata") return SelectCapture(request, descriptor, JsonConvert.SerializeObject(name), view);
            if (name.TargetKind != ExcelNameTargetKind.BoundRange)
                throw Error("RESOURCE_VIEW_UNAVAILABLE", "This defined name has no verified single range in the bound workbook; read metadata only.");
            if (view != "text" && view != "formulas" && view != "structure")
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "Use text, formulas, structure or records/table at $.range.values.");
            var range = NormalizeAddress(name.Address);
            if (CellCount(range) > ExcelReadService.MaxReadCells)
                throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "The defined range exceeds the 100000-cell snapshot bound.");
            var snapshot = _reader.CaptureRange(name.Sheet, range, view == "structure" ? "profile" : view == "formulas" ? "formulas" : "values");
            if (!string.Equals(snapshot.Sheet, name.Sheet, StringComparison.OrdinalIgnoreCase) || NormalizeAddress(snapshot.Address) != range)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The captured range does not match the defined name's bound extent.");
            var source = new DefinedNameSource { Definition = name, Range = view == "structure" ? null : snapshot,
                Profile = view == "structure" ? ExcelReadService.ProfileOutput(snapshot) : null };
            return SelectCapture(request, descriptor, JsonConvert.SerializeObject(source), view);
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 384 || name != name.Trim() || name.Any(char.IsControl))
                throw Error("RESOURCE_TARGET_INVALID", "Use one bounded exact defined name as reported by discovery.");
            return name.ToUpperInvariant();
        }

        private sealed class DefinedNameSource
        {
            [JsonProperty("definition")] public ExcelNameSnapshot Definition { get; set; }
            [JsonProperty("range", NullValueHandling = NullValueHandling.Ignore)] public ExcelRangeSnapshot Range { get; set; }
            [JsonProperty("profile", NullValueHandling = NullValueHandling.Ignore)] public JObject Profile { get; set; }
        }
    }
}
