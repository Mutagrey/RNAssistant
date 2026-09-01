using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    // Complete immutable package source captured at a catalog/run boundary.
    // Human package version and content revision are intentionally distinct.
    public sealed class ToolPackageSource
    {
        public const int CurrentContractVersion = 1;

        private readonly ToolPackageSourceComponent[] _components;

        public int ContractVersion { get { return CurrentContractVersion; } }
        public string Revision { get; private set; }
        public string Id { get; private set; }
        public string Host { get; private set; }
        public string Scope { get; private set; }
        public string PackageVersion { get; private set; }
        public string EntryPoint { get; private set; }
        public string ArgumentSchemaJson { get; private set; }
        public string Code { get; private set; }
        public string StoragePath { get; private set; }
        public string Readme { get; private set; }
        public IReadOnlyList<ToolPackageSourceComponent> Components
        {
            get { return Array.AsReadOnly(_components); }
        }

        public ToolPackageSource(string id, string host, string scope,
            string packageVersion, string entryPoint, string argumentSchemaJson,
            string code, string storagePath, string readme,
            IEnumerable<ToolPackageSourceComponent> components)
        {
            Id = id ?? string.Empty;
            Host = host ?? string.Empty;
            Scope = scope ?? string.Empty;
            PackageVersion = packageVersion ?? string.Empty;
            EntryPoint = entryPoint ?? string.Empty;
            ArgumentSchemaJson = CanonicalSchema(argumentSchemaJson);
            Code = code ?? string.Empty;
            StoragePath = storagePath;
            Readme = readme;
            _components = (components ?? new ToolPackageSourceComponent[0])
                .Where(component => component != null)
                .Select(component => new ToolPackageSourceComponent(
                    component.Name, component.Type, component.FileName,
                    component.Code))
                .ToArray();
            Revision = Hash(Fingerprint().ToString(Formatting.None));
        }

        public static ToolPackageSource Capture(ToolCatalogEntry definition)
        {
            if (definition == null) return null;
            return new ToolPackageSource(
                definition.Id,
                definition.Host,
                definition.Scope,
                definition.PackageVersion,
                definition.EntryPoint,
                definition.ArgumentSchemaJson,
                definition.Code,
                definition.StoragePath,
                definition.Readme,
                (definition.Components ?? new List<ToolPackageComponentDefinition>())
                    .Where(component => component != null)
                    .Select(component => new ToolPackageSourceComponent(
                        component.Name, component.Type, component.FileName,
                        component.Code)));
        }

        public static ToolPackageSource Capture(ToolRegistration registration)
        {
            if (registration == null) return null;
            var metadata = registration.PackageMetadata;
            if (metadata == null)
                throw new InvalidOperationException(
                    "A custom package registration must contain package metadata.");
            IReadOnlyList<ToolPackageSourceComponent> components;
            try
            {
                components = JsonConvert.DeserializeObject<List<ToolPackageSourceComponent>>(
                    string.IsNullOrWhiteSpace(metadata.ComponentsJson)
                        ? "[]" : metadata.ComponentsJson,
                    new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None,
                        MissingMemberHandling = MissingMemberHandling.Ignore
                    }) ?? new List<ToolPackageSourceComponent>();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "Captured package component metadata is invalid: " + ex.Message,
                    ex);
            }
            return new ToolPackageSource(
                registration.Descriptor.Id,
                registration.Binding.Host,
                registration.Binding.Scope,
                metadata.Version,
                registration.Binding.EntryPoint,
                registration.Descriptor.ParametersJson,
                metadata.Source,
                metadata.StoragePath,
                metadata.Readme,
                components);
        }

        private JObject Fingerprint()
        {
            return new JObject
            {
                ["contractVersion"] = CurrentContractVersion,
                ["id"] = Id,
                ["host"] = Host,
                ["scope"] = Scope,
                ["packageVersion"] = PackageVersion,
                ["entryPoint"] = EntryPoint,
                ["argumentSchema"] = JToken.Parse(ArgumentSchemaJson),
                ["code"] = Code,
                ["storagePath"] = StoragePath == null
                    ? JValue.CreateNull() : new JValue(StoragePath),
                ["readme"] = Readme == null
                    ? JValue.CreateNull() : new JValue(Readme),
                ["components"] = new JArray(_components.Select(component =>
                    new JObject
                    {
                        ["name"] = component.Name,
                        ["type"] = component.Type,
                        ["fileName"] = component.FileName == null
                            ? JValue.CreateNull() : new JValue(component.FileName),
                        ["code"] = component.Code
                    }))
            };
        }

        private static string CanonicalSchema(string schemaJson)
        {
            var source = string.IsNullOrWhiteSpace(schemaJson)
                ? "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"
                : schemaJson;
            try
            {
                return Canonicalize(JToken.Parse(source)).ToString(Formatting.None);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException(
                    "Package argument schema is invalid: " + ex.Message,
                    nameof(schemaJson));
            }
        }

        private static JToken Canonicalize(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                var sorted = new JObject();
                foreach (var property in value.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    sorted[property.Name] = Canonicalize(property.Value);
                }
                return sorted;
            }
            var array = token as JArray;
            return array == null
                ? token.DeepClone()
                : new JArray(array.Select(Canonicalize));
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }

    public sealed class ToolPackageSourceComponent
    {
        public string Name { get; private set; }
        public string Type { get; private set; }
        public string FileName { get; private set; }
        public string Code { get; private set; }

        [JsonConstructor]
        public ToolPackageSourceComponent(string name, string type,
            string fileName, string code)
        {
            Name = name ?? string.Empty;
            Type = type ?? string.Empty;
            FileName = fileName;
            Code = code ?? string.Empty;
        }
    }
}
