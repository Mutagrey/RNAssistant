using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void NativeCatalogSkillMutation()
        {
            WithNativeToolFixture(fixture => {
                const string id = "skills.native.catalog";
                const string body = "# Native catalog skill\n\nNATIVE_CATALOG_PUBLICATION_SENTINEL";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.skills.load", new JObject { ["id"] = "skills.skill.authoring" }),
                    NativeCall("skills.upsert", new JObject {
                        ["id"] = id, ["host"] = "Excel", ["name"] = "Native catalog skill",
                        ["description"] = "Verify an authored skill through the native publication boundary.",
                        ["version"] = "1.0.0", ["bodyMarkdown"] = body, ["enabled"] = true })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The skill has a retained publication receipt.",
                    "Skill saved.", "Save the requested skill.", "skills.upsert"));

                var saved = new SkillStore(fixture.Paths).Load().Single(skill => skill.Id == id);
                AssertEqual(body, saved.BodyMarkdown, "the real catalog owner stores the requested skill body");
                AssertTrue(saved.Enabled, "the real catalog owner stores the requested enabled state");
                AssertEqual(ToolExecutionOutcome.Ok, NativeTerminal(fixture, "skills.upsert").ExecutionProgress.Outcome,
                    "a published skill has a successful native terminal");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "catalog publication counts as one verified write");
                AssertEqual(0, result.RunViewState.UnknownEffects, "successful catalog publication is not lost at chat persistence");
                AssertNativeToolReceipt(fixture, "skills.upsert");
                var terminal = NativeTerminal(fixture, "skills.upsert");
                var receipt = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertEqual(new ResourceAuthorityScopeId("catalog", "local"), receipt.Fact.Scope,
                    "catalog mutation retains its own authority scope");
                AssertEqual(fixture.Session.Id, receipt.Fact.Call.Origin.ChatId, "catalog receipt identifies the exact source chat");
                AssertEqual(terminal.ToolCallId, receipt.Fact.Call.Origin.CallId, "catalog receipt identifies the accepted tool call");
                AssertEqual(body, new SkillStore(fixture.Paths).Load().Single(skill => skill.Id == id).BodyMarkdown,
                    "rebuilding conversation and resource projections preserves the authored catalog body");
            });
        }

        private static void NativeCatalogSelfRename()
        {
            WithNativeToolFixture(fixture => {
                IConversationStore store = fixture.Router;
                var other = store.CreateTransient(fixture.Session.Host, fixture.Session.DocumentKey,
                    fixture.Session.DocumentTitle, "Unrelated chat", fixture.Session.DocumentAuthorityId);
                store.Commit(other, new ConversationCommit());
                var otherRevision = other.Revision;
                const string title = "Native task title";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall(ChatRenameToolHandler.ToolId, new JObject { ["title"] = "  " + title + "  " }),
                    NativeCall(ChatRenameToolHandler.ToolId, new JObject { ["title"] = title })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The current title was persisted and then checked unchanged.",
                    "Current chat named.", "Name the current chat.", ChatRenameToolHandler.ToolId, ChatRenameToolHandler.ToolId));

                var retained = store.Load(fixture.Session.Id);
                AssertEqual(title, retained.Title, "the atomic native terminal persists the trimmed self title");
                AssertTrue(retained.TitleAssignedByAgent, "agent naming ownership is retained with the title");
                var terminals = retained.Messages.Where(message => message.ToolName == ChatRenameToolHandler.ToolId &&
                    message.Activity == null && message.AcceptedCallOrigin == null && message.ExecutionProgress != null).ToArray();
                AssertEqual(2, terminals.Length, "changed and no-op self renames both retain their terminals");
                AssertTrue(terminals.All(message => message.ExecutionProgress.Outcome == ToolExecutionOutcome.Ok),
                    "the metadata proof accepts both a changed title and an exact no-op");
                AssertTrue(terminals.All(message => message.AuthorityCommitId == null && message.ResourceEffect == null),
                    "metadata naming does not fabricate document resource authority");
                AssertEqual(0, result.RunViewState.UnknownEffects, "atomic title metadata does not become an uncertain external effect");
                AssertEqual(otherRevision, store.Load(other.Id).Revision, "self rename does not commit another chat");
                fixture.Journal.Rebuild();
                retained = store.Load(fixture.Session.Id);
                AssertEqual(title, retained.Title, "native replay verifies the accepted self title");
                AssertTrue(retained.TitleAssignedByAgent, "native replay keeps agent naming ownership");
                AssertEqual("Unrelated chat", store.Load(other.Id).Title, "native replay leaves the unrelated title unchanged");
            });
        }

        private static void NativeCatalogAddressedRename()
        {
            WithNativeToolFixture(fixture => {
                IConversationStore store = fixture.Router;
                var target = store.CreateTransient(fixture.Session.Host, fixture.Session.DocumentKey,
                    fixture.Session.DocumentTitle, "Observed target", fixture.Session.DocumentAuthorityId);
                store.Commit(target, new ConversationCommit());
                var targetRevision = target.Revision;
                var sourceTitle = fixture.Session.Title;
                var inbox = new ConversationInboxService(fixture.Router);
                var agents = new AgentCommunicationService();
                agents.Register(OverviewScope(store, inbox, fixture.Session, fixture.Paths));
                fixture.Executor.AgentCommunication = agents; fixture.Executor.AgentInbox = inbox;
                var published = fixture.Executor.GetControllerTools().Single(entry => entry.Id == ChatRenameToolHandler.ToolId);
                AssertTrue(JObject.Parse(published.ArgumentSchemaJson)["properties"]["agent_id"] != null,
                    "the native catalog exposes addressed rename");
                var address = AgentChatAddress.IdFor(target.Id);
                const string addressedTitle = "Chess research";
                RunNativeToolScript(fixture, new[] {
                    NativeCall("core.find", new JObject { ["scope"] = "chats", ["query"] = address }),
                    NativeCall("core.read", new JObject { ["target"] = ConversationCatalogAccess.TargetPrefix + address }),
                    NativeCall(ChatRenameToolHandler.ToolId, new JObject { ["title"] = addressedTitle, ["agent_id"] = address })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The exact other chat was renamed.",
                    "Renamed another chat.", "Addressed chat renamed.", ChatRenameToolHandler.ToolId));

                var renamed = store.Load(target.Id);
                AssertEqual(addressedTitle, renamed.Title, "the native target metadata commit stores the addressed title");
                AssertTrue(renamed.TitleAssignedByAgent, "addressed rename records agent naming ownership");
                AssertTrue(renamed.Revision > targetRevision, "addressed rename appends one target metadata commit");
                AssertEqual(sourceTitle, store.Load(fixture.Session.Id).Title, "addressed rename does not rename the source");
                var terminal = NativeTerminal(fixture, ChatRenameToolHandler.ToolId);
                ToolResultWireReadResult wire; string wireError;
                AssertTrue(ToolResultHistoryReader.TryRead(terminal, out wire, out wireError), wireError);
                var receipt = Newtonsoft.Json.JsonConvert.DeserializeObject<ChatRenameResult>(wire.Result.DataJson);
                var expectedOperation = ChatRenameToolHandler.AddressedOperationId(fixture.Session.Id,
                    terminal.RunId, terminal.ToolCallId, address, addressedTitle);
                AssertEqual(expectedOperation, receipt.TargetOperationId,
                    "source terminal retains the exact deterministic target operation");
                AssertEqual(renamed.Revision, receipt.TargetRevision, "source terminal pins the exact target revision");
                fixture.Journal.Rebuild();
                AssertEqual(addressedTitle, store.Load(target.Id).Title, "rebuild preserves the exact target metadata commit");
                AssertEqual(sourceTitle, store.Load(fixture.Session.Id).Title, "rebuild does not merge target history into source");
                targetRevision = renamed.Revision;

                // A stale or crafted registration must not bypass the native
                // handler's refusal even if it still accepts the old argument.
                var invoked = 0;
                var stale = ChatRenameToolHandler.Entry();
                var registry = new ToolHandlerRegistry();
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(stale.Id, stale.Description,
                    stale.ArgumentSchemaJson), stale.Policy, stale.Binding), new ChatRenameToolHandler(fixture.Session,
                    (agentId, title, dispatch) => {
                        invoked++;
                        dispatch();
                        store.SetAgentTitle(target, title);
                        return new ChatRenameResult { Title = target.Title, AgentId = agentId };
                    }, addressedRenameEnabled: false));
                var runtime = new ToolRuntime(registry, ChatModes.Agent, false, false);
                var call = new ToolCall("native_addressed_rename", stale.Id,
                    new JObject { ["title"] = "Must not be applied", ["agent_id"] = address }.ToString());
                var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call),
                    "run", "turn", "step", DateTime.UtcNow, false, 5), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome,
                    "unsupported addressed native receipt produces an ordinary no-effect refusal");
                AssertEqual(ToolFailureKind.RejectedNoEffect, result.Recovery.FailureKind,
                    "addressed refusal is classified before dispatch");
                AssertEqual(0, invoked, "native refusal precedes the addressed mutation delegate");
                AssertTrue(!result.MayHaveDispatched, "native refusal never enters the effect boundary");
                AssertEqual(ToolEffectEvidence.None, result.Evidence.Effect, "refused rename has no effect evidence");
                var retained = store.Load(target.Id);
                AssertEqual(targetRevision, retained.Revision, "addressed refusal appends no target commit");
                AssertEqual(addressedTitle, retained.Title, "addressed refusal preserves the exact target title");
                AssertTrue(retained.TitleAssignedByAgent, "addressed refusal preserves target naming ownership");
                AssertEqual(sourceTitle, store.Load(fixture.Session.Id).Title, "addressed refusal does not rename the source instead");
            });
        }
    }
}
