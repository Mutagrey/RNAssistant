using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;

namespace RNAssistant.Application
{
    // A transient exact member binding over existing CAS/package owners, not a store.
    public sealed class BrowserContentSession : IDisposable
    {
        private readonly Func<string, CancellationToken, Stream> _read;
        private readonly Action _validate;
        private readonly Func<Task> _close;
        private bool _disposed;
        public BrowserTargetDescriptor Target { get; }
        public IReadOnlyList<string> Members { get; }
        public string EntryPath { get; }
        public string Origin { get; }
        public PluginInstance Plugin { get; }
        public Task Drained { get; private set; } = Task.CompletedTask;

        public BrowserContentSession(BrowserTargetDescriptor target, IEnumerable<string> members, string entryPath,
            Func<string, CancellationToken, Stream> read, Action validate, PluginInstance plugin = null, Func<Task> close = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            var paths = members?.Take(513).ToArray();
            if (paths == null || paths.Length == 0 || paths.Length > 512 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
                paths.Any(path => !ValidPath(path)) || !paths.Contains(entryPath, StringComparer.Ordinal))
                throw new InvalidOperationException("The exact Browser member set or entry is invalid.");
            Members = Array.AsReadOnly(paths); EntryPath = entryPath;
            _read = read ?? throw new ArgumentNullException(nameof(read)); _validate = validate ?? throw new ArgumentNullException(nameof(validate));
            Plugin = plugin; _close = close;
            Origin = plugin?.Origin ?? "https://page-" + Guid.NewGuid().ToString("N") + ".rnassistant.invalid";
        }
        public static bool ValidPath(string path)
        { return !string.IsNullOrEmpty(path) && path.Length <= 2048 && !path.Any(char.IsControl) &&
            !path.Contains("\\") && !path.Contains(":") && !path.Contains("%") && !path.Contains("?") && !path.Contains("#") &&
            path.Split('/').All(part => part.Length > 0 && part != "." && part != ".."); }
        public string Url(string path) => Origin + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        public bool TryMember(string url, out string path)
        {
            path = null; Uri uri;
            if (_disposed || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.GetLeftPart(UriPartial.Authority) != Origin ||
                !string.IsNullOrEmpty(uri.UserInfo) || url.Contains("\\")) return false;
            // Check raw spelling before System.Uri removes encoded traversal.
            var raw = url.Substring(Origin.Length).Split('?', '#')[0];
            try { path = Uri.UnescapeDataString(raw.TrimStart('/')); }
            catch (UriFormatException) { return false; }
            return raw.StartsWith("/", StringComparison.Ordinal) && !raw.StartsWith("//", StringComparison.Ordinal) &&
                ValidPath(path) && Members.Contains(path, StringComparer.Ordinal);
        }
        public Stream Open(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Validate();
            if (!Members.Contains(path, StringComparer.Ordinal)) throw new InvalidOperationException("The file is outside this Browser content binding.");
            return _read(path, token);
        }
        public void Validate()
        { if (_disposed) throw new ObjectDisposedException(nameof(BrowserContentSession)); _validate(); }
        public void Dispose()
        { if (_disposed) return; _disposed = true; if (_close != null) Drained = _close(); }
    }
}
