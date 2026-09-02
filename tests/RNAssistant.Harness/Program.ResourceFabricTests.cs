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

            public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
            {
                throw new NotSupportedException();
            }
        }
    }
}
