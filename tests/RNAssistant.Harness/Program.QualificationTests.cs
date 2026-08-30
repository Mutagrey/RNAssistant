using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void QualificationManifestAndCatalogAreStrict()
        {
            var parser = new QualificationManifestParser();
            var pack = parser.Parse(QualificationPackJson(DefaultQualificationSteps()));
            AssertEqual("excel.test.identity", pack.Id, "manifest keeps exact pack id");
            AssertEqual(64, pack.ContentSha256.Length, "manifest pins SHA-256 content hash");
            AssertEqual(4, pack.Steps.Count, "manifest reads finite steps");

            RuntimeThrows<QualificationManifestException>(() => parser.Parse(
                QualificationPackJson(DefaultQualificationSteps()).Replace(
                    "\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
            RuntimeThrows<QualificationManifestException>(() => parser.Parse(
                QualificationPackJson(DefaultQualificationSteps()).Replace(
                    "\"title\":\"Identity test\"", "\"title\":\"Identity test\",\"script\":\"pwsh\"")));
            RuntimeThrows<QualificationManifestException>(() => parser.Parse(
                QualificationPackJson(DefaultQualificationSteps()).Replace(
                    "\"workspacePolicy\":\"runner-owned\"", "\"workspacePolicy\":\"current-document\"")));
            RuntimeThrows<QualificationManifestException>(() => parser.Parse(
                QualificationPackJson(DefaultQualificationSteps()).Replace(
                    "\"dependsOn\":[\"probe\"]", "\"dependsOn\":[\"verify\"]")));
            RuntimeThrows<QualificationManifestException>(() => parser.Parse(
                QualificationPackJson("[{\"id\":\"task\",\"kind\":\"agentTask\",\"action\":\"common.tools_run\"}]")));

            var registry = QualificationCoverageRegistry.Parse(CoverageRegistryJson());
            var catalog = new QualificationPackCatalog(registry, new[] { pack });
            var blocked = catalog.List("Excel", "release", new string[0]).Single();
            AssertTrue(!blocked.Available && blocked.MissingRequirements.SequenceEqual(new[] { "fake.capability" }),
                "missing runtime requirement blocks the pack rather than passing it");
            var available = catalog.List("Excel", "release", new[] { "fake.capability" }).Single();
            AssertTrue(available.Available, "exact capability admits the pack");
            AssertTrue(catalog.MissingCoverage("Excel", "release").SequenceEqual(new[] { "R50" }),
                "mandatory uncovered invariant remains visible");
            RuntimeThrows<ArgumentException>(() => catalog.List("UnknownHost", "release",
                new[] { "fake.capability" }));
            RuntimeThrows<ArgumentException>(() => catalog.MissingCoverage("Excel", "unknown"));
            RuntimeThrows<QualificationManifestException>(() => QualificationManifestParser.ParseRunStatus("5"));
            RuntimeThrows<QualificationManifestException>(() => new QualificationPackCatalog(registry,
                new[] { pack, pack }));
            var unknown = parser.Parse(QualificationPackJson(DefaultQualificationSteps(), "\"UNKNOWN\""));
            RuntimeThrows<QualificationManifestException>(() => new QualificationPackCatalog(registry,
                new[] { unknown }));
            RuntimeThrows<InvalidOperationException>(() => new QualificationRunner(
                new FakeQualificationActions { Supported = false }, new FakeQualificationVerifier(),
                new MemoryQualificationJournal()).Start(pack, QualificationContext()));
        }

        private static async Task QualificationRunnerPausesAndPassesFromVerifier()
        {
            var pack = ParseQualificationPack();
            var actions = new FakeQualificationActions();
            var verifier = new FakeQualificationVerifier
            {
                Result = QualificationVerificationResult.Passed("{\"same\":true}", "{\"same\":true}")
            };
            var journal = new MemoryQualificationJournal();
            var runner = new QualificationRunner(actions, verifier, journal);
            var run = runner.Start(pack, QualificationContext());

            await runner.AdvanceAsync(run, null, CancellationToken.None);
            AssertEqual(QualificationRunStatus.AwaitingUser, run.Status, "runner pauses on explicit user action");
            AssertTrue(actions.StepIds.SequenceEqual(new[] { "probe" }), "only pre-checkpoint action ran");
            AssertEqual(4, journal.Records.Count, "pause is durably recorded without completing user step");

            await runner.AdvanceAsync(run, new QualificationManualInput
            {
                StepId = "switch",
                Acknowledged = true,
                Note = "switched"
            }, CancellationToken.None);
            AssertEqual(QualificationRunStatus.Passed, run.Status, "typed verifier permits terminal pass");
            AssertTrue(run.TerminalPersisted, "terminal pass is persisted");
            AssertEqual(QualificationEvidenceStrength.Automatic, run.Steps[2].EvidenceStrength,
                "assertion evidence is automatic");
            AssertEqual(QualificationEvidenceStrength.Manual, run.Steps[1].EvidenceStrength,
                "manual checkpoint remains labeled manual");
            AssertTrue(actions.StepIds.SequenceEqual(new[] { "probe", "cleanup" }),
                "runner does not dispatch user action or assertion through the action port");
            AssertEqual(1, verifier.Calls, "verifier runs exactly once");
            AssertEqual(10, journal.Records.Count, "each step has one start and one completion boundary");
        }

        private static async Task QualificationRunnerRejectsNarrativePass()
        {
            var pack = ParseQualificationPack(SimpleQualificationSteps());
            var actions = new FakeQualificationActions();
            var verifier = new FakeQualificationVerifier
            {
                Result = QualificationVerificationResult.Passed(null, null, "The model says everything passed.")
            };
            var runner = new QualificationRunner(actions, verifier, new MemoryQualificationJournal());
            var run = runner.Start(pack, QualificationContext());
            await runner.AdvanceAsync(run, null, CancellationToken.None);
            AssertEqual(QualificationRunStatus.Blocked, run.Status, "missing typed expected/actual cannot become pass");
            AssertEqual(QualificationStepOutcome.Unknown, run.Steps[1].Outcome,
                "passing narrative is downgraded to unknown evidence");
        }

        private static async Task QualificationRunnerRunsCleanupWithoutRetry()
        {
            var pack = ParseQualificationPack();
            var actions = new FakeQualificationActions
            {
                Handler = context => context.Step.Kind == QualificationStepKind.Cleanup
                    ? QualificationActionResult.Passed("{\"clean\":true}")
                    : QualificationActionResult.Unknown("probe_unknown", "Probe may have executed.")
            };
            var verifier = new FakeQualificationVerifier();
            var journal = new MemoryQualificationJournal();
            var runner = new QualificationRunner(actions, verifier, journal);
            var run = runner.Start(pack, QualificationContext());
            await runner.AdvanceAsync(run, null, CancellationToken.None);
            AssertEqual(QualificationRunStatus.Blocked, run.Status, "unknown action blocks the run");
            AssertTrue(run.TerminalPersisted, "blocked result is persisted after cleanup");
            AssertTrue(actions.StepIds.SequenceEqual(new[] { "probe", "cleanup" }),
                "remaining normal steps are skipped and cleanup still runs");
            AssertEqual(QualificationStepOutcome.NotRun, run.Steps[1].Outcome, "manual step remains not run");
            AssertEqual(QualificationStepOutcome.NotRun, run.Steps[2].Outcome, "assertion remains not run");
            await runner.AdvanceAsync(run, null, CancellationToken.None);
            AssertEqual(2, actions.StepIds.Count, "terminal advance never retries a possible effect");

            var cancelledActions = new FakeQualificationActions();
            var cancelledRunner = new QualificationRunner(cancelledActions, verifier,
                new MemoryQualificationJournal());
            var cancelledRun = cancelledRunner.Start(pack, QualificationContext());
            await cancelledRunner.AdvanceAsync(cancelledRun, null, new CancellationToken(true));
            AssertEqual(QualificationRunStatus.Cancelled, cancelledRun.Status,
                "safe-boundary cancellation stays explicit");
            AssertTrue(cancelledActions.StepIds.SequenceEqual(new[] { "cleanup" }),
                "cancellation skips normal actions and still runs bounded cleanup");
        }

        private static async Task QualificationRunnerHonorsMandatoryEventBarriers()
        {
            var pack = ParseQualificationPack(SimpleQualificationSteps());
            var actions = new FakeQualificationActions();
            var verifier = new FakeQualificationVerifier();

            var startFailure = new MemoryQualificationJournal { FailAppendNumber = 1 };
            var startRunner = new QualificationRunner(actions, verifier, startFailure);
            RuntimeThrows<QualificationPersistenceException>(() => startRunner.Start(pack, QualificationContext()));
            AssertEqual(0, actions.StepIds.Count, "failed run-start barrier dispatches nothing");

            var stepFailure = new MemoryQualificationJournal { FailAppendNumber = 2 };
            var stepRunner = new QualificationRunner(actions, verifier, stepFailure);
            var stepRun = stepRunner.Start(pack, QualificationContext());
            RuntimeThrows<QualificationPersistenceException>(() =>
                stepRunner.AdvanceAsync(stepRun, null, CancellationToken.None).GetAwaiter().GetResult());
            AssertEqual(0, actions.StepIds.Count, "failed step-start barrier dispatches nothing");

            var completionFailure = new MemoryQualificationJournal { FailAppendNumber = 3 };
            var completionActions = new FakeQualificationActions();
            var completionRunner = new QualificationRunner(completionActions, verifier, completionFailure);
            var completionRun = completionRunner.Start(pack, QualificationContext());
            RuntimeThrows<QualificationPersistenceException>(() =>
                completionRunner.AdvanceAsync(completionRun, null, CancellationToken.None).GetAwaiter().GetResult());
            AssertEqual(1, completionActions.StepIds.Count, "action runs once after durable intent");
            AssertEqual(QualificationRunStatus.Blocked, completionRun.Status,
                "missing completion persistence blocks result");
            RuntimeThrows<InvalidOperationException>(() =>
                completionRunner.AdvanceAsync(completionRun, null, CancellationToken.None).GetAwaiter().GetResult());
            AssertEqual(1, completionActions.StepIds.Count, "open possible effect is never retried");

            using (var cancellation = new CancellationTokenSource())
            {
                var openEffectActions = new HangingQualificationActions(cancellation);
                var openEffectJournal = new MemoryQualificationJournal();
                var openEffectRunner = new QualificationRunner(openEffectActions, verifier, openEffectJournal);
                var openEffectRun = openEffectRunner.Start(pack, QualificationContext());
                await openEffectRunner.AdvanceAsync(openEffectRun, null, cancellation.Token);
                AssertEqual(QualificationRunStatus.Blocked, openEffectRun.Status,
                    "cancellation cannot close an operation that has not stopped");
                AssertTrue(!openEffectRun.CanResume && !openEffectRun.HasDurableTerminal,
                    "open possible effect has no resumable or terminal projection");
                AssertEqual(2, openEffectJournal.Records.Count,
                    "open possible effect persists start but never fabricates completion");
                AssertTrue(openEffectActions.StepIds.SequenceEqual(new[] { "probe" }),
                    "cleanup cannot overlap an operation that has not stopped");
                var replayedOpenEffect = new QualificationRunner(openEffectActions, verifier, openEffectJournal)
                    .Restore(pack, QualificationContext(), openEffectRun.RunId);
                AssertEqual(QualificationRunStatus.Blocked, replayedOpenEffect.Status,
                    "durable open effect remains blocked after replay");
            }
            await Task.CompletedTask;
        }

        private static async Task QualificationRunnerRestoresOnlySafeBoundary()
        {
            var pack = ParseQualificationPack();
            var journal = new MemoryQualificationJournal();
            var actions = new FakeQualificationActions();
            var verifier = new FakeQualificationVerifier();
            var runner = new QualificationRunner(actions, verifier, journal);
            var original = runner.Start(pack, QualificationContext());
            await runner.AdvanceAsync(original, null, CancellationToken.None);

            var restoredRunner = new QualificationRunner(actions, verifier, journal);
            var restored = restoredRunner.Restore(pack, QualificationContext(), original.RunId);
            AssertEqual(QualificationRunStatus.AwaitingUser, restored.Status,
                "durable user checkpoint is safe to resume");
            await restoredRunner.AdvanceAsync(restored, new QualificationManualInput
            {
                StepId = "switch", Acknowledged = true
            }, CancellationToken.None);
            AssertEqual(QualificationRunStatus.Passed, restored.Status, "restored checkpoint reaches typed pass");
            AssertEqual(1, actions.StepIds.Count(id => id == "probe"), "replay does not repeat completed probe");

            var openJournal = new MemoryQualificationJournal { FailAppendNumber = 3 };
            var openActions = new FakeQualificationActions();
            var openRunner = new QualificationRunner(openActions, verifier, openJournal);
            var open = openRunner.Start(ParseQualificationPack(SimpleQualificationSteps()), QualificationContext());
            RuntimeThrows<QualificationPersistenceException>(() =>
                openRunner.AdvanceAsync(open, null, CancellationToken.None).GetAwaiter().GetResult());
            openJournal.FailAppendNumber = 0;
            var replayed = new QualificationRunner(openActions, verifier, openJournal)
                .Restore(open.Pack, open.Context, open.RunId);
            AssertEqual(QualificationRunStatus.Blocked, replayed.Status,
                "open automatic step replays as blocked");
            RuntimeThrows<InvalidOperationException>(() =>
                new QualificationRunner(openActions, verifier, openJournal)
                    .AdvanceAsync(replayed, null, CancellationToken.None).GetAwaiter().GetResult());
            AssertEqual(1, openActions.StepIds.Count, "open automatic step is not redispatched after replay");

            var cancelledJournal = new MemoryQualificationJournal { FailAppendNumber = 4 };
            var cancelledActions = new FakeQualificationActions();
            var cancelledRunner = new QualificationRunner(cancelledActions, verifier, cancelledJournal);
            var cancelled = cancelledRunner.Start(pack, QualificationContext());
            RuntimeThrows<QualificationPersistenceException>(() =>
                cancelledRunner.AdvanceAsync(cancelled, null, new CancellationToken(true)).GetAwaiter().GetResult());
            cancelledJournal.FailAppendNumber = 0;
            var cancelledReplay = new QualificationRunner(cancelledActions, verifier, cancelledJournal)
                .Restore(pack, cancelled.Context, cancelled.RunId);
            await new QualificationRunner(cancelledActions, verifier, cancelledJournal)
                .AdvanceAsync(cancelledReplay, null, CancellationToken.None);
            AssertEqual(QualificationRunStatus.Cancelled, cancelledReplay.Status,
                "pending cancellation survives a failed terminal append");
        }

        private static void QualificationEventsUseCanonicalStreamAndCas()
        {
            foreach (var kind in new[]
            {
                SessionEventKind.QualificationRunStarted,
                SessionEventKind.QualificationStepStarted,
                SessionEventKind.QualificationStepCompleted,
                SessionEventKind.QualificationRunCompleted
            })
            {
                var descriptor = SessionEventDescriptors.For(kind);
                AssertEqual(SessionEventAuthority.Authority, descriptor.Authority,
                    "qualification event is replay authority");
                AssertEqual(SessionEventDurability.Mandatory, descriptor.Durability,
                    "qualification event is mandatory");
                AssertEqual(SessionEventWriteScope.EventPort, descriptor.WriteScope,
                    "qualification event uses the typed event port");
            }

            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "qualification-events", "Qualification.xlsx", "Qualification");
                var eventStore = EventStore(store);
                var journal = new QualificationEventJournal(eventStore, session);
                var start = QualificationEvent("run-1", "run_started", "running");
                journal.Append(QualificationRunEventKind.RunStarted, start);
                var stepStart = QualificationEvent("run-1", "step_started", "verifying", 0,
                    "verify", "assertion", "running", "attempt-1");
                journal.Append(QualificationRunEventKind.StepStarted, stepStart);
                var largeActual = JsonConvert.SerializeObject(new string('x', 40000));
                var completed = QualificationEvent("run-1", "step_completed", "verifying", 0,
                    "verify", "assertion", "passed", "attempt-1");
                completed.EvidenceStrength = "automatic";
                completed.Code = "verified";
                completed.ExpectedJson = "{\"ok\":true}";
                completed.ActualJson = largeActual;
                journal.Append(QualificationRunEventKind.StepCompleted, completed);
                journal.Append(QualificationRunEventKind.RunCompleted,
                    QualificationEvent("run-1", "run_completed", "passed"));

                var replay = journal.Read("run-1");
                AssertEqual(4, replay.Count, "qualification records replay from canonical stream");
                AssertEqual(largeActual, replay[2].Data.ActualJson, "CAS payload restores exact large evidence");
                var durable = eventStore.Read(session, SessionEventReadMode.RequireComplete)
                    .Single(item => item.Type == SessionEventTypes.QualificationStepCompleted);
                AssertTrue(durable.Payload != null && durable.Payload.ByteLength > 32768,
                    "large evidence uses existing chat CAS payload");
            });
        }

        private static async Task QualificationBridgeProjectionIsBounded()
        {
            var pack = ParseQualificationPack(SimpleQualificationSteps());
            var verifier = new FakeQualificationVerifier
            {
                Result = QualificationVerificationResult.Passed("{\"ok\":true}",
                    JsonConvert.SerializeObject(new string('z', 70000)))
            };
            var actions = new FakeQualificationActions();
            var journal = new MemoryQualificationJournal();
            var runner = new QualificationRunner(actions, verifier, journal);
            var run = runner.Start(pack, QualificationContext());
            await runner.AdvanceAsync(run, null, CancellationToken.None);
            var dto = QualificationRunDto.From(run);
            AssertEqual("passed", dto.Status, "bridge projects calculated run status");
            AssertTrue(dto.HasDurableTerminal && !dto.CanResume && dto.CurrentStepId == null,
                "bridge projects finite terminal state");
            AssertTrue(dto.StartedSequence.HasValue && dto.CompletedSequence.HasValue &&
                dto.CompletedSequence > dto.StartedSequence,
                "bridge preserves run-level causal event sequences");
            AssertTrue(dto.Steps[1].ActualTruncated && dto.Steps[1].ActualJson.Length == 65536,
                "bridge bounds large inline evidence");
            AssertEqual(run.Steps[1].CompletedEventId, dto.Steps[1].CompletedEventId,
                "bridge preserves causal event identity");
        }

        private static QualificationPack ParseQualificationPack(string steps = null)
        {
            return new QualificationManifestParser().Parse(
                QualificationPackJson(steps ?? DefaultQualificationSteps()));
        }

        private static QualificationRunContext QualificationContext()
        {
            return new QualificationRunContext("Excel", "16.1.0-dev", "test-commit", "development",
                new[] { "fake.capability" });
        }

        private static string QualificationPackJson(string steps, string coverage = "\"R04\",\"WQ0\"")
        {
            return "{" +
                "\"schemaVersion\":1," +
                "\"id\":\"excel.test.identity\"," +
                "\"revision\":\"1\"," +
                "\"title\":\"Identity test\"," +
                "\"hosts\":[\"Excel\"]," +
                "\"suite\":\"release\"," +
                "\"workspacePolicy\":\"runner-owned\"," +
                "\"requirements\":[\"fake.capability\"]," +
                "\"coverage\":[" + coverage + "]," +
                "\"steps\":" + steps +
                "}";
        }

        private static string DefaultQualificationSteps()
        {
            return "[" +
                "{\"id\":\"probe\",\"kind\":\"hostProbe\",\"action\":\"fake.capture\"}," +
                "{\"id\":\"switch\",\"kind\":\"userAction\",\"instructionKey\":\"fake.switch\",\"dependsOn\":[\"probe\"]}," +
                "{\"id\":\"verify\",\"kind\":\"assertion\",\"assertion\":\"fake.same\",\"dependsOn\":[\"switch\"]}," +
                "{\"id\":\"cleanup\",\"kind\":\"cleanup\",\"action\":\"fake.cleanup\",\"dependsOn\":[\"verify\"]}" +
                "]";
        }

        private static string SimpleQualificationSteps()
        {
            return "[" +
                "{\"id\":\"probe\",\"kind\":\"hostProbe\",\"action\":\"fake.capture\"}," +
                "{\"id\":\"verify\",\"kind\":\"assertion\",\"assertion\":\"fake.same\",\"dependsOn\":[\"probe\"]}" +
                "]";
        }

        private static string CoverageRegistryJson()
        {
            return "{\"schemaVersion\":1,\"entries\":[" +
                "{\"id\":\"R04\",\"owner\":\"OfficeHosts.Identity\",\"hosts\":[\"Excel\"],\"suites\":[\"release\"],\"mandatory\":true}," +
                "{\"id\":\"WQ0\",\"owner\":\"Qualification\",\"hosts\":[\"Excel\"],\"suites\":[\"release\"],\"mandatory\":false}," +
                "{\"id\":\"R50\",\"owner\":\"Qualification\",\"hosts\":[\"Excel\"],\"suites\":[\"release\"],\"mandatory\":true}" +
                "]}";
        }

        private static QualificationRunEventData QualificationEvent(string runId, string eventKind,
            string runStatus, int? stepIndex = null, string stepId = null, string stepKind = null,
            string stepOutcome = null, string attemptId = null)
        {
            return new QualificationRunEventData
            {
                EventKind = eventKind,
                RunId = runId,
                PackId = "excel.test.identity",
                PackRevision = "1",
                PackSha256 = new string('a', 64),
                Host = "Excel",
                ProductVersion = "16.1.0-dev",
                BuildCommit = "test-commit",
                Channel = "development",
                Capabilities = new List<string> { "fake.capability" },
                RunStatus = runStatus,
                StepIndex = stepIndex,
                StepId = stepId,
                StepKind = stepKind,
                StepOutcome = stepOutcome,
                AttemptId = attemptId,
                RecordedUtc = DateTime.UtcNow
            };
        }

        private sealed class FakeQualificationActions : IQualificationActionExecutor
        {
            internal readonly List<string> StepIds = new List<string>();
            internal Func<QualificationStepExecutionContext, QualificationActionResult> Handler;
            internal bool Supported = true;

            public bool Supports(QualificationStep step)
            {
                return Supported && step != null && step.Kind != QualificationStepKind.Assertion &&
                    step.Kind != QualificationStepKind.UserAction;
            }

            public Task<QualificationActionResult> ExecuteAsync(QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                StepIds.Add(context.Step.Id);
                var result = Handler == null
                    ? QualificationActionResult.Passed("{\"completed\":true}")
                    : Handler(context);
                return Task.FromResult(result);
            }
        }

        private sealed class FakeQualificationVerifier : IQualificationVerifier
        {
            internal int Calls;
            internal QualificationVerificationResult Result =
                QualificationVerificationResult.Passed("{\"same\":true}", "{\"same\":true}");

            public bool Supports(QualificationStep step)
            {
                return step != null && step.Kind == QualificationStepKind.Assertion &&
                    string.Equals(step.Assertion, "fake.same", StringComparison.Ordinal);
            }

            public Task<QualificationVerificationResult> VerifyAsync(QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(Result);
            }
        }

        private sealed class HangingQualificationActions : IQualificationActionExecutor
        {
            private readonly CancellationTokenSource _cancellation;
            private readonly TaskCompletionSource<QualificationActionResult> _never =
                new TaskCompletionSource<QualificationActionResult>();

            internal HangingQualificationActions(CancellationTokenSource cancellation)
            {
                _cancellation = cancellation;
            }

            internal readonly List<string> StepIds = new List<string>();

            public bool Supports(QualificationStep step)
            {
                return step != null && step.Kind != QualificationStepKind.Assertion &&
                    step.Kind != QualificationStepKind.UserAction;
            }

            public Task<QualificationActionResult> ExecuteAsync(QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                StepIds.Add(context.Step.Id);
                _cancellation.Cancel();
                return _never.Task;
            }
        }

        private sealed class MemoryQualificationJournal : IQualificationRunJournal
        {
            internal readonly List<QualificationJournalRecord> Records = new List<QualificationJournalRecord>();
            internal int FailAppendNumber;
            private int _appendCount;

            public QualificationEventReceipt Append(QualificationRunEventKind kind, QualificationRunEventData data)
            {
                _appendCount++;
                if (FailAppendNumber == _appendCount) throw new InvalidOperationException("Injected journal failure.");
                var receipt = new QualificationEventReceipt("event-" + _appendCount, _appendCount);
                var clone = JsonConvert.DeserializeObject<QualificationRunEventData>(JsonConvert.SerializeObject(data));
                Records.Add(new QualificationJournalRecord(kind, clone, receipt));
                return receipt;
            }

            public IReadOnlyList<QualificationJournalRecord> Read(string runId)
            {
                return Records.Where(record => string.Equals(record.Data.RunId, runId, StringComparison.Ordinal))
                    .ToArray();
            }
        }

    }
}
