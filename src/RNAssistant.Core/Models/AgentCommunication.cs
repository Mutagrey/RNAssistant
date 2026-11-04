using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RNAssistant.Core.Models
{
    // Persisted with the accepted call, never inferred from a tool's display text.
    public sealed class AgentWaitCondition
    {
        public DateTime RegisteredUtc { get; private set; }
        public DateTime DeadlineUtc { get; private set; }
        public IReadOnlyList<string> RequestIds { get; private set; }
        public bool All { get; private set; }

        [JsonConstructor]
        public AgentWaitCondition(DateTime registeredUtc, DateTime deadlineUtc, IEnumerable<string> requestIds = null, bool all = false)
        {
            var ids = (requestIds ?? new string[0]).Take(33).ToArray();
            if (registeredUtc.Kind != DateTimeKind.Utc || deadlineUtc.Kind != DateTimeKind.Utc ||
                deadlineUtc <= registeredUtc || deadlineUtc - registeredUtc > TimeSpan.FromHours(1) ||
                ids.Length > 32 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 64) ||
                ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || all && ids.Length == 0)
                throw new ArgumentException("Invalid bounded agent wait condition.");
            RegisteredUtc = registeredUtc; DeadlineUtc = deadlineUtc;
            RequestIds = Array.AsReadOnly(ids); All = all;
        }
    }

    // Runtime-authored provenance. A chat address is public; document authority is not.
    public sealed class AgentChatAddress
    {
        public string Source { get; set; }
        public string WorkspaceId { get; set; }
        public string ChatId { get; set; }
        public string AgentId { get { return IdFor(ChatId); } }
        public string Title { get; set; }
        public string ScopeTitle { get; set; }

        // Stable across view order, titles and restarts. The exact storage ID stays
        // runtime-only; callers must reject an ambiguous public address.
        public static string IdFor(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return null;
            const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(chatId));
                var id = new StringBuilder("a-");
                for (var bit = 0; bit < 50; bit += 5)
                {
                    var value = (bytes[bit / 8] << 8) | bytes[bit / 8 + 1];
                    id.Append(alphabet[(value >> (11 - bit % 8)) & 31]);
                }
                return id.ToString();
            }
        }
    }

    public sealed class AgentCommunicationContext
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string MessageId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string ReplyTo { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ExpectReply { get; set; }
        public string ChainId { get; set; }
        public int Limit { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentChatAddress Sender { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string SenderRunId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public AgentChatAddress Root { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Services.ConversationRunPolicy Policy { get; set; }
    }

    // Creation identity is conversation metadata, never an instruction or a grant.
    public sealed class AgentThreadCreation
    {
        public string OperationId { get; set; }
        public string Fingerprint { get; set; }
        public string ChainId { get; set; }
        public AgentChatAddress Creator { get; set; }
        public AgentChatAddress Root { get; set; }
    }
}
