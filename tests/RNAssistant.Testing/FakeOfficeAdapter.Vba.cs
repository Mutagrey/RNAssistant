using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;

namespace RNAssistant.Harness
{
    internal enum FakeVbaOperation
    {
        ReadProject,
        ReadModule,
        ReplaceModule,
        CreateModule,
        RenameModule,
        DeleteModule,
        InstallPackage,
        RemovePackage,
        RunMacro
    }

    internal sealed class FakeVbaBackendCall
    {
        public FakeVbaOperation Operation { get; private set; }
        public object Request { get; private set; }

        public FakeVbaBackendCall(FakeVbaOperation operation, object request)
        {
            Operation = operation;
            Request = request;
        }
    }

    internal sealed partial class FakeOfficeAdapter
    {
        public readonly List<FakeVbaBackendCall> VbaBackendCalls =
            new List<FakeVbaBackendCall>();
        private readonly Dictionary<FakeVbaOperation, Queue<object>>
            _vbaScript = new Dictionary<FakeVbaOperation, Queue<object>>();

        public Action<FakeVbaBackendCall> BeforeVbaBackendCall { get; set; }
        public FakeVbaOperation? ThrowOnVbaOperation { get; set; }

        public int CountVbaCalls(FakeVbaOperation operation)
        {
            return VbaBackendCalls.Count(call => call.Operation == operation);
        }

        public FakeVbaBackendCall SingleVbaCall(FakeVbaOperation operation)
        {
            return VbaBackendCalls.Single(call =>
                call.Operation == operation);
        }

        public int CountVbaWholeModuleWriteCalls()
        {
            return CountVbaCalls(FakeVbaOperation.ReplaceModule) +
                CountVbaCalls(FakeVbaOperation.CreateModule);
        }

        public void QueueVbaProjectSnapshot(VbaProjectSnapshot snapshot)
        {
            QueueVbaScript(FakeVbaOperation.ReadProject, snapshot);
        }

        public void QueueVbaModuleSnapshot(VbaModuleSnapshot snapshot)
        {
            QueueVbaScript(FakeVbaOperation.ReadModule, snapshot);
        }

        public void QueueVbaActionResult(
            FakeVbaOperation operation, VbaBackendActionResult result)
        {
            if (operation == FakeVbaOperation.ReadProject ||
                operation == FakeVbaOperation.ReadModule)
                throw new ArgumentException(
                    "A mutation or macro operation is required.",
                    nameof(operation));
            QueueVbaScript(operation, result);
        }

        public void QueueVbaFailure(
            FakeVbaOperation operation,
            string message,
            string errorCode,
            bool retryable)
        {
            QueueVbaScript(operation, new VbaBackendException(
                message, errorCode, retryable));
        }

        public VbaProjectSnapshot ListProjectComponents()
        {
            var scripted = BeginVbaCall(FakeVbaOperation.ReadProject, null);
            if (scripted != null) return RequireScript<VbaProjectSnapshot>(
                scripted, FakeVbaOperation.ReadProject);
            return new VbaProjectSnapshot
            {
                Title = DocumentTitle,
                Modules = _vbaModules.Values.Select(module =>
                    new VbaProjectComponentSnapshot
                    {
                        Name = module.Name,
                        ComponentType = module.Type,
                        LineCount = LineCount(module.Code) +
                            VbaReportedLineCountOffset,
                        CodeOnlyUserForm = string.Equals(
                            module.Type, "MSForm",
                            StringComparison.OrdinalIgnoreCase)
                                ? (bool?)true : null,
                        HasToolManifest = string.Equals(
                            module.Type, "StdModule",
                            StringComparison.OrdinalIgnoreCase) &&
                            (module.Code ?? string.Empty).IndexOf(
                                "<RNAssistantTool>",
                                StringComparison.Ordinal) >= 0
                    }).ToArray()
            };
        }

