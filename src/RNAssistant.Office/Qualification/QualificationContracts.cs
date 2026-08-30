using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office.Qualification
{
    public enum QualificationStepKind
    {
        Precondition = 1,
        Fixture = 2,
        AgentTask = 3,
        HostProbe = 4,
        UserAction = 5,
        Confirmation = 6,
        Restart = 7,
        Fault = 8,
        Assertion = 9,
        Cleanup = 10
    }

    public enum QualificationRunStatus
    {
        Ready = 1,
        Running = 2,
        AwaitingUser = 3,
        Verifying = 4,
        Passed = 5,
        Failed = 6,
        Blocked = 7,
        Cancelled = 8
    }

    public enum QualificationStepOutcome
    {
        NotRun = 1,
        Running = 2,
        AwaitingUser = 3,
        Passed = 4,
        Failed = 5,
        Blocked = 6,
        Cancelled = 7,
        Unknown = 8
    }

    public enum QualificationEvidenceStrength
    {
        None = 1,
        Automatic = 2,
        Manual = 3
    }

    public sealed class QualificationStep
    {
        internal QualificationStep(string id, QualificationStepKind kind, string title,
            IReadOnlyList<string> dependsOn, string instructionKey, string action,
            string assertion, string prompt, int timeoutSeconds, bool required)
        {
            Id = id;
            Kind = kind;
            Title = title;
            DependsOn = dependsOn;
            InstructionKey = instructionKey;
            Action = action;
            Assertion = assertion;
            Prompt = prompt;
            TimeoutSeconds = timeoutSeconds;
            Required = required;
        }

        public string Id { get; private set; }
        public QualificationStepKind Kind { get; private set; }
        public string Title { get; private set; }
        public IReadOnlyList<string> DependsOn { get; private set; }
        public string InstructionKey { get; private set; }
        public string Action { get; private set; }
        public string Assertion { get; private set; }
        public string Prompt { get; private set; }
        public int TimeoutSeconds { get; private set; }
        public bool Required { get; private set; }
    }

    public sealed class QualificationPack
    {
        internal QualificationPack(string id, string revision, string contentSha256, string title,
            string description, IReadOnlyList<string> hosts, string suite, string workspacePolicy,
            IReadOnlyList<string> requirements, IReadOnlyList<string> coverage,
            IReadOnlyList<QualificationStep> steps)
        {
            Id = id;
            Revision = revision;
            ContentSha256 = contentSha256;
            Title = title;
            Description = description;
            Hosts = hosts;
            Suite = suite;
            WorkspacePolicy = workspacePolicy;
            Requirements = requirements;
            Coverage = coverage;
            Steps = steps;
        }

        public string Id { get; private set; }
        public string Revision { get; private set; }
        public string ContentSha256 { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public IReadOnlyList<string> Hosts { get; private set; }
        public string Suite { get; private set; }
        public string WorkspacePolicy { get; private set; }
        public IReadOnlyList<string> Requirements { get; private set; }
        public IReadOnlyList<string> Coverage { get; private set; }
        public IReadOnlyList<QualificationStep> Steps { get; private set; }
    }

    public sealed class QualificationRunContext
    {
        public QualificationRunContext(string host, string productVersion, string buildCommit, string channel,
            IEnumerable<string> capabilities)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
            if (string.IsNullOrWhiteSpace(productVersion)) throw new ArgumentException("Product version is required.", nameof(productVersion));
            if (string.IsNullOrWhiteSpace(buildCommit)) throw new ArgumentException("Build commit is required.", nameof(buildCommit));
            Host = host.Trim();
            ProductVersion = productVersion.Trim();
            BuildCommit = buildCommit.Trim();
            Channel = string.IsNullOrWhiteSpace(channel) ? "development" : channel.Trim();
            if (Host.Length > 32 || ProductVersion.Length > 64 || BuildCommit.Length > 128 || Channel.Length > 32)
                throw new ArgumentException("Qualification run provenance contains an overlong value.");
            var capabilityList = new List<string>(capabilities ?? new string[0])
                .Select(value => value == null ? null : value.Trim())
                .ToList();
            if (capabilityList.Count > 256 || capabilityList.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 96) ||
                capabilityList.Any(value => !Regex.IsMatch(value,
                    "^[A-Za-z][A-Za-z0-9]*(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)) ||
                capabilityList.Distinct(StringComparer.OrdinalIgnoreCase).Count() != capabilityList.Count)
                throw new ArgumentException("Qualification capabilities must be unique bounded identifiers.", nameof(capabilities));
            Capabilities = Array.AsReadOnly(capabilityList.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        public string Host { get; private set; }
        public string ProductVersion { get; private set; }
        public string BuildCommit { get; private set; }
        public string Channel { get; private set; }
        public IReadOnlyList<string> Capabilities { get; private set; }
    }

    public sealed class QualificationActionResult
    {
        private QualificationActionResult(QualificationStepOutcome outcome, string code, string message,
            string actualJson, string domainEffect)
        {
            if (outcome == QualificationStepOutcome.NotRun || outcome == QualificationStepOutcome.Running ||
                outcome == QualificationStepOutcome.AwaitingUser)
                throw new ArgumentOutOfRangeException(nameof(outcome));
            Outcome = outcome;
            Code = code;
            Message = message;
            ActualJson = actualJson;
            DomainEffect = domainEffect;
        }

        public QualificationStepOutcome Outcome { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string ActualJson { get; private set; }
        public string DomainEffect { get; private set; }

        public static QualificationActionResult Passed(string actualJson, string message = null,
            string domainEffect = null)
        {
            return new QualificationActionResult(QualificationStepOutcome.Passed, "completed", message,
                actualJson, domainEffect);
        }

        public static QualificationActionResult Failed(string code, string message, string actualJson = null,
            string domainEffect = "error")
        {
            return new QualificationActionResult(QualificationStepOutcome.Failed, code, message,
                actualJson, domainEffect);
        }

        public static QualificationActionResult Blocked(string code, string message, string actualJson = null)
        {
            return new QualificationActionResult(QualificationStepOutcome.Blocked, code, message, actualJson, null);
        }

        public static QualificationActionResult Cancelled(string message = null)
        {
            return new QualificationActionResult(QualificationStepOutcome.Cancelled, "cancelled", message, null, null);
        }

        public static QualificationActionResult Unknown(string code, string message, string actualJson = null,
            string domainEffect = "unknown")
        {
            return new QualificationActionResult(QualificationStepOutcome.Unknown, code, message, actualJson, domainEffect);
        }
    }

    public sealed class QualificationVerificationResult
    {
        private QualificationVerificationResult(QualificationStepOutcome outcome, string code, string message,
            string expectedJson, string actualJson, string domainEffect)
        {
            if (outcome != QualificationStepOutcome.Passed && outcome != QualificationStepOutcome.Failed &&
                outcome != QualificationStepOutcome.Blocked && outcome != QualificationStepOutcome.Unknown)
                throw new ArgumentOutOfRangeException(nameof(outcome));
            Outcome = outcome;
            Code = code;
            Message = message;
            ExpectedJson = expectedJson;
            ActualJson = actualJson;
            DomainEffect = domainEffect;
        }

        public QualificationStepOutcome Outcome { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string ExpectedJson { get; private set; }
        public string ActualJson { get; private set; }
        public string DomainEffect { get; private set; }

        public static QualificationVerificationResult Passed(string expectedJson, string actualJson,
            string message = null, string domainEffect = null)
        {
            return new QualificationVerificationResult(QualificationStepOutcome.Passed, "verified", message,
                expectedJson, actualJson, domainEffect);
        }

        public static QualificationVerificationResult Failed(string code, string message,
            string expectedJson, string actualJson, string domainEffect = "error")
        {
            return new QualificationVerificationResult(QualificationStepOutcome.Failed, code, message,
                expectedJson, actualJson, domainEffect);
        }

        public static QualificationVerificationResult Blocked(string code, string message, string actualJson = null)
        {
            return new QualificationVerificationResult(QualificationStepOutcome.Blocked, code, message,
                null, actualJson, null);
        }

        public static QualificationVerificationResult Unknown(string code, string message,
            string expectedJson = null, string actualJson = null, string domainEffect = "unknown")
        {
            return new QualificationVerificationResult(QualificationStepOutcome.Unknown, code, message,
                expectedJson, actualJson, domainEffect);
        }
    }

    public sealed class QualificationStepExecutionContext
    {
        internal QualificationStepExecutionContext(string runId, string attemptId, QualificationPack pack,
            QualificationStep step, QualificationRunContext runContext)
        {
            RunId = runId;
            AttemptId = attemptId;
            Pack = pack;
            Step = step;
            RunContext = runContext;
        }

        public string RunId { get; private set; }
        public string AttemptId { get; private set; }
        public QualificationPack Pack { get; private set; }
        public QualificationStep Step { get; private set; }
        public QualificationRunContext RunContext { get; private set; }
    }

    public interface IQualificationActionExecutor
    {
        bool Supports(QualificationStep step);
        Task<QualificationActionResult> ExecuteAsync(QualificationStepExecutionContext context,
            CancellationToken cancellationToken);
    }

    public interface IQualificationVerifier
    {
        bool Supports(QualificationStep step);
        Task<QualificationVerificationResult> VerifyAsync(QualificationStepExecutionContext context,
            CancellationToken cancellationToken);
    }

    public sealed class QualificationManualInput
    {
        public string StepId { get; set; }
        public bool Acknowledged { get; set; }
        public string Note { get; set; }
    }

    public sealed class QualificationStepSnapshot
    {
        internal QualificationStepSnapshot(QualificationStep step)
        {
            StepId = step.Id;
            Kind = step.Kind;
            Outcome = QualificationStepOutcome.NotRun;
            EvidenceStrength = QualificationEvidenceStrength.None;
        }

        public string StepId { get; internal set; }
        public QualificationStepKind Kind { get; internal set; }
        public QualificationStepOutcome Outcome { get; internal set; }
        public QualificationEvidenceStrength EvidenceStrength { get; internal set; }
        public string AttemptId { get; internal set; }
        public string Code { get; internal set; }
        public string Message { get; internal set; }
        public string ExpectedJson { get; internal set; }
        public string ActualJson { get; internal set; }
        public string DomainEffect { get; internal set; }
        public string StartedEventId { get; internal set; }
        public long? StartedSequence { get; internal set; }
        public string CompletedEventId { get; internal set; }
        public long? CompletedSequence { get; internal set; }
    }

    public sealed class QualificationRunState
    {
        internal QualificationRunState(string runId, string previousRunId, QualificationPack pack,
            QualificationRunContext context, DateTime startedUtc)
        {
            RunId = runId;
            PreviousRunId = previousRunId;
            Pack = pack;
            Context = context;
            StartedUtc = startedUtc;
            Status = QualificationRunStatus.Ready;
            MutableSteps = new List<QualificationStepSnapshot>();
            foreach (var step in pack.Steps) MutableSteps.Add(new QualificationStepSnapshot(step));
        }

        internal List<QualificationStepSnapshot> MutableSteps { get; private set; }
        internal int CurrentStepIndex { get; set; }
        internal QualificationRunStatus? PendingTerminalStatus { get; set; }
        internal bool TerminalPersisted { get; set; }
        internal bool Restorable { get; set; }

        public string RunId { get; private set; }
        public string PreviousRunId { get; private set; }
        public QualificationPack Pack { get; private set; }
        public QualificationRunContext Context { get; private set; }
        public QualificationRunStatus Status { get; internal set; }
        public DateTime StartedUtc { get; private set; }
        public DateTime? CompletedUtc { get; internal set; }
        public string StartedEventId { get; internal set; }
        public long? StartedSequence { get; internal set; }
        public string CompletedEventId { get; internal set; }
        public long? CompletedSequence { get; internal set; }
        public bool CanResume { get { return Restorable && !TerminalPersisted; } }
        public bool HasDurableTerminal { get { return TerminalPersisted; } }
        public IReadOnlyList<QualificationStepSnapshot> Steps { get { return MutableSteps.AsReadOnly(); } }
    }

    public sealed class QualificationPersistenceException : InvalidOperationException
    {
        public QualificationPersistenceException(string message, QualificationRunState run, Exception innerException)
            : base(message, innerException)
        {
            Run = run;
        }

        public QualificationRunState Run { get; private set; }
    }
}
