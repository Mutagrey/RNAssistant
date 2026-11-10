using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Application.Conversations
{
    // Bound existing persistence, shared by Office and Workspace. No new catalog/store.
    internal sealed class ConversationCatalogService
    {
        private readonly ChatStore _store;
        private readonly ChatBlobStore _payloads;
        internal ConversationCatalogService(ChatStore store, ChatBlobStore payloads)
        { _store = store; _payloads = payloads; }

        internal ChatOverview Read(ChatSession scope, string id, bool metadataOnly)
        { return _store.ReadChatOverview(scope, id, metadataOnly); }

        internal void Rename(ChatSession session, string title) { _store.SetAgentTitle(session, title); }

        internal ResourceReadObservation Capture(ChatSession source, ChatOverview overview, string text, string representation)
        {
            var payload = PayloadRef.FromBlob(_payloads.StoreText(text, "application/json; charset=utf-8"));
            var reference = new ResourceRef(ResourceUri.Create("chat", source.Id, "overview", overview.Source.Id,
                overview.Source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)), payload.Sha256);
            var coverage = ResourceCoverage.Whole();
            var descriptor = new ResourceDescriptor { Reference = reference, Provider = "chat", Kind = "chat-overview",
                Title = overview.Title, Mutable = false, Tracking = "snapshot", MimeType = "application/json",
                ContentSha256 = payload.Sha256, ByteLength = payload.ByteLength, Coverage = coverage };
            descriptor.Representations.Add(ResourceRepresentations.Text);
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            var evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"),
                new ResourceAuthorityScopeId("chat", source.Id), reference, representation,
                coverage, true, 0, payload,
                dependencies: overview.SharedContextReference == null ? null : new[] {
                    new ResourceDependency(overview.SharedContextReference, "text", kind: "overview-source") },
                immutable: true, contentSha256: payload.Sha256);
            return new ResourceReadObservation(new ResourceReadResult { Resource = descriptor,
                Representation = representation, Text = text, ContentSha256 = payload.Sha256,
                Coverage = coverage, Complete = true, SourceComplete = false, RawContentIncluded = true,
                ReturnedCharacters = text.Length, TotalCharacters = text.Length, CompleteViewPayload = payload },
                new[] { evidence });
        }
    }

    // Run-local observations authorize only addressed title changes. An overview
    // is historical data and never gives write authority over another chat's history.
    internal sealed class ConversationCatalogAccess
    {
        internal const string TargetPrefix = "Chat overview: ";
        private readonly AgentCommunicationService _agents;
        private readonly ChatSession _source;
        private readonly int _limit;
        private readonly Dictionary<string, ChatSession> _observed = new Dictionary<string, ChatSession>(StringComparer.Ordinal);

        internal ConversationCatalogAccess(AgentCommunicationService agents, ChatSession source, int limit)
        { _agents = agents; _source = source; _limit = limit; }

        internal static bool IsTarget(string target) { return target?.StartsWith(TargetPrefix, StringComparison.Ordinal) == true; }
        private void RequireAvailable()
        {
            if (_agents == null) throw Error("The conversation catalog is unavailable.", "chat_catalog_unavailable");
        }

        internal ResourceFindPage Find(string query)
        {
            RequireAvailable();
            if (query?.Length > 500) throw Error("Chat query is limited to 500 characters.", "chat_query_invalid");
            var peers = _agents.List(_source, _limit).Where(peer => string.IsNullOrEmpty(query) ||
                peer.AgentId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (peer.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(peer => peer.AgentId, StringComparer.Ordinal).ToArray();
            return new ResourceFindPage { Scope = "chats", Query = query, Total = peers.Length, Complete = peers.Length <= 50,
                AvailabilityHint = peers.Length > 50 ? "Refine query or page agents.list; read Chat overview: <agent_id> for a returned address." : null,
                Items = peers.Take(50).Select(peer => new ResourceFindCandidate { Target = TargetPrefix + peer.AgentId,
                    Title = peer.Title, Type = "chat", Scope = "chats", Mutable = false,
                    Representations = new List<string> { ResourceRepresentations.Text, ResourceRepresentations.Metadata },
                    Usage = "Bounded overview, no wake or full-history access. Read before addressed chat.rename; a busy or changed target is rejected." }).ToList() };
        }

        internal ToolHandlerResult Read(ToolHandlerContext context, int maximumCharacters, CancellationToken token)
        {
            RequireAvailable();
            token.ThrowIfCancellationRequested();
            object value;
            var target = (context.Arguments.TryGetValue("target", out value) ? value as string : "")?.Trim();
            var representation = context.Arguments.TryGetValue("representation", out value) ? value as string : "text";
            if (!IsTarget(target) || (representation != "text" && representation != "metadata" && representation != "auto") ||
                context.Arguments.Keys.Any(key => key != "type" && key != "target" && key != "representation"))
                throw Error("Chat overview accepts one semantic target and text or metadata, without history selectors.", "chat_overview_invalid");
            var agentId = target.Substring(TargetPrefix.Length);
            ConversationCatalogService catalog;
            var endpoint = _agents.CatalogTarget(_source, agentId, _limit, out catalog);
            var overview = catalog.Read(_source, endpoint.Address.ChatId, representation == "metadata");
            var budget = Math.Min(8000, Math.Max(0, maximumCharacters - 1024));
            var text = JsonConvert.SerializeObject(overview);
            while (text.Length > budget && (overview.Excerpts.Count > 0 || overview.SummaryClaims.Count > 0))
            {
                overview.BudgetTruncated = true;
                if (overview.Excerpts.Count > 0) overview.Excerpts.RemoveAt(overview.Excerpts.Count - 1);
                else overview.SummaryClaims.RemoveAt(overview.SummaryClaims.Count - 1);
                text = JsonConvert.SerializeObject(overview);
            }
            if (text.Length > budget) throw Error("The chat overview metadata exceeds the model read budget.", "chat_overview_budget");
            token.ThrowIfCancellationRequested();
            var read = catalog.Capture(_source, overview, text, representation == "metadata" ? "metadata" : "text");
            if (_observed.Count >= 128) _observed.Remove(_observed.Keys.First());
            _observed[agentId] = overview.Source;
            var projection = ResourceReadProjection.From(read.Result, target, "chat", "chats");
            return new ToolHandlerResult(RuntimeResult.Ok("Read a bounded historical chat overview; the full history is not included.",
                JsonConvert.SerializeObject(projection), new[] { read.Result.Resource.Reference }),
                ToolEffectEvidence.None, resourceEvidence: read.Evidence);
        }

        internal ChatRenameResult Rename(string agentId, string title, Action dispatch)
        {
            RequireAvailable();
            ConversationCatalogService catalog;
            var endpoint = _agents.CatalogTarget(_source, agentId, _limit, out catalog);
            ChatSession observed;
            if (!_observed.TryGetValue(agentId, out observed))
                throw Error("Read this chat overview with core.read before renaming it.", "chat_rename_unobserved");
            if (!observed.UsesCurrentProtocol) throw Error("This chat stream is incompatible and cannot be renamed by an agent.", "chat_incompatible");
            if (endpoint.Reserve == null) throw Error("The chat has no execution reservation.", "chat_rename_unavailable");
            IDisposable lease;
            try { lease = endpoint.Reserve(); }
            catch (IOException) { throw Error("The target chat is busy; wait until it is idle and read its overview again.", "chat_busy"); }
            using (lease)
            {
                if (endpoint.IsClosing?.Invoke() == true || endpoint.Inbox?.HasActiveWork(endpoint.Address.ChatId) == true ||
                    endpoint.Runs?.IsRunning(endpoint.Address.ChatId) == true)
                    throw Error("The target chat is busy or closing; no change was made.", "chat_busy");
                // Only mutation loads an idle execution object, so existing host caches
                // and callbacks receive a complete session, never an overview projection.
                var current = endpoint.Load();
                if (current == null || current.Id != observed.Id || !ChatStore.SameOverviewScope(_source, current))
                    throw Error("The target chat is unavailable in this scope.", "chat_unavailable");
                if (current.Revision != observed.Revision || !current.UsesCurrentProtocol)
                    throw Error("The chat changed after the overview read. Read it again before renaming.", "chat_rename_stale");
                var changed = current.Title != title || !current.TitleAssignedByAgent;
                string warning = null;
                if (changed)
                {
                    dispatch();
                    catalog.Rename(current, title);
                    _observed.Remove(agentId);
                    // Notification failure must not turn a durable rename into false failure.
                    try { endpoint.Changed?.Invoke(current); }
                    catch (Exception error) when (error is InvalidOperationException || error is IOException)
                    { warning = "Title saved; the UI notification was unavailable. Refresh the chat list."; }
                }
                return new ChatRenameResult { Title = current.Title, AgentId = agentId, Changed = changed, Warning = warning };
            }
        }

        private static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
    }

    internal sealed partial class AgentCommunicationService
    {
        internal AgentConversationEndpoint CatalogTarget(ChatSession source, string agentId, int limit,
            out ConversationCatalogService catalog)
        {
            lock (_sync)
            {
                RequireEnabled(limit);
                catalog = Scope(source).Catalog;
                var endpoint = FindAgent(source, agentId);
                if (catalog == null || endpoint == null)
                    throw new ResourceRequestException("The requested chat overview is unavailable in this scope.", "chat_unavailable", false);
                return endpoint;
            }
        }
    }
}
