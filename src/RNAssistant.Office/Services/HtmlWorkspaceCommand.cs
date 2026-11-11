using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // One prepared domain intent. Neither selection nor UI state grants authority.
    internal sealed class HtmlWorkspaceCommand
    {
        public int Version { get; set; } = 1;
        public string LogicalId { get; set; }
        public string Title { get; set; }
        public ResourceRef Snapshot { get; set; }
        public ResourceHeadState MemberHead { get; set; }
        public string Path { get; set; }
        public bool Create { get; set; }
        public bool FileAbsent { get; set; }

        internal static HtmlWorkspaceCommand Read(string json)
        {
            var value = JsonConvert.DeserializeObject<HtmlWorkspaceCommand>(json ?? "null");
            if (value == null || value.Version != 1 || string.IsNullOrEmpty(value.LogicalId) ||
                value.Create == (value.Snapshot != null))
                throw new InvalidDataException("Missing or incompatible HTML preparation. Prepare the operation again.");
            return value;
        }

        internal HtmlWorkspaceMutation Load(ChatSession actor, DocumentArtifactStore owner, ResourceAuthorityService authority)
        {
            var identity = HtmlWorkspaceIdentity.Identity(actor, LogicalId);
            var current = owner.CurrentSnapshot(actor, identity);
            if (Create)
            {
                if (current != null) throw Conflict();
                return new HtmlWorkspaceMutation(actor, LogicalId, Title, null, new HtmlWorkspace());
            }
            if (current == null || !DocumentArtifactStore.Owns(actor, Snapshot)) throw Conflict();
            var artifact = owner.Read(ConversationMetadata.From(actor), current);
            var body = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText ?? "null");
            if (body == null) throw new InvalidDataException("HTML snapshot body is unavailable.");
            var workspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(body);
            if (Path == null)
            {
                if (current.Uri != Snapshot.Uri || current.Revision != Snapshot.Revision) throw Conflict();
            }
            else
            {
                var file = workspace.Files.SingleOrDefault(item => string.Equals(item.Path, Path, StringComparison.OrdinalIgnoreCase));
                if (FileAbsent ? file != null : file == null) throw Conflict();
                var head = authority.Store.GetHead(authority.Scope(actor, true), HtmlWorkspaceIdentity.MemberIdentity(actor,
                    LogicalId, ChatHtmlResourceCatalog.FileMemberKey(file?.Id ?? Path.ToLowerInvariant())));
                if (MemberHead == null ? head != null : head == null || !head.SameAuthority(MemberHead) ||
                    head.AuthorityGeneration != MemberHead.AuthorityGeneration) throw Conflict();
            }
            return new HtmlWorkspaceMutation(actor, LogicalId, Title, artifact, workspace);
        }

        private static ResourceRequestException Conflict()
        { return new ResourceRequestException("HTML target changed after preparation. Read its current source before editing.", "source_changed", false); }
    }

    // Operation-local candidate, never a replacement chat or a durable store.
    internal sealed class HtmlWorkspaceMutation
    {
        internal ChatSession Actor { get; }
        internal string LogicalId { get; }
        internal string Title { get; }
        internal HtmlWorkspace Workspace { get; set; }
        internal ChatArtifact Snapshot { get; private set; }
        internal List<ChatArtifact> Artifacts { get; } = new List<ChatArtifact>();

        internal HtmlWorkspaceMutation(ChatSession actor, string logicalId, string title, ChatArtifact snapshot, HtmlWorkspace workspace)
        { Actor = actor; LogicalId = logicalId; Title = title; Snapshot = snapshot; Workspace = workspace; }

        // Native editor adapter: the addressed view supplies selection explicitly.
        internal static HtmlWorkspaceMutation ForEditor(ChatSession actor)
        {
            var snapshot = actor.Artifacts.SingleOrDefault(item => item.Id == actor.ActiveHtmlArtifactId);
            return new HtmlWorkspaceMutation(actor, HtmlWorkspaceIdentity.LogicalId(snapshot?.Id) ?? actor.PreparedHtmlWorkspaceId ??
                "html_ws_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), snapshot?.Title ?? "HTML workspace",
                snapshot, HtmlWorkspaceCopyService.CloneCurrent(actor.HtmlWorkspace));
        }

        internal void ProjectEditor()
        {
            Actor.Artifacts.AddRange(Artifacts.Where(item => !Actor.Artifacts.Any(existing => existing.Id == item.Id)));
            Actor.HtmlWorkspace = Workspace;
            Actor.ActiveHtmlArtifactId = Snapshot?.Id;
            HtmlWorkspaceArtifactService.RebuildNavigation(Actor);
        }

        internal void Capture(string label)
        {
            var body = JsonConvert.SerializeObject(new HtmlWorkspaceSnapshot {
                ActiveFileId = Workspace.ActiveFileId, Files = Workspace.Files, DataSources = Workspace.DataSources });
            var parent = Snapshot;
            // Compare domain state; snapshot timestamps and labels are not effects.
            if (parent != null)
            {
                var previous = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(parent.InlineText);
                if (previous.ActiveFileId == Workspace.ActiveFileId &&
                    JToken.DeepEquals(JToken.FromObject(previous.Files), JToken.FromObject(Workspace.Files)) &&
                    JToken.DeepEquals(JToken.FromObject(previous.DataSources), JToken.FromObject(Workspace.DataSources))) return;
            }
            var revision = (parent?.Revision ?? 0) + 1;
            Snapshot = new ChatArtifact {
                Id = ChatResourceUri.CreateSnapshotId(LogicalId, revision), DocumentAuthorityId = Actor.DocumentAuthorityId,
                Kind = ChatArtifactKinds.HtmlWorkspace, Title = Title, Revision = revision,
                MimeType = "application/vnd.rnassistant.html-workspace+json", ParentArtifactId = parent?.Id,
                InlineText = body, MetadataJson = new JObject {
                    ["activeFileId"] = Workspace.ActiveFileId, ["fileCount"] = Workspace.Files.Count,
                    ["dataSourceCount"] = Workspace.DataSources.Count, ["operationLabel"] = label }.ToString(Formatting.None) };
            Artifacts.Add(Snapshot);
        }
    }
}
