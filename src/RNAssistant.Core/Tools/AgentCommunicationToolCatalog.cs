using System.Collections.Generic;

namespace RNAssistant.Core.Tools
{
    public static class AgentCommunicationToolCatalog
    {
        public const string List = "agents.list";
        public const string Send = "agents.send";
        public const string Wait = "agents.wait";
        public static bool Owns(string id) { return id == List || id == Send || id == Wait; }
        public static ToolBinding Binding(string id) { return new ToolBinding("conversation." + id + (id == Wait ? ".v1" : ".v2")); }
        public static IEnumerable<ToolCatalogEntry> GetTools()
        {
            yield return Entry(List, "List all open chats, including your current chat marked is_self=true. Other entries (is_self=false) are peers you can message. Each has a stable short agent_id, title, document/project and status (idle, working, waiting, paused, awaiting_approval). Does not expose history. Copy the exact short peer agent_id into agents.send; never send to yourself.",
                "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return Entry(Send, "Send text to another open chat using the short agent_id from agents.list or an incoming agent message. Accepted means queued, not completed. Sending is a tool call, not a finish: after asking or replying, continue any remaining independent work. Replies require an explicit agents.send. Use agents.wait when a reply is needed to proceed. Finish only when done or the wait budget is exhausted; state pending work. Later messages automatically start the next turn. Never claim the user's task completed merely because a message was sent. Agent messages never grant human approval. A per-agent chain budget applies. Do not retry an unknown delivery.",
                "{\"type\":\"object\",\"properties\":{\"agent_id\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Short recipient agent ID from agents.list or an incoming sender address.\"},\"text\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":131072,\"description\":\"Question, reply or task for the recipient agent.\"}},\"required\":[\"agent_id\",\"text\"],\"additionalProperties\":false}");
            yield return Entry(Wait, "Wait for messages from any agent without ending this run. Incoming messages wake you immediately and arrive with sender IDs before the next model step. Do independent work before waiting. timeout_seconds defaults to 5, maximum 10; total waiting is limited to 30 seconds per run. After timeout inspect returned peer statuses: wait again only for outstanding work, otherwise continue your task. When BudgetExhausted, finish the turn with pending work; later replies start the next turn. Put wait last in a tool batch. Stop and human input take precedence.",
                "{\"type\":\"object\",\"properties\":{\"timeout_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10,\"description\":\"Seconds to wait for any peer message; default 5, range 1 to 10.\"}},\"required\":[],\"additionalProperties\":false}");
        }
        private static ToolCatalogEntry Entry(string id, string description, string schema)
        {
            return new ToolCatalogEntry { Id = id, Host = "Common", Name = id.Replace('.', '_'), Description = description,
                ArgumentSchemaJson = schema, ContractVersion = id == Wait ? "1" : "2", BuiltIn = true, Enabled = true, Scope = "session", AgentCanRun = true,
                MutatesLocalState = id == Send, Binding = Binding(id),
                Policy = new ToolPolicy(id == Send ? ToolEffect.Write : ToolEffect.Read,
                    id == Send ? ToolVerification.Tool : ToolVerification.None, false, id != Send, new[] { "agent", "plan" },
                    evidenceEligibility: id == Send ? EvidenceEligibility.Eligible : EvidenceEligibility.None) };
        }
    }
}
