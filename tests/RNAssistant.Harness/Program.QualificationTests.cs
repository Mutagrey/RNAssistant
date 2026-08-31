using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Qualification;
using RNAssistant.Office.Services;
using RNAssistant.Office.WebView;
using RNAssistant.Office;

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
            AssertEqual("awaiting_user", QualificationManifestParser.RunStatusName(QualificationRunStatus.AwaitingUser),
                "run status wire uses the canonical underscore form");
            RuntimeThrows<QualificationManifestException>(() => QualificationManifestParser.ParseRunStatus("awaitinguser"));
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
            AssertTrue(dto.ReportTruncated, "bounded bridge report declares evidence truncation");

            var manyAssertions = "[" +
                "{\"id\":\"probe\",\"kind\":\"hostProbe\",\"action\":\"fake.capture\"}," +
                string.Join(",", Enumerable.Range(1, 5).Select(index =>
                    "{\"id\":\"verify" + index + "\",\"kind\":\"assertion\",\"assertion\":\"fake.same\",\"dependsOn\":[\"probe\"]}")) +
                "]";
            var aggregatePack = ParseQualificationPack(manyAssertions);
            var aggregateRunner = new QualificationRunner(new FakeQualificationActions(), verifier,
                new MemoryQualificationJournal());
            var aggregateRun = aggregateRunner.Start(aggregatePack, QualificationContext());
            await aggregateRunner.AdvanceAsync(aggregateRun, null, CancellationToken.None);
            var aggregate = QualificationRunDto.From(aggregateRun);
            var evidenceChars = aggregate.Steps.Sum(step =>
                (step.ExpectedJson == null ? 0 : step.ExpectedJson.Length) +
                (step.ActualJson == null ? 0 : step.ActualJson.Length));
            AssertTrue(evidenceChars <= 262144 && aggregate.ReportTruncated,
                "bridge enforces one aggregate report evidence budget");

            var emptyEvidence = new QualificationStepSnapshot(ParseQualificationPack().Steps[0])
            {
                ExpectedJson = string.Empty,
                ActualJson = string.Empty
            };
            var emptyEvidenceDto = QualificationStepResultDto.From(emptyEvidence);
            AssertEqual(string.Empty, emptyEvidenceDto.ExpectedJson,
                "bridge preserves an empty expected value without a false truncation marker");
            AssertTrue(!emptyEvidenceDto.ExpectedTruncated && !emptyEvidenceDto.ActualTruncated,
                "empty bridge evidence is not reported as truncated");
        }

        private static void QualificationBuiltInShellPersistsAndResumes()
        {
            var catalog = QualificationBuiltInCatalog.Load();
            var packs = catalog.List("Excel", "quick", new[] { QualificationApplicationService.ShellCapability });
            AssertEqual(2, packs.Count, "shell and production-path quick packs are embedded");
            var shell = packs.Single(item => item.Pack.Id == "common.ui-shell");
            AssertTrue(shell.Available, "shell capability admits the embedded shell pack");
            var productionQuick = packs.Single(item => item.Pack.Id == "common.quick");
            AssertTrue(!productionQuick.Available && productionQuick.MissingRequirements.SequenceEqual(
                    new[] { "qualification.pack.common.quick.v1" }),
                "production quick pack remains N/A until its exact adapter exists");
            AssertEqual(0, catalog.MissingCoverage("Excel", "quick").Count,
                "embedded quick packs own every mandatory quick coverage id");

            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "qualification-shell", "Qualification.xlsx",
                    "Qualification shell");
                var service = new QualificationApplicationService(EventStore(store));
                AssertTrue(!service.IsQualificationChat(session), "ordinary chat has no qualification marker");
                var run = service.StartAsync(session, "common.ui-shell", null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(QualificationRunStatus.AwaitingUser, run.Status,
                    "embedded shell pauses at the explicit user checkpoint");
                AssertTrue(service.HasOpenRun(session), "open shell run is discovered from durable events");

                var reloaded = store.Load(session.Id);
                var restarted = new QualificationApplicationService(EventStore(store));
                var restored = restarted.GetLatest(reloaded);
                AssertEqual(run.RunId, restored.RunId, "restart discovers the latest run without a second index");
                AssertEqual(QualificationRunStatus.AwaitingUser, restored.Status,
                    "restart restores the safe manual boundary");
                var completed = restarted.AdvanceAsync(reloaded, restored.RunId,
                    new QualificationManualInput
                    {
                        StepId = "acknowledge",
                        Acknowledged = true
                    }, false, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(QualificationRunStatus.Passed, completed.Status,
                    "typed verifier passes only after reading persisted preflight and manual evidence");
                AssertTrue(completed.HasDurableTerminal && !restarted.HasOpenRun(reloaded),
                    "terminal state is durable and no longer resumable");
                AssertEqual("unavailable", completed.Context.BuildCommit,
                    "shell report does not fabricate unavailable build provenance");

                var finalReload = store.Load(session.Id);
                var replayed = new QualificationApplicationService(EventStore(store)).GetLatest(finalReload);
                AssertEqual(QualificationRunStatus.Passed, replayed.Status,
                    "terminal shell result replays after service restart");
                AssertEqual(10, new QualificationEventJournal(EventStore(store), finalReload)
                    .Read(replayed.RunId).Count, "shell run owns exact start/step/terminal boundaries");
            });
        }

        private static void QualificationBuiltInSuitesAreVersionedAndFailClosed()
        {
            var catalog = QualificationBuiltInCatalog.Load();
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["common.quick"] = "qualification.pack.common.quick.v1",
                ["provider.live"] = "qualification.pack.provider.live.v1",
                ["storage.recovery"] = "qualification.pack.storage.recovery.v1",
                ["ui.webview"] = "qualification.pack.ui.webview.v1",
                ["excel.read-write"] = "qualification.pack.excel.read-write.v1",
                ["excel.complex-task"] = "qualification.pack.excel.complex-task.v1",
                ["vba.lifecycle"] = "qualification.pack.vba.lifecycle.v1",
                ["cross.full-run"] = "qualification.pack.cross.full-run.v1"
            };
            var packs = new[] { "quick", "full", "release" }
                .SelectMany(suite => catalog.List("Excel", suite,
                    new[] { QualificationApplicationService.ShellCapability }))
                .Where(item => expected.ContainsKey(item.Pack.Id))
                .ToArray();
            AssertEqual(expected.Count, packs.Length, "every canonical WQ-A4 family is embedded for Excel");
            foreach (var item in packs)
            {
                AssertEqual("1", item.Pack.Revision, item.Pack.Id + " pins manifest revision");
                AssertEqual(64, item.Pack.ContentSha256.Length, item.Pack.Id + " pins manifest content hash");
                AssertTrue(item.Pack.Requirements.Contains(expected[item.Pack.Id]),
                    item.Pack.Id + " requires its exact all-or-nothing adapter capability");
                AssertTrue(!item.Available && item.MissingRequirements.Contains(expected[item.Pack.Id]),
                    item.Pack.Id + " is N/A rather than pass without its owner");
                AssertTrue(item.Pack.Steps.Any(step => step.Kind == QualificationStepKind.Assertion && step.Required),
                    item.Pack.Id + " has a required typed final-state verifier");
                if (item.Pack.WorkspacePolicy == "runner-owned")
                {
                    AssertTrue(item.Pack.Steps.Any(step => step.Kind == QualificationStepKind.Fixture),
                        item.Pack.Id + " owns a versioned runner fixture step");
                    AssertTrue(item.Pack.Steps.Last().Kind == QualificationStepKind.Cleanup,
                        item.Pack.Id + " ends with cleanup");
                }
            }
            AssertEqual(0, catalog.MissingCoverage("Excel", "quick").Count,
                "Excel quick suite has no orphan mandatory coverage");
            AssertEqual(0, catalog.MissingCoverage("Excel", "full").Count,
                "Excel full suite has no orphan mandatory coverage");
            AssertEqual(0, catalog.MissingCoverage("Excel", "release").Count,
                "Excel release suite has no orphan mandatory coverage");
            AssertTrue(catalog.List("Word", "release", new string[0]).All(item => !item.Available),
                "unimplemented Word release families remain N/A");
        }

        private static void QualificationUiBridgeRoutesTypedPayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var token = BridgeToken(bridge);
            var catalogJson = bridge.HandleMessageAsync(
                "{\"id\":\"q1\",\"type\":\"getQualificationCatalog\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"chatId\":\"chat-q\",\"suite\":\"quick\"}}")
                .GetAwaiter().GetResult();
            AssertTrue(JObject.Parse(catalogJson)["ok"].Value<bool>(), "qualification catalog bridge response ok");
            AssertEqual("quick", controller.LastQualificationSuite, "qualification suite remains typed");

            var startJson = bridge.HandleMessageAsync(
                "{\"id\":\"q2\",\"type\":\"startQualification\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"chatId\":\"chat-q\",\"packId\":\"common.ui-shell\"}}")
                .GetAwaiter().GetResult();
            AssertTrue(JObject.Parse(startJson)["ok"].Value<bool>(), "qualification start bridge response ok");
            AssertEqual("common.ui-shell", controller.LastQualificationPackId, "qualification pack id remains typed");

            var advanceJson = bridge.HandleMessageAsync(
                "{\"id\":\"q3\",\"type\":\"advanceQualification\",\"bridgeToken\":\"" + token +
                "\",\"payload\":{\"chatId\":\"qualification-chat\",\"runId\":\"qualification-run\"," +
                "\"stepId\":\"acknowledge\",\"acknowledged\":true,\"cancel\":false}}")
                .GetAwaiter().GetResult();
            AssertTrue(JObject.Parse(advanceJson)["ok"].Value<bool>(), "qualification advance bridge response ok");
            AssertEqual("acknowledge", controller.LastQualificationStepId, "manual step id remains typed");
            AssertTrue(controller.LastQualificationAcknowledged && !controller.LastQualificationCancel,
                "manual acknowledgement and cancel flags are not inferred");
        }

        private static void QualificationHostPortOwnsActionsAndVerifier()
        {
            var builtIn = QualificationBuiltInCatalog.Load();
            var requirements = new[]
            {
                "qualification.excel.wq0.v1", "windows-x64", "office-x64", "independent-client-helper"
            };
            var wq0 = builtIn.List("Excel", "release", requirements)
                .Single(item => item.Pack.Id == "excel.wq0.identity");
            AssertEqual("excel.wq0.identity", wq0.Pack.Id, "embedded WQ0 pack id");
            AssertTrue(wq0.Available, "exact host requirements admit embedded WQ0 pack");
            AssertEqual(0, builtIn.MissingCoverage("Excel", "release").Count,
                "embedded WQ0 owns mandatory release identity coverage");
            AssertTrue(!builtIn.List("Excel", "release", new string[0])
                    .Single(item => item.Pack.Id == "excel.wq0.identity").Available,
                "WQ0 is blocked without Windows/helper capabilities");

            var coverage = QualificationCoverageRegistry.Parse(
                "{\"schemaVersion\":1,\"entries\":[{\"id\":\"host.port\",\"owner\":\"test\"," +
                "\"hosts\":[\"Excel\"],\"suites\":[\"quick\"],\"mandatory\":true}]}");
            var pack = new QualificationManifestParser().Parse("{" +
                "\"schemaVersion\":1,\"id\":\"excel.host-port\",\"revision\":\"1\"," +
                "\"title\":\"Host port\",\"hosts\":[\"Excel\"],\"suite\":\"quick\"," +
                "\"workspacePolicy\":\"read-only\",\"requirements\":[\"fake.host\"]," +
                "\"coverage\":[\"host.port\"],\"steps\":[" +
                "{\"id\":\"probe\",\"kind\":\"hostProbe\",\"action\":\"fake.host.probe\"}," +
                "{\"id\":\"verify\",\"kind\":\"assertion\",\"assertion\":\"fake.host.verify\",\"dependsOn\":[\"probe\"]}]}");
            var catalog = new QualificationPackCatalog(coverage, new[] { pack });
            var host = new FakeQualificationHostPort();
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Excel", "qualification-host", "Qualification.xlsx", "Host port");
                var service = new QualificationApplicationService(EventStore(store), catalog, host);
                var run = service.StartAsync(session, pack.Id, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                AssertEqual(QualificationRunStatus.Passed, run.Status,
                    "typed host verifier owns terminal pass");
                AssertEqual(1, host.ActionCalls, "host action executes once");
                AssertEqual(1, host.VerifierCalls, "host verifier executes once");
                AssertTrue(host.SawPersistedProbe, "host verifier receives persisted action evidence");
            });
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

        private sealed class FakeQualificationHostPort : IQualificationHostPort
        {
            internal int ActionCalls;
            internal int VerifierCalls;
            internal bool SawPersistedProbe;

            public IReadOnlyList<string> QualificationCapabilities
            {
                get { return new[] { "fake.host" }; }
            }

            public bool SupportsQualificationAction(QualificationStep step)
            {
                return step != null && step.Action == "fake.host.probe";
            }

            public QualificationActionResult ExecuteQualificationAction(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ActionCalls++;
                return QualificationActionResult.Passed("{\"host\":\"observed\"}");
            }

            public bool SupportsQualificationAssertion(QualificationStep step)
            {
                return step != null && step.Assertion == "fake.host.verify";
            }

            public QualificationVerificationResult VerifyQualificationAssertion(
                QualificationStepExecutionContext context,
                QualificationEvidenceSnapshot evidence,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifierCalls++;
                var probe = evidence.Find("probe");
                SawPersistedProbe = probe != null && probe.Outcome == QualificationStepOutcome.Passed &&
                    probe.ActualJson == "{\"host\":\"observed\"}";
                return SawPersistedProbe
                    ? QualificationVerificationResult.Passed("{\"persisted\":true}", "{\"persisted\":true}")
                    : QualificationVerificationResult.Failed("missing", "Missing persisted probe.",
                        "{\"persisted\":true}", "{\"persisted\":false}");
            }

            public void ReleaseQualificationResources()
            {
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
