using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Models;
using System.Linq;

namespace RNAssistant.Core.Tools
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ToolFailureKind
    {
        RejectedNoEffect,
        ConflictNoEffect,
        BusyNoEffect,
        ToolDefect
    }

    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ToolRetryPolicy
    {
        None,
        Replan,
        RefreshRequired,
        RetryLater
    }

    public sealed class ToolRecoveryContract
    {
        public ToolFailureKind FailureKind { get; private set; }
        public ToolRetryPolicy RetryPolicy { get; private set; }
        public ResourceIdentity ResourceIdentity { get; private set; }
        public string View { get; private set; }
        public string SemanticTarget { get; private set; }
        public string CurrentContent { get; private set; }
        public bool CurrentContentComplete { get; private set; }
        public string Diff { get; private set; }

        [Newtonsoft.Json.JsonConstructor]
        public ToolRecoveryContract(ToolFailureKind failureKind, ToolRetryPolicy retryPolicy,
            ResourceIdentity resourceIdentity = null, string view = null,
            string semanticTarget = null, string currentContent = null,
            bool currentContentComplete = false, string diff = null)
        {
            if (!Enum.IsDefined(typeof(ToolFailureKind), failureKind))
                throw new ArgumentOutOfRangeException(nameof(failureKind));
            if (!Enum.IsDefined(typeof(ToolRetryPolicy), retryPolicy))
                throw new ArgumentOutOfRangeException(nameof(retryPolicy));
            if (failureKind == ToolFailureKind.ConflictNoEffect &&
                (resourceIdentity == null || string.IsNullOrWhiteSpace(view) ||
                 string.IsNullOrWhiteSpace(semanticTarget)))
                throw new ArgumentException("A conflict requires exact runtime identity, view and semantic target.");
            if (currentContent != null && currentContent.Length > 16384)
                throw new ArgumentException("Recovery content exceeds the bounded model projection.", nameof(currentContent));
            if (diff != null && diff.Length > 8192)
                throw new ArgumentException("Recovery diff exceeds the bounded model projection.", nameof(diff));
            FailureKind = failureKind;
            RetryPolicy = retryPolicy;
            ResourceIdentity = resourceIdentity;
            View = view;
            SemanticTarget = semanticTarget;
            CurrentContent = currentContent;
            CurrentContentComplete = currentContentComplete;
            Diff = diff;
        }

        public bool IsSatisfiedBy(ResourceEvidence evidence)
        {
            return ResourceIdentity != null && evidence != null && evidence.Complete &&
                evidence.Coverage.Kind == ResourceCoverageKinds.Whole &&
                string.Equals(evidence.View, View, StringComparison.Ordinal) &&
                ResourceIdentity.Equals(evidence.Resource.Identity);
        }

        public Newtonsoft.Json.Linq.JObject ModelProjection()
        {
            var result = new Newtonsoft.Json.Linq.JObject
            {
                ["failureKind"] = FailureKind.ToString(),
                ["retryPolicy"] = RetryPolicy.ToString()
            };
            if (!string.IsNullOrWhiteSpace(SemanticTarget)) result["target"] = SemanticTarget;
            if (!string.IsNullOrWhiteSpace(View)) result["representation"] = View;
            if (CurrentContent != null)
            {
                result["currentContent"] = CurrentContent;
                result["currentContentComplete"] = CurrentContentComplete;
            }
            if (!string.IsNullOrWhiteSpace(Diff)) result["changesSinceObservation"] = Diff;
            return result;
        }
    }

    public interface IToolHandler
    {
        Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken);
    }

    public interface IReadOnlyToolHandler : IToolHandler { }
    public interface IManagedMutationToolHandler : IToolHandler { }
    public interface IOpaqueActionToolHandler : IToolHandler { }

    // Preparation may read and validate live state, but it must not dispatch an
    // effect. Its opaque bounded state is persisted with the pending call and is
    // supplied back to the same exact handler only after confirmation.
    public interface IPreparableToolHandler : IToolHandler
    {
        Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken cancellationToken);
    }

    public sealed class ToolHandlerContext
    {
        private int _dispatchPossible;
        private readonly Action _onDispatchPossible;
        private readonly Action<ToolHandlerResult> _onCompleted;
        public ToolExecutionContext Execution { get; private set; }
        public IDictionary<string, object> Arguments { get; private set; }
        public string PreparedStateJson { get; private set; }
        public bool MayHaveDispatched { get { return Volatile.Read(ref _dispatchPossible) != 0; } }

        public ToolHandlerContext(ToolExecutionContext execution, IDictionary<string, object> arguments,
            string preparedStateJson = null, Action onDispatchPossible = null,
            Action<ToolHandlerResult> onCompleted = null)
        {
            Execution = execution ?? throw new ArgumentNullException(nameof(execution));
            Arguments = ToolArgumentNormalizer.NormalizeDictionary(arguments ?? throw new ArgumentNullException(nameof(arguments)));
            PreparedStateJson = preparedStateJson;
            _onDispatchPossible = onDispatchPossible;
            _onCompleted = onCompleted;
        }

        // Domain owners call this while their guard/read-back gate is still held.
        // The callback publishes authority before the owner may release that gate.
        public ToolHandlerResult Complete(ToolHandlerResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (_onCompleted != null) _onCompleted(result);
            return result;
        }

        public void CompleteFailure(Exception error)
        {
            Complete(new ToolHandlerResult(MayHaveDispatched
                ? ToolResult.Unknown(error.Message) : ToolResult.Error(error.Message),
                MayHaveDispatched ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None));
        }

        // A handler must mark this before the first possible effect, not after
        // awaiting an operation whose failure could have hidden its dispatch.
        public void MarkDispatchPossible()
        {
            if (Interlocked.CompareExchange(ref _dispatchPossible, 1, 0) != 0) return;
            try
            {
                if (_onDispatchPossible != null) _onDispatchPossible();
            }
            catch
            {
                Interlocked.Exchange(ref _dispatchPossible, 0);
                throw;
            }
        }
    }

    public sealed class ToolPreparationResult
    {
        public const int MaxPreparedStateChars = 131072;
        public const int MaxConfirmationDataChars = 32768;

        public ToolResult Result { get; private set; }
        public string PreparedStateJson { get; private set; }
        public ToolRecoveryContract Recovery { get; private set; }

        public ToolPreparationResult(ToolResult result, string preparedStateJson = null,
            ToolRecoveryContract recovery = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            if (result.Status == ToolResultStatus.Ok && string.IsNullOrWhiteSpace(preparedStateJson))
                throw new ArgumentException("Successful preparation requires opaque state.", nameof(preparedStateJson));
            if (result.Status != ToolResultStatus.Ok && preparedStateJson != null)
                throw new ArgumentException("Failed preparation cannot authorize prepared state.", nameof(preparedStateJson));
            if (preparedStateJson != null && preparedStateJson.Length > MaxPreparedStateChars)
                throw new ArgumentException("Prepared state exceeds the runtime bound.", nameof(preparedStateJson));
            if (result.Status == ToolResultStatus.Ok && recovery != null)
                throw new ArgumentException("Successful preparation cannot require recovery.", nameof(recovery));
            if (result.DataJson != null && result.DataJson.Length > MaxConfirmationDataChars)
                throw new ArgumentException("Confirmation data exceeds the runtime bound.", nameof(result));
            Result = result;
            PreparedStateJson = preparedStateJson;
            Recovery = recovery;
        }
    }

    public sealed class ToolHandlerResult
    {
        public ToolResult Result { get; private set; }
        public ToolEffectEvidence Effect { get; private set; }
        public bool AwaitingUser { get; private set; }
        public IReadOnlyList<ResourceEvidence> ResourceEvidence { get; private set; }
        public IReadOnlyList<ResourceMutationReadBack> ResourceReadBack { get; private set; }
        public ToolRecoveryContract Recovery { get; private set; }

        public ToolHandlerResult(ToolResult result, ToolEffectEvidence effect = ToolEffectEvidence.Unreported,
            bool awaitingUser = false, IEnumerable<ResourceEvidence> resourceEvidence = null,
            IEnumerable<ResourceMutationReadBack> resourceReadBack = null,
            ToolRecoveryContract recovery = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            if (!Enum.IsDefined(typeof(ToolEffectEvidence), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (awaitingUser && result.Status != ToolResultStatus.Ok)
                throw new ArgumentException("Only a successful interaction can await user input.", nameof(awaitingUser));
            if (result.Status == ToolResultStatus.Ok && recovery != null)
                throw new ArgumentException("A successful result cannot require recovery.", nameof(recovery));
            Effect = effect;
            AwaitingUser = awaitingUser;
            ResourceEvidence = Array.AsReadOnly((resourceEvidence ?? new ResourceEvidence[0]).ToArray());
            ResourceReadBack = Array.AsReadOnly((resourceReadBack ?? new ResourceMutationReadBack[0]).ToArray());
            Recovery = recovery;
        }
    }
}
