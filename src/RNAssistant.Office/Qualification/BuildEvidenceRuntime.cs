using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Qualification
{
    public sealed class BuildIdentitySnapshot
    {
        public BuildIdentitySnapshot(string productVersion, string informationalVersion, string commitSha,
            string buildUtc, string branch, string channel, string workingTreeState, string configuration,
            string platform, string signerSha256)
        {
            ProductVersion = Value(productVersion);
            InformationalVersion = Value(informationalVersion);
            CommitSha = Value(commitSha);
            BuildUtc = Value(buildUtc);
            Branch = Value(branch);
            Channel = Value(channel);
            WorkingTreeState = Value(workingTreeState);
            Configuration = Value(configuration);
            Platform = Value(platform);
            SignerSha256 = Value(signerSha256);
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
        public string SignerSha256 { get; private set; }

        public static BuildIdentitySnapshot FromAssembly(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .Cast<AssemblyMetadataAttribute>())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || metadata.ContainsKey(item.Key)) continue;
                metadata.Add(item.Key, item.Value);
            }
            var information = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .Cast<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            return new BuildIdentitySnapshot(
                Get(metadata, "ProductVersion"),
                information == null ? null : information.InformationalVersion,
                Get(metadata, "CommitSha"),
                Get(metadata, "BuildUtc"),
                Get(metadata, "Branch"),
                Get(metadata, "Channel"),
                Get(metadata, "WorkingTreeState"),
                Get(metadata, "Configuration"),
                Get(metadata, "RuntimePlatform"),
                Get(metadata, "BuildEvidenceSignerSha256"));
        }

        private static string Get(IDictionary<string, string> values, string key)
        {
            string result;
            return values.TryGetValue(key, out result) ? result : "unknown";
        }

        private static string Value(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }
    }

    public interface IBuildEvidenceSignatureVerifier
    {
        bool Verify(byte[] payload, byte[] signature, byte[] certificateDer, out string error);
    }

    public sealed class RsaBuildEvidenceSignatureVerifier : IBuildEvidenceSignatureVerifier
    {
        public bool Verify(byte[] payload, byte[] signature, byte[] certificateDer, out string error)
        {
            try
            {
                using (var certificate = new X509Certificate2(certificateDer))
                using (var rsa = certificate.GetRSAPublicKey())
                {
                    if (rsa == null)
                    {
                        error = "Build evidence certificate has no RSA public key.";
                        return false;
                    }
                    var result = rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);
                    error = result ? null : "Build evidence signature is invalid.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                error = Bound(ex.Message, 500);
                return false;
            }
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum) return value;
            return value.Substring(0, maximum);
        }
    }

    public sealed class BuildEvidenceEvaluation
    {
        internal BuildEvidenceEvaluation(string status, BuildIdentitySnapshot identity,
            BuildEvidenceManifest manifest, string envelopeSha256, string catalogSha256,
            IEnumerable<string> issues, int requiredRunCount, int passedRunCount)
        {
            Status = status;
            Identity = identity;
            Manifest = manifest;
            EnvelopeSha256 = string.IsNullOrWhiteSpace(envelopeSha256) ? "unavailable" : envelopeSha256;
            CatalogSha256 = catalogSha256;
            Issues = Array.AsReadOnly((issues ?? new string[0]).Take(64)
                .Select(item => Bound(item, 500)).ToArray());
            RequiredRunCount = requiredRunCount;
            PassedRunCount = passedRunCount;
        }

        public string Status { get; private set; }
        public BuildIdentitySnapshot Identity { get; private set; }
        internal BuildEvidenceManifest Manifest { get; private set; }
        public string EnvelopeSha256 { get; private set; }
        public string CatalogSha256 { get; private set; }
        public IReadOnlyList<string> Issues { get; private set; }
        public int RequiredRunCount { get; private set; }
        public int PassedRunCount { get; private set; }
        public bool Compatible { get { return Status == "complete" || Status == "incomplete"; } }
        public bool Complete { get { return Status == "complete" && Issues.Count == 0; } }

        public string ActualJson()
        {
            return new JObject
            {
                ["status"] = Status,
                ["manifestSha256"] = EnvelopeSha256,
                ["catalogSha256"] = CatalogSha256,
                ["productVersion"] = Identity.ProductVersion,
                ["informationalVersion"] = Identity.InformationalVersion,
                ["commitSha"] = Identity.CommitSha,
                ["buildUtc"] = Identity.BuildUtc,
                ["configuration"] = Identity.Configuration,
                ["platform"] = Identity.Platform,
                ["workingTreeState"] = Identity.WorkingTreeState,
                ["requiredRuns"] = RequiredRunCount,
                ["passedRuns"] = PassedRunCount,
                ["issues"] = new JArray(Issues)
            }.ToString(Formatting.None);
        }

        public static string ExpectedJson()
        {
            return new JObject
            {
                ["status"] = "complete",
                ["configuration"] = "Release",
                ["platform"] = "x64",
                ["workingTreeState"] = "clean",
                ["hostNeutralHarness"] = "passed",
                ["requiredRuns"] = QualificationReleaseMatrix.RequiredRuns.Count,
                ["fileHashes"] = "verified",
                ["signature"] = "verified"
            }.ToString(Formatting.None);
        }

        private static string Bound(string value, int maximum)
        {
            value = string.IsNullOrWhiteSpace(value) ? "unspecified build evidence issue" : value.Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }

    public static class BuildEvidenceRuntime
    {
        public const string FileName = "RNAssistant.BuildEvidence.v1.json";
        public const string Capability = "qualification.build-evidence.v1";

        public static BuildEvidenceEvaluation Load(QualificationPackCatalog catalog, Assembly assembly,
            string baseDirectory = null, IBuildEvidenceSignatureVerifier signatureVerifier = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var identity = BuildIdentitySnapshot.FromAssembly(assembly);
            var catalogSha = QualificationBuiltInCatalog.Fingerprint(assembly);
            var directory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory : Path.GetFullPath(baseDirectory);
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
                return Result("missing", identity, null, null, catalogSha,
                    new[] { "Signed build evidence sidecar is missing." }, 0);
            try
            {
                var bytes = ReadBounded(path, 2097152);
                var json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\ufeff')
                    throw new InvalidDataException("Build evidence envelope must use UTF-8 without BOM.");
                var envelope = BuildEvidenceEnvelope.Parse(json);
                return EvaluateEnvelope(envelope, identity, catalog, catalogSha, directory,
                    assembly.Location, signatureVerifier ?? new RsaBuildEvidenceSignatureVerifier());
            }
            catch (Exception ex)
            {
                return Result("invalid", identity, null, null, catalogSha,
                    new[] { Bound(ex.Message, 500) }, 0);
            }
        }

        internal static BuildEvidenceEvaluation EvaluateEnvelope(BuildEvidenceEnvelope envelope,
            BuildIdentitySnapshot identity, QualificationPackCatalog catalog, string catalogSha256,
            string baseDirectory, string currentAssemblyPath, IBuildEvidenceSignatureVerifier signatureVerifier)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (signatureVerifier == null) throw new ArgumentNullException(nameof(signatureVerifier));
            var signer = Sha256(envelope.CertificateDer);
            if (!string.Equals(identity.SignerSha256, signer, StringComparison.Ordinal))
                return Result("invalid", identity, null, envelope.Sha256, catalogSha256,
                    new[] { "Build evidence signer does not match the signer pinned into this build." }, 0);
            string signatureError;
            if (!signatureVerifier.Verify(envelope.Payload, envelope.Signature,
                envelope.CertificateDer, out signatureError))
                return Result("invalid", identity, null, envelope.Sha256, catalogSha256,
                    new[] { signatureError ?? "Build evidence signature is invalid." }, 0);
            try
            {
                var manifest = BuildEvidenceManifest.Parse(envelope.Payload);
                return EvaluateVerified(manifest, envelope.Sha256, identity, catalog, catalogSha256,
                    baseDirectory, currentAssemblyPath);
            }
            catch (Exception ex)
            {
                return Result("invalid", identity, null, envelope.Sha256, catalogSha256,
                    new[] { Bound(ex.Message, 500) }, 0);
            }
        }

        internal static BuildEvidenceEvaluation EvaluateVerified(BuildEvidenceManifest manifest,
            string envelopeSha256, BuildIdentitySnapshot identity, QualificationPackCatalog catalog,
            string catalogSha256, string baseDirectory, string currentAssemblyPath)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var incompatible = new List<string>();
            var incomplete = new List<string>();
            Same(manifest.ProductVersion, identity.ProductVersion, "product version", incompatible);
            Same(manifest.InformationalVersion, identity.InformationalVersion, "informational version", incompatible);
            Same(manifest.CommitSha, identity.CommitSha, "commit SHA", incompatible);
            Same(manifest.BuildUtc, identity.BuildUtc, "build UTC", incompatible);
            Same(manifest.Branch, identity.Branch, "branch", incompatible);
            Same(manifest.Channel, identity.Channel, "channel", incompatible);
            Same(manifest.WorkingTreeState, identity.WorkingTreeState, "working tree state", incompatible);
            Same(manifest.Configuration, identity.Configuration, "configuration", incompatible);
            Same(manifest.Platform, identity.Platform, "runtime platform", incompatible);
            Same(manifest.CatalogSha256, catalogSha256, "qualification catalog SHA-256", incompatible);
            if (!string.Equals(manifest.WorkingTreeState, "clean", StringComparison.Ordinal))
                incomplete.Add("Release evidence requires a clean build tree.");
            if (!string.Equals(manifest.Configuration, "Release", StringComparison.Ordinal))
                incomplete.Add("Release evidence requires the Release configuration.");
            if (!string.Equals(manifest.Platform, "x64", StringComparison.OrdinalIgnoreCase))
                incomplete.Add("Release evidence requires the x64 runtime platform.");
            if (!string.Equals(manifest.Environment, "windows-x64-office-x64", StringComparison.Ordinal))
                incomplete.Add("Release evidence requires the Windows x64 + Office x64 environment.");

            ValidateFiles(manifest.Files, baseDirectory, currentAssemblyPath, incompatible);
            var harness = manifest.Checks.FirstOrDefault(item => item.Id == "host-neutral.harness");
            if (harness == null || harness.Outcome != "passed")
                incomplete.Add("Host-neutral harness evidence is missing or not passed.");
            if (manifest.Checks.Any(item => item.Outcome != "passed"))
                incomplete.Add("Complete build evidence cannot contain a non-passed check.");

            var runs = manifest.Runs.ToDictionary(item => item.Key, item => item, StringComparer.OrdinalIgnoreCase);
            if (runs.Count != QualificationReleaseMatrix.RequiredRuns.Count)
                incompatible.Add("Build evidence run matrix contains missing or unexpected records.");
            var passed = 0;
            foreach (var requirement in QualificationReleaseMatrix.RequiredRuns)
            {
                BuildEvidenceRunRecord run;
                if (!runs.TryGetValue(requirement.Key, out run))
                {
                    incomplete.Add("Required qualification run is missing: " + requirement.Key + ".");
                    continue;
                }
                QualificationPack pack;
                try { pack = catalog.Get(requirement.PackId); }
                catch (KeyNotFoundException)
                {
                    incompatible.Add("Required qualification pack is missing from this build: " + requirement.PackId + ".");
                    continue;
                }
                if (run.Outcome != "passed") incomplete.Add("Qualification run did not pass: " + requirement.Key + ".");
                else if (run.PackRevision != pack.Revision || run.PackSha256 != pack.ContentSha256)
                    incompatible.Add("Qualification run used a different pack revision: " + requirement.Key + ".");
                else passed++;
            }
            var issues = incompatible.Concat(incomplete).ToArray();
            var status = incompatible.Count != 0 ? "incompatible" : incomplete.Count != 0 ? "incomplete" : "complete";
            return new BuildEvidenceEvaluation(status, identity, manifest, envelopeSha256, catalogSha256,
                issues, QualificationReleaseMatrix.RequiredRuns.Count, passed);
        }

        private static BuildEvidenceEvaluation Result(string status, BuildIdentitySnapshot identity,
            BuildEvidenceManifest manifest, string envelopeSha, string catalogSha,
            IEnumerable<string> issues, int passed)
        {
            return new BuildEvidenceEvaluation(status, identity, manifest, envelopeSha, catalogSha,
                issues, QualificationReleaseMatrix.RequiredRuns.Count, passed);
        }

        private static void ValidateFiles(IEnumerable<BuildEvidenceFileRecord> files, string baseDirectory,
            string currentAssemblyPath, ICollection<string> issues)
        {
            var root = Path.GetFullPath(string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory : baseDirectory);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            var current = string.IsNullOrWhiteSpace(currentAssemblyPath) ? null : Path.GetFullPath(currentAssemblyPath);
            var currentSeen = false;
            foreach (var item in files)
            {
                var path = Path.GetFullPath(Path.Combine(root,
                    item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add("Build evidence file escapes the artifact root: " + item.Id + ".");
                    continue;
                }
                if (!File.Exists(path))
                {
                    issues.Add("Build evidence file is missing: " + item.Id + ".");
                    continue;
                }
                var info = new FileInfo(path);
                if (info.Length != item.ByteLength || !string.Equals(Sha256File(path), item.Sha256, StringComparison.Ordinal))
                    issues.Add("Build evidence file hash/length mismatch: " + item.Id + ".");
                if (current != null && string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) currentSeen = true;
            }
            if (current != null && !currentSeen)
                issues.Add("Build evidence does not include the current RNAssistant.Office assembly.");
        }

        private static byte[] ReadBounded(string path, int maximum)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > maximum)
                    throw new InvalidDataException("Build evidence envelope is empty or overlong.");
                var result = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < result.Length)
                {
                    var count = stream.Read(result, offset, result.Length - offset);
                    if (count <= 0) throw new EndOfStreamException("Build evidence envelope read was incomplete.");
                    offset += count;
                }
                return result;
            }
        }

        internal static string Sha256(byte[] value)
        {
            using (var algorithm = SHA256.Create()) return Hex(algorithm.ComputeHash(value));
        }

        internal static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var algorithm = SHA256.Create()) return Hex(algorithm.ComputeHash(stream));
        }

        private static string Hex(IEnumerable<byte> value)
        {
            var result = new StringBuilder();
            foreach (var item in value) result.Append(item.ToString("x2"));
            return result.ToString();
        }

        private static void Same(string actual, string expected, string field, ICollection<string> issues)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                issues.Add("Build evidence " + field + " does not match this binary.");
        }

        private static string Bound(string value, int maximum)
        {
            value = string.IsNullOrWhiteSpace(value) ? "Build evidence validation failed." : value.Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }
}
