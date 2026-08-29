using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Vba
{
    internal enum VbaMutationOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum VbaMutationActionStatus
    {
        Succeeded,
        Verified,
        Error,
        Unknown
    }

    internal enum VbaMutationDisposition
    {
        None,
        RolledBack
    }

    internal sealed class VbaMutationOutcome
    {
        private readonly JObject _data;

        public VbaMutationOutcomeStatus Status { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public bool? Retryable { get; private set; }
        public JObject Data { get { return _data == null ? null : (JObject)_data.DeepClone(); } }

        private VbaMutationOutcome(
            VbaMutationOutcomeStatus status,
            string message,
            JObject data,
            string errorCode,
            bool? retryable)
        {
            Status = status;
            Message = message ?? string.Empty;
            _data = data == null ? null : (JObject)data.DeepClone();
            ErrorCode = errorCode;
            Retryable = status == VbaMutationOutcomeStatus.Unknown ? false : retryable;
        }

        public static VbaMutationOutcome Ok(
            string message,
            JObject data = null)
        {
            return new VbaMutationOutcome(
                VbaMutationOutcomeStatus.Ok,
                message,
                data,
                null,
                null);
        }

        public static VbaMutationOutcome Error(
            string message,
            JObject data = null,
            string errorCode = null,
            bool? retryable = null)
        {
            return new VbaMutationOutcome(
                VbaMutationOutcomeStatus.Error,
                message,
                data,
                errorCode,
                retryable);
        }

        public static VbaMutationOutcome Unknown(
            string message,
            JObject data = null,
            string errorCode = "vba_mutation_unknown")
        {
            return new VbaMutationOutcome(
                VbaMutationOutcomeStatus.Unknown,
                message,
                data,
                errorCode,
                false);
        }
    }

    internal sealed class VbaMutationActionResult
    {
        private readonly JObject _data;

        public VbaMutationActionStatus Status { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public bool? Retryable { get; private set; }
        public VbaMutationDisposition Disposition { get; private set; }
        public JObject Data { get { return _data == null ? null : (JObject)_data.DeepClone(); } }

        private VbaMutationActionResult(
            VbaMutationActionStatus status,
            string message,
            JObject data,
            string errorCode,
            bool? retryable,
            VbaMutationDisposition disposition)
        {
            Status = status;
            Message = message ?? string.Empty;
            _data = data == null ? null : (JObject)data.DeepClone();
            ErrorCode = errorCode;
            Retryable = status == VbaMutationActionStatus.Unknown ? false : retryable;
            Disposition = disposition;
        }

        public static VbaMutationActionResult Succeeded(string message, JObject data = null)
        {
            return new VbaMutationActionResult(
                VbaMutationActionStatus.Succeeded,
                message,
                data,
                null,
                null,
                VbaMutationDisposition.None);
        }

        public static VbaMutationActionResult Verified(string message, JObject data = null)
        {
            return new VbaMutationActionResult(
                VbaMutationActionStatus.Verified,
                message,
                data,
                null,
                null,
                VbaMutationDisposition.None);
        }

        public static VbaMutationActionResult Error(
            string message,
            JObject data = null,
            string errorCode = null,
            bool? retryable = null,
            VbaMutationDisposition disposition = VbaMutationDisposition.None)
        {
            return new VbaMutationActionResult(
                VbaMutationActionStatus.Error,
                message,
                data,
                errorCode,
                retryable,
                disposition);
        }

        public static VbaMutationActionResult Unknown(
            string message,
            JObject data = null,
            string errorCode = "vba_mutation_unknown")
        {
            return new VbaMutationActionResult(
                VbaMutationActionStatus.Unknown,
                message,
                data,
                errorCode,
                false,
                VbaMutationDisposition.None);
        }
    }

    internal sealed class VbaMutationCorrelation
    {
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public string TurnId { get; set; }
        public string StepId { get; set; }
        public string ToolCallId { get; set; }
    }

    internal sealed class VbaPatchOperationRequest
    {
        public string Operation { get; set; }
        public string Find { get; set; }
        public string Text { get; set; }
    }

    internal sealed class VbaApplyPatchGuardRequest
    {
        public string RequestedModuleName { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaApplyPatchGuardPreparation
    {
        public VbaMutationGuard Guard { get; set; }
        public string ResolvedModuleName { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Guard != null; } }
    }

    internal sealed class VbaApplyPatchRequest
    {
        public string RequestedModuleName { get; set; }
        public IReadOnlyList<VbaPatchOperationRequest> Operations { get; set; }
        public bool DryRun { get; set; }
        public VbaMutationGuard Guard { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaModuleMutationRequest
    {
        public string Operation { get; set; }
        public string ModuleName { get; set; }
        public VbaModuleState Before { get; set; }
        public bool IntendedAfterExists { get; set; }
        public string IntendedAfterCode { get; set; }
        public string IntendedComponentType { get; set; }
        public VbaMutationCorrelation Correlation { get; set; }
    }

    internal sealed class VbaMutationPreparationResult
    {
        public VbaMutationPreparation Preparation { get; set; }
        public VbaMutationOutcome Error { get; set; }
        public bool Success { get { return Error == null && Preparation != null; } }
    }

    internal sealed class VbaModuleWriteRequest
    {
        public string ModuleName { get; set; }
        public string Code { get; set; }
        public bool CreateIfMissing { get; set; }
        public string ExpectedCodeSha256 { get; set; }
    }

    internal sealed class VbaMutationReadResult
    {
        private readonly JObject _data;

        public bool Success { get; private set; }
        public VbaModuleState Module { get; private set; }
        public bool IsNotFound { get; private set; }
        public string Message { get; private set; }
        public string ErrorCode { get; private set; }
        public bool? Retryable { get; private set; }
        public JObject Data { get { return _data == null ? null : (JObject)_data.DeepClone(); } }

        private VbaMutationReadResult(
            bool success,
            VbaModuleState module,
            bool isNotFound,
            string message,
            string errorCode,
            bool? retryable,
            JObject data)
        {
            Success = success;
            Module = module;
            IsNotFound = isNotFound;
            Message = message ?? string.Empty;
            ErrorCode = errorCode;
            Retryable = retryable;
            _data = data == null ? null : (JObject)data.DeepClone();
        }

        public static VbaMutationReadResult Found(VbaModuleState module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            return new VbaMutationReadResult(true, module, false, string.Empty, null, null, null);
        }

        public static VbaMutationReadResult Failure(
            string message,
            string errorCode,
            bool? retryable,
            JObject data,
            bool isNotFound)
        {
            return new VbaMutationReadResult(
                false,
                null,
                isNotFound,
                message,
                errorCode,
                retryable,
                data);
        }
    }

    internal interface IVbaMutationDocumentContext
    {
        string HostName { get; }
        string DocumentKey { get; }
        string RuntimeDocumentKey { get; }
        string DocumentTitle { get; }
    }

    internal interface IVbaMutationBackend
    {
        VbaMutationActionResult ReplaceModule(VbaModuleWriteRequest request);
    }

    internal interface IVbaMutationReader
    {
        VbaMutationReadResult ReadModule(string moduleName, int maxChars);
    }

    internal static class VbaMutationData
    {
        public static JObject Parse(string dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return new JObject();
            try
            {
                return JObject.Parse(dataJson);
            }
            catch (JsonException)
            {
                return new JObject { ["operationData"] = dataJson };
            }
        }

        public static JObject Clone(JObject data)
        {
            return data == null ? new JObject() : (JObject)data.DeepClone();
        }

        public static JArray Operations(
            IEnumerable<Tuple<string, bool, string>> operations)
        {
            return new JArray((operations ?? Enumerable.Empty<Tuple<string, bool, string>>()).Select(item =>
                new JObject
                {
                    ["op"] = item.Item1,
                    ["changed"] = item.Item2,
                    ["message"] = item.Item3
                }));
        }
    }
}
