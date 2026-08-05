using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            bool includeControllerTools = true)
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
            var taskText = AgentTaskContinuationResolver.Resolve(text, session);
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text,
                    Attachments = attachments == null ? new List<ChatAttachment>() : new List<ChatAttachment>(attachments)
                });
            }
            return RunLoopAsync(taskText, null, false, session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, null, cancellationToken);
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
                AgentProtocolHistory.AppendToolExchange(initialProtocolMessages, null, confirmedCommand, confirmedResult, settings);
            }
            var prompt = BuildConfirmedToolContinuation(
                confirmedCommand,
                session,
                PromptText(settings, p => p.ConfirmedToolContinuationPrompt));
            var taskText = LatestUserRequest(session, prompt);
            return RunLoopAsync(taskText, prompt, CommandMutates(confirmedCommand, tools), session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, initialProtocolMessages, cancellationToken);
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
            string initialFollowUpPrompt,
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
            var state = new AgentRunState { PendingVerification = initialVerificationRequired };
            var routingDiagnosticsJson = string.Empty;
            var protocolMessages = new List<ChatMessage>(initialProtocolMessages ?? new ChatMessage[0]);

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = _toolCatalogSlicer.Slice(route, allTools, observations, settings.MaxAgentToolsPerRequest, settings.AllowAgentToolAuthoring);
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
                var requestText = string.IsNullOrWhiteSpace(initialFollowUpPrompt)
                    ? taskText
                    : taskText + "\n\nContinuation: " + initialFollowUpPrompt;
                var messages = _plannerPromptComposer.BuildMessages(
                    requestText,
                    snapshot,
                    route,
                    slice,
                    observations,
                    documentContext,
                    skills,
                    settings,
                    session,
                    attachments);
                messages.AddRange(protocolMessages);
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", AgentRunPresentation.BuildTaskProgressMessage(route, true));
                var plannerAttempt = await _plannerCompletion.CompleteAsync(
                    settings,
                    messages,
                    slice.Tools,
                    state,
                    progress,
                    AgentRunPresentation.BuildTaskProgressMessage(route, true),
                    "Исправляю формат следующего действия...",
                    PromptText(settings, p => p.RepairMalformedToolBlockPrompt),
                    cancellationToken).ConfigureAwait(false);
                contextUsage = plannerAttempt.ContextUsage;
                cancellationToken.ThrowIfCancellationRequested();
                var completion = plannerAttempt.Completion;
                var plannerText = plannerAttempt.Text;
                var parsed = plannerAttempt.ParseResult;

                if (!parsed.Success)
                {
                    assistantText = AgentRunPresentation.RecordPlannerFailure(session, completion, plannerText, parsed, "Planner JSON invalid");
                    RememberPendingTask(session, taskText, assistantText, "planner_error");
                    break;
                }
                var response = parsed.Response;
                if (string.Equals(response.Kind, AgentResponseKinds.Plan, StringComparison.OrdinalIgnoreCase))
                {
                    if (state.PlanDeclared)
                    {
                        assistantText = "Модель повторно вернула план вместо следующего решения.";
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "Повторный plan decision",
                            Status = "failed",
                            ExecutionStatus = "repeated_plan",
                            ResultMessage = assistantText
                        }));
                        break;
                    }
                    state.PlanDeclared = true;
                    state.WorkingGoal = response.Goal;
                    state.Plan = response.Plan ?? new List<AgentPlanStep>();
                    var visiblePlan = CreateDecisionPlanActivity(response);
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(response.DecisionSummary, completion, visiblePlan));
                    ReportProgress(progress, "plan", response.DecisionSummary, visiblePlan);
                    protocolMessages.Add(new ChatMessage { Role = "assistant", Content = plannerText });
                    protocolMessages.Add(new ChatMessage { Role = "user", Content = "Continue with the next AgentDecision for this plan." });
                    continue;
                }

                if (!string.Equals(response.Kind, AgentResponseKinds.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    if (route.RequiresTool &&
                        !AgentPhaseController.IsRouteComplete(route, state.PendingVerification) &&
                        string.Equals(response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase) &&
                        !state.ToolCorrectionUsed)
                    {
                        state.ToolCorrectionUsed = true;
                        var forced = BuildPlannerCorrectionMessages("This task requires Office tool use before a final answer.", snapshot, route, slice, observations, documentContext, skills, settings, requestText, session, attachments);
                        var correctionAttempt = await _plannerCompletion.CompleteAsync(
                            settings,
                            forced,
                            slice.Tools,
                            state,
                            progress,
                            "Подбираю доступное действие для задачи...",
                            "Повторно исправляю формат действия...",
                            PromptText(settings, p => p.RepairMalformedToolBlockPrompt),
                            cancellationToken).ConfigureAwait(false);
                        contextUsage = correctionAttempt.ContextUsage;
                        if (!correctionAttempt.ParseResult.Success)
                        {
                            assistantText = AgentRunPresentation.RecordPlannerFailure(session, correctionAttempt.Completion, correctionAttempt.Text, correctionAttempt.ParseResult, "Planner correction invalid");
                            break;
                        }
                        response = correctionAttempt.ParseResult.Response;
                        completion = correctionAttempt.Completion;
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
                        UpdatePendingTask(session, taskText, response);
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
                        break;
                    }
                }

                var step = response.Tool;
                var validation = _actionValidator.Validate(step, slice, route, observations, allTools);
                if (!validation.Success)
                {
                    var validationObservation = new AgentObservation
                    {
                        Id = "obs_validation_" + (observations.Count + 1),
                        ToolId = step == null ? string.Empty : step.ToolId,
                        Status = "error",
                        Summary = validation.Message,
                        Mutation = false,
                        RequiresVerification = false
                    };
                    observations.Add(validationObservation);
                    resultLog.Add(new { toolId = validationObservation.ToolId, success = false, status = "validation_failed", message = validation.Message });
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(validation.Message, completion, new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Planner validation",
                        Subtitle = validationObservation.ToolId,
                        Status = "failed",
                        ExecutionStatus = "validation_failed",
                        ResultMessage = validation.Message
                    }));
                    continue;
                }
                var command = validation.Command;
                var plannedActivity = AgentRunPresentation.CreateRunningActivity(command, "planned", "tool");
                plannedActivity.Title = response.DecisionSummary;
                plannedActivity.DataJson = routingDiagnosticsJson;
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(response.DecisionSummary, completion, plannedActivity));
                ReportProgress(progress, "plan", response.DecisionSummary, plannedActivity);

                var stopped = false;
                var continueAfterRecoverableError = false;
                cancellationToken.ThrowIfCancellationRequested();
                if (state.TotalToolSteps >= maxToolSteps)
                {
                    var limitResult = ToolResult.Fail("Agent tool step limit exceeded: " + maxToolSteps + ".");
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
                    AddToolObservation(command, null, unknown, observations, resultLog, session);
                    break;
                }

                ReportProgress(
                    progress,
                    settings.AutoRunToolCalls ? "executing" : "waiting",
                    settings.AutoRunToolCalls
                        ? "Выполняю: " + AgentRunPresentation.FriendlyToolAction(command) + "..."
                        : "Ожидаю ручного запуска: " + AgentRunPresentation.FriendlyToolAction(command) + ".",
                    AgentRunPresentation.CreateRunningActivity(command, settings.AutoRunToolCalls ? "running" : "waiting", "tool"));
                var result = settings.AutoRunToolCalls
                    ? _toolExecutor.Execute(command, allTools, settings, false, false, session, cancellationToken)
                    : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + command.ToolId);
                UpdateRuntimePlan(session, state, result);
                AgentProtocolHistory.AppendToolExchange(protocolMessages, plannerAttempt, command, result, settings);
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
                    if (settings.AutoRetryToolErrors && AgentTranscript.CanRetryToolError(result))
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
                            stopped = true;
                            break;
                        }
                        state.TotalToolSteps += 1;
                        var verifyTool = AgentToolCatalogResolver.Find(allTools, verify.ToolId);
                        if (verifyTool == null)
                        {
                            var missing = ToolResult.Fail("Verification tool is unavailable: " + verify.ToolId);
                            AddToolObservation(verify, null, missing, observations, resultLog, session, AgentObservationPurposes.Verification);
                            continueAfterRecoverableError = settings.AutoRetryToolErrors;
                            stopped = !continueAfterRecoverableError;
                            break;
                        }
                        ReportProgress(progress, "verifying", "Проверяю результат через " + verify.ToolId, AgentRunPresentation.CreateRunningActivity(verify, "running", "verification"));
                        var verifyExecution = await _verificationExecutor.ExecuteAsync(
                            verify.ToolId,
                            () => _toolExecutor.Execute(verify, allTools, settings, false, false, session, CancellationToken.None),
                            cancellationToken).ConfigureAwait(false);
                        var verifyResult = VerificationResultValidator.Validate(command, verify, verifyExecution.Result);
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

        private static void UpdatePendingTask(ChatSession session, string taskText, AgentPlannerResponse response)
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
                session.PendingAgentTask = null;
            }
        }

        private static ChatActivity CreateDecisionPlanActivity(AgentPlannerResponse response)
        {
            var activity = new ChatActivity
            {
                Kind = "plan",
                Title = string.IsNullOrWhiteSpace(response == null ? null : response.Goal) ? "Рабочий план" : response.Goal,
                Subtitle = response == null ? string.Empty : response.DecisionSummary,
                Status = "planned",
                DataJson = JsonConvert.SerializeObject(new
                {
                    protocolVersion = AgentDecisionProtocol.Version,
                    goal = response == null ? null : response.Goal,
                    plan = response == null ? null : response.Plan
                })
            };
            foreach (var step in response == null
                ? (IEnumerable<AgentPlanStep>)new AgentPlanStep[0]
                : response.Plan ?? new List<AgentPlanStep>())
            {
                activity.Children.Add(new ChatActivity
                {
                    Kind = "plan_step",
                    Title = step.Title,
                    Subtitle = step.Id,
                    Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status
                });
            }
            return activity;
        }

        private static void UpdateRuntimePlan(ChatSession session, AgentRunState state, ToolResult result)
        {
            if (state == null || state.Plan == null || state.Plan.Count == 0) return;
            var step = state.Plan.FirstOrDefault(item => item != null && string.Equals(item.Status, "pending", StringComparison.OrdinalIgnoreCase));
            if (step == null) return;
            step.Status = result != null && result.Success ? "completed" : "failed";
            var planActivity = session == null || session.Messages == null
                ? null
                : session.Messages.Select(message => message == null ? null : message.Activity)
                    .LastOrDefault(activity => activity != null && string.Equals(activity.Kind, "plan", StringComparison.OrdinalIgnoreCase));
            var activityStep = planActivity == null
                ? null
                : (planActivity.Children ?? new List<ChatActivity>()).FirstOrDefault(item =>
                    item != null && string.Equals(item.Subtitle, step.Id, StringComparison.OrdinalIgnoreCase));
            if (activityStep != null) activityStep.Status = step.Status;
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
            string purpose = null)
        {
            var observation = _observationNormalizer.Normalize(command, tool, result, purpose);
            observations.Add(observation);
            resultLog.Add(AgentTranscript.DescribeResult(command, result));
            AgentTranscript.AddLocalResultMessage(session, command, result);
            return observation;
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

        private static string BuildConfirmedToolContinuation(ToolCommand command, ChatSession session, string prompt)
        {
            var builder = new StringBuilder();
            builder.AppendLine(prompt ?? string.Empty);
            builder.AppendLine("Confirmed tool:");
            builder.AppendLine("toolId: " + (command == null ? string.Empty : command.ToolId));
            builder.AppendLine("arguments: " + AgentText.Truncate(
                JsonConvert.SerializeObject(command == null ? null : command.Arguments),
                2000));

            var activity = FindLatestToolActivity(session, command == null ? null : command.ToolId);
            if (activity != null)
            {
                builder.AppendLine("status: " + (activity.ExecutionStatus ?? activity.Status ?? string.Empty));
                builder.AppendLine("result: " + AgentText.Truncate(activity.ResultMessage, 1200));
                if (!string.IsNullOrWhiteSpace(activity.DataJson))
                {
                    builder.AppendLine("data: " + AgentText.Truncate(activity.DataJson, 2000));
                }
            }
            return builder.ToString().Trim();
        }

        private static ChatActivity FindLatestToolActivity(ChatSession session, string toolId)
        {
            if (session == null || session.Messages == null || string.IsNullOrWhiteSpace(toolId))
            {
                return null;
            }
            for (var index = session.Messages.Count - 1; index >= 0; index--)
            {
                var found = FindToolActivity(session.Messages[index] == null ? null : session.Messages[index].Activity, toolId);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static ChatActivity FindToolActivity(ChatActivity activity, string toolId)
        {
            if (activity == null)
            {
                return null;
            }
            if (string.Equals(activity.ToolId, toolId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(activity.Status, "planned", StringComparison.OrdinalIgnoreCase))
            {
                return activity;
            }
            foreach (var child in activity.Children ?? new List<ChatActivity>())
            {
                var found = FindToolActivity(child, toolId);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
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
            IReadOnlyList<ChatAttachment> attachments)
        {
            var messages = _plannerPromptComposer.BuildMessages(taskText, snapshot, route, slice, observations, context, skills, settings, session, attachments);
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = correction + " " + PromptText(settings, p => p.ForceToolUsePrompt) + " Return kind=tool with one available read/context tool, or kind=cannot_complete if no tool can satisfy it."
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

    }
}
