using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceReadSelection
    {
        public ResourceReadResult Result { get; set; }
        public IReadOnlyList<ChatAttachment> ModelAttachments { get; set; }
        public IReadOnlyList<ResourceRef> ResourceRefs { get; set; }
        // Exact text already verified by the retained reader, owned by this read
        // operation only. Whole/line consumers reuse it without paging through CAS.
        [Newtonsoft.Json.JsonIgnore]
        public string RetainedText { get; set; }

        public ResourceReadSelection()
        {
            ModelAttachments = new ChatAttachment[0];
            ResourceRefs = new ResourceRef[0];
        }
    }

    internal interface IResourceProvider : IResourceProviderIdentity
    {
        ResourceListPage List(ChatSession session, string kind, string cursor, int limit);
        ResourceDescriptor Resolve(ChatSession session, string resourceUri);
        ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch);
        ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken));
    }

    // Optional provider pushdown. The result is an exact selected subresource;
    // its SourceCoverage names the corresponding span in the larger live source.
    internal interface IResourceCharacterWindowProvider
    {
        ResourceReadSelection ReadCharacterWindow(ChatSession session, ResourceRef reference,
            string representation, int startChar, int charCount,
            System.Threading.CancellationToken cancellationToken);
    }

    internal sealed class ResourceTextBounds
    {
        public int StartChar { get; set; }
        public int EndChar { get; set; }
    }

    internal interface IResourceTextBoundsProvider
    {
        ResourceTextBounds ReadTextBounds(ChatSession session, ResourceRef reference,
            string representation, System.Threading.CancellationToken cancellationToken);
    }

    internal interface IResourceLineWindowProvider
    {
        ResourceReadSelection ReadLineWindow(ChatSession session, ResourceRef reference,
            string representation, int startLine, int lineCount,
            System.Threading.CancellationToken cancellationToken);
    }

    internal interface IResourceIdentityResolver
    {
        ResourceRef ResolveIdentity(ChatSession session, ResourceIdentity identity);
    }

    // Capture only. Exact-view retention, publication and byte transport belong to Gateway.
    internal interface IResourceRawSource
    {
        byte[] ReadRawSource(ChatSession session, ResourceRef reference);
    }

    internal interface IResourceMemberResolver
    {
        ResourceDescriptor ResolveMember(ChatSession session, string parentUri,
            string memberPath, string memberType);
    }

    internal interface ILiveOfficeResourceProvider : IResourceProvider
    {
    }
}
