using System;

namespace RNAssistant.Core.Llm
{
    public sealed class PromptBudgetExceededException : InvalidOperationException
    {
        public bool CanCompact { get; private set; }

        public PromptBudgetExceededException(string message, bool canCompact)
            : base(message)
        {
            CanCompact = canCompact;
        }
    }

}
