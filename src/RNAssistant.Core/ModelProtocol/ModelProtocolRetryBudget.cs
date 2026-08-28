using System;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.ModelProtocol
{
    // One budget per logical protocol step, never per format repair or raw request.
    internal sealed class ModelProtocolRetryBudget
    {
        private const int MaximumProviderRetries = 2;
        private int _providerRetries;

        public int ProtocolAttemptLimit { get; private set; }

        public ModelProtocolRetryBudget(AppSettings settings)
        {
            var configured = settings.MaxAgentFormatRetries > 0
                ? settings.MaxAgentFormatRetries : AppSettings.DefaultMaxAgentFormatRetries;
            ProtocolAttemptLimit = Math.Max(1, Math.Min(AppSettings.MaximumAgentFormatRetries, configured));
        }

        public bool TryTakeProviderRetry(LlmRequestException failure, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (_providerRetries >= MaximumProviderRetries ||
                (failure.Kind != LlmFailureKind.Timeout &&
                 failure.Kind != LlmFailureKind.Network &&
                 failure.Kind != LlmFailureKind.TransientServer)) return false;

            // The whole step gets at most two transient retries (1s, then 2s).
            // Auth/other HTTP errors, 429, size/adapter errors and cancellation do not retry.
            _providerRetries++;
            delay = TimeSpan.FromSeconds(_providerRetries);
            return true;
        }
    }
}
