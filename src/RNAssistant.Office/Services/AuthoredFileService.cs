using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class AuthoredFileService
    {
        private readonly ResourceGatewayService _gateway;
        private readonly DocumentArtifactStore _artifacts;
        private readonly ChatBlobStore _payloads;

        internal AuthoredFileService(ResourceGatewayService gateway, DocumentArtifactStore artifacts, ChatBlobStore payloads)
        { _gateway = gateway; _artifacts = artifacts; _payloads = payloads; }

        internal static string OperationKey(ChatSession session, ToolExecutionContext context)
        { return "file_operation_" + TextPatternEngine.Sha256(JsonConvert.SerializeObject(new[] { session.Id, context.RunId, context.StepId, context.Call.Id + (context.TargetOrdinal.HasValue ? "#patch:" + context.TargetOrdinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") })); }
        internal static ResourceIdentity OperationIdentity(ChatSession session, string key)
        { return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, key)); }
        private static string LogicalId(ChatSession session, ToolExecutionContext context)
        { return "artifact_file_" + OperationKey(session, context).Substring("file_operation_".Length); }
        private static string SnapshotLogicalId(string id)
        {
            var marker = (id ?? "").IndexOf("_r", StringComparison.Ordinal);
            var prefix = marker < 0 ? null : id.Substring(0, marker);
            return prefix != null && prefix.StartsWith("artifact_file_", StringComparison.Ordinal) &&
                prefix.Length == "artifact_file_".Length + 64 ? prefix : null;
        }
        internal static ResourceIdentity Identity(ChatSession session, string logicalId)
        { return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, logicalId)); }

        internal AuthoredFileIntent Prepare(ChatSession session, ToolHandlerContext context, CancellationToken token)
        {
            var operation = context.DomainOperation;
            var intent = new AuthoredFileIntent { LogicalId = LogicalId(session, context.Execution) };
            if (operation == AuthoredFileToolCatalog.PatchToolId || operation == AuthoredFileToolCatalog.UpdateToolId)
            {
                intent.Base = _gateway.ResolveIntentTarget(session, ToolArgumentReader.String(context.Arguments, "target", "")).Reference;
                var artifact = _artifacts.Read(session, intent.Base, false);
                intent.LogicalId = SnapshotLogicalId(artifact.Id);
                if (artifact.Kind != ChatArtifactKinds.File || intent.LogicalId == null || DocumentArtifactStore.FileEncoding(artifact) == null)
                    throw new InvalidDataException("Select a current authored text file, not an uploaded original.");
                RequireCurrentBase(session, intent, _artifacts);
                intent.Source = JsonConvert.DeserializeObject<AuthoredFileMetadata>(artifact.MetadataJson ?? "{}")?.Source;
                if (operation == AuthoredFileToolCatalog.PatchToolId)
                {
                    var patch = ObservedPatch(session, context, intent.Base);
                    intent.Patch = _artifacts.PrepareFilePatch(session, intent.Base, patch, token);
                    RequirePatchObservation(session, context, intent);
                }
            }
            else if (operation == AuthoredFileToolCatalog.CopyToolId)
            {
                intent.Source = _gateway.ResolveIntentTarget(session, ToolArgumentReader.String(context.Arguments, "source", "")).Reference;
                var source = _artifacts.Read(session, intent.Source, false);
                if (source.OriginalAttachment?.Kind != "text" &&
                    !(source.Kind == ChatArtifactKinds.File && DocumentArtifactStore.FileEncoding(source) != null))
                    throw new InvalidDataException("Copy requires a complete original or authored text file.");
                intent.Name = RequireName(ToolArgumentReader.String(context.Arguments, "name", source.Title));
            }
            else if (operation == AuthoredFileToolCatalog.CreateToolId)
            {
                RequireName(ToolArgumentReader.String(context.Arguments, "name", ""));
                var content = ToolArgumentReader.String(context.Arguments, "content", "");
                DocumentArtifactStore.EncodeFileText(content, "utf8");
            }
            else throw new InvalidDataException("Unknown authored file operation.");
            return intent;
        }

        internal static AuthoredFileIntent Intent(string json)
        {
            var intent = JsonConvert.DeserializeObject<AuthoredFileIntent>(json ?? "null");
            if (intent == null || string.IsNullOrWhiteSpace(intent.LogicalId))
                throw new InvalidDataException("The prepared file intent is unavailable.");
            return intent;
        }

        internal static IReadOnlyList<ResourceImpact> PreparePublication(ChatSession session, ToolExecutionContext context,
            string preparedStateJson, DocumentArtifactStore artifacts, ResourceAuthorityService authority)
        {
            var intent = Intent(preparedStateJson);
            var identity = Identity(session, intent.LogicalId);
            if (authority.Store.GetHead(authority.Scope(session, true), OperationIdentity(session, OperationKey(session, context))) != null)
                throw new ToolMutationPreparationException("file_attempt_already_published", "This file operation is already published.");
            try { RequireCurrentBase(session, intent, artifacts); }
            catch (ResourceRequestException ex) { throw new ToolMutationPreparationException(ex.ErrorCode, ex.Message); }
            if (intent.Base != null) artifacts.Read(session, intent.Base, false);
            if (intent.Source != null) artifacts.Read(session, intent.Source, false);
            return new[] { new ResourceImpact(identity, ResourceImpactRelation.Exact) };
        }

        private static void RequireCurrentBase(ChatSession session, AuthoredFileIntent intent, DocumentArtifactStore artifacts)
        {
            var current = artifacts.CurrentSnapshot(session, Identity(session, intent.LogicalId));
            if (current?.Uri != intent.Base?.Uri || current?.Revision != intent.Base?.Revision)
                throw new ResourceRequestException("The file changed. Find and read its current version before editing.", "RESOURCE_REVISION_CHANGED", false);
        }

        internal AuthoredFileSave Save(ChatSession session, ToolHandlerContext context, CancellationToken token)
        {
            var intent = Intent(context.PreparedStateJson);
            var operation = context.DomainOperation;
            ChatArtifact parent = null;
            ChatArtifact source = null;
            byte[] bytes = null;
            PayloadRef staged = null;
            string name;
            string encoding;
            string mime;
            var changes = new List<AuthoredFileChange>();
            if (operation == AuthoredFileToolCatalog.PatchToolId)
            {
                parent = _artifacts.Read(session, intent.Base, false);
                RequireCurrentBase(session, intent, _artifacts);
                // Reconstruct from the same immutable source, including after a pending
                // confirmation/restart. Preparation is not a retained second store.
                var patch = _artifacts.PrepareFilePatch(session, intent.Base,
                    ObservedPatch(session, context, intent.Base), token);
                if (intent.Patch == null || patch.Source.Sha256 != intent.Patch.Source.Sha256 ||
                    patch.Result.Sha256 != intent.Patch.Result.Sha256 || patch.Result.ByteLength != intent.Patch.Result.ByteLength)
                    throw new ResourceRequestException("The prepared file changed. Read it again.", "source_changed", false);
                if (patch.Result.Sha256 == patch.Source.Sha256)
                    return new AuthoredFileSave { Artifact = parent, Changes = changes, Unchanged = true };
                staged = patch.Result;
                encoding = DocumentArtifactStore.FileEncoding(parent);
                name = parent.Title; mime = parent.MimeType;
                changes.AddRange(patch.Edits.Select(edit => new AuthoredFileChange {
                    Offset = edit.Offset, Removed = edit.Before.Length, Inserted = edit.After.Length,
                    Before = Preview(edit.Before), After = Preview(edit.After) }));
            }
            else if (operation == AuthoredFileToolCatalog.UpdateToolId)
            {
                parent = _artifacts.Read(session, intent.Base, false);
                var previous = DocumentArtifactStore.DecodeFileText(_artifacts.ExportBytes(session, intent.Base), out encoding);
                var content = ToolArgumentReader.String(context.Arguments, "content", "");
                bytes = DocumentArtifactStore.EncodeFileText(content, encoding);
                name = parent.Title; mime = parent.MimeType;
                object edits;
                if (context.Arguments.TryGetValue("resolvedTextEdits", out edits))
                    changes.AddRange(JsonConvert.DeserializeObject<List<ResolvedTextEdit>>(JsonConvert.SerializeObject(edits))
                        .Select(edit => new AuthoredFileChange { Offset = edit.Offset, Removed = edit.Before.Length,
                            Inserted = edit.After.Length, Before = Preview(edit.Before), After = Preview(edit.After) }));
                else changes.Add(new AuthoredFileChange { Offset = 0, Removed = previous.Length, Inserted = content.Length,
                    Before = Preview(previous), After = Preview(content) });
            }
            else if (operation == AuthoredFileToolCatalog.CopyToolId)
            {
                source = _artifacts.Read(session, intent.Source, false);
                bytes = _artifacts.ExportBytes(session, intent.Source);
                DocumentArtifactStore.DecodeFileText(bytes, out encoding);
                name = intent.Name;
                mime = string.Equals(name, source.Title, StringComparison.Ordinal)
                    ? MediaType(source.MimeType, name) : Mime(name);
            }
            else
            {
                encoding = "utf8";
                bytes = DocumentArtifactStore.EncodeFileText(ToolArgumentReader.String(context.Arguments, "content", ""), encoding);
                name = RequireName(ToolArgumentReader.String(context.Arguments, "name", ""));
                mime = Mime(name);
            }
            if (staged == null) staged = PayloadRef.FromBlob(_payloads.StoreBytes(bytes, mime));
            var revision = checked((parent?.Revision ?? 0) + 1);
            var artifact = new ChatArtifact {
                Id = ChatResourceUri.CreateSnapshotId(intent.LogicalId, revision), Revision = revision,
                ParentArtifactId = parent?.Id, Kind = ChatArtifactKinds.File, Title = name,
                MimeType = mime, DocumentAuthorityId = session.DocumentAuthorityId,
                ContentSha256 = staged.Sha256, ContentByteLength = staged.ByteLength,
                MetadataJson = JsonConvert.SerializeObject(new AuthoredFileMetadata {
                    FileEncoding = encoding, Source = intent.Source,
                    Changes = changes }), RunId = context.Execution.RunId
            };
            if (parent != null && artifact.CreatedUtc <= parent.CreatedUtc) artifact.CreatedUtc = parent.CreatedUtc.AddTicks(1);
            var history = parent == null ? new ChatArtifact[0] : _artifacts.SnapshotHistory(session, intent.LogicalId);
            context.MarkDispatchPossible();
            session.Artifacts.RemoveAll(item => SnapshotLogicalId(item.Id) == intent.LogicalId);
            session.Artifacts.AddRange(history);
            session.Artifacts.Add(artifact);
            return new AuthoredFileSave { Artifact = artifact, Changes = changes };
        }

        internal static IReadOnlyList<ResourceMutationReadBack> ReadBack(ChatSession session, MutationAttempt attempt,
            ToolExecutionRecord record, string preparedStateJson, DocumentArtifactStore owner, ChatBlobStore payloads)
        {
            var intent = Intent(preparedStateJson);
            var reference = record.Result.Resources.Single();
            var artifact = session.Artifacts.Single(item => ChatResourceUri.CreateArtifactRevisionUri(session, item) == reference.Uri);
            if (SnapshotLogicalId(artifact.Id) != intent.LogicalId || artifact.Kind != ChatArtifactKinds.File ||
                !artifact.ContentByteLength.HasValue)
                throw new InvalidDataException("File read-back does not match the prepared intent.");
            var payload = new PayloadRef(artifact.ContentSha256, artifact.ContentByteLength.Value, artifact.MimeType);
            if (intent.Patch != null && (payload.Sha256 != intent.Patch.Result.Sha256 || payload.ByteLength != intent.Patch.Result.ByteLength))
                throw new InvalidDataException("The written file differs from its prepared patch.");
            var key = OperationKey(session, record.Context);
            var retained = owner.RetainFileSnapshot(session, artifact, payload, attempt.AttemptId, key, intent.Source);
            var link = PayloadRef.FromBlob(payloads.StoreText(JsonConvert.SerializeObject(retained.Revision), "application/json"));
            ArtifactWorkingSet.Set(session, artifact, false);
            return new[] { retained,
                new ResourceMutationReadBack(attempt.Target, true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(retained.Revision, kind: "immutable-snapshot") }),
                new ResourceMutationReadBack(OperationIdentity(session, key), true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(retained.Revision, kind: "operation-result") }) };
        }

        private static TextPatch ObservedPatch(ChatSession session, ToolHandlerContext context, ResourceRef reference)
        {
            var patch = TextPatch.FromArguments(context.Arguments);
            var accepted = session.Messages.FirstOrDefault(m => m.AcceptedCallOrigin != null && m.ToolCallId == context.Execution.Call.Id);
            return accepted == null ? patch : patch.WithCoverage(TextPatch.Coverage(
                (accepted.ResourceEvidence ?? new List<ResourceEvidence>()).Where(e => e.Resource.Uri == reference.Uri && e.Resource.Revision == reference.Revision)));
        }

        private static void RequirePatchObservation(ChatSession session, ToolHandlerContext context,
            AuthoredFileIntent intent)
        {
            var accepted = session.Messages.FirstOrDefault(message => message.AcceptedCallOrigin != null &&
                message.ToolCallId == context.Execution.Call.Id);
            if (accepted == null) return; // Manual editor uses the same current-head guard.
            var evidence = (accepted.ResourceEvidence ?? new List<ResourceEvidence>()).Where(item =>
                item.Resource.Uri == intent.Base.Uri && item.Resource.Revision == intent.Base.Revision).ToArray();
            if (evidence.Any(item => item.Complete && item.Coverage.Kind == ResourceCoverageKinds.Whole)) return;
            var ranges = evidence.Where(item => item.Coverage.Kind == ResourceCoverageKinds.CharacterRange).ToArray();
            if (ranges.Length > 0 && intent.Patch.Edits.All(edit => ranges.Any(item =>
                item.Coverage.Start <= edit.Offset && item.Coverage.End >= edit.Offset + edit.Before.Length))) return;
            throw new ResourceRequestException("Read the current file and all changed lines before patching it.", "source_observation_required", false);
        }

        private static string RequireName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 200 || name != Path.GetFileName(name) ||
                name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(name), @"^\.[A-Za-z0-9]{1,12}$"))
                throw new InvalidDataException("Use a plain file name with a supported extension.");
            return name;
        }
        private static string Mime(string name)
        {
            var extension = Path.GetExtension(name);
            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" :
                string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain";
        }
        private static string MediaType(string source, string name)
        {
            var media = (source ?? string.Empty).Split(';')[0].Trim();
            return media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                media == "application/json" || media == "application/javascript" || media == "application/xml"
                ? media : Mime(name);
        }
        private static string Preview(string text)
        { return text.Length <= 240 ? text : text.Substring(0, 240); }

    }

    internal sealed class AuthoredFileIntent
    {
        public string LogicalId { get; set; }
        public ResourceRef Base { get; set; }
        public ResourceRef Source { get; set; }
        public string Name { get; set; }
        public FilePatchPreparation Patch { get; set; }
    }
    internal sealed class AuthoredFileChange
    {
        [JsonProperty("offset")] public long Offset { get; set; }
        [JsonProperty("removed")] public int Removed { get; set; }
        [JsonProperty("inserted")] public int Inserted { get; set; }
        [JsonProperty("before")] public string Before { get; set; }
        [JsonProperty("after")] public string After { get; set; }
    }
    internal sealed class AuthoredFileMetadata
    {
        [JsonProperty("fileEncoding")] public string FileEncoding { get; set; }
        [JsonProperty("source")] public ResourceRef Source { get; set; }
        [JsonProperty("changes")] public List<AuthoredFileChange> Changes { get; set; }
    }
    internal sealed class AuthoredFileSave
    {
        internal ChatArtifact Artifact { get; set; }
        internal bool Unchanged { get; set; }
        internal List<AuthoredFileChange> Changes { get; set; }
    }
}
