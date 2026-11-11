using System;
using System.Collections.Generic;
using System.Linq;

namespace RNAssistant.Core.Services
{
    // Exact HTTP(S) origins or a single leading *. DNS label; never arbitrary globs.
    public static class HttpOriginPattern
    {
        private static Uri Parse(string value, out bool subdomains)
        {
            value = (value ?? string.Empty).Trim();
            var separator = value.IndexOf("://", StringComparison.Ordinal);
            subdomains = separator >= 0 && value.Substring(separator + 3).StartsWith("*.", StringComparison.Ordinal);
            var address = subdomains ? value.Remove(separator + 3, 2) : value;
            Uri uri;
            var authority = separator < 0 ? "" : address.Substring(separator + 3).TrimEnd('/');
            if (value.Length > 512 || authority.Length == 0 ||
                authority.IndexOfAny(new[] { '/', '\\', '?', '#', '@', '%', '*' }) >= 0 ||
                address.EndsWith("//", StringComparison.Ordinal) ||
                !Uri.TryCreate(address, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                (subdomains && (uri.HostNameType != UriHostNameType.Dns || Host(uri).IndexOf('.') < 0)))
                throw new InvalidOperationException("Укажите HTTP(S)-адрес без пути либо маску вида https://*.example.com. Порт можно указать явно.");
            return uri;
        }

        private static string Host(Uri uri) { return uri.IdnHost.TrimEnd('.').ToLowerInvariant(); }

        public static string Normalize(string value)
        {
            bool subdomains;
            var uri = Parse(value, out subdomains);
            var host = Host(uri);
            if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
            return uri.Scheme + "://" + (subdomains ? "*." : "") + host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        }

        public static List<string> NormalizeList(IEnumerable<string> values)
        {
            var list = (values ?? Enumerable.Empty<string>()).ToList();
            if (list.Count > 128) throw new InvalidOperationException("Список сайтов агента ограничен 128 правилами.");
            return list.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
        }

        public static bool Matches(string pattern, Uri address)
        {
            if (address == null || !address.IsAbsoluteUri || !string.IsNullOrEmpty(address.UserInfo)) return false;
            try
            {
                bool subdomains;
                var origin = Parse(pattern, out subdomains);
                var host = Host(address); var allowed = Host(origin);
                return origin.Scheme == address.Scheme && origin.Port == address.Port &&
                    (host == allowed || subdomains && host.EndsWith("." + allowed, StringComparison.Ordinal));
            }
            catch (InvalidOperationException) { return false; }
            catch (UriFormatException) { return false; }
        }
    }
}
