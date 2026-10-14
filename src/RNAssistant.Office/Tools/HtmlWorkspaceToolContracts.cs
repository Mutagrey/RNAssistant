using System;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;
using System.Collections.Generic;

namespace RNAssistant.Office.Tools
{
    internal enum HtmlWorkspaceOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum HtmlWorkspaceEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class HtmlWorkspaceToolOutcome
    {
        internal HtmlWorkspaceOutcomeStatus Status { get; private set; }
        internal HtmlWorkspaceEffect Effect { get; private set; }
        internal string Message { get; private set; }
        // One execution owns this data through preflight and terminal serialization.
        internal JObject Data { get; private set; }
        internal IReadOnlyList<ResourceRef> Resources { get; private set; } = Array.Empty<ResourceRef>();
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }
        internal ToolRecoveryContract Recovery { get; private set; }
        internal IReadOnlyList<ResourceMutationReadBack> ReadBack { get; private set; }

        internal static HtmlWorkspaceToolOutcome Ok(
            string message, JObject data, HtmlWorkspaceEffect effect,
            IReadOnlyList<ResourceMutationReadBack> readBack = null,
            IReadOnlyList<ResourceRef> resources = null)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                Data = data ?? new JObject(),
                Resources = resources ?? Array.Empty<ResourceRef>(),
                ReadBack = readBack
            };
        }

        internal static HtmlWorkspaceToolOutcome Error(
            string message, JObject data, string errorCode, bool retryable,
            ToolRecoveryContract recovery = null)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Error,
                Effect = HtmlWorkspaceEffect.None,
                Message = message ?? string.Empty,
                Data = data ?? new JObject(),
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "html_workspace_failed" : errorCode,
                Retryable = retryable,
                Recovery = recovery ?? new ToolRecoveryContract(
                    ToolFailureKind.RejectedNoEffect,
                    ToolRetryPolicy.Replan)
            };
        }

        internal static HtmlWorkspaceToolOutcome Unknown(
            string message, JObject data, string errorCode)
        {
            return new HtmlWorkspaceToolOutcome
            {
                Status = HtmlWorkspaceOutcomeStatus.Unknown,
                Effect = HtmlWorkspaceEffect.Unknown,
                Message = message ?? string.Empty,
                Data = data ?? new JObject(),
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "html_workspace_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }


}
