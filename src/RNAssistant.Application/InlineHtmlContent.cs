using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // One transient native-page source assembled from an accepted chat visual. It
    // grants no resource, filesystem or network authority and is never persisted.
    internal static class InlineHtmlContent
    {
        internal const int MaximumCharacters = 200000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static BrowserContentSession Create(string html, string title)
        {
            if (html == null || html.Length == 0 || html.Length > MaximumCharacters)
                throw new InvalidOperationException("HTML interactive source must contain 1 to 200000 characters.");
            var bytes = Utf8.GetBytes(html);
            var key = "inline-" + Guid.NewGuid().ToString("N");
            var target = new BrowserTargetDescriptor
            {
                Kind = BrowserTargetKind.InlineHtml, State = BrowserTargetState.Ready,
                OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" },
                Title = BoundedTitle(title), EntryPath = "index.html", RelativePath = "index.html",
                DisplayLocator = BrowserLocalLocator.ForResource(key, null, "index.html"),
                ContentRevision = Hash(bytes), Immutable = true,
                Capabilities = new BrowserTargetCapabilities { Preview = true, Interact = true, PreviewReason = BrowserRefusalCode.None }
            };
            var document = Utf8.GetBytes(Bootstrap(Convert.ToBase64String(bytes)));
            var closed = false;
            return new BrowserContentSession(target, new[] { "index.html" }, "index.html",
                (path, token) => { token.ThrowIfCancellationRequested(); if (closed) throw new ObjectDisposedException(nameof(InlineHtmlContent)); return new MemoryStream(document, false); },
                () => { if (closed) throw new ObjectDisposedException(nameof(InlineHtmlContent)); },
                close: () => { closed = true; return System.Threading.Tasks.Task.CompletedTask; });
        }

        private static string BoundedTitle(string value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) text = "HTML из чата";
            if (text.Length > 160) text = text.Substring(0, 160);
            text = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            return text.Length == 0 ? "HTML из чата" : text;
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return "sha256:" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Bootstrap(string base64)
        {
            const string policy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "img-src data: blob:; connect-src 'none'; font-src data:; media-src data: blob:; frame-src 'none'; " +
                "worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none';";
            return "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" +
                policy + "\"><style>:root{color-scheme:light dark;}html,body{margin:0;min-height:100%;}</style></head><body><script>" +
                "(function(){var b=atob('" + base64 + "'),a=new Uint8Array(b.length);" +
                "for(var i=0;i<b.length;i++)a[i]=b.charCodeAt(i);var t=new TextDecoder('utf-8').decode(a);" +
                "document.open();document.write(t);document.close();})();</script></body></html>";
        }
    }
}
