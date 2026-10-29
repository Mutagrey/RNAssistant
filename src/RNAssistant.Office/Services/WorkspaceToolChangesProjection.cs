using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // Disposable view of the accepted call and its exact retained effect; never reads the live filesystem.
    internal sealed class WorkspaceToolChangesProjection
    {
        private readonly WorkspaceFileService _files;
        private readonly ChatBlobStore _payloads;
        private readonly Func<ResourceRef, int, string> _officeRead;
        private readonly Func<ResourceRef, ResourceRef, string, IReadOnlyList<TextPatchEdit>> _officeFragments;
        internal WorkspaceToolChangesProjection(WorkspaceFileService files, ChatBlobStore payloads,
            Func<ResourceRef, int, string> officeRead = null, Func<ResourceRef, ResourceRef, string, IReadOnlyList<TextPatchEdit>> officeFragments = null)
        { _files = files; _payloads = payloads; _officeRead = officeRead; _officeFragments = officeFragments; }

        internal RunChangesDto Read(ChatSession session, string run, string call)
        {
            var batch = PatchChangesProjection.Read(session, run, call, _payloads,
                (reference, maximum) => ResourceUri.Parse(reference.Uri).Provider == "file" ? _files.ReadRetainedComparisonText(reference, maximum) :
                    _officeRead != null ? _officeRead(reference, maximum) : throw new InvalidOperationException("Office comparison is unavailable."),
                (reference, afterReference, patch) => ResourceUri.Parse(reference.Uri).Provider == "file" ? _files.ReadRetainedPatchEdits(reference, afterReference, patch, 512000) :
                    _officeFragments != null ? _officeFragments(reference, afterReference, patch) : throw new InvalidOperationException("Office comparison is unavailable."));
            if (batch != null) return batch;
            var result = new RunChangesDto { ChatId = session.Id, RunId = run, ToolCallId = call };
            var messages = session.Messages.Where(m => m.RunId == run && m.ToolCallId == call && m.Activity == null).ToArray();
            var accepted = messages.SingleOrDefault(m => m.AcceptedCallOrigin != null);
            var terminal = messages.SingleOrDefault(m => m.AcceptedCallOrigin == null && m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion);
            if (accepted == null || terminal == null || !ResourceMutationToolCatalog.Owns(terminal.ToolName)) return result;
            var hydrated = AcceptedCallPayloadService.Hydrate(accepted, _payloads);
            var parsed = ConversationResponseHistoryReader.Read(hydrated);
            if (!parsed.Success || parsed.Response.ToolCalls.Count != 1) return result;
            var args = JsonConvert.DeserializeObject<MutationArguments>(parsed.Response.ToolCalls[0].ArgumentsJson);
            if (args?.Target == null || args.Target.Contains(": ")) return result;
            var effect = terminal.ResourceEffect;
            var unknown = terminal.ExecutionProgress?.Outcome == ToolExecutionOutcome.Unknown || effect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch;
            var impacts = (effect?.Impacts ?? new ResourceImpact[0]).Where(i =>
                i.Relation == ResourceImpactRelation.Exact && i.Identity.Uri.StartsWith("rna://file/", StringComparison.Ordinal)).ToArray();
            if (impacts.Length != 1 && !unknown) return result;
            var impact = impacts.Length == 1 ? impacts[0] : null;
            // Input context can contain many unrelated files. Without an exact effect impact
            // there is no evidence binding any one of those sources to this operation.
            var before = impact?.Before;
            var after = unknown ? null : impact?.After;
            var item = new RunTextChangeDto { Id = call, Scope = "Файл", Title = args.Destination ?? args.Target,
                BeforeTitle = args.Target, BeforeExists = before != null, AfterExists = after != null,
                Availability = unknown ? "unverified" : "available" };
            result.EvidenceFound = true;
            if (unknown && impact == null)
            {
                result.Complete = false;
                result.Items.Add(item);
                return result;
            }
            try
            {
                item.Before = before == null ? "" : _files.ReadRetainedComparisonText(before, RunChangesService.MaximumSourceCharacters);
                if (!unknown) item.After = after == null ? "" : _files.ReadRetainedComparisonText(after, RunChangesService.MaximumSourceCharacters);
                else if (before != null || terminal.ToolName == ResourceMutationToolCatalog.Write)
                {
                    var intended = terminal.ToolName == ResourceMutationToolCatalog.Write ? args.Content :
                        terminal.ToolName == ResourceMutationToolCatalog.Replace ? TextEditPlan.Replace(item.Before, args.Query, args.Replacement, args.Mode, args.MatchCase, args.ExpectedMatches).Text :
                        terminal.ToolName == ResourceMutationToolCatalog.Delete ? "" : null;
                    if (intended?.Length <= RunChangesService.MaximumSourceCharacters)
                    { item.IntendedAfter = intended; item.IntendedAfterExists = terminal.ToolName != ResourceMutationToolCatalog.Delete; }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is TextPatternException)
            {
                item.Before = item.After = item.IntendedAfter = null;
                if (!unknown) item.Availability = ex is WorkspaceFileException file && file.Code == "file_too_large" ? "too_large" : "unavailable";
                result.Complete = false;

            }
            result.Items.Add(item);
            return result;
        }

        private sealed class MutationArguments
        {
            public string Target { get; set; }
            public string Destination { get; set; }
            public string Content { get; set; }
            public string Query { get; set; }
            public string Replacement { get; set; }
            public string Mode { get; set; }
            public bool MatchCase { get; set; }
            public int ExpectedMatches { get; set; }
        }
    }
}
