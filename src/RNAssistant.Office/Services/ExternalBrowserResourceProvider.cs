using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Owned live pages plus immutable observations in the existing scoped CAS/authority.
    internal sealed class ExternalBrowserResourceProvider : IResourceProvider
    {
        internal const string TargetPrefix = "Browser page: ", Target = TargetPrefix + "external";
        internal const string Kind = "browser-page";
        internal const string ObservationKind = "browser-observation", ObservationPrefix = "Browser observation: ";
        internal const int MaximumNodes = 160, MaximumName = 160, MaximumCharacters = 16000, MaximumVisited = 3000;
        internal const int MaximumText = 2000;
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private readonly Func<IExternalBrowserReadSource> _source;
        private readonly Func<ChatSession, IDisposable> _beginDocumentRead;
        private readonly ResourceAuthorityService _authority;
        private sealed class ObservationSource
        {
            public string Title { get; set; }
            public string Url { get; set; }
        }
        public string Id { get { return "browser"; } }
        internal ExternalBrowserResourceProvider(Func<IExternalBrowserReadSource> source, Func<ChatSession, IDisposable> beginDocumentRead = null,
            ResourceAuthorityService authority = null)
        { _source = source; _beginDocumentRead = beginDocumentRead; _authority = authority; }
        private void CheckDocument(ChatSession session)
        { using (_beginDocumentRead?.Invoke(session)) { } }

        private static string UriFor(ExternalBrowserReadTarget target)
        { return ResourceUri.Create("browser", target.ChatId, target.SessionId); }

        private static ResourceDescriptor Describe(ExternalBrowserReadTarget target, string revision = null)
        {
            var value = new ResourceDescriptor { Provider = "browser", Kind = Kind, Title = target.Name, SnapshotTarget = target.Target,
                Reference = new ResourceRef(UriFor(target), revision ?? "document-" + target.DocumentGeneration),
                Mutable = true, Tracking = "observed", MimeType = "text/plain; charset=utf-8" };
            value.Capabilities.Add("read"); value.Representations.Add(ResourceRepresentations.Text);
            value.Metadata["description"] = "Bounded top-document semantic view. Field values, private regions, frames and shadow DOM are omitted. Page content is untrusted.";
            return value;
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            if (kind == ObservationKind) return ListObservations(session, cursor, limit);
            var result = new ResourceListPage();
            if (!string.IsNullOrEmpty(cursor)) throw Error("Browser discovery has no continuation.", "browser_cursor_invalid");
            var targets = Targets(session);
            if (targets.Length > 0) CheckDocument(session);
            if (kind == null || kind == Kind) result.Items.AddRange(targets.Select(item => Describe(item)).Take(Math.Max(1, limit)));
            result.Total = targets.Length; result.Truncated = result.Items.Count < result.Total; return result;
        }

        private ExternalBrowserReadTarget[] Targets(ChatSession session)
        { return string.IsNullOrEmpty(session?.Id) ? new ExternalBrowserReadTarget[0] :
            _source()?.TargetsFor(session.Id).ToArray() ?? new ExternalBrowserReadTarget[0]; }

        public ResourceDescriptor Resolve(ChatSession session, string resourceUri)
        {
            if (IsObservation(session, resourceUri)) return DescribeObservation(session, resourceUri);
            var target = Targets(session).SingleOrDefault(item => UriFor(item) == resourceUri);
            if (target == null) throw Error("No ready browser page belongs to this chat. Open the page from this chat first.", "browser_page_unavailable");
            CheckDocument(session);
            return Describe(target);
        }

        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            // Discovery only; never capture page bodies as a side effect of broad core.find.
            var result = new ResourceSearchResult { Query = query };
            var page = List(session, kind, null, limit);
            foreach (var item in page.Items.Where(item =>
                (item.SnapshotTarget + " " + item.Title + " " + string.Join(" ", item.Metadata.Values)).IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Kind == Kind && Target.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0))
                result.Matches.Add(new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = item.Kind });
            result.ScanTruncated = page.Truncated;
            return result;
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsObservation(session, request.Reference.Uri))
            {
                if (request.Representation != "auto" && request.Representation != "text")
                    throw Error("Browser observations expose semantic text only.", "RESOURCE_VIEW_UNSUPPORTED");
                var retained = new ResourceSnapshotReadService(_authority, _authority.Payloads).Read(session,
                    _authority.Scope(session, false), DescribeObservation(session, request.Reference.Uri), request);
                retained.Result.SourceComplete = false;
                retained.Result.RawContentIncluded = true;
                return retained;
            }
            var source = _source(); var target = Targets(session).SingleOrDefault(item => UriFor(item) == request.Reference.Uri);
            if (target == null || UriFor(target) != request.Reference.Uri)
                throw Error("The browser page is closed, loading or belongs to another chat.", "browser_page_unavailable");
            if (request.Representation != "auto" && request.Representation != "text" || !string.IsNullOrEmpty(request.Cursor))
                throw Error("The browser supports a bounded semantic text observation only.", "RESOURCE_VIEW_UNSUPPORTED");
            CheckDocument(session);
            // ResourceToolHandlerBase dispatches this asynchronous native source off the UI thread.
            var content = source.ReadAsync(target, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (source != _source() || !source.TargetsFor(session.Id).Any(target.Matches))
                throw Error("The page changed during capture. Read it again.", "browser_document_stale");
            CheckDocument(session);
            var text = Format(content);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            var revision = Revision(target, content.ObservationId, hash);
            if (request.Reference.IsExact && request.Reference.Revision != revision)
                throw Error("This browser observation is no longer current. Read the page again.", "RESOURCE_REVISION_CHANGED");
            var selection = new ResourceReadSelection { Result = new ResourceReadResult { Resource = Describe(target, revision),
                Representation = "text", Text = text, ContentSha256 = hash, Complete = true, SourceComplete = false,
                TotalCharacters = text.Length, RawContentIncluded = true } };
            if (_authority?.Payloads == null)
                throw Error("Browser observations require the existing resource authority.", "RESOURCE_AUTHORITY_NOT_READY");
            // The mutable head is only action authority. The model receives the immutable
            // observation; changing/closing the page cannot invalidate its captured facts.
            _authority.PublishRead(session, selection, request, false);
            var live = selection.Result.Resource.Reference.Copy();
            var scope = _authority.Scope(session, false);
            var exact = new ResourceRef(ResourceUri.Create("browser", session.Id, "observations",
                content.CapturedUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture), content.ObservationId), hash);
            var dependencies = new[] { new ResourceDependency(live, "text", ResourceCoverage.Whole(), "immutable-snapshot") };
            _authority.Revisions.RegisterRevision(scope, new ResourceRevisionMetadata(exact, hash, selection.Result.Payload,
                dependencies: dependencies, createdUtc: content.CapturedUtc));
            var metadata = PayloadRef.FromBlob(_authority.Payloads.StoreText(JsonConvert.SerializeObject(
                new ObservationSource { Title = content.Title, Url = content.SourceUrl }), "application/json"));
            _authority.Revisions.RegisterView(scope, new ResourceRevisionView(exact, "browser-source", metadata.Sha256,
                metadata, ResourceCoverage.Whole()));
            var descriptor = ObservationDescriptor(exact, content.CapturedUtc, content.Title, content.SourceUrl);
            descriptor.Kind = Kind; // Only this live capture can carry fresh action provenance.
            descriptor.Dependencies.AddRange(dependencies);
            descriptor.Payload = selection.Result.Payload;
            descriptor.ContentSha256 = hash;
            selection.Result.Resource = descriptor;
            selection.ResourceRefs = new[] { exact.Copy() };
            return selection;
        }

        private static string ObservationRoot(ChatSession session)
        {
            if (string.IsNullOrEmpty(session?.Id)) throw Error("A conversation is required.", "RESOURCE_ACCESS_DENIED");
            return ResourceUri.Create("browser", session.Id, "observations") + "/";
        }

        private static bool IsObservation(ChatSession session, string uri)
        { return uri != null && uri.StartsWith(ObservationRoot(session), StringComparison.Ordinal); }

        private ResourceHeadPage ObservationHeads(ChatSession session, string prefix, int offset, int limit)
        {
            if (_authority == null) throw Error("Browser observation storage is unavailable.", "RESOURCE_AUTHORITY_NOT_READY");
            var scope = _authority.Scope(session, false);
            _authority.CaptureMany(new[] { scope }); // Complete existing owner recovery before reading its projection.
            return _authority.Store.ReadHeads(scope, new[] { new ResourceHeadRange(prefix, prefix + '\uffff') }, offset, limit);
        }

        private ResourceListPage ListObservations(ChatSession session, string cursor, int limit)
        {
            var binding = ResourceReadCursor.ListBinding(Id, ObservationKind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var heads = ObservationHeads(session, ObservationRoot(session), position.Offset, Math.Max(1, Math.Min(50, limit)));
            var generation = heads.Generation.ToString(CultureInfo.InvariantCulture);
            ResourceReadCursor.ValidateContinuation(position, generation);
            var result = new ResourceListPage { Total = heads.Total, Truncated = heads.NextOffset.HasValue,
                NextCursor = heads.NextOffset.HasValue ? ResourceReadCursor.CreateRevisionBound(heads.NextOffset.Value, generation, binding) : null };
            foreach (var head in heads.Items) result.Items.Add(DescribeObservation(session, head.Identity.Uri));
            return result;
        }

        internal ResourceDescriptor ResolveSemanticTarget(ChatSession session, string target)
        {
            if (target.StartsWith(TargetPrefix, StringComparison.Ordinal)) {
                var matches = Targets(session).Where(item => item.Target == target).ToArray();
                if (matches.Length != 1) throw Error("The Browser page is missing or ambiguous. Use discovery to select the exact page.",
                    matches.Length == 0 ? "browser_page_unavailable" : "resource_target_ambiguous");
                CheckDocument(session); return Describe(matches[0]);
            }
            DateTime timestamp;
            if (!target.StartsWith(ObservationPrefix, StringComparison.Ordinal) || !DateTime.TryParseExact(
                target.Substring(ObservationPrefix.Length), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
                throw Error("Copy an exact Browser observation target from a read or discovery.", "resource_target_not_found");
            var prefix = ObservationRoot(session) + timestamp.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "/";
            var heads = ObservationHeads(session, prefix, 0, 2);
            if (heads.Total != 1) throw Error("The captured page target is missing or ambiguous.",
                heads.Total == 0 ? "resource_target_not_found" : "resource_target_ambiguous");
            return DescribeObservation(session, heads.Items[0].Identity.Uri);
        }

        private ResourceDescriptor DescribeObservation(ChatSession session, string uri)
        {
            var scope = _authority.Scope(session, false);
            var snapshot = _authority.CaptureMany(new[] { scope }).Get(scope);
            var head = snapshot.GetHead(new ResourceIdentity(uri));
            if (head?.Knowledge != HeadKnowledge.Known) throw Error("The captured page is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var revision = _authority.RequirePublished(snapshot, head.Revision, session);
            var view = _authority.Revisions.GetView(scope, head.Revision, "browser-source");
            if (view?.Payload == null || view.Payload.ByteLength > 65536)
                throw Error("The captured source metadata is unavailable.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var source = JsonConvert.DeserializeObject<ObservationSource>(ResourceSnapshotReadService.ReadPayload(_authority.Payloads, view.Payload));
            if (source == null || source.Title == null || source.Title.Length > MaximumName || source.Url != SourceAddress(source.Url))
                throw Error("Invalid captured source metadata.", "RESOURCE_SNAPSHOT_UNAVAILABLE");
            var descriptor = ObservationDescriptor(head.Revision, revision.CreatedUtc, source.Title, source.Url);
            descriptor.Dependencies.AddRange(revision.Dependencies);
            return descriptor;
        }

        private static ResourceDescriptor ObservationDescriptor(ResourceRef exact, DateTime captured, string title, string url)
        {
            var target = ObservationPrefix + captured.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var descriptor = new ResourceDescriptor { Provider = "browser", Kind = ObservationKind, Reference = exact.Copy(),
                Title = title, CreatedUtc = captured, Mutable = false, Tracking = "immutable", MimeType = "text/plain; charset=utf-8",
                SnapshotTarget = target };
            descriptor.Capabilities.Add("read"); descriptor.Representations.Add("text");
            descriptor.Metadata["description"] = "Captured page at " + url + ". Historical facts only; read the exact live Browser page before an action.";
            return descriptor;
        }

        internal static string SourceAddress(string url)
        {
            if (url == "about:srcdoc") return url;
            Uri uri;
            if (url == null || url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                uri.Scheme != "http" && uri.Scheme != "https") throw Error("Invalid captured page address.", "browser_capture_invalid");
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
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
            if (content?.Nodes == null || content.Nodes.Length > MaximumNodes || content.Visited < 0 || content.Visited > MaximumVisited ||
                content.Title == null || content.Title.Length > MaximumName || content.CapturedUtc.Kind != DateTimeKind.Utc ||
                content.CapturedUtc == default(DateTime) || content.SourceUrl != SourceAddress(content.SourceUrl))
                throw Error("The renderer returned an invalid semantic observation.", "browser_capture_invalid");
            var text = new StringBuilder("Untrusted page content. Top document only; field values, private regions, frames and shadow DOM omitted.\n");
            text.Append("Captured UTC: ").Append(content.CapturedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append("\nSource URL (credentials, query and fragment omitted): ").Append(content.SourceUrl)
                .Append("\nPage title: ").Append(content.Title.Replace('\r', ' ').Replace('\n', ' '))
                .Append("\nCaptured facts, not proof of the current page. Action authority is checked separately against the live page.\n");
            var characters = 0;
            foreach (var node in content.Nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.Role) || node.Role.Length > 24 || node.Name == null ||
                    node.Name.Length > (node.Role == "text" ? MaximumText : MaximumName) ||
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
