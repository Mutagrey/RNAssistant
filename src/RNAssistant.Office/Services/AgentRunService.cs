using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class AgentRunService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly AgentPlannerCompletionRunner _plannerCompletion;
        private readonly OfficeIntentRouter _intentRouter;
        private readonly ToolCatalogSlicer _toolCatalogSlicer;
        private readonly PlannerPromptComposer _plannerPromptComposer;
        private readonly AgentActionValidator _actionValidator;
        private readonly ObservationNormalizer _observationNormalizer;
        private readonly VerificationRunner _verificationRunner;
        private readonly VerificationExecutor _verificationExecutor;
        private readonly AgentToolCatalogResolver _toolCatalogResolver;
        private readonly OfficeSnapshotReader _snapshotReader;
        private readonly ContextCompactionService _contextCompactionService;

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            bool includeControllerTools = true)
            : this(adapter, toolExecutor, completeAsync, includeControllerTools, null)
        {
        }

        internal AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            bool includeControllerTools,
            ContextCompactionService contextCompactionService)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _plannerCompletion = new AgentPlannerCompletionRunner(completeAsync);
            _intentRouter = new OfficeIntentRouter();
            _toolCatalogSlicer = new ToolCatalogSlicer();
            _plannerPromptComposer = new PlannerPromptComposer();
            _actionValidator = new AgentActionValidator();
            _observationNormalizer = new ObservationNormalizer();
            _verificationRunner = new VerificationRunner();
            _verificationExecutor = new VerificationExecutor(TimeSpan.FromSeconds(15));
            _toolCatalogResolver = new AgentToolCatalogResolver(toolExecutor, includeControllerTools);
            _snapshotReader = new OfficeSnapshotReader(adapter);
            _contextCompactionService = contextCompactionService;
        }

        public Task<ChatCompletionResult> RunUserTurnAsync(
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken,
            bool appendUserMessage = true)
        {
            var resumePendingPlan = AgentTaskContinuationResolver.ShouldContinue(text, session);
            var taskText = AgentTaskContinuationResolver.Resolve(text, session);
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text,
                    HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId,
                    Attachments = attachments == null ? new List<ChatAttachment>() : new List<ChatAttachment>(attachments)
                });
            }
            return RunLoopAsync(taskText, false, session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, null, null, null, resumePendingPlan, cancellationToken);
        }

        public Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ToolResult confirmedResult,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken)
        {
            var initialProtocolMessages = new List<ChatMessage>();
            if (confirmedResult != null)
            {
                AgentProtocolHistory.AppendToolExchange(initialProtocolMessages, session, null, confirmedCommand, confirmedResult, settings);
            }
            var taskText = LatestUserRequest(session, confirmedCommand == null ? string.Empty : confirmedCommand.ToolId);
            return RunLoopAsync(taskText, CommandMutates(confirmedCommand, tools), session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, initialProtocolMessages, confirmedCommand, confirmedResult, false, cancellationToken);
        }

        private static bool CommandMutates(ToolCommand command, IReadOnlyList<ToolDefinition> tools)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return false;
            }

            var tool = (tools ?? new ToolDefinition[0]).FirstOrDefault(t =>
                t != null && string.Equals(t.Id, command.ToolId, StringComparison.OrdinalIgnoreCase));
            return ToolSafetyPolicy.EffectiveMutatesDocument(tool, tools);
        }

        private async Task<ChatCompletionResult> RunLoopAsync(
            string taskText,
            bool initialVerificationRequired,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            IReadOnlyList<ChatMessage> initialProtocolMessages,
            ToolCommand initialCommand,
            ToolResult initialPlanResult,
            bool resumePendingPlan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings = settings ?? new AppSettings();
            tools = tools ?? new ToolDefinition[0];
            skills = skills ?? new SkillDefinition[0];

            var snapshot = new OfficeSnapshot { Host = _adapter.HostName };
            var route = _intentRouter.Route(taskText, snapshot, session);
            if (initialVerificationRequired)
            {
                route.Phase = AgentPhases.Verification;
                route.RequiresTool = true;
            }
            if (route.RequiresInspection || settings.IncludeVbaContext || OfficeSnapshotReader.IsVbaTask(taskText))
            {
                ReportProgress(progress, "context", "Собираю необходимый контекст Office...");
                snapshot = _snapshotReader.Read(settings, taskText);
                route.App = AgentText.FirstNonEmpty(snapshot.Host, route.App);
            }

            var observations = new List<AgentObservation>();
            var resultLog = new List<object>();
            object contextUsage = null;
            var assistantText = string.Empty;
            var maxIterations = Math.Max(1, settings.MaxAgentIterations);
            var maxToolSteps = Math.Max(1, settings.MaxAgentToolSteps);
            var allTools = _toolCatalogResolver.Resolve(tools);
            var state = new AgentRunState
            {
                PendingVerification = initialVerificationRequired,
                TotalToolSteps = initialPlanResult == null ? 0 : Math.Max(0, initialPlanResult.ToolStepsConsumed)
            };
            var routingDiagnosticsJson = string.Empty;
            var protocolMessages = new List<ChatMessage>(initialProtocolMessages ?? new ChatMessage[0]);
            var llmRunCache = new LlmRunCache();
            var budgetCompactionRetried = false;
            var runMessageStart = Math.Max(0, (session == null || session.Messages == null ? 0 : session.Messages.Count) - 1);
            if (initialCommand != null && initialPlanResult != null)
            {
                var initialTool = AgentToolCatalogResolver.Find(allTools, initialCommand.ToolId);
                var initialObservation = _observationNormalizer.Normalize(initialCommand, initialTool, initialPlanResult,
                    initialTool != null && (initialTool.MutatesDocument || initialTool.MutatesLocalState)
                        ? AgentObservationPurposes.Mutation
                        : AgentObservationPurposes.Inspection);
                observations.Add(initialObservation);
                resultLog.Add(AgentTranscript.DescribeResult(initialCommand, initialPlanResult));
                AgentPhaseController.Advance(route, observations, state.PendingVerification);
            }
            if ((initialPlanResult != null || resumePendingPlan) && AgentPlanStateService.Restore(session, state) != null)
            {
                if (resumePendingPlan && initialPlanResult == null)
                {
                    state.LastPlanObservationCount = observations.Count;
                }
                ReportPlanProgress(progress, state.PlanActivity);
            }

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeSkills = SkillResolver.ActiveSkills(session, skills);
                var skillScopedTools = SkillResolver.FilterTools(allTools, skills, activeSkills);
                var slice = _toolCatalogSlicer.Slice(route, skillScopedTools, observations, settings.MaxAgentToolsPerRequest, settings.AllowAgentToolAuthoring, settings);
                routingDiagnosticsJson = AgentRunPresentation.BuildRoutingDiagnosticsJson(route, slice);
                if (iteration == 0)
                {
                    ReportProgress(progress, "routing", AgentRunPresentation.BuildTaskProgressMessage(route, false), AgentRunPresentation.BuildRoutingActivity(route, slice));
                }
                if (route.RequiresTool && slice.Tools.Count == 0)
                {
                    assistantText = AgentRunPresentation.RecordMissingTools(session, route, slice);
                    RememberPendingTask(session, taskText, assistantText, AgentResponseKinds.CannotComplete);
                    resultLog.Add(new
                    {
                        success = false,
                        status = "no_available_tools",
                        phase = route.Phase,
                        taskType = route.TaskType
                    });
                    break;
                }
                var requestText = taskText;
                var planDecisionAllowed = !state.PlanDeclared || observations.Count > state.LastPlanObservationCount;
                var requestOptions = AgentPlannerCompletionRunner.BuildOptions(
                    string.IsNullOrWhiteSpace(state.ResponseMode) ? settings.AgentResponseMode : state.ResponseMode,
                    slice.Tools,
                    llmRunCache,
                    planDecisionAllowed);
                requestOptions.ReasoningEnabled = session == null ? (bool?)null : session.ReasoningEnabled;
                var requestProtocolMessages = WithPlanDecisionConstraint(protocolMessages, planDecisionAllowed);
                List<ChatMessage> messages;
                try
                {
                    messages = _plannerPromptComposer.BuildMessages(
                        requestText,
                        snapshot,
                        route,
                        slice,
                        observations,
                        documentContext,
                        skills,
                        settings,
                        session,
                        attachments,
                        requestProtocolMessages,
                        requestOptions);
                }
                catch (PromptBudgetExceededException ex) when (
                    ex.CanCompact &&
                    !budgetCompactionRetried &&
                    settings.AutoCompressContext &&
                    _contextCompactionService != null)
                {
                    var checkpoint = await _contextCompactionService.EnsureWithinBudgetAsync(
                        session,
                        settings,
                        string.Empty,
                        true,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (checkpoint == null) throw;
                    budgetCompactionRetried = true;
                    messages = _plannerPromptComposer.BuildMessages(
                        requestText,
                        snapshot,
                        route,
                        slice,
                        observations,
                        documentContext,
                        skills,
                        settings,
                        session,
                        attachments,
                        requestProtocolMessages,
                        requestOptions);
                }
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", AgentRunPresentation.BuildTaskProgressMessage(route, true));
                var plannerAttempt = await _plannerCompletion.CompleteAsync(
                    settings,
                    messages,
                    slice.Tools,
                    state,
                    requestOptions,
                    progress,
                    AgentRunPresentation.BuildTaskProgressMessage(route, true),
                    "Исправляю формат следующего действия...",
                    PromptText(settings, p => p.RepairDecisionPrompt),
                    cancellationToken).ConfigureAwait(false);
                contextUsage = plannerAttempt.ContextUsage;
                cancellationToken.ThrowIfCancellationRequested();
                var completion = plannerAttempt.Completion;
                var plannerText = plannerAttempt.Text;
                var parsed = plannerAttempt.ParseResult;
                AgentRunPresentation.RecordRecoveredPlannerResponses(session, plannerAttempt.RejectedResponses);

                if (!parsed.Success)
                {
                    assistantText = AgentRunPresentation.RecordPlannerFailure(session, completion, plannerText, parsed, "Planner JSON invalid");
                    RememberPendingTask(session, taskText, assistantText, "planner_error");
                    break;
                }
                var response = parsed.Response;
                var isPlanDecision = string.Equals(response.Kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase);
                if (isPlanDecision && !planDecisionAllowed)
                {
                    state.NoProgressPlanCount += 1;
                    protocolMessages.Add(new ChatMessage { Role = "assistant", Content = plannerText });
                    if (state.NoProgressPlanCount >= 2)
                    {
                        assistantText = "Модель повторно вернула план без нового наблюдения. Выполнение остановлено, текущий план сохранён.";
                        RememberPendingTask(session, taskText, assistantText, "repeated_plan_no_progress");
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, null, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "План не продвигается",
                            Subtitle = "planner",
                            Status = "failed",
                            ExecutionStatus = "repeated_plan_no_progress",
                            ResultMessage = assistantText
                        }));
                        break;
                    }
                    ReportProgress(progress, "repairing", "План уже задан; запрашиваю следующее действие...");
                    protocolMessages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = "The current plan is already declared and no new runtime observation exists after it. Do not return kind=plan or rephrase the plan. Choose one allowed next decision: tool, clarify, final, or cannot_complete."
                    });
                    continue;
                }
                state.NoProgressPlanCount = 0;
                if (!string.IsNullOrWhiteSpace(response.Goal))
                {
                    state.WorkingGoal = response.Goal;
                }
                if (isPlanDecision)
                {
                    bool updatedExisting;
                    var visiblePlan = AgentPlanStateService.ApplyDecision(session, state, response, out updatedExisting);
                    state.LastPlanObservationCount = observations.Count;
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                        response.DecisionSummary,
                        completion,
                        updatedExisting ? AgentPlanStateService.CreateUpdateActivity(response, visiblePlan) : visiblePlan,
                        response.DecisionSummary,
                        state.WorkingGoal));
                    ReportProgress(progress, "plan", response.DecisionSummary, visiblePlan);
                    protocolMessages.Add(new ChatMessage { Role = "assistant", Content = plannerText });
                    var continuation = PromptText(settings, p => p.PlanContinuationPrompt);
                    continuation += " Runtime constraint for the immediately following decision: kind=plan is unavailable until a new observation is recorded.";
                    protocolMessages.Add(new ChatMessage { Role = "user", Content = continuation });
                    continue;
                }

                if (planDecisionAllowed && response.Plan != null && response.Plan.Count > 0)
                {
                    bool updatedExisting;
                    var visiblePlan = AgentPlanStateService.ApplyDecision(session, state, response, out updatedExisting);
                    state.LastPlanObservationCount = observations.Count;
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                        response.DecisionSummary,
                        null,
                        updatedExisting ? AgentPlanStateService.CreateUpdateActivity(response, visiblePlan) : visiblePlan,
                        response.DecisionSummary,
                        state.WorkingGoal));
                    ReportProgress(progress, "plan", response.DecisionSummary, visiblePlan);
                }

                if (!string.Equals(response.Kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    if (route.RequiresTool &&
                        !AgentPhaseController.IsRouteComplete(route, state.PendingVerification) &&
                        string.Equals(response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase) &&
                        !state.ToolCorrectionUsed)
                    {
                        state.ToolCorrectionUsed = true;
                        var forced = BuildPlannerCorrectionMessages(PromptText(settings, p => p.ForceToolUsePrompt), snapshot, route, slice, observations, documentContext, skills, settings, requestText, session, attachments, requestProtocolMessages, plannerAttempt.RequestOptions);
                        var correctionAttempt = await _plannerCompletion.CompleteAsync(
                            settings,
                            forced,
                            slice.Tools,
                            state,
                            plannerAttempt.RequestOptions,
                            progress,
                            "Подбираю доступное действие для задачи...",
                            "Повторно исправляю формат действия...",
                            PromptText(settings, p => p.RepairDecisionPrompt),
                            cancellationToken).ConfigureAwait(false);
                        contextUsage = correctionAttempt.ContextUsage;
                        AgentRunPresentation.RecordRecoveredPlannerResponses(session, correctionAttempt.RejectedResponses);
                        if (!correctionAttempt.ParseResult.Success)
                        {
                            assistantText = AgentRunPresentation.RecordPlannerFailure(session, correctionAttempt.Completion, correctionAttempt.Text, correctionAttempt.ParseResult, "Planner correction invalid");
                            break;
                        }
                        response = correctionAttempt.ParseResult.Response;
                        completion = correctionAttempt.Completion;
                        plannerAttempt = correctionAttempt;
                        plannerText = correctionAttempt.Text;
                    }

                    if (route.RequiresTool &&
                        !AgentPhaseController.IsRouteComplete(route, state.PendingVerification) &&
                        string.Equals(response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase))
                    {
                        assistantText = "Не удалось подобрать безопасное действие Office для этого запроса. Уточните объект или требуемое изменение.";
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "Действие Office не определено",
                            Subtitle = "planner",
                            Status = "failed",
                            ExecutionStatus = "required_tool_decision",
                            ResultMessage = assistantText,
                            DataJson = JsonConvert.SerializeObject(new { response = response, route = route.TaskType })
                        }));
                        break;
                    }

                    if (!string.Equals(response.Kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
                    {
                        assistantText = response.Message ?? string.Empty;
                        ReportPlanProgress(progress, AgentPlanStateService.ApplyTerminalDecision(state, response.Kind));
                        UpdatePendingTask(session, taskText, response, state);
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                            assistantText,
                            completion,
                            null,
                            response.DecisionSummary,
                            state.WorkingGoal));
                        break;
                    }
                }

                var requestedSteps = response.Tools == null || response.Tools.Count == 0
                    ? new[] { response.Tool }
                    : response.Tools.ToArray();
                var validations = requestedSteps
                    .Select(step => _actionValidator.Validate(step, slice, route, observations, allTools))
                    .ToList();
                var invalidStepIndex = validations.FindIndex(item => item == null || !item.Success);
                if (invalidStepIndex >= 0)
                {
                    var invalidValidation = validations[invalidStepIndex];
                    var invalidStep = invalidStepIndex >= 0 && invalidStepIndex < requestedSteps.Length
                        ? requestedSteps[invalidStepIndex]
                        : null;
                    var validationMessage = invalidValidation == null ? "Tool validation failed." : invalidValidation.Message;
                    var validationObservation = new AgentObservation
                    {
                        Id = "obs_validation_" + (observations.Count + 1),
                        ToolId = invalidStep == null ? string.Empty : invalidStep.ToolId,
                        Status = "error",
                        Summary = validationMessage,
                        Mutation = false,
                        RequiresVerification = false
                    };
                    observations.Add(validationObservation);
                    resultLog.Add(new { toolId = validationObservation.ToolId, success = false, status = "validation_failed", message = validationMessage });
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(validationMessage, completion, new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Planner validation",
                        Subtitle = validationObservation.ToolId,
                        Status = "failed",
                        ExecutionStatus = "validation_failed",
                        ResultMessage = validationMessage
                    }));
                    continue;
                }
                var commands = validations.Select(item => item.Command).ToList();
                if (commands.Count > 1)
                {
                    var batchValidationError = ValidateMultiToolBatch(commands, allTools, settings, maxToolSteps - state.TotalToolSteps);
                    if (!string.IsNullOrWhiteSpace(batchValidationError))
                    {
                        var validationObservation = new AgentObservation
                        {
                            Id = "obs_validation_" + (observations.Count + 1),
                            ToolId = string.Join(",", commands.Select(item => item.ToolId)),
                            Status = "error",
                            Summary = batchValidationError,
                            Mutation = false,
                            RequiresVerification = false
                        };
                        observations.Add(validationObservation);
                        resultLog.Add(new { toolId = validationObservation.ToolId, success = false, status = "multi_tool_batch_rejected", message = batchValidationError });
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(batchValidationError, completion, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "Пакет инструментов отклонён",
                            Subtitle = commands.Count + " calls",
                            Status = "failed",
                            ExecutionStatus = "multi_tool_batch_rejected",
                            ResultMessage = batchValidationError
                        }));
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    HtmlWorkspaceArtifactService.StampUncheckpointed(session, runMessageStart, session.ActiveHtmlArtifactId);
                    var batchActivity = AgentRunPresentation.CreateToolBatchActivity(commands, "planned");
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                        response.DecisionSummary,
                        completion,
                        batchActivity,
                        response.DecisionSummary,
                        state.WorkingGoal));
                    ReportProgress(progress, "plan", response.DecisionSummary, batchActivity);

                    var exchanges = new List<AgentToolExchange>();
                    var batchRetry = false;
                    var batchFailed = false;
                    for (var batchIndex = 0; batchIndex < commands.Count; batchIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var batchCommand = commands[batchIndex];
                        var batchTool = AgentToolCatalogResolver.Find(allTools, batchCommand.ToolId);
                        ReportPlanProgress(progress, AgentPlanStateService.BeginCurrent(session, state));
                        batchActivity.Status = settings.AutoRunToolCalls ? "running" : "waiting";
                        batchActivity.ExecutionStatus = batchActivity.Status;
                        batchActivity.Children[batchIndex] = AgentRunPresentation.CreateRunningActivity(
                            batchCommand,
                            settings.AutoRunToolCalls ? "running" : "waiting",
                            "tool");
                        ReportProgress(
                            progress,
                            settings.AutoRunToolCalls ? "executing" : "waiting",
                            "Инструмент " + (batchIndex + 1) + "/" + commands.Count + ": " + AgentRunPresentation.FriendlyToolAction(batchCommand),
                            batchActivity);

                        ToolResult batchResult;
                        if (state.TotalToolSteps >= maxToolSteps)
                        {
                            batchResult = ToolResult.Fail(
                                "Agent tool step limit exceeded: " + maxToolSteps + ".",
                                null,
                                "tool_step_limit_exceeded",
                                false);
                        }
                        else
                        {
                            state.TotalToolSteps += 1;
                            batchResult = settings.AutoRunToolCalls
                                ? _toolExecutor.Execute(batchCommand, allTools, settings, false, false, session, Math.Max(1, maxToolSteps - state.TotalToolSteps + 1), skills, cancellationToken)
                                : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + batchCommand.ToolId);
                        }
                        batchResult = batchResult ?? ToolResult.Fail("Tool returned no result.", null, "missing_result", false);
                        state.TotalToolSteps += Math.Max(0, (batchResult == null ? 0 : batchResult.ToolStepsConsumed) - 1);
                        var retryingBatchError = !batchResult.Success && settings.AutoRetryToolErrors && AgentTranscript.CanRetryToolError(batchResult);
                        ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, batchResult, retryingBatchError));
                        exchanges.Add(new AgentToolExchange(batchCommand, batchResult));
                        var batchPurpose = AgentObservationPurposes.Inspection;
                        AddToolObservation(batchCommand, batchTool, batchResult, observations, resultLog, session, batchPurpose, false);
                        AgentRunPresentation.UpdateToolBatchActivity(batchActivity, batchIndex, batchCommand, batchResult);
                        ReportProgress(
                            progress,
                            batchResult.Success ? "completed" : "failed",
                            batchResult.Message,
                            batchActivity);
                        batchRetry |= retryingBatchError;
                        batchFailed |= !batchResult.Success && !retryingBatchError;
                    }

                    AgentProtocolHistory.AppendToolExchanges(protocolMessages, session, plannerAttempt, exchanges, settings);
                    AgentPhaseController.Advance(route, observations, state.PendingVerification);
                    if (batchFailed) break;
                    if (batchRetry) continue;
                    continue;
                }

                var command = commands[0];
                var plannedActivity = AgentRunPresentation.CreateRunningActivity(command, "planned", "tool");
                plannedActivity.DataJson = routingDiagnosticsJson;
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                    response.DecisionSummary,
                    completion,
                    plannedActivity,
                    response.DecisionSummary,
                    state.WorkingGoal));
                ReportProgress(progress, "plan", response.DecisionSummary, plannedActivity);

                if (string.Equals(command.ToolId, "common.skills_load", StringComparison.OrdinalIgnoreCase))
                {
                    var loadResult = _toolExecutor.Execute(
                        command,
                        allTools,
                        settings,
                        false,
                        false,
                        session,
                        1,
                        skills,
                        cancellationToken);
                    AgentProtocolHistory.AppendToolExchange(protocolMessages, session, plannerAttempt, command, loadResult, settings);
                    AddToolObservation(command, AgentToolCatalogResolver.Find(allTools, command.ToolId), loadResult, observations, resultLog, session, AgentObservationPurposes.Inspection);
                    ReportProgress(progress, loadResult.Success ? "completed" : "failed", loadResult.Message, AgentTranscript.CreateToolActivity(command, loadResult, "control"));
                    if (!loadResult.Success && !settings.AutoRetryToolErrors) break;
                    continue;
                }

                var stopped = false;
                var continueAfterRecoverableError = false;
                cancellationToken.ThrowIfCancellationRequested();
                HtmlWorkspaceArtifactService.StampUncheckpointed(session, runMessageStart, session.ActiveHtmlArtifactId);
                if (state.TotalToolSteps >= maxToolSteps)
                {
                    var limitResult = ToolResult.Fail("Agent tool step limit exceeded: " + maxToolSteps + ".");
                    ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, limitResult, false));
                    resultLog.Add(new { toolId = "agent.step_limit", success = false, status = limitResult.Status, message = limitResult.Message });
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(limitResult.Message, null, new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Agent step limit",
                        Status = "failed",
                        ExecutionStatus = "step_limit",
                        ResultMessage = limitResult.Message
                    }));
                    break;
                }

                state.TotalToolSteps += 1;
                var tool = AgentToolCatalogResolver.Find(allTools, command.ToolId);
                if (tool == null)
                {
                    var unknown = ToolResult.Fail("Tool not found after planning validation: " + command.ToolId);
                    ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, unknown, false));
                    AddToolObservation(command, null, unknown, observations, resultLog, session);
                    break;
                }

                ReportPlanProgress(progress, AgentPlanStateService.BeginCurrent(session, state));
                ReportProgress(
                    progress,
                    settings.AutoRunToolCalls ? "executing" : "waiting",
                    settings.AutoRunToolCalls
                        ? "Выполняю: " + AgentRunPresentation.FriendlyToolAction(command) + "..."
                        : "Ожидаю ручного запуска: " + AgentRunPresentation.FriendlyToolAction(command) + ".",
                    AgentRunPresentation.CreateRunningActivity(command, settings.AutoRunToolCalls ? "running" : "waiting", "tool"));
                var result = settings.AutoRunToolCalls
                    ? _toolExecutor.Execute(command, allTools, settings, false, false, session, Math.Max(1, maxToolSteps - state.TotalToolSteps + 1), skills, cancellationToken)
                    : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + command.ToolId);
                state.TotalToolSteps += Math.Max(0, (result == null ? 0 : result.ToolStepsConsumed) - 1);
                var retryingToolError = !result.Success && settings.AutoRetryToolErrors && AgentTranscript.CanRetryToolError(result);
                ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, result, retryingToolError));
                AgentProtocolHistory.AppendToolExchange(protocolMessages, session, plannerAttempt, command, result, settings);
                AttachPendingId(session, command, result, pendingToolRegistrar);
                RefreshCreatedTool(allTools, command, result);
                var purpose = string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase)
                    ? AgentObservationPurposes.Verification
                    : tool.MutatesDocument || tool.MutatesLocalState
                        ? AgentObservationPurposes.Mutation
                        : AgentObservationPurposes.Inspection;
                var observation = AddToolObservation(command, tool, result, observations, resultLog, session, purpose);
                ReportProgress(progress, result.Success ? "completed" : (AgentTranscript.IsWaitingResult(result) ? "waiting" : "failed"), result.Message, AgentTranscript.CreateToolActivity(command, result, "tool"));

                if (!result.Success)
                {
                    if (retryingToolError)
                    {
                        continue;
                    }
                    break;
                }

                if (string.Equals(observation.Purpose, AgentObservationPurposes.Verification, StringComparison.OrdinalIgnoreCase))
                {
                    state.PendingVerification = false;
                    route.Phase = AgentPhases.Final;
                }

                if (tool.MutatesDocument && settings.RequireVerificationForMutations)
                {
                    state.PendingVerification = true;
                    route.Phase = AgentPhases.Verification;
                    var verificationCommands = _verificationRunner.BuildVerificationCommands(command, tool, allTools, result).ToList();
                    if (verificationCommands.Count == 0)
                    {
                        var unavailable = ToolResult.Fail("No deterministic verification tool is available for " + command.ToolId + ".");
                        ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, unavailable, settings.AutoRetryToolErrors));
                        AddToolObservation(
                            new ToolCommand { ToolId = "agent.verification", Description = "Deterministic verification" },
                            null,
                            unavailable,
                            observations,
                            resultLog,
                            session,
                            AgentObservationPurposes.Verification);
                        continueAfterRecoverableError = settings.AutoRetryToolErrors;
                        stopped = !continueAfterRecoverableError;
                    }
                    foreach (var verify in verificationCommands)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (state.TotalToolSteps >= maxToolSteps)
                        {
                            ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(
                                session,
                                state,
                                ToolResult.Fail("Agent tool step limit exceeded during verification."),
                                false));
                            stopped = true;
                            break;
                        }
                        state.TotalToolSteps += 1;
                        var verifyTool = AgentToolCatalogResolver.Find(allTools, verify.ToolId);
                        if (verifyTool == null)
                        {
                            var missing = ToolResult.Fail("Verification tool is unavailable: " + verify.ToolId);
                            ReportPlanProgress(progress, AgentPlanStateService.BeginCurrent(session, state));
                            ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, missing, settings.AutoRetryToolErrors));
                            AddToolObservation(verify, null, missing, observations, resultLog, session, AgentObservationPurposes.Verification);
                            continueAfterRecoverableError = settings.AutoRetryToolErrors;
                            stopped = !continueAfterRecoverableError;
                            break;
                        }
                        ReportPlanProgress(progress, AgentPlanStateService.BeginCurrent(session, state));
                        ReportProgress(progress, "verifying", "Проверяю результат через " + verify.ToolId, AgentRunPresentation.CreateRunningActivity(verify, "running", "verification"));
                        var verifyExecution = await _verificationExecutor.ExecuteAsync(
                            verify.ToolId,
                            () => _toolExecutor.Execute(verify, allTools, settings, false, false, session, Math.Max(1, maxToolSteps - state.TotalToolSteps + 1), skills, CancellationToken.None),
                            cancellationToken).ConfigureAwait(false);
                        state.TotalToolSteps += Math.Max(0, (verifyExecution.Result == null ? 0 : verifyExecution.Result.ToolStepsConsumed) - 1);
                        var verifyResult = VerificationResultValidator.Validate(command, verify, verifyExecution.Result);
                        var retryingVerification = !verifyResult.Success && !verifyExecution.TimedOut && settings.AutoRetryToolErrors;
                        ReportPlanProgress(progress, AgentPlanStateService.ApplyResult(session, state, verifyResult, retryingVerification));
                        AgentProtocolHistory.AppendToolExchange(protocolMessages, session, null, verify, verifyResult, settings);
                        AddToolObservation(verify, verifyTool, verifyResult, observations, resultLog, session, AgentObservationPurposes.Verification);
                        ReportProgress(progress, verifyResult.Success ? "completed" : "failed", verifyResult.Message, AgentTranscript.CreateToolActivity(verify, verifyResult, "verification"));
                        if (!verifyResult.Success)
                        {
                            if (verifyExecution.TimedOut)
                            {
                                stopped = true;
                                break;
                            }
                            continueAfterRecoverableError = settings.AutoRetryToolErrors;
                            stopped = !continueAfterRecoverableError;
                            break;
                        }
                        state.PendingVerification = false;
                    }
                    if (!state.PendingVerification)
                    {
                        route.Phase = AgentPhases.Final;
                    }
                }
                if (continueAfterRecoverableError)
                {
                    continue;
                }

                if (stopped)
                {
                    break;
                }

                AgentPhaseController.Advance(route, observations, state.PendingVerification);
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = resultLog.Count == 0 ? "Выполнение завершилось без итогового ответа модели." : AgentTranscript.CreateRunSummary(resultLog);
                RememberPendingTask(session, taskText, assistantText, "incomplete");
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, null));
            }

            return new ChatCompletionResult
            {
                AssistantText = assistantText,
                ToolResults = resultLog,
                ContextUsage = contextUsage ?? ContextUsageEstimator.FromSession(session, settings)
            };
        }

        private static void UpdatePendingTask(ChatSession session, string taskText, AgentPlannerResponse response, AgentRunState state)
        {
            if (session == null || response == null)
            {
                return;
            }

            if (string.Equals(response.Kind, AgentResponseKinds.Clarify, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(response.Kind, AgentResponseKinds.CannotComplete, StringComparison.OrdinalIgnoreCase))
            {
                RememberPendingTask(session, taskText, response.Message, response.Kind);
                return;
            }

            if (string.Equals(response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase))
            {
                if (AgentPlanStateService.HasUnfinishedSteps(state))
                {
                    RememberPendingTask(session, taskText, response.Message, "incomplete_plan");
                }
                else
                {
                    session.PendingAgentTask = null;
                }
            }
        }

        private static void RememberPendingTask(ChatSession session, string taskText, string lastQuestion, string kind)
        {
            if (session == null || string.IsNullOrWhiteSpace(taskText))
            {
                return;
            }

            var request = taskText.Trim();
            if (session.PendingAgentTask != null &&
                !string.IsNullOrWhiteSpace(session.PendingAgentTask.Request) &&
                request.StartsWith(session.PendingAgentTask.Request, StringComparison.Ordinal))
            {
                request = session.PendingAgentTask.Request;
            }

            session.PendingAgentTask = new PendingAgentTask
            {
                Request = request,
                LastQuestion = lastQuestion ?? string.Empty,
                Kind = kind ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private AgentObservation AddToolObservation(
            ToolCommand command,
            ToolDefinition tool,
            ToolResult result,
            ICollection<AgentObservation> observations,
            ICollection<object> resultLog,
            ChatSession session,
            string purpose = null,
            bool addTranscript = true)
        {
            var observation = _observationNormalizer.Normalize(command, tool, result, purpose);
            observations.Add(observation);
            resultLog.Add(AgentTranscript.DescribeResult(command, result));
            if (addTranscript) AgentTranscript.AddLocalResultMessage(session, command, result);
            return observation;
        }

        private static string ValidateMultiToolBatch(
            IReadOnlyList<ToolCommand> commands,
            IReadOnlyList<ToolDefinition> tools,
            AppSettings settings,
            int remainingToolSteps)
        {
            if (commands == null || commands.Count < 2) return null;
            if (commands.Count > AgentDecisionProtocol.MaxToolCallsPerDecision)
            {
                return "Пакет содержит слишком много вызовов: максимум " + AgentDecisionProtocol.MaxToolCallsPerDecision + ".";
            }
            if (remainingToolSteps < commands.Count)
            {
                return "Пакет не помещается в оставшийся лимит инструментов. Уменьшите число вызовов.";
            }
            foreach (var command in commands)
            {
                var tool = AgentToolCatalogResolver.Find(tools, command == null ? null : command.ToolId);
                var profile = ToolSafetyPolicy.Resolve(tool, tools);
                if (tool == null || profile == null || !profile.Valid)
                {
                    return "Пакет содержит недоступный инструмент: " + (command == null ? string.Empty : command.ToolId) + ".";
                }
                if (profile.MutatesDocument || profile.MutatesLocalState ||
                    ToolSafetyPolicy.RequiresConfirmation(tool, profile, settings, false, false))
                {
                    return "Multi-tool пакет разрешён только для независимых read-only действий без подтверждения. Выполните " + tool.Id + " отдельным решением.";
                }
            }
            return null;
        }

        private static string LatestUserRequest(ChatSession session, string fallback)
        {
            if (session != null && session.Messages != null)
            {
                for (var i = session.Messages.Count - 1; i >= 0; i--)
                {
                    var message = session.Messages[i];
                    if (message != null &&
                        string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(message.Content))
                    {
                        return message.Content;
                    }
                }
            }
            return fallback ?? string.Empty;
        }

        private List<ChatMessage> BuildPlannerCorrectionMessages(
            string correction,
            OfficeSnapshot snapshot,
            RoutedTask route,
            ToolCatalogSlice slice,
            IEnumerable<AgentObservation> observations,
            DocumentContext context,
            IEnumerable<SkillDefinition> skills,
            AppSettings settings,
            string taskText,
            ChatSession session,
            IReadOnlyList<ChatAttachment> attachments,
            IReadOnlyList<ChatMessage> protocolMessages,
            LlmRequestOptions requestOptions)
        {
            var messages = _plannerPromptComposer.BuildMessages(taskText, snapshot, route, slice, observations, context, skills, settings, session, attachments, protocolMessages, requestOptions);
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = correction ?? string.Empty
            });
            return messages;
        }

        private static string PromptText(AppSettings settings, Func<AgentPromptSettings, string> selector)
        {
            var defaults = new AgentPromptSettings();
            var prompts = settings == null || settings.AgentPrompts == null
                ? defaults
                : settings.AgentPrompts;
            var value = selector(prompts);
            return string.IsNullOrWhiteSpace(value) ? selector(defaults) : value;
        }

        private static IReadOnlyList<ChatMessage> WithPlanDecisionConstraint(
            IReadOnlyList<ChatMessage> protocolMessages,
            bool planDecisionAllowed)
        {
            var messages = new List<ChatMessage>(protocolMessages ?? new ChatMessage[0]);
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = planDecisionAllowed
                    ? "RUNTIME_DECISION_CONSTRAINT: kind=plan is available only for an initial complex-task plan or a material revision justified by a new runtime observation. Never return it merely to restate the current plan."
                    : "RUNTIME_DECISION_CONSTRAINT: the current plan is already declared and no newer runtime observation exists. kind=plan is unavailable. Continue with one tool decision, clarify, final, or cannot_complete."
            });
            return messages;
        }

        private static void AttachPendingId(ChatSession session, ToolCommand command, ToolResult result, ChatCompletionService.PendingToolRegistrar pendingToolRegistrar)
        {
            if (!AgentTranscript.IsWaitingResult(result) || pendingToolRegistrar == null)
            {
                return;
            }

            result.PendingId = pendingToolRegistrar(session, command, result);
        }

        private void RefreshCreatedTool(ICollection<ToolDefinition> tools, ToolCommand command, ToolResult result)
        {
            if (tools == null || command == null || result == null || !result.Success ||
                !string.Equals(command.ToolId, "common.tools_save", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.DataJson))
            {
                return;
            }

            try
            {
                var created = JsonConvert.DeserializeObject<ToolDefinition>(result.DataJson);
                if (created == null || string.IsNullOrWhiteSpace(created.Id))
                {
                    return;
                }
                _toolCatalogResolver.Refresh(tools, created);
            }
            catch (JsonException)
            {
            }
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            ReportProgress(progress, phase, message, null);
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message, activity);
            }
        }

        private static void ReportPlanProgress(Action<string, string, ChatActivity> progress, ChatActivity plan)
        {
            if (plan != null)
            {
                ReportProgress(progress, "plan_update", AgentPlanStateService.ProgressText(plan), AgentPlanStateService.Snapshot(plan));
            }
        }

    }
}
