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
        private readonly IVbaMutationDocumentContext _document;
        private readonly IVbaPackageJournal _journal;
        private readonly IVbaMutationReader _reader;
        private readonly IVbaPackageBackend _backend;

        internal VbaPackageService(
            IVbaMutationDocumentContext document,
            IVbaPackageJournal journal,
            IVbaMutationReader reader,
            IVbaPackageBackend backend)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public VbaPackagePreparationResult PreparePackage(VbaPackageSourceDefinition source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Code))
            {
                return PreparationError("VBA tool has no entry module code.", "vba_code_missing");
            }

            var parsed = new VbaToolManifestParser().Parse(source.Code);
            if (!parsed.Success)
            {
                return PreparationError(parsed.ErrorMessage, parsed.ErrorCode);
            }
            var manifest = parsed.Tool;
            if (!string.Equals(manifest.Id, source.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Host, _document.HostName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source.Host, _document.HostName, StringComparison.OrdinalIgnoreCase))
            {
                return PreparationError(
                    "VBA manifest id/host does not match the selected tool and active Office host.",
                    "vba_manifest_metadata_mismatch");
            }

            var supplied = new Dictionary<string, VbaPackageSourceComponent>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in source.Components ?? new VbaPackageSourceComponent[0])
            {
                if (component == null || string.IsNullOrWhiteSpace(component.Name)) continue;
                if (supplied.ContainsKey(component.Name))
                {
                    return PreparationError(
                        "VBA package contains a duplicate component: " + component.Name,
                        "vba_component_duplicate");
                }
                supplied.Add(component.Name, component);
            }

            var entryName = manifest.Components[0].Name;
            var resolved = new List<VbaPackageComponent>();
            foreach (var declared in manifest.Components)
            {
                VbaPackageSourceComponent suppliedComponent;
                supplied.TryGetValue(declared.Name, out suppliedComponent);
                var isEntry = string.Equals(declared.Name, entryName, StringComparison.OrdinalIgnoreCase);
                var code = isEntry ? source.Code : suppliedComponent == null ? string.Empty : suppliedComponent.Code;
                var type = isEntry ? "StdModule" : suppliedComponent == null ? string.Empty : suppliedComponent.Type;
                if (string.IsNullOrWhiteSpace(code) || !SupportedComponentType(type))
                {
                    return PreparationError(
                        "VBA package source/type is missing for component: " + declared.Name,
                        "vba_component_missing");
                }
                if (VbaPackageOwnershipMarker.ContainsReservedMarker(code))
                {
                    return PreparationError(
                        "VBA package source cannot contain an RNAssistant ownership marker: " + declared.Name + ".",
                        "vba_package_source_marker_reserved");
                }
                if (string.Equals(type, "MSForm", StringComparison.OrdinalIgnoreCase) &&
                    VbaToolManifestParser.ContainsUserFormDesignerExport(code))
                {
                    return PreparationError(
                        "VBA package MSForm must contain code-behind only, not exported Designer/FRX metadata: " + declared.Name,
                        "vba_userform_designer_unsupported");
                }
                resolved.Add(new VbaPackageComponent
                {
                    Name = declared.Name,
                    Type = type,
                    FileName = suppliedComponent == null ? declared.FileName : suppliedComponent.FileName,
                    Code = code,
                    CodeSha256 = VbaTextCanonicalizer.PackageCodeSha256(code)
                });
            }

            var unexpected = supplied.Keys.FirstOrDefault(name =>
                !resolved.Any(component => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(unexpected))
            {
                return PreparationError(
                    "VBA package contains an undeclared component: " + unexpected,
                    "vba_component_undeclared");
            }

            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(manifest, out schema, out schemaError))
            {
                return PreparationError(schemaError, "vba_argument_schema_invalid");
            }

            return new VbaPackagePreparationResult
            {
                Package = new VbaPackageDefinition
                {
                    Id = manifest.Id,
                    Host = manifest.Host,
                    Version = manifest.PackageVersion,
                    EntryPoint = manifest.EntryPoint,
                    ArgumentSchema = schema,
                    ArgumentOrder = new List<string>(manifest.ArgumentOrder ?? new List<string>()),
                    Components = resolved,
                    StoragePath = source.StoragePath,
                    Readme = source.Readme
                }
            };
        }
        private static bool SupportedComponentType(string type)
        {
            return string.Equals(type, "StdModule", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "ClassModule", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "MSForm", StringComparison.OrdinalIgnoreCase);
        }

        private static VbaPackagePreparationResult PreparationError(string message, string code)
        {
            return new VbaPackagePreparationResult
            {
                Error = VbaMutationOutcome.Error(message, null, code, false)
            };
        }

        private VbaMutationReadResult ReadPackageComponent(string moduleName)
        {
            try
            {
                return _reader.ReadModule(moduleName, 1000000);
            }
            catch (Exception ex)
            {
                return VbaMutationReadResult.Failure(
                    "VBA package component read failed: " + ex.Message,
                    "vba_package_component_read_exception",
                    false,
                    null,
                    false);
            }
        }
    }
}
