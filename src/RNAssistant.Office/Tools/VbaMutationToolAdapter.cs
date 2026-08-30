using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed class VbaMutationDocumentContextAdapter : IVbaMutationDocumentContext
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public VbaMutationDocumentContextAdapter(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public string HostName { get { return _adapter.HostName; } }
        public string DocumentKey { get { return _adapter.DocumentKey; } }
        public string RuntimeDocumentKey { get { return _adapter.RuntimeDocumentKey; } }
        public string DocumentTitle { get { return _adapter.DocumentTitle; } }
    }

    internal sealed class VbaMutationBackendAdapter : IVbaMutationBackend
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Func<string, string> _backendToolId;

        public VbaMutationBackendAdapter(
            IOfficeApplicationAdapter adapter,
            Func<string, string> backendToolId)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _backendToolId = backendToolId ?? throw new ArgumentNullException(nameof(backendToolId));
        }

        public VbaMutationActionResult ReplaceModule(VbaModuleWriteRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = new ToolCommand { ToolId = _backendToolId("vba_replace_module") };
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["code"] = request.Code;
            command.Arguments["createIfMissing"] = request.CreateIfMissing;
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256))
            {
                command.Arguments["expectedCodeSha256"] = request.ExpectedCodeSha256;
            }
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA module write returned no result.",
                "vba_write_missing_result");
        }

        public VbaMutationActionResult CreateModule(VbaModuleCreateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = new ToolCommand { ToolId = _backendToolId("vba_create_module_internal") };
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["componentType"] = request.ComponentType;
            command.Arguments["code"] = request.Code;
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA module create returned no result.",
                "vba_write_failed");
        }

        public VbaMutationActionResult RenameModule(VbaRenameBackendRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = new ToolCommand { ToolId = _backendToolId("vba_rename_module_internal") };
            command.Arguments["moduleName"] = request.ModuleName;
            command.Arguments["newModuleName"] = request.NewModuleName;
            command.Arguments["expectedCodeSha256"] = request.ExpectedCodeSha256;
            command.Arguments["expectedComponentType"] = request.ExpectedComponentType;
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA rename returned no result.",
                "vba_rename_missing_result");
        }

        public VbaMutationActionResult DeleteModule(VbaModuleDeleteRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var command = new ToolCommand { ToolId = _backendToolId("vba_delete_module_internal") };
            command.Arguments["moduleName"] = request.ModuleName;
            if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256))
            {
                command.Arguments["expectedCodeSha256"] = request.ExpectedCodeSha256;
            }
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA delete returned no result.",
                "vba_delete_failed");
        }

        public VbaMutationActionResult RestoreModule(VbaRestoreBackendRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ToolCommand command;
            if (request.ModuleExists)
            {
                command = new ToolCommand { ToolId = _backendToolId("vba_replace_module") };
                command.Arguments["moduleName"] = request.ModuleName;
                command.Arguments["code"] = request.Code;
                command.Arguments["createIfMissing"] = false;
                if (!string.IsNullOrWhiteSpace(request.ExpectedCodeSha256))
                {
                    command.Arguments["expectedCodeSha256"] = request.ExpectedCodeSha256;
                }
            }
            else
            {
                command = new ToolCommand { ToolId = _backendToolId("vba_create_module_internal") };
                command.Arguments["moduleName"] = request.ModuleName;
                command.Arguments["componentType"] = request.ComponentType;
                command.Arguments["code"] = request.Code;
            }
            return VbaMutationToolResultMapper.FromBackend(
                _adapter.ExecuteTool(command),
                "VBA restore write returned no result.",
                "vba_restore_failed");
        }
    }

    internal sealed class VbaMutationReaderAdapter : IVbaMutationReader
    {
        private readonly VbaReader _reader;

        public VbaMutationReaderAdapter(VbaReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public VbaMutationReadResult ReadModule(string moduleName, int maxChars)
        {
            VbaModuleState state;
            ToolResult error;
            if (_reader.TryReadModule(moduleName, maxChars, out state, out error))
            {
                return VbaMutationReadResult.Found(state);
            }
            if (error == null)
            {
                return VbaMutationReadResult.Failure(
                    "VBA module read returned no result.",
                    "vba_read_missing_result",
                    true,
                    null,
                    false);
            }
            return VbaMutationReadResult.Failure(
                error.Message,
                error.ErrorCode,
                error.Retryable,
                VbaMutationData.Parse(error.DataJson),
                VbaReader.IsModuleNotFound(error));
        }
    }

    internal static class VbaMutationToolResultMapper
    {
        public static VbaMutationActionResult FromBackend(
            ToolResult result,
            string missingMessage,
            string missingCode)
        {
            if (result == null)
            {
                return VbaMutationActionResult.Error(
                    missingMessage,
                    null,
                    missingCode,
                    false);
            }
            var data = VbaMutationData.Parse(result.DataJson);
            if (result.Success)
            {
                return VbaMutationActionResult.Succeeded(result.Message, data);
            }
            if (string.Equals(result.Status, "partial_failure", StringComparison.OrdinalIgnoreCase))
            {
                return VbaMutationActionResult.Unknown(
                    result.Message,
                    data,
                    string.IsNullOrWhiteSpace(result.ErrorCode) ? "vba_backend_unknown" : result.ErrorCode);
            }
            return VbaMutationActionResult.Error(
                result.Message,
                data,
                result.ErrorCode,
                result.Retryable);
        }

        public static ToolResult ToToolResult(VbaMutationOutcome outcome)
        {
            if (outcome == null)
            {
                return ToolResult.PartialFailure(
                    "VBA mutation returned no typed outcome.",
                    null,
                    "vba_mutation_missing_outcome");
            }
            var data = outcome.Data;
            var dataJson = data == null || !data.HasValues
                ? null
                : data.ToString(Formatting.None);
            if (outcome.Status == VbaMutationOutcomeStatus.Ok)
            {
                return ToolResult.Ok(outcome.Message, dataJson);
            }
            if (outcome.Status == VbaMutationOutcomeStatus.Unknown)
            {
                return ToolResult.PartialFailure(
                    outcome.Message,
                    dataJson,
                    string.IsNullOrWhiteSpace(outcome.ErrorCode)
                        ? "vba_mutation_unknown"
                        : outcome.ErrorCode);
            }
            return ToolResult.Fail(
                outcome.Message,
                dataJson,
                outcome.ErrorCode,
                outcome.Retryable);
        }
    }
}
