using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Domains.Vba
{
    public interface IVbaHostBackendProvider
    {
        IVbaHostBackend VbaHostBackend { get; }
    }

    public interface IVbaHostBackend
    {
        string HostName { get; }
        string DocumentKey { get; }
        string RuntimeDocumentKey { get; }
        string DocumentTitle { get; }

        VbaProjectSnapshot ListProjectComponents();
        VbaModuleSnapshot ReadModule(VbaReadModuleRequest request);
        VbaBackendActionResult ReplaceModule(VbaReplaceModuleRequest request);
        VbaBackendActionResult CreateModule(VbaCreateModuleRequest request);
        VbaBackendActionResult RenameModule(VbaRenameModuleRequest request);
        VbaBackendActionResult DeleteModule(VbaDeleteModuleRequest request);
        VbaBackendActionResult InstallPackage(VbaInstallPackageRequest request);
        VbaBackendActionResult RemovePackage(VbaRemovePackageRequest request);
        VbaBackendActionResult RunMacro(VbaRunMacroRequest request);
    }

    public sealed class VbaProjectSnapshot
    {
        public string Title { get; set; }
        public IReadOnlyList<VbaProjectComponentSnapshot> Modules { get; set; }
    }

    public sealed class VbaProjectComponentSnapshot
    {
        public string Name { get; set; }
        public string ComponentType { get; set; }
        public int LineCount { get; set; }
        public bool? CodeOnlyUserForm { get; set; }
        public bool? HasToolManifest { get; set; }
    }

    public sealed class VbaModuleSnapshot
    {
        public string Name { get; set; }
        public string ComponentType { get; set; }
        public bool? CodeOnlyUserForm { get; set; }
        public int LineCount { get; set; }
        public string Code { get; set; }
        public string CodeSha256 { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class VbaReadModuleRequest
    {
        public string ModuleName { get; set; }
        public int MaxChars { get; set; }
    }

    public sealed class VbaReplaceModuleRequest
    {
        public string ModuleName { get; set; }
        public string Code { get; set; }
        public bool CreateIfMissing { get; set; }
        public string ExpectedCodeSha256 { get; set; }
    }

    public sealed class VbaCreateModuleRequest
    {
        public string ModuleName { get; set; }
        public string ComponentType { get; set; }
        public string Code { get; set; }
    }

    public sealed class VbaRenameModuleRequest
    {
        public string ModuleName { get; set; }
        public string NewModuleName { get; set; }
        public string ExpectedCodeSha256 { get; set; }
        public string ExpectedComponentType { get; set; }
    }

    public sealed class VbaDeleteModuleRequest
    {
        public string ModuleName { get; set; }
        public string ExpectedCodeSha256 { get; set; }
    }

    public sealed class VbaInstallPackageRequest
    {
        public IReadOnlyList<VbaInstallPackageComponent> Components { get; set; }
        public string Marker { get; set; }
    }

    public sealed class VbaInstallPackageComponent
    {
        public string Name { get; set; }
        public string ComponentType { get; set; }
        public string Code { get; set; }
        public bool? ExpectedBeforeExists { get; set; }
        public string ExpectedBeforeComponentType { get; set; }
        public string ExpectedBeforeComparableCodeSha256 { get; set; }
        public bool? ExpectedBeforeOwnershipMarkerPresent { get; set; }
        public string ExpectedBeforeOwnershipMarker { get; set; }
    }

    public sealed class VbaRemovePackageRequest
    {
        public IReadOnlyDictionary<string, string> ExpectedComparableHashes { get; set; }
        public string ExpectedMarker { get; set; }
    }

    public sealed class VbaRunMacroRequest
    {
        public string MacroName { get; set; }
        public IReadOnlyList<object> Arguments { get; set; }
    }

    public enum VbaBackendActionStatus
    {
        Ok,
        Error,
        Unknown
    }

    public sealed class VbaBackendActionResult
    {
        private readonly JObject _data;

        public VbaBackendActionStatus Status { get; private set; }
        public bool Success { get { return Status == VbaBackendActionStatus.Ok; } }
        public string Message { get; private set; }
        public JObject Data
        {
            get { return _data == null ? null : (JObject)_data.DeepClone(); }
        }
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        private VbaBackendActionResult(
            VbaBackendActionStatus status,
            string message,
            object data,
            string errorCode,
            bool retryable)
        {
            Status = status;
            Message = message ?? string.Empty;
            _data = NormalizeData(data);
            ErrorCode = errorCode;
            Retryable = status == VbaBackendActionStatus.Error && retryable;
        }

        public static VbaBackendActionResult Ok(
            string message, object data = null)
        {
            return new VbaBackendActionResult(
                VbaBackendActionStatus.Ok, message, data, null, false);
        }

        public static VbaBackendActionResult Error(
            string message,
            object data = null,
            string errorCode = null,
            bool retryable = false)
        {
            return new VbaBackendActionResult(
                VbaBackendActionStatus.Error,
                message,
                data,
                string.IsNullOrWhiteSpace(errorCode)
                    ? "vba_backend_failed" : errorCode,
                retryable);
        }

        public static VbaBackendActionResult Unknown(
            string message,
            object data = null,
            string errorCode = "vba_backend_unknown")
        {
            return new VbaBackendActionResult(
                VbaBackendActionStatus.Unknown,
                message,
                data,
                string.IsNullOrWhiteSpace(errorCode)
                    ? "vba_backend_unknown" : errorCode,
                false);
        }

        private static JObject NormalizeData(object data)
        {
            if (data == null) return null;
            var jsonObject = data as JObject;
            if (jsonObject != null) return (JObject)jsonObject.DeepClone();
            var json = data as string;
            if (json != null)
            {
                return string.IsNullOrWhiteSpace(json)
                    ? null : JObject.Parse(json);
            }
            return JObject.FromObject(data);
        }
    }

    public sealed class VbaBackendException : InvalidOperationException
    {
        private readonly JObject _data;

        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }
        public JObject Details
        {
            get { return _data == null ? null : (JObject)_data.DeepClone(); }
        }

        public VbaBackendException(
            string message,
            string errorCode,
            bool retryable,
            JObject data = null,
            Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "vba_backend_failed" : errorCode;
            Retryable = retryable;
            _data = data == null ? null : (JObject)data.DeepClone();
        }
    }
}
