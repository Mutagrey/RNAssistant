using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal enum ChatResourceMutationKind { Clear, Edit, Fork, DeleteMessage }

    // Runtime/UI-only intent. These operations are not model-callable tools.
    internal sealed class ChatResourceMutationIntent
    {
        internal ChatResourceMutationKind Kind { get; private set; }
        internal string MessageId { get; private set; }
        internal int MessageIndex { get; private set; }
        internal string Text { get; private set; }
        internal string SourceSessionId { get; private set; }
        internal long? SourceRevision { get; private set; }
        internal ResourceForkPlan Fork { get; private set; }

        internal ChatResourceMutationIntent(ChatResourceMutationKind kind, string messageId = null,
            int messageIndex = -1, string text = null, ChatSession source = null, ResourceForkPlan fork = null)
        {
            if (!Enum.IsDefined(typeof(ChatResourceMutationKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (kind == ChatResourceMutationKind.Fork && source == null) throw new ArgumentNullException(nameof(source));
            if (kind == ChatResourceMutationKind.Fork && fork == null) throw new ArgumentNullException(nameof(fork));
            Kind = kind; MessageId = messageId; MessageIndex = messageIndex; Text = text;
            SourceSessionId = source?.Id; SourceRevision = source?.Revision;
            if (fork != null && (kind != ChatResourceMutationKind.Fork || fork.SourceSessionId != source.Id))
                throw new ArgumentException("A copy plan must belong to this exact fork source.", nameof(fork));
            Fork = fork;
        }

        internal string Operation
        {
            get
            {
                switch (Kind)
                {
                    case ChatResourceMutationKind.Clear: return "common.chat_clear";
                    case ChatResourceMutationKind.Edit: return "common.chat_edit";
                    case ChatResourceMutationKind.Fork: return "common.chat_fork";
                    default: return "common.chat_delete_message";
                }
            }
        }

        internal IDictionary<string, object> Arguments()
        {
            return new Dictionary<string, object> { ["kind"] = Kind.ToString(), ["messageId"] = MessageId,
                ["messageIndex"] = MessageIndex, ["text"] = Text,
                ["sourceSessionId"] = SourceSessionId, ["sourceRevision"] = SourceRevision,
                ["copiedResources"] = Fork?.Heads };
        }
    }
}
