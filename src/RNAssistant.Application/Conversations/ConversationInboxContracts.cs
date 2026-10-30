using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Application.Conversations
{
    internal sealed class ConversationInputUpdate
    {
        internal string InputId { get; set; }
        internal long ExpectedRevision { get; set; }
        internal string Text { get; set; }
    }

    internal sealed class ConversationInboxReceipt
    {
        internal string Id { get; set; }
        internal string OperationId { get; set; }
        internal long Revision { get; set; }
        internal ConversationInputStatus Status { get; set; }
    }

    internal sealed class ConversationInboxSnapshot
    {
        internal string ChatId { get; set; }
        internal string Epoch { get; set; }
        internal long Revision { get; set; }
        internal List<ConversationInput> Items { get; set; }
        internal List<ConversationInboxReceipt> Receipts { get; set; }
        internal bool Paused { get; set; }
        internal string PauseReason { get; set; }
        internal string Phase { get; set; }
        internal bool Running { get; set; }
        internal bool ActiveRun { get; set; }
    }
}
