using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private static void YamlTreePointerAndRefusals()
        {
            const string source = "# YAML navigation\n'a/b':\n  '~': [one, two, three] # values\n' ': true\n'': null\n";
            var tree = ResourceYamlTreeSelector.Select(new StringReader(source), "/a~1b/~0", 1, 1, 1, 2000);
            AssertTrue(tree.Format == "yaml" && tree.NodeType == "array" && tree.Nodes[1].Pointer == "/a~1b/~0/1" &&
                tree.Nodes[1].Value == "two" && tree.Nodes[1].ValueExact == false && tree.TotalChildren == 3 &&
                tree.ReturnedChildren == 1 && tree.NextOffset == 2 && tree.Coverage().Kind == ResourceCoverageKinds.YamlPath,
                "YAML selection has literal keys, decoded scalar previews and exact partial path coverage");
            var root = ResourceYamlTreeSelector.Select(new StringReader(source), "", 0, 0, 10, 2000);
            AssertTrue(root.Nodes.Count == 1 && root.Nodes[0].ChildCount == 3 && root.NextOffset == null &&
                root.Coverage().Path == "",
                "depth zero returns only the selected YAML node");
            AssertEqual("scalar", ResourceYamlTreeSelector.Select(new StringReader(source), "/ ", 0, 0, 1, 2000).NodeType,
                "whitespace keys remain addressable");
            AssertEqual("resource_yaml_pointer_invalid", RuntimeThrows<ResourceRequestException>(() =>
                ResourceYamlTreeSelector.Select(new StringReader(source), "/a~2b", 0, 0, 1, 2000)).ErrorCode,
                "invalid pointer escape fails explicitly");
            AssertEqual("resource_yaml_pointer_not_found", RuntimeThrows<ResourceRequestException>(() =>
                ResourceYamlTreeSelector.Select(new StringReader(source), "/a~1b/~0/01", 0, 0, 1, 2000)).ErrorCode,
                "noncanonical sequence index cannot select a value");
            const string unrelatedAlias = "base: &b {x: 1}\nuse: *b\nother: ok\n";
            var independent = ResourceYamlTreeSelector.Select(new StringReader(unrelatedAlias), "/other", 0, 0, 1, 2000);
            AssertTrue(independent.Nodes.Count == 1 && independent.Nodes[0].Value == "ok" &&
                independent.Coverage().Path == "/other",
                "an alias outside the selected pointer does not block exact YAML navigation");
            AssertEqual(3L, ResourceYamlTreeSelector.Select(new StringReader(unrelatedAlias), "", 0, 0, 1, 2000)
                .TotalChildren, "depth-zero mapping counts explicit keys without expanding alias values");
            foreach (var pointer in new[] { "/use", "/use/x" })
                AssertEqual("resource_yaml_alias_unsupported", RuntimeThrows<ResourceRequestException>(() =>
                    ResourceYamlTreeSelector.Select(new StringReader(unrelatedAlias), pointer, 0, 0, 1, 2000)).ErrorCode,
                    "a selected alias still fails rather than claiming source content");
            foreach (var invalid in new[] { "x: 1\nx: 2\n", "base: &b {x: 1}\nuse: *b\n",
                "base: &b {x: 1}\nuse:\n  <<: *b\n", "? [a, b]\n: c\n", "x: 1\n---\ny: 2\n" })
            {
                var expected = invalid.Contains("use: *b") ? "resource_yaml_alias_unsupported" :
                    invalid.Contains("<<:") ? "resource_yaml_merge_unsupported" :
                    invalid.StartsWith("? [") ? "resource_yaml_key_unsupported" :
                    invalid.Contains("---") ? "resource_yaml_multiple_documents" : "resource_yaml_pointer_ambiguous";
                AssertEqual(expected, RuntimeThrows<ResourceRequestException>(() =>
                    ResourceYamlTreeSelector.Select(new StringReader(invalid), "", 1, 0, 10, 2000)).ErrorCode,
                    "ambiguous YAML never produces tree evidence");
            }
            AssertEqual("resource_yaml_merge_unsupported", RuntimeThrows<ResourceRequestException>(() =>
                ResourceYamlTreeSelector.Select(new StringReader("use:\n  <<: {x: 1}\n"), "", 2, 0, 10, 2000)).ErrorCode,
                "merge syntax without aliases also fails explicitly");
            AssertEqual("scalar", ResourceYamlTreeSelector.Select(new StringReader("'<<': literal\n"), "/<<", 0, 0, 1,
                2000).NodeType, "quoted literal merge-looking keys stay addressable");
            AssertEqual("resource_yaml_invalid", RuntimeThrows<ResourceRequestException>(() =>
                ResourceYamlTreeSelector.Select(new StringReader("x: [one, two"), "/x", 1, 0, 1, 2000)).ErrorCode,
                "incomplete YAML cannot publish partial tree evidence");
            AssertEqual("resource_yaml_view_too_large", RuntimeThrows<ResourceRequestException>(() =>
                ResourceYamlTreeSelector.Select(new StringReader("x: " + new string('a', 5000)), "/x", 0, 0, 1, 500)).ErrorCode,
                "only selected output uses model budget");
            RuntimeThrows<OperationCanceledException>(() => ResourceYamlTreeSelector.Select(
                new StringReader(source), "", 1, 0, 10, 2000, new CancellationToken(true)));
        }

        private static void YamlTreeProviders()
        {
            WithTempPaths(paths =>
            {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "project"));
                var files = new WorkspaceFileService(paths);
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                using (var writer = new StreamWriter(Path.Combine(workspace.RootPath, "large.yaml"), false,
                    new UTF8Encoding(false, true)))
                {
                    var blank = new string(' ', 8192);
                    for (var i = 0; i < 512; i++) writer.Write(blank);
                    writer.Write("\nbase: &b {x: 1}\ncopy: *b\ndata: [one, two, three]\n");
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var selected = provider.ReadTree("large.yaml", "/data", 1, 1, 1, 2000);
                AssertTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 12L * 1024 * 1024 &&
                    selected.Result.Tree.Nodes.Any(node => node.Pointer == "/data/1") &&
                    selected.Evidence.Single().Coverage.Kind == ResourceCoverageKinds.YamlPath &&
                    !selected.Evidence.Single().Complete && selected.Result.Resource.Reference.IsExact,
                    "large YAML workspace file yields one revision-pinned partial tree without whole-source allocation");
                AssertEqual(JsonConvert.SerializeObject(selected.Result.Tree),
                    new ChatBlobStore(paths).ReadText(selected.Evidence.Single().Payload.ToBlobReference()),
                    "CAS stores selected YAML view, not source bytes");
                var handler = new WorkspaceResourceHandler(provider, null, () => null,
                    new Dictionary<string, ResourceEvidence>(), false, null, 2000);
                var execution = new ToolRuntimeFixture().Context(toolId: ResourceToolCatalog.ReadToolId);
                var result = handler.ExecuteAsync(new ToolHandlerContext(execution,
                    new Dictionary<string, object> { ["target"] = "large.yaml", ["representation"] = "tree",
                        ["pointer"] = "/data", ["offset"] = 1, ["limit"] = 1 }),
                    CancellationToken.None).GetAwaiter().GetResult();
                var visible = ModelResultProjection.ProjectResourceData(ResourceToolCatalog.ReadToolId,
                    JObject.Parse(result.Result.DataJson));
                AssertTrue((string)visible["tree"]["format"] == "yaml" &&
                    (string)visible["coverage"]["kind"] == ResourceCoverageKinds.YamlPath &&
                    result.ResourceEvidence.Single().ReadMetadata.RequestedCoverage.Kind == ResourceCoverageKinds.YamlPath,
                    "workspace model and saved request metadata agree on YAML path coverage");
            });
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var artifact = new ChatArtifact { Id = "yaml-tree-artifact", Kind = ChatArtifactKinds.File,
                    Title = "large.yml", MimeType = "application/yaml",
                    InlineText = "padding: " + new string('q', 40000) +
                        "\nbase: &b {x: 1}\ncopy: *b\ndata: [one, two, three]\n" };
                session.Artifacts.Add(artifact);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var descriptor = gateway.Resolve(session, ChatResourceUri.CreateArtifactRevisionUri(session, artifact)).Resource;
                AssertTrue(descriptor.Representations.Contains(ResourceRepresentations.Tree),
                    "complete YAML artifact advertises tree view");
                var selection = gateway.ReadTree(session, descriptor.Reference, descriptor, "/data", 1, 1, 1,
                    2000, CancellationToken.None);
                var evidence = gateway.Evidence(session, selection.Result).Single();
                AssertTrue(selection.Result.Tree.Format == "yaml" && selection.Result.Tree.Nodes.Any(node => node.Pointer == "/data/1") &&
                    evidence.Coverage.Kind == ResourceCoverageKinds.YamlPath && !evidence.Complete,
                    "paged Office YAML source retains one exact partial revision");
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToList(),
                    new AppSettings(), ChatModes.Agent, false);
                var execution = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = ResourceGatewayService.IntentTarget(descriptor),
                        ["representation"] = "tree", ["pointer"] = "/data", ["offset"] = 1, ["limit"] = 1 });
                var projected = ModelResultProjection.ProjectResourceData(ResourceToolCatalog.ReadToolId,
                    JObject.Parse(execution.Result.DataJson));
                AssertTrue(execution.Outcome == ToolExecutionOutcome.Ok &&
                    (string)projected["tree"]["format"] == "yaml" &&
                    (string)projected["coverage"]["kind"] == ResourceCoverageKinds.YamlPath &&
                    execution.ResourceEvidence.Single().ReadMetadata.RequestedCoverage.Kind == ResourceCoverageKinds.YamlPath,
                    "Office model route keeps YAML format and requested partial coverage");
            });
        }
    }
}
