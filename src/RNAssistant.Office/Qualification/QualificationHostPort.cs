using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RNAssistant.Office.Qualification
{
    public sealed class QualificationRecordedStep
    {
        public QualificationRecordedStep(string stepId, QualificationStepOutcome outcome,
            QualificationEvidenceStrength evidenceStrength, string actualJson)
        {
            if (string.IsNullOrWhiteSpace(stepId)) throw new ArgumentException("Step id is required.", nameof(stepId));
            StepId = stepId;
            Outcome = outcome;
            EvidenceStrength = evidenceStrength;
            ActualJson = actualJson;
        }

        public string StepId { get; private set; }
        public QualificationStepOutcome Outcome { get; private set; }
        public QualificationEvidenceStrength EvidenceStrength { get; private set; }
        public string ActualJson { get; private set; }
    }

    public sealed class QualificationEvidenceSnapshot
    {
        private readonly Dictionary<string, QualificationRecordedStep> _steps;

        public QualificationEvidenceSnapshot(IEnumerable<QualificationRecordedStep> steps)
        {
            _steps = (steps ?? new QualificationRecordedStep[0]).ToDictionary(
                item => item.StepId, item => item, StringComparer.Ordinal);
        }

        public IReadOnlyList<QualificationRecordedStep> Steps
        {
            get { return Array.AsReadOnly(_steps.Values.OrderBy(item => item.StepId, StringComparer.Ordinal).ToArray()); }
        }

        public QualificationRecordedStep Find(string stepId)
        {
            QualificationRecordedStep result;
            return _steps.TryGetValue(stepId ?? string.Empty, out result) ? result : null;
        }
    }

    // Host-owned, exact allowlist only. Manifests never name CLR types, commands or scripts.
    public interface IQualificationHostPort
    {
        IReadOnlyList<string> QualificationCapabilities { get; }
        bool SupportsQualificationAction(QualificationStep step);
        QualificationActionResult ExecuteQualificationAction(
            QualificationStepExecutionContext context,
            CancellationToken cancellationToken);
        bool SupportsQualificationAssertion(QualificationStep step);
        QualificationVerificationResult VerifyQualificationAssertion(
            QualificationStepExecutionContext context,
            QualificationEvidenceSnapshot evidence,
            CancellationToken cancellationToken);
        void ReleaseQualificationResources();
    }
}
