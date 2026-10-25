using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public sealed class ResourcePathFilter
    {
        private readonly Regex[] _include, _exclude;
        public ResourcePathFilter(string[] include, string[] exclude)
        { _include = Compile(include); _exclude = Compile(exclude); }
        public bool Matches(string path)
        {
            try { return (_include.Length == 0 || _include.Any(x => x.IsMatch(path))) && !_exclude.Any(x => x.IsMatch(path)); }
            catch (RegexMatchTimeoutException)
            { throw new ResourceRequestException("Path glob exceeded its matching budget; simplify the pattern.", "resource_glob_timeout", false); }
        }
        private static Regex[] Compile(string[] patterns)
        {
            if ((patterns?.Length ?? 0) > 16) throw new ResourceRequestException("At most 16 path globs are supported.", "resource_glob_invalid", false);
            return (patterns ?? new string[0]).Select(pattern => {
                if (string.IsNullOrEmpty(pattern) || pattern.Length > 256 || pattern.Any(char.IsControl) ||
                    pattern.StartsWith("/") || pattern.Contains("..") || pattern.IndexOfAny(new[] { '[', ']', '{', '}', '!', '\\', ':' }) >= 0)
                    throw new ResourceRequestException("Use a relative glob with *, ?, ** or **/ only.", "resource_glob_invalid", false);
                var regex = new StringBuilder("\\A");
                for (var i = 0; i < pattern.Length; i++)
                {
                    if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                    { i++; if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; regex.Append("(?:.*/)?"); } else regex.Append(".*"); }
                    else if (pattern[i] == '*') regex.Append("[^/]*");
                    else if (pattern[i] == '?') regex.Append("[^/]");
                    else regex.Append(Regex.Escape(pattern[i].ToString()));
                }
                return new Regex(regex.Append("\\z").ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }).ToArray();
        }
    }
}
