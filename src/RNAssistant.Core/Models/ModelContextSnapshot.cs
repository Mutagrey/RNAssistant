using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Models
{
    // The one frozen request projection. Only mutable metadata is detached;
    // immutable bodies, refs and delivered observations are shared values.
    public sealed class ModelContextSnapshot
    {
        private readonly ChatMessage[] _messages;
        private readonly ContextReceipt _receipt;
        private readonly IReadOnlyList<ResourceEvidence> _deliveredObservations;
        public string Id { get; private set; }
        public ModelAuthoritySnapshot Authority { get; private set; }
        public IReadOnlyList<ChatMessage> Messages
        { get { return Array.AsReadOnly(_messages.Select(item => item?.CopyForContext()).ToArray()); } }
        public IReadOnlyList<ResourceEvidence> DeliveredObservations { get { return _deliveredObservations; } }
        public ContextReceipt Receipt { get { return _receipt.CopyForContext(); } }

        public ModelContextSnapshot(ModelAuthoritySnapshot authority, IEnumerable<ChatMessage> messages,
            ContextReceipt receipt, IEnumerable<ResourceEvidence> deliveredObservations)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            Id = receipt.SnapshotId;
            Authority = authority;
            _messages = (messages ?? throw new ArgumentNullException(nameof(messages))).Select(item => item?.CopyForContext()).ToArray();
            _receipt = receipt.CopyForContext();
            _deliveredObservations = Array.AsReadOnly((deliveredObservations ??
                throw new ArgumentNullException(nameof(deliveredObservations))).ToArray());
        }
    }
}
