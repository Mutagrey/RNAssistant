using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Services
{
    public sealed class ResourceAddress
    {
        public string Uri { get; internal set; }
        public string Provider { get; internal set; }
        public IReadOnlyList<string> Segments { get; internal set; }
    }

    public static class ResourceUri
    {
        public const string Scheme = "rna";

        public static string Create(string provider, params string[] segments)
        {
            provider = NormalizeProvider(provider);
            var values = (segments ?? new string[0]).Select(NormalizeSegment).ToArray();
            if (values.Length == 0) throw new ArgumentException("At least one resource path segment is required.", "segments");
            return Scheme + "://" + provider + "/" + string.Join("/", values.Select(Uri.EscapeDataString));
        }

        public static ResourceAddress Parse(string value)
        {
            ResourceAddress result;
            if (!TryParse(value, out result)) throw new FormatException("Resource URI is invalid.");
            return result;
        }

        public static bool TryParse(string value, out ResourceAddress result)
        {
            result = null;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out parsed)) return false;
            if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort)
            {
                return false;
            }

            string provider;
            try
            {
                provider = NormalizeProvider(parsed.Host);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var encoded = parsed.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (encoded.Length == 0) return false;
            var segments = new List<string>(encoded.Length);
            foreach (var item in encoded)
            {
                string segment;
                try
                {
                    segment = Uri.UnescapeDataString(item);
                    NormalizeSegment(segment);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is UriFormatException)
                {
                    return false;
                }
                if (segment.IndexOf('/') >= 0 || segment.IndexOf('\\') >= 0) return false;
                segments.Add(segment);
            }

            var canonical = Create(provider, segments.ToArray());
            if (!string.Equals(canonical, value.Trim(), StringComparison.Ordinal)) return false;
            result = new ResourceAddress { Uri = canonical, Provider = provider, Segments = segments.AsReadOnly() };
            return true;
        }

        private static string NormalizeProvider(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) throw new ArgumentException("Resource provider is required.", "provider");
            if (value.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-'))
            {
                throw new ArgumentException("Resource provider contains unsupported characters.", "provider");
            }
            return value;
        }

        private static string NormalizeSegment(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || value == "." || value == ".." || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
            {
                throw new ArgumentException("Resource path segment is invalid.", "segments");
            }
            return value;
        }
    }
}
