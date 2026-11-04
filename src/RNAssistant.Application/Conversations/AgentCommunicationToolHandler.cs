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
    internal sealed class AgentCommunicationToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler, IWaitingToolHandler
    {
        private readonly AgentCommunicationService _service;
        private readonly ConversationInboxService _inbox;
        private readonly ChatSession _session;
        private readonly int _limit;
        internal AgentCommunicationToolHandler(AgentCommunicationService service, ConversationInboxService inbox,
            ChatSession session, int limit)
        { _service = service; _inbox = inbox; _session = session; _limit = limit; }

        private sealed class ErrorData { [JsonProperty("code")] public string Code { get; set; } }
        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        { return Task.FromResult(Execute(context, token)); }

        private ToolHandlerResult Execute(ToolHandlerContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_service == null || _session == null)
                    throw new AgentCommunicationException("agent_communication_unavailable", "Runtime общения недоступен.");
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.List)
                {
                    var list = JObject.FromObject(context.Arguments).ToObject<ListRequest>();
                    return new ToolHandlerResult(RuntimeResult.Ok("Сохранённые чаты этой области, независимо от вкладок. is_self=true — ваш чат.",
                        JsonConvert.SerializeObject(_service.Discover(_session, _limit, list.Query, list.AgentId))), ToolEffectEvidence.None);
                }
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Wait)
                {
                    var wait = JObject.FromObject(context.Arguments).ToObject<WaitRequest>();
                    var condition = _service.PrepareWait(_inbox, _session, _limit, wait.TimeoutSeconds, wait.Requests, wait.Mode);
                    return new ToolHandlerResult(RuntimeResult.Ok("Waiting for agent input."), ToolEffectEvidence.None, waitCondition: condition);
                }
                var operationId = "agent_" + RNAssistant.Core.Services.ConversationInputJournal.Fingerprint(new[] {
                    _session.Id, context.Execution.RunId, context.Execution.StepId, context.Execution.Call.Id });
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Cancel)
                {
                    var cancel = JObject.FromObject(context.Arguments).ToObject<ListRequest>();
                    var cancelReceipt = _service.Cancel(_inbox, _session, _limit, operationId, cancel.AgentId, context.MarkDispatchPossible);
                    var data = JsonConvert.SerializeObject(cancelReceipt);
                    var unknown = cancelReceipt.Status == AgentCancellationStatus.Unconfirmed;
                    var rejected = cancelReceipt.Status == AgentCancellationStatus.Changed || cancelReceipt.Status == AgentCancellationStatus.Unavailable;
                    var result = unknown && context.MayHaveDispatched ? RuntimeResult.Unknown("Отмена не подтверждена. Не повторяйте запрос.", data)
                        : unknown || rejected ? RuntimeResult.Error("Отмена не выполнена или прежний результат не подтверждён. Проверьте участника.", data)
                        : RuntimeResult.Ok(cancelReceipt.Status == AgentCancellationStatus.Requested
                            ? "Отмена запрошена. Завершение запуска и эффекты инструментов ещё не подтверждены."
                            : cancelReceipt.Status == AgentCancellationStatus.Cancelled ? "Отмена запуска подтверждена журналом."
                            : "Участник уже не выполняет этот запуск.", data);
                    return context.Complete(new ToolHandlerResult(result, unknown && context.MayHaveDispatched
                        ? ToolEffectEvidence.Unknown : context.MayHaveDispatched && cancelReceipt.Changed
                            ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
                }
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Spawn)
                {
                    var spawn = JObject.FromObject(context.Arguments).ToObject<SpawnRequest>();
                    var created = _service.Spawn(_inbox, _session, _limit, context.Execution.RunId,
                        operationId, spawn.Title, spawn.InitialMessage, context.MarkDispatchPossible, spawn.ExpectReply);
                    var data = JsonConvert.SerializeObject(created);
                    var unknown = created.InitialMessage == "unknown";
                    var result = unknown ? (context.MayHaveDispatched
                        ? RuntimeResult.Unknown("Чат создан; доставка начального сообщения неизвестна. Не создавайте замену и не повторяйте сообщение.", data)
                        : RuntimeResult.Error("Чат уже создан; прежняя доставка начального сообщения неизвестна. Новая попытка не выполнялась; повтор запрещён.", data))
                        : created.InitialMessage == "rejected" ? RuntimeResult.Error("Чат создан, начальное сообщение не принято. Используйте адрес созданного чата; не создавайте замену.", data)
                        : RuntimeResult.Ok("Обычный чат создан без открытия вкладки. queued означает принятие сообщения, а не запуск или завершение работы.", data);
                    return context.Complete(new ToolHandlerResult(result, unknown && context.MayHaveDispatched
                        ? ToolEffectEvidence.Unknown : context.MayHaveDispatched ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
                }
                var request = JObject.FromObject(context.Arguments).ToObject<SendRequest>();
                var receipt = _service.Send(_inbox, _session, _limit, context.Execution.RunId, operationId,
                    request.AgentId, request.Text, context.MarkDispatchPossible, request.ExpectReply, request.ReplyTo);
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
        public async Task<ToolHandlerResult> CompleteWaitAsync(ToolHandlerContext context, AgentWaitCondition condition, CancellationToken token)
        {
            if (context.Execution.Call.Name != AgentCommunicationToolCatalog.Wait)
                throw new InvalidOperationException("Only agents.wait may resume an observation.");
            var result = await _service.CompleteWaitAsync(_inbox, _session, _limit, context.Execution.RunId, condition, token).ConfigureAwait(false);
            return new ToolHandlerResult(RuntimeResult.Ok(
                result.Status == AgentWaitStatus.MessagesReady ? "Сообщения поступили; они будут переданы в этом запуске перед следующим шагом модели."
                : result.Status == AgentWaitStatus.RepliesReady ? "Указанные ответы получены. Matches подтверждает корреляцию, а не выполнение поручения. Уже обработанные ответы повторно в контекст не добавляются."
                : result.Status == AgentWaitStatus.InputPending ? "Есть другой ввод или запрос с более строгими правами. Условие ожидания не подтверждено: обработайте ввод либо завершите ход для очередного запроса."
                : "Время ожидания истекло. Проверьте состояния собеседников и выберите дальнейшее действие.",
                JsonConvert.SerializeObject(result)), ToolEffectEvidence.None,
                waitTimedOut: result.Status == AgentWaitStatus.TimedOut);
        }
        private sealed class SendRequest
        {
            [JsonProperty("agent_id")] public string AgentId { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("expect_reply")] public bool ExpectReply { get; set; }
            [JsonProperty("reply_to")] public string ReplyTo { get; set; }
        }
        private sealed class ListRequest
        {
            [JsonProperty("query")] public string Query { get; set; }
            [JsonProperty("agent_id")] public string AgentId { get; set; }
        }
        private sealed class WaitRequest
        {
            [JsonProperty("timeout_seconds")] public int TimeoutSeconds { get; set; } = 5;
            [JsonProperty("requests")] public string[] Requests { get; set; }
            [JsonProperty("mode")] public string Mode { get; set; } = "any";
        }
        private sealed class SpawnRequest
        {
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("initial_message")] public string InitialMessage { get; set; }
            [JsonProperty("expect_reply")] public bool ExpectReply { get; set; }
        }
    }
}
