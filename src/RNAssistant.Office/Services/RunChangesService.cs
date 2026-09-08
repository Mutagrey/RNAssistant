using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class RunChangesService
    {
        internal const int MaximumSourceCharacters = 128000;
        internal const int MaximumItems = 100;
        private const int MaximumTotalCharacters = 512000;
        private readonly Func<ChatArtifact, string> _read;
        private readonly Func<VbaMutationQueryRequest, VbaMutationQueryPage> _query;
        private readonly Func<string, VbaMutationDetail> _detail;

        public RunChangesService(Func<ChatArtifact, string> read,
            Func<VbaMutationQueryRequest, VbaMutationQueryPage> query, Func<string, VbaMutationDetail> detail)
        { _read = read; _query = query; _detail = detail; }

        internal static string ReadRetainedSource(ChatSession session, ChatArtifact artifact,
            Func<ChatSession, string, bool> hydrate, ResourceGatewayService gateway)
        {
            if (string.IsNullOrEmpty(artifact.DocumentAuthorityId))
            {
                if (!hydrate(session, artifact.Id)) throw new InvalidOperationException("Retained source is unavailable.");
                if (artifact.InlineText == null || artifact.InlineText.Length > MaximumSourceCharacters)
                    throw new InvalidOperationException("Complete retained source is unavailable within the limit.");
                var bytes = System.Text.Encoding.UTF8.GetBytes(artifact.InlineText);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (artifact.ContentByteLength != bytes.LongLength || !Same(artifact.ContentSha256, hash))
                        throw new InvalidOperationException("Retained source does not match its exact body reference.");
                }
                return artifact.InlineText;
            }
            var text = new System.Text.StringBuilder();
            string cursor = null;
            do
            {
                var read = gateway.Read(session, new ResourceReadRequest {
                    Reference = RNAssistant.Core.Services.ChatResourceUri.CreateArtifactRevision(session, artifact),
                    Representation = "text", Cursor = cursor, MaxChars = ResourceReadRequest.MaximumCharacters }).Result;
                if (read.Text == null) throw new InvalidOperationException("Retained source is unavailable.");
                text.Append(read.Text);
                if (text.Length > MaximumSourceCharacters) throw new InvalidOperationException("Source exceeds the comparison limit.");
                if (read.Complete) return text.ToString();
                if (string.IsNullOrEmpty(read.NextCursor) || read.NextCursor == cursor)
                    throw new InvalidOperationException("Incomplete retained source.");
                cursor = read.NextCursor;
            } while (true);
        }

        public RunChangesDto Read(ChatSession session, string runId)
        {
            if (session == null || string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("An exact chat/run is required.");
            var result = new RunChangesDto { ChatId = session.Id, RunId = runId };
            var all = (session.Artifacts ?? new List<ChatArtifact>()).Where(a => a != null && !string.IsNullOrEmpty(a.Id))
                .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
            var selected = all.Values.Where(a => Same(a.RunId, runId) && IsSource(a)).ToList();
            // Collapse only linear parent chains belonging to this run. Never merge by title/time.
            var parents = new HashSet<string>(selected.Where(a => !string.IsNullOrEmpty(a.ParentArtifactId))
                .GroupBy(a => a.ParentArtifactId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var tips = selected.Where(a => !parents.Contains(a.Id)).OrderBy(a => a.CreatedUtc).ToList();
            if (tips.Count == 0 && selected.Count != 0) result.Complete = false;
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tip in tips.Take(MaximumItems))
            {
                var first = tip;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Id };
                ChatArtifact parent;
                while (!string.IsNullOrEmpty(first.ParentArtifactId) && all.TryGetValue(first.ParentArtifactId, out parent) &&
                    Same(parent.RunId, runId) && Same(parent.Kind, first.Kind) &&
                    selected.Count(a => Same(a.ParentArtifactId, parent.Id)) == 1 && visited.Add(parent.Id)) first = parent;
                covered.UnionWith(visited);
                all.TryGetValue(first.ParentArtifactId ?? "", out parent);
                try
                {
                    if (!string.IsNullOrEmpty(first.ParentArtifactId) && (parent == null || !Same(parent.Kind, first.Kind) || visited.Contains(parent.Id)))
                        throw new InvalidOperationException("Missing retained baseline.");
                    var beforeExists = parent != null && !PlanDocumentService.IsTombstone(parent);
                    var afterExists = !PlanDocumentService.IsTombstone(tip);
                    var before = beforeExists ? ReadSource(parent) : null;
                    var after = afterExists ? ReadSource(tip) : "";
                    if (tip.Kind == ChatArtifactKinds.HtmlWorkspace)
                        AddWorkspace(result, tip.Id, before, after);
                    else
                        Add(result, new RunTextChangeDto { Id = tip.Id, Title = tip.Title, BeforeTitle = parent?.Title,
                            Scope = "Текст", BeforeExists = beforeExists, AfterExists = afterExists,
                            Before = before ?? "", After = after, Availability = "available" });
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is JsonException || ex is ArgumentException)
                { AddGap(result, tip.Id, tip.Title, "Текст", "unavailable"); }
            }
            if (tips.Count > MaximumItems || selected.Any(a => !covered.Contains(a.Id))) result.Complete = false;
            AddVba(result, session, runId);
            var remaining = MaximumTotalCharacters;
            foreach (var item in result.Items)
            {
                var length = (item.Before?.Length ?? 0) + (item.After?.Length ?? 0);
                if (length > remaining) { item.Before = item.After = null; item.Availability = "too_large"; }
                else remaining -= length;
            }
            return result;
        }

        private string ReadSource(ChatArtifact artifact)
        {
            if (artifact.ContentByteLength > MaximumSourceCharacters * 4L)
                throw new InvalidOperationException("Source exceeds the comparison limit.");
            var source = _read(artifact);
            if (source == null || source.Length > MaximumSourceCharacters)
                throw new InvalidOperationException("Complete retained source is unavailable within the comparison limit.");
            return source;
        }

        private static bool IsSource(ChatArtifact artifact)
        {
            if (artifact.Kind == ChatArtifactKinds.HtmlWorkspace || artifact.Kind == ChatArtifactKinds.Markdown ||
                artifact.Kind == ChatArtifactKinds.PlanDocument) return true;
            // Uploads, extracted PDF/Office text, tool output and generated data snapshots are not authored source.
            if (artifact.Kind != ChatArtifactKinds.File) return false;
            var mime = (artifact.MimeType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            return mime.StartsWith("text/", StringComparison.Ordinal) || mime == "application/json" ||
                mime == "application/javascript" || mime == "application/xml" || mime == "application/yaml";
        }

        private static Dictionary<string, HtmlWorkspaceFile> Files(string source)
        {
            if (source == null) return new Dictionary<string, HtmlWorkspaceFile>(StringComparer.Ordinal);
            var snapshot = JsonConvert.DeserializeObject<WorkspaceSource>(source);
            if (snapshot?.Files == null || snapshot.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Id) || f.Content == null) ||
                snapshot.Files.GroupBy(f => f.Id, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidOperationException("Incomplete workspace snapshot.");
            return snapshot.Files.ToDictionary(f => f.Id, StringComparer.Ordinal);
        }

        private sealed class WorkspaceSource
        {
            [JsonProperty("Files", Required = Required.Always)]
            public List<HtmlWorkspaceFile> Files { get; set; }
        }

        private static void AddWorkspace(RunChangesDto result, string id, string before, string after)
        {
            var oldFiles = Files(before);
            var newFiles = Files(after);
            foreach (var key in oldFiles.Keys.Union(newFiles.Keys, StringComparer.Ordinal))
            {
                HtmlWorkspaceFile oldFile, newFile;
                oldFiles.TryGetValue(key, out oldFile); newFiles.TryGetValue(key, out newFile);
                Add(result, new RunTextChangeDto { Id = id + ":" + key, Scope = "HTML-проект",
                    Title = newFile?.Path ?? oldFile?.Path, BeforeTitle = oldFile?.Path,
                    BeforeExists = oldFile != null, AfterExists = newFile != null,
                    Before = oldFile?.Content ?? "", After = newFile?.Content ?? "", Availability = "available" });
            }
        }

        private void AddVba(RunChangesDto result, ChatSession session, string runId)
        {
            if (_query == null) return;
            var callRuns = (session.Messages ?? new List<ChatMessage>())
                .Where(m => m != null && m.ProtocolMessage && !string.IsNullOrEmpty(m.ToolCallId) && !string.IsNullOrEmpty(m.RunId))
                .GroupBy(m => m.ToolCallId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().RunId, StringComparer.OrdinalIgnoreCase);
            var correlated = callRuns.Where(pair => Same(pair.Value, runId)).Select(pair => pair.Key).ToList();
            if (correlated.Count > MaximumItems) result.Complete = false;
            VbaMutationQueryPage page;
            try { page = _query(new VbaMutationQueryRequest { RunId = runId, PageSize = MaximumItems,
                CorrelatedToolCallIds = correlated.Take(MaximumItems).ToArray() }); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException)
            { AddGap(result, "vba-journal", "Журнал VBA недоступен", "VBA", "unavailable"); result.Complete = false; return; }
            if (page.HasMore) result.Complete = false;
            var chains = new Dictionary<string, RunTextChangeDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in page.Rows.Where(r => Same(r.SessionId, session.Id)).OrderBy(r => r.FirstSequence))
            {
                string callRun;
                if (!string.IsNullOrEmpty(row.ToolCallId) && callRuns.TryGetValue(row.ToolCallId, out callRun) && !Same(callRun, runId)) continue;
                if (result.Items.Count >= MaximumItems) { result.Complete = false; break; }
                if (row.ComponentCount > MaximumItems)
                { AddGap(result, row.MutationId, row.ModuleName ?? "Пакет VBA", "VBA", "too_large"); chains.Clear(); continue; }
                VbaMutationDetail detail;
                try { detail = _detail(row.MutationId); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is ArgumentException)
                { AddGap(result, row.MutationId, row.ModuleName ?? "Пакет VBA", "VBA", "unavailable"); chains.Clear(); continue; }
                if (Same(detail.Operation, "rename") && detail.Components.Count == 2)
                {
                    var oldName = detail.Components.FirstOrDefault(c => c.BeforeExists && !c.IntendedAfterExists);
                    var newName = detail.Components.FirstOrDefault(c => !c.BeforeExists && c.IntendedAfterExists);
                    if (oldName != null && newName != null && oldName.ActualExists == false &&
                        newName.ActualExists == true && Same(newName.ActualCodeSha256, newName.IntendedAfterCodeSha256) &&
                        oldName.BeforeCode != null && newName.IntendedAfterCode != null)
                    {
                        RunTextChangeDto prior;
                        if (chains.TryGetValue(oldName.ModuleName, out prior) && prior.AfterExists && prior.After == oldName.BeforeCode)
                        {
                            prior.BeforeTitle = prior.BeforeTitle ?? oldName.ModuleName;
                            prior.Title = newName.ModuleName; prior.After = newName.IntendedAfterCode;
                        }
                        else
                        {
                            prior = new RunTextChangeDto { Id = row.MutationId, Title = newName.ModuleName,
                                BeforeTitle = oldName.ModuleName, Scope = "VBA", BeforeExists = true, AfterExists = true,
                                Before = oldName.BeforeCode, After = newName.IntendedAfterCode, Availability = "available" };
                            Add(result, prior);
                        }
                        chains.Remove(oldName.ModuleName); chains[newName.ModuleName] = prior;
                        continue;
                    }
                }
                foreach (var component in detail.Components)
                {
                    var key = component.ModuleName ?? "VBA";
                    // MatchesIntendedAfter may use a comparable hash. Only exact captured bytes may supply the after text.
                    var exactAfter = component.ActualExists == component.IntendedAfterExists &&
                        (!component.IntendedAfterExists || Same(component.ActualCodeSha256, component.IntendedAfterCodeSha256));
                    var exactBefore = component.ActualExists == component.BeforeExists &&
                        (!component.BeforeExists || Same(component.ActualCodeSha256, component.BeforeCodeSha256));
                    if (exactBefore) continue;
                    if (!exactAfter)
                    {
                        AddGap(result, row.MutationId + ":" + key, key, "VBA", "unverified"); chains.Remove(key); continue;
                    }
                    var change = new RunTextChangeDto { Id = row.MutationId + ":" + key, Title = key, Scope = "VBA",
                        BeforeExists = component.BeforeExists, AfterExists = component.IntendedAfterExists,
                        Before = component.BeforeExists ? component.BeforeCode : "",
                        After = component.IntendedAfterExists ? component.IntendedAfterCode : "", Availability = "available" };
                    if (change.Before == null || change.After == null || change.Before.Length > MaximumSourceCharacters || change.After.Length > MaximumSourceCharacters)
                    { AddGap(result, change.Id, key, "VBA", "unavailable"); chains.Remove(key); continue; }
                    RunTextChangeDto prior;
                    if (chains.TryGetValue(key, out prior) && prior.AfterExists == change.BeforeExists && prior.After == change.Before)
                    { prior.After = change.After; prior.AfterExists = change.AfterExists; }
                    else { Add(result, change); chains[key] = change; }
                }
            }
            result.Items.RemoveAll(item => item.Availability == "available" && item.BeforeExists == item.AfterExists &&
                item.Before == item.After && (item.BeforeTitle == null || item.BeforeTitle == item.Title));
        }

        private static void Add(RunChangesDto result, RunTextChangeDto item)
        {
            if (item.Availability == "available" && item.BeforeExists == item.AfterExists && item.Before == item.After &&
                (item.BeforeTitle == null || item.BeforeTitle == item.Title)) return;
            if (result.Items.Count >= MaximumItems) { result.Complete = false; return; }
            result.Items.Add(item);
        }
        private static void AddGap(RunChangesDto result, string id, string title, string scope, string availability)
        { Add(result, new RunTextChangeDto { Id = id, Title = title, Scope = scope, Availability = availability }); }
        private static bool Same(string a, string b)
        { return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
