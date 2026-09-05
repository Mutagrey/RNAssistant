using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ResourceBinaryLeaseUsesCanonicalViews()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, new ResourceMutationJournal(paths), payloads);
                var bytes = new byte[] { 1, 2, 3, 4 };
                var hash = ArtifactViewerService.Sha256(bytes);
                var attachment = new ChatAttachment { Id = "binary", Kind = "image", FileName = "image.png", ContentType = "image/png",
                    ContentSha256 = hash, ContentByteLength = bytes.Length };
                var message = new ChatMessage { Role = "user", Content = "Read this image.", Attachments = new List<ChatAttachment> { attachment } };
                var artifact = new ChatArtifact { Kind = ChatArtifactKinds.Image, Title = "image.png", MimeType = "image/png",
                    SourceMessageId = message.Id, ContentSha256 = hash, ContentByteLength = bytes.Length, MetadataJson = "{\"attachmentId\":\"binary\"}" };
                var session = new ChatSession(); session.Messages.Add(message); session.Artifacts.Add(artifact);
                var reads = 0;
                var gateway = new ResourceGatewayService(null, null, null, authority: authority,
                    readAttachmentBytes: item => { reads++; return bytes; });
                using (var data = new ResourceDataPlaneService(gateway, (chat, owner) => chat == session.Id && owner == "viewer"))
                {
                    var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
                    var opened = data.Open(session, "viewer", exact, "image");
                    AssertTrue(opened.Binary != null && opened.Binary.Payload.Sha256 == hash, "lease pins the CAS binary view");
                    var wire = Newtonsoft.Json.JsonConvert.SerializeObject(opened);
                    AssertTrue(!wire.Contains("AQIDBA==") && !wire.Contains("base64"), "control plane contains metadata only");
                    var router = new ResourceDataRouter(data);
                    var response = router.Handle("GET", opened.Url, System.Threading.CancellationToken.None);
                    AssertEqual(200, response.StatusCode, "same resource router serves binary data");
                    AssertEqual("image/png", response.ContentType, "binary content type is negotiated, never sniffed");
                    using (var body = new System.IO.MemoryStream()) { response.Body.CopyTo(body); AssertTrue(bytes.SequenceEqual(body.ToArray()), "exact bytes travel outside the bridge"); }
                    data.Close(session.Id, "viewer", opened.LeaseId);
                    AssertEqual(409, router.Handle("GET", opened.Url, System.Threading.CancellationToken.None).StatusCode, "closed binary capabilities fail");
                    var again = data.Open(session, "viewer", exact, "image");
                    AssertEqual(1, reads, "retained binary metadata and bytes reuse CAS, not an independent viewer cache");
                    var scan = new CasReachabilityScan(); store.ScanCasReferences(scan);
                    AssertTrue(scan.References.Any(item => item.Reference.Sha256 == hash), "binary view bytes are durable retention roots");
                    data.Close(session.Id, "viewer", again.LeaseId);
                    RuntimeThrows<OperationCanceledException>(() => data.Open(session, "viewer", exact, "image",
                        cancellationToken: new System.Threading.CancellationToken(true)));
                    RuntimeThrows<ResourceRequestException>(() => data.Open(session, "viewer", exact, "image",
                        validate: result => data.CloseWorkspace(session.Id, "viewer")));
                    for (var index = 0; index < 64; index++) data.Open(session, "viewer", exact, "image");
                    var validated = false;
                    RuntimeThrows<ResourceRequestException>(() => data.Open(session, "viewer", exact, "image",
                        validate: result => validated = true));
                    AssertTrue(!validated, "lease limits reject before another capture/validation, not after allocating bulk payloads");
                    data.CloseWorkspace(session.Id, "viewer");
                    AssertTrue(data.Open(session, "viewer", exact, "image").LeaseId != null,
                        "cancelled opens and closed workspaces release every reserved slot");
                }
            });
        }

        private static void ResourceRuntimePayloadStorage()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var text = "{\"source\":\"" + new string('x', 50000) + "\"}";
                var root = new JObject { ["LastRun"] = new JObject { ["KernelState"] = new JObject {
                    ["Summary"] = new JObject { ["PendingConfirmation"] = new JObject {
                        ["Call"] = new JObject { ["ArgumentsJson"] = text }, ["PreparedStateJson"] = text } } } } };
                RuntimePayloadService.ExternalizeProjection(root, payloads);
                AssertTrue(root.ToString().Length < 2048, "run.updated contains references, not repeated large arguments/preparation");
                AssertTrue(root.SelectToken("LastRun.KernelState.Summary.PendingConfirmation.Call.ArgumentsJson") == null, "no competing inline body");
                var hydrated = RuntimePayloadService.HydrateActiveExecution(root, payloads);
                AssertEqual(text, (string)hydrated.SelectToken("LastRun.KernelState.Summary.PendingConfirmation.Call.ArgumentsJson"), "selected pending arguments hydrate exactly");
                AssertEqual(text, (string)hydrated.SelectToken("LastRun.KernelState.Summary.PendingConfirmation.PreparedStateJson"), "selected pending preparation hydrates exactly");
                AssertTrue(root.ToString().Length < 2048, "hydration does not mutate the durable projection");
                var activity = new ChatActivity { ArgumentsJson = text, DataJson = text };
                RuntimePayloadService.ExternalizeActivity(activity, payloads);
                AssertTrue(activity.ArgumentsJson == null && activity.DataJson == null && activity.ResultPayload != null, "presentation history is reference first too");
                AssertEqual(text, RuntimePayloadService.ReadArguments(activity, payloads), "pending UI command uses the same exact payload");
            });
        }

        private static void ResourceUnpublishedRevisionRetention()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var scope = new ResourceAuthorityScopeId("conversation", "unpublished");
                var reference = new ResourceRef("rna://state/conversation/unpublished/derived-test", "r1");
                var definition = PayloadRef.FromBlob(payloads.StoreText("definition", "text/plain"));
                var part = PayloadRef.FromBlob(payloads.StoreText("part", "text/plain"));
                store.RegisterRevision(scope, new ResourceRevisionMetadata(reference, definition.Sha256, definition));
                store.RegisterView(scope, new ResourceRevisionView(reference, "index", definition.Sha256, definition, ResourceCoverage.Whole(), new[] { part }));
                var scan = new CasReachabilityScan();
                store.ScanCasReferences(scan);
                AssertEqual(0, scan.Issues.Count, "unpublished but durable revision is not a corrupt authority journal");
                AssertTrue(scan.References.Any(item => item.Reference.Sha256 == definition.Sha256) &&
                    scan.References.Any(item => item.Reference.Sha256 == part.Sha256), "both definition and indexed parts remain GC roots before first head publication");
            });
        }

        private static void ResourceSkillCatalogPublication()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var old = executor.CaptureSkills();
                var oldHead = executor.ResourceAuthority.Store.GetHead(CatalogPublicationService.ScopeId, new ResourceIdentity("rna://catalog/skills")).Revision;
                var tools = executor.GetControllerTools().ToList();
                var created = executor.ExecuteManual(Command("common.skills_upsert", "id", "common.resource_test",
                    "host", "Common", "name", "Resource test", "description", "Verify catalog publication.", "version", "1.0.0",
                    "bodyMarkdown", "# Resource test\n\nInspect exact resources.", "enabled", true), tools, new AppSettings(), false, true, session);
                AssertEqual("ok", created.Status, "skill write verifies: " + created.Message + " " + created.DataJson);
                var current = executor.CaptureSkills();
                AssertTrue(current.Generation != old.Generation && current.Skills.Any(skill => skill.Id == "common.resource_test"),
                    "complete skill snapshot becomes active only after publication");
                AssertTrue(!old.Skills.Any(skill => skill.Id == "common.resource_test"), "old captured generation remains unchanged");
                var history = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = oldHead, Representation = "text", MaxChars = 32000 }).Result;
                AssertTrue(!history.Text.Contains("common.resource_test"), "historical catalog body stays exact and does not activate new content");
                var evidence = executor.ResourceGateway.Evidence(session, history).Single();
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence,
                    executor.ResourceAuthority.CaptureMany(new[] { CatalogPublicationService.ScopeId })).State, "historical skill body is not current authority");
                current.Skills.Single(skill => skill.Id == "common.resource_test").BodyMarkdown = "tampered";
                AssertTrue(executor.CaptureSkills().Skills.Single(skill => skill.Id == "common.resource_test").BodyMarkdown != "tampered",
                    "catalog consumers cannot mutate the published body");
            });
        }

        private static void ResourcePromptPublicationIsFrozen()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var settings = new AppSettings { SystemPrompt = "PUBLISHED_ONE" };
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), () => settings, value => settings = value, paths);
                var first = executor.CaptureCatalogs();
                var secondSettings = settings.Clone();
                secondSettings.SystemPrompt = "PUBLISHED_TWO";
                executor.SaveSettingsPublication(secondSettings, () => settings = secondSettings);
                var second = executor.CaptureCatalogs();
                AssertEqual(first.Authority.Generation + 1, second.Authority.Generation,
                    "manual settings publication advances one shared catalog commit");
                var runtimeSettings = new AppSettings { Model = "REQUEST_LOCAL_MODEL", SystemPrompt = "UNPUBLISHED" };
                AssertEqual("PUBLISHED_ONE", PromptSettingsService.ApplyPublishedTemplates(runtimeSettings, first.PromptsJson).SystemPrompt,
                    "the old captured prompt generation cannot change");
                var active = PromptSettingsService.ApplyPublishedTemplates(runtimeSettings, second.PromptsJson);
                AssertEqual("PUBLISHED_TWO", active.SystemPrompt, "the next request uses committed templates");
                AssertEqual(runtimeSettings.Model, active.Model, "prompt activation cannot replace model routing/settings");
                AssertEqual("UNPUBLISHED", runtimeSettings.SystemPrompt, "activation does not mutate the caller's settings");
                AssertEqual(first.Skills.Generation, second.Skills.Generation, "a prompt publication does not invent new skill content");
            });
        }

        private static void ResourceSchemaMappingDerivedPublication()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var runtime = executor.CreateNativeRuntime(session,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), new AppSettings(), "agent", false);
                var gateway = executor.ResourceGateway;
                var scope = executor.ResourceAuthority.Scope(session, false);
                Func<SchemaRegistrySnapshot> schemas = () => ResourceStateProvider.CaptureSchemas(executor.ResourceAuthority.CaptureMany(new[] { scope }));
                Func<string, JObject, ResourceRef> publish = (tool, args) => {
                    var record = ExecuteHtmlNative(runtime, tool, args);
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, tool + ": " + record.Result.Message + " " + record.Result.DataJson);
                    AssertTrue(record.AuthorityCommitId != null, "definition publication crosses the shared commit barrier");
                    return record.Result.Resources.Single(item => item.Uri.StartsWith("rna://state/", StringComparison.Ordinal));
                };
                Func<ResourceRef, string> target = reference => ResourceGatewayService.IntentTarget(gateway.Resolve(session, reference.Uri).Resource);
                var empty = schemas();
                var draft = publish(ResourceDefinitionToolHandler.Draft, new JObject { ["name"] = "sales",
                    ["fields"] = new JArray(new JObject { ["name"] = "sales", ["type"] = "integer", ["unit"] = "RUB" }) });
                AssertEqual(empty.Generation, schemas().Generation, "draft never activates a schema generation");
                session.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.File, Title = "schema-source.json", MimeType = "application/json",
                    InlineText = "[{\"amount\":12,\"backup\":21},{\"amount\":14,\"backup\":41}]" });
                var source = gateway.Find(session, "schema-source.json", "conversation").Items.Single().Target;
                var fields = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "amount" });
                var schema = publish(ResourceDefinitionToolHandler.Publish, new JObject { ["name"] = "sales", ["draft"] = target(draft),
                    ["source"] = source, ["mapping"] = fields });
                var frozen = schemas();
                AssertEqual(1, frozen.Schemas.Count, "only published schema is active");
                AssertTrue(frozen.Generation != empty.Generation, "publication advances schema authority");
                var mapping = publish(ResourceDefinitionToolHandler.Mapping, new JObject { ["name"] = "sales", ["schema"] = target(schema),
                    ["source"] = source, ["mapping"] = fields });
                var derived = publish(ResourceDefinitionToolHandler.Derive, new JObject { ["name"] = "sales", ["mapping"] = target(mapping), ["mode"] = "virtual" });
                var read = gateway.Read(session, new ResourceReadRequest { Reference = derived, Representation = "table", MaxRows = 1 }).Result;
                AssertEqual(12L, Convert.ToInt64(read.Table.Rows[0]["sales"]), "virtual rows use the exact physical mapping");
                AssertEqual("integer", read.Table.Columns[0].Type, "semantic type is validated per emitted batch");
                var evidence = gateway.Evidence(session, read).Single();
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(evidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State,
                    "derived evidence is current before dependency publication");
                var materialized = publish(ResourceDefinitionToolHandler.Derive, new JObject { ["name"] = "saved-sales", ["mapping"] = target(mapping), ["mode"] = "materialized" });
                AssertEqual(2, gateway.Read(session, new ResourceReadRequest { Reference = materialized, Representation = "table", MaxRows = 10 }).Result.Table.Rows.Count,
                    "materialized output is an immutable resource snapshot");
                publish(ResourceDefinitionToolHandler.Mapping, new JObject { ["name"] = "sales", ["schema"] = target(schema), ["source"] = source,
                    ["mapping"] = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "backup" }) });
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State,
                    "mapping publication invalidates derived currentness without changing its historical body");
                AssertEqual(12L, Convert.ToInt64(gateway.Read(session, new ResourceReadRequest { Reference = derived, Representation = "table", MaxRows = 1 }).Result.Table.Rows[0]["sales"]),
                    "historical exact derived view does not silently follow a new mapping");
                AssertEqual(1, frozen.Schemas.Count, "captured schema registry stays immutable");
            });
        }

        private static void ResourceCompletedCallDoesNotHydrateArguments()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var arguments = "{\"content\":\"" + new string('x', 100000) + "\"}";
                var invocation = new ToolInvocation { ToolCallId = "large", ToolId = "common.html_workspace_write_file" };
                var call = new ChatMessage { Role = "assistant", ProtocolMessage = true, ToolCallId = invocation.ToolCallId, ToolName = invocation.ToolId,
                    Content = arguments, AcceptedCallOrigin = new AcceptedToolCallOrigin("step", "attempt", 0), ToolCalls = new List<RNAssistant.Core.Llm.LlmToolCall> {
                        new RNAssistant.Core.Llm.LlmToolCall { Id = invocation.ToolCallId, Name = invocation.ToolId, Type = "function", ArgumentsJson = arguments } },
                    ArgumentPayload = PayloadRef.FromBlob(payloads.StoreText(arguments, "application/json")) };
                AcceptedCallPayloadService.Externalize(call, payloads);
                AssertTrue(call.Content.Length < 256 && call.ToolCalls.Count == 0, "durable accepted fact contains metadata only");
                var result = AgentJsonProtocol.CreateToolResultMessage(invocation,
                    RNAssistant.Core.Tools.Contracts.ToolResult.Ok("saved"), "tool");
                result.ResourceEffect = new ResourceEffect("effect", invocation.ToolId, ResourceEffectOutcome.VerifiedChanged, new ResourceImpact[0]);
                var frozen = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]), "tools", new SkillCatalogSnapshot(null), null, 3);
                var compiled = new ModelContextCompiler().Compile(frozen, new ChatMessage[0], new[] { call, result }, null, new ToolCatalogEntry[0], new AppSettings(), 1024);
                AssertEqual(0, compiled.Receipt.HydratedPayloads, "terminal frame compiles without even a payload reader");
                AssertTrue(string.Join("", compiled.Messages.Select(item => item.Content)).Length < 4096, "completed large source is not reserialized into prompt");
            });
        }

        private static void ResourceBoundedTableLeaseUsesOneSnapshot()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var target = executor.ResourceGateway.ResolveIntentTarget(session, "Excel range: Data!A1:B4");
                adapter.ExcelBackendCalls.Clear();
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var lease = data.Open(session, "workspace", target.Reference, "table");
                    var first = JObject.Parse(System.Text.Encoding.UTF8.GetString(data.Read(lease.LeaseId, 0, 2, System.Threading.CancellationToken.None)));
                    AssertEqual(2, ((JArray)first["rows"]).Count, "first batch is bounded by rows");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), "one bounded provider capture");
                    adapter.SetExcelCellForTest("Data", "B2", 999);
                    var next = JObject.Parse(System.Text.Encoding.UTF8.GetString(data.Read(lease.LeaseId, 2, 2, System.Threading.CancellationToken.None)));
                    AssertEqual((string)first["resource"]["revision"], (string)next["resource"]["revision"], "lease never mixes revisions");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelRangeReadOperation), "continuation uses indexed CAS parts, not Office");
                    var fresh = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = target.Reference, Representation = "table", MaxRows = 4 });
                    AssertTrue(fresh.Result.Resource.Reference.Revision != (string)first["resource"]["revision"], "new head observes external drift");
                    RuntimeThrows<ResourceRequestException>(() => data.Close(session.Id, "other-workspace", lease.LeaseId));
                    data.Close(session.Id, "workspace", lease.LeaseId);
                    RuntimeThrows<ResourceRequestException>(() => data.Read(lease.LeaseId, 4, 1, System.Threading.CancellationToken.None));
                    var router = new ResourceDataRouter(data);
                    AssertEqual(405, router.Handle("POST", lease.Url, System.Threading.CancellationToken.None).StatusCode, "router rejects methods");
                    AssertEqual(403, router.Handle("GET", "https://rnassistant.local-resource/v1/x/../" + lease.LeaseId,
                        System.Threading.CancellationToken.None).StatusCode, "router rejects traversal");
                }
            });
        }

        private static void LocalResourceRestorePublishesNewLogicalRevision()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => RNAssistant.Office.Tools.HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>one</p>", true));
                var originalArtifact = session.ActiveHtmlArtifactId;
                var scope = executor.ResourceAuthority.Scope(session, false);
                var identity = ResourceStateProvider.Identity(scope, "html-workspace");
                var first = executor.ResourceAuthority.Store.GetHead(scope, identity).Revision;
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => RNAssistant.Office.Tools.HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>two</p>", true));
                executor.MutateLocalResources(session, "common.html_workspace_restore", null, () => {
                    RNAssistant.Office.Tools.HtmlWorkspaceToolService.RestoreSnapshot(session, originalArtifact); return true; });
                var restored = executor.ResourceAuthority.Store.GetHead(scope, identity).Revision;
                AssertTrue(first.Revision != restored.Revision, "restore is a new logical revision even for the same payload");
                var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                var metadata = revisions.GetRevision(scope, restored);
                AssertEqual(first.Revision, metadata.RestoredFrom.Revision, "restore retains exact origin lineage");
                AssertEqual(revisions.GetRevision(scope, first).Payload.Sha256, metadata.Payload.Sha256, "CAS deduplicates identical restored bytes");
            });
        }

        private static void ResourceChatLifecyclePublication()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var chats = new ChatStore(paths);
                var store = new ResourceAuthorityStore(paths);
                long? expectedGenerationDuringSave = null;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths),
                    paths: paths, resourceAuthorityStore: store, loadArtifactBody: chats.LoadArtifactBody,
                    persistResourceFacts: saved =>
                    {
                        if (expectedGenerationDuringSave.HasValue)
                            AssertEqual(expectedGenerationDuringSave.Value, store.Capture(new ResourceAuthorityScopeId("conversation", saved.Id)).Generation,
                                "conversation state persists before authority becomes visible");
                        chats.Save(saved);
                    });
                var session = NewSession(adapter);
                var edits = new ChatHistoryEditService(_ => { }, (_, reason) => { }, chats.LoadArtifactBody);
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>one</p>", true));
                executor.MutateLocalResources(session, "common.html_workspace_bind_data", null,
                    () => HtmlWorkspaceToolService.UpsertDataSource(session, "data", "{\"count\":1}"));
                executor.MutateLocalResources(session, "common.plan_doc_save", null,
                    () => new PlanDocumentService().Save(session, "Plan", "# Plan", "draft", () => { }));
                executor.MutateLocalResources(session, "common.task_list_set", null,
                    () => new TaskListService().Set(session, "Goal", new[] { "Read", "Change", "Verify" }
                        .Select(text => new ChatTaskStep { Text = text }).ToList(), () => { }));
                var scope = executor.ResourceAuthority.Scope(session, false);
                var html = ResourceStateProvider.Identity(scope, "html-workspace");
                var plan = ResourceStateProvider.Identity(scope, "plan-document");
                var tasks = ResourceStateProvider.Identity(scope, "task-list");
                var first = store.GetHead(scope, html).Revision;
                var planBefore = store.GetHead(scope, plan).Revision;
                var tasksBefore = store.GetHead(scope, tasks).Revision;
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Start" });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Ready", ResourceRefs = new List<ResourceRef> {
                    ChatResourceUri.ResolveArtifactRevision(session, session.ActivePlanDocumentArtifactId),
                    ChatResourceUri.ResolveArtifactRevision(session, session.ActiveTaskListArtifactId) } });
                var target = new ChatMessage { Role = "user", Content = "Continue",
                    HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId) };
                session.Messages.Add(target);
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>two</p>", true));
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Changed" });
                var second = store.GetHead(scope, html).Revision;
                expectedGenerationDuringSave = store.Capture(scope).Generation;
                executor.MutateChatResources(session, new ChatResourceMutationIntent(ChatResourceMutationKind.Edit, target.Id, text: "Replay"),
                    () => edits.RewriteUserMessage(session, session.Id, target.Id, -1, "Replay"));
                expectedGenerationDuringSave = null;
                var restored = store.GetHead(scope, html).Revision;
                var metadata = store.GetRevision(scope, restored);
                AssertEqual(first.Revision, metadata.RestoredFrom.Revision, "edit has exact restore origin");
                AssertEqual(second.Revision, metadata.Parent.Revision, "edit advances, never rewinds, logical lineage");
                AssertEqual(planBefore.Revision, store.GetHead(scope, plan).Revision.Revision, "unchanged plan has no spurious revision");
                AssertEqual(tasksBefore.Revision, store.GetHead(scope, tasks).Revision.Revision, "unchanged task list has no spurious revision");
                AssertEqual("Replay", chats.Load(session.Id).Messages.Last().Content, "rewritten history is durable before return");

                var sourceGeneration = store.Capture(scope).Generation;
                var fork = NewSession(adapter);
                fork.ParentSessionId = session.Id; fork.ParentSessionRevision = session.Revision;
                fork.DocumentAuthorityId = session.DocumentAuthorityId;
                fork.Messages = ChatCloneService.CloneMessages(session.Messages);
                ChatCloneService.PrepareForkResources(session, fork, chats.LoadArtifactBody);
                AssertTrue(fork.HtmlWorkspace.DataSources.Single().Binding.Resource.Uri != session.HtmlWorkspace.DataSources.Single().Binding.Resource.Uri,
                    "copied artifact bindings are explicitly rebound to child resources");
                AssertTrue(fork.Artifacts.Any(item => item.Id == session.ActiveHtmlArtifactId &&
                    item.InlineText == session.Artifacts.Single(source => source.Id == session.ActiveHtmlArtifactId).InlineText),
                    "rebinding never rewrites the immutable copied snapshot body");
                expectedGenerationDuringSave = 0;
                executor.MutateChatResources(fork, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, target.Id, source: session), () => fork);
                expectedGenerationDuringSave = null;
                var forkScope = executor.ResourceAuthority.Scope(fork, false);
                var forkHead = store.GetHead(forkScope, ResourceStateProvider.Identity(forkScope, "html-workspace")).Revision;
                AssertEqual(1L, store.Capture(forkScope).Generation, "all fork heads are published in one commit");
                AssertEqual(4, store.Capture(forkScope).Commits.Single().HeadChanges.Count, "fork atomically publishes workspace, plan, tasks and membership");
                AssertEqual(session.DocumentAuthorityId, fork.DocumentAuthorityId, "chat fork keeps the same live document authority");
                AssertTrue(forkHead.Uri != restored.Uri && forkHead.Revision != restored.Revision, "fork has its own logical resource identity/revision");
                executor.MutateChatResources(fork, new ChatResourceMutationIntent(ChatResourceMutationKind.Edit, target.Id, text: "Replay"), () => true);
                AssertEqual(ResourceEffectOutcome.VerifiedNoChange, store.Capture(forkScope).Commits.Last().Effect.Outcome,
                    "history with unchanged resource membership does not invent revisions");
                AssertEqual(forkHead.Revision, store.GetHead(forkScope, forkHead.Identity).Revision.Revision, "no-op preserves exact head");

                var retained = store.GetRevision(forkScope, forkHead).Payload;
                expectedGenerationDuringSave = store.Capture(forkScope).Generation;
                executor.MutateChatResources(fork, new ChatResourceMutationIntent(ChatResourceMutationKind.Clear), () =>
                { edits.Clear(fork, new DocumentContext()); return true; });
                expectedGenerationDuringSave = null;
                AssertTrue(store.Capture(forkScope).Heads.Values.All(head => head.Knowledge == HeadKnowledge.Unavailable),
                    "clear atomically removes active heads");
                AssertEqual(0, chats.Load(fork.Id).Messages.Count, "clear is durable");
                AssertEqual(sourceGeneration, store.Capture(scope).Generation, "fork and clear cannot change source chat heads");
                AssertEqual(retained.Sha256, store.GetRevision(forkScope, forkHead).Payload.Sha256, "clear retains immutable revision and CAS");
                var historical = executor.ResourceGateway.Read(fork, new ResourceReadRequest { Reference = forkHead, Representation = "text", MaxChars = 32000 }).Result;
                AssertTrue(historical.Text.Contains("<p>one</p>"), "exact logical revision stays readable after clear");
                AssertEqual(HeadKnowledge.Unavailable, store.GetHead(forkScope, forkHead.Identity).Knowledge, "historical read cannot resurrect current head");
            });
        }

        private static void ResourceChatLifecyclePersistenceFailure()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var chats = new ChatStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var fail = false;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), paths: paths,
                    resourceAuthorityStore: store, persistResourceFacts: saved =>
                    { chats.Save(saved); if (fail) throw new System.IO.IOException("Injected failure after durable conversation write."); });
                var session = NewSession(adapter);
                var edits = new ChatHistoryEditService(_ => { }, (_, reason) => { });
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>retained</p>", true));
                var scope = executor.ResourceAuthority.Scope(session, false);
                var old = store.GetHead(scope, ResourceStateProvider.Identity(scope, "html-workspace")).Revision;
                fail = true;
                RuntimeThrows<System.IO.IOException>(() => executor.MutateChatResources(session,
                    new ChatResourceMutationIntent(ChatResourceMutationKind.Clear), () => { edits.Clear(session, new DocumentContext()); return true; }));
                AssertEqual(0, chats.Load(session.Id).Artifacts.Count, "injected failure is after actual persistence");
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceAuthority.CaptureMany(new[] { scope }));
                var journal = new ResourceMutationJournal(paths);
                AssertEqual(MutationAttemptState.DispatchMayHaveOccurred, journal.Unresolved().Single().State, "failed publication remains unresolved");
                ResourceMutationAuthorityObserver.ReconcileInterrupted(executor.ResourceAuthority, journal);
                AssertEqual(HeadKnowledge.Unknown, store.GetHead(scope, old.Identity).Knowledge, "recovery marks uncertain effect unknown");
                AssertEqual(0, journal.Unresolved().Count, "recovery links a terminal authority commit without replaying clear");
                AssertTrue(store.GetRevision(scope, old).Payload != null, "failed clear never deletes historical bytes");
            });
        }

        private static void ResourceForkPreparationFailsClosed()
        {
            var adapter = FakeOfficeAdapter.ForHost("Word");
            var source = NewSession(adapter);
            HtmlWorkspaceToolService.UpsertFile(source, "index.html", "html", "<p>old</p>", true);
            var checkpoint = ChatResourceUri.ResolveArtifactRevision(source, source.ActiveHtmlArtifactId);
            source.Messages.Add(new ChatMessage { Role = "user", Content = "Checkpoint", HtmlWorkspaceCheckpoint = checkpoint });
            HtmlWorkspaceToolService.UpsertFile(source, "index.html", "html", "<p>new</p>", true);
            source.Artifacts.Single(item => item.Id == ResourceUri.Parse(checkpoint.Uri).Segments[2]).InlineText = null;
            var fork = NewSession(adapter);
            fork.ParentSessionId = source.Id;
            fork.Messages = ChatCloneService.CloneMessages(source.Messages);
            RuntimeThrows<InvalidOperationException>(() => ChatCloneService.PrepareForkResources(source, fork, (_, id) => false));
            AssertTrue(!fork.HtmlWorkspace.Files.Any(item => item.Content == "<p>new</p>"),
                "unavailable old checkpoint never silently falls back to the parent's newer workspace");

            source.Messages.Clear();
            source.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "bound", Binding = new HtmlWorkspaceDataBinding {
                Resource = new ResourceRef("rna://document/shared/root", "r1"), View = "text", Policy = "exact",
                Schema = new ResourceRef(ResourceStateProvider.Identity(new ResourceAuthorityScopeId("conversation", source.Id), "schema-published-demo").Uri, "r1") } });
            HtmlWorkspaceArtifactService.CaptureCurrent(source, "Bound definition");
            var boundFork = NewSession(adapter); boundFork.ParentSessionId = source.Id;
            RuntimeThrows<InvalidOperationException>(() => ChatCloneService.PrepareForkResources(source, boundFork, null));
            AssertTrue(boundFork.HtmlWorkspace.DataSources.Single().Binding.Schema.Uri.Contains(source.Id),
                "unsupported definition copy cannot manufacture a child revision or alias another chat's current head");
        }

        private static void ResourceAuthorityAtomicCommitAndReplay()
        {
            WithTempPaths(paths =>
            {
                var writer = new ResourceAuthorityStore(paths);
                var reader = new ResourceAuthorityStore(paths);
                var notified = 0;
                reader.Changed += (sender, change) => { notified++; AssertTrue(change.Generation > 0, "notification follows durable publication"); };
                var scope = new ResourceAuthorityScopeId("document", "shared");
                var first = new ResourceRef("rna://document/shared/sheet", "r1");
                var second = new ResourceRef("rna://document/shared/module", "r2");
                AssertEqual(0L, reader.Capture(scope).Generation, "reader cached initial generation");
                var commit = ResourceAuthorityCommit.Create(scope, 0, null, new[] {
                    new ResourceHeadChange(first.Identity, null, ResourceHeadState.Known(first, 1)),
                    new ResourceHeadChange(second.Identity, null, ResourceHeadState.Known(second, 1)) }, AuthorityCommitReason.InitialObservation);
                RuntimeThrows<ResourceAuthorityConflictException>(() => writer.Publish(commit));
                writer.RegisterRevision(scope, new ResourceRevisionMetadata(first, null));
                writer.RegisterRevision(scope, new ResourceRevisionMetadata(second, null));
                writer.Changed += (sender, args) => { throw new InvalidOperationException("UI refresh failed"); };
                writer.Publish(commit);
                var frozen = reader.Capture(scope);
                AssertEqual(1L, frozen.Generation, "another window sees committed generation");
                AssertEqual(2, frozen.Heads.Count, "all heads appear atomically");
                AssertTrue(writer.Publish(commit).Duplicate, "same commit is idempotent");
                var conflict = false;
                try { writer.Publish(ResourceAuthorityCommit.Create(scope, 0, null, new ResourceHeadChange[0], AuthorityCommitReason.MetadataTransition)); }
                catch (ResourceAuthorityConflictException) { conflict = true; }
                AssertTrue(conflict, "stale generation fails compare-and-publish");
                var unknown = ResourceAuthorityCommit.Create(scope, 1,
                    new ResourceEffect("e1", "write", ResourceEffectOutcome.UnknownAfterDispatch,
                        new[] { new ResourceImpact(first.Identity, ResourceImpactRelation.Exact) }),
                    new[] { new ResourceHeadChange(first.Identity, frozen.GetHead(first.Identity), ResourceHeadState.Unknown(first.Identity, 2, "crash")) },
                    AuthorityCommitReason.MutationEffect, "attempt1");
                writer.Publish(unknown);
                AssertEqual(2L, reader.Capture(scope).Generation, "cached readers incrementally catch up another window's commit");
                AssertEqual(HeadKnowledge.Known, frozen.GetHead(first.Identity).Knowledge, "old capture remains frozen");
                var replay = new ResourceAuthorityStore(paths).Capture(scope);
                AssertTrue(notified > 0, "other consumers of the same authority receive metadata notifications");
                AssertEqual(HeadKnowledge.Unknown, replay.GetHead(first.Identity).Knowledge, "unknown survives restart");
                AssertEqual(null, replay.GetHead(first.Identity).Revision, "unknown cannot advertise prior revision");
                AssertEqual(2, replay.Commits.Count, "effect history is replayed with heads");
            });
        }

        private static void ResourceEvidenceUsesFrozenAuthority()
        {
            var scope = new ResourceAuthorityScopeId("document", "d");
            var r1 = new ResourceRef("rna://vba/d/module", "r1");
            var r2 = new ResourceRef(r1.Uri, "r2");
            var evidence = new ResourceEvidence("read1", scope, r1, "source", ResourceCoverage.Whole(), true, 1);
            var reducer = new EvidenceStateReducer();
            Func<ResourceHeadState, ResourceAuthoritySnapshotSet> freeze = head => new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, head.AuthorityGeneration, null, 0, new[] { head }) });
            var before = freeze(ResourceHeadState.Known(r1, 1));
            AssertEqual(EvidenceState.Current, reducer.Reduce(evidence, before).State, "read is current at captured generation");
            AssertEqual(EvidenceState.Superseded, reducer.Reduce(evidence, freeze(ResourceHeadState.Known(r2, 2))).State, "same-run write supersedes read");
            AssertEqual(EvidenceState.Unknown, reducer.Reduce(evidence, freeze(ResourceHeadState.Unknown(r1.Identity, 2, "ambiguous dispatch"))).State, "unknown effect cannot remain current");
            AssertEqual(EvidenceState.Current, reducer.Reduce(evidence, before).State, "reduction never changes observation");
            var matcher = new ExcelResourceImpactMatcher();
            AssertTrue(matcher.Intersects(new ResourceCoverage("cell-range", "Sheet1!A1:F500"), new ResourceCoverage("cell-range", "Sheet1!B4:B20")), "range intersection");
            AssertTrue(!matcher.Intersects(new ResourceCoverage("cell-range", "Sheet1!A1:F10"), new ResourceCoverage("cell-range", "Sheet1!G1:H10")), "disjoint ranges");
            var artifact1 = new ResourceRef("rna://chat/s/artifact/a/revision/1", "1");
            var artifact2 = new ResourceRef("rna://chat/s/artifact/a/revision/2", "2");
            AssertTrue(artifact1.Identity.Equals(artifact2.Identity), "exact artifact revisions share one logical identity");
        }

        private static void ResourceCompilerFiltersBeforeBudget()
        {
            var scope = new ResourceAuthorityScopeId("document", "d");
            var r1 = new ResourceRef("rna://vba/d/module", "r1");
            var r2 = new ResourceRef(r1.Uri, "r2");
            var evidence = new ResourceEvidence("read", scope, r1, "source", ResourceCoverage.Whole(), true, 1);
            var command = new ToolInvocation { ToolId = "common.resources_read", ToolCallId = "call1" };
            var result = AgentJsonProtocol.CreateToolResultMessage(command,
                new ToolResultMaterialization(RNAssistant.Core.Tools.Contracts.ToolResult.Ok("read",
                    new JObject { ["text"] = "OBSOLETE_BODY" + new string('x', 20000) }.ToString(), new[] { r1 }),
                    resourceEvidence: new[] { evidence }), int.MaxValue, "tool");
            var call = new ChatMessage { Role = "assistant", ProtocolMessage = true, ToolCallId = "call1", ToolName = command.ToolId,
                ToolCalls = new List<RNAssistant.Core.Llm.LlmToolCall> { new RNAssistant.Core.Llm.LlmToolCall { Id = "call1", Name = command.ToolId, Type = "function", ArgumentsJson = "{}" } } };
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Known(r2, 2) }) }),
                "tools1", new SkillCatalogSnapshot(null), new SchemaRegistrySnapshot(null), 2);
            var compiled = new ModelContextCompiler().Compile(authority, new ChatMessage[0], new[] { call, result },
                null, new ToolCatalogEntry[0], new AppSettings(), 1024);
            var text = string.Join("\n", compiled.Messages.Select(item => item.Content));
            AssertTrue(!text.Contains("OBSOLETE_BODY"), "stale payload excluded before tight budget");
            AssertEqual(2, compiled.Messages.Count, "causal call/result pair retained");
            AssertEqual(1, compiled.Receipt.ExcludedSuperseded, "receipt explains exclusion");
            var changed = compiled.Messages;
            changed[0].Content = "mutated";
            AssertTrue(compiled.Messages[0].Content != "mutated", "request projection is detached from frozen snapshot");
        }

        private static void DocumentAuthoritySurvivesSaveAsAndSeparatesCopy()
        {
            WithTempPaths(paths =>
            {
                var registry = new DocumentAuthorityRegistry(paths);
                var unsaved = registry.Resolve("Excel", "runtime1", null);
                var saved = registry.Resolve("Excel", "runtime1", "/book.xlsx");
                var renamed = registry.Resolve("Excel", "runtime1", "/renamed.xlsx");
                AssertEqual(unsaved.Id, saved.Id, "first save keeps identity");
                AssertEqual(saved.Id, renamed.Id, "SaveAs keeps logical identity");
                AssertEqual(renamed.Id, new DocumentAuthorityRegistry(paths).Resolve("Excel", "runtime2", "/renamed.xlsx").Id, "reopen keeps identity");
                AssertTrue(registry.Resolve("Excel", "runtime3", "/book.xlsx").Id != renamed.Id, "old path is not authority alias");
                AssertTrue(registry.Resolve("Excel", "runtime4", "/copy.xlsx").Id != renamed.Id, "copy is a distinct document");
            });
        }
    }
}
