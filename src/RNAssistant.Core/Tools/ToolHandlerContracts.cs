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

    public sealed class ToolHandlerContext
    {
        private int _dispatchPossible;
        public ToolExecutionContext Execution { get; private set; }
        public IDictionary<string, object> Arguments { get; private set; }
        public bool MayHaveDispatched { get { return Volatile.Read(ref _dispatchPossible) != 0; } }

        public ToolHandlerContext(ToolExecutionContext execution, IDictionary<string, object> arguments)
        {
            Execution = execution ?? throw new ArgumentNullException(nameof(execution));
            Arguments = ToolArgumentNormalizer.NormalizeDictionary(arguments ?? throw new ArgumentNullException(nameof(arguments)));
        }

        // A handler must mark this before the first possible effect, not after
        // awaiting an operation whose failure could have hidden its dispatch.
        public void MarkDispatchPossible()
        {
            Interlocked.Exchange(ref _dispatchPossible, 1);
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
