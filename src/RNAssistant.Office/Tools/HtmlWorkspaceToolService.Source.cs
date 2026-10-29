using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        internal static string WriteDataSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "Stable binding name opened through RN.resources.open(name).", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["json"] = new JObject { ["type"] = "string", ["description"] = "Complete exact valid JSON value serialized as text. Escape its quotes and backslashes for the outer response JSON; runtime validates and stores the decoded text unchanged.", ["minLength"] = 1, ["maxLength"] = MaxDataChars }
                },
                ["required"] = new JArray("name", "json"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        // Validate the mutable owner and the complete candidate before a batch can
        // dispatch another target. Publication still repeats its authority guard.
        internal ResourceHeadState PrepareSourcePatch(ChatSession session, ResourceIntentTarget selected, string before, string candidate)
        {
            HtmlWorkspaceArtifactService.EnsureMutable(session);
            var artifact = session.Artifacts.SingleOrDefault(a => a.Id == session.ActiveHtmlArtifactId);
            var logicalId = HtmlWorkspaceIdentity.LogicalId(artifact?.Id);
            var authority = _resources?.Authority;
            if (artifact == null || logicalId == null || authority == null)
                throw new ResourceRequestException("Select a published current HTML workspace before editing.", "html_active_revision_invalid", false);
            var owner = new RNAssistant.Core.Storage.DocumentArtifactStore(authority.Store, authority.Revisions, authority.Payloads);
            var current = owner.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, logicalId));
            var expected = ChatResourceUri.CreateArtifactRevision(session, artifact);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var file = FindFile(workspace, selected.Descriptor.Title, false);
            var member = ChatHtmlResourceCatalog.FileReference(session, artifact, file.Id);
            if (current?.Uri != expected.Uri || current.Revision != expected.Revision ||
                selected.Reference.Uri != member.Uri || selected.Reference.Revision != member.Revision ||
                (file.Content ?? string.Empty) != before)
                throw new ResourceRequestException("HTML source changed or the target names a historical snapshot. Read the current file before editing.", "RESOURCE_REVISION_CHANGED", false);
            ValidateFile(file.Path, file.Kind, candidate);
            ValidateWorkspaceCapacity(workspace, file.Id, null);
            var identity = HtmlWorkspaceIdentity.MemberIdentity(session, logicalId,
                ChatHtmlResourceCatalog.FileMemberKey(file.Id));
            var head = authority.Store.GetHead(authority.Scope(session, true), identity);
            if (head?.Knowledge != HeadKnowledge.Known || head.Revision == null)
                throw new ResourceRequestException("HTML file authority is unavailable. Reconcile its current publication before editing.", "RESOURCE_REVISION_CHANGED", false);
            return head;
        }

        private static HtmlWorkspaceToolOutcome ApplyWorkspacePatch(
            ChatSession session, IDictionary<string, object> arguments,
            Action markDispatchPossible, CancellationToken cancellationToken)
        {
            object value;
            var edits = arguments.TryGetValue("resolvedTextEdits", out value) ? value as IReadOnlyList<ResolvedTextEdit> : null;
            if (edits == null)
                return HtmlWorkspaceToolOutcome.Error("A typed prepared source edit is required.", null, "invalid_patch", false);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var file = FindFile(workspace, ToolArgumentReader.String(arguments, "path", string.Empty), false);
            var before = file.Content ?? string.Empty;
            var planned = TextEditPlan.ApplyResolved(before, edits, cancellationToken);
            ValidateFile(file.Path, file.Kind, planned.Text);
            ValidateWorkspaceCapacity(workspace, file.Id, null);
            var changed = !string.Equals(before, planned.Text, StringComparison.Ordinal);
            var preview = new RNAssistant.Office.Contracts.HtmlSourcePatchResult {
                Path = file.Path, Kind = file.Kind, Saved = changed, Changed = changed,
                OldCharacters = before.Length, NewCharacters = planned.Text.Length,
                OldLineCount = SourceLineCount(before), NewLineCount = SourceLineCount(planned.Text),
                PreviousContentSha256 = TextPatternEngine.Sha256(before), ContentSha256 = TextPatternEngine.Sha256(planned.Text),
                EditCount = planned.Edits.Count
            };
            if (!changed)
            {
                preview.RevisionArtifactId = session.ActiveHtmlArtifactId;
                return WorkspaceResult(session, "HTML source already matches: " + file.Path + ".",
                    JObject.FromObject(preview), HtmlWorkspaceEffect.VerifiedNoChange);
            }
            markDispatchPossible();
            var saved = UpsertFile(session, file.Path, file.Kind, planned.Text, false);
            preview.RevisionArtifactId = session.ActiveHtmlArtifactId;
            return WorkspaceResult(session, "HTML source updated: " + saved.Path + ".",
                JObject.FromObject(preview), HtmlWorkspaceEffect.VerifiedChange);
        }

        private static int SourceLineCount(string source)
        {
            return SourceLineStarts(source).Count;
        }

        private static List<int> SourceLineStarts(string source)
        {
            source = source ?? string.Empty;
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] == '\r')
                {
                    if (index + 1 < source.Length && source[index + 1] == '\n') index++;
                    starts.Add(index + 1);
                }
                else if (source[index] == '\n')
                {
                    starts.Add(index + 1);
                }
            }
            return starts;
        }

        private static void SourcePosition(List<int> lineStarts, int sourceIndex, out int line, out int column)
        {
            lineStarts = lineStarts ?? new List<int> { 0 };
            var found = lineStarts.BinarySearch(Math.Max(0, sourceIndex));
            var lineIndex = found >= 0 ? found : Math.Max(0, ~found - 1);
            line = lineIndex + 1;
            column = Math.Max(1, sourceIndex - lineStarts[lineIndex] + 1);
        }

    }
}
