using System;
using System.Text;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ConversationStreamProgressProjector
    {
        public static ModelProtocolProgress ForProtocol(Action<string, string, ChatActivity> progress)
        {
            ConversationStreamProgressProjector current = null;
            return new ModelProtocolProgress
            {
                AttemptStarted = streamingEnabled =>
                {
                    current = new ConversationStreamProgressProjector(progress);
                    current.Start(streamingEnabled);
                },
                StreamUpdate = update => { if (current != null) current.OnUpdate(update); },
                AttemptCompleted = () => { if (current != null) current.Complete(); },
                JsonObjectFallback = () =>
                {
                    if (progress != null) progress("thinking", "Endpoint не поддерживает json_schema; продолжаю с json_object.", null);
                },
                OptionalTraceFailed = () => Diagnostics.RuntimeLog.Error("Causal trace append failed at model.response.accepted.")
            };
        }

        private readonly Action<string, string, ChatActivity> _progress;
        private readonly ConversationMessageStreamExtractor _messageExtractor;
        private readonly StringBuilder _pendingContent;
        private readonly StringBuilder _pendingReasoning;
        private bool _contentReported;
        private bool _reasoningSeen;
        private bool _reasoningCompleted;
        private DateTime _lastContentReportUtc;
        private DateTime _lastReasoningReportUtc;

        public ConversationStreamProgressProjector(Action<string, string, ChatActivity> progress)
        {
            _progress = progress;
            _messageExtractor = new ConversationMessageStreamExtractor();
            _pendingContent = new StringBuilder();
            _pendingReasoning = new StringBuilder();
            _lastContentReportUtc = DateTime.UtcNow;
            _lastReasoningReportUtc = DateTime.UtcNow;
        }

        public void Start(bool streamingEnabled)
        {
            if (streamingEnabled) Report("streaming", string.Empty, null);
        }

        public void OnUpdate(LlmStreamUpdate update)
        {
            if (update == null) return;

            if (!string.IsNullOrEmpty(update.ReasoningDelta))
            {
                _reasoningSeen = true;
                if (_reasoningCompleted) _reasoningCompleted = false;
                _pendingReasoning.Append(update.ReasoningDelta);
            }

            QueueVisibleContent(_messageExtractor.Add(update.ContentDelta));
            if (_messageExtractor.MessageCompleted) FlushContent();
            if (update.Completed)
            {
                Complete();
                return;
            }
            if (!_reasoningCompleted && _pendingReasoning.Length > 0 &&
                (_pendingReasoning.Length >= 256 ||
                 DateTime.UtcNow - _lastReasoningReportUtc >= TimeSpan.FromMilliseconds(100)))
            {
                FlushReasoning(false);
            }
        }

        public void Complete()
        {
            QueueVisibleContent(_messageExtractor.Complete());
            FlushReasoning(true);
            FlushContent();
        }

        private void QueueVisibleContent(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            if (_reasoningSeen && !_reasoningCompleted) FlushReasoning(true);
            _pendingContent.Append(delta);
            if (!_contentReported || _pendingContent.Length >= 256 ||
                DateTime.UtcNow - _lastContentReportUtc >= TimeSpan.FromMilliseconds(50))
            {
                FlushContent();
            }
        }

        private void FlushContent()
        {
            if (_pendingContent.Length == 0) return;
            Report("streaming", _pendingContent.ToString(), null);
            _pendingContent.Clear();
            _contentReported = true;
            _lastContentReportUtc = DateTime.UtcNow;
        }

        private void FlushReasoning(bool completed)
        {
            if (!_reasoningSeen ||
                _pendingReasoning.Length == 0 && (!completed || _reasoningCompleted)) return;
            Report("thinking", completed ? "Анализ завершен." : "Модель анализирует запрос...", new ChatActivity
            {
                Kind = "reasoning",
                Title = completed ? "Анализ завершен" : "Анализ",
                Subtitle = "Ход рассуждения",
                Status = completed ? "completed" : "running",
                ResultMessage = _pendingReasoning.ToString()
            });
            _pendingReasoning.Clear();
            _reasoningCompleted = completed;
            _lastReasoningReportUtc = DateTime.UtcNow;
        }

        private void Report(string phase, string message, ChatActivity activity)
        {
            if (_progress != null) _progress(phase, message ?? string.Empty, activity);
        }
    }
}
