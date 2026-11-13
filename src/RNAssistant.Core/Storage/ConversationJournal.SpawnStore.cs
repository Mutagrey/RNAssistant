using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        ConversationInputRecord IEventStore.AcceptAgentSpawn(ChatSession source, ChatSession child,
            AgentSpawnCommand command, Action dispatch)
        {
            if (command == null || dispatch == null) throw new ArgumentNullException(nameof(command));
            command = JsonConvert.DeserializeObject<AgentSpawnCommand>(JsonConvert.SerializeObject(command));
            var context = command.Execution;
            if (source?.LastRun?.KernelState == null || child == null || child.Revision != 0 ||
                context?.Call.Name != AgentCommunicationToolCatalog.Spawn ||
                source.LastRun.RunId != context.RunId || source.LastRun.TurnId != context.TurnId ||
                !SameRunValue(source.LastRun.KernelState.InFlightTool, context) || string.IsNullOrWhiteSpace(command.RuntimeKey) ||
                child.Host != source.Host || child.DocumentKey != source.DocumentKey || child.WorkspaceId != source.WorkspaceId ||
                child.DocumentAuthorityId != source.DocumentAuthorityId || child.Messages.Count != 0 || child.LastRun != null)
                throw new InvalidOperationException("Spawn requires its exact entered call and a fresh chat in the same scope.");
            var policy = ConversationRunPolicy.ForAcceptedRun(source);
            child.Id = "child-" + TextPatternEngine.Sha256(_scope + "\n" + command.OperationId);
            child.Title = command.Title?.Trim(); child.Mode = policy.Mode; child.MutationPolicy = policy.MutationPolicy;
            child.Model = string.IsNullOrWhiteSpace(source.LastRun.ModelConfiguration?.Model) ? source.Model : source.LastRun.ModelConfiguration.Model;
            child.ReasoningEnabled = source.ReasoningEnabled; child.TitleAssignedByAgent = true;
            // A child starts without parent history, resource aliases or approvals.
            // Stable empty context also keeps an exact retry's CAS intent identical.
            child.Context = new DocumentContext { Host = child.Host, DocumentKey = child.DocumentKey,
                Title = child.DocumentTitle, UpdatedUtc = source.CreatedUtc };
            RequireTextSession(child, new ConversationCommit());
            using (RuntimeStorageActivity.Acquire(_paths))
            {
                var body = StoreChatJson(child.Context);
                var creation = Access((db, cursor) => new AgentThreadCreation { OperationId = command.OperationId,
                    ChainId = command.ChainId, Creator = CardAddress(Card(db, source.Id)?.Item1),
                    Root = CardAddress(Card(db, command.RootChatId)?.Item1) }, default(System.Threading.CancellationToken));
                var card = new ConversationCard(child.Id, child.Title, ConversationRunPolicy.ForSession(child), child.CreatedUtc,
                    source.LastRun.ModelConfiguration, new ConversationMetadata(child.Id, 0, child.Host, child.DocumentKey,
                        child.WorkspaceId, child.DocumentAuthorityId), child.DocumentTitle, child.DocumentPath, child.Model,
                    child.ReasoningEnabled, true, creation);
                AgentSpawnJournalReceipt receipt;
                try
                {
                    receipt = SpawnAgent(command.OperationId, source.Id, context.RunId, context.Call.Id, command.RootChatId,
                        command.ChainId, command.ParticipantLimit, command.Title, command.InitialText, command.ExpectReply, command.Assignment,
                        childCard: card, childContext: body, runtimeKey: command.InitialText == null ? null : command.RuntimeKey,
                        validateDispatch: db => {
                            RequireChatRevision(ConversationMetadata.From(source), TextChat(db, source.Id));
                            RequirePeerEntry(db, source.Id, context);
                        }, dispatch: dispatch);
                }
                catch (IOException)
                {
                    receipt = SpawnReceipt(command.OperationId);
                    if (receipt?.Fact.Sender != source.Id || receipt.Fact.RunId != context.RunId || receipt.Fact.CallId != context.Call.Id) throw;
                }
                Fault?.Invoke("before_spawn_readback");
                receipt = SpawnReceipt(receipt.Operation) ?? throw new InvalidDataException("Spawn acceptance is unavailable.");
                var fact = receipt.Fact;
                var text = fact.Input == null ? null : ReadAgentText(new AgentMessageReceipt(receipt.Operation,
                    receipt.Fingerprint, fact.Input, receipt.Position, receipt.ProjectionAvailable));
                if (fact.Chat == null || fact.Sender != source.Id || fact.RunId != context.RunId || fact.CallId != context.Call.Id ||
                    fact.Root != command.RootChatId || fact.ChainId != command.ChainId || fact.ParticipantLimit != command.ParticipantLimit ||
                    fact.Child.ChatId != child.Id || fact.Child.Title != child.Title || !SameRunValue(fact.Chat.Context, body) ||
                    fact.Child.SelectedModel != child.Model || !SameRunValue(fact.Child.Model, source.LastRun.ModelConfiguration) ||
                    fact.Child.ReasoningEnabled != child.ReasoningEnabled || !SameRunValue(fact.Child.Address, card.Address) ||
                    text != command.InitialText || fact.Input != null && (fact.Input.ExpectReply != command.ExpectReply ||
                    fact.Input.Assignment != command.Assignment || fact.Input.RecipientRuntimeKey != command.RuntimeKey))
                    throw new InvalidDataException("Spawn acknowledgement does not match the exact request.");
                card.Policy.RequireSame(fact.Child.Policy);
                if (fact.Input != null) policy.RequireSame(fact.Input.Policy);
                // Verify CAS before publishing either live cursor, including idle creation.
                if (ReadChatBody(fact.Chat.Context) != JsonConvert.SerializeObject(child.Context))
                    throw new InvalidDataException("Spawn context read-back differs.");
                ApplyPeerCursor(source, receipt.Position, fact.Child.CreatedUtc, fact.SenderRevision);
                child.CreatedUtc = fact.Child.CreatedUtc; child.AgentCreation = fact.Child.AgentCreation;
                ApplyPeerCursor(child, receipt.Position, fact.Child.CreatedUtc, 0);
                if (fact.Input == null) return null;
                var input = JsonConvert.DeserializeObject<ConversationInput>(JsonConvert.SerializeObject(fact.Input.Input.Header));
                input.Text = text;
                return new ConversationInputRecord(ProjectChatEvent(new ConversationChatReceipt(fact.Chat, receipt.Operation, receipt.Position)),
                    new ConversationInputEvent { Input = input, Paused = false });
            }
        }

        private void ValidateSpawnChat(SqliteConnection db, JournalRecord record, AgentSpawnAccepted fact, ConversationCard sender)
        {
            var chat = fact.Chat; var child = fact.Child; var address = child.Address;
            var run = CurrentRun(db, fact.Sender)?.Fact;
            ValidateChatAddress(child);
            if (chat == null || run == null || run.RunId != fact.RunId || !SameRunValue(chat.Card, child) ||
                chat.ExpectedRevision != 0 || chat.MessageCount != 0 || chat.Messages.Count != 0 || chat.Run != null ||
                chat.RunState != null || chat.Checkpoint != null || chat.ContextReceipt != null || chat.ModelEvent != null ||
                chat.Tools != null || (chat.References?.Count ?? 0) != 0 || chat.CreatedUtc != child.CreatedUtc ||
                !SameRunValue(chat.Input, fact.Input?.Input) || !SameRunValue(chat.Control, fact.Input?.Control) ||
                fact.SenderRevision != TextChat(db, fact.Sender)?.Position.Sequence ||
                address.Host != sender.Address.Host || address.DocumentKey != sender.Address.DocumentKey ||
                address.WorkspaceId != sender.Address.WorkspaceId || address.DocumentAuthorityId != sender.Address.DocumentAuthorityId ||
                child.DocumentPath != sender.DocumentPath || child.DocumentTitle != sender.DocumentTitle ||
                !child.TitleAssignedByAgent || child.ReasoningEnabled != sender.ReasoningEnabled ||
                child.SelectedModel != (string.IsNullOrWhiteSpace(run.Model?.Model) ? sender.SelectedModel : run.Model.Model) ||
                !SameRunValue(child.Model, run.Model) || child.AgentCreation == null ||
                child.AgentCreation.OperationId != record.Operation || child.AgentCreation.Fingerprint != record.Fingerprint ||
                child.AgentCreation.ChainId != fact.ChainId || !SameRunValue(child.AgentCreation.Creator, CardAddress(sender)) ||
                !SameRunValue(child.AgentCreation.Root, CardAddress(Card(db, fact.Root)?.Item1)))
                throw new InvalidDataException("Spawn metadata must inherit its exact source without history or a child run.");
            ConversationRunPolicy.For(run.Policy.Mode, run.Policy.MutationPolicy).RequireSame(child.Policy);
        }

        private static SessionEvent ProjectSpawnEvent(AgentSpawnJournalReceipt receipt)
        {
            if (receipt?.Fact.Chat == null) throw new InvalidDataException("Spawn is outside the native chat path.");
            return new SessionEvent { SessionId = receipt.Fact.Sender, Sequence = receipt.Position.Sequence,
                EventId = receipt.Operation, CreatedUtc = receipt.Fact.Child.CreatedUtc, Hash = receipt.Position.Hash,
                Type = "agent.spawned", RunId = receipt.Fact.RunId, TurnId = receipt.Fact.RunId };
        }
    }
}
