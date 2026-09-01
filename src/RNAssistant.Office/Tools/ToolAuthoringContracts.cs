using System;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal enum ToolAuthoringOutcomeStatus
    {
        Ok,
        Error,
        Unknown
    }

    internal enum ToolAuthoringEffect
    {
        None,
        VerifiedNoChange,
        VerifiedChange,
        Unknown
    }

    internal sealed class ToolAuthoringOutcome
    {
        internal ToolAuthoringOutcomeStatus Status { get; private set; }
        internal ToolAuthoringEffect Effect { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }
        internal string ErrorCode { get; private set; }
        internal bool Retryable { get; private set; }
        internal bool Success { get { return Status == ToolAuthoringOutcomeStatus.Ok; } }

        internal static ToolAuthoringOutcome Ok(
            string message, string dataJson = null,
            ToolAuthoringEffect effect = ToolAuthoringEffect.None)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Ok,
                Effect = effect,
                Message = message ?? string.Empty,
                DataJson = dataJson
            };
        }

        internal static ToolAuthoringOutcome Error(
            string message, string dataJson = null,
            string errorCode = null, bool retryable = false)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Error,
                Effect = ToolAuthoringEffect.None,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "tool_authoring_failed" : errorCode,
                Retryable = retryable
            };
        }

        internal static ToolAuthoringOutcome Unknown(
            string message, string dataJson = null,
            string errorCode = null)
        {
            return new ToolAuthoringOutcome
            {
                Status = ToolAuthoringOutcomeStatus.Unknown,
                Effect = ToolAuthoringEffect.Unknown,
                Message = message ?? string.Empty,
                DataJson = dataJson,
                ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                    ? "tool_authoring_effect_unknown" : errorCode,
                Retryable = false
            };
        }
    }

    internal sealed class ToolAuthoringPreparation
    {
        internal ToolAuthoringOutcome Outcome { get; private set; }
        internal string PreparedStateJson { get; private set; }

        internal ToolAuthoringPreparation(
            ToolAuthoringOutcome outcome, string preparedStateJson = null)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            PreparedStateJson = preparedStateJson;
        }
    }

    internal sealed class ToolLibraryCoreMutation
    {
        internal string Kind { get; set; }
        internal string BaseId { get; set; }
        internal string ExpectedRevision { get; set; }
        internal ToolCatalogEntry Intended { get; set; }
    }

    internal sealed class ToolManualMutationResult
    {
        internal ToolAuthoringOutcome Outcome { get; private set; }
        internal bool DispatchPossible { get; private set; }
        internal ToolCatalogEntry Package { get; private set; }
        internal string Id { get; private set; }
        internal string Operation { get; private set; }
        internal string PreviousRevision { get; private set; }
        internal string Revision { get; private set; }

        internal ToolManualMutationResult(
            ToolAuthoringOutcome outcome,
            bool dispatchPossible,
            ToolCatalogEntry package,
            string id,
            string operation,
            string previousRevision,
            string revision)
        {
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            DispatchPossible = dispatchPossible;
            Package = package;
            Id = id ?? string.Empty;
            Operation = operation ?? string.Empty;
            PreviousRevision = previousRevision ?? string.Empty;
            Revision = revision ?? string.Empty;
        }
    }
}
