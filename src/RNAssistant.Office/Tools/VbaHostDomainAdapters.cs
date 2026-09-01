using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed class VbaDispatchBoundary
    {
        private readonly object _sync = new object();
        private Action _mark;

        internal IDisposable Bind(Action mark)
        {
            if (mark == null) return EmptyScope.Instance;
            lock (_sync)
            {
                if (_mark != null)
                    throw new InvalidOperationException("A VBA dispatch boundary is already active.");
                _mark = mark;
            }
            return new Scope(this, mark);
        }

        internal void Mark()
        {
            Action mark;
            lock (_sync) { mark = _mark; }
            if (mark != null) mark();
        }

        private void Release(Action mark)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_mark, mark))
                    throw new InvalidOperationException("VBA dispatch boundary ownership changed.");
                _mark = null;
            }
        }

        private sealed class Scope : IDisposable
        {
            private VbaDispatchBoundary _owner;
            private readonly Action _mark;

            internal Scope(VbaDispatchBoundary owner, Action mark)
            {
                _owner = owner;
                _mark = mark;
            }

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner.Release(_mark);
            }
        }

        private sealed class EmptyScope : IDisposable
        {
            internal static readonly EmptyScope Instance = new EmptyScope();
            public void Dispose() { }
        }
    }

    internal sealed class VbaMutationHostDocumentContext :
        IVbaMutationDocumentContext
    {
        private readonly IVbaHostBackend _backend;

        public VbaMutationHostDocumentContext(IVbaHostBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public string HostName { get { return _backend.HostName; } }
        public string DocumentKey { get { return _backend.DocumentKey; } }
        public string RuntimeDocumentKey { get { return _backend.RuntimeDocumentKey; } }
        public string DocumentTitle { get { return _backend.DocumentTitle; } }
    }

    internal sealed class VbaMutationHostBackend : IVbaMutationBackend
    {
        private readonly IVbaHostBackend _backend;
        private readonly VbaDispatchBoundary _dispatch;

        public VbaMutationHostBackend(IVbaHostBackend backend,
            VbaDispatchBoundary dispatch = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _dispatch = dispatch;
        }

        public VbaMutationActionResult ReplaceModule(VbaModuleWriteRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_dispatch != null) _dispatch.Mark();
            return Map(_backend.ReplaceModule(new VbaReplaceModuleRequest
            {
                ModuleName = request.ModuleName,
                Code = request.Code,
                CreateIfMissing = request.CreateIfMissing,
                ExpectedCodeSha256 = request.ExpectedCodeSha256
            }), "VBA module write returned no result.",
                "vba_write_missing_result");
        }

        public VbaMutationActionResult CreateModule(VbaModuleCreateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_dispatch != null) _dispatch.Mark();
            return Map(_backend.CreateModule(new VbaCreateModuleRequest
            {
                ModuleName = request.ModuleName,
                ComponentType = request.ComponentType,
                Code = request.Code
            }), "VBA module create returned no result.",
                "vba_write_failed");
        }

        public VbaMutationActionResult RenameModule(VbaRenameBackendRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_dispatch != null) _dispatch.Mark();
            return Map(_backend.RenameModule(new VbaRenameModuleRequest
            {
                ModuleName = request.ModuleName,
                NewModuleName = request.NewModuleName,
                ExpectedCodeSha256 = request.ExpectedCodeSha256,
                ExpectedComponentType = request.ExpectedComponentType
            }), "VBA rename returned no result.",
                "vba_rename_missing_result");
        }

        public VbaMutationActionResult DeleteModule(VbaModuleDeleteRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_dispatch != null) _dispatch.Mark();
            return Map(_backend.DeleteModule(
                new RNAssistant.Office.Domains.Vba.VbaDeleteModuleRequest
            {
                ModuleName = request.ModuleName,
                ExpectedCodeSha256 = request.ExpectedCodeSha256
            }), "VBA delete returned no result.",
                "vba_delete_failed");
        }

        public VbaMutationActionResult RestoreModule(VbaRestoreBackendRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (_dispatch != null) _dispatch.Mark();
            var result = request.ModuleExists
                ? _backend.ReplaceModule(new VbaReplaceModuleRequest
                {
                    ModuleName = request.ModuleName,
                    Code = request.Code,
                    CreateIfMissing = false,
                    ExpectedCodeSha256 = request.ExpectedCodeSha256
                })
                : _backend.CreateModule(new VbaCreateModuleRequest
                {
                    ModuleName = request.ModuleName,
                    ComponentType = request.ComponentType,
                    Code = request.Code
                });
            return Map(result, "VBA restore write returned no result.",
                "vba_restore_failed");
        }

        internal static VbaMutationActionResult Map(
            VbaBackendActionResult result,
            string missingMessage,
            string missingCode)
        {
            if (result == null)
                return VbaMutationActionResult.Error(
                    missingMessage, null, missingCode, false);
            if (result.Status == VbaBackendActionStatus.Ok)
                return VbaMutationActionResult.Succeeded(
                    result.Message, result.Data);
            if (result.Status == VbaBackendActionStatus.Unknown)
                return VbaMutationActionResult.Unknown(
                    result.Message,
                    result.Data,
                    string.IsNullOrWhiteSpace(result.ErrorCode)
                        ? "vba_backend_unknown" : result.ErrorCode);
            return VbaMutationActionResult.Error(
                result.Message,
                result.Data,
                result.ErrorCode,
                result.Retryable);
        }
    }

    internal sealed class VbaMutationHostReader : IVbaMutationReader
    {
        private readonly VbaReader _reader;

        public VbaMutationHostReader(VbaReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public VbaMutationReadResult ReadModule(string moduleName, int maxChars)
        {
            VbaModuleState state;
            ToolRunResult error;
            if (_reader.TryReadModule(moduleName, maxChars, out state, out error))
                return VbaMutationReadResult.Found(state);
            if (error == null)
                return VbaMutationReadResult.Failure(
                    "VBA module read returned no result.",
                    "vba_read_missing_result",
                    true,
                    null,
                    false);
            return VbaMutationReadResult.Failure(
                error.Message,
                error.ErrorCode,
                error.Retryable,
                VbaMutationData.Parse(error.DataJson),
                VbaReader.IsModuleNotFound(error));
        }
    }

    internal sealed class VbaPackageHostBackend : IVbaPackageBackend
    {
        private readonly IVbaHostBackend _backend;

        public VbaPackageHostBackend(IVbaHostBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public VbaMutationActionResult InstallPackage(
            VbaPackageInstallActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var components = (request.Components ?? new VbaPackageComponent[0])
                .Where(component => component != null)
                .ToList();
            var expected = (request.ExpectedBefore ??
                new VbaPackageExpectedComponentState[0])
                .Where(component => component != null &&
                    !string.IsNullOrWhiteSpace(component.Name))
                .ToDictionary(
                    component => component.Name,
                    StringComparer.OrdinalIgnoreCase);
            if (components.Count == 0 || expected.Count != components.Count ||
                components.Any(component =>
                    !expected.ContainsKey(component.Name)))
            {
                return VbaMutationActionResult.Error(
                    "VBA package install requires one prepared before-state guard per component.",
                    null,
                    "vba_package_guard_invalid",
                    false);
            }
            return VbaMutationHostBackend.Map(
                _backend.InstallPackage(new VbaInstallPackageRequest
                {
                    Components = components.Select(component =>
                    {
                        VbaPackageExpectedComponentState before;
                        expected.TryGetValue(component.Name, out before);
                        return new VbaInstallPackageComponent
                        {
                            Name = component.Name,
                            ComponentType = component.Type,
                            Code = component.Code,
                            ExpectedBeforeExists = before == null
                                ? (bool?)null : before.Exists,
                            ExpectedBeforeComponentType = before == null
                                ? null : before.ComponentType,
                            ExpectedBeforeComparableCodeSha256 = before == null
                                ? null : before.ComparableCodeSha256,
                            ExpectedBeforeOwnershipMarkerPresent = before == null
                                ? (bool?)null : before.OwnershipMarkerPresent,
                            ExpectedBeforeOwnershipMarker = before == null
                                ? null : before.OwnershipMarker
                        };
                    }).ToArray(),
                    Marker = request.Marker
                }),
                "VBA package installation returned no result.",
                "vba_package_install_failed");
        }

        public VbaMutationActionResult RemovePackage(
            VbaPackageRemoveActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return VbaMutationHostBackend.Map(
                _backend.RemovePackage(new VbaRemovePackageRequest
                {
                    ExpectedComparableHashes =
                        request.ExpectedComparableHashes ??
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase),
                    ExpectedMarker = request.ExpectedMarker
                }),
                "VBA package removal returned no result.",
                "vba_package_remove_failed");
        }

        public VbaMutationActionResult RunMacro(VbaPackageRunActionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return VbaMutationHostBackend.Map(
                _backend.RunMacro(new VbaRunMacroRequest
                {
                    MacroName = request.MacroName,
                    Arguments = ToArguments(request.Arguments)
                }),
                "VBA function returned no result.",
                "vba_function_failed");
        }

        internal static IReadOnlyList<object> ToArguments(
            Newtonsoft.Json.Linq.JArray arguments)
        {
            return (arguments ?? new Newtonsoft.Json.Linq.JArray())
                .Select(item =>
                    item.Type == Newtonsoft.Json.Linq.JTokenType.Integer
                        ? (object)(int)item
                        : item.Type == Newtonsoft.Json.Linq.JTokenType.Float
                            ? (double)item
                            : item.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
                                ? (bool)item
                                : (object)((string)item ?? string.Empty))
                .ToArray();
        }
    }
}
