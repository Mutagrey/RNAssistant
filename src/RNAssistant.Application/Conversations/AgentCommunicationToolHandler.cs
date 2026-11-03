using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Application.Conversations
{
    internal sealed class AgentCommunicationToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler
    {
        private readonly AgentCommunicationService _service;
        private readonly ConversationInboxService _inbox;
        private readonly ChatSession _session;
        private readonly int _limit;
        internal AgentCommunicationToolHandler(AgentCommunicationService service, ConversationInboxService inbox,
            ChatSession session, int limit)
        { _service = service; _inbox = inbox; _session = session; _limit = limit; }

        private sealed class ErrorData { [JsonProperty("code")] public string Code { get; set; } }
        public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_service == null || _session == null)
                    throw new AgentCommunicationException("agent_communication_unavailable", "Общение доступно в открытых чатах приложения.");
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.List)
                    return new ToolHandlerResult(RuntimeResult.Ok("Все открытые чаты. is_self=true — ваш текущий чат; остальные — собеседники.",
                        JsonConvert.SerializeObject(_service.List(_session.Id, _limit))), ToolEffectEvidence.None);
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Wait)
                {
                    var wait = JObject.FromObject(context.Arguments).ToObject<WaitRequest>();
                    var result = await _service.WaitAsync(_inbox, _session, _limit, context.Execution.RunId,
                        wait.TimeoutSeconds, token).ConfigureAwait(false);
                    return new ToolHandlerResult(RuntimeResult.Ok(
                        result.Status == AgentWaitStatus.MessagesReady ? "Сообщения поступили; они будут переданы в этом запуске перед следующим шагом модели."
                        : result.Status == AgentWaitStatus.InputPending ? "Есть пользовательский ввод. Обработайте уточнение или завершите ход для очередного запроса."
                        : result.Status == AgentWaitStatus.BudgetExhausted ? "Бюджет ожидания исчерпан. Продолжите самостоятельную работу или завершите ход с указанием ожидаемого ответа."
                        : "Время ожидания истекло. Проверьте состояния собеседников и выберите дальнейшее действие.",
                        JsonConvert.SerializeObject(result)), ToolEffectEvidence.None);
                }
                var request = JObject.FromObject(context.Arguments).ToObject<SendRequest>();
                var receipt = _service.Send(_inbox, _session, _limit, context.Execution.RunId,
                    "agent_" + RNAssistant.Core.Services.ConversationInputJournal.Fingerprint(new[] {
                        _session.Id, context.Execution.RunId, context.Execution.StepId, context.Execution.Call.Id }),
                    request.AgentId, request.Text, context.MarkDispatchPossible);
                return context.Complete(new ToolHandlerResult(RuntimeResult.Ok(
                    "Сообщение принято в очередь; выполнение поручения ещё не подтверждено.", JsonConvert.SerializeObject(receipt)),
                    context.MayHaveDispatched ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
            }
            catch (AgentCommunicationException error) when (!context.MayHaveDispatched)
            {
                return new ToolHandlerResult(RuntimeResult.Error(error.Message,
                    JsonConvert.SerializeObject(new ErrorData { Code = error.Code })), ToolEffectEvidence.None,
                    recovery: new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.None));
            }
        }
        private sealed class SendRequest
        {
            [JsonProperty("agent_id")] public string AgentId { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
        }
        private sealed class WaitRequest
        { [JsonProperty("timeout_seconds")] public int TimeoutSeconds { get; set; } = 5; }
    }
}
