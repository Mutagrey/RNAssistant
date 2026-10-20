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
        RetryLater,
        ChangeRequired
    }

    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ToolRecoveryReadScope
    {
        None,
        Whole,
        RelevantLines
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
        public ToolRecoveryReadScope ReadScope { get; private set; }
        // Exact dependency versions of the failed observation. Runtime-only;
        // semantic targets, not these references, are delivered to the model.
        public IReadOnlyList<ResourceRef> ChangeDependencies { get; private set; }

        [Newtonsoft.Json.JsonConstructor]
        public ToolRecoveryContract(ToolFailureKind failureKind, ToolRetryPolicy retryPolicy,
            ResourceIdentity resourceIdentity = null, string view = null,
            string semanticTarget = null, string currentContent = null,
            bool currentContentComplete = false, string diff = null,
            IEnumerable<ResourceRef> changeDependencies = null,
            ToolRecoveryReadScope readScope = ToolRecoveryReadScope.None)
        {
            if (!Enum.IsDefined(typeof(ToolFailureKind), failureKind))
                throw new ArgumentOutOfRangeException(nameof(failureKind));
            if (!Enum.IsDefined(typeof(ToolRetryPolicy), retryPolicy))
                throw new ArgumentOutOfRangeException(nameof(retryPolicy));
            if (!Enum.IsDefined(typeof(ToolRecoveryReadScope), readScope) ||
                readScope != ToolRecoveryReadScope.None &&
                (resourceIdentity == null || string.IsNullOrWhiteSpace(semanticTarget)))
                throw new ArgumentException("A recovery read requires an exact state and semantic target.");
            if (failureKind == ToolFailureKind.ConflictNoEffect &&
                (resourceIdentity == null || string.IsNullOrWhiteSpace(view) ||
                 string.IsNullOrWhiteSpace(semanticTarget)))
                throw new ArgumentException("A conflict requires exact runtime identity, view and semantic target.");
            if (retryPolicy == ToolRetryPolicy.Replan && resourceIdentity != null &&
                string.IsNullOrWhiteSpace(semanticTarget))
                throw new ArgumentException("State-dependent replanning requires a semantic target.");
            if (currentContent != null && currentContent.Length > 16384)
                throw new ArgumentException("Recovery content exceeds the bounded model projection.", nameof(currentContent));
            if (diff != null && diff.Length > 8192)
                throw new ArgumentException("Recovery diff exceeds the bounded model projection.", nameof(diff));
            var dependencies = (changeDependencies ?? new ResourceRef[0]).Take(33).ToArray();
            if (retryPolicy == ToolRetryPolicy.ChangeRequired)
            {
                if (dependencies.Length < 1 || dependencies.Length > 32 ||
                    dependencies.Any(item => item == null || !item.IsExact) ||
                    dependencies.Select(item => item.Uri).Distinct(StringComparer.Ordinal).Count() != dependencies.Length ||
                    resourceIdentity != null || string.IsNullOrWhiteSpace(semanticTarget))
                    throw new ArgumentException("Change recovery requires 1–32 distinct exact dependencies and a semantic target.");
            }
            else if (dependencies.Length != 0)
                throw new ArgumentException("Change dependencies require ChangeRequired recovery.");
            FailureKind = failureKind;
            RetryPolicy = retryPolicy;
            ResourceIdentity = resourceIdentity;
            View = view;
            SemanticTarget = semanticTarget;
            CurrentContent = currentContent;
            CurrentContentComplete = currentContentComplete;
            Diff = diff;
            ReadScope = readScope;
            ChangeDependencies = Array.AsReadOnly(dependencies.Select(item => item.Copy()).ToArray());
        }

        public bool IsSatisfiedBy(ResourceEvidence evidence)
        {
            return ResourceIdentity != null && evidence != null &&
                (evidence.Complete && evidence.Coverage.Kind == ResourceCoverageKinds.Whole ||
                    ReadScope == ToolRecoveryReadScope.RelevantLines && evidence.Coverage.Kind == ResourceCoverageKinds.CharacterRange) &&
                string.Equals(evidence.View, View, StringComparison.Ordinal) &&
                ResourceIdentity.Equals(evidence.Resource.Identity);
        }

        public bool IsSatisfiedBy(IEnumerable<ResourceEvidence> observations, ResourceEffect effect)
        {
            if (RetryPolicy == ToolRetryPolicy.ChangeRequired)
            {
                // A fresh read, unrelated write, no-op or uncertain effect is not
                // a repair. Exact read-back must prove a new dependency revision.
                return effect != null && (effect.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                    effect.Outcome == ResourceEffectOutcome.Restored) &&
                    effect.Impacts.Any(impact => impact.Relation == ResourceImpactRelation.Exact &&
                        impact.Coverage?.Kind == ResourceCoverageKinds.Whole &&
                        impact.Before?.IsExact == true && impact.After?.IsExact == true &&
                        impact.Identity.Equals(impact.Before.Identity) && impact.Identity.Equals(impact.After.Identity) &&
                        impact.Before.Revision != impact.After.Revision &&
                        ChangeDependencies.Any(dependency => dependency.Identity.Equals(impact.Identity) &&
                            dependency.Revision != impact.After.Revision));
            }
            // The owner can name the logical state behind a validation refusal,
            // including an absent state with no revision yet. Only a subsequent
            // verified change of that exact state permits the same call again.
            if (RetryPolicy == ToolRetryPolicy.Replan)
                return ResourceIdentity != null && effect != null &&
                    (effect.Outcome == ResourceEffectOutcome.VerifiedChanged || effect.Outcome == ResourceEffectOutcome.Restored) &&
                    effect.Impacts.Any(impact => impact.Identity.Equals(ResourceIdentity));
            if (RetryPolicy != ToolRetryPolicy.RefreshRequired || ResourceIdentity == null) return false;
            return (observations ?? new ResourceEvidence[0]).Any(IsSatisfiedBy) ||
                effect != null && (effect.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                    effect.Outcome == ResourceEffectOutcome.Restored || effect.Outcome == ResourceEffectOutcome.ExternalDriftObserved) &&
                effect.Impacts.Any(impact => impact.Identity.Equals(ResourceIdentity));
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
            if (ReadScope != ToolRecoveryReadScope.None)
                result["readScope"] = ReadScope == ToolRecoveryReadScope.Whole ? "whole" : "relevant_lines";
            return result;
        }
    }

    public interface IToolHandler
    {
        Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken);
    }

    public interface IReadOnlyToolHandler : IToolHandler { }
    public interface IManagedMutationToolHandler : IToolHandler { }
    // Trusted built-in planning handlers, including host-gated wrappers. This
    // marker grants an exception only with the exact planning id and binding.
    public interface IPlanningArtifactToolHandler : IManagedMutationToolHandler { }
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
        public ResourceAuthorityCommit AuthorityCommit { get; private set; }

        public ToolHandlerResult(ToolResult result, ToolEffectEvidence effect = ToolEffectEvidence.Unreported,
            bool awaitingUser = false, IEnumerable<ResourceEvidence> resourceEvidence = null,
            IEnumerable<ResourceMutationReadBack> resourceReadBack = null,
            ToolRecoveryContract recovery = null, ResourceAuthorityCommit authorityCommit = null)
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
            if (authorityCommit != null && (result.Status != ToolResultStatus.Ok ||
                effect != ToolEffectEvidence.VerifiedChange || authorityCommit.Effect == null ||
                authorityCommit.Effect.Outcome != ResourceEffectOutcome.VerifiedChanged &&
                authorityCommit.Effect.Outcome != ResourceEffectOutcome.Restored))
                throw new ArgumentException("A direct authority commit requires a verified changed result.", nameof(authorityCommit));
            AuthorityCommit = authorityCommit;
        }
    }
}
