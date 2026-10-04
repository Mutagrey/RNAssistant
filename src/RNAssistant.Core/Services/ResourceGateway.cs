using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Services
{
    public interface IResourceProviderIdentity
    {
        string Id { get; }
    }

    // Shared provider routing. A host gateway retains its own authority and read guards.
    public sealed class ResourceGateway<TProvider> where TProvider : IResourceProviderIdentity
    {
        private readonly IDictionary<string, TProvider> _providers;

        public ResourceGateway(IEnumerable<TProvider> providers)
        {
            _providers = new Dictionary<string, TProvider>(StringComparer.Ordinal);
            foreach (var provider in providers ?? Enumerable.Empty<TProvider>())
            {
                if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
                    throw new ArgumentException("Resource providers must have a canonical id.", nameof(providers));
                var id = provider.Id.Trim().ToLowerInvariant();
                if (!string.Equals(id, provider.Id, StringComparison.Ordinal) || _providers.ContainsKey(id))
                    throw new InvalidOperationException("Duplicate or non-canonical resource provider: " + provider.Id);
                _providers.Add(id, provider);
            }
            if (_providers.Count == 0)
                throw new ArgumentException("At least one resource provider is required.", nameof(providers));
        }

        public IReadOnlyList<TProvider> All()
        {
            return _providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal).ToList();
        }

        public TProvider Get(string providerId)
        {
            TProvider provider;
            providerId = (providerId ?? string.Empty).Trim().ToLowerInvariant();
            if (providerId.Length == 0 || !_providers.TryGetValue(providerId, out provider))
                throw new KeyNotFoundException("Unknown resource provider: " + providerId);
            return provider;
        }

        public TProvider Select(string providerId)
        {
            if (!string.IsNullOrWhiteSpace(providerId)) return Get(providerId);
            if (_providers.Count == 1) return _providers.Values.Single();
            throw new InvalidOperationException("provider is required when more than one resource provider is available.");
        }

        public TProvider ForUri(string resourceUri)
        {
            ResourceAddress address;
            if (!ResourceUri.TryParse(resourceUri, out address))
                throw new FormatException("Runtime preparation requires one canonical resource reference.");
            return Get(address.Provider);
        }
    }
}
