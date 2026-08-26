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

        private static void ResourceContractsSeparateHeadAndRevision()
        {
            var revision = new ResourceRef(ResourceUri.Create("chat", "s1", "artifact", "a1", "revision", "2"), "2");
            var head = new ResourceHead
            {
                Uri = ResourceUri.Create("chat", "s1", "artifact", "a1"),
                Current = revision
            };
            AssertEqual("2", head.Current.Revision, "immutable revision token");
            AssertEqual(false, string.Equals(head.Uri, head.Current.Uri, StringComparison.Ordinal), "head differs from revision URI");
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
        }

        private static void ResourceToolsHardCutoverArtifactTools()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                foreach (var id in new[]
                {
                    ResourceToolExecutor.ListToolId,
                    ResourceToolExecutor.ResolveToolId,
                    ResourceToolExecutor.SearchToolId,
                    ResourceToolExecutor.ReadToolId
                })
                {
                    AssertTrue(tools.Any(tool => string.Equals(tool.Id, id, StringComparison.Ordinal)), id + " is published");
                }
                foreach (var id in new[] { "common.artifacts_list", "common.artifacts_search", "common.artifacts_read" })
                {
                    AssertTrue(tools.All(tool => !string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)), id + " is absent");
                    var result = executor.Execute(new ToolCommand { ToolId = id }, tools, new AppSettings(), false, false);
                    AssertEqual("unknown_tool", result.ErrorCode, id + " is not aliased");
                }
            });
        }

        private sealed class TestResourceProvider : IResourceProvider
        {
            public TestResourceProvider(string id)
            {
                Id = id;
            }

            public string Id { get; private set; }

            public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
            {
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
                throw new NotSupportedException();
            }

            public ResourceReadSelection Read(ChatSession session, string resourceUri, string representation, int offset, int maxChars)
            {
                throw new NotSupportedException();
            }
        }
    }
}
