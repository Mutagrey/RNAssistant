using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.ModelProtocol;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ResourceUriRoundTripsCanonicalAddress()
        {
            var exactSheet = ResourceUri.Create("excel", "workbook", "range", " Sales Report ", "A1");
            AssertContains(exactSheet, "/%20Sales%20Report%20/", "significant name whitespace is escaped");
            AssertEqual(" Sales Report ", ResourceUri.Parse(exactSheet).Segments[2], "resource names round-trip without trimming");
            AssertTrue(exactSheet != ResourceUri.Create("excel", "workbook", "range", "Sales Report", "A1"),
                "names differing in boundary whitespace have distinct resource identities");
            var uri = ResourceUri.Create("chat", "session 1", "artifact", "image.png");
            AssertEqual("rna://chat/session%201/artifact/image.png", uri, "canonical resource URI");
            var parsed = ResourceUri.Parse(uri);
            AssertEqual("chat", parsed.Provider, "resource provider");
            AssertEqual("session 1", parsed.Segments[0], "decoded segment");
            AssertEqual("image.png", parsed.Segments[2], "final segment");
        }

        private static void ResourceUriRejectsAmbiguousAddresses()
        {
            ResourceAddress ignored;
            AssertEqual(false, ResourceUri.TryParse("rna://chat/a/../b", out ignored), "dot segment rejected");
            AssertEqual(false, ResourceUri.TryParse("rna://chat/a%2Fb", out ignored), "encoded slash rejected");
            AssertEqual(false, ResourceUri.TryParse("rna://chat/a?revision=2", out ignored), "query rejected");
            AssertEqual(false, ResourceUri.TryParse("RNA://chat/a", out ignored), "non-canonical scheme rejected");
        }

        private static void ResourceProjectionPreservesLiteralSource()
        {
            var exact = new ResourceRef("rna://chat/private/source", "r_private");
            var literal = "const example = 'rna://literal/in/source';";
            var result = RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read " + exact.Uri,
                new JObject { ["target"] = "Source file", ["text"] = literal,
                    ["startLine"] = 40, ["requestedLines"] = 50, ["selectionComplete"] = true,
                    ["offset"] = 32768, ["coverage"] = new JObject {
                        ["kind"] = ResourceCoverageKinds.CharacterRange, ["start"] = 32768,
                        ["end"] = 32780, ["revision"] = exact.Revision, ["uri"] = exact.Uri },
                    ["sourceCoverage"] = new JObject { ["kind"] = ResourceCoverageKinds.LineRange,
                        ["start"] = 40, ["end"] = 90, ["uri"] = exact.Uri, ["revision"] = exact.Revision },
                    ["sourceTotalLines"] = 240, ["sourceEndChar"] = 64000,
                    ["sourceComplete"] = true, ["complete"] = false }.ToString(), new[] { exact });
            var source = RNAssistant.Office.Services.AgentJsonProtocol.CreateToolResultMessage(
                new ToolInvocation { ToolId = "common.resources_read", ToolCallId = "projection-source" },
                new ToolResultMaterialization(result));
            foreach (var projection in new[] { new ModelResultProjection().Project(source),
                RNAssistant.Office.Services.ModelToolResultProjection.Project(source) })
            {
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(projection, out wire, out error), "projected read is valid wire");
                var data = ToolResultWire.ParseData(wire.Result.DataJson);
                AssertEqual(literal, (string)data["text"], "literal source is never redacted");
                AssertEqual(40, (int)data["startLine"], "requested line start remains visible");
                AssertEqual(50, (int)data["requestedLines"], "requested line count remains visible");
                AssertEqual(true, (bool)data["selectionComplete"], "selected excerpt is complete");
                AssertEqual(ResourceCoverageKinds.CharacterRange, (string)data["coverage"]["kind"],
                    "actual coverage kind remains visible");
                AssertEqual(32768, (int)data["coverage"]["start"], "actual range start remains visible");
                AssertEqual(32780, (int)data["coverage"]["end"], "actual range end remains visible");
                AssertEqual(90, (int?)data["sourceCoverage"]?["end"],
                    "source continuation survives the final model projection");
                AssertEqual(240, (int?)data["sourceTotalLines"], "source line bounds remain visible");
                AssertEqual(64000, (int?)data["sourceEndChar"], "source character bounds remain visible");
                AssertTrue(data["sourceCoverage"]["uri"] == null && data["sourceCoverage"]["revision"] == null,
                    "source coverage does not disclose runtime references");
                AssertTrue(data["offset"] == null && data["coverage"]["uri"] == null &&
                    data["coverage"]["revision"] == null && !wire.Result.Message.Contains(exact.Uri),
                    "runtime offset and exact references stay private");
            }
            foreach (var variant in new[] {
                new { Name = "common.resources_search", Data = new JObject {
                    ["target"] = "Source file", ["matches"] = new JArray(new JObject { ["snippet"] = literal,
                        ["partNumber"] = 1, ["reference"] = exact.Uri }) } },
                new { Name = "common.resources_outline", Data = new JObject {
                    ["target"] = "Source file", ["parts"] = new JArray(new JObject { ["partNumber"] = 1,
                        ["symbols"] = new JArray(new JObject { ["name"] = literal, ["kind"] = "heading", ["reference"] = exact.Uri }) }) } }
            })
            {
                var materialized = RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read " + exact.Uri,
                    variant.Data.ToString(), new[] { exact });
                var tool = RNAssistant.Office.Services.AgentJsonProtocol.CreateToolResultMessage(
                    new ToolInvocation { ToolId = variant.Name, ToolCallId = "projection-literal" },
                    new ToolResultMaterialization(materialized));
                foreach (var projection in new[] { new ModelResultProjection().Project(tool),
                    RNAssistant.Office.Services.ModelToolResultProjection.Project(tool) })
                {
                    ToolResultWireReadResult wire; string error;
                    AssertTrue(ToolResultHistoryReader.TryRead(projection, out wire, out error), "projected navigation is valid wire");
                    var data = ToolResultWire.ParseData(wire.Result.DataJson);
                    var body = variant.Name == "common.resources_search"
                        ? (string)data["matches"][0]["snippet"] : (string)data["parts"][0]["symbols"][0]["name"];
                    AssertEqual(literal, body, "source-owned snippets and headings preserve literal URI-shaped text");
                    AssertTrue(!wire.Result.DataJson.Contains(exact.Uri) && !wire.Result.Message.Contains(exact.Uri),
                        "runtime reference fields do not reach the model");
                }
            }
        }

        private static void ResourceTableProjectionDistinguishesSelection()
        {
            var result = new ResourceReadResult {
                Representation = "table", Offset = 1,
                Table = new ResourceTableBatch { TotalRows = 4,
                    Rows = new IDictionary<string, object>[] {
                        new Dictionary<string, object> { ["value"] = 2 } } },
                Coverage = new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: 1, end: 2),
                Complete = false, Truncated = true
            };
            var projection = ResourceReadProjection.FromTable(result, "Records", "file", "document", 1, 2);
            var visible = ModelResultProjection.ProjectResourceData("common.resources_read", JObject.FromObject(projection));
            AssertTrue((bool)visible["selectionComplete"] == false && !(bool)visible["complete"] &&
                (int)visible["requestedOffset"] == 1 && (int)visible["requestedRows"] == 2 &&
                (int)visible["coverage"]["end"] == 2,
                "a byte-limited row batch does not claim the requested selection is complete");
            result.Coverage = new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: 0, end: 1);
            RuntimeThrows<InvalidOperationException>(() => ResourceReadProjection.FromTable(
                result, "Records", "file", "document", 1, 2));
        }

        private static void ResourceReferencePinsRevision()
        {
            var revision = new ResourceRef(ResourceUri.Create("chat", "s1", "artifact", "a1", "revision", "2"), "2");
            AssertEqual("2", revision.Revision, "immutable revision token");
            AssertContains(revision.Uri, "/revision/2", "canonical reference pins the revision in its URI");

            string sessionId;
            string artifactId;
            int parsedRevision;
            AssertEqual(false, ChatResourceUri.TryParseArtifactRevision(
                new ResourceRef(ResourceUri.Create("chat", "s1", "artifact", "a1", "revision", "02"), "02"),
                out sessionId,
                out artifactId,
                out parsedRevision), "semantically non-canonical revision rejected");
        }

        private static void ResourceGatewayRejectsDuplicateProviders()
        {
            var rejected = false;
            try
            {
                new ResourceProviderRouter<IResourceProvider>(new IResourceProvider[]
                {
                    new ChatArtifactResourceProvider(),
                    new ChatArtifactResourceProvider()
                });
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            AssertTrue(rejected, "duplicate provider id is rejected");
        }

        private static void ResourceGatewayDiscoversProvidersBeforeListing()
        {
            var gateway = new ResourceGatewayService(new IResourceProvider[]
            {
                new TestResourceProvider("zeta"),
                new TestResourceProvider("alpha")
            });
            var discovery = gateway.List(new ChatSession(), null, null, null, 20);
            AssertEqual(0, discovery.Items.Count, "provider discovery does not mix resource pages");
            AssertEqual("alpha", discovery.Providers[0], "providers are canonical and ordered");
            AssertEqual("zeta", discovery.Providers[1], "all providers are discoverable");

            var selected = gateway.List(new ChatSession(), "alpha", null, null, 20);
            AssertEqual("alpha", selected.Provider, "selected provider is recorded");
            AssertEqual("rna://alpha/item", selected.Items.Single().Reference.Uri, "selected provider lists resources");

            var semanticGateway = new ResourceGatewayService(new[]
            {
                new ChatArtifactResourceProvider()
            });
            var empty = semanticGateway.Find(new ChatSession(), null, "conversation");
            AssertEqual(true, empty.Empty,
                "semantic find identifies a genuinely empty available scope");
            AssertEqual(true, empty.Complete, "empty available scope is complete");
            AssertEqual(0, empty.UnavailableScopes.Count,
                "true empty is not provider discovery or unavailability");

            var unavailable = semanticGateway.Find(new ChatSession(), null, "vba");
            AssertEqual(false, unavailable.Empty,
                "missing semantic scope is not reported as true empty");
            AssertEqual(false, unavailable.Complete,
                "missing semantic scope is explicitly incomplete");
            AssertTrue(unavailable.UnavailableScopes.Contains("vba"),
                "missing VBA provider is reported through its semantic scope");

            var chat = new TestResourceProvider("chat");
            var unrelatedVba = new TestResourceProvider("vba", true);
            var scopedGateway = new ResourceGatewayService(new IResourceProvider[]
            {
                chat,
                unrelatedVba
            });
            var scopedFind = scopedGateway.Find(new ChatSession(), null, "conversation");
            var scopedTarget = scopedFind.Items.Single().Target;
            AssertTrue(scopedFind.Complete && !scopedFind.Partial,
                "an unavailable unrelated provider does not make scoped find partial");
            AssertEqual(0, unrelatedVba.ListCalls,
                "scoped find does not enumerate an unrelated provider");
            AssertEqual("rna://chat/item",
                scopedGateway.ResolveIntentTarget(new ChatSession(), scopedTarget).Reference.Uri,
                "semantic read resolution uses the target scope");
            AssertEqual(0, unrelatedVba.ListCalls,
                "semantic read resolution does not enumerate an unrelated provider");

            var created = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var first = new ChatArtifact
            {
                Id = "stable-first",
                Kind = ChatArtifactKinds.Markdown,
                Title = "Repeated title",
                InlineText = "first",
                CreatedUtc = created.AddTicks(1)
            };
            var duplicateSession = new ChatSession
            {
                Artifacts = new System.Collections.Generic.List<ChatArtifact> { first }
            };
            var duplicateGateway = new ResourceGatewayService();
            var stableTarget = duplicateGateway.Find(
                duplicateSession, null, "conversation").Items.Single().Target;
            duplicateSession.Artifacts.Add(new ChatArtifact
            {
                Id = "stable-second",
                Kind = ChatArtifactKinds.Markdown,
                Title = first.Title,
                InlineText = "second",
                CreatedUtc = created.AddTicks(2)
            });
            var duplicates = duplicateGateway.Find(
                duplicateSession, null, "conversation").Items;
            AssertEqual(2, duplicates.Select(item => item.Target)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "duplicate human titles retain distinct stable targets");
            AssertEqual(ChatResourceUri.CreateArtifactRevisionUri(duplicateSession, first),
                duplicateGateway.ResolveIntentTarget(
                    duplicateSession, stableTarget).Reference.Uri,
                "a target remains bound when a duplicate title is added");
            AssertTrue(duplicates.All(item =>
                    item.Target.IndexOf("rna://", StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.Target.IndexOf("stable-", StringComparison.OrdinalIgnoreCase) < 0),
                "stable duplicate targets expose no runtime identity");
        }

        private static void ResourceToolsHardCutoverArtifactTools()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                foreach (var id in new[]
                {
                    ResourceToolCatalog.FindToolId,
                    ResourceToolCatalog.ReadToolId
                })
                {
                    AssertTrue(tools.Any(tool => string.Equals(tool.Id, id, StringComparison.Ordinal)), id + " is published");
                }
                foreach (var id in new[] { "common.artifacts_list", "common.artifacts_search", "common.artifacts_read" })
                {
                    AssertTrue(tools.All(tool => !string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)), id + " is absent");
                    var result = executor.ExecuteManual(new ToolInvocation { ToolId = id }, tools, new AppSettings(), false, false);
                    AssertEqual("unknown_tool", result.ErrorCode, id + " is not aliased");
                }
            });
        }

        private static void ResourceIntentDiscoveryPreservesCoverage()
        {
            var incomplete = true;
            var paged = false;
            var empty = false;
            var provider = new TestResourceProvider("alpha");
            provider.ListPage = cursor =>
            {
                var second = cursor == "next";
                var next = paged && !second ? "next" : null;
                return new ResourceListPage {
                    Items = empty ? new System.Collections.Generic.List<ResourceDescriptor>() :
                        new System.Collections.Generic.List<ResourceDescriptor> { new ResourceDescriptor {
                            Reference = new ResourceRef(ResourceUri.Create("alpha", second ? "second" : "first"), "1"),
                            Provider = "alpha", Kind = "test", Title = second ? "Second" : "Test" } },
                    Total = empty ? 0 : 2, NextCursor = next, Truncated = next != null || incomplete };
            };
            var gateway = new ResourceGatewayService(new[] { provider });
            var session = new ChatSession();
            var clipped = gateway.Find(session, "Test", "conversation");
            AssertEqual(1, clipped.Items.Count, "captured matches remain discoverable");
            AssertTrue(!clipped.Complete && !clipped.Empty,
                "terminal truncation survives metadata filtering even with one result");
            AssertTrue(!clipped.Partial && clipped.UnavailableScopes.Count == 0, "bounded coverage is not provider unavailability");
            var target = clipped.Items.Single().Target;
            var refusal = RuntimeThrows<ResourceRequestException>(() => gateway.ResolveIntentTarget(session, target));
            AssertEqual("resource_scope_incomplete", refusal.ErrorCode, "partial enumeration cannot prove a unique semantic target");
            AssertTrue(!refusal.Retryable, "no automatic retry of an incomplete catalog");
            AssertTrue(!gateway.Find(session, "missing", "conversation").Empty, "zero observed matches are not proof of absence");
            empty = true;
            AssertTrue(!gateway.Find(session, null, "conversation").Empty, "an empty truncated capture is not an empty scope");
            empty = false;
            provider.SearchMatch = new ResourceSearchMatch
            {
                Reference = new ResourceRef(ResourceUri.Create("alpha", "hidden"), "7"),
                Kind = "test",
                Title = "Hidden",
                Representation = ResourceRepresentations.Text,
                Description = "Purpose retained from the searched revision",
                SectionTitle = "Relevant section",
                Snippet = "needle"
            };
            var hidden = gateway.Find(session, "needle", "conversation");
            AssertEqual("Purpose retained from the searched revision", hidden.Items.Single(item => item.Title == "Hidden").Description,
                "search-only descriptors preserve authored descriptions without listing metadata");
            AssertEqual("Relevant section", hidden.Items.Single(item => item.Title == "Hidden").SectionTitle,
                "search-only candidates retain section context");
            AssertTrue(hidden.Items.Any(item => item.Target == "conversation resource: Hidden"),
                "provider search result beyond the bounded listing remains discoverable");
            AssertEqual("7", hidden.Items.Single(item => item.Target == "conversation resource: Hidden").Reference.Revision,
                "search-discovered target carries the provider exact revision");
            AssertEqual("rna://alpha/hidden",
                gateway.ResolveIntentTarget(session, "conversation resource: Hidden").Reference.Uri,
                "search-discovered target resolves directly even when listing remains incomplete");
            provider.SearchMatch.CreatedUtc = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
            var dated = gateway.Find(session, "needle", "conversation").Items.Single(item => item.Title == "Hidden");
            AssertContains(dated.Target, "[created 2026-09-07T10:00:00.0000000Z]",
                "search-only descriptor preserves semantic target disambiguation metadata");
            AssertEqual("rna://alpha/hidden", gateway.ResolveIntentTarget(session, dated.Target).Reference.Uri,
                "dated search-only target is resolvable without a different follow-up target");
            provider.SearchMatch = null;
            empty = false; paged = true; incomplete = false;
            var calls = provider.ListCalls;
            var complete = gateway.Find(session, "Test", "conversation");
            AssertEqual(2, provider.ListCalls - calls, "ordinary pagination is fully consumed");
            AssertTrue(complete.Complete, "intermediate page truncation is resolved by its continuation");
            AssertEqual("rna://alpha/first", gateway.ResolveIntentTarget(session, target).Reference.Uri, "complete enumeration still resolves normally");
            AssertTrue(gateway.Find(session, "missing", "conversation").Empty, "complete negative discovery remains genuinely empty");
            incomplete = true;
            AssertTrue(!gateway.Find(session, "Test", "conversation").Complete, "terminal truncation also survives multi-page discovery");
            var mixed = new ResourceGatewayService(new IResourceProvider[] { provider, new TestResourceProvider("zeta") });
            AssertTrue(!mixed.Find(session, null, "conversation").Complete, "a later complete provider cannot erase earlier truncation");
            incomplete = false;
            provider.ListPage = cursor => new ResourceListPage {
                Items = new System.Collections.Generic.List<ResourceDescriptor> { new ResourceDescriptor {
                    Reference = new ResourceRef(ResourceUri.Create("alpha", "same"), "1"), Kind = "test", Title = "Same" } },
                NextCursor = cursor == "next" ? null : "next", UnavailableResources = cursor == "next" ? 0 : 1 };
            var partial = gateway.Find(session, null, "conversation");
            AssertTrue(partial.Items.Count == 1 && partial.Partial && !partial.Complete, "later healthy pages cannot erase an earlier unavailable resource");
            AssertEqual("resource_scope_incomplete", RuntimeThrows<ResourceRequestException>(() =>
                gateway.ResolveIntentTarget(session, partial.Items.Single().Target)).ErrorCode, "partial healthy pages cannot prove uniqueness");
            provider.ListPage = cursor => new ResourceListPage { NextCursor = "next", Truncated = true };
            var beforePages = provider.ListCalls;
            var bounded = gateway.Find(session, null, "conversation");
            AssertTrue(provider.ListCalls - beforePages == 20 && !bounded.Complete && !bounded.Empty,
                "empty filtered source pages cannot drive an unbounded scan or a complete negative");
            provider.SearchTruncated = true;
            AssertTrue(!gateway.Find(session, "missing", "conversation").Empty, "an incomplete content scan cannot prove absence either");
        }

        private sealed class TestResourceProvider : IResourceProvider
        {
            private readonly bool _failList;

            public TestResourceProvider(string id, bool failList = false)
            {
                Id = id;
                _failList = failList;
            }

            public string Id { get; private set; }
            public int ListCalls { get; private set; }
            internal Func<string, ResourceListPage> ListPage { get; set; }
            internal bool SearchTruncated { get; set; }
            internal ResourceSearchMatch SearchMatch { get; set; }
            internal Func<ChatSession, ResourceReadRequest, ResourceReadSelection> ReadRequest { get; set; }

            public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
            {
                ListCalls++;
                if (_failList)
                    throw new InvalidOperationException("provider unavailable");
                if (ListPage != null) return ListPage(cursor);
                return new ResourceListPage
                {
                    Items = new System.Collections.Generic.List<ResourceDescriptor>
                    {
                        new ResourceDescriptor
                        {
                            Reference = new ResourceRef(ResourceUri.Create(Id, "item"), "1"),
                            Provider = Id,
                            Kind = "test",
                            Title = "Test"
                        }
                    },
                    Total = 1,
                    Truncated = false
                };
            }

            public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
            {
                throw new NotSupportedException();
            }

            public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
            {
                var hasSearchMatch = SearchMatch != null &&
                    ((SearchMatch.Title ?? string.Empty).IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (SearchMatch.Snippet ?? string.Empty).IndexOf(query ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
                if (ListPage != null) return new ResourceSearchResult
                {
                    Query = query,
                    ScanTruncated = SearchTruncated,
                    Matches = hasSearchMatch
                        ? new System.Collections.Generic.List<ResourceSearchMatch> { SearchMatch }
                        : new System.Collections.Generic.List<ResourceSearchMatch>()
                };
                throw new NotSupportedException();
            }

            public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                if (ReadRequest != null) return ReadRequest(session, request);
                throw new NotSupportedException();
            }
        }
    }
}
