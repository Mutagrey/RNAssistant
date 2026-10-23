using System;
using System.Collections.Generic;
using System.Globalization;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    internal static class ResourceTreePointer
    {
        internal static List<string> ParseTokens(string pointer)
        {
            if (pointer == null || pointer.Length > 0 && pointer[0] != '/')
                throw Invalid();
            var tokens = new List<string>();
            if (pointer.Length == 0) return tokens;
            foreach (var encoded in pointer.Substring(1).Split('/'))
            {
                var decoded = new System.Text.StringBuilder();
                for (var i = 0; i < encoded.Length; i++)
                {
                    if (encoded[i] != '~') { decoded.Append(encoded[i]); continue; }
                    if (++i == encoded.Length || encoded[i] != '0' && encoded[i] != '1') throw Invalid();
                    decoded.Append(encoded[i] == '0' ? '~' : '/');
                }
                tokens.Add(decoded.ToString());
            }
            return tokens;
        }

        internal static long ArrayIndex(string token)
        {
            if (token.Length == 0 || token.Length > 1 && token[0] == '0') return -1;
            for (var i = 0; i < token.Length; i++) if (token[i] < '0' || token[i] > '9') return -1;
            return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
        }

        internal static string Escape(string token) { return token.Replace("~", "~0").Replace("/", "~1"); }

        private static ResourceRequestException Invalid()
        { return new ResourceRequestException("Use a slash-prefixed RFC 6901-style pointer with valid ~0/~1 escapes.",
            "resource_tree_pointer_invalid", false); }
    }
}