        public VbaModuleSnapshot ReadModule(VbaReadModuleRequest request)
        {
            request = request ?? new VbaReadModuleRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.ReadModule, request);
            if (scripted != null) return RequireScript<VbaModuleSnapshot>(
                scripted, FakeVbaOperation.ReadModule);
            FakeVbaModule module;
            var moduleName = string.IsNullOrWhiteSpace(request.ModuleName)
                ? "Module1" : request.ModuleName;
            if (!_vbaModules.TryGetValue(moduleName, out module))
                throw new VbaBackendException(
                    "VBA module not found: " + moduleName,
                    "vba_module_not_found", true);
            var code = module.Code ?? string.Empty;
            var maxChars = request.MaxChars <= 0
                ? 30000 : request.MaxChars;
            var truncated = code.Length > maxChars;
            var returnedCode = truncated
                ? code.Substring(0, maxChars) + "\n...[truncated]"
                : code;
            return new VbaModuleSnapshot
            {
                Name = module.Name,
                ComponentType = module.Type,
                CodeOnlyUserForm = string.Equals(
                    module.Type, "MSForm",
                    StringComparison.OrdinalIgnoreCase)
                        ? (bool?)true : null,
                LineCount = LineCount(code) + VbaReportedLineCountOffset,
                Code = returnedCode,
                CodeSha256 = VbaTextCanonicalizer.LiveCodeSha256(code),
                Truncated = truncated
            };
        }

