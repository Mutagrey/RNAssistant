using System;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    // Translate shared snapshots into the existing Office observer/delta channel.
    // Accumulation, batching, bounds and terminal ownership belong to Core.
    internal static class ConversationStreamProgressDelivery
    {
        public static ModelProtocolProgress ForProtocol(Action<string, string, ChatActivity> progress)
        {
            string attempt = null;
            var messageLength = 0; var reasoningLength = 0;
            return new ModelProtocolProgress { AttemptProgress = snapshot =>
            {
                if (snapshot.AttemptId != attempt)
                {
                    attempt = snapshot.AttemptId; messageLength = 0; reasoningLength = 0;
                    progress?.Invoke("streaming", string.Empty, null);
                }
                if (snapshot.Reasoning.Length > reasoningLength || snapshot.TransportEnded && reasoningLength > 0)
                {
                    var ended = snapshot.TransportEnded;
                    var title = ended ? "Рассуждение получено" : "Анализ";
                    progress?.Invoke("thinking", title, new ChatActivity { Kind = "reasoning", Title = title,
                        Subtitle = "Ход рассуждения", Status = ended ? "completed" : "running",
                        ResultMessage = snapshot.Reasoning.Substring(reasoningLength) });
                    reasoningLength = snapshot.Reasoning.Length;
                }
                if (snapshot.Message.Length > messageLength)
                {
                    progress?.Invoke("streaming", snapshot.Message.Substring(messageLength), null);
                    messageLength = snapshot.Message.Length;
                }
            }, RetentionFailed = retained => progress?.Invoke("attempt_unsaved", "Не сохранено", new ChatActivity {
                Kind = "model_attempt", Title = "Не сохранено", Status = "unsaved",
                ModelAttemptId = retained.Body.AttemptId, UnsavedAttempt = retained }), OptionalTraceFailed = () => Diagnostics.RuntimeLog.Error("Optional model parser diagnostic append failed.") };
        }
    }
}
