using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Persistence
{
    public enum SessionEventLane
    {
        Agent = 1,
        DomainDiagnostic = 2
    }

    public enum SessionEventAuthority
    {
        Authority = 1,
        Diagnostic = 2
    }

    public enum SessionEventDurability
    {
        Mandatory = 1,
        BestEffort = 2
    }

    public enum SessionEventWriteScope
    {
        StorageInternal = 1,
        EventPort = 2
    }

    public enum SessionEventKind
    {
        Unknown = 0,
        ModelRequestPrepared = 1,
        ModelResponseReceived = 2,
        ModelFailure = 3,
        ModelStreamChunk = 4,
        ModelAttemptRejected = 5,
        ModelResponseAccepted = 6,
        ToolPackExtensionAccepted = 7,
        ToolPackExtensionRejected = 8,
        RunStartedObservation = 9,
        RunSummaryCreated = 10,
        UiProjected = 11,
        ToolExecutionStartedObservation = 12,
        ToolExecutionCompletedObservation = 13,
        DomainEffectPrepared = 14,
        DomainEffectDispatched = 15,
        DomainEffectVerified = 16,
        SessionCreated = 17,
        SessionForked = 18,
        SessionCommit = 19,
        TurnStarted = 20,
        TurnEnded = 21,
        StepStarted = 22,
        StepEnded = 23
    }

    public sealed class SessionEventDescriptor
    {
        internal SessionEventDescriptor(
            SessionEventKind kind,
            string type,
            SessionEventLane lane,
            SessionEventAuthority authority,
            SessionEventDurability durability,
            SessionEventWriteScope writeScope)
        {
            Kind = kind;
            Type = type;
            Lane = lane;
            Authority = authority;
            Durability = durability;
            WriteScope = writeScope;
        }

        public SessionEventKind Kind { get; private set; }
        public string Type { get; private set; }
        public SessionEventLane Lane { get; private set; }
        public SessionEventAuthority Authority { get; private set; }
        public SessionEventDurability Durability { get; private set; }
        public SessionEventWriteScope WriteScope { get; private set; }
    }

    public static class SessionEventDescriptors
    {
        private static readonly IReadOnlyList<SessionEventDescriptor> Descriptors =
            Array.AsReadOnly(new[]
            {
                Storage(SessionEventKind.SessionCreated, SessionEventTypes.SessionCreated),
                Storage(SessionEventKind.SessionForked, SessionEventTypes.SessionForked),
                Storage(SessionEventKind.SessionCommit, SessionEventTypes.SessionCommit),
                Storage(SessionEventKind.TurnStarted, SessionEventTypes.TurnStarted),
                Storage(SessionEventKind.TurnEnded, SessionEventTypes.TurnEnded),
                Storage(SessionEventKind.StepStarted, SessionEventTypes.StepStarted),
                Storage(SessionEventKind.StepEnded, SessionEventTypes.StepEnded),
                Agent(SessionEventKind.ModelRequestPrepared, SessionEventTypes.LlmRequest,
                    SessionEventAuthority.Authority, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ModelResponseReceived, SessionEventTypes.LlmResponse,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ModelFailure, SessionEventTypes.LlmFailure,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ModelStreamChunk, SessionEventTypes.AssistantChunk,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ModelAttemptRejected, SessionEventTypes.AgentResponseRejected,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ModelResponseAccepted, SessionEventTypes.ModelResponseAccepted,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.BestEffort),
                Agent(SessionEventKind.ToolPackExtensionAccepted, SessionEventTypes.ToolPackExtensionAccepted,
                    SessionEventAuthority.Authority, SessionEventDurability.Mandatory),
                Agent(SessionEventKind.ToolPackExtensionRejected, SessionEventTypes.ToolPackExtensionRejected,
                    SessionEventAuthority.Diagnostic, SessionEventDurability.Mandatory),
                Diagnostic(SessionEventKind.RunStartedObservation, SessionEventTypes.RunStartedObservation),
                Diagnostic(SessionEventKind.RunSummaryCreated, SessionEventTypes.RunSummaryCreated),
                Diagnostic(SessionEventKind.UiProjected, SessionEventTypes.UiProjected),
                Diagnostic(SessionEventKind.ToolExecutionStartedObservation,
                    SessionEventTypes.ToolExecutionStartedObservation),
                Diagnostic(SessionEventKind.ToolExecutionCompletedObservation,
                    SessionEventTypes.ToolExecutionCompletedObservation),
                Diagnostic(SessionEventKind.DomainEffectPrepared, SessionEventTypes.DomainEffectPrepared),
                Diagnostic(SessionEventKind.DomainEffectDispatched, SessionEventTypes.DomainEffectDispatched),
                Diagnostic(SessionEventKind.DomainEffectVerified, SessionEventTypes.DomainEffectVerified)
            });

        private static readonly Dictionary<SessionEventKind, SessionEventDescriptor> ByKind = BuildByKind();
        private static readonly Dictionary<string, SessionEventDescriptor> ByType = BuildByType();

        public static IReadOnlyList<SessionEventDescriptor> All
        {
            get { return Descriptors; }
        }

        public static SessionEventDescriptor For(SessionEventKind kind)
        {
            SessionEventDescriptor descriptor;
            if (!ByKind.TryGetValue(kind, out descriptor))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported session event kind.");
            return descriptor;
        }

        public static bool TryForType(string type, out SessionEventDescriptor descriptor)
        {
            return ByType.TryGetValue(type ?? string.Empty, out descriptor);
        }

        internal static void EnsureCanonical(SessionEventDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (!ReferenceEquals(For(descriptor.Kind), descriptor))
                throw new InvalidOperationException("Session event descriptor must come from the closed catalog.");
        }

        internal static void EnsureEventPortWritable(SessionEventDescriptor descriptor)
        {
            EnsureCanonical(descriptor);
            if (descriptor.WriteScope != SessionEventWriteScope.EventPort)
                throw new InvalidOperationException("Storage-internal session events cannot be appended through IEventStore.");
        }

        private static SessionEventDescriptor Agent(
            SessionEventKind kind,
            string type,
            SessionEventAuthority authority,
            SessionEventDurability durability)
        {
            return new SessionEventDescriptor(kind, type, SessionEventLane.Agent, authority, durability,
                SessionEventWriteScope.EventPort);
        }

        private static SessionEventDescriptor Diagnostic(SessionEventKind kind, string type)
        {
            return new SessionEventDescriptor(kind, type, SessionEventLane.DomainDiagnostic,
                SessionEventAuthority.Diagnostic, SessionEventDurability.BestEffort,
                SessionEventWriteScope.EventPort);
        }

        private static SessionEventDescriptor Storage(SessionEventKind kind, string type)
        {
            return new SessionEventDescriptor(kind, type, SessionEventLane.Agent,
                SessionEventAuthority.Authority, SessionEventDurability.Mandatory,
                SessionEventWriteScope.StorageInternal);
        }

        private static Dictionary<SessionEventKind, SessionEventDescriptor> BuildByKind()
        {
            var result = new Dictionary<SessionEventKind, SessionEventDescriptor>();
            foreach (var descriptor in Descriptors)
            {
                if (descriptor == null || descriptor.Kind == SessionEventKind.Unknown ||
                    string.IsNullOrWhiteSpace(descriptor.Type) || result.ContainsKey(descriptor.Kind))
                    throw new InvalidOperationException("The session event descriptor catalog is invalid.");
                result.Add(descriptor.Kind, descriptor);
            }
            foreach (SessionEventKind kind in Enum.GetValues(typeof(SessionEventKind)))
            {
                if (kind != SessionEventKind.Unknown && !result.ContainsKey(kind))
                    throw new InvalidOperationException("The session event descriptor catalog is incomplete.");
            }
            return result;
        }

        private static Dictionary<string, SessionEventDescriptor> BuildByType()
        {
            var result = new Dictionary<string, SessionEventDescriptor>(StringComparer.Ordinal);
            foreach (var descriptor in Descriptors)
            {
                if (result.ContainsKey(descriptor.Type))
                    throw new InvalidOperationException("Session event types must be unique in the descriptor catalog.");
                result.Add(descriptor.Type, descriptor);
            }
            return result;
        }
    }

    public sealed class SessionEventCorrelation
    {
        public SessionEventCorrelation(string runId, string turnId, string stepId)
        {
            RunId = runId;
            TurnId = turnId;
            StepId = stepId;
        }

        public string RunId { get; private set; }
        public string TurnId { get; private set; }
        public string StepId { get; private set; }
    }

    public sealed class SessionEventPayload
    {
        private SessionEventPayload(string text, byte[] bytes, string contentType)
        {
            Text = text;
            Bytes = bytes;
            ContentType = contentType;
        }

        internal string Text { get; private set; }
        internal byte[] Bytes { get; private set; }
        public string ContentType { get; private set; }

        public static SessionEventPayload FromText(string text, string contentType)
        {
            return text == null ? null : new SessionEventPayload(text, null, contentType);
        }

        public static SessionEventPayload FromBytes(byte[] bytes, string contentType)
        {
            return bytes == null ? null : new SessionEventPayload(null, (byte[])bytes.Clone(), contentType);
        }
    }

    public sealed class SessionEventWrite
    {
        public SessionEventWrite(
            SessionEventDescriptor descriptor,
            object data,
            SessionEventPayload payload,
            SessionEventCorrelation correlation)
        {
            SessionEventDescriptors.EnsureEventPortWritable(descriptor);
            Descriptor = descriptor;
            Data = data;
            Payload = payload;
            Correlation = correlation;
        }

        public SessionEventDescriptor Descriptor { get; private set; }
        public object Data { get; private set; }
        public SessionEventPayload Payload { get; private set; }
        public SessionEventCorrelation Correlation { get; private set; }
    }

    public enum SessionEventReadMode
    {
        Validated = 1,
        RequireComplete = 2
    }

    public interface IEventStore
    {
        SessionEvent Append(ChatSession session, SessionEventWrite write);
        IReadOnlyList<SessionEvent> Read(ChatSession session, SessionEventReadMode mode);
        string ReadPayload(ChatSession session, SessionEvent sessionEvent);
    }
}
