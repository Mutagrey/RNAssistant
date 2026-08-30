using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RNAssistant.Office.Qualification
{
    public sealed class QualificationRunner
    {
        private sealed class QualificationOperationStillRunningException : InvalidOperationException
        {
            internal QualificationOperationStillRunningException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            internal string Code { get; private set; }
        }

        private readonly IQualificationActionExecutor _actions;
        private readonly IQualificationVerifier _verifier;
        private readonly IQualificationRunJournal _journal;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string> _newId;

        public QualificationRunner(IQualificationActionExecutor actions, IQualificationVerifier verifier,
            IQualificationRunJournal journal)
            : this(actions, verifier, journal, () => DateTime.UtcNow, () => Guid.NewGuid().ToString("N"))
        {
        }

        internal QualificationRunner(IQualificationActionExecutor actions, IQualificationVerifier verifier,
            IQualificationRunJournal journal, Func<DateTime> utcNow, Func<string> newId)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _newId = newId ?? throw new ArgumentNullException(nameof(newId));
        }

        public QualificationRunState Start(QualificationPack pack, QualificationRunContext context,
            string previousRunId = null)
        {
            Preflight(pack, context);
            if (previousRunId != null && (string.IsNullOrWhiteSpace(previousRunId) || previousRunId.Length > 96))
                throw new ArgumentException("Previous qualification run id is invalid.", nameof(previousRunId));
            var run = new QualificationRunState(NewId("run"), previousRunId, pack, context, _utcNow())
            {
                Status = QualificationRunStatus.Running,
                Restorable = true
            };
            try
            {
                var receipt = _journal.Append(QualificationRunEventKind.RunStarted,
                    Event(run, QualificationRunEventKind.RunStarted, null));
                run.StartedEventId = receipt.EventId;
                run.StartedSequence = receipt.Sequence;
            }
            catch (Exception ex)
            {
                run.Status = QualificationRunStatus.Blocked;
                run.Restorable = false;
                throw new QualificationPersistenceException(
                    "Qualification run did not start because its mandatory start event was not persisted.", run, ex);
            }
            return run;
        }

        public async Task<QualificationRunState> AdvanceAsync(QualificationRunState run,
            QualificationManualInput manualInput, CancellationToken cancellationToken)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (run.TerminalPersisted) return run;
            if (!run.Restorable)
                throw new InvalidOperationException("Qualification run has an open automatic step and cannot be resumed or retried in place.");
            if (run.Status != QualificationRunStatus.Running && run.Status != QualificationRunStatus.AwaitingUser &&
                run.Status != QualificationRunStatus.Verifying)
                throw new InvalidOperationException("Qualification run is not active.");

            if (cancellationToken.IsCancellationRequested)
            {
                run.PendingTerminalStatus = QualificationRunStatus.Cancelled;
                var openUserStep = run.MutableSteps.FirstOrDefault(step =>
                    step.Outcome == QualificationStepOutcome.AwaitingUser &&
                    step.Kind == QualificationStepKind.UserAction);
                if (openUserStep != null)
                {
                    Apply(openUserStep, QualificationStepOutcome.Cancelled, QualificationEvidenceStrength.None,
                        "cancelled", "Run was cancelled at a user checkpoint.", null, null, null);
                    var openIndex = run.MutableSteps.IndexOf(openUserStep);
                    CompleteStep(run, openIndex);
                    run.CurrentStepIndex = openIndex + 1;
                }
            }

            var consumedManualInput = false;
            while (run.CurrentStepIndex < run.Pack.Steps.Count)
            {
                var index = run.CurrentStepIndex;
                var step = run.Pack.Steps[index];
                var snapshot = run.MutableSteps[index];

                if (cancellationToken.IsCancellationRequested && !run.PendingTerminalStatus.HasValue)
                    run.PendingTerminalStatus = QualificationRunStatus.Cancelled;

                if (run.PendingTerminalStatus.HasValue && step.Kind != QualificationStepKind.Cleanup)
                {
                    run.CurrentStepIndex++;
                    continue;
                }

                if (snapshot.Outcome == QualificationStepOutcome.Passed ||
                    snapshot.Outcome == QualificationStepOutcome.Failed ||
                    snapshot.Outcome == QualificationStepOutcome.Blocked ||
                    snapshot.Outcome == QualificationStepOutcome.Cancelled ||
                    snapshot.Outcome == QualificationStepOutcome.Unknown)
                {
                    run.CurrentStepIndex++;
                    continue;
                }

                if (snapshot.Outcome == QualificationStepOutcome.Running)
                {
                    run.Status = QualificationRunStatus.Blocked;
                    run.Restorable = false;
                    return run;
                }

                QualificationStepOutcome dependencyOutcome;
                if (step.Kind != QualificationStepKind.Cleanup &&
                    snapshot.Outcome == QualificationStepOutcome.NotRun &&
                    MissingDependency(run, step, out dependencyOutcome))
                {
                    BeginStep(run, index, false);
                    Apply(snapshot, QualificationStepOutcome.Blocked, QualificationEvidenceStrength.None,
                        "dependency_not_passed", "A required dependency did not pass.", null,
                        new JObject { ["dependencyOutcome"] = QualificationManifestParser.OutcomeName(dependencyOutcome) }
                            .ToString(Formatting.None), null);
                    SetPendingTerminal(run, QualificationRunStatus.Blocked);
                    CompleteStep(run, index);
                    run.CurrentStepIndex++;
                    continue;
                }

                if (step.Kind == QualificationStepKind.UserAction)
                {
                    if (snapshot.Outcome == QualificationStepOutcome.NotRun)
                    {
                        BeginStep(run, index, true);
                    }
                    if (consumedManualInput || manualInput == null || !manualInput.Acknowledged ||
                        !string.Equals(manualInput.StepId, step.Id, StringComparison.Ordinal))
                    {
                        run.Status = QualificationRunStatus.AwaitingUser;
                        return run;
                    }
                    consumedManualInput = true;
                    var actual = new JObject
                    {
                        ["acknowledged"] = true,
                        ["note"] = manualInput.Note == null ? JValue.CreateNull() : new JValue(Bound(manualInput.Note, 2000))
                    }.ToString(Formatting.None);
                    Apply(snapshot, QualificationStepOutcome.Passed, QualificationEvidenceStrength.Manual,
                        "acknowledged", "User action was acknowledged.", null, actual, null);
                    CompleteStep(run, index);
                    run.CurrentStepIndex++;
                    run.Status = QualificationRunStatus.Running;
                    continue;
                }

                BeginStep(run, index, false);
                var executionContext = new QualificationStepExecutionContext(run.RunId, snapshot.AttemptId,
                    run.Pack, step, run.Context);
                try
                {
                    if (step.Kind == QualificationStepKind.Assertion)
                    {
                        run.Status = QualificationRunStatus.Verifying;
                        var stepCancellation = run.PendingTerminalStatus.HasValue && step.Kind == QualificationStepKind.Cleanup
                            ? CancellationToken.None : cancellationToken;
                        var result = await VerifyAsync(executionContext, step.TimeoutSeconds, stepCancellation)
                            .ConfigureAwait(false);
                        ApplyVerification(snapshot, result);
                    }
                    else
                    {
                        run.Status = QualificationRunStatus.Running;
                        var stepCancellation = run.PendingTerminalStatus.HasValue && step.Kind == QualificationStepKind.Cleanup
                            ? CancellationToken.None : cancellationToken;
                        var result = await ExecuteAsync(executionContext, step.TimeoutSeconds, stepCancellation)
                            .ConfigureAwait(false);
                        ApplyAction(snapshot, result);
                    }
                }
                catch (QualificationOperationStillRunningException ex)
                {
                    Apply(snapshot, QualificationStepOutcome.Unknown, QualificationEvidenceStrength.None,
                        ex.Code, ex.Message, null, null, "unknown");
                    run.Status = QualificationRunStatus.Blocked;
                    run.Restorable = false;
                    return run;
                }

                if (snapshot.Outcome != QualificationStepOutcome.Passed && (step.Required || step.Kind == QualificationStepKind.Cleanup))
                    SetPendingTerminal(run, TerminalStatus(snapshot.Outcome));
                CompleteStep(run, index);
                run.CurrentStepIndex++;
                run.Status = QualificationRunStatus.Running;
            }
            return Complete(run);
        }

        public QualificationRunState Restore(QualificationPack pack, QualificationRunContext context, string runId)
        {
            Preflight(pack, context);
            var records = _journal.Read(runId);
            if (records.Count == 0 || records[0].Kind != QualificationRunEventKind.RunStarted)
                throw new InvalidOperationException("Qualification run has no durable start record.");
            ValidateIdentity(records, pack, context, runId);
            var start = records[0].Data;
            var run = new QualificationRunState(runId, start.PreviousRunId, pack, context, start.RecordedUtc)
            {
                Status = QualificationRunStatus.Running,
                Restorable = true
            };
            run.StartedEventId = records[0].Receipt.EventId;
            run.StartedSequence = records[0].Receipt.Sequence;
            var sawTerminal = false;
            var openStepIndex = -1;
            for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
            {
                var record = records[recordIndex];
                if (sawTerminal) throw new InvalidOperationException("Qualification run contains records after its terminal event.");
                if (record.Kind == QualificationRunEventKind.RunStarted)
                    throw new InvalidOperationException("Qualification run contains more than one start event.");
                if (record.Data.PendingTerminalStatus != null)
                {
                    var pending = QualificationManifestParser.ParseRunStatus(record.Data.PendingTerminalStatus);
                    if (run.PendingTerminalStatus.HasValue && run.PendingTerminalStatus.Value != pending)
                        throw new InvalidOperationException("Qualification terminal intent changed during replay.");
                    run.PendingTerminalStatus = pending;
                }
                if (record.Kind == QualificationRunEventKind.RunCompleted)
                {
                    if (openStepIndex >= 0)
                        throw new InvalidOperationException("Qualification run ended with an open step.");
                    if ((!run.PendingTerminalStatus.HasValue && run.CurrentStepIndex != pack.Steps.Count) ||
                        (run.PendingTerminalStatus.HasValue && pack.Steps.Skip(run.CurrentStepIndex)
                            .Any(step => step.Kind == QualificationStepKind.Cleanup)))
                        throw new InvalidOperationException("Qualification run ended before its required remaining steps.");
                    run.Status = QualificationManifestParser.ParseRunStatus(record.Data.RunStatus);
                    if (!IsTerminal(run.Status))
                        throw new InvalidOperationException("Qualification terminal event has a non-terminal status.");
                    run.CompletedUtc = record.Data.RecordedUtc;
                    run.CompletedEventId = record.Receipt.EventId;
                    run.CompletedSequence = record.Receipt.Sequence;
                    run.TerminalPersisted = true;
                    run.Restorable = false;
                    sawTerminal = true;
                    continue;
                }
                var index = record.Data.StepIndex.GetValueOrDefault(-1);
                if (index < 0 || index >= pack.Steps.Count ||
                    !string.Equals(pack.Steps[index].Id, record.Data.StepId, StringComparison.Ordinal) ||
                    !string.Equals(QualificationManifestParser.StepKindName(pack.Steps[index].Kind),
                        record.Data.StepKind, StringComparison.Ordinal))
                    throw new InvalidOperationException("Qualification event references a different pack step.");
                var snapshot = run.MutableSteps[index];
                if (record.Kind == QualificationRunEventKind.StepStarted)
                {
                    var skippedToCleanup = run.PendingTerminalStatus.HasValue &&
                        pack.Steps[index].Kind == QualificationStepKind.Cleanup &&
                        index >= run.CurrentStepIndex && pack.Steps.Skip(run.CurrentStepIndex)
                            .Take(index - run.CurrentStepIndex).All(step => step.Kind != QualificationStepKind.Cleanup);
                    if (openStepIndex >= 0 || (index != run.CurrentStepIndex && !skippedToCleanup))
                        throw new InvalidOperationException("Qualification step order is invalid.");
                    if (snapshot.Outcome != QualificationStepOutcome.NotRun)
                        throw new InvalidOperationException("Qualification step was started more than once.");
                    snapshot.AttemptId = record.Data.AttemptId;
                    snapshot.Outcome = QualificationManifestParser.ParseOutcome(record.Data.StepOutcome);
                    if (snapshot.Outcome != QualificationStepOutcome.Running &&
                        snapshot.Outcome != QualificationStepOutcome.AwaitingUser)
                        throw new InvalidOperationException("Qualification start event has an invalid step outcome.");
                    snapshot.StartedEventId = record.Receipt.EventId;
                    snapshot.StartedSequence = record.Receipt.Sequence;
                    run.CurrentStepIndex = index;
                    openStepIndex = index;
                    continue;
                }
                if (openStepIndex != index || snapshot.Outcome != QualificationStepOutcome.Running &&
                    snapshot.Outcome != QualificationStepOutcome.AwaitingUser ||
                    !string.Equals(snapshot.AttemptId, record.Data.AttemptId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Qualification completion has no matching start event.");
                snapshot.Outcome = QualificationManifestParser.ParseOutcome(record.Data.StepOutcome);
                snapshot.EvidenceStrength = ParseEvidenceStrength(record.Data.EvidenceStrength);
                snapshot.Code = record.Data.Code;
                snapshot.Message = record.Data.Message;
                snapshot.ExpectedJson = record.Data.ExpectedJson;
                snapshot.ActualJson = record.Data.ActualJson;
                snapshot.DomainEffect = record.Data.DomainEffect;
                snapshot.CompletedEventId = record.Receipt.EventId;
                snapshot.CompletedSequence = record.Receipt.Sequence;
                openStepIndex = -1;
                if (snapshot.Outcome != QualificationStepOutcome.Passed &&
                    (pack.Steps[index].Required || pack.Steps[index].Kind == QualificationStepKind.Cleanup))
                    SetPendingTerminal(run, TerminalStatus(snapshot.Outcome));
                run.CurrentStepIndex = index + 1;
            }
            if (sawTerminal) return run;
            var open = run.MutableSteps.FirstOrDefault(item => item.Outcome == QualificationStepOutcome.Running ||
                item.Outcome == QualificationStepOutcome.AwaitingUser);
            if (open != null && open.Outcome == QualificationStepOutcome.AwaitingUser &&
                open.Kind == QualificationStepKind.UserAction)
            {
                run.Status = QualificationRunStatus.AwaitingUser;
                run.CurrentStepIndex = run.MutableSteps.IndexOf(open);
                return run;
            }
            if (open != null)
            {
                run.Status = QualificationRunStatus.Blocked;
                run.Restorable = false;
                return run;
            }
            run.Status = QualificationRunStatus.Running;
            return run;
        }

        private void Preflight(QualificationPack pack, QualificationRunContext context)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!pack.Hosts.Contains("*") && !pack.Hosts.Contains(context.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Qualification pack does not support host " + context.Host + ".");
            var capabilities = new HashSet<string>(context.Capabilities, StringComparer.OrdinalIgnoreCase);
            var missing = pack.Requirements.FirstOrDefault(requirement => !capabilities.Contains(requirement));
            if (missing != null)
                throw new InvalidOperationException("Qualification requirement is unavailable: " + missing + ".");
            foreach (var step in pack.Steps)
            {
                if (step.Kind == QualificationStepKind.UserAction) continue;
                if (step.Kind == QualificationStepKind.Assertion)
                {
                    if (!_verifier.Supports(step))
                        throw new InvalidOperationException("Qualification assertion is not allowlisted: " + step.Assertion + ".");
                }
                else if (!_actions.Supports(step))
                {
                    throw new InvalidOperationException("Qualification action is not allowlisted: " +
                        (step.Action ?? QualificationManifestParser.StepKindName(step.Kind)) + ".");
                }
            }
        }

        private void BeginStep(QualificationRunState run, int index, bool awaitingUser)
        {
            var snapshot = run.MutableSteps[index];
            snapshot.AttemptId = NewId("step attempt");
            snapshot.Outcome = awaitingUser ? QualificationStepOutcome.AwaitingUser : QualificationStepOutcome.Running;
            run.Status = awaitingUser ? QualificationRunStatus.AwaitingUser : QualificationRunStatus.Running;
            try
            {
                var receipt = _journal.Append(QualificationRunEventKind.StepStarted,
                    Event(run, QualificationRunEventKind.StepStarted, index));
                snapshot.StartedEventId = receipt.EventId;
                snapshot.StartedSequence = receipt.Sequence;
            }
            catch (Exception ex)
            {
                snapshot.Outcome = QualificationStepOutcome.NotRun;
                snapshot.AttemptId = null;
                run.Status = QualificationRunStatus.Blocked;
                run.Restorable = false;
                throw new QualificationPersistenceException(
                    "Qualification step was not dispatched because its mandatory start event was not persisted.", run, ex);
            }
        }

        private void CompleteStep(QualificationRunState run, int index)
        {
            var snapshot = run.MutableSteps[index];
            try
            {
                var receipt = _journal.Append(QualificationRunEventKind.StepCompleted,
                    Event(run, QualificationRunEventKind.StepCompleted, index));
                snapshot.CompletedEventId = receipt.EventId;
                snapshot.CompletedSequence = receipt.Sequence;
            }
            catch (Exception ex)
            {
                snapshot.Outcome = QualificationStepOutcome.Unknown;
                snapshot.EvidenceStrength = QualificationEvidenceStrength.None;
                snapshot.Code = "completion_not_persisted";
                snapshot.Message = "The step may have executed but its completion event was not persisted.";
                run.Status = QualificationRunStatus.Blocked;
                run.Restorable = false;
                throw new QualificationPersistenceException(
                    "Qualification stopped after a possible effect because completion evidence was not persisted.", run, ex);
            }
        }

        private QualificationRunState Complete(QualificationRunState run)
        {
            var status = run.PendingTerminalStatus ?? QualificationRunStatus.Passed;
            var verified = run.Pack.Steps.Select((step, index) => new { step, snapshot = run.MutableSteps[index] })
                .Any(item => item.step.Required && item.step.Kind == QualificationStepKind.Assertion &&
                    item.snapshot.Outcome == QualificationStepOutcome.Passed &&
                    item.snapshot.EvidenceStrength == QualificationEvidenceStrength.Automatic);
            if (status == QualificationRunStatus.Passed && !verified)
                status = QualificationRunStatus.Blocked;
            run.Status = status;
            run.CompletedUtc = _utcNow();
            try
            {
                var receipt = _journal.Append(QualificationRunEventKind.RunCompleted,
                    Event(run, QualificationRunEventKind.RunCompleted, null));
                run.CompletedEventId = receipt.EventId;
                run.CompletedSequence = receipt.Sequence;
                run.TerminalPersisted = true;
                run.Restorable = false;
                return run;
            }
            catch (Exception ex)
            {
                run.Status = QualificationRunStatus.Blocked;
                run.TerminalPersisted = false;
                run.Restorable = false;
                throw new QualificationPersistenceException(
                    "Qualification terminal result was not persisted and cannot be reported as pass.", run, ex);
            }
        }

        private async Task<QualificationActionResult> ExecuteAsync(QualificationStepExecutionContext context,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            try
            {
                var result = await WithTimeout(
                    token => _actions.ExecuteAsync(context, token), timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                if (result == null) return QualificationActionResult.Unknown("missing_result", "Action returned no typed result.");
                QualificationJson.EnsureJsonValue(result.ActualJson, "Action actual evidence");
                ValidateDomainEffect(result.DomainEffect);
                return result;
            }
            catch (TimeoutException)
            {
                return QualificationActionResult.Unknown("timeout", "Action exceeded its bounded timeout.");
            }
            catch (OperationCanceledException)
            {
                return QualificationActionResult.Unknown("cancelled_after_start",
                    "Cancellation occurred after the action boundary was entered.");
            }
            catch (QualificationOperationStillRunningException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return QualificationActionResult.Unknown("executor_exception", Bound(ex.Message, 1000));
            }
        }

        private async Task<QualificationVerificationResult> VerifyAsync(QualificationStepExecutionContext context,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            try
            {
                var result = await WithTimeout(
                    token => _verifier.VerifyAsync(context, token), timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
                if (result == null)
                    return QualificationVerificationResult.Unknown("missing_result", "Verifier returned no typed result.");
                QualificationJson.EnsureJsonValue(result.ExpectedJson, "Assertion expected evidence");
                QualificationJson.EnsureJsonValue(result.ActualJson, "Assertion actual evidence");
                ValidateDomainEffect(result.DomainEffect);
                if (result.Outcome == QualificationStepOutcome.Passed &&
                    (result.ExpectedJson == null || result.ActualJson == null))
                    return QualificationVerificationResult.Unknown("missing_evidence",
                        "A passing assertion requires typed expected and actual JSON.");
                return result;
            }
            catch (TimeoutException)
            {
                return QualificationVerificationResult.Unknown("timeout", "Verifier exceeded its bounded timeout.");
            }
            catch (OperationCanceledException)
            {
                return QualificationVerificationResult.Unknown("cancelled", "Verifier was cancelled.");
            }
            catch (QualificationOperationStillRunningException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return QualificationVerificationResult.Unknown("verifier_exception", Bound(ex.Message, 1000));
            }
        }

        private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> operation,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<T> execution;
            try { execution = operation(linked.Token); }
            catch { linked.Dispose(); throw; }
            if (execution == null) { linked.Dispose(); return default(T); }
            var delay = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            var completed = await Task.WhenAny(execution, delay).ConfigureAwait(false);
            if (completed == execution)
            {
                linked.Dispose();
                return await execution.ConfigureAwait(false);
            }
            try
            {
                linked.Cancel();
            }
            catch
            {
                // A throwing cancellation callback does not prove that the operation stopped.
            }
            if (execution.IsCompleted)
            {
                linked.Dispose();
                return await execution.ConfigureAwait(false);
            }
            linked.Dispose();
            var observation = execution.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var ignored = task.Exception;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            if (cancellationToken.IsCancellationRequested)
                throw new QualificationOperationStillRunningException("cancelled_open_effect",
                    "Cancellation was requested, but the started operation has not stopped; cleanup and retry are blocked.");
            throw new QualificationOperationStillRunningException("timeout_open_effect",
                "The started operation exceeded its timeout and has not stopped; cleanup and retry are blocked.");
        }

        private static void ApplyAction(QualificationStepSnapshot snapshot, QualificationActionResult result)
        {
            Apply(snapshot, result.Outcome, QualificationEvidenceStrength.None, result.Code, result.Message,
                null, result.ActualJson, result.DomainEffect);
        }

        private static void ApplyVerification(QualificationStepSnapshot snapshot, QualificationVerificationResult result)
        {
            Apply(snapshot, result.Outcome, QualificationEvidenceStrength.Automatic, result.Code, result.Message,
                result.ExpectedJson, result.ActualJson, result.DomainEffect);
        }

        private static void Apply(QualificationStepSnapshot snapshot, QualificationStepOutcome outcome,
            QualificationEvidenceStrength strength, string code, string message, string expectedJson,
            string actualJson, string domainEffect)
        {
            snapshot.Outcome = outcome;
            snapshot.EvidenceStrength = strength;
            snapshot.Code = Bound(code, 128);
            snapshot.Message = Bound(message, 2000);
            snapshot.ExpectedJson = expectedJson;
            snapshot.ActualJson = actualJson;
            snapshot.DomainEffect = domainEffect;
        }

        private static bool MissingDependency(QualificationRunState run, QualificationStep step,
            out QualificationStepOutcome outcome)
        {
            foreach (var dependency in step.DependsOn)
            {
                var snapshot = run.MutableSteps.First(item =>
                    string.Equals(item.StepId, dependency, StringComparison.OrdinalIgnoreCase));
                if (snapshot.Outcome != QualificationStepOutcome.Passed)
                {
                    outcome = snapshot.Outcome;
                    return true;
                }
            }
            outcome = QualificationStepOutcome.Passed;
            return false;
        }

        private static void SetPendingTerminal(QualificationRunState run, QualificationRunStatus status)
        {
            if (!run.PendingTerminalStatus.HasValue)
                run.PendingTerminalStatus = status;
        }

        private static QualificationRunStatus TerminalStatus(QualificationStepOutcome outcome)
        {
            if (outcome == QualificationStepOutcome.Failed) return QualificationRunStatus.Failed;
            if (outcome == QualificationStepOutcome.Cancelled) return QualificationRunStatus.Cancelled;
            return QualificationRunStatus.Blocked;
        }

        private QualificationRunEventData Event(QualificationRunState run, QualificationRunEventKind kind,
            int? stepIndex)
        {
            var data = new QualificationRunEventData
            {
                EventKind = QualificationEventJournal.EventKindName(kind),
                RunId = run.RunId,
                PreviousRunId = run.PreviousRunId,
                PackId = run.Pack.Id,
                PackRevision = run.Pack.Revision,
                PackSha256 = run.Pack.ContentSha256,
                Host = run.Context.Host,
                ProductVersion = run.Context.ProductVersion,
                BuildCommit = run.Context.BuildCommit,
                Channel = run.Context.Channel,
                Capabilities = new List<string>(run.Context.Capabilities),
                RunStatus = QualificationManifestParser.RunStatusName(run.Status),
                PendingTerminalStatus = run.PendingTerminalStatus.HasValue
                    ? QualificationManifestParser.RunStatusName(run.PendingTerminalStatus.Value) : null,
                RecordedUtc = kind == QualificationRunEventKind.RunStarted ? run.StartedUtc :
                    kind == QualificationRunEventKind.RunCompleted && run.CompletedUtc.HasValue
                        ? run.CompletedUtc.Value : _utcNow()
            };
            if (!stepIndex.HasValue) return data;
            var step = run.Pack.Steps[stepIndex.Value];
            var snapshot = run.MutableSteps[stepIndex.Value];
            data.StepIndex = stepIndex;
            data.StepId = step.Id;
            data.StepKind = QualificationManifestParser.StepKindName(step.Kind);
            data.StepOutcome = QualificationManifestParser.OutcomeName(snapshot.Outcome);
            data.AttemptId = snapshot.AttemptId;
            if (kind == QualificationRunEventKind.StepCompleted)
            {
                data.EvidenceStrength = EvidenceStrengthName(snapshot.EvidenceStrength);
                data.Code = snapshot.Code;
                data.Message = snapshot.Message;
                data.ExpectedJson = snapshot.ExpectedJson;
                data.ActualJson = snapshot.ActualJson;
                data.DomainEffect = snapshot.DomainEffect;
            }
            return data;
        }

        private static void ValidateIdentity(IReadOnlyList<QualificationJournalRecord> records,
            QualificationPack pack, QualificationRunContext context, string runId)
        {
            var previousRunId = records[0].Data.PreviousRunId;
            foreach (var record in records)
            {
                var data = record.Data;
                if (!string.Equals(data.RunId, runId, StringComparison.Ordinal) ||
                    !string.Equals(data.PreviousRunId, previousRunId, StringComparison.Ordinal) ||
                    !string.Equals(data.PackId, pack.Id, StringComparison.Ordinal) ||
                    !string.Equals(data.PackRevision, pack.Revision, StringComparison.Ordinal) ||
                    !string.Equals(data.PackSha256, pack.ContentSha256, StringComparison.Ordinal) ||
                    !string.Equals(data.Host, context.Host, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(data.ProductVersion, context.ProductVersion, StringComparison.Ordinal) ||
                    !string.Equals(data.BuildCommit, context.BuildCommit, StringComparison.Ordinal) ||
                    !string.Equals(data.Channel, context.Channel, StringComparison.Ordinal) ||
                    data.Capabilities == null || !data.Capabilities.SequenceEqual(context.Capabilities,
                        StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Qualification run provenance does not match the selected pack and build.");
            }
        }

        private string NewId(string subject)
        {
            var value = _newId();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
                throw new InvalidOperationException("Qualification " + subject + " id factory returned an invalid id.");
            return value;
        }

        private static bool IsTerminal(QualificationRunStatus status)
        {
            return status == QualificationRunStatus.Passed || status == QualificationRunStatus.Failed ||
                status == QualificationRunStatus.Blocked || status == QualificationRunStatus.Cancelled;
        }

        private static string EvidenceStrengthName(QualificationEvidenceStrength strength)
        {
            if (strength == QualificationEvidenceStrength.Automatic) return "automatic";
            if (strength == QualificationEvidenceStrength.Manual) return "manual";
            return "none";
        }

        private static QualificationEvidenceStrength ParseEvidenceStrength(string value)
        {
            if (value == "automatic") return QualificationEvidenceStrength.Automatic;
            if (value == "manual") return QualificationEvidenceStrength.Manual;
            if (value == "none") return QualificationEvidenceStrength.None;
            throw new InvalidOperationException("Qualification evidence strength is invalid.");
        }

        private static void ValidateDomainEffect(string value)
        {
            if (value == null || value == "verified_change" || value == "verified_no_change" ||
                value == "error" || value == "unknown") return;
            throw new QualificationManifestException("domain_effect", "Unsupported qualification domain effect: " + value + ".");
        }

        private static string Bound(string value, int maximum)
        {
            if (value == null || value.Length <= maximum) return value;
            return value.Substring(0, maximum);
        }
    }
}
