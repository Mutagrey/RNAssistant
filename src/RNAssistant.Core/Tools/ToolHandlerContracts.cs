using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Tools.Contracts;

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
        public ToolExecutionContext Execution { get; private set; }
        public IDictionary<string, object> Arguments { get; private set; }
        public string PreparedStateJson { get; private set; }
        public bool MayHaveDispatched { get { return Volatile.Read(ref _dispatchPossible) != 0; } }

        public ToolHandlerContext(ToolExecutionContext execution, IDictionary<string, object> arguments,
            string preparedStateJson = null)
        {
            Execution = execution ?? throw new ArgumentNullException(nameof(execution));
            Arguments = ToolArgumentNormalizer.NormalizeDictionary(arguments ?? throw new ArgumentNullException(nameof(arguments)));
            PreparedStateJson = preparedStateJson;
        }

        // A handler must mark this before the first possible effect, not after
        // awaiting an operation whose failure could have hidden its dispatch.
        public void MarkDispatchPossible()
        {
            Interlocked.Exchange(ref _dispatchPossible, 1);
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

        public ToolHandlerResult(ToolResult result, ToolEffectEvidence effect = ToolEffectEvidence.Unreported,
            bool awaitingUser = false)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            if (!Enum.IsDefined(typeof(ToolEffectEvidence), effect)) throw new ArgumentOutOfRangeException(nameof(effect));
            if (awaitingUser && result.Status != ToolResultStatus.Ok)
                throw new ArgumentException("Only a successful interaction can await user input.", nameof(awaitingUser));
            Effect = effect;
            AwaitingUser = awaitingUser;
        }
    }
}
