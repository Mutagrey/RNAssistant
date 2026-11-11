using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
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

        internal ChatArtifact RequireCurrentSource(ChatSession actor, ResourceIntentTarget selected)
        {
            var exact = ChatResourceUri.ArtifactSnapshot(selected.Reference);
            string id;
            if (!ChatResourceUri.TryParseArtifactRevision(exact, out _, out id, out _) || !RNAssistant.Core.Storage.DocumentArtifactStore.Owns(actor, exact))
                throw new ResourceRequestException("HTML belongs to another document.", "RESOURCE_ACCESS_DENIED", false);
            var logical = HtmlWorkspaceIdentity.LogicalId(id);
            var current = DocumentOwner.CurrentSnapshot(actor, HtmlWorkspaceIdentity.Identity(actor, logical));
            if (current?.Uri != exact.Uri || current.Revision != exact.Revision || selected.Target.Contains("HTML snapshot: "))
                throw new ResourceRequestException("Historical HTML cannot be edited. Read its working target.", "source_changed", false);
            return DocumentOwner.Read(ConversationMetadata.From(actor), current);
        }

        internal ResourceHeadState PrepareSourceMutation(ChatSession actor, ResourceIntentTarget selected, string before, string candidate)
        {
            var artifact = RequireCurrentSource(actor, selected);
            var logicalId = HtmlWorkspaceIdentity.LogicalId(artifact.Id);
            var body = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText);
            var workspace = HtmlWorkspaceCopyService.CreateWorkspaceFromSnapshot(body);
            var file = FindFile(workspace, selected.Descriptor.Title, false);
            if ((file.Content ?? "") != before) throw new ResourceRequestException("HTML source changed.", "source_changed", false);
            ValidateFile(file.Path, file.Kind, candidate);
            ValidateWorkspaceCapacity(workspace, file.Id, null);
            var identity = HtmlWorkspaceIdentity.MemberIdentity(actor, logicalId, ChatHtmlResourceCatalog.FileMemberKey(file.Id));
            var head = _resources.Authority.Store.GetHead(_resources.Authority.Scope(actor, true), identity);
            if (head?.Knowledge != HeadKnowledge.Known || head.Revision == null)
                throw new ResourceRequestException("HTML file authority is unavailable.", "source_changed", false);
            return head;
        }

        private HtmlWorkspaceToolOutcome ApplyWorkspacePatch(
            HtmlWorkspaceMutation session, IDictionary<string, object> arguments,
            Action markDispatchPossible, CancellationToken cancellationToken)
        {
            object value;
            var edits = arguments.TryGetValue("resolvedTextEdits", out value) ? value as IReadOnlyList<ResolvedTextEdit> : null;
            if (edits == null)
                return HtmlWorkspaceToolOutcome.Error("A typed prepared source edit is required.", null, "invalid_patch", false);
            var workspace = NormalizedWorkspaceCopy(session.Workspace);
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
                preview.RevisionArtifactId = session.Snapshot?.Id;
                return WorkspaceResult(session, "HTML source already matches: " + file.Path + ".",
                    JObject.FromObject(preview), HtmlWorkspaceEffect.VerifiedNoChange);
            }
            markDispatchPossible();
            var saved = UpsertFile(session, file.Path, file.Kind, planned.Text, false);
            preview.RevisionArtifactId = session.Snapshot?.Id;
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
