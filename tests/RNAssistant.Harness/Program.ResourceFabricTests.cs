using System;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

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
    }
}
