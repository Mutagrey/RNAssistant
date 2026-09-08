using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Domain intent and exact immutable content only; the common observer owns publication.
    internal sealed class MarkdownDocumentService
    {
        internal const int MaximumCharacters = 200000;
        private readonly ResourceGatewayService _gateway;
        private readonly DocumentArtifactStore _artifacts;
        internal MarkdownDocumentService(ResourceGatewayService gateway, DocumentArtifactStore artifacts)
        { _gateway = gateway; _artifacts = artifacts; }

        internal static string OperationKey(ChatSession session, ToolExecutionContext context)
        { return "markdown_operation_" + TextPatternEngine.Sha256(JsonConvert.SerializeObject(new[] { session.Id, context.RunId, context.StepId, context.Call.Id })); }
        internal static ResourceIdentity OperationIdentity(ChatSession session, string key)
        { return new ResourceIdentity(ResourceUri.Create("state", "document", session.DocumentAuthorityId, key)); }

        internal MarkdownDocumentIntent Prepare(ChatSession session, ToolHandlerContext context)
        {
            var target = ToolArgumentReader.String(context.Arguments, "target", "");
            var intent = new MarkdownDocumentIntent { LogicalId = OperationKey(session, context.Execution).Replace("markdown_operation_", "artifact_md_") };
            if (!string.IsNullOrWhiteSpace(target))
            {
                intent.Base = _gateway.ResolveIntentTarget(session, target).Reference;
                var artifact = _artifacts.Read(session, intent.Base, false);
                intent.LogicalId = MarkdownDocumentIdentity.LogicalId(artifact.Id);
                if (artifact.Kind != ChatArtifactKinds.Markdown || intent.LogicalId == null)
                    throw new InvalidDataException("This target is not an authored Markdown document. Uploaded originals are immutable; create a new document explicitly.");
            }
            if (context.Execution.Call.Name == MarkdownDocumentToolCatalog.RestoreToolId)
            {
                if (intent.Base == null) throw new InvalidDataException("Restore requires an explicit Markdown target.");
                var version = ToolArgumentReader.Int32(context.Arguments, "version");
                var sources = _artifacts.SnapshotHistory(session, intent.LogicalId).Where(item => item.Revision == version).ToArray();
                if (sources.Length != 1) throw new InvalidDataException("The Markdown restore version is missing or ambiguous.");
                intent.RestoreFrom = ChatResourceUri.CreateArtifactRevision(session, sources[0]);
            }
            else ValidateText(context.Arguments);
            return intent;
        }

        internal static MarkdownDocumentIntent Intent(string preparedStateJson)
        {
            var intent = JsonConvert.DeserializeObject<MarkdownDocumentIntent>(preparedStateJson ?? "null");
            if (intent == null || string.IsNullOrWhiteSpace(intent.LogicalId))
                throw new InvalidDataException("The runtime Markdown preparation is unavailable.");
            return intent;
        }

        internal static IReadOnlyList<ResourceImpact> PreparePublication(ChatSession session, ToolExecutionContext context, string preparedStateJson,
            DocumentArtifactStore artifacts, ResourceAuthorityService authority)
        {
            try
            {
                var intent = Intent(preparedStateJson);
                var identity = MarkdownDocumentIdentity.Identity(session, intent.LogicalId);
                if (authority.Store.GetHead(authority.Scope(session, true), OperationIdentity(session, OperationKey(session, context))) != null)
                    throw new ToolMutationPreparationException("markdown_attempt_already_published", "This Markdown operation is already published. Recover the retained result without replaying the write.");
                var current = artifacts.CurrentSnapshot(session, identity);
                if (current?.Uri != intent.Base?.Uri || current?.Revision != intent.Base?.Revision)
                    throw new ToolMutationPreparationException("RESOURCE_REVISION_CHANGED", "Markdown changed in another chat. Find and read the current document before editing.");
                if (intent.Base != null) artifacts.Read(session, intent.Base);
                if (intent.RestoreFrom != null)
                {
                    var source = artifacts.Read(session, intent.RestoreFrom);
                    if (MarkdownDocumentIdentity.LogicalId(source.Id) != intent.LogicalId)
                        throw new InvalidDataException("Restore source belongs to another Markdown document.");
                }
                return new[] { new ResourceImpact(identity, ResourceImpactRelation.Exact) };
            }
            catch (InvalidDataException ex) { throw new ToolMutationPreparationException("RESOURCE_SNAPSHOT_UNAVAILABLE", ex.Message); }
        }

        internal ChatArtifact Save(ChatSession session, ToolHandlerContext context)
        {
            var intent = Intent(context.PreparedStateJson);
            var parent = intent.Base == null ? null : _artifacts.Read(session, intent.Base, false);
            var source = intent.RestoreFrom == null ? null : _artifacts.Read(session, intent.RestoreFrom);
            if (source == null) ValidateText(context.Arguments);
            var revision = checked((parent?.Revision ?? 0) + 1);
            var created = DateTime.UtcNow;
            if (parent != null && created <= parent.CreatedUtc) created = parent.CreatedUtc.AddTicks(1);
            var artifact = new ChatArtifact
            {
                Id = ChatResourceUri.CreateSnapshotId(intent.LogicalId, revision),
                DocumentAuthorityId = session.DocumentAuthorityId, Kind = ChatArtifactKinds.Markdown,
                MimeType = "text/markdown; charset=utf-8", Revision = revision, ParentArtifactId = parent?.Id,
                Title = source?.Title ?? ToolArgumentReader.String(context.Arguments, "title", ""),
                InlineText = source?.InlineText ?? ToolArgumentReader.String(context.Arguments, "markdown", ""),
                MetadataJson = new JObject { ["documentId"] = intent.LogicalId,
                    ["description"] = source == null ? ToolArgumentReader.String(context.Arguments, "description", "") : (string)JObject.Parse(source.MetadataJson)["description"] }.ToString(Formatting.None),
                CreatedUtc = created, RunId = context.Execution.RunId
            };
            var history = parent == null ? new ChatArtifact[0] : _artifacts.SnapshotHistory(session, intent.LogicalId);
            context.MarkDispatchPossible();
            session.Artifacts.RemoveAll(item => MarkdownDocumentIdentity.LogicalId(item.Id) == intent.LogicalId);
            session.Artifacts.AddRange(history);
            session.Artifacts.Add(artifact);
            return artifact;
        }

        internal static IReadOnlyList<ResourceMutationReadBack> ReadBack(ChatSession session, MutationAttempt attempt,
            ToolExecutionRecord record, string preparedStateJson, DocumentArtifactStore owner, ChatBlobStore payloads)
        {
            var intent = Intent(preparedStateJson);
            var reference = record.Result.Resources.Single();
            var artifact = session.Artifacts.Single(item => ChatResourceUri.CreateArtifactRevisionUri(session, item) == reference.Uri);
            if (MarkdownDocumentIdentity.LogicalId(artifact.Id) != intent.LogicalId ||
                artifact.Kind != ChatArtifactKinds.Markdown || artifact.InlineText == null)
                throw new InvalidDataException("Markdown read-back does not match the prepared document.");
            var key = OperationKey(session, record.Context);
            var retained = owner.RetainAuthoredSnapshot(session, artifact, attempt.AttemptId, key, intent.RestoreFrom);
            var link = PayloadRef.FromBlob(payloads.StoreText(JsonConvert.SerializeObject(retained.Revision), "application/json"));
            ArtifactWorkingSet.Set(session, artifact, false);
            return new[] { retained,
                new ResourceMutationReadBack(attempt.Target, true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(retained.Revision, kind: "immutable-snapshot") },
                    restoredFrom: intent.RestoreFrom == null ? null : owner.LogicalRevisionForSnapshot(session, attempt.Target, intent.RestoreFrom)),
                new ResourceMutationReadBack(OperationIdentity(session, key), true, "text", link.Sha256, link,
                    dependencies: new[] { new ResourceDependency(retained.Revision, kind: "operation-result") }) };
        }

        private static void ValidateText(IDictionary<string, object> arguments)
        {
            var title = ToolArgumentReader.String(arguments, "title", "");
            var markdown = ToolArgumentReader.String(arguments, "markdown", "");
            var description = ToolArgumentReader.String(arguments, "description", "");
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || string.IsNullOrWhiteSpace(markdown) || markdown.Length > MaximumCharacters ||
                string.IsNullOrWhiteSpace(description) || description.Length > 1200)
                throw new InvalidDataException("Provide a title, complete Markdown and a concise description of purpose and contents within the declared bounds.");
        }
    }

    internal sealed class MarkdownDocumentIntent
    {
        public string LogicalId { get; set; }
        public ResourceRef Base { get; set; }
        public ResourceRef RestoreFrom { get; set; }
    }
}
