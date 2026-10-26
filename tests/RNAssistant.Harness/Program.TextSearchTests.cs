using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
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
        private static void WorkspaceFindDefaultKeepsDirectories()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "find-default"));
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, "src"));
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, ".git"));
                File.WriteAllText(Path.Combine(workspace.RootPath, "src", "invoice.ts"), "needle");
                File.WriteAllText(Path.Combine(workspace.RootPath, ".git", "hidden.txt"), "hidden");
                var ports = typeof(RNAssistant.Runtime.WorkspaceConversationService).GetNestedType("WorkspacePorts",
                    System.Reflection.BindingFlags.NonPublic);
                var schema = (string)ports.GetMethod("ResourceSchema",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new object[] { true, true, false, 2000 });
                var provider = new WorkspaceFileResourceProvider(new WorkspaceFileService(paths), workspace);
                var handler = new RNAssistant.Runtime.WorkspaceResourceHandler(provider, null, () => null,
                    new Dictionary<string, ResourceEvidence>(), true, null, 2000);
                var fixture = new ToolRuntimeFixture(RuntimeRegistration("common.resources_find", schema: schema));
                fixture.Handler.Run = handler.ExecuteAsync;
                Func<string, string[]> find = arguments => {
                    var record = fixture.Runtime.ExecuteAsync(fixture.Context(arguments), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, "find succeeds through actual runtime argument normalization");
                    return JObject.Parse(record.Result.DataJson)["items"].Select(item => (string)item["target"]).ToArray();
                };
                AssertEqual("src", string.Join(",", find("{}")), "omitted selectors browse directories, excluding hidden paths");
                AssertEqual("src", string.Join(",", find("{\"type\":\"file\",\"recursive\":null,\"includeGlobs\":null,\"excludeGlobs\":null}")),
                    "strict optional nulls preserve ordinary directory browsing");
                AssertEqual(0, find("{\"recursive\":false}").Length, "explicit false still selects immediate files only");
                AssertEqual("src/invoice.ts", string.Join(",", find("{\"recursive\":true}")), "explicit recursion finds nested files only");
                AssertEqual("src/invoice.ts", string.Join(",", find("{\"directory\":\"src\",\"includeGlobs\":[\"**/*.ts\"]}")),
                    "explicit glob still selects files without injected recursion");
            });
        }

        private static void TextSearchWorkspaceAndContinuation()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "search-workspace"));
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, "src"));
                Directory.CreateDirectory(Path.Combine(workspace.RootPath, ".git"));
                File.WriteAllText(Path.Combine(workspace.RootPath, ".git", "hidden.txt"), "needle");
                File.WriteAllText(Path.Combine(workspace.RootPath, ".env"), "needle");
                File.WriteAllText(Path.Combine(workspace.RootPath, "src", "a.unknown"), "α😀 needle\r\nneedle again\rthird needle");
                File.WriteAllText(Path.Combine(workspace.RootPath, "src", "b.cs"), "void Other() {}\nneedle");
                if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var outside = Path.Combine(paths.Root, "outside.txt"); File.WriteAllText(outside, "needle");
                    File.CreateSymbolicLink(Path.Combine(workspace.RootPath, "linked.txt"), outside);
                }
                var provider = new WorkspaceFileResourceProvider(new WorkspaceFileService(paths), workspace);
                var request = new ResourceTextSearchRequest { Scope = "workspace", Recursive = true, Query = "needle", Limit = 1 };
                var engine = new ResourceTextSearch();
                Func<ResourceWithinSearchPage> search = () => engine.Search(request, provider.SearchSources(request, CancellationToken.None), CancellationToken.None);
                var first = search();
                AssertEqual("src/a.unknown", first.Matches.Single().Target, "unknown language uses text tools");
                AssertEqual(5, first.Matches.Single().StartColumn.Value, "UTF-16 Unicode column");
                AssertTrue(!first.ScanComplete && first.Continuation != null && first.ExactReferences.Single().IsExact, "bounded page has exact runtime pin");
                request.Continuation = first.Continuation;
                var second = search();
                AssertEqual(2, second.Matches.Single().StartLine.Value, "CRLF continuation neither duplicates nor skips");
                request.Continuation = second.Continuation;
                var third = search();
                AssertEqual(3, third.Matches.Single().StartLine.Value, "CR line coordinates");
                request.Continuation = third.Continuation;
                AssertEqual("src/b.cs", search().Matches.Single().Target, "cross-file continuation");
                request.Continuation = first.Continuation;
                AssertEqual("resource_search_stale", RuntimeThrows<ResourceRequestException>(() => search()).ErrorCode, "foreign/out-of-order continuation refused");
                request.Continuation = null; first = search(); request.Continuation = first.Continuation;
                File.AppendAllText(Path.Combine(workspace.RootPath, "src", "a.unknown"), "changed");
                AssertEqual("resource_search_stale", RuntimeThrows<ResourceRequestException>(() => search()).ErrorCode, "external edit refuses old coordinates");
                AssertEqual("resource_search_stale", RuntimeThrows<ResourceRequestException>(() => new ResourceTextSearch().Search(request,
                    provider.SearchSources(request, CancellationToken.None), CancellationToken.None)).ErrorCode, "restart has no inferred continuation authority");
                request.Continuation = null; request.IncludeGlobs = new[] { "**/*.unknown" }; request.Limit = 20;
                var filtered = search();
                AssertEqual(3, filtered.Matches.Count, "glob filters and hidden-file exclusions");
                AssertTrue(filtered.ScanComplete && filtered.SourceComplete, "complete selected scope");
                var discovered = provider.FindPaths("", "a", true, new[] { "**/*.unknown" }, null, CancellationToken.None);
                AssertEqual("src/a.unknown", discovered.Items.Single().Target, "recursive metadata discovery");
                AssertEqual(0, discovered.ResourceRefs.Count, "metadata discovery cannot grant read authority");
                request.ExcludeGlobs = new[] { "src/**" }; AssertEqual(0, search().Matches.Count, "explicit exclusions");
                using (var cancel = new CancellationTokenSource())
                { cancel.Cancel(); RuntimeThrows<OperationCanceledException>(() => provider.SearchSources(request, cancel.Token)); }
                request.Directory = "../";
                RuntimeThrows<WorkspaceFileException>(() => provider.SearchSources(request, CancellationToken.None));
            });
        }

        private static void TextSearchRegexAndCoverage()
        {
            var revision = new ResourceRef("rna://file/test", "r1");
            var body = "prefix\r\nCall(α)\nCall(β)\n";
            Func<ResourceSearchSources> sources = () => new ResourceSearchSources { Items = new List<ResourceSearchSource> {
                new ResourceSearchSource { Target = "any.xyz", Open = token => new ResourceSearchText { Reference = revision,
                    Reader = new StringReader(body) } } } };
            var request = new ResourceTextSearchRequest { Target = "any.xyz", Query = @"Call\([αβ]\)", Mode = "regex" };
            var result = new ResourceTextSearch().Search(request, sources(), CancellationToken.None);
            AssertEqual(2, result.Matches.Count, "language-independent regex");
            AssertEqual(2, result.Matches[0].StartLine.Value, "regex line coordinate");
            request.Query = "a+"; request.Limit = 1; body = "aaa";
            var nonoverlapping = new ResourceTextSearch();
            result = nonoverlapping.Search(request, sources(), CancellationToken.None);
            request.Continuation = result.Continuation;
            result = nonoverlapping.Search(request, sources(), CancellationToken.None);
            AssertEqual(0, result.Matches.Count, "regex continuation resumes after the matched span");
            AssertTrue(result.TotalMatchesExact && result.TotalMatches == 1, "regex quantifier does not duplicate suffixes");
            request.Continuation = null;
            request.Query = "[";
            RuntimeThrows<ResourceRequestException>(() => new ResourceTextSearch().Search(request, sources(), CancellationToken.None));
            request.Query = "needle"; request.Limit = 1;
            body = new string('x', ResourceTextSearch.MaximumLineCharacters + 1) + "\nneedle\nneedle";
            var engine = new ResourceTextSearch();
            result = engine.Search(request, sources(), CancellationToken.None);
            AssertTrue(result.IncompleteReasons.Contains("line_too_long") && result.Continuation != null, "long regex line is explicit incomplete coverage");
            request.Continuation = result.Continuation;
            result = engine.Search(request, sources(), CancellationToken.None);
            request.Continuation = result.Continuation;
            result = engine.Search(request, sources(), CancellationToken.None);
            AssertTrue(!result.ScanComplete && result.IncompleteReasons.Contains("line_too_long"), "later pages cannot hide earlier gaps");
            request.Continuation = null; request.Mode = "literal"; request.Limit = 20;
            body = new string('x', 100000) + "needle";
            AssertEqual(1, new ResourceTextSearch().Search(request, sources(), CancellationToken.None).Matches.Count, "literal search supports long lines");
            request.Mode = "regex"; request.Query = "(a+)+$"; body = new string('a', 16000) + "!";
            result = new ResourceTextSearch().Search(request, sources(), CancellationToken.None);
            AssertTrue(!result.ScanComplete && result.IncompleteReasons.Contains("regex_timeout"), "pathological regex has a bounded failure");
        }

        private static void TextSearchSchemasAndPresentation()
        {
            var schema = JObject.Parse(ResourceModelSearchSchema.Create(true, true)); string error;
            AssertTrue(ToolSchemaSupport.ValidateArguments(JObject.Parse("{\"scope\":\"workspace\",\"query\":\"x\",\"recursive\":true}"), schema, false, out error), "workspace variant");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(JObject.Parse("{\"scope\":\"workspace\",\"target\":\"a\",\"query\":\"x\"}"), schema, false, out error), "target/scope are disjoint");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(JObject.Parse("{\"scope\":\"vba\",\"directory\":\"src\",\"query\":\"x\"}"), schema, false, out error), "wrong provider selectors refused");
            AssertTrue(ToolSchemaSupport.ValidateArguments(JObject.Parse("{\"type\":\"office\",\"target\":\"Module1\",\"query\":\"x\"}"), schema, false, out error), "bound Office target variant");
            var strict = ToolSchemaSupport.ForStructuredOutput(schema, keepDescriptions: true);
            var strictArgs = new JObject { ["scope"] = "workspace", ["query"] = "needle" };
            foreach (var field in ((JObject)schema["anyOf"][1]["properties"]).Properties())
                if (strictArgs[field.Name] == null) strictArgs[field.Name] = JValue.CreateNull();
            AssertTrue(ToolSchemaSupport.ValidateArguments(strictArgs, strict, false, out error), "strict schema accepts complete nullable optional fields: " + error);
            var parsed = ResourceModelSearchSchema.Parse(strictArgs.ToObject<Dictionary<string, object>>());
            AssertEqual("literal", parsed.Mode, "strict null optional mode keeps runtime default");
            AssertEqual(20, parsed.Limit, "strict null optional limit keeps runtime default");
            var projected = ModelResultProjection.ProjectResourceData("common.resources_search", JObject.Parse(
                "{\"scope\":\"workspace\",\"query\":\"x\",\"scanComplete\":false,\"matchesComplete\":false,\"sourceComplete\":true,\"revision\":\"secret\",\"matches\":[{\"target\":\"a.js\",\"startLine\":3,\"startColumn\":2,\"snippet\":\"x\",\"uri\":\"rna://secret\"}],\"continuation\":{\"afterTarget\":\"a.js\",\"afterLine\":3,\"afterColumn\":2,\"revision\":\"secret\"}}"));
            AssertTrue(!projected.ToString().Contains("secret") && (int)projected["matches"][0]["startLine"] == 3, "closed projection keeps coordinates and strips authority");
            var blocks = new List<ToolResultBlockDto>(); ToolResultPresentationService.AppendContent(blocks, projected.ToString());
            var list = blocks.OfType<ToolListBlockDto>().Single();
            AssertTrue(!list.Complete && list.Items.Single().Detail.Contains("3"), "typed preview shows coordinates and incomplete coverage");
            foreach (var tool in VbaToolCatalog.GetTools().Where(x => x.Id != VbaToolCatalog.RunMacro))
                AssertTrue(ToolDisplayCatalog.Resolve(tool).TargetArguments.Contains("moduleName"), "actual VBA ID has module caption: " + tool.Id);
        }

        private static void TextSearchOfficeParity()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                adapter.SetVbaModule("SearchOne", "Sub Alpha()\r\n  Call Beta\r\nEnd Sub", "StdModule");
                adapter.SetVbaModule("SearchTwo", "Sub Beta()\nEnd Sub", "StdModule");
                var session = NewSession(adapter);
                var request = new ResourceTextSearchRequest { Scope = "vba", Query = @"Sub (Alpha|Beta)", Mode = "regex" };
                var page = executor.ResourceGateway.SearchText(session, request, new ResourceTextSearch(), CancellationToken.None);
                AssertEqual(2, page.Matches.Count, "VBA project regex finds both definitions");
                AssertTrue(page.ScanComplete && page.Matches.All(x => x.StartLine == 1) && page.ExactReferences.All(x => x.IsExact), "Office coordinates and exact runtime refs");
                var tools = executor.GetControllerTools().Where(x => x.Id == ResourceToolCatalog.SearchToolId).ToArray();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), ChatModes.Agent, false, manualRun: true);
                var call = new ToolCall("search-vba", ResourceToolCatalog.SearchToolId, "{\"scope\":\"vba\",\"query\":\"Call Beta\"}");
                var record = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolExecutionOutcome.Ok, record.Outcome, "native ToolRuntime admits new scoped contract");
                AssertEqual(1, JObject.Parse(record.Result.DataJson)["matches"].Count(), "native result retained");
                var catalog = executor.GetControllerTools().ToList();
                AssertTrue(executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.WriteFileToolId, "path", "index.html",
                    "content", "<main>needleHtml</main>"), catalog, new AppSettings(), false, false, session).Success, "publish HTML fixture");
                var html = executor.ResourceGateway.SearchText(session, new ResourceTextSearchRequest { Scope = "html", Query = "needleHtml", Mode = "regex" },
                    new ResourceTextSearch(), CancellationToken.None);
                AssertTrue(html.ScanComplete && html.Matches.Count >= 1 && html.Matches.All(x => x.StartLine.HasValue), "HTML scope uses the same matcher and coordinates");
            });
            var missing = new ResourceGatewayService(new IResourceProvider[] { new ChatArtifactResourceProvider() }).SearchText(new ChatSession(),
                new ResourceTextSearchRequest { Scope = "vba", Query = "anything" }, new ResourceTextSearch(), CancellationToken.None);
            AssertTrue(!missing.ScanComplete && missing.UnavailableScopes.Contains("vba"), "missing provider never proves an empty project");
        }

        // Reproducible tool-only experiment. This does not measure model task success.
        private static void TextSearchMixedProjectExperiment()
        {
            var files = Enumerable.Range(0, 500).ToDictionary(i => "noise/" + i.ToString("D4") + ".txt",
                i => new string('x', 1024), StringComparer.Ordinal);
            files.Add("src/report.unknown", "fn BuildReport(value) { return value; }");
            files.Add("src/billing.py", "total -= amount # rare_bug_marker");
            files.Add("web/bridge.js", "onClick = () => BuildReport(window.chrome.webview.postMessage(data));");
            files.Add("src/Bridge.cs", "void HandleMessage(string data) { RunVba(data); }");
            files.Add("vba/Bridge.bas", "Sub RunVba()\nApplication.Run macroName\nEnd Sub");
            foreach (var task in new[] {
                Tuple.Create("named-definition", "fn BuildReport", 1),
                Tuple.Create("behavioral-defect", "total -= amount", 1),
                Tuple.Create("signature-call-sites", @"BuildReport\(", 2),
                Tuple.Create("cross-language-candidates", @"postMessage|HandleMessage|Application\.Run", 3),
                Tuple.Create("dynamic-event-candidates", @"onClick|Application\.Run", 2),
                Tuple.Create("sparse-defect", "rare_bug_marker", 1) })
            {
                var opens = 0;
                var sources = new ResourceSearchSources { Items = files.Select(pair => new ResourceSearchSource {
                    Target = pair.Key, Open = token => { opens++; return new ResourceSearchText {
                        Reader = new StringReader(pair.Value), Reference = new ResourceRef("rna://fixture/" + pair.Key, "r1") }; }
                }).ToList() };
                var request = new ResourceTextSearchRequest { Scope = "workspace", Recursive = true, Query = task.Item2, Mode = "regex" };
                var engine = new ResourceTextSearch();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var calls = 0; var delivered = 0; var hits = new List<ResourceWithinSearchItem>();
                ResourceWithinSearchPage result;
                do
                {
                    result = engine.Search(request, sources, CancellationToken.None); calls++;
                    delivered += System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(result));
                    hits.AddRange(result.Matches); request.Continuation = result.Continuation;
                    AssertTrue(calls <= 10, "bounded progress through sparse mixed project");
                } while (request.Continuation != null);
                AssertEqual(task.Item3, hits.Count, task.Item1 + " expected candidates");
                AssertTrue(result.ScanComplete && result.TotalMatchesExact && result.TotalMatches == hits.Count,
                    "final total is cumulative and exact only after complete traversal");
                Console.WriteLine("METRIC text-search task=" + task.Item1 + " calls=" + calls + " sourceOpens=" + opens +
                    " deliveredBytes=" + delivered + " sourceCharacters=" + files.Values.Sum(x => x.Length) +
                    " elapsedMs=" + clock.ElapsedMilliseconds + " hits=" + hits.Count);
            }
        }
    }
}
