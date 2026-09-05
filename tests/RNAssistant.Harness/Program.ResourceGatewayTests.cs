using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using ToolResultStatus = RNAssistant.Core.Tools.Contracts.ToolResultStatus;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void NativeResourceToolsUseRuntimeForManualAndModelCalls()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Chat;
                session.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "First", InlineText = "body" });
                var tools = executor.GetControllerTools().ToArray();
                AssertResourceControllerProjection(tools.Single(item => item.Id == ResourceToolCatalog.FindToolId),
                    ResourceFindToolHandler.Descriptor, ResourceFindToolHandler.Policy, "resources_find");
                AssertResourceControllerProjection(tools.Single(item => item.Id == ResourceToolCatalog.ReadToolId),
                    ResourceReadToolHandler.Descriptor, ResourceReadToolHandler.Policy, "resources_read");
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), ChatModes.Chat, false);
                Func<string, string, ToolExecutionRecord> execute = (id, arguments) =>
                {
                    var call = new ToolCall("native_" + id, id, arguments);
                    var policy = runtime.Describe(call);
                    AssertTrue(policy != null && policy.IndependentLocalRead && !policy.MayHaveSideEffects,
                        id + " has source-owned read policy");
                    return runtime.ExecuteAsync(new ToolExecutionContext(call, policy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                };

                var runtimeContext = JObject.Parse(
                    ConversationPromptComposer.BuildRuntimeContext(
                        ChatModes.Chat,
                        adapter,
                        tools,
                        BuiltInSkillProvider.GetSkills(adapter),
                        NewContext(adapter),
                        session,
                        new AppSettings()));
                var contextProjectTarget =
                    (string)runtimeContext.SelectToken("document.vba_project_target");
                AssertEqual(
                    VbaResourceProvider.ProjectSemanticTarget(adapter.DocumentTitle),
                    contextProjectTarget,
                    "bound VBA project target is directly available in runtime context");
                var directProjectRead = execute(
                    ResourceToolCatalog.ReadToolId,
                    JsonConvert.SerializeObject(new
                    {
                        target = contextProjectTarget,
                        representation = ResourceRepresentations.Structure
                    }));
                AssertEqual(ToolExecutionOutcome.Ok, directProjectRead.Outcome,
                    "runtime-context VBA project target is readable without a preceding find call");
                AssertEqual(1, ((JArray)JObject.Parse(
                    (string)JObject.Parse(directProjectRead.Result.DataJson)["text"])["components"]).Count,
                    "direct project read returns the exact bound component inventory");
                var directProjectStructure = JObject.Parse(
                    (string)JObject.Parse(directProjectRead.Result.DataJson)["text"]);
                AssertTrue(directProjectStructure.ToString(Formatting.None)
                        .IndexOf("rna://", StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA project structure never exposes runtime resource URIs");
                AssertEqual("VBA module: Module1",
                    (string)directProjectStructure.SelectToken(
                        "components[0].target"),
                    "VBA project structure exposes the readable module target");
                var directModuleRead = execute(
                    ResourceToolCatalog.ReadToolId,
                    JsonConvert.SerializeObject(new
                    {
                        target = (string)directProjectStructure.SelectToken(
                            "components[0].target"),
                        representation = ResourceRepresentations.Source
                    }));
                AssertEqual(ToolExecutionOutcome.Ok, directModuleRead.Outcome,
                    "semantic module target remains readable immediately after structure read");

                var found = execute(ResourceToolCatalog.FindToolId, "{\"scope\":\"conversation\"}");
                AssertEqual(ToolExecutionOutcome.Ok, found.Outcome, "semantic resource find succeeds in chat mode");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched, found.Evidence.Dispatch, "provider invocation is recorded");
                AssertEqual(ToolEffectEvidence.None, found.Evidence.Effect, "read success does not manufacture verified effect");
                var findData = JObject.Parse(found.Result.DataJson);
                var candidate = ((JArray)findData["items"]).OfType<JObject>().Single(item =>
                    string.Equals((string)item["title"], "First", StringComparison.Ordinal));
                var resourceTarget = (string)candidate["target"];
                AssertTrue(!string.IsNullOrWhiteSpace(resourceTarget), "find returns a readable semantic target");
                AssertTrue(candidate["reference"] == null && candidate["provider"] == null && candidate["kind"] == null,
                    "find data does not expose provider routing or exact reference plumbing");
                var resourceUri = found.Result.Resources.Single(reference =>
                    reference.Uri.StartsWith("rna://", StringComparison.Ordinal)).Uri;

                var opaqueTarget = execute(
                    ResourceToolCatalog.ReadToolId,
                    JsonConvert.SerializeObject(new
                    {
                        target = resourceUri,
                        representation = ResourceRepresentations.Text
                    }));
                AssertEqual(ToolExecutionOutcome.Error, opaqueTarget.Outcome,
                    "runtime-owned URI is rejected as a semantic read target");
                AssertEqual("resource_target_runtime_owned",
                    (string)JObject.Parse(opaqueTarget.Result.DataJson)["code"],
                    "opaque target has a stable recovery code");
                AssertTrue(opaqueTarget.Message.IndexOf(resourceUri,
                        StringComparison.OrdinalIgnoreCase) < 0,
                    "opaque target error does not echo the URI into model context");
                string historyError;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("opaque_history",
                            ResourceToolCatalog.ReadToolId,
                            JsonConvert.SerializeObject(new
                            {
                                target = resourceUri,
                                representation = ResourceRepresentations.Text
                            })),
                        out historyError),
                    "stored resource URI target requires explicit chat reset");

                var calls = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResourceToolCatalog.FindToolId] = "{\"query\":\"body\",\"scope\":\"conversation\"}",
                    [ResourceToolCatalog.ReadToolId] = JsonConvert.SerializeObject(new
                    {
                        target = resourceTarget,
                        representation = ResourceRepresentations.Text
                    })
                };
                foreach (var item in calls)
                {
                    var record = execute(item.Key, item.Value);
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, item.Key + " uses its native handler");
                    AssertTrue(record.Result.Resources.Any(reference => reference.Uri == resourceUri),
                        item.Key + " exposes each returned exact resource at Tool Result root");
                    var command = new ToolInvocation
                    {
                        ToolId = item.Key,
                        Arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(item.Value)
                    };
                    var manual = executor.ExecuteManual(command, tools, new AppSettings(), false, true, session);
                    AssertTrue(manual.Success, item.Key + " manual path uses the same native handler");
                    AssertEqual(record.Result.DataJson, manual.DataJson,
                        item.Key + " manual and kernel paths share one implementation");
                    AssertTrue((manual.ModelResourceRefs ?? new ResourceRef[0])
                            .Any(reference => reference.Uri == resourceUri),
                        item.Key + " manual projection retains the same root resource reference");
                    AssertTrue(runtime.Describe(new ToolCall("wrong_case", item.Key.ToUpperInvariant(), "{}")) == null,
                        item.Key + " has no case alias");

                    var invalid = new ToolCall("invalid_" + item.Key, item.Key, "{\"unknown\":true}");
                    var policy = runtime.Describe(invalid);
                    var rejected = runtime.ExecuteAsync(new ToolExecutionContext(invalid, policy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome, item.Key + " rejects invalid arguments");
                    AssertEqual(ToolDispatchEvidence.NotDispatched, rejected.Evidence.Dispatch,
                        item.Key + " validates schema before provider access");
                }
                var readRecord = execute(ResourceToolCatalog.ReadToolId, calls[ResourceToolCatalog.ReadToolId]);
                AssertTrue(readRecord.Result.Resources.Any(reference => reference.Uri == resourceUri),
                    "native resource read retains the exact ResourceRef in typed result data");
                var readData = JObject.Parse(readRecord.Result.DataJson);
                AssertEqual(resourceTarget, (string)readData["target"], "read preserves the semantic target");
                AssertEqual("body", (string)readData["text"], "read returns complete content without caller page size");
                AssertTrue(readData["nextCursor"] == null && readData["resource"] == null,
                    "read data hides cursor and exact resource plumbing");
                HtmlWorkspaceToolService.UpsertFile(
                    session,
                    "nested/report.html",
                    "html",
                    "<main>Resolved through native tool</main>",
                    true);
                var htmlFind = execute(ResourceToolCatalog.FindToolId,
                    "{\"query\":\"nested/report.html\",\"scope\":\"html\"}");
                AssertTrue(((string)JObject.Parse(htmlFind.Result.DataJson)
                        .SelectToken("items[0].target")).StartsWith(
                            "HTML file: nested/report.html [created ",
                            StringComparison.Ordinal),
                    "path resolution is internal to semantic HTML discovery");
                foreach (var retired in new[]
                {
                    "common.resources_list",
                    "common.resources_resolve",
                    "common.resources_search"
                })
                {
                    AssertTrue(tools.All(item => !string.Equals(item.Id, retired, StringComparison.Ordinal)),
                        retired + " is removed from the catalog");
                    AssertTrue(runtime.Describe(new ToolCall("retired", retired, "{}")) == null,
                        retired + " has no runtime alias");
                }
                var invalidManual = executor.ExecuteManual(Command(ResourceToolCatalog.FindToolId, "provider", "chat"), tools,
                    new AppSettings(), false, true, session);
                AssertTrue(!invalidManual.Success && invalidManual.ErrorCode == "invalid_arguments",
                    "manual command rejects retired provider routing");

                var hostRuntime = new HostRuntime(adapter, FixturePaths.Value);
                var target = new OfficeDocumentExecutionExpectation
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey
                };
                var backendCalls = adapter.TotalBackendCallCount;
                var liveFindArguments = "{\"scope\":\"vba\"}";
                using (hostRuntime.BeginDocumentAccess(target))
                {
                    var blockedCall = new ToolCall("blocked_live_find", ResourceToolCatalog.FindToolId,
                        liveFindArguments);
                    var blockedPolicy = runtime.Describe(blockedCall);
                    var blocked = runtime.ExecuteAsync(new ToolExecutionContext(blockedCall, blockedPolicy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Error, blocked.Outcome,
                        "semantic find cannot borrow document access held on the same thread");
                    AssertEqual("tool_mutation_busy", (string)JObject.Parse(blocked.Result.DataJson)["code"],
                        "native live find reports the occupied document gate");
                    AssertEqual(backendCalls, adapter.TotalBackendCallCount, "blocked native live find never reaches Office backend");
                }

                for (var moduleIndex = 1; moduleIndex <= 80; moduleIndex++)
                {
                    adapter.SetVbaModule(
                        "InventoryModule" + moduleIndex.ToString("D3"),
                        "Option Explicit\n' inventory " + moduleIndex,
                        "StdModule");
                }
                var releasedCall = new ToolCall("released_live_find", ResourceToolCatalog.FindToolId,
                    liveFindArguments);
                var releasedPolicy = runtime.Describe(releasedCall);
                var released = runtime.ExecuteAsync(new ToolExecutionContext(releasedCall, releasedPolicy, "run", "turn", "step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, released.Outcome, "native live find succeeds after document access release");
                AssertTrue(adapter.TotalBackendCallCount > backendCalls, "released native live find reaches the Office backend");
                var vbaBrowse = JObject.Parse(released.Result.DataJson);
                AssertTrue((int)vbaBrowse["total"] > 20 && !(bool)vbaBrowse["complete"],
                    "large VBA browse reports that the visible candidates are not the complete inventory");
                var projectCandidate = ((JArray)vbaBrowse["items"]).OfType<JObject>().First();
                AssertEqual("VBA project", (string)projectCandidate["type"],
                    "unfiltered VBA browse keeps the project inventory target visible before modules");
                var projectTarget = (string)projectCandidate["target"];
                var projectRead = execute(ResourceToolCatalog.ReadToolId,
                    JsonConvert.SerializeObject(new
                    {
                        target = projectTarget,
                        representation = ResourceRepresentations.Structure
                    }));
                AssertEqual(ToolExecutionOutcome.Ok, projectRead.Outcome,
                    "large VBA project structure is read through one public call: " + projectRead.Result.Message + " " + projectRead.Result.DataJson);
                var projectReadData = JObject.Parse(projectRead.Result.DataJson);
                AssertEqual(true, (bool)projectReadData["complete"],
                    "large project structure is complete");
                AssertTrue(projectReadData["hasMore"] == null &&
                    projectReadData["progressCharacters"] == null,
                    "whole project read exposes no model continuation state");
                var projectManifest = JObject.Parse((string)projectReadData["text"]);
                AssertEqual(81, ((JArray)projectManifest["components"]).Count,
                    "project structure contains every live VBA module, not only the visible find page");
                AssertTrue(projectManifest.ToString(Formatting.None)
                        .IndexOf("rna://", StringComparison.OrdinalIgnoreCase) < 0,
                    "large project structure contains semantic targets only");

                var retiredNext = execute(ResourceToolCatalog.ReadToolId,
                    JsonConvert.SerializeObject(new
                    {
                        target = projectTarget,
                        action = "next"
                    }));
                AssertEqual(ToolExecutionOutcome.Error, retiredNext.Outcome,
                    "model-owned resource continuation action is rejected by the whole-read schema");
                AssertEqual(ToolDispatchEvidence.NotDispatched, retiredNext.Evidence.Dispatch,
                    "retired continuation never reaches a provider");

                var contextBody = "EXACT_CONTEXT " + new string('x', 40000);
                var note = new ContextNote { Role = ContextNoteRole.OfficeObservation, Title = "Large observation", Text = contextBody };
                executor.ResourceAuthority.ObserveNote(session, note, executor.Payloads);
                session.Context.Notes.Add(note);
                note.Text = note.Preview = "FORGED_DISPLAY";
                var contextRead = execute(ResourceToolCatalog.ReadToolId,
                    "{\"target\":\"Office observation: Large observation\",\"representation\":\"text\"}");
                AssertEqual(ToolExecutionOutcome.Ok, contextRead.Outcome, "semantic context read pins the first bounded page for subsequent reads");
                AssertEqual(contextBody, (string)JObject.Parse(contextRead.Result.DataJson)["text"], "model-facing context uses the same whole exact CAS view");
                AssertEqual(note.Evidence.Resource.Revision, contextRead.Result.Resources.Single().Revision,
                    "complete model-facing context keeps the original logical revision");
            });
        }

        private static void AssertResourceControllerProjection(
            ToolCatalogEntry definition,
            ToolDescriptor descriptor,
            ToolPolicy policy,
            string name)
        {
            AssertEqual(descriptor.Id, definition.Id, name + " id");
            AssertEqual(descriptor.Description, definition.Description, name + " description");
            AssertEqual(descriptor.ParametersJson, definition.ArgumentSchemaJson, name + " schema");
            AssertEqual("Common", definition.Host, name + " host");
            AssertEqual(name, definition.Name, name + " name");
            AssertEqual("session", definition.Scope, name + " scope");
            AssertTrue(definition.BuiltIn && definition.Enabled && definition.AgentCanRun,
                name + " controller availability");
            AssertTrue(!definition.MutatesDocument && !definition.MutatesLocalState,
                name + " controller read flags");
            AssertTrue(ReferenceEquals(policy, definition.Policy),
                name + " preserves source-owned policy instance");
        }

        private static void ResourceGatewayReadsSearchesAndPages()
        {
            var attachment = new ChatAttachment
            {
                Id = "attachment-text",
                Kind = "text",
                FileName = "notes.txt",
                ContentType = "text/plain",
                ExtractedText = new string('a', 180) + " NEEDLE " + new string('b', 180),
                ExtractedCharCount = 368,
                ExtractedTextSha256 = TextPatternEngine.Sha256(new string('a', 180) + " NEEDLE " + new string('b', 180)),
                ContentSha256 = new string('1', 64),
                ContentByteLength = 368
            };
            var message = new ChatMessage
            {
                Id = "source-message",
                Role = "user",
                Content = "Use notes",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var artifact = new ChatArtifact
            {
                Id = "attachment_attachment-text",
                Kind = ChatArtifactKinds.Attachment,
                Title = "notes.txt",
                MimeType = "text/plain",
                SourceMessageId = message.Id,
                ContentSha256 = attachment.ContentSha256,
                ContentByteLength = attachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"attachment-text\"}"
            };
            var session = new ChatSession
            {
                Messages = new List<ChatMessage> { message },
                Artifacts = new List<ChatArtifact> { artifact }
            };
            message.ResourceRefs.Add(ArtifactReference(session, artifact));
            var gateway = new ResourceGatewayService();
            var resourceUri = ArtifactUri(session, artifact);

            var listed = gateway.List(session, null, null, null, 10);
            AssertEqual(1, listed.Items.Count, "resource list count");
            AssertEqual(resourceUri, listed.Items[0].Reference.Uri, "resource list uses canonical URI");
            AssertTrue(listed.Items[0].Representations.Contains("text"),
                "artifact list advertises extracted text");

            var pagingSession = new ChatSession();
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "First", InlineText = "1" });
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "Second", InlineText = "2" });
            var pagingGateway = new ResourceGatewayService();
            var firstListPage = pagingGateway.List(
                pagingSession, ChatArtifactResourceProvider.ProviderName, ChatArtifactKinds.Markdown, null, 1);
            AssertTrue(!string.IsNullOrWhiteSpace(firstListPage.NextCursor),
                "resource list exposes an opaque continuation");
            AssertTrue(firstListPage.Cursor.StartsWith("r2:", StringComparison.Ordinal),
                "resource list keeps an internal revision-bound cursor");
            var firstListJson = JsonConvert.SerializeObject(firstListPage);
            AssertTrue(firstListJson.IndexOf("\"cursor\"", StringComparison.Ordinal) < 0,
                "resource list does not expose the current-page cursor");
            AssertContains(firstListJson, "\"nextCursor\"",
                "resource list exposes only the usable continuation cursor");
            ResourceRequestException crossOperationCursor = null;
            try
            {
                pagingGateway.Read(pagingSession, new ResourceReadRequest
                {
                    Reference = firstListPage.Items[0].Reference,
                    Representation = ResourceRepresentations.Text,
                    Cursor = firstListPage.Cursor,
                    MaxChars = ResourceReadRequest.MaximumCharacters
                });
            }
            catch (ResourceRequestException ex)
            {
                crossOperationCursor = ex;
            }
            AssertEqual("resource_cursor_invalid", crossOperationCursor == null ? null : crossOperationCursor.ErrorCode,
                "internal list cursor is rejected by resource read");
            AssertTrue(crossOperationCursor != null && !crossOperationCursor.Retryable,
                "invalid internal cross-operation cursor is not retried unchanged");
            ResourceRequestException crossListQuery = null;
            try
            {
                pagingGateway.List(
                    pagingSession,
                    ChatArtifactResourceProvider.ProviderName,
                    null,
                    firstListPage.NextCursor,
                    1);
            }
            catch (ResourceRequestException ex)
            {
                crossListQuery = ex;
            }
            AssertEqual("resource_cursor_invalid", crossListQuery == null ? null : crossListQuery.ErrorCode,
                "list cursor is bound to the exact provider and kind query even when rows match");
            pagingSession.Artifacts.Add(new ChatArtifact { Kind = ChatArtifactKinds.Markdown, Title = "Third", InlineText = "3" });
            ResourceRequestException listDrift = null;
            try
            {
                pagingGateway.List(
                    pagingSession,
                    ChatArtifactResourceProvider.ProviderName,
                    ChatArtifactKinds.Markdown,
                    firstListPage.NextCursor,
                    1);
            }
            catch (ResourceRequestException ex)
            {
                listDrift = ex;
            }
            AssertEqual("resource_revision_changed", listDrift == null ? null : listDrift.ErrorCode,
                "resource list continuation fails instead of shifting across collection revisions");

            var defaultArtifact = new ChatArtifact
            {
                Kind = ChatArtifactKinds.Markdown,
                Title = "Default page",
                InlineText = new string('d', 3000)
            };
            pagingSession.Artifacts.Add(defaultArtifact);
            var defaultRead = pagingGateway.Read(pagingSession, new ResourceReadRequest
            {
                Reference = ChatResourceUri.CreateArtifactRevision(pagingSession, defaultArtifact),
                Representation = ResourceRepresentations.Text
            }).Result;
            AssertEqual(ResourceReadRequest.DefaultCharacters, defaultRead.ReturnedCharacters,
                "default resource page leaves conservative room for exact evidence and continuation");
            AssertTrue(!string.IsNullOrWhiteSpace(defaultRead.NextCursor),
                "conservative default remains lossless through provider-owned paging");
            AssertTrue(ResourceReadToolHandler.Descriptor.ParametersJson.IndexOf(
                    "maxChars", StringComparison.OrdinalIgnoreCase) < 0 &&
                ResourceReadToolHandler.Descriptor.ParametersJson.IndexOf(
                    "cursor", StringComparison.OrdinalIgnoreCase) < 0 &&
                ResourceReadToolHandler.Descriptor.ParametersJson.IndexOf(
                    "uri", StringComparison.OrdinalIgnoreCase) < 0,
                "public resource schema hides provider paging and identity state");

            var resolved = gateway.Resolve(session, resourceUri);
            AssertEqual(resourceUri, resolved.Resource.Reference.Uri, "resource resolve is exact");

            ResourceRequestException pinnedMismatch = null;
            try
            {
                ReadResource(gateway, session, resourceUri, "text", null, 128, "999");
            }
            catch (ResourceRequestException ex)
            {
                pinnedMismatch = ex;
            }
            AssertEqual("resource_revision_mismatch", pinnedMismatch == null ? null : pinnedMismatch.ErrorCode,
                "immutable read rejects a revision token that disagrees with the exact URI");

            var first = ReadResource(gateway, session, resourceUri, "text", null, 128).Result;
            AssertEqual(128, first.ReturnedCharacters, "resource read is bounded");
            AssertTrue(!first.Complete && first.Truncated, "resource read exposes truncation");
            var firstJson = JsonConvert.SerializeObject(first);
            AssertTrue(firstJson.IndexOf("\"offset\"", StringComparison.Ordinal) < 0,
                "resource result keeps continuation offsets opaque");
            AssertContains(firstJson, "\"nextCursor\"", "resource result exposes only the continuation cursor");
            var second = ReadResource(gateway, session, resourceUri, "text",
                first.NextCursor, 128).Result;
            AssertTrue(second.Offset > 0, "resource cursor advances");

            var sharedText = new string('q', 300);
            var firstTwin = new ChatArtifact
            {
                Id = "cursor-twin-a",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Twin A",
                InlineText = sharedText
            };
            var secondTwin = new ChatArtifact
            {
                Id = "cursor-twin-b",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Twin B",
                InlineText = sharedText
            };
            var twinSession = new ChatSession
            {
                Artifacts = new List<ChatArtifact> { firstTwin, secondTwin }
            };
            var twinGateway = new ResourceGatewayService();
            var firstTwinPage = ReadResource(
                twinGateway,
                twinSession,
                ChatResourceUri.CreateArtifactRevisionUri(twinSession, firstTwin),
                ResourceRepresentations.Text,
                null,
                128).Result;
            ResourceRequestException immutableCrossResource = null;
            try
            {
                ReadResource(
                    twinGateway,
                    twinSession,
                    ChatResourceUri.CreateArtifactRevisionUri(twinSession, secondTwin),
                    ResourceRepresentations.Text,
                    firstTwinPage.NextCursor,
                    128);
            }
            catch (ResourceRequestException ex)
            {
                immutableCrossResource = ex;
            }
            AssertEqual("resource_cursor_invalid",
                immutableCrossResource == null ? null : immutableCrossResource.ErrorCode,
                "immutable continuation is bound to the exact URI even when content hashes match");

            var search = gateway.Search(session, null, "NEEDLE", null, 10, 256);
            AssertEqual(1, search.Matches.Count, "resource text search count");
            AssertEqual(resourceUri, search.Matches[0].Reference.Uri, "resource search URI");
            AssertContains(search.Matches[0].Snippet, "NEEDLE", "resource search snippet");

            var invalidRepresentationRejected = false;
            try
            {
                ReadResource(gateway, session, resourceUri, "legacy", null, 128);
            }
            catch (InvalidOperationException)
            {
                invalidRepresentationRejected = true;
            }
            AssertTrue(invalidRepresentationRejected, "resource reads reject unknown representations");

            var imageAttachment = new ChatAttachment
            {
                Id = "direct-image",
                Kind = "image",
                FileName = "direct.png",
                ContentType = "image/png",
                ContentSha256 = new string('2', 64),
                ContentByteLength = 512
            };
            var imageMessage = new ChatMessage
            {
                Id = "direct-source",
                Role = "user",
                Attachments = new List<ChatAttachment> { imageAttachment }
            };
            session.Messages.Add(imageMessage);
            var imageArtifact = new ChatArtifact
            {
                Id = "attachment_direct-image",
                Kind = ChatArtifactKinds.Image,
                SourceMessageId = imageMessage.Id,
                ContentSha256 = imageAttachment.ContentSha256,
                ContentByteLength = imageAttachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"direct-image\"}"
            };
            session.Artifacts.Add(imageArtifact);
            var media = ReadResource(
                gateway,
                session,
                ArtifactUri(session, imageArtifact),
                "media",
                null,
                128);
            AssertTrue(media.Result.HydratedForNextModelStep, "media is hydrated only by an explicit resource read");
            AssertEqual("direct-image", media.ModelAttachments.Single().Id, "media read returns exact attachment");

            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_unmapped",
                Kind = ChatArtifactKinds.Image,
                SourceMessageId = imageMessage.Id
            });
            imageMessage.Attachments.Add(new ChatAttachment { Id = "unmapped", Kind = "image" });
            var implicitAttachmentMappingRejected = false;
            try
            {
                ReadResource(
                    gateway,
                    session,
                    ArtifactUri(session, session.Artifacts.Last()),
                    "media",
                    null,
                    128);
            }
            catch (InvalidOperationException)
            {
                implicitAttachmentMappingRejected = true;
            }
            AssertTrue(implicitAttachmentMappingRejected,
                "artifact media requires explicit attachmentId metadata instead of a legacy id convention");

            var htmlSession = new ChatSession();
            HtmlWorkspaceToolService.UpsertFile(
                htmlSession,
                "index.html",
                "html",
                "<main>Dashboard</main>",
                true);
            var oldScript = new string('x', 180) + " OLD_NEEDLE";
            HtmlWorkspaceToolService.UpsertFile(
                htmlSession,
                "scripts/nested/app.js",
                "script",
                oldScript,
                false);
            HtmlWorkspaceToolService.UpsertDataSource(htmlSession, "rows", "{\"items\":[1,2]}");

            var htmlGateway = new ResourceGatewayService();
            var mutation = new HtmlWorkspaceToolService().Execute(
                HtmlWorkspaceToolCatalog.WriteFileToolId,
                Command(
                    HtmlWorkspaceToolCatalog.WriteFileToolId,
                    "path", "reports/oil-production-chart.html",
                    "content", "<main>Oil chart</main>").Arguments,
                htmlSession,
                delegate { },
                CancellationToken.None);
            AssertEqual(HtmlWorkspaceOutcomeStatus.Ok, mutation.Status,
                "HTML mutation succeeds before canonical-ref assertions");
            var mutationData = JObject.Parse(mutation.DataJson);
            AssertEqual(2, (int)mutationData["version"], "HTML mutation result version");
            var mutationArtifactUri = (string)mutationData.SelectToken("artifactRef.uri");
            AssertTrue(!string.IsNullOrWhiteSpace(mutationArtifactUri),
                "HTML mutation returns the exact artifact revision URI");
            var mutationMember = ((JArray)mutationData["members"])
                .OfType<JObject>()
                .Single(item => (string)item["path"] == "reports/oil-production-chart.html");
            var mutationMemberUri = (string)mutationMember["uri"];
            AssertTrue(!string.IsNullOrWhiteSpace(mutationMemberUri) &&
                mutationMemberUri.IndexOf("oil-production", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML mutation returns an opaque canonical member URI");
            AssertContains(ReadResource(
                    htmlGateway,
                    htmlSession,
                    mutationMemberUri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result.Text,
                "Oil chart",
                "member URI returned by mutation is directly readable");
            var resolvedByPath = htmlGateway.ResolveMember(
                htmlSession,
                mutationArtifactUri,
                "reports/oil-production-chart.html",
                "file");
            AssertEqual(mutationMemberUri, resolvedByPath.Resource.Reference.Uri,
                "central path resolver returns the same canonical member URI");

            var oldScriptResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                10).Items.Single(item => item.Title == "scripts/nested/app.js");
            AssertTrue(oldScriptResource.Reference.Uri.IndexOf("scripts", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML member URI does not expose a nested workspace path");
            AssertEqual(ResourceRepresentations.Source, oldScriptResource.Representations.Last(),
                "HTML files advertise source representation");

            var boundedSource = ReadResource(
                htmlGateway,
                htmlSession,
                oldScriptResource.Reference.Uri,
                ResourceRepresentations.Source,
                null,
                128).Result;
            AssertEqual(128, boundedSource.ReturnedCharacters, "HTML member read obeys the common bound");
            AssertTrue(boundedSource.Truncated && !string.IsNullOrWhiteSpace(boundedSource.NextCursor),
                "HTML member read exposes the common continuation cursor");

            ResourceRequestException missingMember = null;
            try
            {
                var address = ResourceUri.Parse(oldScriptResource.Reference.Uri);
                var segments = address.Segments.ToArray();
                segments[7] = new string('0', 64);
                ReadResource(
                    htmlGateway,
                    htmlSession,
                    ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, segments),
                    ResourceRepresentations.Source,
                    null,
                    128);
            }
            catch (ResourceRequestException ex)
            {
                missingMember = ex;
            }
            AssertEqual("member_not_found", missingMember == null ? null : missingMember.ErrorCode,
                "unknown HTML member URI cannot fall back to the parent artifact");

            var simpleMember = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                20).Items.Single(item => item.Title == "index.html");
            var noncanonicalAddress = ResourceUri.Parse(simpleMember.Reference.Uri);
            var noncanonicalSegments = noncanonicalAddress.Segments.ToArray();
            noncanonicalSegments[7] = "index.html";
            ResourceRequestException noncanonicalMember = null;
            try
            {
                htmlGateway.Resolve(
                    htmlSession,
                    ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, noncanonicalSegments));
            }
            catch (ResourceRequestException ex)
            {
                noncanonicalMember = ex;
            }
            AssertEqual("noncanonical_member_uri",
                noncanonicalMember == null ? null : noncanonicalMember.ErrorCode,
                "human-readable member key is classified instead of generic not-found");

            var missingRevisionAddress = ResourceUri.Parse(mutationArtifactUri);
            var missingRevisionSegments = missingRevisionAddress.Segments.ToArray();
            missingRevisionSegments[4] = "999";
            ResourceRequestException missingRevision = null;
            try
            {
                htmlGateway.Resolve(htmlSession,
                    ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, missingRevisionSegments));
            }
            catch (ResourceRequestException ex)
            {
                missingRevision = ex;
            }
            AssertEqual("revision_not_found", missingRevision == null ? null : missingRevision.ErrorCode,
                "missing artifact revision has a precise error");

            var foreignAddress = ResourceUri.Parse(mutationArtifactUri);
            var foreignSegments = foreignAddress.Segments.ToArray();
            foreignSegments[0] = "another-chat";
            ResourceRequestException chatMismatch = null;
            try
            {
                htmlGateway.Resolve(htmlSession,
                    ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, foreignSegments));
            }
            catch (ResourceRequestException ex)
            {
                chatMismatch = ex;
            }
            AssertEqual("active_chat_mismatch", chatMismatch == null ? null : chatMismatch.ErrorCode,
                "foreign chat URI has a precise error");

            var corruptHtml = new ChatArtifact
            {
                Id = "corrupt-html",
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Revision = 1000,
                InlineText = "{invalid"
            };
            htmlSession.Artifacts.Add(corruptHtml);
            ResourceRequestException corrupt = null;
            try
            {
                htmlGateway.Resolve(htmlSession, ChatResourceUri.CreateArtifactRevisionUri(htmlSession, corruptHtml));
            }
            catch (ResourceRequestException ex)
            {
                corrupt = ex;
            }
            AssertEqual("resource_corrupt", corrupt == null ? null : corrupt.ErrorCode,
                "corrupt HTML revision fails exact resolution explicitly");
            AssertTrue(!htmlGateway.List(
                    htmlSession,
                    ChatArtifactResourceProvider.ProviderName,
                    ChatArtifactKinds.HtmlWorkspace,
                    null,
                    50).Items.Any(item => item.Reference.Uri.IndexOf("corrupt-html", StringComparison.Ordinal) >= 0),
                "corrupt HTML revision is excluded from discovery");

            var dataResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.DataKind,
                null,
                10).Items.Single();
            var binding = JsonConvert.DeserializeObject<HtmlWorkspaceDataBinding>(
                ReadResource(htmlGateway, htmlSession, dataResource.Reference.Uri, ResourceRepresentations.Text, null, 32000).Result.Text);
            AssertTrue(binding.Resource.IsExact, "HTML data members expose exact binding metadata, not a second body");
            AssertContains(htmlGateway.Read(htmlSession, new ResourceReadRequest { Reference = binding.Resource,
                Representation = ResourceRepresentations.Text }).Result.Text, "items", "binding reads its canonical resource body through the same gateway");

            HtmlWorkspaceToolService.UpsertFile(
                htmlSession,
                "scripts/nested/app.js",
                "script",
                "const CURRENT_NEEDLE = true;",
                false);
            var currentScriptResource = htmlGateway.List(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                ChatHtmlResourceCatalog.FileKind,
                null,
                10).Items.Single(item => item.Title == "scripts/nested/app.js");
            AssertTrue(!string.Equals(
                    oldScriptResource.Reference.Uri,
                    currentScriptResource.Reference.Uri,
                    StringComparison.Ordinal),
                "HTML mutation creates a new revision-pinned member URI");
            AssertContains(
                ReadResource(htmlGateway, htmlSession, oldScriptResource.Reference.Uri, ResourceRepresentations.Source, null, 8000).Result.Text,
                "OLD_NEEDLE",
                "historical HTML member URI remains pinned to its original revision");
            AssertContains(
                ReadResource(htmlGateway, htmlSession, currentScriptResource.Reference.Uri, ResourceRepresentations.Source, null, 8000).Result.Text,
                "CURRENT_NEEDLE",
                "current HTML member URI reads the new revision");

            var htmlSearch = htmlGateway.Search(
                htmlSession,
                ChatArtifactResourceProvider.ProviderName,
                "CURRENT_NEEDLE",
                ChatHtmlResourceCatalog.FileKind,
                10,
                128);
            AssertEqual(currentScriptResource.Reference.Uri, htmlSearch.Matches.Single().Reference.Uri,
                "HTML search returns the exact current member URI");

            var activeHtmlArtifact = htmlSession.Artifacts.Single(item => item.Id == htmlSession.ActiveHtmlArtifactId);
            var activeHtmlUri = ArtifactUri(htmlSession, activeHtmlArtifact);
            var activeHtmlDescriptor = htmlGateway.Resolve(htmlSession, activeHtmlUri).Resource;
            AssertEqual("metadata,structure", string.Join(",", activeHtmlDescriptor.Representations.ToArray()),
                "HTML root descriptor does not advertise unsupported text reads");
            var unsupportedHtmlTextRejected = false;
            try
            {
                ReadResource(
                    htmlGateway,
                    htmlSession,
                    activeHtmlUri,
                    ResourceRepresentations.Text,
                    null,
                    8000);
            }
            catch (ResourceRequestException ex)
            {
                unsupportedHtmlTextRejected = string.Equals(
                    ex.ErrorCode,
                    "resource_representation_unavailable",
                    StringComparison.Ordinal);
            }
            AssertTrue(unsupportedHtmlTextRejected,
                "unsupported HTML root text reads return a stable actionable error");
            var structure = ReadResource(
                htmlGateway,
                htmlSession,
                activeHtmlUri,
                ResourceRepresentations.Structure,
                null,
                8000).Result;
            AssertContains(structure.Text, "scripts/nested/app.js", "HTML structure lists member metadata");
            AssertTrue(structure.Text.IndexOf("CURRENT_NEEDLE", StringComparison.Ordinal) < 0,
                "HTML structure does not copy member bodies");
        }

        private static void ArtifactViewerReadsExactBoundedTextAndMarkdown()
        {
            var markdown = "# Exact\n\n" + new string('m', 40050);
            var session = new ChatSession();
            var artifact = new ChatArtifact
            {
                Id = "plan-r2",
                Kind = ChatArtifactKinds.PlanDocument,
                Title = "Plan.md",
                MimeType = "text/markdown",
                Revision = 2,
                InlineText = markdown,
                ContentSha256 = TextPatternEngine.Sha256(markdown)
            };
            session.Artifacts.Add(artifact);
            var uri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
            var viewerGateway = new ResourceGatewayService();
            var viewer = new ArtifactViewerService(viewerGateway);
            var dataPlane = new ResourceDataPlaneService(viewerGateway);

            var first = viewer.ReadPage(session, uri, null, dataPlane);
            AssertEqual(ArtifactViewerKinds.Markdown, first.ViewerKind, "viewer classifies Markdown without sniffing");
            AssertEqual(uri, first.ResourceUri, "viewer preserves exact canonical URI");
            AssertEqual(0, first.Offset, "viewer first page offset");
            AssertEqual(ArtifactViewerService.PageCharacters, first.ReturnedCharacters, "viewer page bound");
            AssertTrue(first.Truncated && !first.Complete && first.FullReadAllowed,
                "bounded first page retains availability of an admitted exact full read");
            AssertEqual(artifact.ContentSha256, first.ContentSha256, "viewer returns representation hash evidence");
            AssertEqual(
                ResourceReadCursor.ReadBinding(uri, ResourceRepresentations.Text),
                first.NextCursor.Split(':').Last(),
                "viewer continuation remains bound to its exact URI and representation");

            var second = viewer.ReadPage(session, uri, first.NextCursor, dataPlane);
            AssertEqual(first.ReturnedCharacters, second.Offset, "viewer continuation is contiguous");
            AssertTrue(second.Complete && second.SourceComplete, "second page completes exact Markdown");
            AssertEqual(markdown, ReadViewerBatch(dataPlane, first) + ReadViewerBatch(dataPlane, second),
                "viewer pages preserve exact Markdown through the data plane");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(
                session, uri.Replace(session.Id, "other-chat"), null, dataPlane));

            var extracted = "attachment exact text";
            var attachment = new ChatAttachment
            {
                Id = "text-a",
                Kind = "text",
                FileName = "notes.txt",
                ContentType = "text/plain",
                ExtractedText = extracted,
                ExtractedCharCount = extracted.Length,
                ExtractedTextSha256 = TextPatternEngine.Sha256(extracted),
                ContentSha256 = new string('a', 64),
                ContentByteLength = 128
            };
            var message = new ChatMessage
            {
                Id = "message-a",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var attachmentArtifact = new ChatArtifact
            {
                Id = "attachment_text-a",
                Kind = ChatArtifactKinds.Attachment,
                Title = "notes.txt",
                MimeType = "text/plain",
                SourceMessageId = message.Id,
                ContentSha256 = attachment.ContentSha256,
                ContentByteLength = attachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"text-a\",\"textTruncated\":false}"
            };
            session.Messages.Add(message);
            session.Artifacts.Add(attachmentArtifact);
            var attachmentPage = viewer.ReadPage(
                session, ChatResourceUri.CreateArtifactRevisionUri(session, attachmentArtifact), null, dataPlane);
            AssertEqual(ArtifactViewerKinds.Text, attachmentPage.ViewerKind, "viewer classifies admitted text source");
            AssertEqual(attachment.ExtractedTextSha256, attachmentPage.ContentSha256,
                "text viewer pins the extracted representation hash, not binary attachment hash");

            var foreignAttachment = new ChatAttachment
            {
                Id = "reused-id",
                Kind = "text",
                FileName = "foreign.txt",
                ContentType = "text/plain",
                ExtractedText = "foreign text",
                ExtractedCharCount = 12,
                ExtractedTextSha256 = TextPatternEngine.Sha256("foreign text"),
                ContentSha256 = new string('b', 64),
                ContentByteLength = 64
            };
            session.Messages.Add(new ChatMessage
            {
                Id = "foreign-message",
                Attachments = new List<ChatAttachment> { foreignAttachment }
            });
            var reboundArtifact = new ChatArtifact
            {
                Id = "rebound-attachment",
                Kind = ChatArtifactKinds.Attachment,
                Title = "foreign.txt",
                MimeType = "text/plain",
                SourceMessageId = "missing-source-message",
                InlineText = "must not replace missing attachment evidence",
                ContentSha256 = foreignAttachment.ContentSha256,
                ContentByteLength = foreignAttachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"reused-id\"}"
            };
            session.Artifacts.Add(reboundArtifact);
            var reboundUri = ChatResourceUri.CreateArtifactRevisionUri(session, reboundArtifact);
            AssertEqual("metadata", string.Join(",", viewerGateway.Resolve(session, reboundUri).Resource.Representations),
                "attachment id cannot rebind an artifact to a different source message");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(session, reboundUri, null, dataPlane));

            var mismatchedArtifact = new ChatArtifact
            {
                Id = "mismatched-attachment",
                Kind = ChatArtifactKinds.Attachment,
                Title = "notes.txt",
                MimeType = "text/plain",
                SourceMessageId = message.Id,
                ContentSha256 = new string('c', 64),
                ContentByteLength = attachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"text-a\"}"
            };
            session.Artifacts.Add(mismatchedArtifact);
            var mismatchedUri = ChatResourceUri.CreateArtifactRevisionUri(session, mismatchedArtifact);
            AssertEqual("metadata", string.Join(",", viewerGateway.Resolve(session, mismatchedUri).Resource.Representations),
                "attachment binary evidence must match the immutable artifact revision");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(session, mismatchedUri, null, dataPlane));

            var truncatedText = "bounded extraction";
            var truncatedAttachment = new ChatAttachment
            {
                Id = "truncated-a",
                Kind = "text",
                FileName = "truncated.txt",
                ContentType = "text/plain",
                ExtractedText = truncatedText,
                ExtractedCharCount = truncatedText.Length,
                ExtractedTextSha256 = TextPatternEngine.Sha256(truncatedText),
                ContentSha256 = new string('d', 64),
                ContentByteLength = 96,
                TextTruncated = true
            };
            var truncatedMessage = new ChatMessage
            {
                Id = "message-truncated",
                Attachments = new List<ChatAttachment> { truncatedAttachment }
            };
            var truncated = new ChatArtifact
            {
                Id = "truncated-text",
                Kind = ChatArtifactKinds.Attachment,
                Title = "truncated.txt",
                MimeType = "text/plain",
                SourceMessageId = truncatedMessage.Id,
                ContentSha256 = truncatedAttachment.ContentSha256,
                ContentByteLength = truncatedAttachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"truncated-a\",\"textTruncated\":true}"
            };
            session.Messages.Add(truncatedMessage);
            session.Artifacts.Add(truncated);
            var truncatedPage = viewer.ReadPage(
                session, ChatResourceUri.CreateArtifactRevisionUri(session, truncated), null, dataPlane);
            AssertTrue(!truncatedPage.SourceComplete && !truncatedPage.Complete && !truncatedPage.FullReadAllowed,
                "truncated extraction cannot masquerade as full copy/download authority");

            var oversizedText = new string('x', ArtifactViewerService.MaximumDocumentCharacters + 1000);
            var oversized = new ChatArtifact
            {
                Id = "oversized-text",
                Kind = ChatArtifactKinds.File,
                Title = "oversized.txt",
                MimeType = "text/plain",
                InlineText = oversizedText,
                ContentSha256 = TextPatternEngine.Sha256(oversizedText)
            };
            session.Artifacts.Add(oversized);
            var oversizedUri = ChatResourceUri.CreateArtifactRevisionUri(session, oversized);
            var oversizedBinding = ResourceReadCursor.ReadBinding(
                oversizedUri,
                ResourceRepresentations.Text);
            var boundedEnd = viewer.ReadPage(
                session,
                oversizedUri,
                ResourceReadCursor.CreateImmutable(480000, oversizedBinding), dataPlane);
            AssertTrue(boundedEnd.ViewerLimitReached && boundedEnd.NextCursor == null && !boundedEnd.FullReadAllowed,
                "viewer stops paging at the explicit document bound");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(
                session,
                oversizedUri,
                ResourceReadCursor.CreateImmutable(512000, oversizedBinding), dataPlane));

            var html = new ChatArtifact
            {
                Id = "uploaded-html",
                Kind = ChatArtifactKinds.Attachment,
                Title = "unsafe.html",
                MimeType = "text/html",
                InlineText = "<script>unsafe()</script>",
                ContentSha256 = TextPatternEngine.Sha256("<script>unsafe()</script>")
            };
            session.Artifacts.Add(html);
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(
                session, ChatResourceUri.CreateArtifactRevisionUri(session, html), null, dataPlane));
        }

        private static string ReadViewerBatch(ResourceDataPlaneService plane, ArtifactViewerPageDto page)
        {
            var json = System.Text.Encoding.UTF8.GetString(plane.Read(page.Data.LeaseId, page.Offset,
                ArtifactViewerService.PageCharacters, CancellationToken.None));
            return (string)JObject.Parse(json)["text"];
        }

        private static void ArtifactViewerReadsExactImageBytes()
        {
            var bytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZKmsAAAAASUVORK5CYII=");
            string hash;
            using (var sha = SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
            var attachment = new ChatAttachment
            {
                Id = "image-a",
                Kind = "image",
                FileName = "pixel.png",
                ContentType = "image/png",
                ContentSha256 = hash,
                ContentByteLength = bytes.LongLength
            };
            var message = new ChatMessage
            {
                Id = "image-message",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var artifact = new ChatArtifact
            {
                Id = "attachment_image-a",
                Kind = ChatArtifactKinds.Image,
                Title = "pixel.png",
                MimeType = "image/png",
                SourceMessageId = message.Id,
                ContentSha256 = hash,
                ContentByteLength = bytes.LongLength,
                MetadataJson = "{\"attachmentId\":\"image-a\"}"
            };
            var session = new ChatSession();
            session.Messages.Add(message);
            session.Artifacts.Add(artifact);
            var uri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
            var viewer = new ArtifactViewerService(new ResourceGatewayService(), item => bytes);

            var image = viewer.ReadImage(session, uri);
            AssertEqual(ArtifactViewerKinds.Image, image.ViewerKind, "image viewer returns typed kind");
            AssertEqual(uri, image.ResourceUri, "image viewer pins exact canonical URI");
            AssertEqual(hash, image.ContentSha256, "image viewer preserves binary hash evidence");
            AssertEqual(bytes.LongLength, image.ByteLength, "image viewer preserves exact byte length");
            AssertTrue(bytes.SequenceEqual(image.Bytes), "image provider captures exact local bytes without bridge encoding");

            var jpeg = new byte[] { 0xff, 0xd8, 0x01, 0x02, 0xff, 0xd9 };
            var requestedDimension = 0;
            var thumbnailViewer = new ArtifactViewerService(
                new ResourceGatewayService(),
                item => bytes,
                null,
                (payload, maximumDimension) =>
                {
                    requestedDimension = maximumDimension;
                    return new ArtifactImageThumbnailRenderResult
                    {
                        Bytes = jpeg,
                        Width = 160,
                        Height = 120
                    };
                });
            var thumbnail = thumbnailViewer.ReadImageThumbnail(session, uri);
            AssertEqual(ArtifactViewerService.MaximumImageThumbnailDimension, requestedDimension,
                "image thumbnail uses the bounded renderer dimension");
            AssertEqual(uri, thumbnail.ResourceUri, "image thumbnail pins exact canonical URI");
            AssertEqual(hash, thumbnail.ContentSha256, "image thumbnail preserves source hash evidence");
            AssertEqual(160, thumbnail.Width, "image thumbnail returns bounded width");
            AssertTrue(jpeg.SequenceEqual(thumbnail.Bytes),
                "image thumbnail returns the separately rendered JPEG");

            RuntimeThrows<InvalidOperationException>(() =>
                new ArtifactViewerService(new ResourceGatewayService(), item => new byte[] { 1 })
                    .ReadImage(session, uri));
            RuntimeThrows<InvalidOperationException>(() =>
                new ArtifactViewerService(
                    new ResourceGatewayService(), item => bytes, null,
                    (payload, maximumDimension) => new ArtifactImageThumbnailRenderResult
                    {
                        Bytes = jpeg,
                        Width = maximumDimension + 1,
                        Height = maximumDimension
                    })
                    .ReadImageThumbnail(session, uri));
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadImage(
                session, uri.Replace(session.Id, "other-chat")));
        }

        private static void ArtifactViewerReadsExactPdfPreview()
        {
            var pdfBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nexact-test-payload");
            string pdfHash;
            using (var sha = SHA256.Create())
            {
                pdfHash = BitConverter.ToString(sha.ComputeHash(pdfBytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
            var extracted = "[PDF page 1]\nVisible text\n[PDF page 2]\n\n";
            var attachment = new ChatAttachment
            {
                Id = "pdf-a",
                Kind = "pdf",
                FileName = "exact.pdf",
                ContentType = "application/pdf",
                ContentSha256 = pdfHash,
                ContentByteLength = pdfBytes.LongLength,
                ExtractedText = extracted,
                ExtractedTextSha256 = TextPatternEngine.Sha256(extracted),
                ExtractedCharCount = extracted.Length,
                PageCount = 2,
                PageTextLengths = new List<int> { 12, 0 }
            };
            var message = new ChatMessage
            {
                Id = "pdf-message",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var artifact = new ChatArtifact
            {
                Id = "attachment_pdf-a",
                Kind = ChatArtifactKinds.Attachment,
                Title = "exact.pdf",
                MimeType = "application/pdf",
                SourceMessageId = message.Id,
                ContentSha256 = pdfHash,
                ContentByteLength = pdfBytes.LongLength,
                MetadataJson = "{\"attachmentId\":\"pdf-a\"}"
            };
            var session = new ChatSession();
            session.Messages.Add(message);
            session.Artifacts.Add(artifact);
            var uri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact);
            var jpeg = new byte[] { 0xff, 0xd8, 0x01, 0x02, 0xff, 0xd9 };
            var viewer = new ArtifactViewerService(
                new ResourceGatewayService(),
                item => pdfBytes,
                (payload, pageIndex, maximumDimension) => new ArtifactPdfPageRenderResult
                {
                    Bytes = jpeg,
                    Width = maximumDimension == ArtifactPdfViewerService.MaximumThumbnailDimension
                        ? (pageIndex == 0 ? 160 : 120)
                        : (pageIndex == 0 ? 800 : 600),
                    Height = maximumDimension == ArtifactPdfViewerService.MaximumThumbnailDimension
                        ? (pageIndex == 0 ? 120 : 160)
                        : (pageIndex == 0 ? 600 : 800)
                });

            var info = viewer.ReadPdfInfo(session, uri);
            AssertEqual(ArtifactViewerKinds.Pdf, info.ViewerKind, "PDF viewer returns typed kind");
            AssertEqual(2, info.PageCount, "PDF viewer returns exact page count");
            AssertTrue(!info.TextTruncated, "complete PDF extraction stays complete");
            var textPlane = new ResourceDataPlaneService(new ResourceGatewayService());
            var textPage = viewer.ReadPage(session, uri, null, textPlane);
            AssertEqual(ArtifactViewerKinds.Pdf, textPage.ViewerKind,
                "PDF extracted text uses the bounded generic viewer kind");
            AssertEqual(extracted, ReadViewerBatch(textPlane, textPage), "PDF viewer returns exact bounded extracted text through the data plane");

            var page = viewer.ReadPdfPage(session, uri, 1);
            AssertEqual(1, page.PageIndex, "PDF viewer renders requested zero-based page");
            AssertEqual(600, page.Width, "PDF viewer preserves bounded render width");
            AssertTrue(jpeg.SequenceEqual(page.Bytes),
                "PDF viewer returns exact rendered JPEG bytes");
            var thumbnail = viewer.ReadPdfThumbnail(session, uri, 1);
            AssertEqual(120, thumbnail.Width, "PDF viewer renders a separately bounded thumbnail width");
            AssertEqual(160, thumbnail.Height, "PDF viewer preserves thumbnail aspect ratio");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPdfPage(session, uri, 2));
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPdfThumbnail(session, uri, 2));
            RuntimeThrows<InvalidOperationException>(() =>
                new ArtifactViewerService(
                    new ResourceGatewayService(), item => pdfBytes,
                    (payload, pageIndex, maximumDimension) => new ArtifactPdfPageRenderResult
                    {
                        Bytes = jpeg,
                        Width = maximumDimension + 1,
                        Height = maximumDimension
                    })
                    .ReadPdfThumbnail(session, uri, 0));
            RuntimeThrows<InvalidOperationException>(() =>
                new ArtifactViewerService(
                    new ResourceGatewayService(), item => pdfBytes,
                    (payload, pageIndex, maximumDimension) => { throw new BadImageFormatException(); })
                    .ReadPdfPage(session, uri, 0));
        }

        private static void ResourceGatewayRejectsAmbiguousChatArtifacts()
        {
            var session = new ChatSession();
            var first = new ChatArtifact
            {
                Id = "duplicate-artifact",
                Kind = ChatArtifactKinds.PlanDocument,
                Title = "First.md",
                MimeType = "text/markdown",
                InlineText = "FIRST_DUPLICATE_BODY",
                ContentSha256 = TextPatternEngine.Sha256("FIRST_DUPLICATE_BODY")
            };
            var second = new ChatArtifact
            {
                Id = first.Id.ToUpperInvariant(),
                Kind = ChatArtifactKinds.PlanDocument,
                Title = "Second.md",
                MimeType = "text/markdown",
                InlineText = "SECOND_DUPLICATE_BODY",
                ContentSha256 = TextPatternEngine.Sha256("SECOND_DUPLICATE_BODY")
            };
            var unique = new ChatArtifact
            {
                Id = "unique-artifact",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Unique.md",
                MimeType = "text/markdown",
                InlineText = "UNIQUE_BODY",
                ContentSha256 = TextPatternEngine.Sha256("UNIQUE_BODY")
            };
            session.Artifacts.Add(first);
            session.Artifacts.Add(second);
            session.Artifacts.Add(unique);
            session.ActivePlanDocumentArtifactId = first.Id;
            var gateway = new ResourceGatewayService();
            var duplicateUri = ChatResourceUri.CreateArtifactRevisionUri(session, first);
            var duplicateReference = ChatResourceUri.CreateArtifactRevision(session, first);

            var listed = gateway.List(
                session, ChatArtifactResourceProvider.ProviderName, ChatArtifactKinds.Markdown, null, 10);
            AssertEqual(1, listed.Items.Count, "ambiguous artifact identity is omitted from discovery");
            AssertEqual(ChatResourceUri.CreateArtifactRevisionUri(session, unique), listed.Items[0].Reference.Uri,
                "unrelated exact artifacts remain available");
            AssertEqual(0, gateway.Search(
                session, ChatArtifactResourceProvider.ProviderName, "DUPLICATE_BODY", null, 10, 128).Matches.Count,
                "ambiguous artifact bodies are not searched through an arbitrary duplicate");
            ResourceRequestException ambiguousResolve = null;
            ResourceRequestException ambiguousRead = null;
            try { gateway.Resolve(session, duplicateUri); }
            catch (ResourceRequestException ex) { ambiguousResolve = ex; }
            try { ReadResource(gateway, session, duplicateUri, ResourceRepresentations.Text, null, 128); }
            catch (ResourceRequestException ex) { ambiguousRead = ex; }
            AssertEqual("resource_corrupt", ambiguousResolve == null ? null : ambiguousResolve.ErrorCode,
                "ambiguous exact resolve is classified as corrupt persistence");
            AssertEqual("resource_corrupt", ambiguousRead == null ? null : ambiguousRead.ErrorCode,
                "ambiguous exact read is classified as corrupt persistence");
            AssertEqual(null, ChatResourceUri.ResolveArtifactRevision(session, first.Id),
                "ambiguous id cannot create a shared resource reference");
            string referencedId;
            AssertEqual(false, ChatResourceUri.TryGetCurrentArtifactId(
                session, duplicateReference, out referencedId),
                "ambiguous current reference is rejected");
            AssertEqual(false, ChatResourceUri.TryGetArtifactId(
                session, duplicateReference, out referencedId),
                "ambiguous historical reference is rejected");
            AssertEqual(0, ChatResourceUri.CurrentArtifactIds(
                session, new[] { duplicateReference }).Count,
                "ambiguous references do not enter prompt or reachability projections");
            var promptIndex = ChatResourcePromptIndex.Build(session, 5000, new AppSettings());
            AssertTrue(promptIndex.IndexOf(first.Id, StringComparison.OrdinalIgnoreCase) < 0,
                "ambiguous id is omitted from the bounded prompt index");
            AssertContains(promptIndex, "note: Unique.md",
                "unrelated semantic artifact target remains in the prompt index");
            var runtimeContext = ConversationPromptComposer.BuildRuntimeContext(
                ChatModes.Plan,
                null,
                new ToolCatalogEntry[0],
                new SkillDefinition[0],
                null,
                session,
                new AppSettings());
            AssertTrue(runtimeContext.IndexOf("\"active_plan\"", StringComparison.Ordinal) < 0,
                "ambiguous active Plan is not projected into model context");

            ChatSessionNormalizer.Normalize(session, "Excel", "ambiguous-artifacts", "Ambiguous.xlsx");
            AssertEqual(3, session.Artifacts.Count,
                "normalization preserves ambiguity instead of selecting a newest duplicate");
            AssertEqual(null, session.ActivePlanDocumentArtifactId,
                "ambiguous active Plan pointer is cleared");
            var bridgeArtifacts = ChatArtifactDto.From(session);
            AssertEqual(1, bridgeArtifacts.Count,
                "bridge projection omits every ambiguous revision without hiding unique artifacts");
            AssertEqual(unique.Id, bridgeArtifacts[0].Id, "bridge retains the unrelated unique artifact");
            AssertEqual(1, ArtifactLibraryProjectionService.Project(session).Heads.Count,
                "Library projection retains only the unrelated unique artifact");

            var referenceMessage = new ChatMessage
            {
                Id = "ambiguous-reference-message",
                ProtocolMessage = true,
                ResourceRefs = new List<ResourceRef>
                {
                    duplicateReference,
                    ChatResourceUri.CreateArtifactRevision(session, unique)
                }
            };
            session.Messages.Add(referenceMessage);
            ChatResourceReferenceService.LinkMessageResources(session, 0);
            AssertEqual(1, referenceMessage.ResourceRefs.Count,
                "reference rebase drops ambiguous identities without blocking unrelated resources");
            AssertEqual(ChatResourceUri.CreateArtifactRevisionUri(session, unique), referenceMessage.ResourceRefs[0].Uri,
                "reference rebase preserves the unrelated exact artifact");

            WithTempPaths(paths =>
            {
                var store = new RNAssistant.Core.Storage.ChatStore(paths);
                AssertEqual(false, store.LoadArtifactBody(session, first.Id),
                    "body hydration does not choose an arbitrary duplicate");
                RuntimeThrows<InvalidOperationException>(() => store.Save(session));
            });

            var htmlSession = new ChatSession { ActiveHtmlArtifactId = "duplicate-html" };
            htmlSession.Artifacts.Add(new ChatArtifact
            {
                Id = "duplicate-html",
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Revision = 1,
                InlineText = "{\"Files\":[],\"DataSources\":[]}"
            });
            htmlSession.Artifacts.Add(new ChatArtifact
            {
                Id = "DUPLICATE-HTML",
                Kind = ChatArtifactKinds.HtmlWorkspace,
                Revision = 2,
                InlineText = "{\"Files\":[],\"DataSources\":[]}"
            });
            RuntimeThrows<InvalidOperationException>(() =>
                HtmlWorkspaceArtifactService.Restore(htmlSession, htmlSession.ActiveHtmlArtifactId));
            AssertEqual(0, HtmlWorkspaceNavigationService.GetRecoveryCandidates(htmlSession, null).Count,
                "HTML recovery never offers an ambiguous revision candidate");
            var htmlGateway = new ResourceGatewayService();
            AssertEqual(0, htmlGateway.List(
                    htmlSession,
                    ChatArtifactResourceProvider.ProviderName,
                    ChatHtmlResourceCatalog.FileKind,
                    null,
                    20).Items.Count,
                "ambiguous active HTML artifact exposes no member through discovery");
        }

        private static void ResourceGatewayPreservesEmptyTextRepresentations()
        {
            var emptyHash = TextPatternEngine.Sha256(string.Empty);
            var attachment = new ChatAttachment
            {
                Id = "empty-text",
                Kind = "text",
                FileName = "empty.txt",
                ContentType = "text/plain",
                ExtractedText = string.Empty,
                ExtractedCharCount = 0,
                ExtractedTextSha256 = emptyHash,
                ExtractedTextByteLength = 0,
                ContentSha256 = emptyHash,
                ContentByteLength = 0
            };
            var message = new ChatMessage
            {
                Id = "empty-source",
                Attachments = new List<ChatAttachment> { attachment }
            };
            var artifact = new ChatArtifact
            {
                Id = "attachment_empty-text",
                Kind = ChatArtifactKinds.Attachment,
                Title = attachment.FileName,
                MimeType = attachment.ContentType,
                SourceMessageId = message.Id,
                ContentSha256 = attachment.ContentSha256,
                ContentByteLength = attachment.ContentByteLength,
                MetadataJson = "{\"attachmentId\":\"empty-text\"}"
            };
            var whitespace = " \r\n\t ";
            var whitespaceArtifact = new ChatArtifact
            {
                Id = "whitespace-markdown",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Whitespace.md",
                MimeType = "text/markdown",
                InlineText = whitespace,
                ContentSha256 = TextPatternEngine.Sha256(whitespace)
            };
            var unavailable = new ChatArtifact
            {
                Id = "missing-markdown-body",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Missing.md",
                MimeType = "text/markdown",
                InlineText = null,
                ContentSha256 = new string('f', 64)
            };
            var session = new ChatSession
            {
                Messages = new List<ChatMessage> { message },
                Artifacts = new List<ChatArtifact> { artifact, whitespaceArtifact, unavailable }
            };
            var gateway = new ResourceGatewayService();
            var emptyUri = ChatResourceUri.CreateArtifactRevisionUri(session, artifact);

            var empty = ReadResource(
                gateway, session, emptyUri, ResourceRepresentations.Text, null, 128).Result;
            AssertEqual(string.Empty, empty.Text, "empty attachment text is preserved");
            AssertEqual(0, empty.TotalCharacters, "empty attachment length remains exact");
            AssertTrue(empty.Complete && !empty.Truncated, "empty attachment read is complete");
            AssertEqual(emptyHash, empty.ContentSha256, "empty attachment keeps representation hash");
            var emptyPage = new ArtifactViewerService(gateway).ReadPage(session, emptyUri, null, new ResourceDataPlaneService(gateway));
            AssertTrue(emptyPage.Complete && emptyPage.FullReadAllowed,
                "empty exact text remains viewable and downloadable");

            var whitespaceRead = ReadResource(
                gateway,
                session,
                ChatResourceUri.CreateArtifactRevisionUri(session, whitespaceArtifact),
                ResourceRepresentations.Text,
                null,
                128).Result;
            AssertEqual(whitespace, whitespaceRead.Text, "whitespace-only Markdown is not treated as absent");
            RuntimeThrows<InvalidOperationException>(() => ReadResource(
                gateway,
                session,
                ChatResourceUri.CreateArtifactRevisionUri(session, unavailable),
                ResourceRepresentations.Text,
                null,
                128));
        }

        private static void LiveOfficeAndVbaResourcesAreBoundedAndGuarded()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var sharedVbaSource =
                    "Option Explicit\nSub ResourceNeedle()\n" + new string('x', 220) + "\nEnd Sub";
                adapter.SetVbaModule("ResourceModule", sharedVbaSource, "StdModule");
                adapter.SetVbaModule("ResourceTwin", sharedVbaSource, "StdModule");
                var session = NewSession(adapter);
                var gateway = executor.ResourceGateway;

                var discovery = gateway.List(session, null, null, null, 20);
                AssertEqual("catalog,chat,context,document,excel,state,vba", string.Join(",", discovery.Providers.ToArray()),
                    "resource discovery exposes the registered providers only");

                var defaultVba = gateway.List(
                    session,
                    VbaResourceProvider.ProviderName,
                    null,
                    null,
                    20);
                AssertTrue(defaultVba.Items.Any(item => item.Kind == VbaResourceProvider.ProjectKind) &&
                    defaultVba.Items.Any(item => item.Kind == VbaResourceProvider.ComponentKind &&
                        string.Equals(item.Title, "ResourceModule", StringComparison.OrdinalIgnoreCase)),
                    "default VBA discovery exposes exact live component URIs without hidden kind vocabulary");
                ResourceRequestException unknownVbaKind = null;
                try
                {
                    gateway.List(session, VbaResourceProvider.ProviderName, "module", null, 20);
                }
                catch (ResourceRequestException ex)
                {
                    unknownVbaKind = ex;
                }
                AssertEqual("resource_kind_unknown", unknownVbaKind == null ? null : unknownVbaKind.ErrorCode,
                    "unknown VBA kind is explicit instead of looking like an empty project");

                var document = gateway.List(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind,
                    null,
                    20).Items.Single();
                AssertTrue(document.Reference.Uri.StartsWith("rna://document/", StringComparison.Ordinal),
                    "live document uses a canonical resource URI");
                AssertTrue(document.Reference.Uri.IndexOf("MockWorkbook", StringComparison.OrdinalIgnoreCase) < 0 &&
                    document.Reference.Uri.IndexOf("C:", StringComparison.OrdinalIgnoreCase) < 0,
                    "live document URI does not expose its title or path");

                for (var index = 0; index < 6; index++)
                {
                    adapter.AddExcelSheetForTest("ResourcePage" + index);
                }
                var documentText = ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result;
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.Text), "live document text is readable on demand");
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.ContentSha256) &&
                    !string.Equals(
                        documentText.ContentSha256,
                        documentText.Resource.Reference.Revision,
                        StringComparison.Ordinal),
                    "logical revision is distinct from the view content hash");
                AssertTrue(!string.IsNullOrWhiteSpace(documentText.NextCursor),
                    "long live document read exposes a revision-bound continuation");
                adapter.AddExcelSheetForTest("ResourceDrift");
                ResourceRequestException documentDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        document.Reference.Uri,
                        ResourceRepresentations.Text,
                        documentText.NextCursor,
                        128,
                        documentText.Resource.Reference.Revision);
                }
                catch (ResourceRequestException ex)
                {
                    documentDrift = ex;
                }
                AssertEqual("resource_revision_changed", documentDrift == null ? null : documentDrift.ErrorCode,
                    "live document continuation fails instead of mixing revisions");
                ResourceRequestException pinnedDocumentDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        document.Reference.Uri,
                        ResourceRepresentations.Text,
                        null,
                        128,
                        documentText.Resource.Reference.Revision);
                }
                catch (ResourceRequestException ex)
                {
                    pinnedDocumentDrift = ex;
                }
                AssertEqual("resource_revision_changed", pinnedDocumentDrift == null ? null : pinnedDocumentDrift.ErrorCode,
                    "live read rejects an explicitly pinned stale revision before returning its first chunk");

                var selection = gateway.List(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.SelectionKind,
                    null,
                    20).Items.Single();
                var selectionSearch = gateway.Search(
                    session,
                    LiveDocumentResourceProvider.ProviderName,
                    "Sales",
                    LiveDocumentResourceProvider.SelectionKind,
                    10,
                    128);
                AssertEqual(selection.Reference.Uri, selectionSearch.Matches.Single().Reference.Uri,
                    "selection search returns its exact live resource URI");
                AssertTrue(!string.IsNullOrWhiteSpace(selectionSearch.Matches.Single().Reference.Revision),
                    "live search result pins the searched content revision");

                var component = VbaComponent(executor, session, "ResourceModule");
                AssertTrue(component.Reference.Uri.IndexOf("ResourceModule", StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA component URI does not expose the module name");
                var firstSource = ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result;
                AssertEqual(128, firstSource.ReturnedCharacters, "VBA source read is bounded");
                AssertTrue(firstSource.Truncated && !string.IsNullOrWhiteSpace(firstSource.NextCursor),
                    "VBA source read exposes continuation");
                AssertTrue(!string.IsNullOrWhiteSpace(firstSource.Resource.Reference.Revision),
                    "VBA source read carries exact revision evidence");
                var twinComponent = VbaComponent(executor, session, "ResourceTwin");
                ResourceRequestException vbaCrossResource = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        twinComponent.Reference.Uri,
                        ResourceRepresentations.Source,
                        firstSource.NextCursor,
                        128,
                        firstSource.Resource.Reference.Revision);
                }
                catch (ResourceRequestException ex)
                {
                    vbaCrossResource = ex;
                }
                AssertEqual("resource_cursor_invalid",
                    vbaCrossResource == null ? null : vbaCrossResource.ErrorCode,
                    "live continuation is bound to the exact VBA URI even when revisions match");

                var firstComponentPage = gateway.List(
                    session,
                    VbaResourceProvider.ProviderName,
                    VbaResourceProvider.ComponentKind,
                    null,
                    1);
                AssertTrue(!string.IsNullOrWhiteSpace(firstComponentPage.NextCursor),
                    "VBA component list exposes an opaque continuation");
                adapter.SetVbaModule("ResourceListDrift", "Sub Added()\nEnd Sub", "StdModule");
                ResourceRequestException vbaListDrift = null;
                try
                {
                    gateway.List(
                        session,
                        VbaResourceProvider.ProviderName,
                        VbaResourceProvider.ComponentKind,
                        firstComponentPage.NextCursor,
                        1);
                }
                catch (ResourceRequestException ex)
                {
                    vbaListDrift = ex;
                }
                AssertEqual("resource_revision_changed", vbaListDrift == null ? null : vbaListDrift.ErrorCode,
                    "VBA list continuation fails instead of shifting across project revisions");

                var sourceSearch = SearchVbaSource(executor, session, "ResourceNeedle");
                AssertTrue(sourceSearch.Matches.Any(match =>
                    string.Equals(match.Title, "ResourceModule", StringComparison.OrdinalIgnoreCase)),
                    "VBA resource search finds source text");
                var metadataSearch = SearchVbaSource(executor, session, "ResourceModule");
                AssertEqual(ResourceRepresentations.Metadata, metadataSearch.Matches.Single().Representation,
                    "VBA resource search discovers component metadata without reading its body");

                adapter.SetVbaModule(
                    "ResourceModule",
                    "Option Explicit\nSub ResourceNeedleChanged()\n" + new string('y', 220) + "\nEnd Sub",
                    "StdModule");
                ResourceRequestException vbaDrift = null;
                try
                {
                    ReadResource(
                        gateway,
                        session,
                        component.Reference.Uri,
                        ResourceRepresentations.Source,
                        firstSource.NextCursor,
                        128,
                        firstSource.Resource.Reference.Revision);
                }
                catch (ResourceRequestException ex)
                {
                    vbaDrift = ex;
                }
                AssertEqual("resource_revision_changed", vbaDrift == null ? null : vbaDrift.ErrorCode,
                    "VBA continuation fails instead of mixing source revisions");
                AssertContains(vbaDrift == null ? null : vbaDrift.Message,
                    "Run common.resources_read again for the same semantic target",
                    "live revision drift gives one explicit fresh whole-read recovery");

                session.LastRun = new ChatRunRecord
                {
                    RunId = "resource-migration-run",
                    DocumentRuntimeKey = adapter.RuntimeDocumentKey
                };
                var previousDocumentKey = session.DocumentKey;
                adapter.DocumentKeyValue = "saved-document";
                AssertTrue(!string.IsNullOrWhiteSpace(ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result.Text),
                    "live document URI survives a save while chat migration is deferred");
                AssertContains(ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result.Text,
                    "ResourceNeedleChanged",
                    "VBA URI survives a save while chat migration is deferred");

                ChatSessionNormalizer.RecordDocumentKeyMigration(
                    session,
                    previousDocumentKey,
                    adapter.DocumentKeyValue);
                session.DocumentKey = adapter.DocumentKeyValue;
                session.Context.DocumentKey = session.DocumentKey;
                AssertTrue(!string.IsNullOrWhiteSpace(ReadResource(
                    gateway,
                    session,
                    document.Reference.Uri,
                    ResourceRepresentations.Text,
                    null,
                    128).Result.Text),
                    "live document URI survives completed document identity migration");
                AssertContains(ReadResource(
                    gateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result.Text,
                    "ResourceNeedleChanged",
                    "VBA URI survives completed document identity migration");

                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var renamed = executor.ExecuteManual(
                    Command(
                        "common.vba_rename_module",
                        "moduleName", "ResourceModule",
                        "newModuleName", "ResourceModuleRenamed"),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(renamed.Success, "VBA resource recovery setup renames the live component");
                var staleComponent = executor.ExecuteManual(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "target", "VBA module: ResourceModule",
                        "representation", ResourceRepresentations.Source),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("resource_target_not_found", staleComponent.ErrorCode,
                    "renamed VBA component returns a stable missing-target error");
                AssertEqual(true, staleComponent.Retryable,
                    "stale VBA component target invites fresh discovery");
                AssertContains(staleComponent.Message, ResourceToolCatalog.FindToolId,
                    "stale VBA component target explains exact recovery");

                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var blocked = executor.ExecuteManual(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "target", ResourceGatewayService.IntentBaseTarget(document),
                        "representation", ResourceRepresentations.Text),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("active_document_changed", blocked.ErrorCode,
                    "live resource read refuses a different active document");
            });
        }

        private static void ArtifactPromptUsesBoundedWorkingSet()
        {
            var session = new ChatSession();
            for (var index = 0; index < 20; index++)
            {
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "artifact_" + index,
                    Kind = ChatArtifactKinds.Markdown,
                    Title = "Artifact " + index,
                    RelativePath = "private/path/" + index,
                    InlineText = "body",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(index)
                });
            }
            session.ActivePlanDocumentArtifactId = "artifact_0";
            var latestMessage = new ChatMessage
            {
                Role = "user",
                Content = "latest"
            };
            latestMessage.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Single(item => item.Id == "artifact_19")));
            session.Messages.Add(latestMessage);

            var prompt = ChatResourcePromptIndex.Build(session, 5000, new AppSettings());
            AssertContains(prompt, "showing=12/20", "artifact prompt exposes bounded working set");
            AssertContains(prompt, "note: Artifact 0", "active artifact target remains visible");
            AssertContains(prompt, "note: Artifact 19", "recently referenced artifact target remains visible");
            AssertTrue(prompt.IndexOf("artifact_0", StringComparison.OrdinalIgnoreCase) < 0 &&
                prompt.IndexOf("artifact_19", StringComparison.OrdinalIgnoreCase) < 0,
                "artifact ids remain runtime-owned");
            AssertTrue(prompt.IndexOf("private/path", StringComparison.OrdinalIgnoreCase) < 0,
                "artifact prompt does not expose local paths");
            AssertTrue(prompt.IndexOf("policy=", StringComparison.OrdinalIgnoreCase) < 0,
                "artifact prompt has one reference-first rule instead of per-artifact legacy policies");
        }

        private static void HistoricalAttachmentsStayReferenceOnly()
        {
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            var session = NewSession(adapter);
            var historicMessage = new ChatMessage
            {
                Role = "user",
                Content = "Old request",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment
                    {
                        Id = "old-text",
                        Kind = "text",
                        FileName = "large.txt",
                        ExtractedText = "HISTORICAL_BODY_MUST_NOT_REPLAY",
                        ExtractedCharCount = 50000
                    }
                },
                AttachmentAnalysis = new AttachmentAnalysisContext
                {
                    Content = "HISTORICAL_ANALYSIS_MUST_NOT_REPLAY",
                    AttachmentIds = new List<string> { "old-text" }
                }
            };
            session.Messages.Add(historicMessage);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Old answer" });
            session.Messages.Add(new ChatMessage { Role = "user", Content = "New request" });
            session.Artifacts.Add(new ChatArtifact
            {
                Id = "attachment_old-text",
                Kind = ChatArtifactKinds.Attachment,
                SourceMessageId = historicMessage.Id,
                MetadataJson = "{\"attachmentId\":\"old-text\"}"
            });
            historicMessage.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
            var historicUri = ArtifactUri(session, session.Artifacts.Last());

            var prompt = new ModelContextCompiler().BuildPreview(
                ChatModes.Agent,
                "New request", adapter, new ToolCatalogEntry[0], new SkillDefinition[0],
                new DocumentContext(), new AppSettings(), session, null);
            var old = prompt.First(message => (message.Content ?? string.Empty).StartsWith("Old request", StringComparison.Ordinal));
            AssertEqual(0, old.Attachments.Count, "historical attachment bodies are removed from replay");
            AssertTrue(old.Content.IndexOf(historicUri, StringComparison.Ordinal) < 0,
                "historical model message hides canonical resource identity");
            AssertContains(FlattenSimple(prompt), "target=attachment: Untitled",
                "runtime context keeps a semantic resource target");
            AssertTrue(old.Content.IndexOf("artifact:attachment_old-text", StringComparison.Ordinal) < 0,
                "model history does not expose a second artifact-id reference channel");
            AssertTrue(old.Content.IndexOf("attachment:old-text", StringComparison.Ordinal) < 0,
                "model history does not duplicate the canonical resource as attachment metadata");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_BODY_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical extracted text is not copied into every prompt");
            AssertTrue(FlattenSimple(prompt).IndexOf("HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "historical query-specific media analysis is not replayed into every prompt");
            var usage = JObject.FromObject(ContextUsageEstimator.FromSession(session, new AppSettings()));
            AssertTrue(usage["usedChars"].Value<int>() < 1000,
                "session usage estimates the virtualized reference rather than the historical body");

            var gateway = new ResourceGatewayService();
            var descriptor = gateway.Resolve(session, historicUri).Resource;
            AssertTrue(!descriptor.Representations.Contains("analysis"),
                "query-specific helper analysis is not exposed as a reusable resource representation");

            string compactionInput = null;
            LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
            {
                compactionInput = FlattenSimple(messages);
                return Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"References retained.\"}" });
            };
            new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                session,
                new AppSettings(),
                string.Empty,
                true,
                null,
                CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(compactionInput.IndexOf(historicUri, StringComparison.Ordinal) < 0,
                "compaction hides canonical resource identity");
            AssertTrue(compactionInput.IndexOf("HISTORICAL_BODY_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "compaction does not reopen historical attachment bodies");
            AssertTrue(compactionInput.IndexOf("HISTORICAL_ANALYSIS_MUST_NOT_REPLAY", StringComparison.Ordinal) < 0,
                "compaction does not inject saved media analysis");
        }

        private static void AgentHydratesArtifactMediaOnlyAfterRead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var image = new ChatAttachment
                {
                    Id = "historic-image",
                    Kind = "image",
                    FileName = "chart.png",
                    ContentType = "image/png",
                    Size = 4,
                    ContentSha256 = new string('4', 64),
                    ContentByteLength = 4
                };
                var source = new ChatMessage
                {
                    Id = "historic-source",
                    Role = "user",
                    Content = "Earlier image",
                    Attachments = new List<ChatAttachment> { image }
                };
                var session = NewSession(adapter);
                session.Model = "omni";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored.", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_historic-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    ContentSha256 = image.ContentSha256,
                    ContentByteLength = image.ContentByteLength,
                    MetadataJson = "{\"attachmentId\":\"historic-image\"}"
                });
                source.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
                var resourceUri = ArtifactUri(session, session.Artifacts.Last());
                var resourceTarget = executor.ResourceGateway.Find(
                    session, "chart.png", "conversation").Items.Single().Target;
                var settings = new AppSettings { Model = "omni" };
                settings.ModelCapabilities["omni"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                var calls = 0;
                string materializedPrompt = null;
                LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
                {
                    calls += 1;
                    var mediaMessages = messages.Where(message =>
                        message != null && message.ProtocolMessage &&
                        (message.Attachments ?? new List<ChatAttachment>()).Any(item => item != null && item.Id == image.Id)).ToList();
                    if (calls == 1)
                    {
                        AssertEqual(0, mediaMessages.Count, "historical media is absent before explicit read");
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Читаю изображение.\",\"final\":false,\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"" + resourceTarget + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    if (calls == 2)
                    {
                        AssertEqual(1, mediaMessages.Count, "media is hydrated for the next model step only");
                        materializedPrompt = JsonConvert.SerializeObject(messages);
                        AssertTrue(FlattenSimple(messages).IndexOf(resourceUri, StringComparison.Ordinal) < 0,
                            "model projection hides resource URI provenance");
                        AssertEqual(0, mediaMessages[0].ResourceRefs.Count,
                            "model media projection hides canonical resource provenance");
                        AssertTrue(!messages.Any(message => message != null &&
                            string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.OrdinalIgnoreCase) &&
                            (message.ResourceRefs ?? new List<ResourceRef>()).Any(reference => reference.Uri == resourceUri)),
                            "model tool result hides the durable ResourceRef");
                        var durableMedia = session.Messages.First(message => message != null &&
                            (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal));
                        AssertTrue(ReferencesArtifact(session, durableMedia, "attachment_historic-image"),
                            "durable media retains canonical resource provenance");
                        AssertTrue(session.Messages.Any(message => message != null &&
                            string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.OrdinalIgnoreCase) &&
                            (message.ResourceRefs ?? new List<ResourceRef>()).Any(reference => reference.Uri == resourceUri)),
                            "durable tool result retains the exact ResourceRef");
                        return Task.FromResult(new LlmCompletionResult { Content = "invalid envelope" });
                    }
                    AssertEqual(1, mediaMessages.Count, "format repair retains media from the same accepted prompt");
                    AssertEqual(materializedPrompt, JsonConvert.SerializeObject(messages.Where(message =>
                        !(message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal))),
                        "repair does not change the materialized semantic prompt");
                    AssertTrue(messages.Any(message => message != null && !message.ExcludeFromModelContext &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal)),
                        "media stays available until the logical model step accepts or fails");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Изображение прочитано.\",\"final\":true,\"tool_calls\":[]}"
                    });
                };
                var tools = executor.GetControllerTools().ToList();
                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Прочитай старое изображение.",
                    session,
                    NewContext(adapter),
                    settings,
                    tools,
                    (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();

                AssertEqual("Изображение прочитано.", result.AssistantText, "agent completes after lazy media read");
                AssertEqual(3, calls, "artifact media read plus one format repair completes");
                AssertTrue(session.Messages.Where(message => message != null && message.ProtocolMessage)
                    .All(message => (message.Attachments ?? new List<ChatAttachment>()).Count == 0),
                    "hydrated media is released after the following model step");
                AssertTrue(session.Messages.Where(message => message != null &&
                    (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal))
                    .All(message => message.ExcludeFromModelContext), "consumed media stays out of later steps");
            });
        }

        private static void AgentResourceMediaProjectionFailureIsExplicit()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var settings = new AppSettings
                {
                    Model = "text-only",
                    AgentResponseMode = AgentResponseModes.JsonObject
                };
                settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings
                {
                    SupportsImages = false,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                settings.ModelCapabilities["vision-helper"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 16384
                };
                settings.AttachmentModelPriority.Add("vision-helper");
                var session = NewSession(adapter);
                session.Model = settings.Model;
                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                using (var modelSession = ConversationModelSession.CreateAsync(
                    adapter,
                    null,
                    new AttachmentAnalysisService((s, m, o, u, c) =>
                        throw new InvalidOperationException("helper unavailable")),
                    EventStore(store),
                    ChatModes.Agent,
                    "Read the image.",
                    session,
                    NewContext(adapter),
                    settings,
                    executor.GetControllerTools().ToList(),
                    null,
                    null,
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult())
                {
                    var command = new ToolInvocation
                    {
                        ToolId = ResourceToolCatalog.ReadToolId,
                        ToolCallId = "media_projection_failure"
                    };
                    var original = new ToolResultMaterialization(
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok(
                            "Media read.",
                            "{\"complete\":true}",
                            new[] { new ResourceRef("rna://chat/projection/resource") }),
                        new[]
                        {
                            new ChatAttachment
                            {
                                Id = "projection-image",
                                Kind = "image",
                                FileName = "projection.png",
                                ContentType = "image/png",
                                Size = 4
                            }
                        });
                    var prepared = modelSession.PrepareToolResultAsync(
                        command, original, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Ok, original.Result.Status,
                        "request-local media failure does not rewrite the executed read record");
                    AssertEqual(ToolResultStatus.Error, prepared.Result.Result.Status,
                        "unavailable resource evidence is an explicit model-facing read error");
                    AssertEqual("artifact_media_unavailable",
                        (string)JObject.Parse(prepared.Result.Result.DataJson)["code"],
                        "resource projection failure has an actionable code");
                    AssertEqual(ToolResultStatus.Ok,
                        ToolResultResourceService.ProjectionFailureStatus(
                            new ToolInvocation { ToolId = "excel.add_sheet" }, ToolResultStatus.Ok),
                        "projection failure cannot rewrite a known mutation outcome");
                    AssertEqual(ToolResultStatus.Ok,
                        ToolResultResourceService.ProjectionFailureStatus(
                            new ToolInvocation { ToolId = "common.resources_upsert" }, ToolResultStatus.Ok),
                        "resource namespace prefixes do not classify future mutations as exact reads");
                }
            });
        }

        private static void AgentRoutesArtifactMediaThroughHelper()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var image = new ChatAttachment
                {
                    Id = "helper-image",
                    Kind = "image",
                    FileName = "scan.png",
                    ContentType = "image/png",
                    Size = 4,
                    ContentSha256 = new string('5', 64),
                    ContentByteLength = 4
                };
                var source = new ChatMessage
                {
                    Id = "helper-source",
                    Role = "user",
                    Content = "Earlier scan",
                    Attachments = new List<ChatAttachment> { image }
                };
                var session = NewSession(adapter);
                session.Model = "text-only";
                session.Messages.Add(source);
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Stored.", ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion });
                session.Artifacts.Add(new ChatArtifact
                {
                    Id = "attachment_helper-image",
                    Kind = ChatArtifactKinds.Image,
                    Title = image.FileName,
                    MimeType = image.ContentType,
                    SourceMessageId = source.Id,
                    ContentSha256 = image.ContentSha256,
                    ContentByteLength = image.ContentByteLength,
                    MetadataJson = "{\"attachmentId\":\"helper-image\"}"
                });
                source.ResourceRefs.Add(ArtifactReference(session, session.Artifacts.Last()));
                var resourceUri = ArtifactUri(session, session.Artifacts.Last());
                var resourceTarget = executor.ResourceGateway.Find(
                    session, "scan.png", "conversation").Items.Single().Target;
                var settings = new AppSettings { Model = "text-only" };
                settings.ModelCapabilities["text-only"] = new ModelCapabilitySettings
                {
                    SupportsImages = false,
                    SupportsAudio = false,
                    MaxContextTokens = 32768
                };
                settings.ModelCapabilities["vision-helper"] = new ModelCapabilitySettings
                {
                    SupportsImages = true,
                    SupportsAudio = false,
                    MaxContextTokens = 16384
                };
                settings.AttachmentModelPriority.Add("vision-helper");
                var primaryCalls = 0;
                var helperCalls = 0;
                LlmCompletionDelegate completion = (requestSettings, messages, options, stream, cancellationToken) =>
                {
                    if (string.Equals(requestSettings.Model, "vision-helper", StringComparison.OrdinalIgnoreCase))
                    {
                        helperCalls += 1;
                        AssertEqual(2, messages.Count(), "historical helper receives isolated instruction and request");
                        AssertEqual(1, messages.Last().Attachments.Count, "historical helper receives selected media");
                        return Task.FromResult(new LlmCompletionResult { Content = "Summary\nThe scan contains a total of 42." });
                    }
                    primaryCalls += 1;
                    if (primaryCalls == 1)
                    {
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "{\"message\":\"Читаю скан.\",\"final\":false,\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"target\":\"" + resourceTarget + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    var evidenceMessage = messages.First(message => message != null && message.ProtocolMessage &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal));
                    AssertTrue(evidenceMessage.AttachmentAnalysis != null, "helper evidence is attached to the protocol message");
                    AssertContains(evidenceMessage.AttachmentAnalysis.Content, "total of 42", "helper evidence reaches primary context");
                    AssertTrue(FlattenSimple(messages).IndexOf(resourceUri, StringComparison.Ordinal) < 0,
                        "helper-routed model context hides the resource URI");
                    AssertEqual(0, evidenceMessage.ResourceRefs.Count,
                        "helper-routed model context hides exact resource references");
                    AssertTrue(session.Messages.Any(message => message != null &&
                        string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.OrdinalIgnoreCase) &&
                        (message.ResourceRefs ?? new List<ResourceRef>()).Any(reference => reference.Uri == resourceUri)),
                        "helper-routed durable result retains exact resource evidence");
                    var rawRead = false;
                    new LlmMessageBuilder(delegate
                    {
                        rawRead = true;
                        return new byte[] { 1, 2, 3, 4 };
                    }).Build(messages, requestSettings);
                    AssertTrue(!rawRead, "text-only primary does not reload helper-routed raw media");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"На скане указано 42.\",\"final\":true,\"tool_calls\":[]}"
                    });
                };

                var result = CreateConversationRunService(adapter, executor, completion).ExecuteAsync(
                    ChatModes.Agent,
                    "Какое число на старом скане?",
                    session,
                    NewContext(adapter),
                    settings,
                    executor.GetControllerTools().ToList(),
                    (Action<string, string, ChatActivity>)null).GetAwaiter().GetResult();

                AssertEqual("На скане указано 42.", result.AssistantText, "agent completes from helper evidence");
                AssertEqual(2, primaryCalls, "primary model keeps normal two-step loop");
                AssertEqual(1, helperCalls, "only the missing vision modality uses a helper");
            });
        }
    }
}
