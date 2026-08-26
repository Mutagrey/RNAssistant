using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

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
        public IReadOnlyList<string> ArtifactIds { get; set; }
        public IReadOnlyList<string> ResourceUris { get; set; }

        public ResourceReadSelection()
        {
            ModelAttachments = new ChatAttachment[0];
            ArtifactIds = new string[0];
            ResourceUris = new string[0];
        }
    }

    internal interface IResourceProvider
    {
        string Id { get; }
        ResourceListPage List(ChatSession session, string kind, string cursor, int limit);
        ResourceDescriptor Resolve(ChatSession session, string resourceUri);
        ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch);
        ResourceReadSelection Read(ChatSession session, string resourceUri, string representation, int offset, int maxChars);
    }

    internal interface ILiveOfficeResourceProvider : IResourceProvider
    {
    }

    internal sealed class ResourceProviderRegistry
    {
        private readonly IDictionary<string, IResourceProvider> _providers;

        public ResourceProviderRegistry(IEnumerable<IResourceProvider> providers)
        {
            _providers = new Dictionary<string, IResourceProvider>(StringComparer.Ordinal);
            foreach (var provider in providers ?? new IResourceProvider[0])
            {
                if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
                {
                    throw new ArgumentException("Resource providers must have a canonical id.", "providers");
                }
                var id = provider.Id.Trim().ToLowerInvariant();
                if (!string.Equals(id, provider.Id, StringComparison.Ordinal) || _providers.ContainsKey(id))
                {
                    throw new InvalidOperationException("Duplicate or non-canonical resource provider: " + provider.Id);
                }
                _providers.Add(id, provider);
            }
            if (_providers.Count == 0) throw new ArgumentException("At least one resource provider is required.", "providers");
        }

        public IReadOnlyList<IResourceProvider> All()
        {
            return _providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal).ToList();
        }

        public IResourceProvider Get(string providerId)
        {
            IResourceProvider provider;
            providerId = (providerId ?? string.Empty).Trim().ToLowerInvariant();
            if (providerId.Length == 0 || !_providers.TryGetValue(providerId, out provider))
            {
                throw new KeyNotFoundException("Unknown resource provider: " + providerId);
            }
            return provider;
        }
    }
}
