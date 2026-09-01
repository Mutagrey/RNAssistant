using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.OfficeHosts.Vba
{
    public static partial class VbaProjectSupport
    {
        private const int StdModuleType = 1;
        private const int ClassModuleType = 2;
        private const int MsFormType = 3;

        public static object GetVbaProject(object documentObject)
        {
            if (documentObject == null)
            {
                throw new InvalidOperationException("No active document object.");
            }

            try
            {
                return documentObject.GetType().InvokeMember("VBProject", BindingFlags.GetProperty, null, documentObject, null);
            }
            catch (MissingMethodException)
            {
                dynamic target = documentObject;
                return target.VBProject;
            }
        }

        public static VbaProjectSnapshot ListProjectComponents(
            object documentObject, string title)
        {
            dynamic vbProject = GetVbaProject(documentObject);
            var modules = new List<VbaProjectComponentSnapshot>();
            foreach (dynamic component in vbProject.VBComponents)
            {
                var type = (int)component.Type;
                dynamic codeModule = component.CodeModule;
                var lineCount = (int)codeModule.CountOfLines;
                modules.Add(new VbaProjectComponentSnapshot
                {
                    Name = (string)component.Name,
                    ComponentType = ComponentTypeName(type),
                    LineCount = lineCount,
                    CodeOnlyUserForm = type == MsFormType
                        ? (bool?)IsCodeOnlyUserForm(component) : null,
                    HasToolManifest = type == StdModuleType &&
                        ContainsText(codeModule, "<RNAssistantTool>", lineCount)
                });
            }

            return new VbaProjectSnapshot
            {
                Title = title ?? string.Empty,
                Modules = modules
            };
        }

        private static bool ContainsText(dynamic codeModule, string text, int lineCount)
        {
            if (codeModule == null || lineCount <= 0 || string.IsNullOrEmpty(text)) return false;
            try
            {
                var startLine = 1;
                var startColumn = 1;
                var endLine = lineCount;
                var endColumn = -1;
                return (bool)codeModule.Find(
                    text,
                    ref startLine,
                    ref startColumn,
                    ref endLine,
                    ref endColumn,
                    false,
                    false,
                    false);
            }
            catch
            {
                // If the host does not expose Find, let discovery inspect this module normally.
                return true;
            }
        }

        public static VbaModuleSnapshot ReadModule(
            object documentObject, string moduleName, int maxChars)
        {
            dynamic component = FindComponent(GetVbaProject(documentObject), moduleName);
            if (component == null)
            {
                throw new VbaBackendException(
                    "VBA module not found: " + moduleName,
                    "vba_module_not_found",
                    true);
            }

            var componentType = (int)component.Type;
            var fullCode = ReadComponentCode(component);
            var code = Trim(fullCode, Math.Max(1, Math.Min(1000000, maxChars)));
            return new VbaModuleSnapshot
            {
                Name = (string)component.Name,
                ComponentType = ComponentTypeName(componentType),
                CodeOnlyUserForm = componentType == MsFormType
                    ? (bool?)IsCodeOnlyUserForm(component) : null,
                LineCount = (int)component.CodeModule.CountOfLines,
                Code = code,
                CodeSha256 = VbaTextCanonicalizer.LiveCodeSha256(fullCode),
                Truncated = !string.Equals(code, fullCode, StringComparison.Ordinal)
            };
        }

        public static VbaBackendActionResult ReplaceModule(
            object documentObject,
            string moduleName,
            string code,
            bool createIfMissing,
            string expectedCodeSha256 = null)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return VbaBackendActionResult.Error("No moduleName provided.");
            }
            code = code ?? string.Empty;
            string validationError;
            if (!TryValidateLiveCode(code, out validationError))
                return VbaBackendActionResult.Error(
                    validationError, null, "vba_code_invalid", true);

            dynamic vbProject = GetVbaProject(documentObject);
            dynamic component = FindComponent(vbProject, moduleName);
            if (component == null && !string.IsNullOrWhiteSpace(expectedCodeSha256))
            {
                return StaleLiveModule(moduleName, expectedCodeSha256, false, null, "write");
            }
            var created = false;
            dynamic module = null;
            var originalCode = string.Empty;
            var mutationStarted = false;
            try
            {
                if (component == null)
                {
                    if (!createIfMissing)
                    {
                        return VbaBackendActionResult.Error(
                            "VBA module not found: " + moduleName,
                            null,
                            "vba_module_not_found",
                            true);
                    }

                    component = vbProject.VBComponents.Add(StdModuleType);
                    created = true;
                    component.Name = moduleName;
                }

                module = component.CodeModule;
                originalCode = created ? string.Empty : ReadComponentCode(component);
                if (!created && !string.IsNullOrWhiteSpace(expectedCodeSha256))
                {
                    var actualHash = VbaTextCanonicalizer.LiveCodeSha256(originalCode);
                    if (!string.Equals(expectedCodeSha256, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return StaleLiveModule(moduleName, expectedCodeSha256, true, actualHash, "write");
                    }
                }
                mutationStarted = true;
                ReplaceCode(module, code);
                VerifyComponentLiveCode(component, code, "VBA module replacement");
            }
            catch (Exception ex)
            {
                Exception rollbackError = null;
                try
                {
                    if (created)
                    {
                        vbProject.VBComponents.Remove(component);
                        if (FindComponent(vbProject, moduleName) != null)
                        {
                            throw new InvalidOperationException("Incomplete VBA module is still present after rollback: " + moduleName);
                        }
                    }
                    else if (mutationStarted)
                    {
                        ReplaceCode(module, originalCode);
                        VerifyComponentLiveCode(component, originalCode, "VBA module rollback");
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackError = rollbackException;
                }

                if (rollbackError != null)
                {
                    throw new InvalidOperationException(
                        "VBA module replacement failed and the original code could not be restored: " + rollbackError.Message,
                        ex);
                }

                throw new InvalidOperationException(
                    created
                        ? "VBA module replacement failed; the incomplete module was removed."
                        : mutationStarted
                            ? "VBA module replacement failed; the original code was restored."
                            : "VBA module replacement failed before source mutation.",
                    ex);
            }
            return VbaBackendActionResult.Ok(
                "VBA module replaced: " + component.Name,
                new
            {
                moduleName = (string)component.Name,
                lineCount = (int)component.CodeModule.CountOfLines,
                codeSha256 = VbaTextCanonicalizer.LiveCodeSha256(ReadComponentCode(component))
            });
        }

        public static VbaBackendActionResult CreateModule(
            object documentObject,
            string moduleName,
            string componentType,
            string code)
        {
            if (!VbaToolManifestParser.ValidComponentName(moduleName))
                return VbaBackendActionResult.Error(
                    "Invalid VBA module name; use 1-31 ASCII letters, numbers, or underscore and start with a letter.",
                    null,
                    "vba_module_name_invalid",
                    false);
            code = code ?? string.Empty;
            string validationError;
            if (!TryValidateLiveCode(code, out validationError))
                return VbaBackendActionResult.Error(
                    validationError, null, "vba_code_invalid", true);

            var type = string.Equals(componentType, "ClassModule", StringComparison.OrdinalIgnoreCase) ? ClassModuleType :
                string.Equals(componentType, "StdModule", StringComparison.OrdinalIgnoreCase) ? StdModuleType :
                string.Equals(componentType, "MSForm", StringComparison.OrdinalIgnoreCase) || string.Equals(componentType, "UserForm", StringComparison.OrdinalIgnoreCase) ? MsFormType : 0;
            if (type == 0)
                return VbaBackendActionResult.Error(
                    "Only StdModule, ClassModule, and MSForm can be created.",
                    null,
                    "vba_component_type_read_only",
                    false);

            dynamic vbProject = GetVbaProject(documentObject);
            if (FindComponent(vbProject, moduleName) != null)
                return VbaBackendActionResult.Error(
                    "VBA module already exists: " + moduleName,
                    null,
                    "vba_module_exists",
                    false);
            dynamic component = null;
            try
            {
                component = vbProject.VBComponents.Add(type);
                component.Name = moduleName;
                ReplaceCode(component.CodeModule, code);
                VerifyComponentLiveCode(component, code, "VBA module creation");
                return VbaBackendActionResult.Ok(
                    "Inserted VBA module: " + moduleName,
                    new
                {
                    moduleName = (string)component.Name,
                    componentType = ComponentTypeName((int)component.Type),
                    lineCount = (int)component.CodeModule.CountOfLines,
                    codeSha256 = VbaTextCanonicalizer.LiveCodeSha256(ReadComponentCode(component))
                });
            }
            catch (Exception ex)
            {
                Exception cleanupError = null;
                if (component != null)
                {
                    try
                    {
                        vbProject.VBComponents.Remove(component);
                        if (FindComponent(vbProject, moduleName) != null)
                        {
                            throw new InvalidOperationException("Incomplete VBA module is still present after create rollback: " + moduleName);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        cleanupError = cleanupException;
                    }
                }
                if (cleanupError != null)
                {
                    throw new InvalidOperationException(
                        "VBA module creation failed and the incomplete component could not be removed: " + cleanupError.Message,
                        ex);
                }
                throw;
            }
        }

        public static VbaBackendActionResult RenameModule(
            object documentObject,
            string moduleName,
            string newModuleName,
            string expectedCodeSha256 = null,
            string expectedComponentType = null)
        {
            if (!VbaToolManifestParser.ValidComponentName(moduleName) ||
                !VbaToolManifestParser.ValidComponentName(newModuleName))
            {
                return VbaBackendActionResult.Error(
                    "Invalid VBA module name; use 1-31 ASCII letters, numbers, or underscore and start with a letter.",
                    null,
                    "vba_module_name_invalid",
                    false);
            }
            if (string.Equals(moduleName, newModuleName, StringComparison.OrdinalIgnoreCase))
            {
                return VbaBackendActionResult.Error(
                    "The VBA rename destination is the current component name.",
                    null,
                    "vba_rename_noop",
                    true);
            }

            dynamic vbProject = GetVbaProject(documentObject);
            dynamic component = FindComponent(vbProject, moduleName);
            if (component == null)
            {
                return string.IsNullOrWhiteSpace(expectedCodeSha256)
                    ? VbaBackendActionResult.Error(
                        "VBA module not found: " + moduleName,
                        null,
                        "vba_module_not_found",
                        true)
                    : StaleLiveModule(moduleName, expectedCodeSha256, false, null, "rename");
            }
            if (FindComponent(vbProject, newModuleName) != null)
            {
                return VbaBackendActionResult.Error(
                    "VBA rename destination already exists: " + newModuleName,
                    null,
                    "vba_module_exists",
                    true);
            }

            var type = (int)component.Type;
            var actualComponentType = ComponentTypeName(type);
            if (!string.IsNullOrWhiteSpace(expectedComponentType) &&
                !string.Equals(
                    expectedComponentType,
                    actualComponentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return VbaBackendActionResult.Error(
                    "The VBA component type changed before rename dispatch.",
                    new
                    {
                        moduleName = moduleName,
                        expectedComponentType = expectedComponentType,
                        actualComponentType = actualComponentType
                    },
                    "stale_vba_module",
                    true);
            }
            if (type != StdModuleType && type != ClassModuleType &&
                (type != MsFormType || !IsCodeOnlyUserForm(component)))
            {
                return VbaBackendActionResult.Error(
                    "Only StdModule, ClassModule, and blank code-only MSForm components can be renamed through RNAssistant.",
                    null,
                    "vba_component_type_read_only",
                    false);
            }

            var originalName = (string)component.Name;
            var originalCode = ReadComponentCode(component);
            var originalHash = VbaTextCanonicalizer.LiveCodeSha256(originalCode);
            if (!string.IsNullOrWhiteSpace(expectedCodeSha256) &&
                !string.Equals(expectedCodeSha256, originalHash, StringComparison.OrdinalIgnoreCase))
            {
                return StaleLiveModule(moduleName, expectedCodeSha256, true, originalHash, "rename");
            }

            var mutationStarted = false;
            try
            {
                mutationStarted = true;
                component.Name = newModuleName;
                dynamic renamed = FindComponent(vbProject, newModuleName);
                if (renamed == null || FindComponent(vbProject, originalName) != null ||
                    (int)renamed.Type != type ||
                    !string.Equals(
                        VbaTextCanonicalizer.VbeComparableCodeSha256(ReadComponentCode(renamed)),
                        VbaTextCanonicalizer.VbeComparableCodeSha256(originalCode),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("VBA rename read-back did not preserve the component identity, type, and source.");
                }

                return VbaBackendActionResult.Ok(
                    "VBA module renamed: " + originalName + " -> " + (string)renamed.Name,
                    new
                    {
                        previousModuleName = originalName,
                        moduleName = (string)renamed.Name,
                        componentType = actualComponentType,
                        lineCount = (int)renamed.CodeModule.CountOfLines,
                        codeSha256 = VbaTextCanonicalizer.LiveCodeSha256(ReadComponentCode(renamed))
                    });
            }
            catch (Exception ex)
            {
                Exception rollbackError = null;
                if (mutationStarted)
                {
                    try
                    {
                        component.Name = originalName;
                        dynamic restored = FindComponent(vbProject, originalName);
                        if (restored == null || FindComponent(vbProject, newModuleName) != null ||
                            (int)restored.Type != type ||
                            !string.Equals(
                                VbaTextCanonicalizer.VbeComparableCodeSha256(ReadComponentCode(restored)),
                                VbaTextCanonicalizer.VbeComparableCodeSha256(originalCode),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("The original VBA component could not be verified after rename rollback.");
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackError = rollbackException;
                    }
                }

                if (rollbackError != null)
                {
                    throw new InvalidOperationException(
                        "VBA module rename failed and the original name could not be restored: " + rollbackError.Message,
                        ex);
                }
                throw new InvalidOperationException(
                    "VBA module rename failed; the original name was restored.",
                    ex);
            }
        }

        public static VbaBackendActionResult DeleteModule(
            object documentObject,
            string moduleName,
            string expectedCodeSha256 = null)
        {
            dynamic vbProject = GetVbaProject(documentObject);
            dynamic component = FindComponent(vbProject, moduleName);
            if (component == null)
            {
                return string.IsNullOrWhiteSpace(expectedCodeSha256)
                    ? VbaBackendActionResult.Error(
                        "VBA module not found: " + moduleName,
                        null,
                        "vba_module_not_found",
                        true)
                    : StaleLiveModule(moduleName, expectedCodeSha256, false, null, "delete");
            }
            var type = (int)component.Type;
            if (type != StdModuleType && type != ClassModuleType)
                return VbaBackendActionResult.Error(
                    "Document modules and UserForms cannot be deleted through RNAssistant.",
                    null,
                    "vba_component_type_read_only",
                    false);
            if (!string.IsNullOrWhiteSpace(expectedCodeSha256))
            {
                var actualHash = VbaTextCanonicalizer.LiveCodeSha256(ReadComponentCode(component));
                if (!string.Equals(expectedCodeSha256, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    return StaleLiveModule(moduleName, expectedCodeSha256, true, actualHash, "delete");
                }
            }
            vbProject.VBComponents.Remove(component);
            if (FindComponent(vbProject, moduleName) != null)
            {
                return VbaBackendActionResult.Unknown(
                    "VBA module deletion returned success but the module is still present: " + moduleName,
                    new { moduleName = moduleName, type = ComponentTypeName(type) },
                    "vba_delete_verify_failed");
            }
            return VbaBackendActionResult.Ok(
                "VBA module deleted: " + moduleName,
                new { moduleName = moduleName, type = ComponentTypeName(type) });
        }

        private static VbaBackendActionResult StaleLiveModule(
            string moduleName,
            string expectedCodeSha256,
            bool actualExists,
            string actualCodeSha256,
            string operation)
        {
            return VbaBackendActionResult.Error(
                "VBA module changed immediately before the backend " + operation + ". The operation was not applied; re-read current code and rebuild the action.",
                new
                {
                    moduleName = moduleName ?? string.Empty,
                    expectedCodeSha256 = expectedCodeSha256,
                    actualExists = actualExists,
                    actualCodeSha256 = actualCodeSha256,
                    inspectTool = "common.resources_read",
                    resourceProvider = "vba",
                    resourceKind = "vba-component"
                },
                "stale_vba_module",
                true);
        }

        public static string RunStringFunction(
            object applicationObject,
            string macroName,
            IReadOnlyList<object> arguments)
        {
            if (applicationObject == null) throw new InvalidOperationException("Office application is not available.");
            var source = arguments ?? new object[0];
            if (source.Count > 30)
                throw new InvalidOperationException(
                    "VBA macro execution supports at most 30 positional arguments.");
            var invokeArguments = new object[source.Count + 1];
            invokeArguments[0] = macroName;
            for (var index = 0; index < source.Count; index++)
            {
                var item = source[index];
                invokeArguments[index + 1] = item ?? string.Empty;
            }
            var output = applicationObject.GetType().InvokeMember(
                "Run",
                BindingFlags.InvokeMethod,
                null,
                applicationObject,
                invokeArguments);
            return Convert.ToString(output);
        }

        public static VbaBackendActionResult InstallPackage(
            object documentObject,
            IReadOnlyList<VbaInstallPackageComponent> source,
            string marker)
        {
            var payload = (source ?? new VbaInstallPackageComponent[0])
                .Where(item => item != null)
                .ToList();
            var components = payload.Select(item => new ToolPackageComponentDefinition
            {
                Name = item.Name,
                Type = item.ComponentType,
                Code = item.Code ?? string.Empty
            }).ToList();
            if (components.Count == 0)
                return VbaBackendActionResult.Error(
                    "VBA package has no components.",
                    null,
                    "vba_package_empty",
                    false);
            if (components.Any(component => !VbaToolManifestParser.ValidComponentName(component.Name) ||
                (!string.Equals(component.Type, "StdModule", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase))))
            {
                return VbaBackendActionResult.Error(
                    "VBA package supports only valid StdModule, ClassModule, and code-only MSForm components.",
                    null,
                    "vba_component_invalid",
                    false);
            }
            var duplicate = components.GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                return VbaBackendActionResult.Error(
                    "VBA package contains a duplicate component: " + duplicate.Key,
                    null,
                    "vba_component_duplicate",
                    false);
            }
            foreach (var component in components)
            {
                string validationError;
                if (!TryValidateLiveCode(component.Code, out validationError))
                {
                    return VbaBackendActionResult.Error(
                        component.Name + ": " + validationError,
                        null,
                        "vba_code_invalid",
                        true);
                }
                if (string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase) &&
                    VbaToolManifestParser.ContainsUserFormDesignerExport(component.Code))
                {
                    return VbaBackendActionResult.Error(
                        "VBA package MSForm must contain code-behind only, not exported Designer/FRX metadata: " + component.Name,
                        null,
                        "vba_userform_designer_unsupported",
                        false);
                }
            }

            dynamic vbProject = GetVbaProject(documentObject);
            var guardError = ValidatePackageInstallGuards(vbProject, payload);
            if (guardError != null) return guardError;
            var ownerMarker = PackageOwnerMarker(marker);
            foreach (var component in components)
            {
                dynamic existing = FindComponent(vbProject, component.Name);
                if (existing == null) continue;
                var existingIsForm = (int)existing.Type == MsFormType;
                var intendedIsForm = string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase);
                if (existingIsForm != intendedIsForm || existingIsForm &&
                    (!IsCodeOnlyUserForm(existing) || string.IsNullOrWhiteSpace(ownerMarker) ||
                     ReadComponentCode(existing).IndexOf(ownerMarker, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    return VbaBackendActionResult.Error(
                        "VBA package cannot replace UserForm state unless the existing component is an owned blank code-only MSForm: " + component.Name,
                        null,
                        "vba_userform_designer_unsupported",
                        false);
                }
            }
            var tempDirectory = Path.Combine(Path.GetTempPath(), "RNAssistant-Vba-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var backups = new List<string>();
            var installedNames = new List<string>();
            var replacedForms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var component in components)
                {
                    dynamic existing = FindComponent(vbProject, component.Name);
                    if (string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase))
                    {
                        if (existing == null)
                        {
                            existing = vbProject.VBComponents.Add(MsFormType);
                            installedNames.Add((string)existing.Name);
                            existing.Name = component.Name;
                            installedNames[installedNames.Count - 1] = component.Name;
                        }
                        else
                        {
                            replacedForms[component.Name] = ReadComponentCode(existing);
                        }
                        ReplaceCode(existing.CodeModule, PrepareCodeOnlyFormSource(component, marker));
                        continue;
                    }
                    if (existing != null)
                    {
                        var backupPath = Path.Combine(tempDirectory, "backup_" + component.Name + ComponentExtension((int)existing.Type));
                        existing.Export(backupPath);
                        backups.Add(backupPath);
                        vbProject.VBComponents.Remove(existing);
                    }

                    var importPath = Path.Combine(tempDirectory, component.Name + (string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase) ? ".cls" : ".bas"));
                    File.WriteAllText(importPath, PrepareImportSource(component, marker), new UTF8Encoding(false));
                    dynamic imported = vbProject.VBComponents.Import(importPath);
                    var importedName = (string)imported.Name;
                    installedNames.Add(importedName);
                    if (!string.Equals(importedName, component.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        imported.Name = component.Name;
                        installedNames[installedNames.Count - 1] = component.Name;
                    }
                }
                foreach (var component in components)
                {
                    dynamic installed = FindComponent(vbProject, component.Name);
                    if (installed == null)
                    {
                        throw new InvalidOperationException("VBA package verification could not find component: " + component.Name);
                    }
                    var expectedType = string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase)
                        ? ClassModuleType
                        : string.Equals(component.Type, "MSForm", StringComparison.OrdinalIgnoreCase) ? MsFormType : StdModuleType;
                    if ((int)installed.Type != expectedType || expectedType == MsFormType && !IsCodeOnlyUserForm(installed))
                    {
                        throw new InvalidOperationException("VBA package verification found an unexpected component type or Designer state: " + component.Name);
                    }
                    VerifyComponentPackageCode(installed, component.Code, "VBA package installation");
                }
                return VbaBackendActionResult.Ok(
                    "VBA package installed.",
                    new
                {
                    components = components.Select(component => new
                    {
                        name = component.Name,
                        type = component.Type,
                        codeSha256 = VbaTextCanonicalizer.PackageCodeSha256(component.Code)
                    }).ToArray()
                });
            }
            catch (Exception ex)
            {
                Exception rollbackError = null;
                try
                {
                    foreach (var name in installedNames)
                    {
                        dynamic installed = FindComponent(vbProject, name);
                        if (installed != null) vbProject.VBComponents.Remove(installed);
                    }
                    foreach (var replaced in replacedForms)
                    {
                        dynamic form = FindComponent(vbProject, replaced.Key);
                        if (form == null)
                        {
                            form = vbProject.VBComponents.Add(MsFormType);
                            form.Name = replaced.Key;
                        }
                        ReplaceCode(form.CodeModule, replaced.Value);
                    }
                    foreach (var backup in backups) vbProject.VBComponents.Import(backup);
                }
                catch (Exception rollback) { rollbackError = rollback; }
                return VbaBackendActionResult.Error(
                    "VBA package installation failed" + (rollbackError == null ? ". " : " and rollback failed: " + rollbackError.Message + ". ") + ex.Message,
                    null,
                    rollbackError == null ? "vba_package_install_failed" : "vba_package_rollback_failed",
                    false);
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); }
                catch { }
            }
        }

        public static VbaBackendActionResult RemovePackage(
            object documentObject,
            IReadOnlyDictionary<string, string> expectedComponents,
            string expectedMarker)
        {
            var expected = expectedComponents ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dynamic vbProject = GetVbaProject(documentObject);
            foreach (var property in expected)
            {
                dynamic component = FindComponent(vbProject, property.Key);
                if (component == null) continue;
                if ((int)component.Type == MsFormType && !IsCodeOnlyUserForm(component))
                {
                    return VbaBackendActionResult.Error(
                        "VBA package cannot remove a UserForm with Designer controls or unverified Designer state: " + property.Key,
                        null,
                        "vba_userform_designer_unsupported",
                        false);
                }
                var code = ReadComponentCode(component);
                if (string.IsNullOrWhiteSpace(expectedMarker) || code.IndexOf(expectedMarker, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return VbaBackendActionResult.Error(
                        "VBA component is not owned by this RNAssistant package and was not removed: " + property.Key,
                        null,
                        "vba_component_not_owned",
                        false);
                }
                var actual = VbaTextCanonicalizer.PackageComparableCodeSha256(code);
                if (!string.Equals(actual, property.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return VbaBackendActionResult.Error(
                        "VBA component changed after installation and was not removed: " + property.Key,
                        new { component = property.Key, expected = property.Value, actual = actual },
                        "vba_component_modified",
                        false);
                }
            }
            var tempDirectory = Path.Combine(Path.GetTempPath(), "RNAssistant-Vba-Remove-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var backups = new List<string>();
            var formBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var removed = new List<string>();
            try
            {
                foreach (var property in expected)
                {
                    dynamic component = FindComponent(vbProject, property.Key);
                    if (component == null) continue;
                    if ((int)component.Type == MsFormType)
                    {
                        formBackups[property.Key] = ReadComponentCode(component);
                        continue;
                    }
                    var backupPath = Path.Combine(tempDirectory, "backup_" + property.Key + ComponentExtension((int)component.Type));
                    component.Export(backupPath);
                    backups.Add(backupPath);
                }
                foreach (var property in expected)
                {
                    dynamic component = FindComponent(vbProject, property.Key);
                    if (component == null) continue;
                    vbProject.VBComponents.Remove(component);
                    removed.Add(property.Key);
                }
                foreach (var property in expected)
                {
                    if (FindComponent(vbProject, property.Key) != null)
                    {
                        throw new InvalidOperationException(
                            "VBA package removal verification found component: " + property.Key);
                    }
                }
                return VbaBackendActionResult.Ok(
                    "VBA package components removed.",
                    new { components = removed });
            }
            catch (Exception ex)
            {
                Exception rollbackError = null;
                try
                {
                    foreach (var backup in backups)
                    {
                        var name = Path.GetFileNameWithoutExtension(backup).Substring("backup_".Length);
                        if (FindComponent(vbProject, name) == null) vbProject.VBComponents.Import(backup);
                    }
                    foreach (var formBackup in formBackups)
                    {
                        if (FindComponent(vbProject, formBackup.Key) != null) continue;
                        dynamic form = vbProject.VBComponents.Add(MsFormType);
                        form.Name = formBackup.Key;
                        ReplaceCode(form.CodeModule, formBackup.Value);
                    }
                }
                catch (Exception rollback) { rollbackError = rollback; }
                return VbaBackendActionResult.Error(
                    "VBA package removal failed" + (rollbackError == null ? ". " : " and rollback failed: " + rollbackError.Message + ". ") + ex.Message,
                    null,
                    rollbackError == null ? "vba_package_remove_failed" : "vba_package_rollback_failed",
                    false);
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); }
                catch { }
            }
        }

        private static string PrepareImportSource(ToolPackageComponentDefinition component, string marker)
        {
            var code = VbaTextCanonicalizer.NormalizePackageCode(component.Code);
            var markerLine = string.IsNullOrWhiteSpace(marker) ? string.Empty : "' " + marker.Trim() + Environment.NewLine;
            if (string.Equals(component.Type, "ClassModule", StringComparison.OrdinalIgnoreCase))
            {
                if (code.StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase)) return InsertMarkerAfterAttributes(code, markerLine);
                return "VERSION 1.0 CLASS\r\nBEGIN\r\n  MultiUse = -1\r\nEND\r\n" +
                    "Attribute VB_Name = \"" + component.Name + "\"\r\n" +
                    "Attribute VB_GlobalNameSpace = False\r\nAttribute VB_Creatable = False\r\n" +
                    "Attribute VB_PredeclaredId = False\r\nAttribute VB_Exposed = False\r\n" + markerLine + code;
            }
            if (code.StartsWith("Attribute VB_Name", StringComparison.OrdinalIgnoreCase)) return InsertMarkerAfterAttributes(code, markerLine);
            return "Attribute VB_Name = \"" + component.Name + "\"\r\n" + markerLine + code;
        }

        private static string PrepareCodeOnlyFormSource(ToolPackageComponentDefinition component, string marker)
        {
            var code = VbaTextCanonicalizer.NormalizePackageCode(component == null ? null : component.Code);
            var markerLine = string.IsNullOrWhiteSpace(marker) ? string.Empty : "' " + marker.Trim() + "\r\n";
            return markerLine + code;
        }

        private static string PackageOwnerMarker(string marker)
        {
            marker = (marker ?? string.Empty).Trim();
            var end = marker.IndexOf("; version=", StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = marker.IndexOf("; hash=", StringComparison.OrdinalIgnoreCase);
            return end < 0 ? marker : marker.Substring(0, end + 1);
        }

        private static void VerifyComponentLiveCode(object componentObject, string expectedCode, string operation)
        {
            if (componentObject == null)
            {
                throw new InvalidOperationException((operation ?? "VBA write") + " verification found no component.");
            }
            var expectedHash = VbaTextCanonicalizer.LiveCodeSha256(expectedCode);
            var actualCode = ReadComponentCode(componentObject);
            var actualHash = VbaTextCanonicalizer.LiveCodeSha256(actualCode);
            var expectedComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(expectedCode);
            var actualComparableHash = VbaTextCanonicalizer.VbeComparableCodeSha256(actualCode);
            if (!string.Equals(expectedComparableHash, actualComparableHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    (operation ?? "VBA write") + " verification failed: expected/actual live hashes " + expectedHash + "/" + actualHash +
                    ", VBE-comparable hashes " + expectedComparableHash + "/" + actualComparableHash + ".");
            }
        }

        private static void VerifyComponentPackageCode(object componentObject, string expectedCode, string operation)
        {
            if (componentObject == null)
            {
                throw new InvalidOperationException((operation ?? "VBA package write") + " verification found no component.");
            }
            var expectedHash = VbaTextCanonicalizer.PackageCodeSha256(expectedCode);
            var actualCode = ReadComponentCode(componentObject);
            var actualHash = VbaTextCanonicalizer.PackageCodeSha256(actualCode);
            var expectedComparableHash = VbaTextCanonicalizer.PackageComparableCodeSha256(expectedCode);
            var actualComparableHash = VbaTextCanonicalizer.PackageComparableCodeSha256(actualCode);
            if (!string.Equals(expectedComparableHash, actualComparableHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    (operation ?? "VBA package write") + " verification failed: expected/actual package hashes " + expectedHash + "/" + actualHash +
                    ", VBE-comparable package hashes " + expectedComparableHash + "/" + actualComparableHash + ".");
            }
        }

        private static string InsertMarkerAfterAttributes(string code, string marker)
        {
            if (string.IsNullOrEmpty(marker)) return code;
            var lines = code.Replace("\r\n", "\n").Split('\n').ToList();
            var index = 0;
            while (index < lines.Count &&
                (lines[index].StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase) ||
                 lines[index].StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                 lines[index].StartsWith("END", StringComparison.OrdinalIgnoreCase) ||
                 lines[index].TrimStart().StartsWith("MultiUse", StringComparison.OrdinalIgnoreCase) ||
                 lines[index].StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase))) index++;
            lines.Insert(index, marker.TrimEnd('\r', '\n'));
            return string.Join("\r\n", lines.ToArray());
        }

        private static string ComponentExtension(int type)
        {
            return type == 2 ? ".cls" : ".bas";
        }

        private static object FindComponent(object vbProjectObject, string moduleName)
        {
            if (vbProjectObject == null || string.IsNullOrWhiteSpace(moduleName))
            {
                return null;
            }

            dynamic vbProject = vbProjectObject;
            foreach (dynamic component in vbProject.VBComponents)
            {
                if (string.Equals((string)component.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return component;
                }
            }

            return null;
        }

        private static string ReadComponentCode(dynamic component)
        {
            dynamic module = component.CodeModule;
            return (int)module.CountOfLines <= 0 ? string.Empty : (string)module.Lines[1, (int)module.CountOfLines];
        }

        private static bool IsCodeOnlyUserForm(dynamic component)
        {
            if (component == null || (int)component.Type != MsFormType) return false;
            try
            {
                dynamic designer = component.Designer;
                if (designer == null || (int)designer.Controls.Count != 0) return false;
                object picture = designer.Picture;
                object mouseIcon = designer.MouseIcon;
                return picture == null && mouseIcon == null;
            }
            catch
            {
                return false;
            }
        }

        private static void ReplaceCode(dynamic module, string code)
        {
            code = PrepareLiveCodeForWrite(code);
            if ((int)module.CountOfLines > 0)
            {
                module.DeleteLines(1, (int)module.CountOfLines);
            }
            if (!string.IsNullOrEmpty(code))
            {
                module.InsertLines(1, code);
            }
        }

        private static string PrepareLiveCodeForWrite(string code)
        {
            string validationError;
            if (!TryValidateLiveCode(code ?? string.Empty, out validationError))
            {
                throw new InvalidOperationException(validationError);
            }
            var normalized = VbaTextCanonicalizer.NormalizeLiveCode(code ?? string.Empty);
            if (normalized.Length == 0) return string.Empty;
            return normalized.Replace("\n", "\r\n");
        }

        private static bool TryValidateLiveCode(string code, out string error)
        {
            code = code ?? string.Empty;
            for (var index = 0; index < code.Length; index++)
            {
                var value = code[index];
                if (value == '\uFEFF')
                {
                    error = "VBA code contains a BOM/zero-width no-break space at character " + index + ". Remove the hidden character before writing.";
                    return false;
                }
                if (value == '\u2028' || value == '\u2029')
                {
                    error = "VBA code contains an unsupported Unicode line separator at character " + index + ". Use CRLF or LF line endings.";
                    return false;
                }
                if (char.GetUnicodeCategory(value) == System.Globalization.UnicodeCategory.Format)
                {
                    error = "VBA code contains a hidden Unicode formatting character at character " + index + ".";
                    return false;
                }
                if (char.IsControl(value) && value != '\r' && value != '\n' && value != '\t')
                {
                    error = "VBA code contains raw control character U+" + ((int)value).ToString("X4") +
                        " at character " + index + ". Use a VBA expression such as ChrW$(" + ((int)value) +
                        ") instead of embedding the control character in source text.";
                    return false;
                }
            }
            int joinedLine;
            string joinedFragment;
            if (TryFindJoinedVbaBlock(code, out joinedLine, out joinedFragment))
            {
                error = "VBA code appears to join a block terminator and following code on line " + joinedLine +
                    " near '" + joinedFragment + "'. Insert a line break before writing.";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryFindJoinedVbaBlock(string code, out int lineNumber, out string fragment)
        {
            var terminators = new[] { "End Sub", "End Function", "End Property", "End Type", "End Enum" };
            var lines = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var executable = VbaCodeOutsideStringsAndComments(lines[lineIndex]);
                foreach (var terminator in terminators)
                {
                    var searchFrom = 0;
                    while (searchFrom < executable.Length)
                    {
                        var found = executable.IndexOf(terminator, searchFrom, StringComparison.OrdinalIgnoreCase);
                        if (found < 0) break;
                        var after = found + terminator.Length;
                        var remainder = executable.Substring(after).Trim();
                        if (remainder.Length > 0)
                        {
                            lineNumber = lineIndex + 1;
                            var sourceFragment = lines[lineIndex].Substring(found).Trim();
                            fragment = sourceFragment.Substring(0, Math.Min(80, sourceFragment.Length));
                            return true;
                        }
                        searchFrom = after;
                    }
                }
            }
            lineNumber = 0;
            fragment = string.Empty;
            return false;
        }

        private static string VbaCodeOutsideStringsAndComments(string line)
        {
            var source = line ?? string.Empty;
            var output = new StringBuilder(source.Length);
            var inString = false;
            for (var index = 0; index < source.Length; index++)
            {
                var value = source[index];
                if (!inString && value == '\'') break;
                if (!inString && IsVbaRemComment(source, index)) break;
                if (value == '"')
                {
                    output.Append(' ');
                    if (inString && index + 1 < source.Length && source[index + 1] == '"')
                    {
                        output.Append(' ');
                        index++;
                        continue;
                    }
                    inString = !inString;
                    continue;
                }
                output.Append(inString ? ' ' : value);
            }
            return output.ToString();
        }

        private static bool IsVbaRemComment(string source, int index)
        {
            if (index < 0 || index + 3 > (source ?? string.Empty).Length ||
                !string.Equals(source.Substring(index, 3), "Rem", StringComparison.OrdinalIgnoreCase)) return false;
            var validBefore = index == 0 || char.IsWhiteSpace(source[index - 1]) || source[index - 1] == ':';
            var after = index + 3;
            var validAfter = after >= source.Length || char.IsWhiteSpace(source[after]);
            return validBefore && validAfter;
        }

        private static string ComponentTypeName(int type)
        {
            switch (type)
            {
                case 1:
                    return "StdModule";
                case 2:
                    return "ClassModule";
                case 3:
                    return "MSForm";
                case 100:
                    return "Document";
                default:
                    return type.ToString();
            }
        }

        private static string Trim(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars) + "\n...[truncated]";
        }
    }
}
