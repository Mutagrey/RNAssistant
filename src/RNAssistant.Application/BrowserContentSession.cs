using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;

namespace RNAssistant.Application
{
    // A transient exact member binding over existing CAS/package owners, not a store.
    public sealed class BrowserContentSession : IDisposable
    {
        private readonly Func<string, CancellationToken, Stream> _read;
        private readonly Action _validate;
        private readonly Func<Task> _close;
        private readonly Func<string, ResourceRef> _annotationSource;
        private bool _disposed;
        public BrowserTargetDescriptor Target { get; }
        public IReadOnlyList<string> Members { get; }
        public string EntryPath { get; }
        public string Origin { get; }
        public PluginInstance Plugin { get; }
        public HtmlWorkspaceSnapshot HtmlWorkspace { get; }
        public Task Drained { get; private set; } = Task.CompletedTask;

        public BrowserContentSession(BrowserTargetDescriptor target, IEnumerable<string> members, string entryPath,
            Func<string, CancellationToken, Stream> read, Action validate, PluginInstance plugin = null, Func<Task> close = null,
            HtmlWorkspaceSnapshot htmlWorkspace = null, Func<string, ResourceRef> annotationSource = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            var paths = members?.Take(513).ToArray();
            if (paths == null || paths.Length == 0 || paths.Length > 512 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
                paths.Any(path => !ValidPath(path)) || !paths.Contains(entryPath, StringComparer.Ordinal))
                throw new InvalidOperationException("The exact Browser member set or entry is invalid.");
            Members = Array.AsReadOnly(paths); EntryPath = entryPath;
            _read = read ?? throw new ArgumentNullException(nameof(read)); _validate = validate ?? throw new ArgumentNullException(nameof(validate));
            Plugin = plugin; _close = close;
            HtmlWorkspace = htmlWorkspace;
            _annotationSource = annotationSource;
            Origin = plugin?.Origin ?? "https://page-" + Guid.NewGuid().ToString("N") + ".rnassistant.invalid";
        }
        public static bool ValidPath(string path)
        { return !string.IsNullOrEmpty(path) && path.Length <= 2048 && !path.Any(char.IsControl) &&
            !path.Contains("\\") && !path.Contains(":") && !path.Contains("%") && !path.Contains("?") && !path.Contains("#") &&
            path.Split('/').All(part => part.Length > 0 && part != "." && part != ".."); }
        public string Url(string path) => Origin + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        public string InitialUrl(string locator)
        {
            BrowserLocalLocator parsed;
            if (!BrowserLocalLocator.TryParse(locator, out parsed)) throw new InvalidOperationException("Invalid Browser address.");
            return Url(EntryPath) + parsed.Query + parsed.Fragment;
        }
        public string LocalLocator(string member)
        {
            if (!Members.Contains(member, StringComparer.Ordinal)) throw new InvalidOperationException("Unknown Browser member.");
            if (Target.PluginId != null) return BrowserLocalLocator.ForPlugin(Target.PluginId, member, Target.SavedVersionId);
            if (Target.Kind == BrowserTargetKind.WorkspaceHtmlProject) return BrowserLocalLocator.ForProject(Target.WorkspaceId, member, Target.SavedVersionId);
            BrowserLocalLocator locator;
            if (!BrowserLocalLocator.TryParse(Target.DisplayLocator, out locator)) throw new InvalidOperationException("Invalid document address.");
            return BrowserLocalLocator.ForResource(locator.Key, locator.VersionId, member);
        }
        public string Location(string url)
        {
            string member;
            if (!TryMember(url, out member)) throw new InvalidOperationException("Address outside the exact Browser content.");
            var address = new Uri(url);
            return LocalLocator(member) + address.Query + address.Fragment;
        }
        public void UpdateLocation(BrowserPageStateDto state, string url)
        {
            BrowserLocalLocator locator;
            if (!BrowserLocalLocator.TryParse(Location(url), out locator)) throw new InvalidOperationException("Invalid Browser location.");
            state.Url = locator.Canonical;
            state.Target.DisplayLocator = locator.ContentLocator;
            state.Target.RelativePath = state.Target.EntryPath = locator.RelativePath;
        }
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
        public BrowserAnnotationCapture Annotation(string path)
        {
            Validate();
            return CapturedAnnotation(path);
        }
        public BrowserAnnotationContext AnnotationContext(string path) => CapturedAnnotation(path)?.Context;
        private BrowserAnnotationCapture CapturedAnnotation(string path)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BrowserContentSession));
            if (string.IsNullOrEmpty(Target.OwnerScope?.ChatId) || !Members.Contains(path, StringComparer.Ordinal)) return null;
            var source = _annotationSource?.Invoke(path);
            if (source?.IsExact != true) return null;
            return new BrowserAnnotationCapture { Owner = Target.OwnerScope, Source = source.Copy(), Title = Target.Title,
                Page = new HtmlVisualSourcePage { Path = path, ContentRevision = Target.ContentRevision, SavedVersionId = Target.SavedVersionId,
                    WorkspaceId = Target.OwnerScope.Kind == BrowserOwnerKind.Workspace ? Target.OwnerScope.ScopeId : null } };
        }
        public void Dispose()
        { if (_disposed) return; _disposed = true; if (_close != null) Drained = _close(); }
    }
}
