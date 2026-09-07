using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Runtime
{
    internal static class OfficeToolFailure
    {
        internal static Task<ToolHandlerResult> Rejected(
            string message, string code,
            ToolRetryPolicy retryPolicy = ToolRetryPolicy.None,
            bool retryable = false)
        {
            return Handler(message, code, retryable, new ToolRecoveryContract(
                ToolFailureKind.RejectedNoEffect, retryPolicy));
        }

        internal static Task<ToolHandlerResult> Guard(
            OfficeDocumentGuardException error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return Handler(error.Message, error.ErrorCode, error.Retryable,
                GuardRecovery(error.ErrorCode));
        }

        internal static Task<ToolHandlerResult> Lock(
            HostRuntime.MutationLockException error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return Handler(error.Message,
                error.Retryable ? "tool_mutation_busy" :
                    "tool_mutation_lock_unavailable",
                error.Retryable,
                LockRecovery(error.Retryable));
        }

        internal static Task<ToolHandlerResult> Resource(
            ResourceRequestException error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return Handler(error.Message, error.ErrorCode, error.Retryable,
                ResourceRecovery(error.ErrorCode));
        }

        internal static ToolRecoveryContract DefiniteDomain(bool retryable)
        {
            return new ToolRecoveryContract(
                ToolFailureKind.RejectedNoEffect,
                retryable ? ToolRetryPolicy.RetryLater :
                    ToolRetryPolicy.Replan);
        }

        internal static Task<ToolPreparationResult> GuardPreparation(
            OfficeDocumentGuardException error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return Preparation(error.Message, error.ErrorCode, error.Retryable,
                GuardRecovery(error.ErrorCode));
        }

        internal static Task<ToolPreparationResult> LockPreparation(
            HostRuntime.MutationLockException error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            return Preparation(error.Message,
                error.Retryable ? "tool_mutation_busy" :
                    "tool_mutation_lock_unavailable",
                error.Retryable,
                LockRecovery(error.Retryable));
        }

        private static Task<ToolHandlerResult> Handler(
            string message, string code, bool retryable,
            ToolRecoveryContract recovery)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message, Data(code, retryable)),
                ToolEffectEvidence.None,
                recovery: recovery));
        }

        private static Task<ToolPreparationResult> Preparation(
            string message, string code, bool retryable,
            ToolRecoveryContract recovery)
        {
            return Task.FromResult(new ToolPreparationResult(
                RuntimeResult.Error(message, Data(code, retryable)),
                recovery: recovery));
        }

        private static ToolRecoveryContract GuardRecovery(string code)
        {
            return new ToolRecoveryContract(
                IsUnavailableTarget(code) ? ToolFailureKind.RejectedNoEffect :
                    ToolFailureKind.ToolDefect,
                ToolRetryPolicy.None);
        }

        private static ToolRecoveryContract ResourceRecovery(string code)
        {
            if (string.Equals(code, "tool_mutation_busy", StringComparison.Ordinal))
                return LockRecovery(true);
            if (string.Equals(code, "tool_mutation_lock_unavailable", StringComparison.Ordinal) ||
                string.Equals(code, "document_identity_unavailable", StringComparison.Ordinal))
                return new ToolRecoveryContract(
                    ToolFailureKind.ToolDefect, ToolRetryPolicy.None);
            return new ToolRecoveryContract(
                ToolFailureKind.RejectedNoEffect,
                IsUnavailableTarget(code) ? ToolRetryPolicy.None :
                    ToolRetryPolicy.Replan);
        }

        private static bool IsUnavailableTarget(string code)
        {
            return string.Equals(code, "active_document_changed", StringComparison.Ordinal) ||
                string.Equals(code, "document_session_unavailable", StringComparison.Ordinal);
        }

        private static ToolRecoveryContract LockRecovery(bool retryable)
        {
            return new ToolRecoveryContract(
                retryable ? ToolFailureKind.BusyNoEffect :
                    ToolFailureKind.ToolDefect,
                retryable ? ToolRetryPolicy.RetryLater :
                    ToolRetryPolicy.None);
        }

        private static string Data(string code, bool retryable)
        {
            return JsonConvert.SerializeObject(new { code, retryable });
        }
    }
}
