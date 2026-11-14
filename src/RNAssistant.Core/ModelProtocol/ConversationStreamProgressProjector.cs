using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public enum ModelTransportOutcome { Receiving, Completed, Cancelled, Failed }

    // A bounded, immutable observation of one HTTP request. Transport completion
    // is not protocol admission or runtime acceptance.
    public sealed class ConversationAttemptSnapshot
    {
        [JsonProperty]
        public string AttemptId { get; internal set; }
        [JsonProperty]
        public long Revision { get; internal set; }
        [JsonProperty]
        public string Message { get; internal set; }
        [JsonProperty]
        public string Reason { get; internal set; }
        [JsonProperty]
        public string Decision { get; internal set; }
        [JsonProperty]
        public string Refusal { get; internal set; }
        [JsonProperty]
        public bool ReasonTruncated { get; internal set; }
        [JsonProperty]
        public bool DecisionTruncated { get; internal set; }
        [JsonProperty]
        public bool RefusalTruncated { get; internal set; }
        [JsonProperty]
        public string Reasoning { get; internal set; }
        [JsonProperty]
        public bool MessageTruncated { get; internal set; }
        [JsonProperty]
        public bool ReasoningTruncated { get; internal set; }
        [JsonProperty]
        public bool? IsEventStream { get; internal set; }
        [JsonProperty]
        public ModelTransportOutcome TransportOutcome { get; internal set; }
        [JsonProperty] public bool OutputTruncated { get; internal set; }
        public bool TransportEnded { get { return TransportOutcome != ModelTransportOutcome.Receiving; } }
    }

    // One coalesced snapshot, not an unbounded queue. All producers (including the
    // timer and terminal flush) serialize here; late transport callbacks are inert.
    public sealed class ConversationStreamProgressProjector : IDisposable
    {
        public const int MaximumFieldCharacters = 24000;
        private readonly object _sync = new object();
        private readonly string _attemptId;
        private readonly Action<ConversationAttemptSnapshot> _publish;
        private readonly ConversationMessageStreamExtractor _extractor, _reasonExtractor, _decisionExtractor;
        private readonly StringBuilder _reason = new StringBuilder(), _decision = new StringBuilder(), _refusal = new StringBuilder();
        private bool _reasonTruncated, _decisionTruncated, _refusalTruncated;
        private readonly StringBuilder _message = new StringBuilder();
        private readonly StringBuilder _reasoning = new StringBuilder();
        private Timer _timer;
        private long _revision;
        private int _pending;
        private bool _sawContent, _messageTruncated, _reasoningTruncated, _outputTruncated;
        private bool? _isEventStream;
        private ModelTransportOutcome _outcome;
        public ConversationAttemptSnapshot Snapshot { get { lock (_sync) return Capture(); } }

        public ConversationStreamProgressProjector(string attemptId, bool schemaTransport,
            Action<ConversationAttemptSnapshot> publish)
        {
            if (string.IsNullOrWhiteSpace(attemptId)) throw new ArgumentException("An attempt identity is required.", nameof(attemptId));
            _attemptId = attemptId; _publish = publish;
            _extractor = new ConversationMessageStreamExtractor(schemaTransport, maximumCharacters: MaximumFieldCharacters);
            _reasonExtractor = new ConversationMessageStreamExtractor(schemaTransport, "reason", MaximumFieldCharacters);
            _decisionExtractor = new ConversationMessageStreamExtractor(schemaTransport, "decision", MaximumFieldCharacters);
            lock (_sync) Publish();
        }

        public void OnUpdate(LlmStreamUpdate update)
        {
            if (update == null) return;
            lock (_sync)
            {
                if (_outcome != ModelTransportOutcome.Receiving) return;
                var firstMessage = _message.Length == 0;
                var firstReasoning = _reasoning.Length == 0;
                var firstVisible = _reason.Length + _decision.Length + _refusal.Length == 0;
                if (!string.IsNullOrEmpty(update.ContentDelta))
                { _sawContent = true; _isEventStream = true; }
                if (!string.IsNullOrEmpty(update.ReasoningDelta) || !string.IsNullOrEmpty(update.RefusalDelta)) _isEventStream = true;
                AppendContent(update.ContentDelta);
                Append(_refusal, update.RefusalDelta, ref _refusalTruncated);
                Append(_reasoning, update.ReasoningDelta, ref _reasoningTruncated);
                if (_pending == 0) return;
                if (firstMessage && _message.Length > 0 || firstReasoning && _reasoning.Length > 0 || firstVisible && _reason.Length + _decision.Length + _refusal.Length > 0 || _pending >= 256)
                    Publish();
                else if (_timer == null) _timer = new Timer(Flush, null, 100, Timeout.Infinite);
                // update.Completed belongs to the parser. The request owner ends
                // transport after it has the completion (including JSON fallback).
            }
        }

        public ConversationAttemptSnapshot End(ModelTransportOutcome outcome, LlmCompletionResult completion = null)
        {
            if (outcome == ModelTransportOutcome.Receiving) throw new ArgumentException("A terminal transport outcome is required.");
            lock (_sync)
            {
                if (_outcome != ModelTransportOutcome.Receiving) return Capture();
                if (completion != null)
                {
                    _isEventStream = completion.IsEventStream;
                    if (!_sawContent) AppendContent(completion.Content);
                    if (_refusal.Length == 0)
                    {
                        Append(_refusal, completion.RefusalContent, ref _refusalTruncated);
                        _refusalTruncated |= completion.RefusalTruncated;
                    }
                    _outputTruncated = completion.Termination == LlmCompletionTermination.OutputLimit;
                    if (_reasoning.Length == 0) Append(_reasoning, completion.ReasoningContent, ref _reasoningTruncated);
                    _reasoningTruncated |= completion.ReasoningTruncated;
                }
                Append(_message, _extractor.Complete(), ref _messageTruncated);
                _messageTruncated |= _message.Length > 0 && !_extractor.MessageCompleted;
                _reasonTruncated |= _reason.Length > 0 && !_reasonExtractor.MessageCompleted;
                _decisionTruncated |= _decision.Length > 0 && !_decisionExtractor.MessageCompleted;
                _outcome = outcome;
                Publish();
                return Capture();
            }
        }

        private void AppendContent(string value)
        {
            Append(_message, _extractor.Add(value), ref _messageTruncated);
            Append(_reason, _reasonExtractor.Add(value), ref _reasonTruncated);
            Append(_decision, _decisionExtractor.Add(value), ref _decisionTruncated);
            _messageTruncated |= _extractor.Truncated;
            _reasonTruncated |= _reasonExtractor.Truncated;
            _decisionTruncated |= _decisionExtractor.Truncated;
        }

        private void Append(StringBuilder target, string value, ref bool truncated)
        {
            if (string.IsNullOrEmpty(value)) return;
            var take = Math.Min(value.Length, MaximumFieldCharacters - target.Length);
            target.Append(value, 0, take);
            truncated |= take < value.Length;
            _pending += take;
        }

        private void Flush(object state)
        {
            lock (_sync)
            {
                if (_outcome == ModelTransportOutcome.Receiving && _pending > 0) Publish();
            }
        }

        private ConversationAttemptSnapshot Capture()
        {
            return new ConversationAttemptSnapshot { AttemptId = _attemptId, Revision = _revision,
                Message = _message.ToString(), Reasoning = _reasoning.ToString(),
                Reason = _reason.ToString(), Decision = _decision.ToString(), Refusal = _refusal.ToString(),
                ReasonTruncated = _reasonTruncated, DecisionTruncated = _decisionTruncated, RefusalTruncated = _refusalTruncated,
                MessageTruncated = _messageTruncated, ReasoningTruncated = _reasoningTruncated,
                IsEventStream = _isEventStream, TransportOutcome = _outcome, OutputTruncated = _outputTruncated };
        }

        private void Publish()
        {
            _timer?.Dispose(); _timer = null; _pending = 0; _revision++;
            // Observation must not fail the request or escape a timer callback.
            // Durable attempt commits use a separate mandatory event port.
            try { _publish?.Invoke(Capture()); } catch { }
        }

        public void Dispose() { End(ModelTransportOutcome.Failed); }
    }
}
