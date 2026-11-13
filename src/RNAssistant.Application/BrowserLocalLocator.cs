using System;
using System.Linq;

namespace RNAssistant.Application
{
    // Display address only. A parsed locator never authorizes access to content.
    public enum BrowserLocatorKind { Home, ProjectSource, ProjectVersion, PluginSource, PluginVersion, Resource, ResourceVersion }

    public sealed class BrowserLocalLocator
    {
        public BrowserLocatorKind Kind { get; private set; }
        public string Key { get; private set; }
        public string VersionId { get; private set; }
        public string RelativePath { get; private set; }
        public string Query { get; private set; }
        public string Fragment { get; private set; }
        public string Canonical { get; private set; }
        public string ContentLocator { get; private set; }
        public string RecentLocator { get { return ContentLocator; } }

        public static bool TryParse(string input, out BrowserLocalLocator result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 2048 || input.Any(char.IsControl) ||
                input.Contains("\\") || input[0] != '/' || input.StartsWith("//", StringComparison.Ordinal)) return false;
            var fragmentAt = input.IndexOf('#');
            var fragment = fragmentAt < 0 ? "" : input.Substring(fragmentAt);
            var pathAndQuery = fragmentAt < 0 ? input : input.Substring(0, fragmentAt);
            var queryAt = pathAndQuery.IndexOf('?');
            var query = queryAt < 0 ? "" : pathAndQuery.Substring(queryAt);
            var path = queryAt < 0 ? pathAndQuery : pathAndQuery.Substring(0, queryAt);
            if (path == "/")
            { result = new BrowserLocalLocator { Kind = BrowserLocatorKind.Home, Canonical = "/", ContentLocator = "/", Query = "", Fragment = "" }; return query.Length == 0 && fragment.Length == 0; }
            var encoded = path.Substring(1).Split('/');
            if (encoded.Length > 64 || encoded.Any(item => item.Length == 0)) return false;
            string[] parts;
            try { parts = encoded.Select(Uri.UnescapeDataString).ToArray(); }
            catch (UriFormatException) { return false; }
            if (parts.Any(item => item.Length > 256 || item == "." || item == ".." ||
                item.IndexOfAny(new[] { '/', '\\', ':', '%', '?', '#', '\0' }) >= 0 || item.Any(char.IsControl))) return false;
            var value = new BrowserLocalLocator { Key = parts.Length > 1 ? parts[1] : null, Query = query, Fragment = fragment };
            if (parts[0] == "resources" && (parts.Length == 2 || parts.Length == 4 && parts[2] == "versions"))
            {
                value.Kind = parts.Length == 2 ? BrowserLocatorKind.Resource : BrowserLocatorKind.ResourceVersion;
                value.VersionId = parts.Length == 4 ? parts[3] : null;
            }
            else if ((parts[0] == "projects" || parts[0] == "plugins") && parts.Length >= 4)
            {
                var plugin = parts[0] == "plugins";
                var saved = parts[2] == "versions";
                if ((!saved && parts[2] != "source") || saved && parts.Length < 5) return false;
                value.Kind = plugin ? (saved ? BrowserLocatorKind.PluginVersion : BrowserLocatorKind.PluginSource) :
                    (saved ? BrowserLocatorKind.ProjectVersion : BrowserLocatorKind.ProjectSource);
                value.VersionId = saved ? parts[3] : null;
                value.RelativePath = string.Join("/", parts.Skip(saved ? 4 : 3));
            }
            else return false;
            value.ContentLocator = "/" + string.Join("/", parts.Select(Uri.EscapeDataString));
            value.Canonical = value.ContentLocator + query + fragment;
            result = value; return true;
        }

        public static string ForProject(string key, string path, string version = null)
        { return Format("projects", key, path, version); }
        public static string ForPlugin(string key, string path, string version = null)
        { return Format("plugins", key, path, version); }
        public static string ForResource(string key, string version = null)
        { return Require("/resources/" + Uri.EscapeDataString(key ?? "") + (version == null ? "" : "/versions/" + Uri.EscapeDataString(version))); }
        private static string Format(string area, string key, string path, string version)
        {
            return Require("/" + area + "/" + Uri.EscapeDataString(key ?? "") +
                (version == null ? "/source/" : "/versions/" + Uri.EscapeDataString(version) + "/") +
                string.Join("/", (path ?? "").Split('/').Select(Uri.EscapeDataString)));
        }
        private static string Require(string locator)
        {
            BrowserLocalLocator parsed;
            if (!TryParse(locator, out parsed)) throw new ArgumentException("Invalid local Browser address.");
            return parsed.Canonical;
        }
    }
}
