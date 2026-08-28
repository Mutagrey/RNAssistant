namespace RNAssistant.Core.ModelProtocol
{
    // Read-only conversion of an explicitly identified historical v2 JSON envelope.
    // Never a live-parser fallback; never grants execution authority to old tool names.
    // Owner: ModelProtocol. Wire history consumers in Phase 2C2; remove in Phase 10.
    public static class ConversationResponseV2Adapter
    {
        public static ConversationResponseParseResult Read(string acceptedV2Envelope)
        {
            // Status is checked only as the v2 discriminator, then discarded. Whether
            // the model requested more work follows solely from the call list.
            return ConversationResponseJson.Read(acceptedV2Envelope, true);
        }
    }
}
