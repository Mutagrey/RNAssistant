using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ConversationJournal
    {
        private sealed class LegacyResourceRevision
        {
            internal ResourceAuthorityScopeId Scope;
            internal ResourceRevisionMetadata Metadata;
            internal ResourceRevisionView[] Views;
        }

        private ResourceRef[] ImportLegacyResources(ChatSession source, ConversationTextEntry[] entries, CancellationToken token)
        {
            var metadata = ConversationMetadata.From(source);
            var roots = source.Artifacts.Select(item => ChatResourceUri.CreateArtifactRevision(source, item))
                .Concat(entries.SelectMany(item => (item.Header.ResourceRefs ?? new List<ResourceRef>()).Concat(new[] { item.Header.HtmlWorkspaceCheckpoint })))
                .Concat((source.ArtifactLinks ?? new List<ChatArtifactLink>()).Select(link => link.Reference))
                .Concat(source.ActivePlanDocumentArtifactId == null ? new ResourceRef[0] : new[] { DocumentArtifactStore.PlanSelectionReference(source, source.ActivePlanDocumentArtifactId) })
                .Where(item => item != null && DocumentArtifactStore.Owns(metadata, item)).Select(ChatResourceUri.ArtifactSnapshot)
                .GroupBy(item => item.Uri + "\n" + item.Revision, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (roots.Length == 0 && source.ActiveHtmlArtifactId == null) return roots;
            var owner = new ResourceAuthorityStore(_paths);
            var handles = new List<FileStream>();
            var scopes = new Dictionary<string, ResourceAuthoritySnapshot>(StringComparer.Ordinal);
            var graph = new List<LegacyResourceRevision>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Func<ResourceAuthorityScopeId, ResourceAuthoritySnapshot> capture = scope => {
                ResourceAuthoritySnapshot snapshot;
                if (scopes.TryGetValue(scope.ToString(), out snapshot)) return snapshot;
                var prefix = TextPatternEngine.Sha256(scope.ToString());
                foreach (var suffix in new[] { ".authority.jsonl", ".revisions.jsonl", ".views.jsonl" })
                {
                    var path = Path.Combine(_paths.ResourceAuthorityDirectory, prefix + suffix);
                    if (!File.Exists(path)) continue;
                    LegacyRecoveryFiles.RequireRegular(path);
                    handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    LegacyRecoveryFiles.CheckJournal(path, token);
                }
                snapshot = owner.Capture(scope); scopes.Add(scope.ToString(), snapshot); return snapshot;
            };
            try
            {
                // Match the existing legacy load's selected HTML/Markdown history
                // membership, using bounded canonical metadata, not today's files.
                if (source.DocumentAuthorityId != null)
                {
                    var logicalIds = roots.Select(reference => MarkdownDocumentIdentity.LogicalId(ResourceUri.Parse(reference.Uri).Segments[2]))
                        .Concat(new[] { HtmlWorkspaceIdentity.LogicalId(source.ActiveHtmlArtifactId) }).Where(id => id != null).Distinct().ToArray();
                    if (logicalIds.Length != 0)
                    {
                        var snapshot = capture(new ResourceAuthorityScopeId("document", source.DocumentAuthorityId));
                        roots = roots.Concat(snapshot.Heads.Values.Where(head => head.Knowledge == HeadKnowledge.Known &&
                            DocumentArtifactStore.Owns(metadata, head.Revision)).Select(head => head.Revision).Where(reference => {
                                string ownerId, id; int revision;
                                return ChatResourceUri.TryParseArtifactRevision(reference, out ownerId, out id, out revision) &&
                                    logicalIds.Any(logical => id.StartsWith(logical + "_r", StringComparison.Ordinal));
                            })).GroupBy(reference => reference.Uri + "\n" + reference.Revision, StringComparer.Ordinal).Select(group => group.First()).ToArray();
                    }
                }
                if (roots.Length > 10000) throw new InvalidDataException("Слишком много сохранённых оригиналов истории.");
                var pending = new Queue<ResourceRef>(roots);
                // Every readable artifact needs the old immutable publication
                // barrier, not merely speculative registered bytes.
                foreach (var reference in roots)
                {
                    var scope = LegacyResourceScope(source, reference);
                    var snapshot = capture(scope);
                    var head = snapshot.GetHead(reference.Identity);
                    if (head?.Knowledge == HeadKnowledge.Removed ||
                        snapshot.GetHead(ArtifactWorkingSet.Identity(metadata, reference))?.Knowledge == HeadKnowledge.Removed)
                        throw new InvalidDataException("История ссылается на удалённый старый ресурс. Этот чат не перенесён, чтобы не восстановить удалённое: " + reference.Uri);
                    if (head == null || head.Knowledge != HeadKnowledge.Known && head.Knowledge != HeadKnowledge.Removed ||
                        !snapshot.Commits.SelectMany(commit => commit.HeadChanges).Any(change => change.After.Knowledge == HeadKnowledge.Known &&
                            SameLegacyResource(change.After.Revision, reference)))
                        throw new InvalidDataException("Старый ресурс не имеет подтверждённой публикации: " + reference.Uri);
                }
                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    var reference = pending.Dequeue();
                    if (!seen.Add(reference.Uri + "\n" + reference.Revision)) continue;
                    if (seen.Count > 10000) throw new InvalidDataException("Слишком много связанных ресурсов истории.");
                    var scope = LegacyResourceScope(source, reference); capture(scope);
                    var retained = owner.GetRevision(scope, reference);
                    if (retained == null) throw new InvalidDataException("Метаданные сохранённого ресурса отсутствуют: " + reference.Uri);
                    var views = new List<ResourceRevisionView>(); int? offset = 0; int? count = null;
                    while (offset.HasValue)
                    {
                        var page = owner.ReadViews(scope, reference, offset.Value, 50, count);
                        count = page.SnapshotCount;
                        if (count > 1000) throw new InvalidDataException("Слишком много представлений сохранённого ресурса.");
                        views.AddRange(page.Items); offset = page.NextOffset;
                    }
                    foreach (var body in new[] { retained.Payload }.Concat(views.SelectMany(view => new[] { view.Payload }.Concat(view.Parts))).Where(item => item != null))
                    {
                        token.ThrowIfCancellationRequested();
                        if (body.ByteLength > 256L * 1024 * 1024) throw new InvalidDataException("Связанный ресурс превышает 256 MiB.");
                        LegacyRecoveryFiles.RequireRegular(_bodies.PathFor(body.Sha256));
                        if (!_bodies.HasVerifiedReference(body.ToBlobReference())) throw new InvalidDataException("Байты связанного ресурса недоступны.");
                    }
                    if (retained.Parent != null) pending.Enqueue(retained.Parent);
                    if (retained.RestoredFrom != null) pending.Enqueue(retained.RestoredFrom);
                    foreach (var dependency in retained.Dependencies) pending.Enqueue(dependency.Resource);
                    graph.Add(new LegacyResourceRevision { Scope = scope, Metadata = retained, Views = views.ToArray() });
                }
            }
            finally { foreach (var handle in handles) handle.Dispose(); }

            var target = new ResourceJournal(_paths, _protector);
            // Preflight conflicts before staging; never replace a native snapshot
            // head or resurrect a removal in the destination.
            foreach (var reference in roots)
            {
                var scope = LegacyResourceScope(source, reference); var snapshot = target.Capture(scope);
                var head = snapshot.GetHead(reference.Identity);
                if (head != null && (head.Knowledge == HeadKnowledge.Known ? !SameLegacyResource(head.Revision, reference) :
                    head.Knowledge != HeadKnowledge.Removed || !snapshot.Commits.SelectMany(commit => commit.HeadChanges)
                        .Any(change => change.After.Knowledge == HeadKnowledge.Known && SameLegacyResource(change.After.Revision, reference))))
                    throw new InvalidDataException("Сохранённый ресурс конфликтует с текущей историей: " + reference.Uri);
            }
            foreach (var item in graph)
            {
                token.ThrowIfCancellationRequested(); target.RegisterRevision(item.Scope, item.Metadata);
                foreach (var view in item.Views) target.RegisterView(item.Scope, view);
            }
            foreach (var group in roots.GroupBy(reference => LegacyResourceScope(source, reference)))
            {
                token.ThrowIfCancellationRequested();
                var current = target.Capture(group.Key);
                var missing = group.Where(reference => current.GetHead(reference.Identity) == null).ToArray();
                if (missing.Length == 0) continue;
                var changes = missing.Select(reference => new ResourceHeadChange(reference.Identity, null,
                    ResourceHeadState.Known(reference, current.Generation + 1, "legacy-chat-immutable-snapshot"))).ToArray();
                target.Publish(new ResourceAuthorityCommit("legacy-chat-snapshots:" + Guid.NewGuid().ToString("N"), group.Key,
                    current.Generation, current.Generation + 1, null, changes, AuthorityCommitReason.Restore));
            }
            return roots;
        }

        private static bool SameLegacyResource(ResourceRef first, ResourceRef second)
        { return first?.Uri == second?.Uri && first?.Revision == second?.Revision; }

        private static ResourceAuthorityScopeId LegacyResourceScope(ChatSession source, ResourceRef reference)
        {
            if (reference?.IsExact != true) throw new InvalidDataException("Перенос требует точной ревизии ресурса.");
            var address = ResourceUri.Parse(reference.Uri);
            if (address.Provider == "chat" && address.Segments.Count >= 1)
            {
                if (address.Segments[0] == source.DocumentAuthorityId) return new ResourceAuthorityScopeId("document", source.DocumentAuthorityId);
                if (address.Segments[0] == source.WorkspaceId) return new ResourceAuthorityScopeId("workspace", source.WorkspaceId);
            }
            if ((address.Provider == "state" || address.Provider == "context") && address.Segments.Count == 3 &&
                (address.Segments[0] == "document" && address.Segments[1] == source.DocumentAuthorityId ||
                 address.Segments[0] == "workspace" && address.Segments[1] == source.WorkspaceId ||
                 address.Segments[0] == "conversation" && address.Segments[1] == source.Id))
                return new ResourceAuthorityScopeId(address.Segments[0], address.Segments[1]);
            // File identities are immutable retained dependencies of an already
            // validated artifact, never workspace paths supplied by the UI.
            if (address.Provider == "file" && address.Segments.Count > 0)
                return new ResourceAuthorityScopeId("file", address.Segments[0]);
            throw new InvalidDataException("Не поддерживается область зависимости старого ресурса: " + reference.Uri);
        }
    }
}
