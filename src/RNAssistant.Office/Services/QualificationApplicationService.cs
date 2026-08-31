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
    public sealed class QualificationApplicationService : IDisposable
    {
        public const string ShellCapability = "qualification.shell.v1";

        private readonly IEventStore _events;
        private readonly QualificationPackCatalog _catalog;
        private readonly IQualificationHostPort _host;
        private readonly BuildEvidenceEvaluation _buildEvidence;
        private readonly IReadOnlyList<string> _capabilities;

        public QualificationApplicationService(IEventStore events)
            : this(events, QualificationBuiltInCatalog.Load(), null, null)
        {
        }

        public QualificationApplicationService(IEventStore events, IQualificationHostPort host)
            : this(events, QualificationBuiltInCatalog.Load(), host, null)
        {
        }

        internal QualificationApplicationService(IEventStore events, QualificationPackCatalog catalog,
            IQualificationHostPort host = null, BuildEvidenceEvaluation buildEvidence = null)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _host = host;
            _buildEvidence = buildEvidence ?? BuildEvidenceRuntime.Load(
                _catalog, typeof(QualificationApplicationService).Assembly);
            var capabilities = new[] { ShellCapability }
                .Concat(host == null ? new string[0] : host.QualificationCapabilities ?? new string[0])
                .Concat(_buildEvidence.Complete ? new[] { BuildEvidenceRuntime.Capability } : new string[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _capabilities = Array.AsReadOnly(capabilities);
        }

        public BuildEvidenceEvaluation BuildEvidence
        {
            get { return _buildEvidence; }
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
            EnsurePack(pack, session.Host);
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

        public void Dispose()
        {
            if (_host != null) _host.ReleaseQualificationResources();
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
            EnsurePack(pack, host);
            return runner.Restore(pack, CurrentContext(host), runId.Trim());
        }

        private void EnsurePack(QualificationPack pack, string host)
        {
            var availability = _catalog.List(host, pack.Suite, _capabilities)
                .FirstOrDefault(item => string.Equals(item.Pack.Id, pack.Id, StringComparison.OrdinalIgnoreCase));
            if (availability == null || !availability.Available)
                throw new InvalidOperationException("Qualification pack is unavailable for this host or build.");
        }

        private QualificationRunner Runner(QualificationEventJournal journal)
        {
            return new QualificationRunner(
                new ApplicationActions(new ShellActions(_buildEvidence), _host),
                new ApplicationVerifier(new ShellVerifier(journal, _buildEvidence), _host, journal),
                journal);
        }

        private QualificationEventJournal Journal(ChatSession session)
        {
            return new QualificationEventJournal(_events, session);
        }

        private QualificationRunContext CurrentContext(string host)
        {
            return new QualificationRunContext(host, _buildEvidence.Identity.ProductVersion,
                _buildEvidence.Identity.CommitSha, _buildEvidence.Identity.Channel, _capabilities,
                _buildEvidence.EnvelopeSha256);
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
            private readonly BuildEvidenceEvaluation _buildEvidence;

            internal ShellActions(BuildEvidenceEvaluation buildEvidence)
            {
                _buildEvidence = buildEvidence ?? throw new ArgumentNullException(nameof(buildEvidence));
            }

            public bool Supports(QualificationStep step)
            {
                return step != null &&
                    (step.Kind == QualificationStepKind.Precondition &&
                     string.Equals(step.Action, "qualification.shell.preflight", StringComparison.Ordinal) ||
                     step.Kind == QualificationStepKind.Precondition &&
                     string.Equals(step.Action, "qualification.release.evidence.preflight", StringComparison.Ordinal) ||
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
                if (string.Equals(context.Step.Action, "qualification.release.evidence.preflight", StringComparison.Ordinal))
                {
                    return Task.FromResult(_buildEvidence.Complete
                        ? QualificationActionResult.Passed(_buildEvidence.ActualJson(),
                            "Signed exact-build evidence is compatible and complete.", "verified_no_change")
                        : QualificationActionResult.Blocked("build_evidence_incomplete",
                            "Signed exact-build evidence is not complete for this binary.",
                            _buildEvidence.ActualJson()));
                }
                return Task.FromResult(QualificationActionResult.Blocked(
                    "action_not_allowlisted", "Qualification shell action is not allowlisted."));
            }
        }

        private sealed class ShellVerifier : IQualificationVerifier
        {
            private readonly IQualificationRunJournal _journal;
            private readonly BuildEvidenceEvaluation _buildEvidence;

            internal ShellVerifier(IQualificationRunJournal journal, BuildEvidenceEvaluation buildEvidence)
            {
                _journal = journal;
                _buildEvidence = buildEvidence ?? throw new ArgumentNullException(nameof(buildEvidence));
            }

            public bool Supports(QualificationStep step)
            {
                return step != null && step.Kind == QualificationStepKind.Assertion &&
                    (string.Equals(step.Assertion, "qualification.shell.roundtrip", StringComparison.Ordinal) ||
                     string.Equals(step.Assertion, "qualification.release.evidence.complete", StringComparison.Ordinal));
            }

            public Task<QualificationVerificationResult> VerifyAsync(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(context.Step.Assertion, "qualification.release.evidence.complete", StringComparison.Ordinal))
                {
                    var expectedEvidence = BuildEvidenceEvaluation.ExpectedJson();
                    var actualEvidence = _buildEvidence.ActualJson();
                    return Task.FromResult(_buildEvidence.Complete
                        ? QualificationVerificationResult.Passed(expectedEvidence, actualEvidence,
                            "Complete signed release evidence matches this exact build.", "verified_no_change")
                        : QualificationVerificationResult.Failed("build_evidence_incomplete",
                            "Release evidence is incomplete or incompatible.", expectedEvidence, actualEvidence));
                }
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

        private sealed class ApplicationActions : IQualificationActionExecutor
        {
            private readonly IQualificationActionExecutor _shell;
            private readonly IQualificationHostPort _host;

            internal ApplicationActions(IQualificationActionExecutor shell, IQualificationHostPort host)
            {
                _shell = shell;
                _host = host;
            }

            public bool Supports(QualificationStep step)
            {
                return _shell.Supports(step) || _host != null && _host.SupportsQualificationAction(step);
            }

            public Task<QualificationActionResult> ExecuteAsync(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                var shell = _shell.Supports(context.Step);
                var host = _host != null && _host.SupportsQualificationAction(context.Step);
                if (shell == host)
                    throw new InvalidOperationException(shell
                        ? "Qualification action has more than one owner."
                        : "Qualification action is not allowlisted.");
                return shell
                    ? _shell.ExecuteAsync(context, cancellationToken)
                    : Task.FromResult(_host.ExecuteQualificationAction(context, cancellationToken));
            }
        }

        private sealed class ApplicationVerifier : IQualificationVerifier
        {
            private readonly IQualificationVerifier _shell;
            private readonly IQualificationHostPort _host;
            private readonly IQualificationRunJournal _journal;

            internal ApplicationVerifier(IQualificationVerifier shell, IQualificationHostPort host,
                IQualificationRunJournal journal)
            {
                _shell = shell;
                _host = host;
                _journal = journal;
            }

            public bool Supports(QualificationStep step)
            {
                return _shell.Supports(step) || _host != null && _host.SupportsQualificationAssertion(step);
            }

            public Task<QualificationVerificationResult> VerifyAsync(
                QualificationStepExecutionContext context,
                CancellationToken cancellationToken)
            {
                var shell = _shell.Supports(context.Step);
                var host = _host != null && _host.SupportsQualificationAssertion(context.Step);
                if (shell == host)
                    throw new InvalidOperationException(shell
                        ? "Qualification assertion has more than one owner."
                        : "Qualification assertion is not allowlisted.");
                if (shell) return _shell.VerifyAsync(context, cancellationToken);
                var evidence = _journal.Read(context.RunId)
                    .Where(item => item.Kind == QualificationRunEventKind.StepCompleted)
                    .Select(item => new QualificationRecordedStep(
                        item.Data.StepId,
                        QualificationManifestParser.ParseOutcome(item.Data.StepOutcome),
                        ParseStrength(item.Data.EvidenceStrength),
                        item.Data.ActualJson));
                return Task.FromResult(_host.VerifyQualificationAssertion(
                    context, new QualificationEvidenceSnapshot(evidence), cancellationToken));
            }

            private static QualificationEvidenceStrength ParseStrength(string value)
            {
                QualificationEvidenceStrength result;
                return Enum.TryParse(value, true, out result) ? result : QualificationEvidenceStrength.None;
            }
        }
    }
}
