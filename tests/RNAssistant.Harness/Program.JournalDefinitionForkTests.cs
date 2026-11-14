using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static OfficeToolExecutor JournalDefinitionExecutor(AppDataPaths paths, IConversationStore store, ResourceJournal resources)
        {
            return new OfficeToolExecutor(FakeOfficeAdapter.ForHost("Excel"), new VbaJournalStore(paths), new SkillStore(paths),
                new ToolStore(paths), () => new AppSettings(), paths: paths, resourceAuthorityStore: resources,
                resourceMutationJournal: resources, persistResourceFacts: store.Commit);
        }

        private static ResourceRef ForkedDefinition(ChatSession chat, ResourceRef source)
        { return chat.ResourceCopies.Single(link => link.Source.Uri == source.Uri && link.Source.Revision == source.Revision).Copy; }

        private static void JournalDefinitionForkConsumers()
        {
            WithJournalInput(true, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths);
                var executor = JournalDefinitionExecutor(paths, store, resources); var gateway = executor.ResourceGateway;
                var attachments = new AttachmentStore(paths);
                var original = attachments.Import("sales.json", "application/json",
                    Encoding.UTF8.GetBytes("[{\"amount\":12,\"backup\":21}]"), source.Id);
                attachments.CommitToCas(new ChatMessage { Attachments = new List<ChatAttachment> { original } });
                var input = new ChatMessage { Role = "user", Content = "Retain exact definitions", Attachments = new List<ChatAttachment> { original } };
                LinkJournalUpload(paths, source, input); store.Commit(source, ConversationCommit.UserInput(input));
                executor.BindResourceAuthority(source);
                var runtime = executor.CreateNativeRuntime(source,
                    OfficeToolCatalog.ForHost(source.Host).Concat(executor.GetControllerTools()).ToList(), new AppSettings(), "agent", false, manualRun: true);
                Func<string, JObject, ResourceRef> publish = (tool, args) => {
                    var record = ExecuteHtmlNative(runtime, tool, args);
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, tool + ": " + record.Result.Message);
                    return record.Result.Resources.Single(item => item.Uri.StartsWith("rna://state/", StringComparison.Ordinal));
                };
                Func<ResourceRef, string> target = reference => ResourceGatewayService.IntentTarget(gateway.Resolve(source, reference.Uri).Resource);
                var data = target(input.ResourceRefs.Single());
                var draft = publish(ResourceDefinitionToolHandler.Draft, new JObject { ["name"] = "sales",
                    ["fields"] = new JArray(new JObject { ["name"] = "sales", ["type"] = "integer" }) });
                var fields = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "amount" });
                var schema = publish(ResourceDefinitionToolHandler.Publish, new JObject { ["name"] = "sales", ["draft"] = target(draft),
                    ["source"] = data, ["mapping"] = fields });
                var mapping = publish(ResourceDefinitionToolHandler.Mapping, new JObject { ["name"] = "sales", ["schema"] = target(schema),
                    ["source"] = data, ["mapping"] = fields });
                var derived = publish(ResourceDefinitionToolHandler.Derive, new JObject { ["name"] = "sales", ["mapping"] = target(mapping), ["mode"] = "virtual" });
                var saved = publish(ResourceDefinitionToolHandler.Derive, new JObject { ["name"] = "saved", ["mapping"] = target(mapping), ["mode"] = "materialized" });
                var latest = publish(ResourceDefinitionToolHandler.Mapping, new JObject { ["name"] = "sales", ["schema"] = target(schema), ["source"] = data,
                    ["mapping"] = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "backup" }) });
                var note = new ContextNote { Role = ContextNoteRole.SuppliedData, Title = "Supplied data", Text = new string('x', 70000) };
                executor.ResourceAuthority.ObserveNote(source, note, executor.Payloads); source.Context.Notes.Add(note);
                var message = new ChatMessage { Role = "user", Content = "Use the old mapping and retain the latest one",
                    ResourceRefs = new List<ResourceRef> { derived, saved, latest } };
                source.Messages.Add(message); store.Commit(source, ConversationCommit.UserInput(message));
                var child = PrepareTextFork(store, source, source.Messages.Count);
                var plan = ChatCloneService.PrepareForkResources(source, child, store.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                var childDerived = ForkedDefinition(child, derived); var childScope = executor.ResourceAuthority.Scope(child, false);
                AssertEqual(0L, resources.Capture(childScope).Generation, "preparation retains copies without publishing heads");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(child,
                    new ResourceReadRequest { Reference = childDerived, Representation = "table", MaxRows = 1 })).ErrorCode,
                    "prepared provenance is not a publication grant");
                var before = journal.Journal.WrittenBytes;
                executor.MutateChatResources(child, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, source: source, fork: plan), () => child);
                AssertTrue(journal.Journal.WrittenBytes - before < 16384, "copy graph and supplied body stay outside the bounded fork frame");
                AssertEqual(1L, resources.Capture(childScope).Generation, "one authority commit publishes the whole copied graph");
                AssertEqual(note.Evidence.Payload.Sha256, child.Context.Notes.Single().Evidence.Payload.Sha256, "supplied body reuses its exact CAS reference");
                AssertEqual(resources.GetRevision(executor.ResourceAuthority.Scope(source, false), saved).Payload.Sha256,
                    resources.GetRevision(childScope, ForkedDefinition(child, saved)).Payload.Sha256, "materialized rows are not rewritten");
                AssertEqual(ForkedDefinition(child, latest).Revision, resources.GetHead(childScope, ForkedDefinition(child, latest).Identity).Revision.Revision,
                    "publication order selects the latest copy while retaining the older mapping");
                journal.Rebuild(); child = store.Load(child.Id);
                AssertEqual(12L, Convert.ToInt64(gateway.Read(child, new ResourceReadRequest {
                    Reference = childDerived, Representation = "table", MaxRows = 1 }).Result.Table.Rows[0]["sales"]), "native copy reads its pinned older dependency");
                AssertEqual(32000, gateway.Read(child, new ResourceReadRequest {
                    Reference = child.Context.Notes.Single().Evidence.Resource, Representation = "text", MaxChars = 80000 }).Result.Text.Length,
                    "reopened supplied data keeps the gateway's bounded text window");
                AssertEqual(70000, executor.Payloads.ReadText(child.Context.Notes.Single().Evidence.Payload.ToBlobReference()).Length,
                    "bounded reads do not truncate the retained supplied body");
                RuntimeThrows<ResourceRequestException>(() => gateway.Read(child, new ResourceReadRequest { Reference = derived, Representation = "table" }));
                var reads = journal.ChatBodyReads; store.ReadHeader(child.Id); store.ReadMessageWindow(child.Id, child.Messages.Count, 1);
                AssertEqual(2L, journal.ChatBodyReads - reads, "header/page read only the selected text and resource manifest, not the copy graph or note body");
                var copyFacts = child.ResourceCopies.ToList(); child.ResourceCopies.Clear();
                RuntimeThrows<InvalidOperationException>(() => store.Commit(child, new ConversationCommit())); child.ResourceCopies = copyFacts;
                store.SetTitle(child, "Independent child");
                var nested = ForkSharedJournal(paths, journal, resources, store.Load(child.Id), child.Messages.Count);
                AssertEqual(2, nested.ResourceCopies.Select(link => link.Copy).Where(reference => reference.Uri.EndsWith("/mapping-sales", StringComparison.Ordinal))
                    .Select(reference => reference.Revision).Distinct().Count(), "nested fork retains both exact revisions");
                var nestedDerived = ForkedDefinition(nested, derived);
                store.Delete(source.Host, source.DocumentKey, source.Id); store.Delete(child.Host, child.DocumentKey, child.Id);
                journal.Rebuild(); nested = store.Load(nested.Id);
                AssertEqual(12L, Convert.ToInt64(gateway.Read(nested, new ResourceReadRequest { Reference = nestedDerived,
                    Representation = "table", MaxRows = 1 }).Result.Table.Rows[0]["sales"]), "deleted ancestors do not revoke an explicit durable copy");
                var backup = Path.Combine(paths.Root, "definition-backup");
                RuntimeThrows<InvalidOperationException>(() => JournalCasService(paths, StorageProtector.None).BackupJournals(backup));
                AssertTrue(File.Exists(Path.Combine(paths.ResourceAuthorityDirectory, "documents.json")),
                    "full Office backup remains gated on its existing identity registry; it is never removed to export a partial backup");
                var copyJson = JsonConvert.SerializeObject(nested.ResourceCopies.OrderBy(link => link.Source.Uri, StringComparer.Ordinal)
                    .ThenBy(link => link.Source.Revision, StringComparer.Ordinal).ToArray());
                File.Delete(new ChatBlobStore(paths).PathFor(TextPatternEngine.Sha256(copyJson)));
                RuntimeThrows<InvalidDataException>(() => ((IConversationStore)new ConversationJournal(paths, scope)).Load(nested.Id));
                RuntimeThrows<InvalidDataException>(() => journal.Rebuild());
            });
        }

        private static void JournalDefinitionForkReceipts()
        {
            foreach (var fault in new[] { "before_append", "after_flush", "after_fork_index" }) WithJournalInput(false, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths);
                var executor = JournalDefinitionExecutor(paths, store, resources);
                var note = new ContextNote { Role = ContextNoteRole.SuppliedData, Text = "Exact supplied bytes" };
                executor.ResourceAuthority.ObserveNote(source, note, executor.Payloads); source.Context.Notes.Add(note);
                var message = new ChatMessage { Role = "user", Content = "Copy supplied data" };
                source.Messages.Add(message); store.Commit(source, ConversationCommit.UserInput(message));
                var child = PrepareTextFork(store, source, 1);
                var plan = ChatCloneService.PrepareForkResources(source, child, store.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                var command = new ConversationCommit();
                var once = true;
                Action<string> fail = stage => { if (once && stage == fault) { once = false; throw new IOException("fork copy fault"); } };
                journal.Journal.Fault = fail; journal.Fault = fail;
                try { store.Commit(child, command); } catch (IOException) { }
                journal.Journal.Fault = null; journal.Fault = null;
                AssertEqual(fault != "before_append", store.Load(child.Id) != null, "copy provenance is accepted with the fork or not at all");
                store.Commit(child, command); var revision = child.Revision;
                store.Delete(source.Host, source.DocumentKey, source.Id); store.Commit(child, command);
                AssertEqual(revision, child.Revision, "exact retry after parent deletion cannot duplicate or change copy provenance");
                journal.Rebuild(); var loaded = store.Load(child.Id);
                AssertEqual(child.ResourceCopies.Count, loaded.ResourceCopies.Count, "copy provenance rebuilds from its retained CAS body");
                AssertEqual(0L, resources.Capture(executor.ResourceAuthority.Scope(loaded, false)).Generation,
                    "persisting provenance alone does not publish heads");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.Read(loaded,
                    new ResourceReadRequest { Reference = loaded.Context.Notes.Single().Evidence.Resource, Representation = "text" })).ErrorCode,
                    "restart cannot promote an unconfirmed copy into usable evidence");
                if (fault != "after_flush") return;
                var backup = Path.Combine(paths.Root, "copy-receipt-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "copy-receipt-restore"), _ => StorageProtector.None);
                var restoredStore = (IConversationStore)new ConversationJournal(restored, scope); var restoredChild = restoredStore.Load(child.Id);
                AssertEqual(JsonConvert.SerializeObject(loaded.ResourceCopies), JsonConvert.SerializeObject(restoredChild.ResourceCopies),
                    "isolated journal/CAS restore retains exact copy provenance");
                AssertEqual("Exact supplied bytes", new ChatBlobStore(restored).ReadText(restoredChild.Context.Notes.Single().Evidence.Payload.ToBlobReference()),
                    "restore keeps the untruncated supplied body");
                AssertEqual(0L, new ResourceJournal(restored).Capture(executor.ResourceAuthority.Scope(restoredChild, false)).Generation,
                    "restore does not manufacture a copied authority head");
            });
        }
    }
}
