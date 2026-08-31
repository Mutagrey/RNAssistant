using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

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
                AssertResourceControllerProjection(tools.Single(item => item.Id == ResourceToolCatalog.ListToolId),
                    ResourceListToolHandler.Descriptor, ResourceListToolHandler.Policy, "resources_list");
                AssertResourceControllerProjection(tools.Single(item => item.Id == ResourceToolCatalog.ResolveToolId),
                    ResourceResolveToolHandler.Descriptor, ResourceResolveToolHandler.Policy, "resources_resolve");
                AssertResourceControllerProjection(tools.Single(item => item.Id == ResourceToolCatalog.SearchToolId),
                    ResourceSearchToolHandler.Descriptor, ResourceSearchToolHandler.Policy, "resources_search");
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

                var listed = execute(ResourceToolCatalog.ListToolId, "{\"provider\":\"chat\"}");
                AssertEqual(ToolExecutionOutcome.Ok, listed.Outcome, "native resource list succeeds in chat mode");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched, listed.Evidence.Dispatch, "provider invocation is recorded");
                AssertEqual(ToolEffectEvidence.None, listed.Evidence.Effect, "read success does not manufacture verified effect");
                var resourceUri = JsonConvert.DeserializeObject<ResourceListPage>(listed.Result.DataJson)
                    .Items.Single().Reference.Uri;
                AssertTrue(resourceUri.StartsWith("rna://", StringComparison.Ordinal),
                    "native list retains canonical resource references");

                var calls = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResourceToolCatalog.ListToolId] = "{\"provider\":\"chat\"}",
                    [ResourceToolCatalog.ResolveToolId] = JsonConvert.SerializeObject(new { uri = resourceUri }),
                    [ResourceToolCatalog.SearchToolId] = "{\"provider\":\"chat\",\"query\":\"body\"}",
                    [ResourceToolCatalog.ReadToolId] = JsonConvert.SerializeObject(new
                    {
                        uri = resourceUri,
                        representation = ResourceRepresentations.Text,
                        maxChars = 128
                    })
                };
                foreach (var item in calls)
                {
                    var record = item.Key == ResourceToolCatalog.ListToolId ? listed : execute(item.Key, item.Value);
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, item.Key + " uses its native handler");
                    var command = new ToolCommand
                    {
                        ToolId = item.Key,
                        Arguments = JsonConvert.DeserializeObject<Dictionary<string, object>>(item.Value)
                    };
                    var manual = executor.Execute(command, tools, new AppSettings(), false, true, session);
                    AssertTrue(manual.Success, item.Key + " manual path uses the same native handler");
                    AssertEqual(record.Result.DataJson, manual.DataJson,
                        item.Key + " manual and kernel paths share one implementation");
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
                HtmlArtifactToolExecutor.UpsertFile(
                    session,
                    "nested/report.html",
                    "html",
                    "<main>Resolved through native tool</main>",
                    true);
                var htmlArtifact = session.Artifacts.Single(item => item.Id == session.ActiveHtmlArtifactId);
                var nativeMemberResolve = execute(ResourceToolCatalog.ResolveToolId,
                    JsonConvert.SerializeObject(new
                    {
                        parentUri = ChatResourceUri.CreateArtifactRevisionUri(session, htmlArtifact),
                        memberPath = "nested/report.html",
                        memberType = "file"
                    }));
                AssertEqual(ToolExecutionOutcome.Ok, nativeMemberResolve.Outcome,
                    "native resource resolver accepts the path alternative");
                AssertEqual("nested/report.html",
                    (string)JObject.Parse(nativeMemberResolve.Result.DataJson).SelectToken("resource.title"),
                    "native path resolution returns the exact member descriptor");
                var foreignSegments = ResourceUri.Parse(
                    ChatResourceUri.CreateArtifactRevisionUri(session, htmlArtifact)).Segments.ToArray();
                foreignSegments[0] = "foreign-chat";
                var foreignResolve = execute(ResourceToolCatalog.ResolveToolId,
                    JsonConvert.SerializeObject(new
                    {
                        uri = ResourceUri.Create(ChatArtifactResourceProvider.ProviderName, foreignSegments)
                    }));
                var foreignData = JObject.Parse(foreignResolve.Result.DataJson);
                AssertEqual(ToolExecutionOutcome.Error, foreignResolve.Outcome,
                    "foreign chat resolution is a typed runtime error");
                AssertEqual("active_chat_mismatch", (string)foreignData["code"],
                    "resource runtime preserves the precise error code");
                AssertContains(foreignResolve.Message, "owning chat",
                    "resource runtime includes actionable recovery guidance");
                var invalidManual = executor.Execute(Command(ResourceToolCatalog.ListToolId, "limit", 51), tools,
                    new AppSettings(), false, true, session);
                AssertTrue(!invalidManual.Success && invalidManual.ErrorCode == "invalid_arguments",
                    "manual command uses the same native validation boundary");

                var hostRuntime = new HostRuntime(adapter, FixturePaths.Value);
                var target = new OfficeDocumentExecutionExpectation
                {
                    Host = adapter.HostName,
                    DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey
                };
                var backendCalls = adapter.Executed.Count;
                var liveListArguments = "{\"provider\":\"vba\",\"kind\":\"vba-component\"}";
                using (hostRuntime.BeginDocumentAccess(target))
                {
                    var blockedCall = new ToolCall("blocked_live_list", ResourceToolCatalog.ListToolId,
                        liveListArguments);
                    var blockedPolicy = runtime.Describe(blockedCall);
                    var blocked = runtime.ExecuteAsync(new ToolExecutionContext(blockedCall, blockedPolicy, "run", "turn", "step",
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Error, blocked.Outcome,
                        "new native command cannot borrow document access held on the same thread");
                    AssertEqual("tool_mutation_busy", (string)JObject.Parse(blocked.Result.DataJson)["code"],
                        "native live list reports the occupied document gate");
                    AssertEqual(backendCalls, adapter.Executed.Count, "blocked native live list never reaches Office backend");
                }

                var releasedCall = new ToolCall("released_live_list", ResourceToolCatalog.ListToolId,
                    liveListArguments);
                var releasedPolicy = runtime.Describe(releasedCall);
                var released = runtime.ExecuteAsync(new ToolExecutionContext(releasedCall, releasedPolicy, "run", "turn", "step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, released.Outcome, "native live list succeeds after document access release");
                AssertTrue(adapter.Executed.Count > backendCalls, "released native live list reaches the Office backend");
            });
        }

        private static void AssertResourceControllerProjection(
            ToolDefinition definition,
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
            AssertTrue(ReferenceEquals(policy, definition.RuntimePolicy),
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
            var readRegistry = new ToolHandlerRegistry();
            var readRegistration = ToolPackSnapshot.Capture(
                ResourceReadToolHandler.Descriptor,
                ResourceReadToolHandler.Policy,
                ResourceReadToolHandler.Binding);
            readRegistry.Register(readRegistration,
                new ResourceReadToolHandler(pagingGateway, pagingSession, null));
            var readRuntime = new ToolRuntime(readRegistry, ChatModes.Chat, false, false);
            var crossCall = new ToolCall("cross_cursor", ResourceToolCatalog.ReadToolId,
                JsonConvert.SerializeObject(new
                {
                    uri = firstListPage.Items[0].Reference.Uri,
                    representation = ResourceRepresentations.Text,
                    cursor = firstListPage.Cursor
                }));
            var crossPolicy = readRuntime.Describe(crossCall);
            var crossRecord = readRuntime.ExecuteAsync(new ToolExecutionContext(
                crossCall, crossPolicy, "run", "turn", "step", DateTime.UtcNow, false, 1),
                CancellationToken.None).GetAwaiter().GetResult();
            var crossOperationCursor = ToolResultUiProjection.Create(crossRecord);
            AssertEqual("resource_cursor_invalid", crossOperationCursor.ErrorCode,
                "list cursor is rejected by resource read");
            AssertTrue(crossOperationCursor.Retryable != true,
                "invalid cross-operation cursor is not retried unchanged");
            AssertContains(crossOperationCursor.Message, "Omit cursor",
                "invalid cursor tells the model how to restart");
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
            HtmlArtifactToolExecutor.UpsertFile(
                htmlSession,
                "index.html",
                "html",
                "<main>Dashboard</main>",
                true);
            var oldScript = new string('x', 180) + " OLD_NEEDLE";
            HtmlArtifactToolExecutor.UpsertFile(
                htmlSession,
                "scripts/nested/app.js",
                "script",
                oldScript,
                false);
            HtmlArtifactToolExecutor.UpsertDataSource(htmlSession, "rows", "{\"items\":[1,2]}");

            var htmlGateway = new ResourceGatewayService();
            var mutation = new HtmlArtifactToolExecutor().ExecuteControllerTool(
                Command(
                    HtmlArtifactToolExecutor.UpsertToolId,
                    "resourceType", "file",
                    "name", "reports/oil-production-chart.html",
                    "content", "<main>Oil chart</main>",
                    "setActive", false),
                htmlSession,
                false);
            AssertTrue(mutation.Success, "HTML mutation succeeds before canonical-ref assertions");
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
            AssertContains(
                ReadResource(htmlGateway, htmlSession, dataResource.Reference.Uri, ResourceRepresentations.Text, null, 128).Result.Text,
                "items",
                "HTML data is an independently readable text resource");

            HtmlArtifactToolExecutor.UpsertFile(
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

            var first = viewer.ReadPage(session, uri, null);
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

            var second = viewer.ReadPage(session, uri, first.NextCursor);
            AssertEqual(first.ReturnedCharacters, second.Offset, "viewer continuation is contiguous");
            AssertTrue(second.Complete && second.SourceComplete, "second page completes exact Markdown");
            AssertEqual(markdown, first.Text + second.Text, "viewer pages preserve exact Markdown bytes as text");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(
                session, uri.Replace(session.Id, "other-chat"), null));

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
                session, ChatResourceUri.CreateArtifactRevisionUri(session, attachmentArtifact), null);
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
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(session, reboundUri, null));

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
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(session, mismatchedUri, null));

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
                session, ChatResourceUri.CreateArtifactRevisionUri(session, truncated), null);
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
                ResourceReadCursor.CreateImmutable(480000, oversizedBinding));
            AssertTrue(boundedEnd.ViewerLimitReached && boundedEnd.NextCursor == null && !boundedEnd.FullReadAllowed,
                "viewer stops paging at the explicit document bound");
            RuntimeThrows<InvalidOperationException>(() => viewer.ReadPage(
                session,
                oversizedUri,
                ResourceReadCursor.CreateImmutable(512000, oversizedBinding)));

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
                session, ChatResourceUri.CreateArtifactRevisionUri(session, html), null));
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
            AssertContains(promptIndex, unique.Id, "unrelated exact artifact remains in the prompt index");
            var runtimeContext = ConversationPromptComposer.BuildRuntimeContext(
                ChatModes.Plan,
                null,
                new ToolDefinition[0],
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
            var emptyPage = new ArtifactViewerService(gateway).ReadPage(session, emptyUri, null);
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
                AssertEqual("chat,document,vba", string.Join(",", discovery.Providers.ToArray()),
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
                    string.Equals(
                        documentText.ContentSha256,
                        documentText.Resource.Reference.Revision,
                        StringComparison.Ordinal),
                    "live document read carries exact revision evidence");
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
                        128);
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
                        documentText.ContentSha256);
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
                        128);
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
                        128);
                }
                catch (ResourceRequestException ex)
                {
                    vbaDrift = ex;
                }
                AssertEqual("resource_revision_changed", vbaDrift == null ? null : vbaDrift.ErrorCode,
                    "VBA continuation fails instead of mixing source revisions");
                AssertContains(vbaDrift == null ? null : vbaDrift.Message,
                    "both cursor and revision omitted",
                    "live revision drift gives one explicit fresh-read recovery action");

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

                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var renamed = executor.Execute(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "ResourceModule",
                        "newModuleName", "ResourceModuleRenamed",
                        "mode", "rename"),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(renamed.Success, "VBA resource recovery setup renames the live component");
                var staleComponent = executor.Execute(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "uri", component.Reference.Uri,
                        "representation", ResourceRepresentations.Source),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("resource_not_found", staleComponent.ErrorCode,
                    "renamed VBA component returns a stable missing-resource error");
                AssertEqual(true, staleComponent.Retryable,
                    "stale VBA component URI invites fresh discovery");
                AssertContains(staleComponent.Message, "common.resources_list",
                    "stale VBA component URI explains exact recovery");

                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var blocked = executor.Execute(
                    Command(
                        ResourceToolCatalog.ReadToolId,
                        "uri", document.Reference.Uri,
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
            AssertContains(prompt, "artifact_0", "active artifact remains visible");
            AssertContains(prompt, "artifact_19", "recently referenced artifact remains visible");
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

            var prompt = new ConversationPromptComposer().BuildMessages(
                ChatModes.Agent,
                "New request", adapter, new ToolDefinition[0], new SkillDefinition[0],
                new DocumentContext(), new AppSettings(), session, null);
            var old = prompt.First(message => (message.Content ?? string.Empty).StartsWith("Old request", StringComparison.Ordinal));
            AssertEqual(0, old.Attachments.Count, "historical attachment bodies are removed from replay");
            AssertContains(old.Content, "resource:" + historicUri, "historical canonical resource reference remains");
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
            AssertContains(compactionInput, "resource:" + historicUri,
                "compaction receives the canonical resource reference");
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
                            Content = "{\"message\":\"Читаю изображение.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    if (calls == 2)
                    {
                        AssertEqual(1, mediaMessages.Count, "media is hydrated for the next model step only");
                        materializedPrompt = JsonConvert.SerializeObject(messages);
                        AssertContains(FlattenSimple(messages), resourceUri, "hydrated media retains resource URI provenance");
                        AssertTrue(ReferencesArtifact(session, mediaMessages[0], "attachment_historic-image"),
                            "hydrated media retains canonical resource provenance");
                        AssertTrue(messages.Any(message => message != null &&
                            string.Equals(message.ToolName, ResourceToolCatalog.ReadToolId, StringComparison.OrdinalIgnoreCase) &&
                            (message.ResourceRefs ?? new List<ResourceRef>()).Any(reference => reference.Uri == resourceUri)),
                            "resource tool result carries the same durable ResourceRef");
                        return Task.FromResult(new LlmCompletionResult { Content = "invalid envelope" });
                    }
                    AssertEqual(1, mediaMessages.Count, "format repair retains media from the same accepted prompt");
                    AssertEqual(materializedPrompt, JsonConvert.SerializeObject(messages.Where(message =>
                        !(message.Content ?? string.Empty).StartsWith("FORMAT_REPAIR:", StringComparison.Ordinal))),
                        "repair does not change the materialized prompt or resource evidence");
                    AssertTrue(messages.Any(message => message != null && !message.ExcludeFromModelContext &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal)),
                        "media stays available until the logical model step accepts or fails");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"Изображение прочитано.\",\"tool_calls\":[]}"
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
                            Content = "{\"message\":\"Читаю скан.\",\"tool_calls\":[{\"name\":\"common.resources_read\",\"arguments\":{\"uri\":\"" + resourceUri + "\",\"representation\":\"media\"}}]}"
                        });
                    }
                    var evidenceMessage = messages.First(message => message != null && message.ProtocolMessage &&
                        (message.Content ?? string.Empty).StartsWith("RESOURCE_MEDIA_INPUT", StringComparison.Ordinal));
                    AssertTrue(evidenceMessage.AttachmentAnalysis != null, "helper evidence is attached to the protocol message");
                    AssertContains(evidenceMessage.AttachmentAnalysis.Content, "total of 42", "helper evidence reaches primary context");
                    var rawRead = false;
                    new LlmMessageBuilder(delegate
                    {
                        rawRead = true;
                        return new byte[] { 1, 2, 3, 4 };
                    }).Build(messages, requestSettings);
                    AssertTrue(!rawRead, "text-only primary does not reload helper-routed raw media");
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = "{\"message\":\"На скане указано 42.\",\"tool_calls\":[]}"
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