        public VbaBackendActionResult ReplaceModule(
            VbaReplaceModuleRequest request)
        {
            request = request ?? new VbaReplaceModuleRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.ReplaceModule, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.ReplaceModule);
            FakeVbaModule existing;
            var exists = _vbaModules.TryGetValue(
                request.ModuleName ?? string.Empty, out existing);
            if (!exists && !request.CreateIfMissing)
                return VbaBackendActionResult.Error(
                    "VBA module not found: " + request.ModuleName,
                    null, "vba_module_not_found", true);
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256) &&
                (!exists || !string.Equals(
                    request.ExpectedCodeSha256,
                    VbaTextCanonicalizer.LiveCodeSha256(existing.Code),
                    StringComparison.OrdinalIgnoreCase)))
                return VbaBackendActionResult.Error(
                    "stale VBA backend write", null,
                    "stale_vba_module", true);
            var code = ApplyVbaWriteTransform(request.Code);
            SetVbaModule(request.ModuleName, code,
                exists ? existing.Type : "StdModule");
            return VbaBackendActionResult.Ok(
                "fake VBA module replaced");
        }

        public VbaBackendActionResult CreateModule(VbaCreateModuleRequest request)
        {
            request = request ?? new VbaCreateModuleRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.CreateModule, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.CreateModule);
            if (_vbaModules.ContainsKey(request.ModuleName ?? string.Empty))
                return VbaBackendActionResult.Error(
                    "VBA module already exists: " + request.ModuleName,
                    null, "vba_module_exists", false);
            SetVbaModule(request.ModuleName,
                ApplyVbaWriteTransform(request.Code),
                request.ComponentType);
            return VbaBackendActionResult.Ok("fake VBA module created");
        }

        public VbaBackendActionResult RenameModule(VbaRenameModuleRequest request)
        {
            request = request ?? new VbaRenameModuleRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.RenameModule, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.RenameModule);
            FakeVbaModule existing;
            if (!_vbaModules.TryGetValue(
                request.ModuleName ?? string.Empty, out existing))
                return VbaBackendActionResult.Error(
                    "VBA module not found: " + request.ModuleName,
                    null, "vba_module_not_found", true);
            if (string.Equals(request.ModuleName, request.NewModuleName,
                StringComparison.OrdinalIgnoreCase))
                return VbaBackendActionResult.Error(
                    "The VBA rename destination is the current component name.",
                    null, "vba_rename_noop", true);
            if (_vbaModules.ContainsKey(request.NewModuleName ?? string.Empty))
                return VbaBackendActionResult.Error(
                    "VBA rename destination already exists: " +
                    request.NewModuleName,
                    null, "vba_module_exists", true);
            if (!string.IsNullOrWhiteSpace(request.ExpectedComponentType) &&
                !string.Equals(request.ExpectedComponentType, existing.Type,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(request.ExpectedCodeSha256) &&
                !string.Equals(request.ExpectedCodeSha256,
                    VbaTextCanonicalizer.LiveCodeSha256(existing.Code),
                    StringComparison.OrdinalIgnoreCase))
                return VbaBackendActionResult.Error(
                    "stale VBA backend rename", null,
                    "stale_vba_module", true);
            _vbaModules.Remove(request.ModuleName);
            existing.Name = request.NewModuleName;
            _vbaModules[request.NewModuleName] = existing;
            return VbaBackendActionResult.Ok(
                "fake VBA module renamed",
                new
                {
                    moduleName = request.ModuleName,
                    newModuleName = request.NewModuleName
                });
        }

        public VbaBackendActionResult DeleteModule(VbaDeleteModuleRequest request)
        {
            request = request ?? new VbaDeleteModuleRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.DeleteModule, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.DeleteModule);
            FakeVbaModule existing;
            var exists = _vbaModules.TryGetValue(
                request.ModuleName ?? string.Empty, out existing);
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256) &&
                (!exists || !string.Equals(request.ExpectedCodeSha256,
                    VbaTextCanonicalizer.LiveCodeSha256(existing.Code),
                    StringComparison.OrdinalIgnoreCase)))
                return VbaBackendActionResult.Error(
                    "stale VBA backend delete", null,
                    "stale_vba_module", true);
            _vbaModules.Remove(request.ModuleName ?? string.Empty);
            return VbaBackendActionResult.Ok("fake VBA module deleted");
        }

        public VbaBackendActionResult InstallPackage(
            VbaInstallPackageRequest request)
        {
            request = request ?? new VbaInstallPackageRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.InstallPackage, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.InstallPackage);
            var guardError = ValidatePackageInstallGuard(
                request.Components);
            if (guardError != null) return guardError;
            foreach (var component in request.Components ??
                new VbaInstallPackageComponent[0])
            {
                var markerLine = string.IsNullOrWhiteSpace(request.Marker)
                    ? string.Empty : "' " + request.Marker.Trim() + "\n";
                SetVbaModule(component.Name,
                    ApplyVbaWriteTransform(markerLine +
                        (component.Code ?? string.Empty)),
                    component.ComponentType);
            }
            return VbaBackendActionResult.Ok(
                "fake VBA package installed");
        }

        public VbaBackendActionResult RemovePackage(
            VbaRemovePackageRequest request)
        {
            request = request ?? new VbaRemovePackageRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.RemovePackage, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.RemovePackage);
            foreach (var item in request.ExpectedComparableHashes ??
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase))
            {
                FakeVbaModule module;
                if (!_vbaModules.TryGetValue(item.Key, out module))
                    continue;
                if (!string.Equals(PackageMarkerEvidence(module.Code),
                        request.ExpectedMarker,
                        StringComparison.OrdinalIgnoreCase))
                    return VbaBackendActionResult.Error(
                        "not owned", null,
                        "vba_component_not_owned", false);
                if (!string.Equals(
                        VbaTextCanonicalizer.PackageComparableCodeSha256(
                            module.Code),
                        item.Value,
                        StringComparison.OrdinalIgnoreCase))
                    return VbaBackendActionResult.Error(
                        "modified", null,
                        "vba_component_modified", false);
                _vbaModules.Remove(item.Key);
            }
            return VbaBackendActionResult.Ok(
                "fake VBA package removed");
        }

        public VbaBackendActionResult RunMacro(VbaRunMacroRequest request)
        {
            request = request ?? new VbaRunMacroRequest();
            var scripted = BeginVbaCall(FakeVbaOperation.RunMacro, request);
            if (scripted != null) return RequireScript<VbaBackendActionResult>(
                scripted, FakeVbaOperation.RunMacro);
            RanMacros.Add(request.MacroName ?? string.Empty);
            return VbaBackendActionResult.Ok(
                "ran " + request.MacroName,
                new { output = "fake-vba-result" });
        }

        private object BeginVbaCall(
            FakeVbaOperation operation, object request)
        {
            var call = new FakeVbaBackendCall(operation, request);
            VbaBackendCalls.Add(call);
            var before = BeforeVbaBackendCall;
            if (before != null) before(call);
            if (ThrowOnVbaOperation == operation)
            {
                ThrowOnVbaOperation = null;
                throw new InvalidOperationException(
                    "scripted VBA backend failure");
            }
            Queue<object> queue;
            if (!_vbaScript.TryGetValue(operation, out queue) ||
                queue.Count == 0) return null;
            var scripted = queue.Dequeue();
            var failure = scripted as VbaBackendException;
            if (failure != null) throw failure;
            return scripted;
        }

        private void QueueVbaScript(FakeVbaOperation operation, object value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            Queue<object> queue;
            if (!_vbaScript.TryGetValue(operation, out queue))
            {
                queue = new Queue<object>();
                _vbaScript.Add(operation, queue);
            }
            queue.Enqueue(value);
        }

        private static T RequireScript<T>(
            object value, FakeVbaOperation operation) where T : class
        {
            var typed = value as T;
            if (typed != null) return typed;
            throw new InvalidOperationException(
                "Invalid typed VBA script for " + operation + ".");
        }

        private string ApplyVbaWriteTransform(string code)
        {
            return VbaWriteTransform == null
                ? code ?? string.Empty
                : VbaWriteTransform(code ?? string.Empty);
        }

        private VbaBackendActionResult ValidatePackageInstallGuard(
            IReadOnlyList<VbaInstallPackageComponent> components)
        {
            var items = components ?? new VbaInstallPackageComponent[0];
            if (items.Any(item => item == null ||
                item.ExpectedBeforeExists == null ||
                item.ExpectedBeforeOwnershipMarkerPresent == null))
                return VbaBackendActionResult.Error(
                    "VBA package install guard is incomplete.", null,
                    "vba_package_guard_invalid", false);
            foreach (var item in items)
            {
                FakeVbaModule actual;
                var actualExists = _vbaModules.TryGetValue(
                    item.Name ?? string.Empty, out actual);
                if (actualExists != item.ExpectedBeforeExists.Value)
                    return VbaBackendActionResult.Error(
                        "stale VBA package install", null,
                        "stale_vba_package", false);
                if (!actualExists) continue;
                var actualMarker = PackageMarkerEvidence(actual.Code);
                if (!string.Equals(actual.Type,
                        item.ExpectedBeforeComponentType,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        VbaTextCanonicalizer.PackageComparableCodeSha256(
                            actual.Code),
                        item.ExpectedBeforeComparableCodeSha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.ExpectedBeforeOwnershipMarkerPresent.Value !=
                        !string.IsNullOrWhiteSpace(actualMarker) ||
                    item.ExpectedBeforeOwnershipMarkerPresent.Value &&
                    !string.Equals(actualMarker,
                        item.ExpectedBeforeOwnershipMarker,
                        StringComparison.OrdinalIgnoreCase))
                    return VbaBackendActionResult.Error(
                        "stale VBA package install", null,
                        "stale_vba_package", false);
            }
            return null;
        }

        private static string PackageMarkerEvidence(string code)
        {
            var lines = (code ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => (line ?? string.Empty).TrimStart())
                .Where(line => line.StartsWith(
                        "' RNAssistantPackage:",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("' RNAssistantSession:",
                        StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Substring(1).TrimStart())
                .ToArray();
            return lines.Length == 0 ? null : string.Join("\n", lines);
        }
    }
}
