using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.ModelProtocol
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ModelReferenceKind { Execution, Resource, Observation, Request }

    // Prefix is a type discriminator, never authority. Persist the exact alias;
    // changing a future formatter cannot rename an already issued reference.
    public sealed class ModelReference
    {
        public ModelReferenceKind Kind { get; private set; }
        public string Alias { get; private set; }
        [JsonIgnore]
        public long Ordinal { get { return long.Parse(Alias.Substring(1), CultureInfo.InvariantCulture); } }

        [JsonConstructor]
        public ModelReference(ModelReferenceKind kind, string alias)
        {
            var prefix = Prefix(kind);
            long ordinal;
            if (alias == null || alias.Length < 2 || alias[0] != prefix || alias[1] == '0' ||
                !long.TryParse(alias.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal <= 0 ||
                alias != prefix + ordinal.ToString(CultureInfo.InvariantCulture))
                throw new ArgumentException("Invalid typed model reference.", nameof(alias));
            Kind = kind;
            Alias = alias;
        }

        public static ModelReference Create(ModelReferenceKind kind, long ordinal)
        { return new ModelReference(kind, Prefix(kind) + ordinal.ToString(CultureInfo.InvariantCulture)); }

        private static char Prefix(ModelReferenceKind kind)
        {
            switch (kind)
            {
                case ModelReferenceKind.Execution: return 'E';
                case ModelReferenceKind.Resource: return 'R';
                case ModelReferenceKind.Observation: return 'O';
                case ModelReferenceKind.Request: return 'Q';
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
