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
                    var described = gateway.Resolve(session, exact.Uri).Resource;
                    AssertTrue(described.Representations.Contains("image") && described.Representations.Contains("thumbnail"),
                        "existing image views are discoverable before capture");
                    var imageCapability = described.ViewCapabilities.Single(item => item.View == "image");
                    AssertEqual((int)ArtifactViewerService.MaximumImageBytes, imageCapability.MaxBatchBytes.Value, "declared image bound");
                    AssertTrue(!imageCapability.SupportsOffset && !imageCapability.SupportsFields && !imageCapability.SupportsStream,
                        "whole binary delivery does not advertise record streaming");
                    AssertEqual(2, gateway.List(session, "chat", null, null, 10).Items.Single().ViewCapabilities.Count,
                        "list and resolve share metadata-only capabilities");
                    AssertEqual("RESOURCE_VIEW_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() =>
                        data.Open(session, "viewer", exact, "render-page", "0")).ErrorCode, "image cannot negotiate a PDF view");
                    AssertEqual("RESOURCE_VIEW_INVALID", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = exact, Representation = "image", Fields = new List<string> { "field" } })).ErrorCode,
                        "unsupported binary selectors refuse before capture");
                    AssertEqual(0, reads, "discovery and failed negotiation read no source bytes");
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

        private static void ResourceBinaryCapabilitiesAndRetainedBounds()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, new ResourceMutationJournal(paths), payloads);
                var source = payloads.StoreBytes(new byte[] { 1, 2, 3 }, "application/pdf");
                var attachment = new ChatAttachment { Id = "binary-source", Kind = "pdf", ContentType = "application/pdf",
                    ContentSha256 = source.Sha256, ContentByteLength = source.ByteLength, PageCount = 2 };
                var message = new ChatMessage { Role = "user", Attachments = new List<ChatAttachment> { attachment } };
                var artifact = new ChatArtifact { Kind = ChatArtifactKinds.Attachment, MimeType = "application/pdf",
                    SourceMessageId = message.Id, ContentSha256 = source.Sha256, ContentByteLength = source.ByteLength,
                    MetadataJson = "{\"attachmentId\":\"binary-source\"}" };
                var session = new ChatSession(); session.Messages.Add(message); session.Artifacts.Add(artifact);
                var reads = 0;
                var gateway = new ResourceGatewayService(null, null, null, authority: authority,
                    readAttachmentBytes: item => { reads++; throw new InvalidOperationException("unexpected hydration"); });
                var exact = ChatResourceUri.CreateArtifactRevision(session, artifact);
                var descriptor = gateway.Resolve(session, exact.Uri).Resource;
                AssertEqual((int)ArtifactPdfViewerService.MaximumPageImageBytes,
                    descriptor.ViewCapabilities.Single(item => item.View == "render-page").MaxBatchBytes.Value, "PDF page bound");
                AssertEqual((int)ArtifactPdfViewerService.MaximumThumbnailImageBytes,
                    descriptor.ViewCapabilities.Single(item => item.View == "page-thumbnail").MaxBatchBytes.Value, "PDF thumbnail bound");
                AssertTrue(!descriptor.Representations.Contains("image"), "PDF original is not an image view");
                AssertEqual(0, new ResourceGatewayService(null, null, null, authority: authority)
                    .Resolve(session, exact.Uri).Resource.ViewCapabilities.Count, "unconfigured binary owner advertises no binary views");
                attachment.ContentType = "text/html";
                AssertEqual(0, gateway.Resolve(session, exact.Uri).Resource.ViewCapabilities.Count, "inconsistent MIME offers no binary view");
                attachment.ContentType = "application/pdf";
                var scope = authority.Scope(session, false);
                store.RegisterRevision(scope, new ResourceRevisionMetadata(exact, source.Sha256));
                foreach (var view in new[] { "render-page", "page-thumbnail" })
                {
                    var retained = new ResourceBinaryView { Payload = view == "render-page"
                        ? new PayloadRef(source.Sha256, source.ByteLength, "text/html")
                        : new PayloadRef(source.Sha256, ArtifactPdfViewerService.MaximumThumbnailImageBytes + 1, "image/jpeg") };
                    var metadata = PayloadRef.FromBlob(payloads.StoreText(Newtonsoft.Json.JsonConvert.SerializeObject(retained), "application/json"));
                    store.RegisterView(scope, new ResourceRevisionView(exact, "binary:" + view + ":0", retained.Payload.Sha256,
                        metadata, ResourceCoverage.Whole(), new[] { retained.Payload }));
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = exact, Representation = view, ViewPath = "0" })).ErrorCode,
                        "retained metadata cannot bypass the negotiated MIME or per-view bound");
                }
                AssertEqual(0, reads, "discovery and rejected retained views do not render or hydrate source bytes");
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

        private static void ResourceCatalogContinuationsPinPublication()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var body = "# Equal publication body\n" + new string('x', 600);
                var details = "# Equal reference body\n" + new string('y', 600);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.catalog_cursor", Host = "Common", Name = "Original name",
                    Description = "Catalog revision cursor test.", BodyMarkdown = body, Enabled = true });
                string error; SkillReferenceMetadata referenceMetadata;
                AssertTrue(store.TrySaveReference(skill, "references/details.md", details, out referenceMetadata, out error), "reference setup: " + error);
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;
                var authority = executor.ResourceAuthority;
                var scope = CatalogPublicationService.ScopeId;
                var revisions = (IResourceRevisionStore)authority.Store;
                var active = executor.CaptureSkills();
                var publishedSkill = active.Skills.Single(item => item.Id == skill.Id);
                var r1 = publishedSkill.Publication;
                var references = new[] { r1, CatalogResourceProvider.SkillResource(publishedSkill),
                    CatalogResourceProvider.SkillResource(publishedSkill, "references/details.md") };
                Func<ResourceRef, string, int, ResourceReadResult> read = (reference, cursor, max) => gateway.Read(session,
                    new ResourceReadRequest { Reference = reference, Representation = "text", Cursor = cursor, MaxChars = max }).Result;
                var first = references.Select(reference => read(reference, null, 8)).ToArray();
                var expected = first.Select(page => page.Text + read(page.Resource.Reference, page.NextCursor, 32000).Text).ToArray();
                AssertTrue(!expected[0].Contains("Equal publication body") && !expected[0].Contains("StoragePath"), "public root projection does not expose skill bodies or authoring paths");
                AssertEqual(body, expected[1], "skill body is an independent exact resource");
                AssertEqual(details, expected[2], "reference is an independent exact resource");
                foreach (var page in first)
                    AssertEqual(r1.Revision, ResourceReadCursor.ParseRevisionBound(page.NextCursor,
                        ResourceReadCursor.ReadBinding(page.Resource.Reference.Uri, "text")).Revision, "all catalog views use publication revision cursors");
                using (var plane = new ResourceDataPlaneService(gateway))
                {
                    var leases = references.Skip(1).Select(reference => plane.Open(session, "catalog-preview", new ResourceRef(reference.Uri), "text")).ToArray();
                    AssertTrue(references.Skip(1).All(reference => authority.Store.GetHead(scope, reference.Identity) == null),
                        "head reads of catalog members use publication dependencies, never synthetic member heads");
                    var changed = executor.ExecuteManual(Command("common.skills_upsert", "id", skill.Id, "host", "Common", "name", "Changed name",
                        "description", skill.Description, "version", "1.0.0", "bodyMarkdown", body, "enabled", true),
                        executor.GetControllerTools().ToList(), new AppSettings(), false, true, session);
                    AssertTrue(changed.Success, "metadata-only skill publication: " + changed.Message);
                    var r2 = executor.CaptureSkills().Skills.Single(item => item.Id == skill.Id).Publication;
                    AssertTrue(r1.Revision != r2.Revision, "changing metadata publishes a new catalog revision even when member bytes are equal");
                    for (var index = 1; index < first.Length; index++)
                    {
                        var nextReference = new ResourceRef(references[index].Uri, r2.Revision);
                        AssertEqual(expected[index], read(nextReference, null, 32000).Text, "unchanged member bytes are reused in the next publication");
                        AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(nextReference, first[index].NextCursor, 8)).ErrorCode,
                            "a member cannot consume another publication's cursor even for equal bytes");
                    }
                    var r3 = new ResourceRef(r1.Uri, "r_catalog_restore");
                    var original = revisions.GetRevision(scope, r1);
                    revisions.RegisterRevision(scope, new ResourceRevisionMetadata(r3, original.ContentSha256, original.Payload, r2, r1));
                    var before = authority.Store.Capture(scope);
                    authority.Store.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                        new[] { new ResourceHeadChange(r1.Identity, before.GetHead(r1.Identity), ResourceHeadState.Known(r3, before.Generation + 1)) }, AuthorityCommitReason.Restore));
                    for (var index = 0; index < first.Length; index++)
                    {
                        var restored = new ResourceRef(references[index].Uri, r3.Revision);
                        AssertEqual(expected[index], read(restored, null, 32000).Text, "restored publication preserves all exact public bytes");
                        AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(restored, first[index].NextCursor, 8)).ErrorCode,
                            "restored equal root/member bytes still have distinct logical cursors");
                        AssertEqual(expected[index].Substring(8), read(references[index], first[index].NextCursor, 32000).Text, "historical continuation remains at its publication");
                    }
                    var frozen = authority.CaptureMany(new[] { scope });
                    foreach (var page in first.Skip(1))
                        AssertEqual("RESOURCE_DEPENDENCY_STALE", RuntimeThrows<ResourceRequestException>(() => gateway.RequireCurrent(session,
                            page.Resource, "text", frozen)).ErrorCode, "member currentness is rejected by the shared publication dependency reducer");
                    for (var index = 0; index < leases.Length; index++)
                    {
                        var page = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(leases[index].LeaseId, 0, 8, System.Threading.CancellationToken.None)));
                        var tail = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(leases[index].LeaseId, 8, 32000, System.Threading.CancellationToken.None)));
                        AssertEqual(expected[index + 1], (string)page["text"] + (string)tail["text"], "open member leases survive publication changes as exact snapshots");
                        plane.Close(session.Id, "catalog-preview", leases[index].LeaseId);
                    }
                    var hashCursor = ResourceReadCursor.CreateRevisionBound(8, first[0].ContentSha256, ResourceReadCursor.ReadBinding(r1.Uri, "text"));
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r3, hashCursor, 8)).ErrorCode, "old hash-bound catalog tokens are rejected");
                    AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(r1.Uri), first[0].NextCursor, 8)).ErrorCode, "continuation cannot float through Current");
                    RuntimeThrows<ResourceRequestException>(() => read(references[1], first[2].NextCursor, 8));
                    var generation = authority.Store.Capture(scope).Generation;
                    read(references[1], null, 32000);
                    AssertEqual(r3.Revision, executor.CaptureSkills().Skills.Single(item => item.Id == skill.Id).Publication.Revision, "reading historical skill resources cannot reactivate an old generation");
                    AssertEqual(generation, authority.Store.Capture(scope).Generation, "historical reads do not publish authority changes");
                    authority.ReportExternalDrift(scope, r1.Identity);
                    AssertEqual("RESOURCE_HEAD_UNKNOWN", RuntimeThrows<ResourceRequestException>(() => plane.Open(session, "catalog-preview",
                        new ResourceRef(references[1].Uri), "text")).ErrorCode, "member head reads do not heal an unknown publication");
                    AssertEqual(body, read(references[1], null, 32000).Text, "committed historical member remains readable under unknown current authority");
                    AssertEqual(HeadKnowledge.Unknown, authority.Store.GetHead(scope, r1.Identity).Knowledge, "historical catalog reads leave unknown authority unchanged");
                }
            });
        }

        private static void ResourceCatalogSnapshotsFailClosed()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "common.missing_catalog", Host = "Common", Name = "Missing catalog",
                    Description = "Missing catalog payload test.", BodyMarkdown = "# Canonical body", Enabled = true });
                string error; SkillReferenceMetadata reference;
                AssertTrue(store.TrySaveReference(skill, "references/details.md", "# Unique reference payload", out reference, out error), "reference setup: " + error);
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store, new ToolStore(paths));
                var session = NewSession(adapter);
                var active = executor.CaptureCatalogs();
                var scope = CatalogPublicationService.ScopeId;
                var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                var root = active.Authority.GetHead(new ResourceIdentity("rna://catalog/prompts")).Revision;
                var metadata = revisions.GetRevision(scope, root);
                var prepared = new ResourceRef(root.Uri, "r_unpublished_catalog");
                revisions.RegisterRevision(scope, new ResourceRevisionMetadata(prepared, metadata.ContentSha256, metadata.Payload, root));
                Func<ResourceRef, ResourceReadSelection> read = exact => executor.ResourceGateway.Read(session,
                    new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 128 });
                var generation = executor.ResourceAuthority.Store.Capture(scope).Generation;
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(prepared)).ErrorCode,
                    "prepared catalog metadata cannot be exposed as a committed publication");
                AssertTrue(revisions.GetView(scope, prepared, "text") == null, "failed publication check cannot retain a public text view");
                System.IO.File.Delete(executor.Payloads.PathFor(metadata.Payload.Sha256));
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(root)).ErrorCode,
                    "missing prompt CAS cannot become an empty successful catalog body");
                AssertTrue(revisions.GetView(scope, root, "text") == null, "missing root payload is not replaced by an invented empty view");
                var publishedSkill = active.Skills.Skills.Single(item => item.Id == skill.Id);
                System.IO.File.WriteAllText(executor.Payloads.PathFor(publishedSkill.References.Single().Payload.Sha256), "CORRUPT CAS");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(
                    CatalogResourceProvider.SkillResource(publishedSkill, "references/details.md"))).ErrorCode, "corrupt reference CAS has a typed unavailable result");
                AssertEqual(generation, executor.ResourceAuthority.Store.Capture(scope).Generation, "missing/corrupt catalog reads cannot advance authority");
                AssertEqual(root.Revision, executor.ResourceAuthority.Store.GetHead(scope, root.Identity).Revision.Revision, "failures never replace a catalog head");
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
                var library = executor.GetPromptLibrary();
                executor.SaveSettingsControls(settings, new RNAssistant.Office.Contracts.SaveSettingsPayload {
                    Settings = RNAssistant.Office.Contracts.SettingsControlsDto.From(secondSettings), ExpectedPromptPublication = library.Publication },
                    new RNAssistant.Office.Contracts.PromptMutationBatch { Type = RNAssistant.Office.Contracts.PromptMutationBatch.ContractType, ContractVersion = 1,
                        Changes = new[] { new RNAssistant.Office.Contracts.PromptFieldChange {
                            Resource = library.Items.Single(item => item.Key == "systemPrompt").Resource, Value = secondSettings.SystemPrompt } } },
                    value => settings = value);
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
                var revisionStore = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                var publicationGeneration = executor.ResourceAuthority.Store.Capture(scope).Generation;
                foreach (var reference in new[] { schema, mapping, derived, materialized })
                {
                    var metadata = revisionStore.GetRevision(scope, reference);
                    var prepared = new ResourceRef(reference.Uri, "r_prepared_definition");
                    revisionStore.RegisterRevision(scope, new ResourceRevisionMetadata(prepared, metadata.ContentSha256, metadata.Payload,
                        reference, dependencies: metadata.Dependencies));
                    var index = revisionStore.GetView(scope, reference, "record-index-v1:$");
                    if (index != null) revisionStore.RegisterView(scope, new ResourceRevisionView(prepared, index.View,
                        index.ContentSha256, index.Payload, index.Coverage, index.Parts));
                    foreach (var view in new[] { "text", "table", "records" })
                        AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                            new ResourceReadRequest { Reference = prepared, Representation = view, MaxRows = 1 })).ErrorCode,
                            "prepared definition cannot borrow the visibility of an older committed head or a retained index");
                }
                AssertEqual(publicationGeneration, executor.ResourceAuthority.Store.Capture(scope).Generation,
                    "rejected definition reads do not publish or heal heads");
                foreach (var reference in new[] { derived, materialized })
                {
                    var page = gateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "table",
                        Fields = new List<string> { "sales" }, MaxRows = 1 }).Result;
                    var tail = gateway.Read(session, new ResourceReadRequest { Reference = reference, Representation = "table",
                        Fields = new List<string> { "sales" }, ViewPath = "$", Cursor = page.NextCursor, MaxRows = 1 }).Result;
                    AssertEqual(14L, Convert.ToInt64(tail.Table.Rows.Single()["sales"]), "virtual and materialized continuations preserve the exact projection");
                    AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = new ResourceRef(reference.Uri), Representation = "table",
                            Fields = new List<string> { "sales" }, Cursor = page.NextCursor })).ErrorCode, "derived continuations cannot float to a current head");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = new ResourceRef(reference.Uri, reference.Revision.ToUpperInvariant()), Representation = "table",
                            Fields = new List<string> { "sales" }, Cursor = page.NextCursor })).ErrorCode, "logical revision comparison is case-sensitive before metadata lookup");
                    AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                        new ResourceReadRequest { Reference = reference, Representation = "table", Fields = new List<string> { "Sales" },
                            Cursor = page.NextCursor })).ErrorCode, "derived projection is checked before field lookup or source reads");
                }
                AssertEqual("RESOURCE_VIEW_UNSUPPORTED", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = derived, Representation = "table", ViewPath = "$.ignored" })).ErrorCode,
                    "virtual views do not silently ignore an unsupported record path");
                using (var currentData = new ResourceDataPlaneService(gateway))
                {
                    var current = currentData.Open(session, "preview", new ResourceRef(derived.Identity.Uri), "table");
                    currentData.Close(session.Id, "preview", current.LeaseId);
                }
                publish(ResourceDefinitionToolHandler.Mapping, new JObject { ["name"] = "sales", ["schema"] = target(schema), ["source"] = source,
                    ["mapping"] = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "backup" }) });
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(evidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State,
                    "mapping publication invalidates derived currentness without changing its historical body");
                AssertEqual(12L, Convert.ToInt64(gateway.Read(session, new ResourceReadRequest { Reference = derived, Representation = "table", MaxRows = 1 }).Result.Table.Rows[0]["sales"]),
                    "historical exact derived view does not silently follow a new mapping");
                using (var staleData = new ResourceDataPlaneService(gateway))
                {
                    foreach (var reference in new[] { derived, materialized })
                    {
                        var stale = RuntimeThrows<ResourceRequestException>(() => staleData.Open(session, "preview", new ResourceRef(reference.Identity.Uri), "table"));
                        AssertEqual("RESOURCE_DEPENDENCY_STALE", stale.ErrorCode, "data-plane head reads use canonical dependency currentness");
                        var exact = staleData.Open(session, "preview", reference, "table");
                        staleData.Close(session.Id, "preview", exact.LeaseId);
                    }
                }
                AssertEqual(1, frozen.Schemas.Count, "captured schema registry stays immutable");

                var note = new ContextNote { Role = ContextNoteRole.SuppliedData, Text = "FORKED_DATA", Title = "Draft" };
                executor.ResourceAuthority.ObserveNote(session, note, executor.Payloads);
                session.Context.Notes.Add(note);
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "virtual", Binding = new HtmlWorkspaceDataBinding {
                    Resource = derived, Mapping = mapping, Schema = schema, View = "table", Policy = "exact" } });
                session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "saved", Binding = new HtmlWorkspaceDataBinding {
                    Resource = materialized, View = "table", Policy = "exact" } });
                HtmlWorkspaceArtifactService.CaptureCurrent(session, "Fork source");
                var originalSnapshotId = session.ActiveHtmlArtifactId;
                var originalSnapshotBody = session.Artifacts.Single(item => item.Id == originalSnapshotId).InlineText;
                using (var exportData = new ResourceDataPlaneService(gateway))
                {
                    session.HtmlWorkspace.DataSources[0].Binding.Policy = "head";
                    var exporter = new HtmlWorkspaceExportService(gateway, exportData);
                    var rejected = RuntimeThrows<ResourceRequestException>(() => exporter.Open(session, originalSnapshotId, System.Threading.CancellationToken.None));
                    AssertEqual("RESOURCE_DEPENDENCY_STALE", rejected.ErrorCode, "export cannot label a derived snapshot with stale dependencies as current");
                    session.HtmlWorkspace.DataSources[0].Binding.Policy = "exact";
                    foreach (var binding in exporter.Open(session, originalSnapshotId, System.Threading.CancellationToken.None).Bindings)
                        exportData.Close(session.Id, originalSnapshotId, binding.Lease.LeaseId);
                }
                var chats = new ChatStore(FixturePaths.Value);
                chats.Save(session);
                var child = NewSession(adapter);
                child.ParentSessionId = session.Id; child.DocumentAuthorityId = session.DocumentAuthorityId;
                child.Context = ChatCloneService.CloneContext(session.Context);
                var copyPlan = ChatCloneService.PrepareForkResources(session, child, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                var childScope = executor.ResourceAuthority.Scope(child, false);
                AssertEqual(0L, executor.ResourceAuthority.Store.Capture(childScope).Generation, "preparation cannot publish copied heads");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(child,
                    new ResourceReadRequest { Reference = child.Context.Notes[0].Evidence.Resource, Representation = "text" })).ErrorCode,
                    "prepared context copies cannot become readable/published through a read");
                AssertEqual(0L, executor.ResourceAuthority.Store.Capture(childScope).Generation, "rejected read leaves fork preparation unpublished");
                var childDerived = child.HtmlWorkspace.DataSources.Single(item => item.Name == "virtual").Binding.Resource;
                foreach (var view in new[] { "text", "table", "records" })
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(child,
                        new ResourceReadRequest { Reference = childDerived, Representation = view, MaxRows = 1 })).ErrorCode,
                        "copy provenance alone cannot authorize a prepared fork before its commit");
                AssertTrue(childDerived.Uri.Contains(child.Id) && childDerived.Revision != derived.Revision, "fork has a new exact resource identity/revision");
                var copiedSnapshot = child.Artifacts.Single(item => item.Id == originalSnapshotId);
                AssertEqual(originalSnapshotBody, copiedSnapshot.InlineText, "old snapshot body is not rewritten during copy");
                executor.MutateChatResources(child, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, source: session, fork: copyPlan), () => {
                    chats.Save(child); return child;
                });
                AssertEqual(1L, executor.ResourceAuthority.Store.Capture(childScope).Generation, "all copied definitions and workspace publish in one commit");
                RuntimeThrows<InvalidOperationException>(() => executor.MutateChatResources(child,
                    new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, source: session, fork: copyPlan), () => child));
                AssertEqual(1L, executor.ResourceAuthority.Store.Capture(childScope).Generation, "an existing child cannot be republished as a new fork");
                AssertEqual(12L, Convert.ToInt64(gateway.Read(child, new ResourceReadRequest { Reference = childDerived, Representation = "table", MaxRows = 1 })
                    .Result.Table.Rows[0]["sales"]), "copied derived view uses its exact old mapping, not the parent's newer mapping");
                var childSaved = child.HtmlWorkspace.DataSources.Single(item => item.Name == "saved").Binding.Resource;
                var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                AssertEqual(revisions.GetRevision(scope, materialized).Payload.Sha256, revisions.GetRevision(childScope, childSaved).Payload.Sha256,
                    "materialized data keeps the same immutable CAS body");
                RuntimeThrows<ResourceRequestException>(() => gateway.Read(child, new ResourceReadRequest { Reference = derived, Representation = "table" }));
                var replayed = chats.Load(child.Id);
                AssertEqual(child.ResourceCopies.Count, replayed.ResourceCopies.Count, "exact copy provenance replays from conversation events");
                chats.LoadArtifactBody(replayed, originalSnapshotId);
                AssertTrue(HtmlWorkspaceArtifactService.Restore(replayed, originalSnapshotId), "old copied workspace can be restored through exact copy links");
                AssertEqual(childDerived.Revision, replayed.HtmlWorkspace.DataSources.Single(item => item.Name == "virtual").Binding.Resource.Revision,
                    "restore reuses the deliberately copied revision, never the parent head");
                AssertEqual(childScope, replayed.Context.Notes[0].Evidence.ScopeId, "supplied context is copied into the child's evidence scope");
                AssertEqual("FORKED_DATA", gateway.Read(replayed, new ResourceReadRequest {
                    Reference = replayed.Context.Notes[0].Evidence.Resource, Representation = "text" }).Result.Text,
                    "replayed context copies use the same gateway after atomic fork publication");
                AssertEqual(originalSnapshotBody, session.Artifacts.Single(item => item.Id == originalSnapshotId).InlineText,
                    "fork never mutates the parent snapshot");

                var childRuntime = executor.CreateNativeRuntime(child,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), new AppSettings(), "agent", false);
                var copiedMapping = Newtonsoft.Json.JsonConvert.DeserializeObject<ResourceMappingDefinition>(
                    executor.Payloads.ReadText(revisions.GetRevision(childScope,
                        child.HtmlWorkspace.DataSources.Single(item => item.Name == "virtual").Binding.Mapping).Payload.ToBlobReference()));
                var republished = ExecuteHtmlNative(childRuntime, ResourceDefinitionToolHandler.Mapping, new JObject {
                    ["name"] = "sales", ["schema"] = ResourceGatewayService.IntentTarget(gateway.Resolve(child, copiedMapping.Schema.Uri).Resource),
                    ["source"] = ResourceGatewayService.IntentTarget(gateway.Resolve(child, copiedMapping.Source.Uri).Resource),
                    ["mapping"] = new JArray(new JObject { ["field"] = "sales", ["sourceField"] = "backup" }) });
                AssertEqual(ToolExecutionOutcome.Ok, republished.Outcome, "child mapping publication succeeds independently");
                var childNewMapping = republished.Result.Resources.Single(item => item.Uri.StartsWith("rna://state/", StringComparison.Ordinal));
                child.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "latest-mapping", Binding = new HtmlWorkspaceDataBinding {
                    Resource = childNewMapping, View = "text", Policy = "exact" } });
                HtmlWorkspaceArtifactService.CaptureCurrent(child, "Independent child mapping");
                chats.Save(child);
                var grandchild = NewSession(adapter);
                grandchild.ParentSessionId = child.Id; grandchild.DocumentAuthorityId = child.DocumentAuthorityId;
                grandchild.Context = ChatCloneService.CloneContext(child.Context);
                var nextPlan = ChatCloneService.PrepareForkResources(child, grandchild, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                executor.MutateChatResources(grandchild, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, source: child, fork: nextPlan), () => {
                    chats.Save(grandchild); return grandchild;
                });
                var grandScope = executor.ResourceAuthority.Scope(grandchild, false);
                AssertEqual(2, grandchild.ResourceCopies.Select(item => item.Copy).Where(item => item.Uri.EndsWith("/mapping-sales", StringComparison.Ordinal))
                    .Select(item => item.Revision).Distinct().Count(), "nested fork retains both exact historical mappings without merging equal identities");
                AssertEqual(grandchild.HtmlWorkspace.DataSources.Single(item => item.Name == "latest-mapping").Binding.Resource.Revision,
                    executor.ResourceAuthority.Store.GetHead(grandScope, ResourceStateProvider.Identity(grandScope, "mapping-sales")).Revision.Revision,
                    "publication order, not hash or recursive traversal order, chooses the copied head");
                AssertEqual(12L, Convert.ToInt64(gateway.Read(grandchild, new ResourceReadRequest {
                    Reference = grandchild.HtmlWorkspace.DataSources.Single(item => item.Name == "virtual").Binding.Resource,
                    Representation = "table", MaxRows = 1 }).Result.Table.Rows[0]["sales"]), "nested fork still reads the pinned older mapping");

                var failed = NewSession(adapter); failed.ParentSessionId = child.Id; failed.DocumentAuthorityId = child.DocumentAuthorityId;
                var failedPlan = ChatCloneService.PrepareForkResources(child, failed, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                RuntimeThrows<InvalidOperationException>(() => executor.MutateChatResources<int>(failed,
                    new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, source: child, fork: failedPlan),
                    () => { throw new InvalidOperationException("fork persistence failed"); }));
                var failedState = executor.ResourceAuthority.Store.Capture(executor.ResourceAuthority.Scope(failed, false));
                AssertTrue(failedPlan.Heads.All(item => failedState.GetHead(item.Identity).Knowledge == HeadKnowledge.Unknown),
                    "fork failure cannot leave a partially activated schema/mapping graph");
                System.IO.File.Delete(executor.Payloads.PathFor(revisions.GetRevision(scope, derived).Payload.Sha256));
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = derived, Representation = "table", Cursor = read.NextCursor,
                        Fields = new List<string> { "Sales" } })).ErrorCode, "virtual continuation guards run before definition CAS hydration");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = derived, Representation = "table", Cursor = read.NextCursor })).ErrorCode,
                    "missing committed virtual definition has a typed unavailable result, not a newer replacement");
                System.IO.File.WriteAllText(executor.Payloads.PathFor(revisions.GetRevision(scope, derived).Payload.Sha256), "corrupt definition");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = derived, Representation = "table" })).ErrorCode,
                    "corrupt committed virtual definition fails through the same retained payload reader");
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

        private static void HtmlExportDataPlaneCapture()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                executor.MutateLocalResources(session, "common.html_workspace_write_file", null,
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<p>export</p>", true));
                var target = executor.ResourceGateway.ResolveIntentTarget(session, "Excel range: Data!A1:B4");
                var binding = new HtmlWorkspaceDataSource { Id = "cells", Name = "cells", Binding =
                    new HtmlWorkspaceDataBinding { Resource = new ResourceRef(target.Reference.Identity.Uri), Policy = "head", View = "table" } };
                executor.MutateLocalResources(session, "common.html_workspace_bind_data", null, () => {
                    session.HtmlWorkspace.DataSources.Add(binding);
                    return HtmlWorkspaceArtifactService.CaptureCurrent(session, "Export binding");
                });
                var workspaceId = session.ActiveHtmlArtifactId;
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                {
                    var export = new HtmlWorkspaceExportService(executor.ResourceGateway, data);
                    var prepared = export.Open(session, workspaceId, System.Threading.CancellationToken.None);
                    var lease = prepared.Bindings.Single().Lease;
                    AssertTrue(lease.Descriptor.Reference.IsExact, "head export resolves one exact revision");
                    AssertEqual("head", binding.Binding.Policy, "export does not rewrite live binding policy");
                    AssertEqual(workspaceId, session.ActiveHtmlArtifactId, "export capabilities are not workspace revisions");
                    AssertTrue(prepared.Generations.Count == 1, "export records the frozen authority stamp");
                    var before = lease.Descriptor.Reference.Copy();
                    adapter.SetExcelCellForTest("Data", "B2", 999);
                    var fresh = executor.ResourceGateway.Read(session, new ResourceReadRequest {
                        Reference = binding.Binding.Resource, Representation = "table", MaxRows = 4 });
                    AssertTrue(before.Revision != fresh.Result.Resource.Reference.Revision, "source head can advance after export preparation");
                    var first = JObject.Parse(System.Text.Encoding.UTF8.GetString(data.Read(lease.LeaseId, 0, 2, System.Threading.CancellationToken.None)));
                    var next = JObject.Parse(System.Text.Encoding.UTF8.GetString(data.Read(lease.LeaseId, 2, 2, System.Threading.CancellationToken.None)));
                    AssertEqual(before.Revision, (string)next["resource"]["revision"], "export stream keeps its exact pre-change revision");
                    AssertEqual("120", (string)first["rows"][1]["c2"], "export retains exact pre-change B2, independently of random resource IDs");
                    data.Close(session.Id, workspaceId, lease.LeaseId);
                    binding.Binding.Policy = "exact"; binding.Binding.Resource = before;
                    var historical = export.Open(session, workspaceId, System.Threading.CancellationToken.None);
                    AssertEqual(before.Revision, historical.Bindings.Single().Lease.Descriptor.Reference.Revision,
                        "explicit historical export never borrows a newer head");
                    data.Close(session.Id, workspaceId, historical.Bindings.Single().Lease.LeaseId);
                    // Failure after opening the first binding must not exhaust the shared lease pool.
                    session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "missing", Name = "missing", Binding =
                        new HtmlWorkspaceDataBinding { Resource = new ResourceRef(before.Uri, "missing-revision"), Policy = "exact", View = "table" } });
                    for (var index = 0; index < 65; index++)
                    {
                        var error = RuntimeThrows<ResourceRequestException>(() => export.Open(session, workspaceId, System.Threading.CancellationToken.None));
                        AssertTrue(error.ErrorCode != "RESOURCE_LEASE_LIMIT", "failed export releases earlier capabilities");
                    }
                    session.HtmlWorkspace.DataSources.RemoveAt(1);
                    RuntimeThrows<OperationCanceledException>(() => export.Open(session, workspaceId, new System.Threading.CancellationToken(true)));
                    var final = export.Open(session, workspaceId, System.Threading.CancellationToken.None);
                    data.Close(session.Id, workspaceId, final.Bindings.Single().Lease.LeaseId);
                    RuntimeThrows<ResourceRequestException>(() => export.Open(session, "wrong-workspace", System.Threading.CancellationToken.None));
                    binding.Binding.Policy = "head"; binding.Binding.Resource = new ResourceRef(before.Identity.Uri);
                    session.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Id = "pinned", Name = "pinned", Binding =
                        new HtmlWorkspaceDataBinding { Resource = before, Policy = "exact", View = "table" } });
                    var ownerChecks = 0;
                    using (var racingData = new ResourceDataPlaneService(executor.ResourceGateway, (_, __) => {
                        if (++ownerChecks == 4) executor.ResourceAuthority.ReportExternalDrift(executor.ResourceAuthority.Scope(session, true), before.Identity);
                        return true;
                    }))
                    {
                        var raced = RuntimeThrows<ResourceRequestException>(() =>
                            new HtmlWorkspaceExportService(executor.ResourceGateway, racingData).Open(session, workspaceId, System.Threading.CancellationToken.None));
                        AssertEqual("RESOURCE_EFFECT_UNKNOWN", raced.ErrorCode, "unknown head capture never yields an export manifest");
                    }
                }
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
                    var fresh = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = target.Reference, Representation = "table", MaxRows = 1 });
                    AssertTrue(fresh.Result.Resource.Reference.Revision != (string)first["resource"]["revision"], "new head observes external drift");
                    var backendCalls = adapter.ExcelBackendCalls.Count;
                    AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.Read(session,
                        new ResourceReadRequest { Reference = target.Reference, Representation = "table", Cursor = fresh.Result.NextCursor })).ErrorCode,
                        "floating structural continuation is rejected before live capture");
                    AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.Read(session,
                        new ResourceReadRequest { Reference = new ResourceRef(target.Reference.Uri, "r_unseen"), Representation = "table",
                            Cursor = fresh.Result.NextCursor })).ErrorCode, "cross-revision structural continuation is rejected before live capture");
                    AssertEqual(backendCalls, adapter.ExcelBackendCalls.Count, "invalid table continuations never dispatch Office reads");
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

        private static void ResourceStructuredContinuationsPinProjection()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession();
                var scope = authority.Scope(session, false);
                var gateway = new ResourceGatewayService(null, null, null, authority: authority);
                var identity = ResourceStateProvider.Identity(scope, "projection-test");
                var r1 = new ResourceRef(identity.Uri, "r_projection");
                var r2 = new ResourceRef(identity.Uri, "r_projection_restore");
                var body = "{\"Rows\":[{\"Amount\":1,\"amount\":11},{\"Amount\":2,\"amount\":22}],\"rows\":[{\"Amount\":3,\"amount\":33},{\"Amount\":4,\"amount\":44}]}";
                var payload = PayloadRef.FromBlob(payloads.StoreText(body, "application/json"));
                Action<ResourceRef, ResourceRef> publish = (reference, previous) => {
                    store.RegisterRevision(scope, new ResourceRevisionMetadata(reference, payload.Sha256, payload, previous, previous));
                    var snapshot = store.Capture(scope);
                    store.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                        new[] { new ResourceHeadChange(identity, snapshot.GetHead(identity), ResourceHeadState.Known(reference, snapshot.Generation + 1)) },
                        previous == null ? AuthorityCommitReason.InitialObservation : AuthorityCommitReason.Restore));
                };
                Func<ResourceRef, string, string, string[], ResourceReadResult> read = (reference, cursor, path, fields) => gateway.Read(session,
                    new ResourceReadRequest { Reference = reference, Representation = "records", ViewPath = path,
                        Fields = fields?.ToList(), Cursor = cursor, MaxRows = 1 }).Result;
                publish(r1, null);
                var first = read(new ResourceRef(identity.Uri), null, "$.Rows", new[] { "Amount" });
                AssertEqual(1L, Convert.ToInt64(first.Table.Rows.Single()["Amount"]), "first page resolves the requested case-sensitive path and field");
                AssertEqual(2L, Convert.ToInt64(read(r1, first.NextCursor, "$.Rows", new[] { "Amount" }).Table.Rows.Single()["Amount"]), "exact page keeps its projection");
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => read(r1, first.NextCursor, "$.rows", new[] { "Amount" })).ErrorCode,
                    "JSON path case cannot change under the same cursor");
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => read(r1, first.NextCursor, "$.Rows", new[] { "amount" })).ErrorCode,
                    "valid fields differing only by case cannot exchange cursors");
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => gateway.Read(session,
                    new ResourceReadRequest { Reference = r1, Representation = "table", ViewPath = "$.Rows", Fields = new List<string> { "Amount" },
                        Cursor = first.NextCursor })).ErrorCode, "table and records tokens stay view-bound");
                AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(r1.Uri), first.NextCursor, "$.Rows", new[] { "Amount" })).ErrorCode,
                    "a structural continuation requires an explicit exact ref");
                publish(r2, r1);
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r2, first.NextCursor, "$.Rows", new[] { "Amount" })).ErrorCode,
                    "equal-byte restore is not the original cursor revision");
                var restored = read(r2, null, "$.Rows", new[] { "Amount" });
                AssertEqual(2L, Convert.ToInt64(read(r2, restored.NextCursor, "$.Rows", new[] { "Amount" }).Table.Rows.Single()["Amount"]), "restore has its own valid continuation");
                AssertEqual(2L, Convert.ToInt64(read(r1, first.NextCursor, "$.Rows", new[] { "Amount" }).Table.Rows.Single()["Amount"]), "historical continuation survives a restore");
                var all = read(r1, null, "$.Rows", null);
                AssertEqual(2, read(r1, all.NextCursor, "$.Rows", new string[0]).Table.Rows.Single().Count, "null and empty field selections mean the same all-fields projection");
                var artifact = new ChatArtifact { Kind = ChatArtifactKinds.File, Title = "projection.json", MimeType = "application/json", InlineText = body };
                session.Artifacts.Add(artifact);
                var artifactRef = ChatResourceUri.CreateArtifactRevision(session, artifact);
                var artifactFirst = read(new ResourceRef(artifactRef.Identity.Uri), null, "$.Rows", null);
                AssertEqual(2, read(artifactFirst.Resource.Reference, artifactFirst.NextCursor, "$.Rows", null).Table.Rows.Single().Count,
                    "a first artifact read rebinds outgoing continuation to its resolved exact URI");
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(artifactRef.Identity.Uri),
                    artifactFirst.NextCursor, "$.Rows", null)).ErrorCode, "artifact identity resolution cannot silently supply a continuation's missing revision");
                var generation = store.Capture(scope).Generation;
                System.IO.File.Delete(payloads.PathFor(store.GetView(scope, r1, "record-index-v1:$.Rows").Payload.Sha256));
                AssertEqual("resource_cursor_invalid", RuntimeThrows<ResourceRequestException>(() => read(r1, first.NextCursor, "$.Rows", new[] { "amount" })).ErrorCode,
                    "projection guards run before structural index CAS hydration");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(r1, first.NextCursor, "$.Rows", new[] { "Amount" })).ErrorCode,
                    "valid continuation with missing exact index fails explicitly");
                AssertEqual(generation, store.Capture(scope).Generation, "continuations and failures cannot publish heads");
            });
        }

        private static void ResourceRetainedPayloadsFailClosed()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession();
                var scope = authority.Scope(session, false);
                var gateway = new ResourceGatewayService(null, null, null, authority: authority);
                var exact = new ResourceRef(ResourceStateProvider.Identity(scope, "retained-payload-test").Uri, "r_retained_payload");
                var payload = PayloadRef.FromBlob(payloads.StoreText("[{\"value\":1},{\"value\":2}]", "application/json"));
                store.RegisterRevision(scope, new ResourceRevisionMetadata(exact, payload.Sha256, payload));
                store.Publish(ResourceAuthorityCommit.Create(scope, 0, null,
                    new[] { new ResourceHeadChange(exact.Identity, null, ResourceHeadState.Known(exact, 1)) }, AuthorityCommitReason.InitialObservation));
                Func<string, ResourceReadResult> read = view => gateway.Read(session,
                    new ResourceReadRequest { Reference = exact, Representation = view, MaxRows = 1 }).Result;
                AssertEqual(1L, Convert.ToInt64(read("table").Table.Rows.Single()["value"]), "committed table index is retained");
                var index = store.GetView(scope, exact, "record-index-v1:$");
                System.IO.File.WriteAllText(payloads.PathFor(index.Parts.Single().Sha256), "corrupt record part");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read("table")).ErrorCode,
                    "corrupt indexed part is a typed unavailable snapshot, never re-materialized from source");
                System.IO.File.WriteAllText(payloads.PathFor(index.Payload.Sha256), "corrupt index");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read("records")).ErrorCode,
                    "corrupt index is a typed unavailable snapshot");
                System.IO.File.WriteAllText(payloads.PathFor(payload.Sha256), "corrupt source");
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read("text")).ErrorCode,
                    "retained source uses the same typed CAS failure behavior");
                AssertEqual(1L, store.Capture(scope).Generation, "payload failures never change publication authority");
                AssertEqual(exact.Revision, store.GetHead(scope, exact.Identity).Revision.Revision, "payload failures do not invent a replacement head");
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
                var preparedFork = ChatCloneService.PrepareForkResources(session, fork, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertTrue(fork.HtmlWorkspace.DataSources.Single().Binding.Resource.Uri != session.HtmlWorkspace.DataSources.Single().Binding.Resource.Uri,
                    "copied artifact bindings are explicitly rebound to child resources");
                AssertTrue(fork.Artifacts.Any(item => item.Id == session.ActiveHtmlArtifactId &&
                    item.InlineText == session.Artifacts.Single(source => source.Id == session.ActiveHtmlArtifactId).InlineText),
                    "rebinding never rewrites the immutable copied snapshot body");
                expectedGenerationDuringSave = 0;
                executor.MutateChatResources(fork, new ChatResourceMutationIntent(ChatResourceMutationKind.Fork, target.Id, source: session, fork: preparedFork), () => fork);
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
            WithTempPaths(paths =>
            {
            var store = new ResourceAuthorityStore(paths);
            var payloads = new ChatBlobStore(paths);
            var copies = new ResourceForkService(new ResourceAuthorityService(store, store, payloads: payloads), payloads);
            var adapter = FakeOfficeAdapter.ForHost("Word");
            var source = NewSession(adapter);
            HtmlWorkspaceToolService.UpsertFile(source, "index.html", "html", "<p>old</p>", true);
            var checkpoint = ChatResourceUri.ResolveArtifactRevision(source, source.ActiveHtmlArtifactId);
            source.Messages.Add(new ChatMessage { Role = "user", Content = "Checkpoint", HtmlWorkspaceCheckpoint = checkpoint });
            HtmlWorkspaceToolService.UpsertFile(source, "index.html", "html", "<p>new</p>", true);
            var missing = source.Artifacts.Single(item => item.Id == ResourceUri.Parse(checkpoint.Uri).Segments[2]);
            var oldBody = missing.InlineText;
            missing.InlineText = null;
            var fork = NewSession(adapter);
            fork.ParentSessionId = source.Id;
            fork.Messages = ChatCloneService.CloneMessages(source.Messages);
            RuntimeThrows<InvalidOperationException>(() => ChatCloneService.PrepareForkResources(source, fork, (_, id) => false, copies));
            AssertTrue(!fork.HtmlWorkspace.Files.Any(item => item.Content == "<p>new</p>"),
                "unavailable old checkpoint never silently falls back to the parent's newer workspace");

            source.Messages.Clear();
            missing.InlineText = oldBody;
            source.HtmlWorkspace.DataSources.Add(new HtmlWorkspaceDataSource { Name = "bound", Binding = new HtmlWorkspaceDataBinding {
                Resource = new ResourceRef("rna://document/shared/root", "r1"), View = "text", Policy = "exact",
                Schema = new ResourceRef(ResourceStateProvider.Identity(new ResourceAuthorityScopeId("conversation", source.Id), "schema-published-demo").Uri, "r1") } });
            HtmlWorkspaceArtifactService.CaptureCurrent(source, "Bound definition");
            var boundFork = NewSession(adapter); boundFork.ParentSessionId = source.Id;
            RuntimeThrows<InvalidOperationException>(() => ChatCloneService.PrepareForkResources(source, boundFork, null, copies));
            AssertEqual(0, boundFork.ResourceCopies.Count, "an unavailable definition cannot manufacture copy provenance");
            AssertEqual(0L, store.Capture(new ResourceAuthorityScopeId("conversation", boundFork.Id)).Generation,
                "unavailable definition copy cannot activate a partial child graph");
            });
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

        private static void ResourceLiveContinuationsUseLogicalRevisions()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var body = "Option Explicit\n' " + new string('a', 700);
                adapter.SetVbaModule("CursorModule", body, "StdModule");
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var authority = executor.ResourceAuthority;
                var scope = authority.Scope(session, true);
                var revisions = (IResourceRevisionStore)authority.Store;
                var identity = gateway.ResolveIntentTarget(session, "VBA module: CursorModule").Reference.Identity;
                Func<ResourceRef, string, int, ResourceReadResult> read = (reference, cursor, maximum) => gateway.Read(session,
                    new ResourceReadRequest { Reference = reference, Representation = "source", Cursor = cursor, MaxChars = maximum }).Result;
                var first = read(new ResourceRef(identity.Uri), null, 128);
                var r1 = first.Resource.Reference;
                var binding = ResourceReadCursor.ReadBinding(identity.Uri, "source");
                AssertEqual(r1.Revision, ResourceReadCursor.ParseRevisionBound(first.NextCursor, binding).Revision,
                    "Gateway continuations expose the logical revision, not the private provider hash");
                AssertTrue(r1.Revision != first.ContentSha256, "logical lineage is separate from physical bytes");
                AssertEqual(ResourceCoverageKinds.CharacterRange, first.Coverage.Kind,
                    "the delivered bounded page remains partial evidence");
                AssertEqual(ResourceCoverageKinds.Whole, revisions.GetView(scope, r1, "source").Coverage.Kind,
                    "the full VBA source already captured by the provider is retained once");
                AssertEqual(body.Substring(128, 128), read(r1, first.NextCursor, 128).Text,
                    "exact continuation reads the retained snapshot using a logical cursor");
                adapter.SetVbaModule("CursorModule", body.Replace('a', 'b'), "StdModule");
                var r2 = read(new ResourceRef(identity.Uri), null, 128).Resource.Reference;
                adapter.SetVbaModule("CursorModule", body, "StdModule");
                // Isolate the cursor boundary using the same durable restore metadata
                // and authority publication unit as mutation read-back.
                var payload = PayloadRef.FromBlob(executor.Payloads.StoreText(body, first.Resource.MimeType));
                var r3 = new ResourceRef(identity.Uri, "r_restored_cursor");
                revisions.RegisterRevision(scope, new ResourceRevisionMetadata(r3, first.ContentSha256, payload, r2, r1));
                var before = authority.Store.Capture(scope);
                authority.Store.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(identity, before.GetHead(identity), ResourceHeadState.Known(r3, before.Generation + 1)) },
                    AuthorityCommitReason.MutationEffect));
                var calls = adapter.TotalBackendCallCount;
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r3, first.NextCursor, 128)).ErrorCode,
                    "a same-byte restore cannot borrow a cursor from its origin even before a full view is retained");
                AssertEqual(calls, adapter.TotalBackendCallCount, "logical cursor mismatch fails before Office dispatch");
                var restored = read(r3, null, 128);
                AssertEqual(first.ContentSha256, restored.ContentSha256, "restore deduplicates equal physical bytes");
                AssertEqual(body.Substring(128, 128), read(r3, restored.NextCursor, 128).Text, "restored revision has its own exact continuation");
                AssertEqual(body, read(r3, null, 1000).Text, "whole exact read retains the restored view");
                calls = adapter.TotalBackendCallCount;
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r3, first.NextCursor, 128)).ErrorCode,
                    "retained snapshot selection applies the same logical cursor rule");
                var legacyHashCursor = ResourceReadCursor.CreateRevisionBound(128, first.ContentSha256, binding);
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r3, legacyHashCursor, 128)).ErrorCode,
                    "old hash-bound cursors have no compatibility path");
                AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(identity.Uri), restored.NextCursor, 128)).ErrorCode,
                    "a moving head cannot consume an exact continuation");
                RuntimeThrows<ResourceRequestException>(() => gateway.Read(session, new ResourceReadRequest {
                    Reference = r3, Representation = "structure", Cursor = restored.NextCursor, MaxChars = 128 }));
                RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(ResourceUri.Create("vba", "another", "module"), r3.Revision), restored.NextCursor, 128));
                AssertEqual(body.Substring(128, 128), read(r1, first.NextCursor, 128).Text,
                    "historical continuation reads the original retained revision");
                adapter.SetVbaModule("CursorModule", "CHANGED_OUTSIDE_RESOURCE_READ", "StdModule");
                AssertEqual(body, read(r3, null, 1000).Text, "exact reads use an available snapshot even when it is the last known head");
                AssertEqual(calls, adapter.TotalBackendCallCount, "retained reads and cursor rejection never query Office");
                using (var plane = new ResourceDataPlaneService(gateway))
                {
                    var lease = plane.Open(session, "vba-snapshot", r3, "source");
                    var page = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(lease.LeaseId, 0, 128, System.Threading.CancellationToken.None)));
                    authority.ReportExternalDrift(scope, identity);
                    var tail = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(lease.LeaseId, 128, 1000, System.Threading.CancellationToken.None)));
                    AssertEqual(body, (string)page["text"] + (string)tail["text"], "existing data-plane leases remain exact after the authority becomes unknown");
                    plane.Close(session.Id, "vba-snapshot", lease.LeaseId);
                }
                var generation = authority.Store.Capture(scope).Generation;
                var historical = read(r1, first.NextCursor, 128);
                AssertEqual(EvidenceState.Unknown, new EvidenceStateReducer().Reduce(gateway.Evidence(session, historical).Single(),
                    authority.CaptureMany(new[] { scope })).State, "snapshot access never reconciles unknown evidence currentness");
                AssertEqual(generation, authority.Store.Capture(scope).Generation, "historical reads never publish a head");
                var missing = new ResourceRef(identity.Uri, "r_missing_cursor_view");
                var missingPayload = new PayloadRef(new string('f', 64), body.Length, "text/plain");
                revisions.RegisterRevision(scope, new ResourceRevisionMetadata(missing, missingPayload.Sha256, missingPayload));
                revisions.RegisterView(scope, new ResourceRevisionView(missing, "source", missingPayload.Sha256, missingPayload, ResourceCoverage.Whole()));
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(missing, null, 128)).ErrorCode,
                    "missing retained CAS fails explicitly without falling back to current Office bytes");
                AssertEqual(calls, adapter.TotalBackendCallCount, "retained failures also stay outside Office");
                var unboundGateway = new ResourceGatewayService(adapter, null, null);
                AssertEqual("RESOURCE_AUTHORITY_NOT_READY", RuntimeThrows<ResourceRequestException>(() => unboundGateway.List(session,
                    "document", null, null, 20)).ErrorCode, "Gateway cannot expose raw physical live revisions without shared authority");
            });
        }

        private static void ResourceContextGatewayUsesScopedSnapshots()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession { DocumentAuthorityId = "context-document" };
                var liveReads = 0;
                var gateway = new ResourceGatewayService(null, null, null, authority: authority,
                    beginLiveOfficeRead: chat => { liveReads++; throw new InvalidOperationException("Context snapshots cannot call Office."); });
                var dataBody = "CANONICAL_DATA " + new string('x', 1000);
                var data = new ContextNote { Role = ContextNoteRole.SuppliedData, Title = "Input draft", Text = dataBody };
                var office = new ContextNote { Role = ContextNoteRole.OfficeObservation, Title = "Captured cells", Text = "EXACT_OFFICE_CELLS" };
                authority.ObserveNote(session, data, payloads);
                authority.ObserveNote(session, office, payloads);
                session.Context.Notes.AddRange(new[] { data, office,
                    new ContextNote { Role = ContextNoteRole.UserInstruction, Title = "Hidden instruction", Evidence = data.Evidence },
                    new ContextNote { Title = "Untyped note", Evidence = data.Evidence } });
                data.Text = data.Preview = "FORGED_DISPLAY";
                office.Text = office.Preview = "FORGED_DISPLAY";
                var conversation = authority.Scope(session, false);
                var document = authority.Scope(session, true);
                var listed = gateway.List(session, "context", null, null, 20);
                AssertEqual(2, listed.Items.Count, "only typed supplied data and Office observations are discoverable resources");
                var target = gateway.Find(session, "Input draft", "conversation").Items.Single();
                AssertEqual("context data: Input draft", target.Target, "context uses ordinary semantic resource targets");
                AssertEqual(data.Evidence.Resource.Revision, gateway.ResolveIntentTarget(session, target.Target).Reference.Revision,
                    "model target resolution pins the canonical revision");
                AssertEqual("Office observation: Captured cells", gateway.Find(session, "Captured cells", "document").Items.Single().Target,
                    "Office context is discovered in its document scope, not the conversation scope");
                AssertEqual(0, gateway.Find(session, "Captured cells", "conversation").Items.Count, "scope filtering cannot borrow document context");
                AssertEqual(0, gateway.Search(session, "context", "FORGED_DISPLAY", null, 20, 200).Matches.Count,
                    "display previews are not a searchable alternate body");
                var page = gateway.Read(session, new ResourceReadRequest { Reference = data.Evidence.Resource, Representation = "text", MaxChars = 8 }).Result;
                var rest = gateway.Read(session, new ResourceReadRequest { Reference = page.Resource.Reference, Representation = "text", Cursor = page.NextCursor }).Result;
                AssertEqual(dataBody, page.Text + rest.Text, "bounded context reads hydrate full exact CAS rather than display or retained fragments");
                AssertEqual(ResourceCoverageKinds.CharacterRange, page.Coverage.Kind, "partial reads keep partial evidence coverage");
                var observed = gateway.Read(session, new ResourceReadRequest { Reference = office.Evidence.Resource, Representation = "text" }).Result;
                var evidence = gateway.Evidence(session, observed).Single();
                AssertEqual(document, evidence.ScopeId, "snapshot reads preserve document authority without being live COM reads");
                AssertTrue(!evidence.Immutable, "Office observations still track document head currentness");
                AssertEqual(EvidenceState.Current, new EvidenceStateReducer().Reduce(evidence, authority.CaptureMany(new[] { document })).State,
                    "shared reducer recognizes the current Office observation");
                var other = new ChatSession { DocumentAuthorityId = session.DocumentAuthorityId };
                AssertEqual("EXACT_OFFICE_CELLS", gateway.Read(other, new ResourceReadRequest {
                    Reference = office.Evidence.Resource, Representation = "text" }).Result.Text, "explicit document refs stay shared across chats on that document");
                AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() => gateway.Read(other,
                    new ResourceReadRequest { Reference = data.Evidence.Resource, Representation = "text" })).ErrorCode, "conversation data never aliases another chat");
                other.DocumentAuthorityId = "another-document";
                AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() => gateway.Read(other,
                    new ResourceReadRequest { Reference = office.Evidence.Resource, Representation = "text" })).ErrorCode, "document refs require the exact bound document authority");
                using (var plane = new ResourceDataPlaneService(gateway))
                {
                    var opened = plane.Open(session, "context-viewer", new ResourceRef(office.Evidence.Resource.Uri), "text");
                    authority.ReportExternalDrift(document, office.Evidence.Resource.Identity);
                    var batch = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(opened.LeaseId, 0, 7, System.Threading.CancellationToken.None)));
                    var tail = JObject.Parse(System.Text.Encoding.UTF8.GetString(plane.Read(opened.LeaseId, 7, 100, System.Threading.CancellationToken.None)));
                    AssertEqual("EXACT_OFFICE_CELLS", (string)batch["text"] + (string)tail["text"], "an open data-plane handle remains pinned after document drift");
                    plane.Close(session.Id, "context-viewer", opened.LeaseId);
                    AssertEqual("RESOURCE_HEAD_UNKNOWN", RuntimeThrows<ResourceRequestException>(() => plane.Open(session, "context-viewer",
                        new ResourceRef(office.Evidence.Resource.Uri), "text")).ErrorCode, "new head reads cannot claim an unknown observation as current");
                }
                var generation = store.Capture(document).Generation;
                AssertEqual("EXACT_OFFICE_CELLS", gateway.Read(session, new ResourceReadRequest {
                    Reference = office.Evidence.Resource, Representation = "text" }).Result.Text, "explicit historical observations remain readable after drift");
                AssertEqual(generation, store.Capture(document).Generation, "historical reads never reconcile or republish a head");
                AssertEqual(EvidenceState.Unknown, new EvidenceStateReducer().Reduce(evidence, authority.CaptureMany(new[] { document })).State,
                    "the same reducer still excludes unknown Office evidence after historical reads");
                AssertEqual(1L, store.Capture(conversation).Generation, "all data reads leave publication authority untouched");
                AssertEqual(0, liveReads, "retained context operations never enter a live Office read");
            });
        }

        private static void ResourceRetainedStateReadsPreservePublication()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession();
                var scope = authority.Scope(session, false);
                var gateway = new ResourceGatewayService(null, null, null, authority: authority);
                var identity = ResourceStateProvider.Identity(scope, "state-read-test");
                var r1 = new ResourceRef(identity.Uri, "r1");
                var body = "CANONICAL_STATE_BODY";
                var payload = PayloadRef.FromBlob(payloads.StoreText(body, "text/plain"));
                store.RegisterRevision(scope, new ResourceRevisionMetadata(r1, payload.Sha256, payload));
                Func<ResourceRef, string, int, ResourceReadResult> read = (reference, cursor, max) => gateway.Read(session,
                    new ResourceReadRequest { Reference = reference, Representation = "text", Cursor = cursor, MaxChars = max }).Result;
                Action<ResourceRef> publish = reference => {
                    var snapshot = store.Capture(scope);
                    store.Publish(ResourceAuthorityCommit.Create(scope, snapshot.Generation, null,
                        new[] { new ResourceHeadChange(identity, snapshot.GetHead(identity), ResourceHeadState.Known(reference, snapshot.Generation + 1)) },
                        AuthorityCommitReason.InitialObservation));
                };
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() => read(r1, null, 4)).ErrorCode,
                    "retained metadata alone cannot activate an unpublished state identity");
                AssertEqual(0L, store.Capture(scope).Generation, "unpublished read is side-effect free");
                publish(r1);
                var fragment = PayloadRef.FromBlob(payloads.StoreText(body.Substring(0, 4), "text/plain"));
                store.RegisterView(scope, new ResourceRevisionView(r1, "text", payload.Sha256, fragment,
                    new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 4)));
                var first = read(new ResourceRef(identity.Uri), null, 4);
                AssertEqual(body.Length, first.TotalCharacters, "a retained fragment cannot replace the canonical whole revision body");
                AssertEqual(body, first.Text + read(r1, first.NextCursor, 32000).Text, "continuation reads beyond an already retained partial view");
                AssertEqual(payload.Sha256, store.GetView(scope, r1, "text").Payload.Sha256, "whole-view retention uses canonical CAS, never concatenated fragments");
                var r2 = new ResourceRef(identity.Uri, "r2");
                var changed = PayloadRef.FromBlob(payloads.StoreText("CHANGED_STATE", "text/plain"));
                store.RegisterRevision(scope, new ResourceRevisionMetadata(r2, changed.Sha256, changed, r1));
                publish(r2);
                var r3 = new ResourceRef(identity.Uri, "r3");
                store.RegisterRevision(scope, new ResourceRevisionMetadata(r3, payload.Sha256, payload, r2, r1));
                publish(r3);
                AssertEqual("RESOURCE_REVISION_CHANGED", RuntimeThrows<ResourceRequestException>(() => read(r3, first.NextCursor, 100)).ErrorCode,
                    "equal restored bytes cannot reuse another logical revision's cursor");
                AssertEqual("RESOURCE_CURSOR_INVALID", RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(identity.Uri), first.NextCursor, 100)).ErrorCode,
                    "continuations require an explicit exact revision, never a moving head");
                AssertEqual(body.Substring(4), read(r1, first.NextCursor, 100).Text, "historical continuation retains its original logical revision");
                var observed = gateway.Evidence(session, read(r1, null, 100)).Single();
                AssertTrue(!observed.Immutable, "materialized state is head-tracked even when its bytes are retained");
                AssertEqual(EvidenceState.Superseded, new EvidenceStateReducer().Reduce(observed, authority.CaptureMany(new[] { scope })).State,
                    "same-hash restore does not make old logical evidence current");
                var before = store.Capture(scope);
                store.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(identity, before.GetHead(identity), ResourceHeadState.Unavailable(identity, before.Generation + 1, "removed")) },
                    AuthorityCommitReason.InitialObservation));
                var removedGeneration = store.Capture(scope).Generation;
                AssertEqual(body, read(r1, null, 100).Text, "removed state keeps explicit retained historical access");
                RuntimeThrows<ResourceRequestException>(() => read(new ResourceRef(identity.Uri), null, 100));
                AssertEqual(HeadKnowledge.Unavailable, store.GetHead(scope, identity).Knowledge, "reads cannot resurrect removed state");
                AssertEqual(removedGeneration, store.Capture(scope).Generation, "reads cannot advance a tombstone's authority generation");
            });
        }

        private static void ResourceCompilerUsesTypedContextPayloads()
        {
            WithTempPaths(paths =>
            {
                var payloads = new ChatBlobStore(paths);
                var store = new ResourceAuthorityStore(paths);
                var authority = new ResourceAuthorityService(store, store, payloads: payloads);
                var session = new ChatSession { Host = "Excel", DocumentKey = "doc", DocumentAuthorityId = "doc-authority" };
                var officeBody = "OFFICE_CANONICAL " + new string('x', 600);
                var office = new ContextNote { Role = ContextNoteRole.OfficeObservation, Title = "Cells", Kind = "selection", Text = officeBody };
                authority.ObserveNote(session, office, payloads);
                AssertTrue(office.Text.Length < 400, "persisted/UI note text is only a bounded preview");
                var data = new ContextNote { Role = ContextNoteRole.SuppliedData, Title = "Draft skill", Kind = "skill_definition", Text = "DRAFT_DATA_CANONICAL" };
                authority.ObserveNote(session, data, payloads);
                var instruction = new ContextNote { Role = ContextNoteRole.UserInstruction, Title = "Preferences", Kind = "selection",
                    Text = "FORGED_UI_INSTRUCTION", InstructionPayload = PayloadRef.FromBlob(payloads.StoreText("USER_CANONICAL", "text/plain")) };
                var untyped = new ContextNote { Kind = "instruction", Text = "UNTYPED_BODY", InstructionPayload = instruction.InstructionPayload };
                office.Text = "FORGED_UI_OBSERVATION";
                data.Text = "FORGED_UI_DATA";
                var scopes = new[] { authority.Scope(session, true), authority.Scope(session, false) };
                Func<ModelAuthoritySnapshot> capture = () => new ModelAuthoritySnapshot(authority.CaptureMany(scopes),
                    "tools", new SkillCatalogSnapshot(null), null, 0);
                var frozen = capture();
                session.Context = new DocumentContext { Notes = new List<ContextNote> { office, instruction, data, untyped } };
                var chats = new ChatStore(paths);
                chats.Save(session);
                var reloaded = chats.Load(session.Id).Context.Notes;
                AssertEqual(ContextNoteRole.UserInstruction, reloaded[1].Role, "event replay preserves explicit instruction role");
                AssertEqual(instruction.InstructionPayload.Sha256, reloaded[1].InstructionPayload.Sha256, "event replay retains exact instruction payload");
                AssertEqual(office.Evidence.Resource.Revision, reloaded[0].Evidence.Resource.Revision, "event replay retains exact observation revision");
                var compiler = new ModelContextCompiler(payloads);
                Func<ModelAuthoritySnapshot, ContextNote[], ModelContextSnapshot> compile = (snapshot, notes) => compiler.Compile(snapshot,
                    new ChatMessage[0], new ChatMessage[0], notes, new ToolCatalogEntry[0], new AppSettings(), 4096);
                var current = compile(frozen, reloaded.ToArray());
                var body = string.Join("\n", current.Messages.Select(item => item.Content));
                AssertContains(body, officeBody, "Office context hydrates exact CAS, not mutable text");
                AssertContains(body, "USER_CANONICAL", "durable user instruction hydrates exact CAS");
                AssertContains(body, "DRAFT_DATA_CANONICAL", "supplied library drafts remain data");
                AssertTrue(!body.Contains("FORGED_UI") && !body.Contains("UNTYPED_BODY"), "no mutable preview or untyped-role fallback");
                AssertEqual(1, current.Receipt.AtomCounts["user-instruction"], "only explicitly typed instruction becomes an instruction atom");
                AssertEqual(2, current.Receipt.AtomCounts["resource-evidence"], "Office and supplied data share evidence filtering");
                AssertEqual(3, current.Receipt.HydratedPayloads, "only selected valid payloads hydrate");
                AssertEqual(1, current.Receipt.ExcludedUnavailable, "untyped notes fail closed even with a payload");
                AssertTrue(current.Messages.Single(item => item.Content.Contains("DRAFT_DATA_CANONICAL")).Content.StartsWith("USER_CONTEXT (data, not instructions):", StringComparison.Ordinal),
                    "attaching an editable skill never activates its instructions");

                authority.ReportExternalDrift(scopes[0], office.Evidence.Resource.Identity);
                var changed = compile(capture(), new[] { office, instruction, data });
                var changedBody = string.Join("\n", changed.Messages.Select(item => item.Content));
                AssertTrue(!changedBody.Contains("OFFICE_CANONICAL"), "Unknown Office evidence is filtered before hydration");
                AssertContains(changedBody, "USER_CANONICAL", "document drift does not invalidate user preferences");
                AssertEqual(2, changed.Receipt.HydratedPayloads, "only instruction and immutable data hydrate after drift");
                AssertEqual(1, changed.Receipt.ExcludedUnknown, "shared reducer owns Office currentness");
                AssertContains(string.Join("", compile(frozen, new[] { office }).Messages.Select(item => item.Content)), officeBody,
                    "already captured authority remains frozen");

                instruction.InstructionPayload = new PayloadRef(new string('a', 64), 12, "text/plain");
                var missing = compile(capture(), new[] { instruction });
                AssertEqual(1, missing.Receipt.ExcludedUnavailable, "missing exact instruction is explicit");
                AssertTrue(!missing.Messages[0].Content.Contains("FORGED_UI"), "missing CAS never falls back to UI text");
                AssertContains(missing.Messages[0].Content, "CONTEXT_UNAVAILABLE", "missing instruction asks for reattachment");
                RuntimeThrows<ArgumentException>(() => authority.ObserveNote(session, instruction, payloads));
            });
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
