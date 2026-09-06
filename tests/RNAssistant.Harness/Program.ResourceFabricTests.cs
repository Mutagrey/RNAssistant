using RNAssistant.Core.Tools;
using System;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ResourceUriRoundTripsCanonicalAddress()
        {
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

        private static void ResourceRegistryRejectsDuplicateProviders()
        {
            var rejected = false;
            try
            {
                new ResourceProviderRegistry(new IResourceProvider[]
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
            AssertTrue(!clipped.Complete && clipped.RefineQuery && !clipped.Empty,
                "terminal truncation survives metadata filtering even with one result");
            AssertTrue(!clipped.Partial && clipped.UnavailableScopes.Count == 0, "bounded coverage is not provider unavailability");
            var target = clipped.Items.Single().Target;
            var refusal = RuntimeThrows<ResourceRequestException>(() => gateway.ResolveIntentTarget(session, target));
            AssertEqual("resource_scope_incomplete", refusal.ErrorCode, "partial enumeration cannot prove a unique semantic target");
            AssertTrue(!refusal.Retryable, "no automatic retry of an incomplete catalog");
            AssertTrue(!gateway.Find(session, "missing", "conversation").Empty, "zero observed matches are not proof of absence");
            empty = true;
            AssertTrue(!gateway.Find(session, null, "conversation").Empty, "an empty truncated capture is not an empty scope");
            empty = false; paged = true; incomplete = false;
            var calls = provider.ListCalls;
            var complete = gateway.Find(session, "Test", "conversation");
            AssertEqual(2, provider.ListCalls - calls, "ordinary pagination is fully consumed");
            AssertTrue(complete.Complete && !complete.RefineQuery, "intermediate page truncation is resolved by its continuation");
            AssertEqual("rna://alpha/first", gateway.ResolveIntentTarget(session, target).Reference.Uri, "complete enumeration still resolves normally");
            AssertTrue(gateway.Find(session, "missing", "conversation").Empty, "complete negative discovery remains genuinely empty");
            incomplete = true;
            AssertTrue(!gateway.Find(session, "Test", "conversation").Complete, "terminal truncation also survives multi-page discovery");
            var mixed = new ResourceGatewayService(new IResourceProvider[] { provider, new TestResourceProvider("zeta") });
            AssertTrue(!mixed.Find(session, null, "conversation").Complete, "a later complete provider cannot erase earlier truncation");
            incomplete = false; provider.SearchTruncated = true;
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
                if (ListPage != null) return new ResourceSearchResult { Query = query, ScanTruncated = SearchTruncated };
                throw new NotSupportedException();
            }

            public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
            {
                throw new NotSupportedException();
            }
        }
    }
}
