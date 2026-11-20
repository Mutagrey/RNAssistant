using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    internal sealed partial class ResourceJournal
    {
        private sealed class CatalogHead
        {
            internal string Logical, Title, Kind;
            internal ResourceRef Reference;
            internal long Ticks;
            internal bool Removed, Incomplete, OnlyIfAbsent;
        }

        // The index orders locators. Publication and returned metadata are still
        // proved by DocumentArtifactStore. A missing logical head uses its existing
        // degraded presentation until the publisher completes.
        private List<CatalogHead> CatalogChanges(SqliteConnection db, ResourceJournalFact fact)
        {
            var changes = new List<CatalogHead>();
            var commit = AuthorityOf(fact);
            if (fact.Scope.Kind != "document" || commit == null) return changes;
            var scope = fact.Scope;
            var metadata = new ConversationMetadata("catalog", 0, null, null, documentAuthorityId: scope.Id);
            foreach (var change in commit.HeadChanges)
            {
                var address = ResourceUri.Parse(change.Identity.Uri);
                ResourceRef exact = null;
                var logical = change.Identity.Uri;
                var state = change.After;
                var logicalHead = address.Provider == "state" && address.Segments.Count == 3 &&
                    address.Segments[0] == "document" && address.Segments[1] == scope.Id &&
                    (address.Segments[2].StartsWith("plan_doc_", StringComparison.Ordinal) ||
                     address.Segments[2].StartsWith("html_ws_", StringComparison.Ordinal) ||
                     address.Segments[2].StartsWith("artifact_md_", StringComparison.Ordinal));
                if (!logicalHead)
                {
                    if (address.Provider != "chat" || address.Segments.Count != 3 || address.Segments[0] != scope.Id ||
                        address.Segments[1] != "artifact" || DocumentArtifactStore.ContextLogicalId(address.Segments[2]) != null) continue;
                    exact = state.Revision;
                    // A removal carries only its logical key. Version is immaterial when resolving that key.
                    var identitySource = exact ?? new ResourceRef(ResourceUri.Create("chat", scope.Id, "artifact", address.Segments[2], "revision", "1"), "1");
                    if (!DocumentArtifactStore.Owns(metadata, identitySource) || !identitySource.Identity.Equals(change.Identity)) continue;
                    var root = ArtifactWorkingSet.Identity(metadata, identitySource).Uri;
                    if (root != logical)
                    {
                        changes.Add(new CatalogHead { Logical = root, OnlyIfAbsent = true, Incomplete = true });
                        continue;
                    }
                }
                var row = new CatalogHead { Logical = logical, Removed = state.Knowledge == HeadKnowledge.Removed };
                changes.Add(row);
                if (row.Removed) continue;
                if (state.Knowledge != HeadKnowledge.Known) { row.Incomplete = true; continue; }
                if (logicalHead)
                {
                    var revision = RevisionOf(fact);
                    if (revision?.Reference.Uri != state.Revision.Uri || revision.Reference.Revision != state.Revision.Revision)
                        revision = RevisionOf(FindRevision(db, scope, state.Revision)?.Fact);
                    var dependencies = revision?.Dependencies.Where(item => item.Kind == "immutable-snapshot").ToArray();
                    exact = dependencies?.Length == 1 ? dependencies[0].Resource : null;
                }
                if (exact == null || !DocumentArtifactStore.Owns(metadata, exact)) { row.Incomplete = true; continue; }
                string doc, artifact; int version;
                if (!ChatResourceUri.TryParseArtifactRevision(exact, out doc, out artifact, out version)) { row.Incomplete = true; continue; }
                var view = artifact.StartsWith("attachment_", StringComparison.Ordinal) ? "artifact-original-record" :
                    artifact.StartsWith("plan_doc_", StringComparison.Ordinal) ? "artifact-plan-record" : "artifact-authored-record";
                var record = SelectedView(db, scope, exact, view);
                try
                {
                    if (record?.Payload == null || record.Payload.ByteLength > 512 * 1024) { row.Incomplete = true; continue; }
                    var json = _bodies.ReadText(record.Payload.ToBlobReference());
                    var item = json == null ? null : JObject.Parse(json)["Artifact"]?.ToObject<ChatArtifact>();
                    if (item?.Id != artifact || item.Revision != version || item.DocumentAuthorityId != doc ||
                        ArtifactWorkingSet.Identity(metadata, item).Uri != logical) { row.Incomplete = true; continue; }
                    row.Reference = exact; row.Kind = item.Kind; row.Ticks = item.CreatedUtc.Ticks;
                    row.Removed = PlanDocumentService.IsTombstone(item) || item.Kind == DocumentArtifactStore.SharedContextKind ||
                        item.Kind == ChatArtifactKinds.ToolResult || item.Kind == ChatArtifactKinds.TaskList || item.Kind == ChatArtifactKinds.Compaction;
                    row.Title = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(item.Title ?? ""), "material-catalog-title"));
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException ||
                    error is System.Security.Cryptography.CryptographicException || error is UnauthorizedAccessException)
                { row.Incomplete = true; }
            }
            return changes;
        }

        private static void SaveCatalogChanges(SqliteConnection db, SqliteTransaction tx, ResourceAuthorityScopeId scope,
            IEnumerable<CatalogHead> rows, long sequence)
        {
            foreach (var row in rows.OrderByDescending(item => item.OnlyIfAbsent))
                using (var command = Command(db, (row.OnlyIfAbsent ? "INSERT OR IGNORE" : "INSERT OR REPLACE") +
                    " INTO material_head VALUES($scope,$logical,$uri,$rev,$kind,$title,$ticks,$state,$sequence)", scope, tx))
                {
                    command.Parameters.AddWithValue("$logical", row.Logical); command.Parameters.AddWithValue("$sequence", sequence);
                    command.Parameters.AddWithValue("$uri", (object)row.Reference?.Uri ?? DBNull.Value);
                    command.Parameters.AddWithValue("$rev", (object)row.Reference?.Revision ?? DBNull.Value);
                    command.Parameters.AddWithValue("$title", (object)row.Title ?? DBNull.Value);
                    command.Parameters.AddWithValue("$kind", (object)row.Kind ?? DBNull.Value);
                    command.Parameters.AddWithValue("$ticks", row.Ticks);
                    command.Parameters.AddWithValue("$state", row.Incomplete ? -1 : row.Removed ? 0 : 1);
                    command.ExecuteNonQuery();
                }
        }

        internal sealed class MaterialPage
        {
            internal long Generation;
            internal IReadOnlyList<ResourceRef> References;
        }

        internal MaterialPage ReadMaterialPage(IReadOnlyDictionary<string, string> documents, IEnumerable<string> linked,
            string actorDocument, bool chatOnly, string query, string kind, string sort, bool descending, int offset,
            Func<string, string, string> format, CancellationToken token)
        {
            return Access((db, cursor) => {
                using (var setup = db.CreateCommand())
                {
                    setup.CommandText = "CREATE TEMP TABLE catalog_docs(scope TEXT PRIMARY KEY,title TEXT); CREATE TEMP TABLE catalog_links(logical TEXT PRIMARY KEY)";
                    setup.ExecuteNonQuery();
                }
                foreach (var doc in documents)
                    using (var entry = db.CreateCommand())
                    {
                        entry.CommandText = "INSERT INTO catalog_docs VALUES($scope,$title)";
                        entry.Parameters.AddWithValue("$scope", "document:" + doc.Key); entry.Parameters.AddWithValue("$title", doc.Value);
                        entry.ExecuteNonQuery();
                    }
                using (var entry = db.CreateCommand())
                {
                    entry.CommandText = "INSERT OR IGNORE INTO catalog_links VALUES($logical)";
                    var name = entry.Parameters.Add("$logical", SqliteType.Text);
                    foreach (var id in linked) { token.ThrowIfCancellationRequested(); name.Value = id; entry.ExecuteNonQuery(); }
                }
                using (var status = db.CreateCommand())
                {
                    status.CommandText = "SELECT EXISTS(SELECT 1 FROM catalog_docs d JOIN material_head h ON h.scope=d.scope WHERE h.state=-1)";
                    if (Convert.ToInt32(status.ExecuteScalar()) != 0) return null;
                }
                var result = new MaterialPage();
                using (var stamp = db.CreateCommand())
                {
                    stamp.CommandText = "SELECT COALESCE(MAX((SELECT MAX(sequence) FROM material_head h WHERE h.scope=d.scope)),0) FROM catalog_docs d";
                    result.Generation = Convert.ToInt64(stamp.ExecuteScalar());
                }
                token.ThrowIfCancellationRequested();
                db.CreateFunction<string, string>("catalog_title", value => value == null ? "" :
                    Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(value), "material-catalog-title")));
                db.CreateFunction<string, string, string>("catalog_format", format);
                db.CreateFunction<string, string, bool>("catalog_matches", (title, term) =>
                    (title ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
                var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), true);
                db.CreateCollation("catalog_ru", comparer.Compare);
                var order = sort == "updated" ? "h.ticks" : sort == "title" ? "catalog_title(h.title) COLLATE catalog_ru" :
                    sort == "format" ? "catalog_format(h.kind,catalog_title(h.title)) COLLATE catalog_ru" :
                    "(CASE WHEN l.logical IS NOT NULL THEN 'Этот чат' WHEN h.scope=$actor THEN 'В документе' ELSE d.title END) COLLATE catalog_ru";
                var rows = new List<ResourceRef>();
                using (var command = db.CreateCommand())
                {
                    // A single document uses the (scope,state,ticks,uri) range directly.
                    command.CommandText = "SELECT h.uri,h.revision FROM material_head h JOIN catalog_docs d ON d.scope=h.scope LEFT JOIN catalog_links l ON l.logical=h.logical WHERE h.state=1" +
                        (documents.Count == 1 ? " AND h.scope=$single" : "") + (chatOnly ? " AND l.logical IS NOT NULL" : "") +
                        (string.IsNullOrEmpty(kind) ? "" : " AND (h.kind=$kind OR ($kind='file' AND h.kind='attachment'))") +
                        (string.IsNullOrWhiteSpace(query) ? "" : " AND catalog_matches(catalog_title(h.title),$query)") +
                        " ORDER BY " + order + (descending ? " DESC" : " ASC") + ",h.uri ASC LIMIT 51 OFFSET $offset";
                    if (documents.Count == 1) command.Parameters.AddWithValue("$single", "document:" + documents.Keys.First());
                    if (sort == "owner") command.Parameters.AddWithValue("$actor", "document:" + actorDocument);
                    if (!string.IsNullOrEmpty(kind)) command.Parameters.AddWithValue("$kind", kind);
                    if (!string.IsNullOrWhiteSpace(query)) command.Parameters.AddWithValue("$query", query.Trim());
                    command.Parameters.AddWithValue("$offset", offset);
                    using (token.Register(command.Cancel))
                    using (var reader = command.ExecuteReader()) while (reader.Read())
                    { token.ThrowIfCancellationRequested(); rows.Add(new ResourceRef(reader.GetString(0), reader.GetString(1))); }
                }
                result.References = rows;
                return result;
            }, token);
        }
    }
}
