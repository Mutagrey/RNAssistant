using System;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    internal sealed class ResourceCallLink
    {
        public ResourceMutationOrigin Origin { get; private set; }
        public JournalCursor Accepted { get; private set; }
        public JournalCursor Started { get; private set; }
        [JsonConstructor]
        internal ResourceCallLink(ResourceMutationOrigin origin, JournalCursor accepted, JournalCursor started)
        {
            if (origin == null || accepted == null || started == null || started.Sequence <= accepted.Sequence)
                throw new ArgumentException("Exact accepted-call and runtime-entry positions are required.");
            Origin = origin; Accepted = accepted.Copy(); Started = started.Copy();
        }
    }

    // Runtime-owned binding recorded with the accepted call before resource
    // preparation/dispatch. It is not a model-facing URI or an execution request.
    internal sealed class ConversationResourceBinding
    {
        public string Owner { get { return "resources"; } }
        public ResourceAuthorityScopeId Scope { get; private set; }
        public string AttemptId { get; private set; }
        public ResourceIdentity Target { get; private set; }

        [JsonConstructor]
        internal ConversationResourceBinding(ResourceAuthorityScopeId scope, string attemptId, ResourceIdentity target, string owner = "resources")
        {
            if (scope == null || string.IsNullOrWhiteSpace(attemptId) || target == null || owner != "resources")
                throw new ArgumentException("Exact resource attempt owner/identity required.");
            Scope = scope; AttemptId = attemptId; Target = target;
        }

        internal bool Matches(ResourceOutcomeLink link)
        { return link != null && Scope.Equals(link.Scope) && AttemptId == link.AttemptId && Target.Equals(link.Target); }

        internal static ConversationResourceBinding ForCall(string conversationScope, string chat, string run, string call,
            ResourceAuthorityScopeId scope, ResourceIdentity target)
        {
            var identity = JsonConvert.SerializeObject(new { conversationScope, chat, run, call, scope, target });
            return new ConversationResourceBinding(scope, "ma_" + RNAssistant.Core.Tools.TextPatternEngine.Sha256(identity), target);
        }

        internal bool BelongsTo(string conversationScope, string chat, string run, string call)
        { return AttemptId == ForCall(conversationScope, chat, run, call, Scope, Target).AttemptId; }
    }

    internal sealed class ResourceOutcomeLink
    {
        public string Owner { get { return "resources"; } }
        public ResourceAuthorityScopeId Scope { get; private set; }
        public string AttemptId { get; private set; }
        public ResourceIdentity Target { get; private set; }
        public string Operation { get; private set; }
        public string Fingerprint { get; private set; }
        public string CommitId { get; private set; }
        private readonly JournalCursor _position;
        public JournalCursor Position { get { return _position.Copy(); } }

        [JsonConstructor]
        internal ResourceOutcomeLink(ResourceAuthorityScopeId scope, string attemptId, ResourceIdentity target,
            string operation, string fingerprint, string commitId, JournalCursor position, string owner = "resources")
        {
            if (scope == null || string.IsNullOrWhiteSpace(attemptId) || target == null || owner != "resources" ||
                string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(commitId) || position == null)
                throw new ArgumentException("Exact immutable resource outcome locator required.");
            Scope = scope; AttemptId = attemptId; Target = target; Operation = operation; Fingerprint = fingerprint;
            CommitId = commitId; _position = position.Copy();
        }

        internal static ResourceOutcomeLink From(ResourceJournalReceipt receipt)
        {
            var publication = receipt?.Fact.Publication;
            if (receipt?.Fact.Authority != null && receipt.Fact.Attempt != null)
                return new ResourceOutcomeLink(receipt.Fact.Scope, receipt.Fact.Attempt.AttemptId, receipt.Fact.Attempt.Target,
                    receipt.Operation, receipt.Fingerprint, receipt.Fact.Authority.CommitId, receipt.Position);
            if (publication == null || publication.AttemptId == null) throw new ArgumentException("A published exact attempt receipt is required.");
            return new ResourceOutcomeLink(publication.Scope, publication.AttemptId, publication.Target,
                receipt.Operation, receipt.Fingerprint, publication.CommitId, receipt.Position);
        }
    }
}
