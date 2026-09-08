using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SharedMarkdownLifecycle()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var store = new ChatStore(paths);
                var links = new ArtifactWorkingSetService(store.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = executor.GetControllerTools().ToList();
                var a = NewSession(adapter);
                Func<ChatSession, string, string, ToolRunResult> save = (chat, selectedTarget, markdown) => {
                    var command = Command(MarkdownDocumentToolCatalog.SaveToolId, "title", "Architecture.md",
                        "description", "System responsibilities, protocols and recovery procedures.", "markdown", markdown);
                    if (selectedTarget != null) command.Arguments["target"] = selectedTarget;
                    return executor.ExecuteManual(command, catalog, new AppSettings(), false, false, chat);
                };
                var body = "# Архитектура\r\n\r\n" + new string('Ж', 40000) + "\n```csharp\nvar path = @\"C:\\docs\";\n```\n";
                var created = save(a, null, body);
                AssertTrue(created.Success, "native authored Markdown creation: " + created.Message);
                var first = a.Artifacts.Single(item => item.Kind == ChatArtifactKinds.Markdown);
                var exact = ChatResourceUri.CreateArtifactRevision(a, first);
                var logical = MarkdownDocumentIdentity.Identity(a, MarkdownDocumentIdentity.LogicalId(first.Id));
                var target = (string)JObject.Parse(created.DataJson)["target"];
                AssertTrue(!created.DataJson.Contains(body) && !created.DataJson.Contains("rna://"), "model result is compact and semantic");
                AssertTrue(string.IsNullOrEmpty(a.ActivePlanDocumentArtifactId) && string.IsNullOrEmpty(a.ActiveHtmlArtifactId), "MD never occupies Plan or HTML selection");
                a.Messages.Add(new ChatMessage { Role = "assistant", Content = "Documentation", ResourceRefs = new List<ResourceRef> { exact } });
                store.Save(a);
                var b = NewSession(adapter); b.DocumentAuthorityId = a.DocumentAuthorityId;
                var found = executor.ResourceGateway.Find(b, "Architecture", "document");
                AssertEqual(target, found.Items.Single().Target, "another chat discovers the same exact target");
                AssertContains(found.Items.Single().Description, "recovery", "authored purpose is available for discovery");
                links.Change(b, LinkRequest(b, exact.Uri, false), store.Save);
                b = store.Load(b.Id);
                var currentRead = executor.ResourceGateway.Read(b, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 32000 });
                var reconstructed = currentRead.Result.Text;
                var page = currentRead;
                for (var count = 0; page.Result.NextCursor != null && count < 8; count++)
                {
                    page = executor.ResourceGateway.Read(b, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 32000, Cursor = page.Result.NextCursor });
                    reconstructed += page.Result.Text;
                }
                AssertTrue(page.Result.Complete, "bounded Markdown pages reach a complete representation");
                AssertEqual(body, reconstructed, "complete Unicode Markdown survives another chat and reload");
                AssertTrue(currentRead.Result.Resource.Dependencies.Any(item => item.Kind == "current-state" && item.Resource.Identity.Equals(logical)), "reads carry the shared logical-head dependency");
                var updated = save(b, target, "# Updated\n\nSecond version.\n");
                AssertTrue(updated.Success, "second chat edits the same MD: " + updated.Message);
                var currentTarget = (string)JObject.Parse(updated.DataJson)["target"];
                AssertTrue(!save(a, target, "# Must not overwrite").Success, "an old semantic snapshot target is rejected");
                AssertEqual(body, store.DocumentArtifacts.Read(b, exact).InlineText, "first revision remains exact");
                AssertContains(executor.ResourceGateway.Read(b, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 128 }).Result.Text,
                    "# Архитектура", "common Gateway still reads historical Markdown after logical-head drift");
                var head = store.DocumentArtifacts.CurrentSnapshot(b, logical);
                AssertTrue(head.Uri != exact.Uri, "edit publishes a new shared head");
                AssertEqual(1, links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Count(item => item.Kind == ChatArtifactKinds.Markdown), "picker lists one current version per MD");
                var c = NewSession(adapter); c.DocumentAuthorityId = b.DocumentAuthorityId;
                var writers = new[] { b, c }.Select((chat, index) => Task.Run(() => save(chat, currentTarget, "# Writer " + index))).ToArray();
                Task.WaitAll(writers);
                AssertEqual(1, writers.Count(task => task.Result.Success), "competing MD writers publish one child");
                AssertEqual(3, store.DocumentArtifacts.SnapshotHistory(b, MarkdownDocumentIdentity.LogicalId(first.Id)).Count, "stale refusal adds no revision");
                var current = store.DocumentArtifacts.Read(b, store.DocumentArtifacts.CurrentSnapshot(b, logical));
                currentTarget = ResourceGatewayService.IntentTarget(new ResourceDescriptor { Kind = current.Kind, Title = current.Title, CreatedUtc = current.CreatedUtc });
                var restored = executor.ExecuteManual(Command(MarkdownDocumentToolCatalog.RestoreToolId, "target", currentTarget, "version", 1),
                    catalog, new AppSettings(), false, false, b);
                AssertTrue(restored.Success, "restore through the same document authority: " + restored.Message);
                var restoredRef = store.DocumentArtifacts.CurrentSnapshot(b, logical);
                var restoredArtifact = store.DocumentArtifacts.Read(b, restoredRef);
                AssertEqual(body, restoredArtifact.InlineText, "restore preserves every source character");
                AssertEqual(current.Id, restoredArtifact.ParentArtifactId, "restore advances causal parent");
                AssertEqual(exact.Uri, executor.ResourceAuthority.Revisions.GetRevision(executor.ResourceAuthority.Scope(b, true), restoredRef).RestoredFrom.Uri,
                    "restore retains exact source provenance");
                var independent = save(b, null, "# Independent\nDifferent document with the same title.");
                AssertTrue(independent.Success, "second MD with the same title coexists");
                AssertEqual(2, links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Count(item => item.Kind == ChatArtifactKinds.Markdown), "independent MD resources remain distinct");
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), store.DocumentArtifacts);
                var original = ingestion.Stage(b, "uploaded.md", "text/markdown", System.Text.Encoding.UTF8.GetBytes("# Immutable original"));
                var originalMessage = new ChatMessage { Role = "user", Content = "Original", Attachments = ingestion.LoadDrafts(b, new[] { original.Id }).ToList() };
                b.Messages.Add(originalMessage); ingestion.CommitAndLink(b, originalMessage, 0);
                var originalTarget = executor.ResourceGateway.Find(b, "uploaded.md", "document").Items.Single().Target;
                AssertTrue(!save(b, originalTarget, "# Must stay original").Success, "a .md extension never grants mutation authority to an uploaded original");
                var fork = NewSession(adapter); fork.DocumentAuthorityId = b.DocumentAuthorityId; fork.ParentSessionId = b.Id;
                fork.Messages = ChatCloneService.CloneMessages(a.Messages);
                ChatCloneService.PrepareForkResources(b, fork, store.LoadArtifactBody, new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertEqual(exact.Uri, fork.Messages.Single().ResourceRefs.Single().Uri, "fork retains historical document identity");
                store.Save(b); b = store.Load(b.Id);
                AssertEqual(4, b.Artifacts.Count(item => MarkdownDocumentIdentity.LogicalId(item.Id) == MarkdownDocumentIdentity.LogicalId(first.Id)), "library reload reconstructs complete committed MD history");
                AssertTrue(b.Artifacts.Where(item => MarkdownDocumentIdentity.LogicalId(item.Id) != null).All(item => item.InlineText == null), "opening a chat loads Markdown metadata rather than every historical body");
                links.Change(b, LinkRequest(b, restoredRef.Uri, true), store.Save);
                AssertTrue(ArtifactWorkingSet.IsDetached(b, restoredArtifact), "unlink applies to the entire logical MD");
                AssertEqual(restoredRef.Uri, store.DocumentArtifacts.CurrentSnapshot(b, logical).Uri, "unlink preserves document head");
                store.Delete(a.Host, a.DocumentKey, a.Id);
                var gc = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed && gc.Health.MissingBlobCount == 0, "origin deletion and GC preserve document bytes");
                AssertEqual(body, new ChatStore(paths).DocumentArtifacts.Read(b, exact).InlineText, "history survives a fresh store");
                var foreign = NewSession(adapter); foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                RuntimeThrows<System.IO.InvalidDataException>(() => store.DocumentArtifacts.Read(foreign, exact));
                links.Change(c, LinkRequest(c, restoredRef.Uri, false), store.Save);
                System.IO.File.Delete(executor.Payloads.PathFor(restoredArtifact.ContentSha256));
                RuntimeThrows<System.IO.InvalidDataException>(() => store.DocumentArtifacts.Read(c, restoredRef));
                links.Change(c, LinkRequest(c, restoredRef.Uri, true), store.Save);
                AssertTrue(ArtifactWorkingSet.IsDetached(c, restoredArtifact), "missing MD bytes never prevent unlinking the chat reference");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "all MD effects and refusals are terminal");
            });
        }

        private static void SharedMarkdownKernelReplay()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var body = "# Detailed documentation\r\n" + new string('x', 40000) + "\nEnd.\n";
                var responses = new Queue<string>(new[] {
                    LoadToolSchemaResponse(MarkdownDocumentToolCatalog.SaveToolId),
                    ModelProtocolWire.Write("I will save the complete documentation as a reusable Markdown document.", new[] {
                        new ConversationToolCall { Name = MarkdownDocumentToolCatalog.SaveToolId,
                            Arguments = new JObject { ["title"] = "Detailed.md", ["description"] = "Detailed system documentation.", ["markdown"] = body } } }),
                    ModelProtocolWire.Write("Documentation saved.", new ConversationToolCall[0]) });
                var service = CreateConversationRunService(adapter, executor,
                    (settings, messages, options, stream, token) => Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() }));
                var session = NewSession(adapter);
                var result = service.ExecuteAsync(ChatModes.Agent, "Create detailed Markdown documentation.", session, NewContext(adapter),
                    new AppSettings { AutoConfirmToolActions = true, ContextWindowOverrideTokens = 131072 },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), null).GetAwaiter().GetResult();
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "the complete agent cycle discovers and executes the native Markdown tool: " + JsonConvert.SerializeObject(result.RunViewState));
                var replayed = AssertKernelReplay(session);
                var artifact = replayed.Artifacts.Single(item => item.Kind == ChatArtifactKinds.Markdown);
                AssertTrue(artifact.InlineText == null, "replay remains metadata-only for Markdown");
                AssertEqual(body, new ChatStore(FixturePaths.Value).DocumentArtifacts.Read(replayed, ChatResourceUri.CreateArtifactRevision(replayed, artifact)).InlineText,
                    "large accepted-call text survives publication, event replay and exact CAS read");
                AssertTrue(replayed.ArtifactLinks.Any(link => !link.Detached && link.Reference.Uri == ChatResourceUri.CreateArtifactRevisionUri(replayed, artifact)),
                    "the agent persists a document link without duplicate body ownership");
            });
        }

        private static void SharedMarkdownPreparedGuards()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var definition = executor.GetControllerTools().Single(item => item.Id == MarkdownDocumentToolCatalog.SaveToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Agent, false);
                var records = new List<ToolExecutionRecord>();
                foreach (var id in new[] { "batch-a", "batch-b" })
                {
                    var call = new ToolCall(id, definition.Id, "{\"title\":\"Same name\",\"description\":\"Independent reference guide.\",\"markdown\":\"# Content\"}");
                    var batchContext = new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "same-model-step", DateTime.UtcNow, false, 2);
                    var record = runtime.ExecuteAsync(batchContext, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, "two calls in one model step remain independent");
                    AssertTrue(ReferenceEquals(batchContext, record.Context), "preparation preserves the kernel execution identity");
                    records.Add(record);
                }
                AssertEqual(2, session.Artifacts.Select(item => MarkdownDocumentIdentity.LogicalId(item.Id)).Distinct().Count(), "operation receipts include runtime call identity");
                var owner = new ChatStore(FixturePaths.Value).DocumentArtifacts;
                var service = new MarkdownDocumentService(executor.ResourceGateway, owner);
                var target = (string)JObject.Parse(records[0].Result.DataJson)["target"];
                var args = new Dictionary<string, object> { ["target"] = target, ["title"] = "Updated", ["description"] = "Current guide.", ["markdown"] = "# Next" };
                var staleCall = new ToolCall("stale-prepared", definition.Id, JsonConvert.SerializeObject(args));
                var context = new ToolExecutionContext(staleCall, runtime.Describe(staleCall), "run", "turn", "stale-step", DateTime.UtcNow, false, 1);
                var prepared = service.Prepare(session, new ToolHandlerContext(context, args));
                var winnerCall = new ToolCall("winner", definition.Id, JsonConvert.SerializeObject(args));
                AssertEqual(ToolExecutionOutcome.Ok, ExecuteNative(runtime, winnerCall, runtime.Describe(winnerCall)).Outcome, "intervening writer succeeds");
                context = new ToolExecutionContext(staleCall, runtime.Describe(staleCall), "run", "turn", "stale-step", DateTime.UtcNow, false, 1, JsonConvert.SerializeObject(prepared));
                using (new ResourceMutationJournal(FixturePaths.Value).AcquireScope(executor.ResourceAuthority.Scope(session, true)))
                {
                    var rejected = RuntimeThrows<ToolMutationPreparationException>(() => MarkdownDocumentService.PreparePublication(session, context, JsonConvert.SerializeObject(prepared), owner, executor.ResourceAuthority));
                    AssertEqual("RESOURCE_REVISION_CHANGED", rejected.Code, "exact prepared base is rechecked under the publication lease");
                }
                var readOnly = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Chat, false);
                AssertEqual(ToolExecutionOutcome.Error, ExecuteNative(readOnly, winnerCall, readOnly.Describe(winnerCall)).Outcome, "Chat mode remains read-only");
                AssertEqual(0, new ResourceMutationJournal(FixturePaths.Value).Unresolved().Count, "stale preparation never crosses dispatch");
            });
        }

        private static void SharedMarkdownFailedLink()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), paths: paths,
                    persistResourceFacts: saved => { throw new System.IO.IOException("Injected link save failure."); });
                var session = NewSession(adapter);
                var definition = executor.GetControllerTools().Single(item => item.Id == MarkdownDocumentToolCatalog.SaveToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Agent, false);
                var call = new ToolCall("md-failed-link", MarkdownDocumentToolCatalog.SaveToolId,
                    "{\"title\":\"Guide\",\"description\":\"Deployment guide.\",\"markdown\":\"# Guide\\nRetained\"}");
                RuntimeThrows<System.IO.IOException>(() => ExecuteNative(runtime, call, runtime.Describe(call)));
                session.Artifacts.Clear(); session.ArtifactLinks.Clear();
                var retry = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertContains(retry.Result.DataJson, "markdown_attempt_already_published", "lost chat link cannot replay a published operation");
                AssertEqual(ToolDispatchEvidence.NotDispatched, retry.Evidence.Dispatch, "duplicate creation rejected before dispatch");
                var owner = new ChatStore(paths).DocumentArtifacts;
                AssertEqual(1, owner.List(session).Count, "one publication only");
                AssertContains(owner.Read(session, ChatResourceUri.CreateArtifactRevision(session, owner.List(session).Single())).InlineText, "Retained", "body survives failed actor save");
            });
        }
    }
}
