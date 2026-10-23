using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JsonTreePointerAndBudget()
        {
            const string source = "{\"a/b\":{\"~\":[1,2,3]},\" \" : true,\"\":null}";
            var tree = ResourceJsonTreeSelector.Select(new StringReader(source), "/a~1b/~0", 1, 1, 1, 2000);
            AssertEqual("array", tree.NodeType, "escaped pointer identifies the array");
            AssertEqual("/a~1b/~0/1", tree.Nodes[1].Pointer, "array index is a canonical child pointer");
            AssertEqual("2", tree.Nodes[1].Value, "number preview is navigable");
            AssertTrue(tree.Nodes[1].ValueExact == false && tree.TotalChildren == 3 &&
                tree.ReturnedChildren == 1 && tree.NextOffset == 2 && tree.Coverage().Path == "/a~1b/~0" &&
                tree.Coverage().Start == 1 && tree.Coverage().End == 2, "typed tree keeps exact partial coverage and next offset");
            var root = ResourceJsonTreeSelector.Select(new StringReader(source), "", 0, 0, 10, 2000);
            AssertTrue(root.Nodes.Count == 1 && root.Nodes[0].ChildCount == 3 && root.NextOffset == null &&
                root.Coverage().Path == "", "depth zero returns just root with its child count");
            AssertEqual("boolean", ResourceJsonTreeSelector.Select(new StringReader(source), "/ ", 0, 0, 1, 2000).NodeType,
                "whitespace-bearing object keys are exact");
            AssertEqual("null", ResourceJsonTreeSelector.Select(new StringReader(source), "/", 0, 0, 1, 2000).NodeType,
                "empty object keys remain addressable");
            AssertEqual("resource_json_pointer_invalid", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader(source), "/a~2b", 0, 0, 1, 2000)).ErrorCode,
                "invalid escape fails explicitly");
            AssertEqual("resource_json_pointer_not_found", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader(source), "/a~1b/~0/01", 0, 0, 1, 2000)).ErrorCode,
                "array indices with leading zero are not selected");
            AssertEqual("resource_json_pointer_ambiguous", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader("{\"x\":{\"a\":1},\"x\":{\"b\":2}}"), "/x/a", 0, 0, 1, 2000)).ErrorCode,
                "duplicate ancestor keys cannot choose a path");
            AssertEqual("resource_json_pointer_ambiguous", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader("{\"x\":1,\"x\":2}"), "", 1, 0, 10, 2000)).ErrorCode,
                "tree cannot publish duplicate child pointers");
            AssertEqual("resource_json_view_too_large", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader("{\"x\":\"" + new string('a', 5000) + "\"}"),
                    "/x", 0, 0, 1, 500)).ErrorCode, "only model output is budgeted");
            AssertEqual("resource_json_invalid", RuntimeThrows<ResourceRequestException>(() =>
                ResourceJsonTreeSelector.Select(new StringReader("{\"x\": [1,2"), "", 1, 0, 10, 2000)).ErrorCode,
                "incomplete JSON cannot publish partial evidence");
            foreach (var invalid in new[] { "{'x':1}", "{x:1}", "{\"x\":1,}", "[1,]",
                "{\"x\":undefined}", "{\"x\":01}", "{\"x\":NaN}" })
                AssertEqual("resource_json_invalid", RuntimeThrows<ResourceRequestException>(() =>
                    ResourceJsonTreeSelector.Select(new StringReader(invalid), "", 0, 0, 1, 2000)).ErrorCode,
                    "permissive Json.NET extensions cannot become exact JSON tree evidence");
            RuntimeThrows<OperationCanceledException>(() => ResourceJsonTreeSelector.Select(
                new StringReader(source), "", 1, 0, 10, 2000, new CancellationToken(true)));
        }

        private static void JsonTreeWorkspaceExactStreaming()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                using (var writer = new StreamWriter(Path.Combine(workspace.RootPath, "large.json"), false,
                    new UTF8Encoding(false, true)))
                {
                    var blank = new string(' ', 8192);
                    for (var i = 0; i < 512; i++) writer.Write(blank);
                    writer.Write("{\"data\":[{\"x\":1},{\"x\":2},{\"x\":3}]}");
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var first = provider.ReadTree("large.json", "/data", 2, 1, 1, 2000);
                AssertTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 12L * 1024 * 1024,
                    "four-MiB workspace JSON scans without whole-source allocation");
                AssertTrue(first.Result.Tree.Nodes.Any(node => node.Pointer == "/data/1/x") &&
                    first.Result.Tree.NextOffset == 2 && first.Evidence.Single().Coverage.Kind == ResourceCoverageKinds.JsonPath &&
                    !first.Evidence.Single().Complete && first.Result.Resource.Reference.IsExact,
                    "array slice has exact partial evidence from one revision");
                var retained = new ChatBlobStore(paths).ReadText(first.Evidence.Single().Payload.ToBlobReference());
                AssertEqual(JsonConvert.SerializeObject(first.Result.Tree), retained,
                    "CAS retains the model-visible typed tree rather than source bytes");
                var observed = new Dictionary<string, ResourceEvidence>();
                var handler = new WorkspaceResourceHandler(provider, null, () => null, observed, false, null, 2000);
                var execution = new ToolRuntimeFixture().Context(toolId: ResourceToolCatalog.ReadToolId);
                var args = new Dictionary<string, object> { ["target"] = "large.json", ["representation"] = "tree",
                    ["pointer"] = "/data", ["depth"] = 1, ["offset"] = 1, ["limit"] = 1 };
                var result = handler.ExecuteAsync(new ToolHandlerContext(execution, args), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, result.Result.Status,
                    "workspace resource tool accepts tree selection");
                var visible = ModelResultProjection.ProjectResourceData(ResourceToolCatalog.ReadToolId, JObject.Parse(result.Result.DataJson));
                AssertTrue(visible["tree"] != null && visible["text"] == null &&
                    (string)visible["coverage"]["path"] == "/data" &&
                    (bool)visible["selectionComplete"] && !(bool)visible["complete"] && !(bool)visible["sourceComplete"],
                    "model sees tree and partial pointer coverage, not internal retained JSON text");
                var metadata = result.ResourceEvidence.Single().ReadMetadata;
                AssertTrue(metadata.RequestedDepth == 1 && metadata.RequestedCoverage.Path == "/data" &&
                    metadata.RequestedCoverage.Start == 1 && metadata.RequestedCoverage.End == 2 &&
                    !observed["large.json"].Complete, "accepted selector survives evidence and cannot authorize overwrite");
                var restored = JsonConvert.DeserializeObject<ResourceEvidence>(JsonConvert.SerializeObject(result.ResourceEvidence.Single()));
                AssertEqual("/data", restored.ReadMetadata.RequestedCoverage.Path, "pointer survives durable metadata roundtrip");
                File.WriteAllText(Path.Combine(workspace.RootPath, "large.json"), "{\"data\":[9]}");
                var changed = provider.ReadTree("large.json", "/data", 1, 0, 1, 2000);
                AssertTrue(changed.Result.Resource.Reference.Revision != first.Result.Resource.Reference.Revision &&
                    retained == new ChatBlobStore(paths).ReadText(first.Evidence.Single().Payload.ToBlobReference()),
                    "external drift creates a new exact revision without changing retained tree bytes");

                var ports = typeof(WorkspaceConversationService).GetNestedType("WorkspacePorts", BindingFlags.NonPublic);
                var schema = JObject.Parse((string)ports.GetMethod("ResourceSchema", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { false, true, true, 2000 }));
                string error;
                AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "large.json", ["representation"] = "tree",
                    ["pointer"] = "", ["depth"] = 0 }, schema, false, out error), "workspace schema admits root pointer");
                AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "large.json", ["representation"] = "tree",
                    ["pointer"] = "", ["startLine"] = 1, ["lineCount"] = 1 }, schema, false, out error),
                    "workspace schema rejects mixed tree/line selectors");
                AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["type"] = "office",
                    ["target"] = "File: data.json", ["representation"] = "tree", ["pointer"] = "/data",
                    ["limit"] = 2 }, schema, false, out error),
                    "workspace Office binding exposes the same tree selector");
            });
        }

        private static void JsonTreeOfficeArtifactPages()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var artifact = new ChatArtifact { Id = "json-tree-artifact", Kind = ChatArtifactKinds.File,
                    Title = "large.json", MimeType = "application/json",
                    InlineText = "{\"padding\":\"" + new string('q', 40000) + "\",\"data\":[0,1,2]}" };
                session.Artifacts.Add(artifact);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var descriptor = gateway.Resolve(session, ChatResourceUri.CreateArtifactRevisionUri(session, artifact)).Resource;
                AssertTrue(descriptor.Representations.Contains(ResourceRepresentations.Tree),
                    "JSON file discovery advertises its typed tree view");
                var selection = gateway.ReadTree(session, descriptor.Reference, descriptor, "/data", 1, 1, 1, 2000,
                    CancellationToken.None);
                var evidence = gateway.Evidence(session, selection.Result).Single();
                AssertTrue(selection.Result.Tree.Nodes.Any(node => node.Pointer == "/data/1") &&
                    selection.Result.Tree.NextOffset == 2 && evidence.Payload != null &&
                    evidence.Coverage.Kind == ResourceCoverageKinds.JsonPath && !evidence.Complete,
                    "Office artifact pages become one exact partial tree observation");
                AssertEqual(JsonConvert.SerializeObject(selection.Result.Tree),
                    executor.Payloads.ReadText(evidence.Payload.ToBlobReference()),
                    "Office evidence retains the selected typed body");
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToList(),
                    new AppSettings(), ChatModes.Agent, false);
                var target = ResourceGatewayService.IntentTarget(descriptor);
                var execution = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "tree", ["pointer"] = "/data",
                        ["depth"] = 1, ["offset"] = 1, ["limit"] = 1 });
                AssertEqual(ToolExecutionOutcome.Ok, execution.Outcome,
                    "Office model tool routes JSON tree through the common gateway");
                var projected = ModelResultProjection.ProjectResourceData(ResourceToolCatalog.ReadToolId,
                    JObject.Parse(execution.Result.DataJson));
                AssertTrue(projected["tree"] != null && projected["text"] == null &&
                    (string)projected["coverage"]["path"] == "/data" &&
                    execution.ResourceEvidence.Single().ReadMetadata.RequestedDepth == 1,
                    "Office model and evidence retain tree selector without source text");
                var schema = JObject.Parse(ResourceReadToolHandler.Descriptor.ParametersJson);
                string error;
                AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "File: large.json",
                    ["representation"] = "tree", ["pointer"] = "/data", ["offset"] = 1, ["limit"] = 1 },
                    schema, false, out error), "Office schema admits JSON tree selector");
                AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["target"] = "File: large.json",
                    ["representation"] = "tree", ["pointer"] = "/data", ["startChar"] = 0, ["charCount"] = 1 },
                    schema, false, out error), "Office schema rejects mixed tree/source selectors");
                const string clippedText = "{\"data\":[1]}";
                var attachment = new ChatAttachment { Id = "clipped-json", Kind = "text", FileName = "clipped.json",
                    ContentType = "application/json", ExtractedText = clippedText,
                    ExtractedCharCount = clippedText.Length, ExtractedTextSha256 = TextPatternEngine.Sha256(clippedText),
                    ContentSha256 = new string('d', 64), ContentByteLength = 100, TextTruncated = true };
                var message = new ChatMessage { Id = "clipped-json-message",
                    Attachments = new List<ChatAttachment> { attachment } };
                var clipped = new ChatArtifact { Id = "clipped-json-artifact", Kind = ChatArtifactKinds.Attachment,
                    Title = "clipped.json", MimeType = "application/json", SourceMessageId = message.Id,
                    ContentSha256 = attachment.ContentSha256, ContentByteLength = attachment.ContentByteLength,
                    MetadataJson = "{\"attachmentId\":\"clipped-json\",\"textTruncated\":true}" };
                session.Messages.Add(message);
                session.Artifacts.Add(clipped);
                var clippedDescriptor = gateway.Resolve(session, ChatResourceUri.CreateArtifactRevisionUri(session, clipped)).Resource;
                AssertTrue(!clippedDescriptor.Representations.Contains(ResourceRepresentations.Tree),
                    "incomplete extracted JSON does not advertise an exact tree view");
                AssertEqual("resource_json_source_incomplete", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.ReadTree(session, clippedDescriptor.Reference, clippedDescriptor, "", 1, 0, 1, 2000,
                        CancellationToken.None)).ErrorCode, "truncated extraction cannot prove a JSON tree");
                var shared = new ChatArtifact { Id = "shared-json-artifact", Kind = DocumentArtifactStore.SharedContextKind,
                    Title = "private.json", MimeType = "application/json", InlineText = "{\"claim\":\"private\"}" };
                session.Artifacts.Add(shared);
                var sharedDescriptor = gateway.Resolve(session, ChatResourceUri.CreateArtifactRevisionUri(session, shared)).Resource;
                AssertTrue(!sharedDescriptor.Representations.Contains(ResourceRepresentations.Tree),
                    "filtered shared context cannot advertise raw JSON navigation");
                AssertEqual("resource_json_view_unsupported", RuntimeThrows<ResourceRequestException>(() =>
                    gateway.ReadTree(session, sharedDescriptor.Reference, sharedDescriptor, "", 1, 0, 1, 2000,
                        CancellationToken.None)).ErrorCode, "tree selector cannot bypass shared-claim filtering");
            });
        }
    }
}
