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
                    if (list.Assignments != null)
                    {
                        if (list.Query != null || list.AgentId != null || list.Group != null || list.AfterAgent != null)
                            throw new AgentCommunicationException("agent_assignment_query_invalid", "Для поручений используйте assignments и after_request; остальные фильтры относятся к участникам.");
                        return new ToolHandlerResult(RuntimeResult.Ok("Необязательные поручения этого чата. ResolutionReceived подтверждает получение отчёта, не внешний эффект. InputStatus сохраняет отличие очереди от применённого ввода.",
                            JsonConvert.SerializeObject(_service.Assignments(_inbox, _session, _limit, list.Assignments, list.AfterRequest))), ToolEffectEvidence.None);
                    }
                    if (list.AfterRequest != null) throw new AgentCommunicationException("agent_assignment_query_invalid", "after_request требует assignments.");
                    return new ToolHandlerResult(RuntimeResult.Ok("Сохранённые чаты этой области, независимо от вкладок. is_self=true — ваш чат.",
                        JsonConvert.SerializeObject(_service.Discover(_session, _limit, list.Query, list.AgentId, list.Group, list.AfterAgent))), ToolEffectEvidence.None);
                }
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Wait)
                {
                    var wait = JObject.FromObject(context.Arguments).ToObject<WaitRequest>();
                    if (wait.Until != "reply" && wait.Until != "completion")
                        throw new AgentCommunicationException("agent_wait_invalid", "until: reply или completion.");
                    var condition = _service.PrepareWait(_inbox, _session, _limit, wait.TimeoutSeconds, wait.Requests, wait.Mode, wait.Until == "completion");
                    return new ToolHandlerResult(RuntimeResult.Ok("Waiting for agent input."), ToolEffectEvidence.None, waitCondition: condition);
                }
                var operationId = "agent_" + RNAssistant.Core.Services.ConversationInputJournal.Fingerprint(new[] {
                    _session.Id, context.Execution.RunId, context.Execution.StepId, context.Execution.Call.Id });
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Resolve)
                {
                    var resolutionRequest = JObject.FromObject(context.Arguments).ToObject<ResolveRequest>();
                    var resolved = _service.Resolve(_inbox, _session, _limit, context.Execution.RunId, operationId,
                        resolutionRequest.RequestId, resolutionRequest.Outcome, resolutionRequest.Text, context.MarkDispatchPossible);
                    return context.Complete(new ToolHandlerResult(RuntimeResult.Ok("Отчёт о результате поручения принят в очередь отправителя. Это не подтверждение внешних эффектов или завершения Run.",
                        JsonConvert.SerializeObject(resolved)), context.MayHaveDispatched ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
                }
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Group)
                {
                    var groupRequest = JObject.FromObject(context.Arguments).ToObject<AgentGroupRequest>();
                    var result = _service.Group(_session, _limit, groupRequest, operationId, context.MarkDispatchPossible);
                    return context.Complete(new ToolHandlerResult(RuntimeResult.Ok("Группы — ссылки на чаты. История и выполнение участников сохранены.",
                        JsonConvert.SerializeObject(result)), groupRequest.Action == "list" ? ToolEffectEvidence.VerifiedNoChange :
                        result.Changed ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
                }
                if (context.Execution.Call.Name == AgentCommunicationToolCatalog.Pause || context.Execution.Call.Name == AgentCommunicationToolCatalog.Resume)
                {
                    var control = JObject.FromObject(context.Arguments).ToObject<ListRequest>();
                    var pauseReceipt = _service.Pause(_inbox, _session, _limit, operationId, control.AgentId,
                        context.Execution.Call.Name == AgentCommunicationToolCatalog.Resume, context.MarkDispatchPossible);
                    var data = JsonConvert.SerializeObject(pauseReceipt);
                    var unknown = pauseReceipt.Status == AgentPauseStatus.Unconfirmed;
                    var rejected = pauseReceipt.Status == AgentPauseStatus.Changed || pauseReceipt.Status == AgentPauseStatus.Unavailable || pauseReceipt.Status == AgentPauseStatus.Protected;
                    var result = unknown && context.MayHaveDispatched ? RuntimeResult.Unknown("Управление не подтверждено. Не повторяйте запрос.", data)
                        : unknown || rejected ? RuntimeResult.Error("Управление не выполнено. Пользовательский Stop и пауза после перезапуска требуют действия пользователя.", data)
                        : RuntimeResult.Ok(pauseReceipt.Status == AgentPauseStatus.Requested ? "Пауза запрошена; текущий шаг ещё может выполняться."
                            : pauseReceipt.Status == AgentPauseStatus.Paused ? "Запуск приостановлен на безопасной границе."
                            : pauseReceipt.Status == AgentPauseStatus.Released ? "Агентская пауза снята; выполнение и завершение работы не подтверждены."
                            : "Агентской паузы нет; состояние не изменено.", data);
                    return context.Complete(new ToolHandlerResult(result, unknown && context.MayHaveDispatched ? ToolEffectEvidence.Unknown
                        : pauseReceipt.Changed && context.MayHaveDispatched ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.VerifiedNoChange));
                }
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
                        operationId, spawn.Title, spawn.InitialMessage, context.MarkDispatchPossible, spawn.ExpectReply, spawn.Assignment);
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
                    request.AgentId, request.Text, context.MarkDispatchPossible, request.ExpectReply, request.ReplyTo, request.Assignment);
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
                : result.Status == AgentWaitStatus.ResolutionsReady ? "Получены явные отчёты о результате поручений. Проверяйте reported_outcome: failed/declined также завершают ожидание. Completed — оценка исполнителя, не доказательство внешнего эффекта."
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
            [JsonProperty("assignment")] public bool Assignment { get; set; }
        }
        private sealed class ResolveRequest
        {
            [JsonProperty("request_id")] public string RequestId { get; set; }
            [JsonProperty("outcome")] public string Outcome { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
        }
        private sealed class ListRequest
        {
            [JsonProperty("query")] public string Query { get; set; }
            [JsonProperty("agent_id")] public string AgentId { get; set; }
            [JsonProperty("group")] public string Group { get; set; }
            [JsonProperty("after_agent")] public string AfterAgent { get; set; }
            [JsonProperty("assignments")] public string Assignments { get; set; }
            [JsonProperty("after_request")] public string AfterRequest { get; set; }
        }
        private sealed class WaitRequest
        {
            [JsonProperty("timeout_seconds")] public int TimeoutSeconds { get; set; } = 5;
            [JsonProperty("requests")] public string[] Requests { get; set; }
            [JsonProperty("mode")] public string Mode { get; set; } = "any";
            [JsonProperty("until")] public string Until { get; set; } = "reply";
        }
        private sealed class SpawnRequest
        {
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("initial_message")] public string InitialMessage { get; set; }
            [JsonProperty("expect_reply")] public bool ExpectReply { get; set; }
            [JsonProperty("assignment")] public bool Assignment { get; set; }
        }
    }
}
