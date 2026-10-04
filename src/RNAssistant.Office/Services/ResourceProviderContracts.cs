using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal sealed class ResourceRequestException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public bool Retryable { get; private set; }

        public ResourceRequestException(string message, string errorCode, bool retryable)
            : base(message)
        {
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "resource_request_invalid" : errorCode;
            Retryable = retryable;
        }
    }

    internal sealed class ResourceReadSelection
    {
        public ResourceReadResult Result { get; set; }
        public IReadOnlyList<ChatAttachment> ModelAttachments { get; set; }
        public IReadOnlyList<ResourceRef> ResourceRefs { get; set; }

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
        ResourceReadSelection Read(ChatSession session, ResourceReadRequest request);
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
