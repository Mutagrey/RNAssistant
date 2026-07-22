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
        private readonly RecipeExpander _recipeExpander;
        private readonly AgentToolCatalogResolver _toolCatalogResolver;

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync,
            bool includeControllerTools = true)
            : this(
                adapter,
                toolExecutor,
                (settings, messages, streamProgress, cancellationToken) => completeAsync(settings, messages, cancellationToken),
                includeControllerTools)
        {
        }

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            ChatCompletionService.CompletionDelegate completeAsync,
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
            _recipeExpander = new RecipeExpander();
            _toolCatalogResolver = new AgentToolCatalogResolver(toolExecutor, includeControllerTools);
        }

        public async Task<ChatCompletionResult> RunUserTurnAsync(
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
            return await RunLoopAsync(taskText, null, false, session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken)
        {
            return await ContinueAfterToolAsync(
                confirmedCommand,
                session,
                documentContext,
                settings,
                tools,
                null,
                progress,
                pendingToolRegistrar,
                skills,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
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
            var prompt = BuildConfirmedToolContinuation(
                confirmedCommand,
                session,
                PromptText(settings, p => p.ConfirmedToolContinuationPrompt));
            var taskText = LatestUserRequest(session, prompt);
            return await RunLoopAsync(taskText, prompt, CommandMutates(confirmedCommand, tools), session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken).ConfigureAwait(false);
        }

        public bool CommandMutates(ToolCommand command, IReadOnlyList<ToolDefinition> tools)
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
            CancellationToken cancellationToken)
        {
            settings = settings ?? new AppSettings();
            return await RunControlledLoopAsync(taskText, initialFollowUpPrompt, initialVerificationRequired, session, documentContext, settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ChatCompletionResult> RunControlledLoopAsync(
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
            if (route.RequiresInspection || settings.IncludeVbaContext || LooksLikeVbaTask(taskText))
            {
                ReportProgress(progress, "context", "Собираю необходимый контекст Office...");
                snapshot = CaptureOfficeSnapshot(settings, taskText);
                route.App = FirstNonEmpty(snapshot.Host, route.App);
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

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = _toolCatalogSlicer.Slice(route, allTools, observations, settings.MaxAgentToolsPerRequest, settings.AllowAgentToolAuthoring == true);
                routingDiagnosticsJson = BuildRoutingDiagnosticsJson(route, slice);
                if (iteration == 0)
                {
                    ReportProgress(progress, "routing", BuildTaskProgressMessage(route, false), BuildRoutingActivity(route, slice));
                }
                if (route.RequiresTool && slice.Tools.Count == 0)
                {
                    assistantText = RecordMissingTools(session, route, slice);
                    RememberPendingTask(session, taskText, assistantText, AgentResponseKinds.CannotDo);
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
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", BuildTaskProgressMessage(route, true));
                var plannerAttempt = await _plannerCompletion.CompleteAsync(
                    settings,
                    messages,
                    state,
                    progress,
                    BuildTaskProgressMessage(route, true),
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
                    assistantText = RecordPlannerFailure(session, completion, plannerText, parsed, "Planner JSON invalid");
                    RememberPendingTask(session, taskText, assistantText, "planner_error");
                    break;
                }
                var response = parsed.Response;
                if (!string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase))
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
                            state,
                            progress,
                            "Подбираю доступное действие для задачи...",
                            "Повторно исправляю формат действия...",
                            PromptText(settings, p => p.RepairMalformedToolBlockPrompt),
                            cancellationToken).ConfigureAwait(false);
                        contextUsage = correctionAttempt.ContextUsage;
                        if (!correctionAttempt.ParseResult.Success)
                        {
                            assistantText = RecordPlannerFailure(session, correctionAttempt.Completion, correctionAttempt.Text, correctionAttempt.ParseResult, "Planner correction invalid");
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
                            ExecutionStatus = "required_tool_plan",
                            ResultMessage = assistantText,
                            DataJson = JsonConvert.SerializeObject(new { response = response, route = route.TaskType })
                        }));
                        break;
                    }

                    if (!string.Equals(response.Kind, AgentResponseKinds.ToolPlan, StringComparison.OrdinalIgnoreCase))
                    {
                        assistantText = response.Message ?? string.Empty;
                        UpdatePendingTask(session, taskText, response);
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
                        break;
                    }
                }

                var commands = new List<ToolCommand>();
                var validationFailed = false;
                foreach (var step in response.Steps)
                {
                    var validation = _actionValidator.Validate(step, slice, route, observations, allTools);
                    if (!validation.Success)
                    {
                        var observation = new AgentObservation
                        {
                            Id = "obs_validation_" + (observations.Count + 1),
                            ToolId = step == null ? string.Empty : step.ToolId,
                            Status = "error",
                            Summary = validation.Message,
                            Mutation = false,
                            RequiresVerification = false
                        };
                        observations.Add(observation);
                        resultLog.Add(new { toolId = observation.ToolId, success = false, status = "validation_failed", message = validation.Message });
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(validation.Message, completion, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "Planner validation",
                            Subtitle = observation.ToolId,
                            Status = "failed",
                            ExecutionStatus = "validation_failed",
                            ResultMessage = validation.Message
                        }));
                        validationFailed = true;
                        break;
                    }
                    commands.Add(validation.Command);
                }

                if (validationFailed)
                {
                    continue;
                }

                var batchValidationError = PlannerBatchPolicy.Validate(commands, allTools, route, settings);
                if (!string.IsNullOrWhiteSpace(batchValidationError))
                {
                    var observation = new AgentObservation
                    {
                        Id = "obs_batch_validation_" + (observations.Count + 1),
                        ToolId = "agent.plan_batch",
                        Status = "error",
                        Summary = batchValidationError,
                        Mutation = false,
                        RequiresVerification = false
                    };
                    observations.Add(observation);
                    resultLog.Add(new
                    {
                        toolId = observation.ToolId,
                        success = false,
                        status = "validation_failed",
                        message = batchValidationError
                    });
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(batchValidationError, completion, new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Planner batch validation",
                        Subtitle = commands.Count + " actions",
                        Status = "failed",
                        ExecutionStatus = "validation_failed",
                        ResultMessage = batchValidationError
                    }));
                    continue;
                }

                var planActivity = AgentTranscript.CreateAgentPlanActivity(commands);
                planActivity.Title = "План действий";
                planActivity.DataJson = routingDiagnosticsJson;
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(AgentTranscript.CreateAgentPlanMessage(commands), completion, planActivity));
                ReportProgress(progress, "plan", BuildPlanProgressMessage(commands), planActivity);

                var stopped = false;
                var continueAfterRecoverableError = false;
                foreach (var plannedCommand in commands)
                {
                    foreach (var command in _recipeExpander.Expand(plannedCommand, observations))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (command == null)
                        {
                            continue;
                        }
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
                            stopped = true;
                            break;
                        }

                        state.TotalToolSteps += 1;
                        var tool = AgentToolCatalogResolver.Find(allTools, command.ToolId);
                        if (tool == null)
                        {
                            var unknown = ToolResult.Fail("Tool not found after planning validation: " + command.ToolId);
                            AddToolObservation(command, null, unknown, observations, resultLog, session);
                            stopped = true;
                            break;
                        }

                        ReportProgress(
                            progress,
                            settings.AutoRunToolCalls != false ? "executing" : "waiting",
                            settings.AutoRunToolCalls != false
                                ? "Выполняю: " + FriendlyToolAction(command) + "..."
                                : "Ожидаю ручного запуска: " + FriendlyToolAction(command) + ".",
                            CreateRunningActivity(command, settings.AutoRunToolCalls != false ? "running" : "waiting", "tool"));
                        var result = settings.AutoRunToolCalls != false
                            ? _toolExecutor.Execute(command, allTools, settings, false, false, session, cancellationToken)
                            : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + command.ToolId);
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
                            if (settings.AutoRetryToolErrors != false && AgentTranscript.CanRetryToolError(result))
                            {
                                continueAfterRecoverableError = true;
                            }
                            else
                            {
                                stopped = true;
                            }
                            break;
                        }

                        if (string.Equals(observation.Purpose, AgentObservationPurposes.Verification, StringComparison.OrdinalIgnoreCase))
                        {
                            state.PendingVerification = false;
                            route.Phase = AgentPhases.Final;
                        }

                        if (tool.MutatesDocument && settings.RequireVerificationForMutations != false)
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
                                continueAfterRecoverableError = settings.AutoRetryToolErrors != false;
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
                                    continueAfterRecoverableError = settings.AutoRetryToolErrors != false;
                                    stopped = !continueAfterRecoverableError;
                                    break;
                                }
                                ReportProgress(progress, "verifying", "Проверяю результат через " + verify.ToolId, CreateRunningActivity(verify, "running", "verification"));
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
                                        continueAfterRecoverableError = false;
                                        stopped = true;
                                        break;
                                    }
                                    continueAfterRecoverableError = settings.AutoRetryToolErrors != false;
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
                    }
                    if (stopped)
                    {
                        break;
                    }
                    if (continueAfterRecoverableError)
                    {
                        break;
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
                string.Equals(response.Kind, AgentResponseKinds.CannotDo, StringComparison.OrdinalIgnoreCase))
            {
                RememberPendingTask(session, taskText, response.Message, response.Kind);
                return;
            }

            if (string.Equals(response.Kind, AgentResponseKinds.Final, StringComparison.OrdinalIgnoreCase))
            {
                session.PendingAgentTask = null;
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
            string purpose = null)
        {
            var observation = _observationNormalizer.Normalize(command, tool, result, purpose);
            observations.Add(observation);
            resultLog.Add(AgentTranscript.DescribeResult(command, result));
            AgentTranscript.AddLocalResultMessage(session, command, result);
            return observation;
        }

        private OfficeSnapshot CaptureOfficeSnapshot(AppSettings settings, string taskText)
        {
            var snapshot = new OfficeSnapshot
            {
                Host = _adapter.HostName,
                DocumentTitle = SafeRead(() => _adapter.DocumentTitle)
            };

            var contextProvider = _adapter as IOfficeContextProvider;
            if (contextProvider != null)
            {
                try
                {
                    var context = contextProvider.GetOfficeContext();
                    if (context != null)
                    {
                        snapshot.Host = FirstNonEmpty(context.Host, snapshot.Host);
                        snapshot.DocumentTitle = FirstNonEmpty(context.DocumentTitle, snapshot.DocumentTitle);
                        snapshot.ContainerName = context.ContainerName;
                        snapshot.SelectionAddress = context.SelectionAddress;
                        snapshot.SelectionText = context.SelectionText;
                    }
                }
                catch
                {
                }
            }

            var vba = CaptureVbaSnapshot(settings, taskText);
            if (!string.IsNullOrWhiteSpace(vba))
            {
                snapshot.SnapshotText = FirstNonEmpty(snapshot.SnapshotText, string.Empty) + "\n\nCurrent VBA project snapshot:\n" + vba;
            }
            return snapshot;
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
            builder.AppendLine("arguments: " + TrimDiagnosticText(
                JsonConvert.SerializeObject(command == null ? null : command.Arguments),
                2000));

            var activity = FindLatestToolActivity(session, command == null ? null : command.ToolId);
            if (activity != null)
            {
                builder.AppendLine("status: " + (activity.ExecutionStatus ?? activity.Status ?? string.Empty));
                builder.AppendLine("result: " + TrimDiagnosticText(activity.ResultMessage, 1200));
                if (!string.IsNullOrWhiteSpace(activity.DataJson))
                {
                    builder.AppendLine("data: " + TrimDiagnosticText(activity.DataJson, 2000));
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
                Content = correction + " " + PromptText(settings, p => p.ForceToolUsePrompt) + " Return kind=tool_plan with an available read/context tool, or kind=cannot_do if no tool can satisfy it."
            });
            return messages;
        }

        private static string SafeRead(Func<string> read)
        {
            try
            {
                return read == null ? string.Empty : read() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private static string RecordPlannerFailure(
            ChatSession session,
            LlmCompletionResult completion,
            string rawText,
            AgentPlannerParseResult parseResult,
            string title)
        {
            var assistantText = "Planner response is invalid: " +
                (parseResult == null ? "unknown" : parseResult.ErrorCode + ". " + parseResult.ErrorMessage);
            if (session != null)
            {
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, new ChatActivity
                {
                    Kind = "diagnostic",
                    Title = title,
                    Subtitle = "strict_json",
                    Status = "failed",
                    ExecutionStatus = parseResult == null ? "unknown" : parseResult.ErrorCode,
                    ResultMessage = "Модель вернула некорректный формат плана: " + (parseResult == null ? "unknown" : parseResult.ErrorCode) + ".",
                    DataJson = JsonConvert.SerializeObject(new
                    {
                        errorCode = parseResult == null ? "unknown" : parseResult.ErrorCode,
                        errorMessage = parseResult == null ? string.Empty : parseResult.ErrorMessage,
                        responsePreview = TrimDiagnosticText(rawText, 1200)
                    })
                }));
            }
            return assistantText;
        }

        private static string RecordMissingTools(
            ChatSession session,
            RoutedTask route,
            ToolCatalogSlice slice)
        {
            var host = route == null ? string.Empty : route.App;
            var assistantText = "Нет доступного локального инструмента для этого этапа задачи.";
            if (session != null)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = assistantText,
                    Activity = new ChatActivity
                    {
                        Kind = "diagnostic",
                        Title = "Tool routing",
                        Subtitle = route == null ? string.Empty : route.TaskType + " / " + route.Phase,
                        Status = "failed",
                        ExecutionStatus = "no_available_tools",
                        ResultMessage = "host=" + host + "; reason=" + (route == null ? string.Empty : route.DecisionReason),
                        DataJson = BuildRoutingDiagnosticsJson(route, slice)
                    }
                });
            }
            return assistantText;
        }

        private static ChatActivity BuildRoutingActivity(RoutedTask route, ToolCatalogSlice slice)
        {
            return new ChatActivity
            {
                Kind = "diagnostic",
                Title = BuildTaskProgressMessage(route, false).TrimEnd('.'),
                Subtitle = route == null ? string.Empty : route.Mode + " · " + route.TaskType,
                Status = "completed",
                ExecutionStatus = "routed",
                ResultMessage = route == null
                    ? string.Empty
                    : "phase=" + route.Phase + "; reason=" + route.DecisionReason + "; tools=" + (slice == null ? 0 : slice.Tools.Count),
                DataJson = BuildRoutingDiagnosticsJson(route, slice)
            };
        }

        private static string BuildTaskProgressMessage(RoutedTask route, bool active)
        {
            if (route == null)
            {
                return active ? "Анализирую задачу..." : "Проверяю доступные действия.";
            }

            if (!active)
            {
                if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TaskType, "macro_execution", StringComparison.OrdinalIgnoreCase))
                {
                    return "Проверяю доступные операции VBA.";
                }
                if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
                {
                    return "Проверяю доступные операции с графиками.";
                }
                return "Проверяю доступные действия для текущего документа.";
            }

            if (string.Equals(route.Phase, AgentPhases.Verification, StringComparison.OrdinalIgnoreCase))
            {
                return "Проверяю результат внесенных изменений...";
            }
            if (string.Equals(route.Phase, AgentPhases.ReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
                {
                    return "Изучаю существующие графики и их параметры...";
                }
                if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TaskType, "macro_execution", StringComparison.OrdinalIgnoreCase))
                {
                    return "Изучаю VBA-проект и доступные модули...";
                }
                return "Изучаю содержимое текущего документа...";
            }
            if (string.Equals(route.TaskType, "chart", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю изменения графика...";
            }
            if (string.Equals(route.TaskType, "vba", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю VBA-код и параметры модуля...";
            }
            if (string.Equals(route.TaskType, "formatting", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю форматирование документа...";
            }
            if (string.Equals(route.TaskType, "tool_authoring", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготавливаю описание нового инструмента...";
            }
            return "Подготавливаю изменение текущего документа...";
        }

        private static string BuildPlanProgressMessage(IReadOnlyList<ToolCommand> commands)
        {
            var actions = (commands ?? new ToolCommand[0])
                .Where(command => command != null)
                .Select(command => FriendlyToolAction(command))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(3)
                .ToArray();
            return actions.Length == 0
                ? "Перехожу к выполнению действия."
                : "Выполняю: " + string.Join("; ", actions) + ".";
        }

        private static string FriendlyToolAction(ToolCommand command)
        {
            if (command == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(command.Description))
            {
                return command.Description.Trim().TrimEnd('.');
            }

            switch ((command.ToolId ?? string.Empty).ToLowerInvariant())
            {
                case "excel.list_charts": return "проверяю список графиков";
                case "excel.get_chart": return "читаю параметры графика";
                case "excel.add_chart": return "создаю график";
                case "excel.update_chart": return "изменяю график";
                case "excel.delete_chart": return "удаляю график";
                case "excel.vba_read_project": return "читаю VBA-проект";
                case "excel.vba_read_module": return "читаю VBA-модуль";
                case "excel.insert_vba_module": return "создаю VBA-модуль";
                case "excel.vba_replace_module": return "обновляю VBA-модуль";
                case "excel.run_macro": return "запускаю макрос";
                default: return command.ToolId;
            }
        }

        private static string BuildRoutingDiagnosticsJson(RoutedTask route, ToolCatalogSlice slice)
        {
            var exclusions = slice == null || slice.Excluded == null
                ? new List<ToolExclusion>()
                : slice.Excluded;
            return JsonConvert.SerializeObject(new
            {
                route = route == null ? null : new
                {
                    app = route.App,
                    mode = route.Mode,
                    taskType = route.TaskType,
                    phase = route.Phase,
                    riskAllowed = route.RiskAllowed,
                    requiresTool = route.RequiresTool,
                    requiresInspection = route.RequiresInspection,
                    reason = route.DecisionReason
                },
                selectedTools = slice == null
                    ? new string[0]
                    : slice.Tools.Select(tool => tool.Id).ToArray(),
                selectedToolDetails = slice == null
                    ? new object[0]
                    : slice.Tools.Select(tool => new
                    {
                        toolId = tool.Id,
                        mutatesDocument = tool.MutatesDocument,
                        mutatesLocalState = tool.MutatesLocalState,
                        agentCanRun = tool.AgentCanRun,
                        requiresConfirmation = tool.RequiresConfirmation,
                        riskLevel = tool.RiskLevel
                    }).ToArray(),
                excludedCounts = exclusions
                    .GroupBy(item => item.Reason ?? "unknown", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                excludedTools = exclusions.Take(40).Select(item => new
                {
                    toolId = item.ToolId,
                    reason = item.Reason,
                    detail = item.Detail
                }).ToArray()
            });
        }

        private static string TrimDiagnosticText(string value, int maxChars)
        {
            value = value ?? string.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "\n[truncated]";
        }

        private string CaptureVbaSnapshot(AppSettings settings, string taskText)
        {
            settings = settings ?? new AppSettings();
            if (!settings.IncludeVbaContext && !LooksLikeVbaTask(taskText))
            {
                return string.Empty;
            }

            try
            {
                return _adapter.GetVbaSnapshot(Math.Max(1000, settings.VbaContextCharLimit));
            }
            catch (Exception ex)
            {
                return "VBA project snapshot could not be read: " + ex.Message;
            }
        }

        private static bool LooksLikeVbaTask(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return value.IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макрос", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макро", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("visual basic", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private static ChatActivity CreateRunningActivity(ToolCommand command, string status, string kind)
        {
            return new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = command == null ? "Действие" : FriendlyToolAction(command),
                Subtitle = command == null ? string.Empty : command.ToolId,
                Status = status,
                ExecutionStatus = status,
                ToolId = command == null ? string.Empty : command.ToolId,
                ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented)
            };
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
