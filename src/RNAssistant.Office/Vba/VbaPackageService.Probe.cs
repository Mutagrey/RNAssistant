using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Vba
{
    internal sealed partial class VbaPackageService
    {
        public VbaPackageProbeResult Probe(VbaPackageDefinition package)
        {
            if (package == null || package.Components == null || package.Components.Count == 0)
            {
                return new VbaPackageProbeResult
                {
                    State = VbaPackageInstallationState.Unavailable,
                    Data = new JObject { ["errorCode"] = "vba_package_empty" }
                };
            }

            var expectedHash = PackageHash(package);
            var details = new JArray();
            var observations = new List<PackageComponentObservation>();
            foreach (var component in package.Components)
            {
                var read = ReadPackageComponent(component.Name);
                if (read == null || !read.Success)
                {
                    if (read != null && read.IsNotFound)
                    {
                        observations.Add(PackageComponentObservation.Missing(component));
                        details.Add(new JObject
                        {
                            ["name"] = component.Name,
                            ["status"] = "missing"
                        });
                        continue;
                    }
                    return new VbaPackageProbeResult
                    {
                        State = VbaPackageInstallationState.Unavailable,
                        Data = new JObject
                        {
                            ["errorCode"] = read == null ? "vba_read_missing_result" : read.ErrorCode,
                            ["message"] = read == null ? "VBA package component read returned no result." : read.Message,
                            ["component"] = component.Name,
                            ["details"] = details
                        }
                    };
                }

                var current = read.Module;
                var expectedComparable = VbaTextCanonicalizer.PackageComparableCodeSha256(component.Code);
                var actualComparable = VbaTextCanonicalizer.PackageComparableCodeSha256(current.Code);
                var sourceMatches = string.Equals(expectedComparable, actualComparable, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(component.Type, current.ComponentType, StringComparison.OrdinalIgnoreCase) &&
                    (!string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase) || current.CodeOnlyUserForm == true);
                var marker = VbaPackageOwnershipMarker.Parse(current.Code);
                var markerMatches = marker.Valid &&
                    string.Equals(marker.PackageId, package.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(marker.PackageVersion, package.Version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(marker.PackageHash, expectedHash, StringComparison.OrdinalIgnoreCase);
                observations.Add(new PackageComponentObservation
                {
                    Component = component,
                    Exists = true,
                    SourceMatches = sourceMatches,
                    Marker = marker,
                    MarkerMatches = markerMatches
                });
                details.Add(new JObject
                {
                    ["name"] = component.Name,
                    ["status"] = sourceMatches ? "matching" : "modified",
                    ["expectedComparable"] = expectedComparable,
                    ["actualComparable"] = actualComparable,
                    ["expectedType"] = component.Type,
                    ["actualType"] = current.ComponentType,
                    ["ownership"] = marker.Found ? marker.Valid ? marker.Kind : "invalid" : "none",
                    ["ownershipMatches"] = marker.Found && markerMatches,
                    ["lifecycleId"] = marker.LifecycleId
                });
            }

            return ApplyUnresolvedLifecycle(package, ClassifyObservations(observations, details));
        }

        public string ClassifyDocumentSnapshot(
            ToolPackageSource globalSource,
            IReadOnlyList<ToolPackageSourceComponent> liveComponents)
        {
            var preparation = PreparePackage(globalSource);
            if (!preparation.Success) return "invalid";
            var package = preparation.Package;
            var expectedHash = PackageHash(package);
            var live = (liveComponents ?? new ToolPackageSourceComponent[0])
                .Where(component => component != null && !string.IsNullOrWhiteSpace(component.Name))
                .GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var observations = new List<PackageComponentObservation>();
            var details = new JArray();
            var expectedNames = new HashSet<string>(
                package.Components.Select(component => component.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var component in package.Components)
            {
                ToolPackageSourceComponent current;
                if (!live.TryGetValue(component.Name, out current))
                {
                    observations.Add(PackageComponentObservation.Missing(component));
                    details.Add(new JObject { ["name"] = component.Name, ["status"] = "missing" });
                    continue;
                }
                var sourceMatches = string.Equals(
                        VbaTextCanonicalizer.PackageComparableCodeSha256(component.Code),
                        VbaTextCanonicalizer.PackageComparableCodeSha256(current.Code),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(component.Type, current.Type, StringComparison.OrdinalIgnoreCase);
                var marker = VbaPackageOwnershipMarker.Parse(current.Code);
                var markerMatches = marker.Valid &&
                    string.Equals(marker.PackageId, package.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(marker.PackageVersion, package.Version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(marker.PackageHash, expectedHash, StringComparison.OrdinalIgnoreCase);
                observations.Add(new PackageComponentObservation
                {
                    Component = component,
                    Exists = true,
                    SourceMatches = sourceMatches,
                    Marker = marker,
                    MarkerMatches = markerMatches
                });
                details.Add(new JObject
                {
                    ["name"] = component.Name,
                    ["status"] = sourceMatches ? "matching" : "modified",
                    ["ownership"] = marker.Found ? marker.Valid ? marker.Kind : "invalid" : "none",
                    ["ownershipMatches"] = marker.Found && markerMatches
                });
            }
            var unexpected = live.Keys.Where(name => !expectedNames.Contains(name)).ToList();
            foreach (var name in unexpected)
            {
                details.Add(new JObject
                {
                    ["name"] = name,
                    ["status"] = "unexpected"
                });
            }
            var classified = ClassifyObservations(observations, details);
            if (unexpected.Count > 0)
            {
                classified = new VbaPackageProbeResult
                {
                    State = VbaPackageInstallationState.ModifiedLocal,
                    Data = new JObject
                    {
                        ["state"] = StatusText(VbaPackageInstallationState.ModifiedLocal),
                        ["details"] = details,
                        ["canCleanupSession"] = false
                    }
                };
            }
            return StatusText(ApplyUnresolvedLifecycle(package, classified).State);
        }

        public static string StatusText(VbaPackageInstallationState state)
        {
            switch (state)
            {
                case VbaPackageInstallationState.NotInstalled: return "not_installed";
                case VbaPackageInstallationState.DocumentLocal: return "document_local";
                case VbaPackageInstallationState.Persistent: return "installed";
                case VbaPackageInstallationState.SessionOwned: return "session_cleanup_required";
                case VbaPackageInstallationState.Partial: return "partial";
                case VbaPackageInstallationState.ModifiedLocal: return "modified_local";
                case VbaPackageInstallationState.RecoveryRequired: return "recovery_required";
                default: return "unavailable";
            }
        }

        internal static string PackageHash(VbaPackageDefinition package)
        {
            return TextPatternEngine.Sha256(string.Join(
                "\n",
                (package.Components ?? new VbaPackageComponent[0])
                    .OrderBy(component => component.Name)
                    .Select(component => component.Name + ":" +
                        VbaTextCanonicalizer.PackageCodeSha256(component.Code))
                    .ToArray()));
        }

        private static bool SameOwnedMarker(
            IList<PackageComponentObservation> observations,
            string kind,
            out string ownershipMarker,
            out string lifecycleId)
        {
            ownershipMarker = null;
            lifecycleId = null;
            if (observations == null || observations.Count == 0) return false;
            var first = observations[0];
            if (!first.MarkerMatches || !string.Equals(first.Marker.Kind, kind, StringComparison.Ordinal)) return false;
            ownershipMarker = first.Marker.Raw;
            lifecycleId = first.Marker.LifecycleId;
            var expectedMarker = ownershipMarker;
            var expectedLifecycleId = lifecycleId;
            return observations.All(item =>
                item.MarkerMatches &&
                string.Equals(item.Marker.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(item.Marker.Raw, expectedMarker, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Marker.LifecycleId ?? string.Empty, expectedLifecycleId ?? string.Empty, StringComparison.Ordinal));
        }

        private static VbaPackageProbeResult ClassifyObservations(
            IList<PackageComponentObservation> observations,
            JArray details)
        {
            var present = observations.Where(item => item.Exists).ToList();
            VbaPackageInstallationState state;
            string ownershipMarker = null;
            string lifecycleId = null;
            var canCleanupSession = false;
            if (present.Count == 0)
            {
                state = VbaPackageInstallationState.NotInstalled;
            }
            else if (present.Any(item => !item.SourceMatches))
            {
                state = VbaPackageInstallationState.ModifiedLocal;
            }
            else if (present.Count < observations.Count)
            {
                canCleanupSession = SameOwnedMarker(present, "session", out ownershipMarker, out lifecycleId);
                state = canCleanupSession
                    ? VbaPackageInstallationState.RecoveryRequired
                    : VbaPackageInstallationState.Partial;
            }
            else if (present.All(item => !item.Marker.Found))
            {
                state = VbaPackageInstallationState.DocumentLocal;
            }
            else if (SameOwnedMarker(present, "persistent", out ownershipMarker, out lifecycleId))
            {
                state = VbaPackageInstallationState.Persistent;
            }
            else if (SameOwnedMarker(present, "session", out ownershipMarker, out lifecycleId))
            {
                state = VbaPackageInstallationState.SessionOwned;
                canCleanupSession = true;
            }
            else
            {
                state = VbaPackageInstallationState.RecoveryRequired;
            }
            return new VbaPackageProbeResult
            {
                State = state,
                OwnershipMarker = ownershipMarker,
                LifecycleId = lifecycleId,
                CanCleanupSession = canCleanupSession,
                Data = new JObject
                {
                    ["state"] = StatusText(state),
                    ["details"] = details ?? new JArray(),
                    ["canCleanupSession"] = canCleanupSession,
                    ["lifecycleId"] = lifecycleId
                }
            };
        }

        private VbaPackageProbeResult ApplyUnresolvedLifecycle(
            VbaPackageDefinition package,
            VbaPackageProbeResult probe)
        {
            UnresolvedSessionLifecycle unresolved;
            try
            {
                unresolved = FindUnresolvedSessionLifecycle(package);
            }
            catch (Exception ex)
            {
                return new VbaPackageProbeResult
                {
                    State = VbaPackageInstallationState.Unavailable,
                    Data = new JObject
                    {
                        ["errorCode"] = "vba_journal_unavailable",
                        ["message"] = "VBA package lifecycle history could not be read. " + ex.Message
                    }
                };
            }
            if (unresolved == null) return probe;

            var liveLifecycleMatches = !string.IsNullOrWhiteSpace(probe.LifecycleId) &&
                string.Equals(probe.LifecycleId, unresolved.LifecycleId, StringComparison.Ordinal);
            if (probe.State == VbaPackageInstallationState.NotInstalled)
            {
                probe.State = VbaPackageInstallationState.RecoveryRequired;
                probe.CanCleanupSession = true;
                probe.LifecycleId = unresolved.LifecycleId;
                probe.OwnershipMarker = unresolved.OwnershipMarker;
            }
            else if ((probe.State == VbaPackageInstallationState.SessionOwned ||
                      probe.State == VbaPackageInstallationState.RecoveryRequired) &&
                probe.CanCleanupSession && liveLifecycleMatches)
            {
                probe.LifecycleId = unresolved.LifecycleId;
            }
            else
            {
                probe.State = VbaPackageInstallationState.RecoveryRequired;
                probe.CanCleanupSession = false;
            }
            probe.Data = probe.Data ?? new JObject();
            probe.Data["state"] = StatusText(probe.State);
            probe.Data["durableLifecycleIncomplete"] = true;
            probe.Data["lifecycleId"] = unresolved.LifecycleId;
            probe.Data["canCleanupSession"] = probe.CanCleanupSession;
            return probe;
        }

        private UnresolvedSessionLifecycle FindUnresolvedSessionLifecycle(VbaPackageDefinition package)
        {
            var records = _journal.ListPackageMutations(
                _document.HostName,
                _document.DocumentKey);
            var groups = (records ?? new VbaPackageMutationRecord[0])
                .Where(record => record != null && record.Prepared != null &&
                    record.Prepared.SessionOnly &&
                    !string.IsNullOrWhiteSpace(record.Prepared.LifecycleId) &&
                    string.Equals(record.Prepared.PackageId, package.Id, StringComparison.OrdinalIgnoreCase))
                .GroupBy(record => record.Prepared.LifecycleId, StringComparer.Ordinal)
                .OrderByDescending(group => group.Max(record => record.Prepared.CreatedUtc));
            foreach (var group in groups)
            {
                var install = group
                    .Where(record => string.Equals(
                        record.Prepared.Operation,
                        "package_install",
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => record.Prepared.CreatedUtc)
                    .FirstOrDefault();
                if (install == null || install.Terminal != null &&
                    !string.Equals(install.Terminal.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal) &&
                    !string.Equals(install.Terminal.Status, VbaMutationStatuses.Unknown, StringComparison.Ordinal))
                {
                    continue;
                }
                var cleanupCommitted = group.Any(record =>
                    string.Equals(record.Prepared.Operation, "package_remove", StringComparison.OrdinalIgnoreCase) &&
                    record.Terminal != null &&
                    string.Equals(record.Terminal.Status, VbaMutationStatuses.Committed, StringComparison.Ordinal));
                if (cleanupCommitted) continue;
                return new UnresolvedSessionLifecycle
                {
                    LifecycleId = group.Key,
                    OwnershipMarker = install.Prepared.OwnershipMarker
                };
            }
            return null;
        }

        private sealed class PackageComponentObservation
        {
            public VbaPackageComponent Component { get; set; }
            public bool Exists { get; set; }
            public bool SourceMatches { get; set; }
            public VbaPackageOwnershipMarker Marker { get; set; }
            public bool MarkerMatches { get; set; }

            public static PackageComponentObservation Missing(VbaPackageComponent component)
            {
                return new PackageComponentObservation
                {
                    Component = component,
                    Exists = false,
                    Marker = VbaPackageOwnershipMarker.None()
                };
            }
        }

        private sealed class UnresolvedSessionLifecycle
        {
            public string LifecycleId { get; set; }
            public string OwnershipMarker { get; set; }
        }
    }

    public sealed class VbaPackageOwnershipMarker
    {
        private VbaPackageOwnershipMarker()
        {
        }

        public bool Found { get; private set; }
        public bool Valid { get; private set; }
        public string Kind { get; private set; }
        public string PackageId { get; private set; }
        public string PackageVersion { get; private set; }
        public string PackageHash { get; private set; }
        public string LifecycleId { get; private set; }
        public string Raw { get; private set; }

        public static VbaPackageOwnershipMarker None()
        {
            return new VbaPackageOwnershipMarker();
        }

        public static bool ContainsReservedMarker(string code)
        {
            return Lines(code).Any(IsMarkerLine);
        }

        public static string Evidence(string code)
        {
            var markers = Lines(code)
                .Where(IsMarkerLine)
                .Select(NormalizeMarkerLine)
                .ToArray();
            return markers.Length == 0 ? null : string.Join("\n", markers);
        }

        public static VbaPackageOwnershipMarker Parse(string code)
        {
            var markerLines = Lines(code).Where(IsMarkerLine).Select(NormalizeMarkerLine).ToList();
            if (markerLines.Count == 0) return None();
            if (markerLines.Count != 1) return Invalid(string.Join("\n", markerLines));

            var raw = markerLines[0];
            string kind;
            string body;
            if (raw.StartsWith("RNAssistantPackage:", StringComparison.OrdinalIgnoreCase))
            {
                kind = "persistent";
                body = raw.Substring("RNAssistantPackage:".Length);
            }
            else if (raw.StartsWith("RNAssistantSession:", StringComparison.OrdinalIgnoreCase))
            {
                kind = "session";
                body = raw.Substring("RNAssistantSession:".Length);
            }
            else
            {
                return Invalid(raw);
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in body.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;
                var equals = segment.IndexOf('=');
                if (equals <= 0) return Invalid(raw);
                var name = segment.Substring(0, equals).Trim();
                var value = segment.Substring(equals + 1).Trim();
                if (string.IsNullOrWhiteSpace(value) || fields.ContainsKey(name)) return Invalid(raw);
                if (!string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "version", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "hash", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "lifecycle", StringComparison.OrdinalIgnoreCase)) return Invalid(raw);
                fields.Add(name, value);
            }

            string id;
            string version;
            string hash;
            string lifecycle;
            fields.TryGetValue("id", out id);
            fields.TryGetValue("version", out version);
            fields.TryGetValue("hash", out hash);
            fields.TryGetValue("lifecycle", out lifecycle);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) || !ValidSha256(hash) ||
                string.Equals(kind, "persistent", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(lifecycle))
            {
                return Invalid(raw);
            }
            return new VbaPackageOwnershipMarker
            {
                Found = true,
                Valid = true,
                Kind = kind,
                PackageId = id,
                PackageVersion = version,
                PackageHash = hash,
                LifecycleId = lifecycle,
                Raw = raw
            };
        }

        private static VbaPackageOwnershipMarker Invalid(string raw)
        {
            return new VbaPackageOwnershipMarker
            {
                Found = true,
                Valid = false,
                Raw = raw
            };
        }

        private static IEnumerable<string> Lines(string code)
        {
            return (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static bool IsMarkerLine(string line)
        {
            var value = (line ?? string.Empty).TrimStart();
            return value.StartsWith("' RNAssistantPackage:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("' RNAssistantSession:", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMarkerLine(string line)
        {
            var value = (line ?? string.Empty).TrimStart();
            if (value.StartsWith("'", StringComparison.Ordinal)) value = value.Substring(1).TrimStart();
            return value;
        }

        private static bool ValidSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(character =>
                character >= '0' && character <= '9' ||
                character >= 'a' && character <= 'f' ||
                character >= 'A' && character <= 'F');
        }
    }
}
