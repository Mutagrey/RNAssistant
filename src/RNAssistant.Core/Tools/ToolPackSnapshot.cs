using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Core.Tools
{
    // Immutable run authority. Callable membership is a separate model-context
    // concern; every registration here pins the exact executable contract.
    public sealed class ToolPackSnapshot
    {
        private readonly ToolRegistration[] _registrations;
        private readonly IDictionary<string, ToolRegistration> _byId;

        public string PackId { get; private set; }
        public string Mode { get; private set; }
        public string Host { get; private set; }
        public string Revision { get; private set; }
        public IReadOnlyList<ToolRegistration> Registrations
        {
            get { return Array.AsReadOnly(_registrations); }
        }

        public ToolPackSnapshot(string packId, string mode, string host,
            IEnumerable<ToolRegistration> registrations)
        {
            if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("A pack id is required.", nameof(packId));
            if (mode != "agent" && mode != "plan" && mode != "chat")
                throw new ArgumentException("A supported conversation mode is required.", nameof(mode));
            var source = (registrations ?? throw new ArgumentNullException(nameof(registrations))).ToArray();
            if (source.Any(registration => registration == null))
                throw new ArgumentException("Tool registrations cannot contain null entries.", nameof(registrations));
            var collision = source.GroupBy(registration => registration.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (collision != null)
                throw new InvalidOperationException("Duplicate tool id in snapshot: " + collision.Key);

            _registrations = source
                .Select(CopyAndValidate)
                .OrderBy(registration => registration.Descriptor.Id, StringComparer.Ordinal)
                .ToArray();
            _byId = _registrations.ToDictionary(
                registration => registration.Descriptor.Id,
                StringComparer.Ordinal);
            PackId = packId;
            Mode = mode;
            Host = host ?? string.Empty;
            Revision = Hash(new JObject
            {
                ["packId"] = PackId,
                ["mode"] = Mode,
                ["host"] = Host,
                ["tools"] = new JArray(_registrations.Select(registration => new JObject
                {
                    ["id"] = registration.Descriptor.Id,
                    ["revision"] = registration.Revision
                }))
            }.ToString(Formatting.None));
        }

        public ToolRegistration Find(string exactToolId)
        {
            if (exactToolId == null) return null;
            ToolRegistration registration;
            return _byId.TryGetValue(exactToolId, out registration) ? registration : null;
        }

        public ToolPolicySnapshot Describe(string exactToolId)
        {
            var registration = Find(exactToolId);
            return registration == null ? null : new ToolPolicySnapshot(
                registration.Descriptor.Id,
                registration.Revision,
                registration.Policy);
        }

        public static ToolRegistration Capture(ToolDescriptor descriptor, ToolPolicy policy,
            ToolBinding binding, ToolPackageMetadata packageMetadata = null)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            return new ToolRegistration(descriptor, policy, binding,
                RegistrationRevision(descriptor, policy, binding, packageMetadata), packageMetadata);
        }

        public static string RegistrationRevision(ToolDescriptor descriptor, ToolPolicy policy,
            ToolBinding binding, ToolPackageMetadata packageMetadata = null)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            JToken parameters;
            try
            {
                parameters = Canonicalize(JToken.Parse(descriptor.ParametersJson));
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Tool parameters are not valid JSON: " + ex.Message, nameof(descriptor));
            }

            return Hash(new JObject
            {
                ["descriptor"] = new JObject
                {
                    ["id"] = descriptor.Id,
                    ["description"] = descriptor.Description,
                    ["parameters"] = parameters
                },
                ["policy"] = new JObject
                {
                    ["effect"] = policy.Effect.ToString().ToLowerInvariant(),
                    ["verification"] = policy.Verification.ToString().ToLowerInvariant(),
                    ["requiresConfirmation"] = policy.RequiresConfirmation,
                    ["independentLocalRead"] = policy.IndependentLocalRead,
                    ["allowedModes"] = new JArray(policy.AllowedModes),
                    ["riskLevel"] = policy.RiskLevel
                },
                ["binding"] = new JObject
                {
                    ["handlerId"] = binding.HandlerId,
                    ["entryPoint"] = Value(binding.EntryPoint),
                    ["scope"] = Value(binding.Scope),
                    ["host"] = Value(binding.Host)
                },
                ["package"] = PackageFingerprint(packageMetadata)
            }.ToString(Formatting.None));
        }

        private static ToolRegistration CopyAndValidate(ToolRegistration source)
        {
            var descriptor = new ToolDescriptor(
                source.Descriptor.Id,
                source.Descriptor.Description,
                source.Descriptor.ParametersJson);
            var policy = new ToolPolicy(
                source.Policy.Effect,
                source.Policy.Verification,
                source.Policy.RequiresConfirmation,
                source.Policy.IndependentLocalRead,
                source.Policy.AllowedModes,
                source.Policy.RiskLevel);
            var binding = new ToolBinding(
                source.Binding.HandlerId,
                source.Binding.EntryPoint,
                source.Binding.Scope,
                source.Binding.Host);
            var package = source.PackageMetadata == null ? null : new ToolPackageMetadata(
                source.PackageMetadata.Version,
                source.PackageMetadata.StoragePath,
                source.PackageMetadata.Source,
                source.PackageMetadata.ComponentsJson,
                source.PackageMetadata.InstallationStatus);
            var expected = RegistrationRevision(descriptor, policy, binding, package);
            if (!string.Equals(source.Revision, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Tool registration revision does not match its captured contract: " + descriptor.Id);
            return new ToolRegistration(descriptor, policy, binding, expected, package);
        }

        private static JToken PackageFingerprint(ToolPackageMetadata package)
        {
            if (package == null) return JValue.CreateNull();
            return new JObject
            {
                ["version"] = Value(package.Version),
                ["storagePath"] = Value(package.StoragePath),
                ["sourceSha256"] = Hash(package.Source ?? string.Empty),
                ["componentsSha256"] = Hash(package.ComponentsJson ?? string.Empty),
                ["installationStatus"] = Value(package.InstallationStatus)
            };
        }

        private static JToken Canonicalize(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                var sorted = new JObject();
                foreach (var property in value.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                    sorted[property.Name] = Canonicalize(property.Value);
                return sorted;
            }
            var array = token as JArray;
            return array == null ? token.DeepClone() : new JArray(array.Select(Canonicalize));
        }

        private static JToken Value(string value)
        {
            return value == null ? JValue.CreateNull() : new JValue(value);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
