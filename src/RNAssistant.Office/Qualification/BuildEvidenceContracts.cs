using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Qualification
{
    internal sealed class BuildEvidenceFileRecord
    {
        internal BuildEvidenceFileRecord(string id, string path, long byteLength, string sha256)
        {
            Id = id;
            Path = path;
            ByteLength = byteLength;
            Sha256 = sha256;
        }

        public string Id { get; private set; }
        public string Path { get; private set; }
        public long ByteLength { get; private set; }
        public string Sha256 { get; private set; }
    }

    internal sealed class BuildEvidenceCheckRecord
    {
        internal BuildEvidenceCheckRecord(string id, string outcome, string completedUtc, string evidenceSha256)
        {
            Id = id;
            Outcome = outcome;
            CompletedUtc = completedUtc;
            EvidenceSha256 = evidenceSha256;
        }

        public string Id { get; private set; }
        public string Outcome { get; private set; }
        public string CompletedUtc { get; private set; }
        public string EvidenceSha256 { get; private set; }
    }

    internal sealed class BuildEvidenceRunRecord
    {
        internal BuildEvidenceRunRecord(string packId, string host, string variant, string packRevision,
            string packSha256, string outcome, string runId, string completedEventId,
            string completedUtc, string evidenceSha256)
        {
            PackId = packId;
            Host = host;
            Variant = variant;
            PackRevision = packRevision;
            PackSha256 = packSha256;
            Outcome = outcome;
            RunId = runId;
            CompletedEventId = completedEventId;
            CompletedUtc = completedUtc;
            EvidenceSha256 = evidenceSha256;
        }

        public string PackId { get; private set; }
        public string Host { get; private set; }
        public string Variant { get; private set; }
        public string PackRevision { get; private set; }
        public string PackSha256 { get; private set; }
        public string Outcome { get; private set; }
        public string RunId { get; private set; }
        public string CompletedEventId { get; private set; }
        public string CompletedUtc { get; private set; }
        public string EvidenceSha256 { get; private set; }

        internal string Key
        {
            get { return PackId + "|" + Host + "|" + Variant; }
        }
    }

    internal sealed class BuildEvidenceManifest
    {
        private BuildEvidenceManifest(string productVersion, string informationalVersion, string commitSha,
            string buildUtc, string branch, string channel, string workingTreeState, string configuration,
            string platform, string catalogSha256, string environment, string environmentSha256,
            string evidenceBundleSha256, IReadOnlyList<BuildEvidenceFileRecord> files,
            IReadOnlyList<BuildEvidenceCheckRecord> checks, IReadOnlyList<BuildEvidenceRunRecord> runs)
        {
            ProductVersion = productVersion;
            InformationalVersion = informationalVersion;
            CommitSha = commitSha;
            BuildUtc = buildUtc;
            Branch = branch;
            Channel = channel;
            WorkingTreeState = workingTreeState;
            Configuration = configuration;
            Platform = platform;
            CatalogSha256 = catalogSha256;
            Environment = environment;
            EnvironmentSha256 = environmentSha256;
            EvidenceBundleSha256 = evidenceBundleSha256;
            Files = files;
            Checks = checks;
            Runs = runs;
        }

        public string ProductVersion { get; private set; }
        public string InformationalVersion { get; private set; }
        public string CommitSha { get; private set; }
        public string BuildUtc { get; private set; }
        public string Branch { get; private set; }
        public string Channel { get; private set; }
        public string WorkingTreeState { get; private set; }
        public string Configuration { get; private set; }
        public string Platform { get; private set; }
        public string CatalogSha256 { get; private set; }
        public string Environment { get; private set; }
        public string EnvironmentSha256 { get; private set; }
        public string EvidenceBundleSha256 { get; private set; }
        public IReadOnlyList<BuildEvidenceFileRecord> Files { get; private set; }
        public IReadOnlyList<BuildEvidenceCheckRecord> Checks { get; private set; }
        public IReadOnlyList<BuildEvidenceRunRecord> Runs { get; private set; }

        public static BuildEvidenceManifest Parse(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > 1048576)
                throw new QualificationManifestException("build_evidence_payload_bounds",
                    "Build evidence payload is empty or exceeds 1 MiB.");
            string json;
            try { json = new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException ex)
            {
                throw new QualificationManifestException("build_evidence_utf8",
                    "Build evidence payload is not valid UTF-8.", ex);
            }
            if (json.Length > 0 && json[0] == '\ufeff')
                throw new QualificationManifestException("build_evidence_bom",
                    "Build evidence payload must use UTF-8 without BOM.");
            var root = QualificationJson.ReadObject(json, "Build evidence payload", 1048576);
            QualificationJson.EnsureOnly(root, new[]
            {
                "schemaVersion", "status", "productVersion", "informationalVersion", "commitSha",
                "buildUtc", "branch", "channel", "workingTreeState", "configuration", "platform",
                "catalogSha256", "environment", "environmentSha256", "evidenceBundleSha256",
                "files", "checks", "runs"
            }, "Build evidence payload");
            if (Integer(root, "schemaVersion", "Build evidence payload") != 1)
                throw new QualificationManifestException("build_evidence_schema", "Build evidence schemaVersion must be 1.");
            if (Required(root, "status", 16, "Build evidence payload") != "complete")
                throw new QualificationManifestException("build_evidence_status", "Build evidence status must be complete.");
            var productVersion = Required(root, "productVersion", 64, "Build evidence payload");
            var informationalVersion = Required(root, "informationalVersion", 128, "Build evidence payload");
            var commitSha = Hash(root, "commitSha", true, "Build evidence payload");
            var buildUtc = Utc(root, "buildUtc", "Build evidence payload");
            var branch = Required(root, "branch", 128, "Build evidence payload");
            var channel = Identifier(root, "channel", 32, "Build evidence payload");
            var tree = Required(root, "workingTreeState", 16, "Build evidence payload");
            if (tree != "clean" && tree != "dirty")
                throw new QualificationManifestException("build_evidence_tree", "workingTreeState must be clean or dirty.");
            var configuration = Required(root, "configuration", 32, "Build evidence payload");
            var platform = Required(root, "platform", 32, "Build evidence payload");
            var catalogSha256 = Hash(root, "catalogSha256", false, "Build evidence payload");
            var environment = Identifier(root, "environment", 96, "Build evidence payload");
            var environmentSha256 = Hash(root, "environmentSha256", false, "Build evidence payload");
            var bundleSha256 = Hash(root, "evidenceBundleSha256", false, "Build evidence payload");
            var files = ParseFiles(root["files"] as JArray);
            var checks = ParseChecks(root["checks"] as JArray);
            var runs = ParseRuns(root["runs"] as JArray);
            return new BuildEvidenceManifest(productVersion, informationalVersion, commitSha, buildUtc,
                branch, channel, tree, configuration, platform, catalogSha256, environment,
                environmentSha256, bundleSha256, files, checks, runs);
        }

        private static IReadOnlyList<BuildEvidenceFileRecord> ParseFiles(JArray array)
        {
            if (array == null || array.Count == 0 || array.Count > 256)
                throw new QualificationManifestException("build_evidence_files", "files must contain 1 to 256 records.");
            var result = new List<BuildEvidenceFileRecord>();
            foreach (var token in array)
            {
                var item = token as JObject;
                if (item == null) throw new QualificationManifestException("build_evidence_file", "Each file record must be an object.");
                QualificationJson.EnsureOnly(item, new[] { "id", "path", "byteLength", "sha256" }, "Build evidence file");
                var id = Identifier(item, "id", 96, "Build evidence file");
                var path = Required(item, "path", 512, "Build evidence file").Replace('\\', '/');
                if (Path.IsPathRooted(path) || path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                    throw new QualificationManifestException("build_evidence_path", "Build evidence file path must be safe and relative.");
                var length = Long(item, "byteLength", "Build evidence file");
                if (length < 0) throw new QualificationManifestException("build_evidence_length", "Build evidence file length cannot be negative.");
                result.Add(new BuildEvidenceFileRecord(id, path, length,
                    Hash(item, "sha256", false, "Build evidence file")));
            }
            EnsureUnique(result.Select(item => item.Id), "build evidence file id");
            EnsureUnique(result.Select(item => item.Path), "build evidence file path");
            return Array.AsReadOnly(result.ToArray());
        }

        private static IReadOnlyList<BuildEvidenceCheckRecord> ParseChecks(JArray array)
        {
            if (array == null || array.Count == 0 || array.Count > 64)
                throw new QualificationManifestException("build_evidence_checks", "checks must contain 1 to 64 records.");
            var result = new List<BuildEvidenceCheckRecord>();
            foreach (var token in array)
            {
                var item = token as JObject;
                if (item == null) throw new QualificationManifestException("build_evidence_check", "Each check record must be an object.");
                QualificationJson.EnsureOnly(item, new[] { "id", "outcome", "completedUtc", "evidenceSha256" }, "Build evidence check");
                result.Add(new BuildEvidenceCheckRecord(
                    Identifier(item, "id", 96, "Build evidence check"),
                    Outcome(item, "Build evidence check"),
                    Utc(item, "completedUtc", "Build evidence check"),
                    Hash(item, "evidenceSha256", false, "Build evidence check")));
            }
            EnsureUnique(result.Select(item => item.Id), "build evidence check id");
            return Array.AsReadOnly(result.ToArray());
        }

        private static IReadOnlyList<BuildEvidenceRunRecord> ParseRuns(JArray array)
        {
            if (array == null || array.Count == 0 || array.Count > 64)
                throw new QualificationManifestException("build_evidence_runs", "runs must contain 1 to 64 records.");
            var result = new List<BuildEvidenceRunRecord>();
            foreach (var token in array)
            {
                var item = token as JObject;
                if (item == null) throw new QualificationManifestException("build_evidence_run", "Each run record must be an object.");
                QualificationJson.EnsureOnly(item, new[]
                {
                    "packId", "host", "variant", "packRevision", "packSha256", "outcome",
                    "runId", "completedEventId", "completedUtc", "evidenceSha256"
                }, "Build evidence run");
                result.Add(new BuildEvidenceRunRecord(
                    Identifier(item, "packId", 96, "Build evidence run"),
                    Required(item, "host", 32, "Build evidence run"),
                    Identifier(item, "variant", 64, "Build evidence run"),
                    Required(item, "packRevision", 32, "Build evidence run"),
                    Hash(item, "packSha256", false, "Build evidence run"),
                    Outcome(item, "Build evidence run"),
                    Identifier(item, "runId", 128, "Build evidence run"),
                    Identifier(item, "completedEventId", 128, "Build evidence run"),
                    Utc(item, "completedUtc", "Build evidence run"),
                    Hash(item, "evidenceSha256", false, "Build evidence run")));
            }
            EnsureUnique(result.Select(item => item.Key), "build evidence run key");
            return Array.AsReadOnly(result.ToArray());
        }

        private static int Integer(JObject value, string field, string subject)
        {
            var token = value[field];
            if (token == null || token.Type != JTokenType.Integer)
                throw new QualificationManifestException("build_evidence_integer", subject + "." + field + " must be an integer.");
            return (int)token;
        }

        private static long Long(JObject value, string field, string subject)
        {
            var token = value[field];
            if (token == null || token.Type != JTokenType.Integer)
                throw new QualificationManifestException("build_evidence_integer", subject + "." + field + " must be an integer.");
            return (long)token;
        }

        private static string Required(JObject value, string field, int maximum, string subject)
        {
            return QualificationJson.RequiredString(value, field, maximum, subject);
        }

        private static string Identifier(JObject value, string field, int maximum, string subject)
        {
            var result = Required(value, field, maximum, subject);
            if (!Regex.IsMatch(result, "^[A-Za-z0-9][A-Za-z0-9._*-]*$", RegexOptions.CultureInvariant))
                throw new QualificationManifestException("build_evidence_identifier", subject + "." + field + " is not a bounded identifier.");
            return result;
        }

        private static string Hash(JObject value, string field, bool commit, string subject)
        {
            var result = Required(value, field, commit ? 64 : 64, subject);
            var pattern = commit ? "^(?:[0-9a-f]{40}|[0-9a-f]{64})$" : "^[0-9a-f]{64}$";
            if (!Regex.IsMatch(result, pattern, RegexOptions.CultureInvariant))
                throw new QualificationManifestException("build_evidence_hash", subject + "." + field + " must be a lowercase SHA value.");
            return result;
        }

        private static string Utc(JObject value, string field, string subject)
        {
            var result = Required(value, field, 20, subject);
            DateTime parsed;
            if (!DateTime.TryParseExact(result, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out parsed))
                throw new QualificationManifestException("build_evidence_utc", subject + "." + field + " must be UTC yyyy-MM-ddTHH:mm:ssZ.");
            return result;
        }

        private static string Outcome(JObject value, string subject)
        {
            var result = Required(value, "outcome", 16, subject);
            if (result != "passed" && result != "failed" && result != "blocked" && result != "unknown")
                throw new QualificationManifestException("build_evidence_outcome", subject + ".outcome is unsupported.");
            return result;
        }

        private static void EnsureUnique(IEnumerable<string> values, string subject)
        {
            var items = values.ToArray();
            if (items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
                throw new QualificationManifestException("build_evidence_duplicate", "Duplicate " + subject + ".");
        }
    }

    internal sealed class BuildEvidenceEnvelope
    {
        private BuildEvidenceEnvelope(byte[] certificateDer, byte[] payload, byte[] signature, string sha256)
        {
            CertificateDer = certificateDer;
            Payload = payload;
            Signature = signature;
            Sha256 = sha256;
        }

        public byte[] CertificateDer { get; private set; }
        public byte[] Payload { get; private set; }
        public byte[] Signature { get; private set; }
        public string Sha256 { get; private set; }

        public static BuildEvidenceEnvelope Parse(string json)
        {
            var root = QualificationJson.ReadObject(json, "Build evidence envelope", 2097152);
            QualificationJson.EnsureOnly(root,
                new[] { "schemaVersion", "algorithm", "certificateDer", "payloadBase64", "signatureBase64" },
                "Build evidence envelope");
            var schema = root["schemaVersion"];
            if (schema == null || schema.Type != JTokenType.Integer || (int)schema != 1)
                throw new QualificationManifestException("build_evidence_envelope_schema", "Build evidence envelope schemaVersion must be 1.");
            if (QualificationJson.RequiredString(root, "algorithm", 16, "Build evidence envelope") != "RS256")
                throw new QualificationManifestException("build_evidence_algorithm", "Build evidence signature algorithm must be RS256.");
            var certificate = Base64(root, "certificateDer", 16384);
            var payload = Base64(root, "payloadBase64", 1048576);
            var signature = Base64(root, "signatureBase64", 8192);
            if (certificate.Length == 0 || payload.Length == 0 || signature.Length == 0)
                throw new QualificationManifestException("build_evidence_envelope_empty", "Build evidence envelope values cannot be empty.");
            return new BuildEvidenceEnvelope(certificate, payload, signature, QualificationJson.Sha256(json));
        }

        private static byte[] Base64(JObject root, string field, int maximumBytes)
        {
            var value = QualificationJson.RequiredString(root, field,
                (maximumBytes * 4 / 3) + 8, "Build evidence envelope");
            try
            {
                var result = Convert.FromBase64String(value);
                if (result.Length > maximumBytes)
                    throw new QualificationManifestException("build_evidence_base64_bounds", field + " exceeds its decoded size limit.");
                if (!string.Equals(Convert.ToBase64String(result), value, StringComparison.Ordinal))
                    throw new QualificationManifestException("build_evidence_base64", field + " is not canonical base64.");
                return result;
            }
            catch (FormatException ex)
            {
                throw new QualificationManifestException("build_evidence_base64", field + " is not canonical base64.", ex);
            }
        }
    }

    internal sealed class BuildEvidenceRunRequirement
    {
        internal BuildEvidenceRunRequirement(string packId, string host, string variant)
        {
            PackId = packId;
            Host = host;
            Variant = variant;
        }

        public string PackId { get; private set; }
        public string Host { get; private set; }
        public string Variant { get; private set; }
        public string Key { get { return PackId + "|" + Host + "|" + Variant; } }
    }

    internal static class QualificationReleaseMatrix
    {
        private static readonly IReadOnlyList<BuildEvidenceRunRequirement> Matrix = Build();

        public static IReadOnlyList<BuildEvidenceRunRequirement> RequiredRuns
        {
            get { return Matrix; }
        }

        private static IReadOnlyList<BuildEvidenceRunRequirement> Build()
        {
            var result = new List<BuildEvidenceRunRequirement>();
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
                result.Add(new BuildEvidenceRunRequirement("common.quick", host, "default"));
            result.Add(new BuildEvidenceRunRequirement("provider.live", "*", "default"));
            result.Add(new BuildEvidenceRunRequirement("storage.recovery", "*", "default"));
            result.Add(new BuildEvidenceRunRequirement("ui.webview", "*", "default"));
            result.Add(new BuildEvidenceRunRequirement("excel.wq0.identity", "Excel", "vsto"));
            result.Add(new BuildEvidenceRunRequirement("excel.wq0.identity", "Excel", "desktop-native"));
            result.Add(new BuildEvidenceRunRequirement("excel.read-write", "Excel", "default"));
            result.Add(new BuildEvidenceRunRequirement("excel.complex-task", "Excel", "default"));
            foreach (var host in new[] { "Excel", "Word", "PowerPoint" })
                result.Add(new BuildEvidenceRunRequirement("vba.lifecycle", host, "default"));
            foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
                result.Add(new BuildEvidenceRunRequirement("cross.full-run", host, "default"));
            return Array.AsReadOnly(result.ToArray());
        }
    }
}
