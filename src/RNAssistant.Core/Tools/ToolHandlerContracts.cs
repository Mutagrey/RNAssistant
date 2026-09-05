using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Core.Models;
using System.Linq;

namespace RNAssistant.Core.Tools
{
    public interface IToolHandler
    {
        Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken);
    }

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

        public ToolPreparationResult(ToolResult result, string preparedStateJson = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            if (result.Status == ToolResultStatus.Ok && string.IsNullOrWhiteSpace(preparedStateJson))
                throw new ArgumentException("Successful preparation requires opaque state.", nameof(preparedStateJson));
            if (result.Status != ToolResultStatus.Ok && preparedStateJson != null)
                throw new ArgumentException("Failed preparation cannot authorize prepared state.", nameof(preparedStateJson));
            if (preparedStateJson != null && preparedStateJson.Length > MaxPreparedStateChars)
                throw new ArgumentException("Prepared state exceeds the runtime bound.", nameof(preparedStateJson));
            if (result.DataJson != null && result.DataJson.Length > MaxConfirmationDataChars)
                throw new ArgumentException("Confirmation data exceeds the runtime bound.", nameof(result));
            Result = result;
            PreparedStateJson = preparedStateJson;
        }
    }

    public sealed class ToolHandlerResult
    {
        public ToolResult Result { get; private set; }
        public ToolEffectEvidence Effect { get; private set; }
        public bool AwaitingUser { get; private set; }
        public IReadOnlyList<ResourceEvidence> ResourceEvidence { get; private set; }
        public IReadOnlyList<ResourceMutationReadBack> ResourceReadBack { get; private set; }

        public ToolHandlerResult(ToolResult result, ToolEffectEvidence effect = ToolEffectEvidence.Unreported,
            bool awaitingUser = false, IEnumerable<ResourceEvidence> resourceEvidence = null,
            IEnumerable<ResourceMutationReadBack> resourceReadBack = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            if (!Enum.IsDefined(typeof(ToolEffectEvidence), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (awaitingUser && result.Status != ToolResultStatus.Ok)
                throw new ArgumentException("Only a successful interaction can await user input.", nameof(awaitingUser));
            Effect = effect;
            AwaitingUser = awaitingUser;
            ResourceEvidence = Array.AsReadOnly((resourceEvidence ?? new ResourceEvidence[0]).ToArray());
            ResourceReadBack = Array.AsReadOnly((resourceReadBack ?? new ResourceMutationReadBack[0]).ToArray());
        }
    }
}
