using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Qualification
{
    public sealed class QualificationCoverageEntry
    {
        internal QualificationCoverageEntry(string id, string owner, IReadOnlyList<string> hosts,
            IReadOnlyList<string> suites, bool mandatory)
        {
            Id = id;
            Owner = owner;
            Hosts = hosts;
            Suites = suites;
            Mandatory = mandatory;
        }

        public string Id { get; private set; }
        public string Owner { get; private set; }
        public IReadOnlyList<string> Hosts { get; private set; }
        public IReadOnlyList<string> Suites { get; private set; }
        public bool Mandatory { get; private set; }
    }

    public sealed class QualificationCoverageRegistry
    {
        private readonly Dictionary<string, QualificationCoverageEntry> _entries;

        private QualificationCoverageRegistry(IEnumerable<QualificationCoverageEntry> entries)
        {
            _entries = new Dictionary<string, QualificationCoverageEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (_entries.ContainsKey(entry.Id))
                    throw new QualificationManifestException("duplicate_coverage", "Duplicate coverage id: " + entry.Id + ".");
                _entries.Add(entry.Id, entry);
            }
        }

        public IReadOnlyList<QualificationCoverageEntry> Entries
        {
            get { return Array.AsReadOnly(_entries.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray()); }
        }

        public static QualificationCoverageRegistry Parse(string json)
        {
            var root = QualificationJson.ReadObject(json, "Qualification coverage registry");
            QualificationJson.EnsureOnly(root, new[] { "schemaVersion", "entries" }, "Qualification coverage registry");
            if (root["schemaVersion"] == null || root["schemaVersion"].Type != JTokenType.Integer ||
                (int)root["schemaVersion"] != 1)
                throw new QualificationManifestException("coverage_version", "Coverage schemaVersion must be 1.");
            var array = root["entries"] as JArray;
            if (array == null || array.Count == 0 || array.Count > 512)
                throw new QualificationManifestException("coverage_count", "Coverage entries must contain between 1 and 512 items.");
            var entries = new List<QualificationCoverageEntry>(array.Count);
            for (var index = 0; index < array.Count; index++)
            {
                var item = array[index] as JObject;
                var subject = "entries[" + index + "]";
                if (item == null) throw new QualificationManifestException("coverage_entry", subject + " must be an object.");
                QualificationJson.EnsureOnly(item, new[] { "id", "owner", "hosts", "suites", "mandatory" }, subject);
                var id = QualificationJson.RequiredString(item, "id", 96, subject);
                if (!Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9]*(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant))
                    throw new QualificationManifestException("coverage_id", "Invalid coverage id: " + id + ".");
                var owner = QualificationJson.RequiredString(item, "owner", 128, subject);
                var hosts = QualificationJson.StringArray(item, "hosts", 8, 32, true, subject);
                if (hosts.Any(host => host != "*" && host != "Excel" && host != "Word" &&
                    host != "PowerPoint" && host != "Outlook"))
                    throw new QualificationManifestException("coverage_host", subject + " contains an unsupported host.");
                var suites = QualificationJson.StringArray(item, "suites", 3, 16, true, subject)
                    .Select(value => value.ToLowerInvariant()).ToArray();
                if (suites.Any(value => value != "quick" && value != "full" && value != "release"))
                    throw new QualificationManifestException("coverage_suite", subject + " contains an unsupported suite.");
                if (item["mandatory"] == null || item["mandatory"].Type != JTokenType.Boolean)
                    throw new QualificationManifestException("coverage_mandatory", subject + ".mandatory must be boolean.");
                entries.Add(new QualificationCoverageEntry(id, owner, Array.AsReadOnly(hosts.ToArray()),
                    Array.AsReadOnly(suites), (bool)item["mandatory"]));
            }
            return new QualificationCoverageRegistry(entries);
        }

        internal void EnsureKnown(IEnumerable<string> coverageIds, string packId)
        {
            var unknown = coverageIds.FirstOrDefault(id => !_entries.ContainsKey(id));
            if (unknown != null)
                throw new QualificationManifestException("unknown_coverage",
                    "Pack " + packId + " references unknown coverage id: " + unknown + ".");
        }

        internal IReadOnlyList<string> RequiredFor(string host, string suite)
        {
            return Array.AsReadOnly(_entries.Values.Where(entry => entry.Mandatory &&
                entry.Suites.Contains(suite, StringComparer.OrdinalIgnoreCase) &&
                (entry.Hosts.Contains("*") || entry.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)))
                .Select(entry => entry.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public sealed class QualificationPackAvailability
    {
        internal QualificationPackAvailability(QualificationPack pack, IReadOnlyList<string> missingRequirements)
        {
            Pack = pack;
            MissingRequirements = missingRequirements;
        }

        public QualificationPack Pack { get; private set; }
        public IReadOnlyList<string> MissingRequirements { get; private set; }
        public bool Available { get { return MissingRequirements.Count == 0; } }
    }

    public sealed class QualificationPackCatalog
    {
        private static readonly HashSet<string> Hosts = new HashSet<string>(
            new[] { "Excel", "Word", "PowerPoint", "Outlook" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Suites = new HashSet<string>(
            new[] { "quick", "full", "release" }, StringComparer.OrdinalIgnoreCase);
        private readonly QualificationCoverageRegistry _coverage;
        private readonly Dictionary<string, QualificationPack> _packs;

        public QualificationPackCatalog(QualificationCoverageRegistry coverage, IEnumerable<QualificationPack> packs)
        {
            _coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
            _packs = new Dictionary<string, QualificationPack>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs ?? new QualificationPack[0])
            {
                if (pack == null) throw new ArgumentException("Catalog packs cannot contain null.", nameof(packs));
                if (_packs.ContainsKey(pack.Id))
                    throw new QualificationManifestException("duplicate_pack", "Duplicate current pack id: " + pack.Id + ".");
                _coverage.EnsureKnown(pack.Coverage, pack.Id);
                _packs.Add(pack.Id, pack);
            }
        }

        public QualificationPack Get(string id)
        {
            QualificationPack pack;
            if (!_packs.TryGetValue(id ?? string.Empty, out pack))
                throw new KeyNotFoundException("Qualification pack was not found: " + (id ?? "<null>") + ".");
            return pack;
        }

        public IReadOnlyList<QualificationPackAvailability> List(string host, string suite,
            IEnumerable<string> availableRequirements)
        {
            ValidateScope(host, suite);
            var available = new HashSet<string>(availableRequirements ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return Array.AsReadOnly(_packs.Values.Where(pack =>
                    string.Equals(pack.Suite, suite, StringComparison.OrdinalIgnoreCase) &&
                    (pack.Hosts.Contains("*") || pack.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
                .Select(pack => new QualificationPackAvailability(pack,
                    Array.AsReadOnly(pack.Requirements.Where(requirement => !available.Contains(requirement)).ToArray())))
                .ToArray());
        }

        public IReadOnlyList<string> MissingCoverage(string host, string suite)
        {
            ValidateScope(host, suite);
            var covered = new HashSet<string>(_packs.Values.Where(pack =>
                    string.Equals(pack.Suite, suite, StringComparison.OrdinalIgnoreCase) &&
                    (pack.Hosts.Contains("*") || pack.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase)))
                .SelectMany(pack => pack.Coverage), StringComparer.OrdinalIgnoreCase);
            return Array.AsReadOnly(_coverage.RequiredFor(host, suite).Where(id => !covered.Contains(id)).ToArray());
        }

        private static void ValidateScope(string host, string suite)
        {
            if (string.IsNullOrWhiteSpace(host) || !Hosts.Contains(host))
                throw new ArgumentException("Qualification host is missing or unsupported.", nameof(host));
            if (string.IsNullOrWhiteSpace(suite) || !Suites.Contains(suite))
                throw new ArgumentException("Qualification suite is missing or unsupported.", nameof(suite));
        }
    }
}
