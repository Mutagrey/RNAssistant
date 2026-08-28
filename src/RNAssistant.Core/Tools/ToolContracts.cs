using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Tools
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolEffect { Read, Write, External, Unclassified }
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolVerification { None, Tool }

    public sealed class ToolDescriptor
    {
        public string Id { get; private set; }
        public string Description { get; private set; }
        public string ParametersJson { get; private set; }

        public ToolDescriptor(string id, string description, string parametersJson)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace))
                throw new ArgumentException("An exact tool id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(parametersJson))
                throw new ArgumentException("An argument schema is required.", nameof(parametersJson));
            Id = id;
            Description = description ?? string.Empty;
            ParametersJson = parametersJson;
        }
    }

    // Local execution authority, never model-authored schema metadata.
    public sealed class ToolPolicy
    {
        [JsonProperty(Required = Required.Always)]
        public ToolEffect Effect { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public ToolVerification Verification { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public bool RequiresConfirmation { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public bool IndependentLocalRead { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public IReadOnlyList<string> AllowedModes { get; private set; }
        [JsonProperty(Required = Required.Always)]
        public int RiskLevel { get; private set; }
        [JsonIgnore]
        public bool MayHaveSideEffects { get { return Effect != ToolEffect.Read; } }

        [JsonConstructor]
        public ToolPolicy(ToolEffect effect, ToolVerification verification, bool requiresConfirmation,
            bool independentLocalRead, IEnumerable<string> allowedModes, int riskLevel = 0)
        {
            if (!Enum.IsDefined(typeof(ToolEffect), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (!Enum.IsDefined(typeof(ToolVerification), verification)) throw new ArgumentOutOfRangeException(nameof(verification));
            if (riskLevel < 0) throw new ArgumentOutOfRangeException(nameof(riskLevel));
            if (independentLocalRead && (effect != ToolEffect.Read || requiresConfirmation))
                throw new ArgumentException("Only an unconfirmed read can be an independent local read.");
            var modes = (allowedModes ?? throw new ArgumentNullException(nameof(allowedModes))).ToArray();
            if (modes.Length == 0 || modes.Any(mode => mode != "agent" && mode != "plan" && mode != "chat"))
                throw new ArgumentException("Explicit supported conversation modes are required.", nameof(allowedModes));
            Effect = effect;
            Verification = verification;
            RequiresConfirmation = requiresConfirmation;
            IndependentLocalRead = independentLocalRead;
            AllowedModes = Array.AsReadOnly(modes.Distinct(StringComparer.Ordinal).OrderBy(mode => mode, StringComparer.Ordinal).ToArray());
            RiskLevel = riskLevel;
        }

        public bool Matches(ToolPolicy other)
        {
            return other != null && Effect == other.Effect && Verification == other.Verification &&
                RequiresConfirmation == other.RequiresConfirmation && IndependentLocalRead == other.IndependentLocalRead &&
                RiskLevel == other.RiskLevel && AllowedModes.SequenceEqual(other.AllowedModes, StringComparer.Ordinal);
        }
    }

    public sealed class ToolBinding
    {
        public string HandlerId { get; private set; }
        public string EntryPoint { get; private set; }

        public ToolBinding(string handlerId, string entryPoint = null)
        {
            if (string.IsNullOrWhiteSpace(handlerId)) throw new ArgumentException("Handler identity is required.", nameof(handlerId));
            HandlerId = handlerId;
            EntryPoint = entryPoint;
        }
    }

    // Opaque package data remains separate from the descriptor and runtime state.
    public sealed class ToolPackageMetadata
    {
        public string Version { get; private set; }
        public string StoragePath { get; private set; }
        public string Source { get; private set; }
        public string ComponentsJson { get; private set; }
        public string InstallationStatus { get; private set; }

        public ToolPackageMetadata(string version = null, string storagePath = null, string source = null,
            string componentsJson = null, string installationStatus = null)
        {
            Version = version;
            StoragePath = storagePath;
            Source = source;
            ComponentsJson = componentsJson;
            InstallationStatus = installationStatus;
        }
    }

    public sealed class ToolRegistration
    {
        public ToolDescriptor Descriptor { get; private set; }
        public ToolPolicy Policy { get; private set; }
        public ToolBinding Binding { get; private set; }
        public string Revision { get; private set; }
        public ToolPackageMetadata PackageMetadata { get; private set; }

        public ToolRegistration(ToolDescriptor descriptor, ToolPolicy policy, ToolBinding binding,
            string revision, ToolPackageMetadata packageMetadata = null)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("A captured contract revision is required.", nameof(revision));
            Revision = revision;
            PackageMetadata = packageMetadata;
        }
    }
}
