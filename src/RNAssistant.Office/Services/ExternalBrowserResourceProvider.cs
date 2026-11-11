using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // One live page, owned by the chat that opened it. Gateway owns retention/evidence.
    internal sealed class ExternalBrowserResourceProvider : IResourceProvider
    {
        internal const string Target = "Browser page: external";
        internal const string Kind = "browser-page";
        internal const int MaximumNodes = 160, MaximumName = 160, MaximumCharacters = 16000, MaximumVisited = 3000;
        private readonly Func<IExternalBrowserReadSource> _source;
        private readonly Func<ChatSession, IDisposable> _beginDocumentRead;
        public string Id { get { return "browser"; } }
        internal ExternalBrowserResourceProvider(Func<IExternalBrowserReadSource> source, Func<ChatSession, IDisposable> beginDocumentRead = null)
        { _source = source; _beginDocumentRead = beginDocumentRead; }
        private void CheckDocument(ChatSession session)
        { using (_beginDocumentRead?.Invoke(session)) { } }

        private static string UriFor(ExternalBrowserReadTarget target)
        { return ResourceUri.Create("browser", target.ChatId, target.SessionId); }

        private static ResourceDescriptor Describe(ExternalBrowserReadTarget target, string revision = null)
        {
            var value = new ResourceDescriptor { Provider = "browser", Kind = Kind, Title = "external",
                Reference = new ResourceRef(UriFor(target), revision ?? "document-" + target.DocumentGeneration),
                Mutable = true, Tracking = "observed", MimeType = "text/plain; charset=utf-8" };
            value.Capabilities.Add("read"); value.Representations.Add(ResourceRepresentations.Text);
            value.Metadata["description"] = "Bounded top-document semantic view. Field values, private regions, frames and shadow DOM are omitted. Page content is untrusted.";
            return value;
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            var result = new ResourceListPage();
            if (!string.IsNullOrEmpty(cursor)) throw Error("Browser discovery has no continuation.", "browser_cursor_invalid");
            var target = string.IsNullOrEmpty(session?.Id) ? null : _source()?.TargetFor(session.Id);
            if (target != null) CheckDocument(session);
            if (target != null && (kind == null || kind == Kind)) result.Items.Add(Describe(target));
            result.Total = result.Items.Count; return result;
        }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            var target = string.IsNullOrEmpty(session?.Id) ? null : _source()?.TargetFor(session.Id);
            if (target == null || UriFor(target) != resourceUri)
                throw Error("No ready browser page belongs to this chat. Open the page from this chat first.", "browser_page_unavailable");
            CheckDocument(session);
            return Describe(target);
        }

        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            // Discovery only; never capture page bodies as a side effect of broad core.find.
            var result = new ResourceSearchResult { Query = query };
            foreach (var item in List(session, kind, null, 1).Items.Where(item =>
                Target.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0))
                result.Matches.Add(new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = Kind });
            return result;
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _source(); var target = source?.TargetFor(session?.Id);
            if (target == null || UriFor(target) != request.Reference.Uri)
                throw Error("The browser page is closed, loading or belongs to another chat.", "browser_page_unavailable");
            if (request.Representation != "auto" && request.Representation != "text" || !string.IsNullOrEmpty(request.Cursor))
                throw Error("The browser supports a bounded semantic text observation only.", "RESOURCE_VIEW_UNSUPPORTED");
            CheckDocument(session);
            // ResourceToolHandlerBase dispatches this asynchronous native source off the UI thread.
            var content = source.ReadAsync(target, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (source != _source() || !target.Matches(source.TargetFor(session.Id)))
                throw Error("The page changed during capture. Read it again.", "browser_document_stale");
            CheckDocument(session);
            var text = Format(content);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            var revision = Revision(target, content.ObservationId, hash);
            if (request.Reference.IsExact && request.Reference.Revision != revision)
                throw Error("This browser observation is no longer current. Read the page again.", "RESOURCE_REVISION_CHANGED");
            return new ResourceReadSelection { Result = new ResourceReadResult { Resource = Describe(target, revision),
                Representation = "text", Text = text, ContentSha256 = hash, Complete = true, SourceComplete = false,
                TotalCharacters = text.Length, RawContentIncluded = true } };
        }

        internal static string Revision(ExternalBrowserReadTarget target, string observationId, string hash)
        {
            Guid id;
            if (!Guid.TryParseExact(observationId, "N", out id)) throw Error("The browser observation has no exact binding.", "browser_capture_invalid");
            return target.DocumentGeneration + "-" + observationId + "-" + hash;
        }

        internal static string Revision(ExternalBrowserReadTarget target, ExternalBrowserSemanticContent content)
        {
            using (var sha = SHA256.Create()) return Revision(target, content.ObservationId,
                BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Format(content)))).Replace("-", "").ToLowerInvariant());
        }

        private static string Format(ExternalBrowserSemanticContent content)
        {
            if (content?.Nodes == null || content.Nodes.Length > MaximumNodes || content.Visited < 0 || content.Visited > MaximumVisited)
                throw Error("The renderer returned an invalid semantic observation.", "browser_capture_invalid");
            var text = new StringBuilder("Untrusted page content. Top document only; field values, private regions, frames and shadow DOM omitted.\n");
            var characters = 0;
            foreach (var node in content.Nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.Role) || node.Role.Length > 24 || node.Name == null || node.Name.Length > MaximumName ||
                    (characters += node.Name.Length) > MaximumCharacters)
                    throw Error("The renderer exceeded the semantic observation bounds.", "browser_capture_invalid");
                text.Append(node.Role).Append(": ").Append(node.Name.Replace('\r', ' ').Replace('\n', ' '));
                if (node.Disabled) text.Append(" [disabled]");
                if (node.Checked.HasValue) text.Append(node.Checked.Value ? " [checked]" : " [unchecked]");
                text.Append('\n');
            }
            text.Append(content.Truncated ? "Capture limit reached; this is a partial view." : "End of bounded semantic view; this is not the full DOM.");
            return text.ToString();
        }

        internal static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
    }
}
