using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    // Bulk fixture setup only. Production has no snapshot Save API; tests of
    // event semantics must use the same explicit commits as their actual owner.
    internal static class ConversationStoreFixture
    {
        internal static void Save(this ChatStore store, ChatSession session)
        { new ChatConversationStoreAdapter(store).Save(session); }

        internal static void Save(this IConversationStore store, ChatSession session)
        {
            ChatSessionNormalizer.Normalize(session, session.Host, session.DocumentKey, session.DocumentTitle);
            store.Commit(session, new ConversationCommit(session.Messages));
        }
    }
}
