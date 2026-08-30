using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Office.Services
{
    public sealed class QualificationApplicationService
    {
        public const string ShellCapability = "qualification.shell.v1";

        private readonly IEventStore _events;
        private readonly QualificationPackCatalog _catalog;
        private readonly IReadOnlyList<string> _capabilities;

        public QualificationApplicationService(IEventStore events)
            : this(events, QualificationBuiltInCatalog.Load())
        {
        }

        internal QualificationApplicationService(IEventStore events, QualificationPackCatalog catalog)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _capabilities = Array.AsReadOnly(new[] { ShellCapability });
        }

        public QualificationPack GetPack(string packId)
        {
            return _catalog.Get(packId);
        }

        public IReadOnlyList<QualificationPackAvailability> List(string host, string suite)
        {
            return _catalog.List(host, NormalizeSuite(suite), _capabilities);
        }

        public IReadOnlyList<string> MissingCoverage(string host, string suite)
        {
            return _catalog.MissingCoverage(host, NormalizeSuite(suite));
        }

        public async Task<QualificationRunState> StartAsync(
            ChatSession session,
            string packId,
            string previousRunId,
            CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var pack = _catalog.Get(packId);
            EnsureShellPack(pack, session.Host);
            var journal = Journal(session);
            var latestRunId = journal.FindLatestRunId();
            if (latestRunId != null && !HasDurableTerminal(journal.Read(latestRunId)))
                throw new InvalidOperationException("В этом qualification-чате уже есть незавершённый запуск.");

            var runner = Runner(journal);
            var run = runner.Start(pack, CurrentContext(session.Host), previousRunId);
            return await runner.AdvanceAsync(run, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<QualificationRunState> AdvanceAsync(
            ChatSession session,
            string runId,
            QualificationManualInput manualInput,
            bool cancel,
            CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var journal = Journal(session);
            var runner = Runner(journal);
            var run = Restore(runner, journal, session.Host, runId);
            if (run.HasDurableTerminal) return run;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (cancel) linked.Cancel();
                return await runner.AdvanceAsync(run, manualInput, linked.Token).ConfigureAwait(false);
            }
        }

        public QualificationRunState GetLatest(ChatSession session, string runId = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var journal = Journal(session);
            var selectedRunId = string.IsNullOrWhiteSpace(runId) ? journal.FindLatestRunId() : runId.Trim();
            if (selectedRunId == null) return null;
            return Restore(Runner(journal), journal, session.Host, selectedRunId);
        }

        public bool HasOpenRun(ChatSession session)
        {
            if (session == null) return false;
            var journal = Journal(session);
            var runId = journal.FindLatestRunId();
            return runId != null && !HasDurableTerminal(journal.Read(runId));
        }

        public bool IsQualificationChat(ChatSession session)
        {
            return session != null && Journal(session).FindLatestRunId() != null;
        }

        private QualificationRunState Restore(
            QualificationRunner runner,
            QualificationEventJournal journal,
            string host,
            string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new InvalidOperationException("Qualification run id is required.");
            var records = journal.Read(runId.Trim());
            if (records.Count == 0 || records[0].Kind != QualificationRunEventKind.RunStarted)
                throw new InvalidOperationException("Qualification run was not found in this chat.");
            var start = records[0].Data;
            var pack = _catalog.Get(start.PackId);
            if (!string.Equals(pack.Revision, start.PackRevision, StringComparison.Ordinal) ||
                !string.Equals(pack.ContentSha256, start.PackSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Qualification pack changed; this run cannot be resumed with a different manifest.");
            EnsureShellPack(pack, host);
            return runner.Restore(pack, CurrentContext(host), runId.Trim());
        }

        private void EnsureShellPack(QualificationPack pack, string host)
        {
            var availability = _catalog.List(host, pack.Suite, _capabilities)
                .FirstOrDefault(item => string.Equals(item.Pack.Id, pack.Id, StringComparison.OrdinalIgnoreCase));
            if (availability == null || !availability.Available)
                throw new InvalidOperationException("Qualification pack is unavailable for this host or build.");
            if (!string.Equals(pack.WorkspacePolicy, "read-only", StringComparison.Ordinal) ||
                pack.Steps.Any(step => step.Kind == QualificationStepKind.AgentTask ||
                    step.Kind == QualificationStepKind.HostProbe ||
                    step.Kind == QualificationStepKind.Fixture ||
                    step.Kind == QualificationStepKind.Confirmation ||
                    step.Kind == QualificationStepKind.Restart ||
                    step.Kind == QualificationStepKind.Fault))
                throw new InvalidOperationException("WQ-A2 accepts only the read-only shell pack.");
        }

        private QualificationRunner Runner(QualificationEventJournal journal)
        {
            return new QualificationRunner(new ShellActions(), new ShellVerifier(journal), journal);
        }

        private QualificationEventJournal Journal(ChatSession session)
        {
            return new QualificationEventJournal(_events, session);
        }

        private QualificationRunContext CurrentContext(string host)
        {
            return new QualificationRunContext(host, ApplicationVersionService.Current,
                "unavailable", "development", _capabilities);
        }

        private static bool HasDurableTerminal(IReadOnlyList<QualificationJournalRecord> records)
        {
            return records != null && records.Count > 0 &&
                records[records.Count - 1].Kind == QualificationRunEventKind.RunCompleted;
        }

        private static string NormalizeSuite(string suite)
        {
            return string.IsNullOrWhiteSpace(suite) ? "quick" : suite.Trim().ToLowerInvariant();
        }

        private sealed class ShellActions : IQualificationActionExecutor
        {
            public bool Supports(QualificationStep step)
            {
                return step != null &&
                    (step.Kind == QualificationStepKind.Precondition &&
                     string.Equals(step.Action, "qualification.shell.preflight", StringComparison.Ordinal) ||
                     step.Kind == QualificationStepKind.Cleanup &&
                     string.Equals(step.Action, "qualification.shell.cleanup", StringComparison.Ordinal));
            }

            public Task<QualificationActionResult> ExecuteAsync(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(context.Step.Action, "qualification.shell.preflight", StringComparison.Ordinal))
                {
                    return Task.FromResult(QualificationActionResult.Passed(new JObject
                    {
                        ["runner"] = "reachable",
                        ["eventStore"] = "same-chat-stream",
                        ["workspacePolicy"] = context.Pack.WorkspacePolicy
                    }.ToString(Formatting.None), "Qualification shell preflight completed."));
                }
                if (string.Equals(context.Step.Action, "qualification.shell.cleanup", StringComparison.Ordinal))
                {
                    return Task.FromResult(QualificationActionResult.Passed(
                        "{\"closed\":true}", "Qualification shell cleanup completed."));
                }
                return Task.FromResult(QualificationActionResult.Blocked(
                    "action_not_allowlisted", "Qualification shell action is not allowlisted."));
            }
        }

        private sealed class ShellVerifier : IQualificationVerifier
        {
            private readonly IQualificationRunJournal _journal;

            internal ShellVerifier(IQualificationRunJournal journal)
            {
                _journal = journal;
            }

            public bool Supports(QualificationStep step)
            {
                return step != null && step.Kind == QualificationStepKind.Assertion &&
                    string.Equals(step.Assertion, "qualification.shell.roundtrip", StringComparison.Ordinal);
            }

            public Task<QualificationVerificationResult> VerifyAsync(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = _journal.Read(context.RunId)
                    .Where(item => item.Kind == QualificationRunEventKind.StepCompleted)
                    .ToDictionary(item => item.Data.StepId, item => item.Data, StringComparer.Ordinal);
                QualificationRunEventData preflight;
                QualificationRunEventData acknowledged;
                var actual = new JObject
                {
                    ["preflightPersisted"] = completed.TryGetValue("preflight", out preflight) &&
                        string.Equals(preflight.StepOutcome, "passed", StringComparison.Ordinal),
                    ["manualCheckpointPersisted"] = completed.TryGetValue("acknowledge", out acknowledged) &&
                        string.Equals(acknowledged.StepOutcome, "passed", StringComparison.Ordinal) &&
                        string.Equals(acknowledged.EvidenceStrength, "manual", StringComparison.Ordinal)
                };
                var expected = new JObject
                {
                    ["preflightPersisted"] = true,
                    ["manualCheckpointPersisted"] = true
                };
                var expectedJson = expected.ToString(Formatting.None);
                var actualJson = actual.ToString(Formatting.None);
                var passed = JToken.DeepEquals(expected, actual);
                return Task.FromResult(passed
                    ? QualificationVerificationResult.Passed(expectedJson, actualJson,
                        "Durable shell evidence matched.")
                    : QualificationVerificationResult.Failed("shell_evidence_mismatch",
                        "Required durable shell evidence is missing.", expectedJson, actualJson));
            }
        }
    }
}
