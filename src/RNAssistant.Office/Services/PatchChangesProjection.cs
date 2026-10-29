using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // A disposable view over the existing terminal effect and immutable CAS sources.
    internal static class PatchChangesProjection
    {
        internal static RunChangesDto Read(ChatSession session, string run, string call, ChatBlobStore payloads,
            Func<ResourceRef, int, string> read, Func<ResourceRef, ResourceRef, string, IReadOnlyList<TextPatchEdit>> fragments, int maximumCharacters = 512000)
        {
            var messages = session.Messages.Where(m => m.RunId == run && m.ToolCallId == call && m.Activity == null).ToArray();
            var terminal = messages.LastOrDefault(m => m.AcceptedCallOrigin == null && m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion);
            if (terminal?.ToolName != ResourceMutationToolCatalog.Patch) return null;
            ToolResultWireReadResult wire; string error;
            if (!ToolResultHistoryReader.TryRead(terminal, out wire, out error)) return null;
            PatchBatchResult batch;
            try { batch = JsonConvert.DeserializeObject<PatchBatchResult>(wire.Result.DataJson ?? "{}"); }
            catch (JsonException) { return null; }
            if (batch?.Type != "rnassistant.patch" || batch.Targets == null || batch.Targets.Count == 0) return null;
            IReadOnlyList<ResourcePatchSection> sections = null;
            var accepted = messages.SingleOrDefault(m => m.AcceptedCallOrigin != null);
            if (accepted != null && payloads != null)
            {
                try
                {
                    var parsed = ConversationResponseHistoryReader.Read(AcceptedCallPayloadService.Hydrate(accepted, payloads));
                    if (parsed.Success && parsed.Response.ToolCalls.Count == 1)
                        sections = ResourcePatch.Parse(JsonConvert.DeserializeObject<PatchArguments>(parsed.Response.ToolCalls[0].ArgumentsJson).Patch);
                }
                catch (Exception ex) when (ex is JsonException || ex is TextPatternException || ex is IOException) { }
            }
            var result = new RunChangesDto { ChatId = session.Id, RunId = run, ToolCallId = call, EvidenceFound = true };
            var remaining = maximumCharacters;
            for (var i = 0; i < batch.Targets.Count && i < ResourcePatch.MaximumTargets; i++)
            {
                var row = batch.Targets[i];
                var fact = terminal.ResourceEffect?.Targets?.ElementAtOrDefault(i);
                var status = fact?.Status ?? row.Status;
                var item = new RunTextChangeDto { Id = call + ":" + i, Title = row.Target, Scope = "Текст",
                    TargetStatus = status, Availability = status == ResourceTargetStatus.Rejected ? "rejected" :
                        status == ResourceTargetStatus.NotStarted ? "not_started" : status == ResourceTargetStatus.Unknown ? "unverified" : "available" };
                result.Items.Add(item);
                if (status == ResourceTargetStatus.Rejected || status == ResourceTargetStatus.NotStarted) continue;
                try
                {
                    if (fact?.Source == null || remaining <= 0) throw new InvalidOperationException("Retained source is unavailable.");
                    item.BeforeExists = true;
                    item.Before = read(fact.Source, Math.Min(128000, remaining));
                    remaining -= item.Before.Length;
                    if (status == ResourceTargetStatus.Unknown)
                    {
                        if (sections == null || i >= sections.Count) continue;
                        var vba = ResourceUri.Parse(fact.Source.Uri).Provider == "vba";
                        var intended = TextEditPlan.Patch(item.Before, sections[i].Body,
                            normalizeLine: vba ? (Func<string, string>)VbaTextCanonicalizer.NormalizeVbeComparableLine : null).Text;
                        if (intended.Length <= remaining) { item.IntendedAfter = intended; item.IntendedAfterExists = true; remaining -= intended.Length; }
                        continue;
                    }
                    item.After = status == ResourceTargetStatus.Unchanged ? item.Before : read(fact.After, Math.Max(1, Math.Min(128000, remaining)));
                    item.AfterExists = true; remaining -= item.After.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is ResourceRequestException || ex is TextPatternException)
                {
                    item.Before = item.After = item.IntendedAfter = null;
                    if (status == ResourceTargetStatus.Unknown) continue;
                    item.Availability = "unavailable"; result.Complete = false;
                    if (status == ResourceTargetStatus.Changed && fact?.Source != null && sections != null && i < sections.Count &&
                        ResourceUri.Parse(fact.Source.Uri).Provider != "vba" && remaining > 0)
                    {
                        try
                        {
                            var edits = fragments(fact.Source, fact.After, sections[i].Body);
                            var size = edits.Sum(e => (long)e.Before.Length + e.After.Length);
                            if (size > remaining) continue;
                            item.Hunks = edits.Select(e => new RunTextHunkDto { Offset = -1, Before = e.Before, After = e.After,
                                Removed = e.Before.Length, Inserted = e.After.Length }).ToList();
                            remaining -= (int)size; item.Availability = "hunks";
                        }
                        catch (Exception missing) when (missing is IOException || missing is InvalidOperationException || missing is ArgumentException || missing is ResourceRequestException || missing is TextPatternException) { }
                    }
                }
            }
            result.Complete = result.Items.All(i => i.Availability != "unavailable");
            return result;
        }
        private sealed class PatchArguments { public string Patch { get; set; } }
        private static ResourceRevisionView View(ResourceAuthorityService authority, ResourceRef exact, ResourceAuthorityScopeId ownerScope = null)
        {
            if (authority == null || exact?.IsExact != true) throw new InvalidOperationException("Exact retained source is unavailable.");
            var address = ResourceUri.Parse(exact.Uri);
            var document = address.Provider == "state" ? address.Segments[1] : address.Segments[0];
            var scope = ownerScope ?? (address.Provider == "state" ? new ResourceAuthorityScopeId(address.Segments[0], document) :
                ResourceAuthorityScopeId.Document(new DocumentAuthorityId(document)));
            authority.RequirePublished(authority.Store.Capture(scope), exact);
            // An observed excerpt can coexist with the owner's complete retained
            // view. It must not mask that exact snapshot in the user-facing diff.
            var view = new[] { ResourceRepresentations.Source, ResourceRepresentations.Text, DocumentArtifactStore.FileTextView }
                .Select(kind => authority.Revisions.GetView(scope, exact, kind))
                .FirstOrDefault(candidate => candidate?.Payload != null && candidate.Coverage.Kind == ResourceCoverageKinds.Whole);
            if (view == null) throw new InvalidOperationException("Complete retained text is unavailable.");
            return view;
        }
        internal static string ReadSource(ResourceAuthorityService authority, ResourceRef exact, int maximum, ResourceAuthorityScopeId scope = null)
        {
            var view = View(authority, exact, scope);
            if (view.Payload.ByteLength > maximum * 4L) throw new InvalidOperationException("Source exceeds the comparison budget.");
            var text = authority.Payloads.ReadText(view.Payload.ToBlobReference());
            if (text == null || text.Length > maximum) throw new InvalidOperationException("Source exceeds the comparison budget.");
            return text;
        }
        internal static IReadOnlyList<TextPatchEdit> ReadFragments(ResourceAuthorityService authority, ResourceRef exact, ResourceRef after, string patch, ResourceAuthorityScopeId scope = null)
        {
            var view = View(authority, exact, scope);
            using (var stream = authority.Payloads.OpenVerifiedRead(view.Payload.ToBlobReference(), long.MaxValue, CancellationToken.None))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
            {
                var edits = TextPatch.Parse(patch).Resolve(reader);
                if (edits.Sum(e => (long)e.Before.Length + e.After.Length) > 512000)
                    throw new InvalidOperationException("Fragments exceed the comparison budget.");
                var afterView = View(authority, after, scope);
                using (var afterStream = authority.Payloads.OpenVerifiedRead(afterView.Payload.ToBlobReference(), long.MaxValue, CancellationToken.None))
                using (var afterReader = new StreamReader(afterStream, new UTF8Encoding(false, true), false))
                    return TextPatch.ReadAfterFragments(afterReader, edits);
            }
        }
    }
}
