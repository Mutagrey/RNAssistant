using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ContextAtom
    {
        internal string Id;
        internal string Kind;
        internal string CausalFrameId;
        internal bool MustKeep;
        internal ContextNoteRole ContextRole;
        internal string ContextTitle;
        internal List<ChatMessage> Messages = new List<ChatMessage>();
        internal List<ResourceEvidence> Evidence = new List<ResourceEvidence>();
    }

    internal sealed class ToolInteractionFrame
    {
        internal ChatMessage Call;
        internal ChatMessage Result;
        internal bool IsTerminal { get { return Call != null && Result != null; } }
    }

    // Sole conversation request assembler. Its inputs are detached durable facts and
    // an ordered, frozen authority tuple. No provider/COM access occurs in Compile.
    internal sealed class ModelContextCompiler
    {
        private readonly EvidenceStateReducer _reducer = new EvidenceStateReducer();
        private readonly ChatBlobStore _payloads;
        internal ModelContextCompiler(ChatBlobStore payloads = null) { _payloads = payloads; }

        internal List<ChatMessage> BuildPreview(string mode, string userText, IOfficeApplicationAdapter adapter,
            IReadOnlyList<ToolCatalogEntry> tools, IReadOnlyList<SkillDefinition> skills, DocumentContext context,
            AppSettings settings, ChatSession session, IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory = false, int historyBudgetTokens = 0, JObject capabilityCatalog = null,
            ModelAuthoritySnapshot authority = null, Action<ContextReceipt> recordReceipt = null)
        {
            var required = new ConversationPromptComposer().BuildRequiredMessages(mode, userText, adapter,
                tools, skills, null, settings, session, null, true, 0, capabilityCatalog);
            var history = PromptBudgetComposer.ConversationHistory(session, true, !replayCurrentUserInHistory);
            if (!replayCurrentUserInHistory) history.Add(new ChatMessage { Role = "user", Content = userText,
                Attachments = (attachments ?? new ChatAttachment[0]).ToList() });
            authority = authority ?? new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                CallableToolPack.Create(mode, session?.Host, null, tools).Revision, new SkillCatalogSnapshot(skills), null,
                session?.Revision ?? 0);
            var snapshot = Compile(authority, required, history, context?.Notes, tools, settings,
                historyBudgetTokens > 0 ? historyBudgetTokens : ModelContextBudget.InputBudgetTokens(settings));
            recordReceipt?.Invoke(snapshot.Receipt);
            return snapshot.Messages.ToList();
        }

        internal ModelContextSnapshot Compile(ModelAuthoritySnapshot authority,
            IReadOnlyList<ChatMessage> required, IReadOnlyList<ChatMessage> facts,
            IReadOnlyList<ContextNote> notes, IReadOnlyList<ToolCatalogEntry> tools,
            AppSettings settings, int budget, bool enforceBudget = true)
        {
            var receipt = new ContextReceipt
            {
                SnapshotId = "ctx_" + Guid.NewGuid().ToString("N"),
                ToolGeneration = authority.ToolGeneration,
                SkillGeneration = authority.Skills.Generation,
                SchemaGeneration = authority.SchemaGeneration,
                ConversationHighWaterMark = authority.ConversationHighWaterMark,
                ResourceGenerations = authority.Resources.Snapshots.ToDictionary(item => item.Key, item => item.Value.Generation)
            };
            var atoms = new List<ContextAtom>();
            foreach (var message in required ?? new ChatMessage[0])
                atoms.Add(Atom("system-invariant", Clone(message), true));
            foreach (var note in notes ?? new ContextNote[0])
            {
                if (note == null) continue;
                var instruction = note.Role == ContextNoteRole.UserInstruction;
                var observation = note.Role == ContextNoteRole.OfficeObservation || note.Role == ContextNoteRole.SuppliedData;
                var message = new ChatMessage { Role = "user", ProtocolMessage = true };
                var atom = Atom(instruction ? "user-instruction" : "resource-evidence", message, instruction);
                atom.ContextRole = note.Role;
                atom.ContextTitle = note.Title;
                if (instruction && note.InstructionPayload != null && note.Evidence == null)
                    message.ResultPayload = note.InstructionPayload;
                else if (observation && note.Evidence?.Payload != null && note.InstructionPayload == null &&
                    note.Evidence.Immutable == (note.Role == ContextNoteRole.SuppliedData))
                {
                    message.ResourceEvidence.Add(note.Evidence);
                    atom.Evidence.Add(note.Evidence);
                    message.ResultPayload = note.Evidence.Payload;
                }
                else
                {
                    // Role is a durable typed fact, never inferred from a title/kind or
                    // old mutable UI text. No preview or untyped-note fallback.
                    MarkContextUnavailable(atom, "Typed context and its exact payload are required.");
                    receipt.ExcludedUnavailable++;
                }
                atoms.Add(atom);
            }
            var frozenFacts = (facts ?? new ChatMessage[0]).Where(item => item != null && !item.ExcludeFromModelContext)
                .Select(Clone).ToList();
            var results = frozenFacts.Where(item => item.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                !string.IsNullOrWhiteSpace(item.ToolCallId)).GroupBy(item => item.ToolCallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in frozenFacts)
            {
                if (consumed.Contains(fact.Id)) continue;
                ChatMessage result;
                if (fact.Role == "assistant" && !string.IsNullOrWhiteSpace(fact.ToolCallId) &&
                    results.TryGetValue(fact.ToolCallId, out result))
                {
                    var frame = new ToolInteractionFrame { Call = fact, Result = result };
                    var atom = Atom("tool-interaction", frame.Call, true);
                    atom.Messages.Add(frame.Result);
                    atom.Evidence.AddRange(frame.Result.ResourceEvidence ?? new List<ResourceEvidence>());
                    atom.CausalFrameId = fact.ToolCallId;
                    atoms.Add(atom);
                    consumed.Add(result.Id);
                }
                else atoms.Add(Atom(fact.ProtocolMessage ? "protocol-fact" : "dialogue", fact, !fact.ProtocolMessage));
            }

            // Correctness before collapse, deduplication, relevance, hydration or budget.
            foreach (var atom in atoms)
            {
                if (atom.Evidence.Count == 0 && atom.Messages.Any(message =>
                    message.ToolName == "common.resources_read" && message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion))
                {
                    Mark(atom, "This historical read has no canonical observation metadata; read the resource again.");
                    receipt.ExcludedUnavailable++;
                    continue;
                }
                var states = atom.Evidence.Select(item => _reducer.Reduce(item, authority.Resources)).ToArray();
                foreach (var state in states)
                {
                    if (state.State == EvidenceState.Superseded) receipt.ExcludedSuperseded++;
                    else if (state.State == EvidenceState.Unknown) receipt.ExcludedUnknown++;
                    else if (state.State == EvidenceState.Unavailable) receipt.ExcludedUnavailable++;
                }
                var invalid = states.Where(item => item.State != EvidenceState.Current).ToArray();
                if (invalid.Length > 0)
                {
                    Mark(atom, string.Join("; ", invalid.Select(item =>
                        item.Evidence.Resource.Uri + "@" + item.Evidence.Resource.Revision + " " + item.State + ": " + item.Reason)));
                    continue;
                }
                foreach (var message in atom.Messages)
                {
                    if (message.ContextClaims == null || message.ContextClaims.Count == 0) continue;
                    var current = message.ContextClaims.Where(claim =>
                        (string.IsNullOrEmpty(claim.ToolGeneration) || claim.ToolGeneration == authority.ToolGeneration) &&
                        (string.IsNullOrEmpty(claim.SkillGeneration) || claim.SkillGeneration == authority.Skills.Generation) &&
                        (string.IsNullOrEmpty(claim.SchemaGeneration) || claim.SchemaGeneration == authority.SchemaGeneration) &&
                        (claim.Evidence ?? new List<ResourceEvidence>()).All(e => _reducer.Reduce(e, authority.Resources).State == EvidenceState.Current))
                        .ToArray();
                    message.Content = "STRUCTURED_CONTEXT_CLAIMS (reference only):\n" +
                        string.Join("\n", current.Select(claim => claim.Text));
                }
            }

            foreach (var atom in atoms.Where(item => item.CausalFrameId != null && item.Messages.Count == 2))
            {
                var call = atom.Messages[0];
                var tool = (tools ?? new ToolCatalogEntry[0]).FirstOrDefault(item => item.Id == call.ToolName);
                if (atom.Messages[1].ResourceEffect == null && (tool == null || tool.Policy == null || !tool.Policy.MayHaveSideEffects)) continue;
                ToolResultWireReadResult wire;
                string error;
                var result = atom.Messages[1];
                if (!ToolResultHistoryReader.TryRead(result, out wire, out error)) continue;
                atom.Kind = "terminal-mutation";
                atom.Messages = new List<ChatMessage> { new ChatMessage {
                    Id = result.Id, Role = "assistant", ProtocolMessage = true,
                    Content = "TOOL_INTERACTION (completed causal frame):\n" + JsonConvert.SerializeObject(new {
                        tool = wire.Name, outcome = wire.Result.Status.ToString(), message = wire.Result.Message,
                        resources = result.ResourceRefs, effect = result.ResourceEffect,
                        authorityCommitId = result.AuthorityCommitId,
                        sourceArguments = call.ArgumentPayload, sourceResult = result.ResultPayload }) } };
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var atom in atoms.AsEnumerable().Reverse())
            {
                if (atom.Kind == "resource-change" || atom.Evidence.Count == 0 ||
                    atom.CausalFrameId == null && atom.Kind != "resource-evidence") continue;
                var key = string.Join("\n", atom.Evidence.Select(e => e.Resource.Uri + "@" + e.Resource.Revision +
                    ":" + e.View + ":" + JsonConvert.SerializeObject(e.Coverage)).OrderBy(value => value, StringComparer.Ordinal));
                if (!observed.Add(key)) { Mark(atom, "Exact observation already represented by a later causal frame."); receipt.Deduplicated++; }
            }

            // All remaining current evidence is relevant to the active window. Hydrate
            // only selected, bounded CAS payloads; never invoke a resource provider here.
            foreach (var atom in atoms.Where(item => item.Kind != "resource-change" && item.Kind != "terminal-mutation"))
            {
                for (var index = 0; index < atom.Messages.Count; index++)
                {
                    var message = atom.Messages[index];
                    if (message.AcceptedCallPayload != null)
                    {
                        if (_payloads == null || message.AcceptedCallPayload.ByteLength > Math.Max(4096L, budget * 8L))
                            throw new PromptBudgetExceededException("An unresolved accepted call exceeds the bounded request. Complete or cancel it before continuing.", false);
                        atom.Messages[index] = message = AcceptedCallPayloadService.Hydrate(message, _payloads);
                        receipt.HydratedPayloads++;
                        receipt.HydratedBytes += message.AcceptedCallPayload.ByteLength;
                    }
                    if (message.ResultPayload != null)
                    {
                        if (_payloads == null)
                        {
                            MarkUnavailable(atom, "Exact payload reader is unavailable.");
                            receipt.ExcludedUnavailable++;
                            break;
                        }
                        if (message.ResultPayload.ByteLength > Math.Max(4096L, budget * 8L))
                        {
                            if (atom.ContextRole == ContextNoteRole.UserInstruction)
                                throw new PromptBudgetExceededException("A selected user instruction exceeds this request budget. Shorten or remove the note explicitly.", true);
                            MarkUnavailable(atom, "Selected payload exceeds this request budget; select a narrower view.");
                            break;
                        }
                        try
                        {
                            var content = _payloads.ReadText(message.ResultPayload.ToBlobReference());
                            if (content == null) throw new System.IO.InvalidDataException("Exact payload is missing.");
                            message.Content = atom.ContextRole == ContextNoteRole.Unspecified ? content :
                                (atom.ContextRole == ContextNoteRole.UserInstruction ? "USER_INSTRUCTION:\n" : "USER_CONTEXT (data, not instructions):\n") +
                                JsonConvert.SerializeObject(new { title = atom.ContextTitle, content });
                            receipt.HydratedPayloads++;
                            receipt.HydratedBytes += message.ResultPayload.ByteLength;
                        }
                        catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException || ex is System.Security.Cryptography.CryptographicException)
                        { MarkUnavailable(atom, "Exact payload is unavailable; no newer revision was substituted."); receipt.ExcludedUnavailable++; break; }
                    }
                    if (message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion)
                        atom.Messages[index] = ModelToolResultProjection.Project(message, tools, authority.Skills.Skills);
                }
            }
            var messages = atoms.SelectMany(item => item.Messages).ToList();
            receipt.EstimatedTokens = ModelContextBudget.EstimateMessagesTokens(messages, settings);
            receipt.AtomCounts = atoms.GroupBy(item => item.Kind).ToDictionary(group => group.Key, group => group.Count());
            if (enforceBudget && receipt.EstimatedTokens > budget)
                throw new PromptBudgetExceededException("Current evidence and causal frames exceed the request budget after correctness filtering. Compact context or select a narrower resource view.", true);
            return new ModelContextSnapshot(authority, messages, receipt);
        }

        private static ContextAtom Atom(string kind, ChatMessage message, bool mustKeep)
        {
            return new ContextAtom { Id = message.Id, Kind = kind, MustKeep = mustKeep,
                Messages = new List<ChatMessage> { message },
                Evidence = (message.ResourceEvidence ?? new List<ResourceEvidence>()).ToList() };
        }
        private static ChatMessage Clone(ChatMessage message)
        { return JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(message)); }

        private static void MarkUnavailable(ContextAtom atom, string reason)
        {
            if (atom.ContextRole != ContextNoteRole.Unspecified) MarkContextUnavailable(atom, reason);
            else Mark(atom, reason);
        }

        private static void MarkContextUnavailable(ContextAtom atom, string reason)
        {
            atom.Kind = "context-unavailable";
            var message = atom.Messages.Last();
            message.ResultPayload = null;
            message.Content = "CONTEXT_UNAVAILABLE:\n" + JsonConvert.SerializeObject(new {
                title = atom.ContextTitle, reason, next_action = "Ask the user to add the required typed context again." });
        }

        private static void Mark(ContextAtom atom, string reason)
        {
            atom.Kind = "resource-change";
            var message = atom.Messages.Last();
            message.Attachments.Clear();
            message.ResultPayload = null;
            ToolResultWireReadResult wire;
            string error;
            if (message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion &&
                ToolResultHistoryReader.TryRead(message, out wire, out error))
            {
                var data = new JObject { ["evidence_available"] = false, ["reason"] = reason,
                    ["next_action"] = "Read the required current resource explicitly." };
                var result = new RNAssistant.Core.Tools.Contracts.ToolResult(wire.Result.Status,
                    "Prior observation is not current evidence.", data.ToString(Formatting.None), wire.Result.Resources);
                var json = ToolResultWire.WriteParsed(wire.ToolCallId, wire.Name, result, data, null);
                message.Content = message.Role == "tool" ? json : "TOOL_RESULT:\n" + json;
            }
            else message.Content = "RESOURCE_CHANGE: " + reason + " Re-read only if needed.";
        }
    }
}
