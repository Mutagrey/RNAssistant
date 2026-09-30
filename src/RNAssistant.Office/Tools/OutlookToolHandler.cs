using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class OutlookToolHandler : IReadOnlyToolHandler, IManagedMutationToolHandler
    {
        private readonly string _toolId;
        private readonly OutlookToolAdapter _adapter;
        private readonly HostRuntime _runtime;
        private readonly ChatSession _session;
        private readonly OutlookSearchResourceService _search;

        internal OutlookToolHandler(
            string toolId,
            OutlookToolAdapter adapter,
            HostRuntime runtime,
            ChatSession session, ResourceGatewayService gateway)
        {
            if (!OutlookToolIds.Owns(toolId))
                throw new ArgumentException(
                    "An exact Outlook tool id is required.", nameof(toolId));
            _toolId = toolId;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _session = session;
            _search = new OutlookSearchResourceService(gateway);
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (!OutlookToolIds.Owns(toolId)) return null;
            if (toolId == OutlookToolIds.SearchMail) return new ToolBinding("outlook.search.mail.resource.v1");
            if (toolId == OutlookToolIds.IndexArchive) return new ToolBinding("outlook.index.archive.v1");
            return new ToolBinding(
                "outlook." +
                toolId.Substring("outlook.".Length).Replace('_', '.') +
                ".v1");
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (_session == null)
                return OfficeToolFailure.Rejected(
                    "Outlook operations require an active chat session.",
                    "outlook_session_required");
            try
            {
                if (_toolId == OutlookToolIds.SearchMail)
                    return Task.FromResult(_runtime.ReadDocument(Target(_session), cancellationToken, delegate {
                        context.MarkDispatchPossible();
                        return _search.Search(_session, context.Arguments, cancellationToken);
                    }));
                if (_toolId == OutlookToolIds.IndexArchive)
                {
                    if (!(_session.DocumentKey ?? string.Empty).StartsWith("outlook-mailbox:", StringComparison.Ordinal))
                        return OfficeToolFailure.Rejected(
                            "Archive indexing requires a mailbox chat. Open the Outlook account chat, then retry there.",
                            "outlook_mailbox_required");
                    return Task.FromResult(IndexArchive(context, cancellationToken));
                }
                var outcome = OutlookToolIds.IsRead(_toolId)
                    ? _runtime.ReadDocument(
                        Target(_session), cancellationToken, delegate
                        {
                            context.MarkDispatchPossible();
                            return _adapter.Execute(
                                _toolId, context.Arguments, null,
                                cancellationToken);
                        })
                    : _runtime.ExecuteDocumentMutation(
                        Target(_session), cancellationToken, delegate
                        {
                            return _adapter.Execute(
                                _toolId, context.Arguments,
                                context.MarkDispatchPossible,
                                cancellationToken);
                        }, terminalOutcome => context.Complete(Result(terminalOutcome)), context.CompleteFailure);
                return Task.FromResult(Result(outcome));
            }
            catch (OfficeDocumentGuardException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Guard(ex);
            }
            catch (HostRuntime.MutationLockException ex)
                when (!context.MayHaveDispatched)
            {
                return OfficeToolFailure.Lock(ex);
            }
        }

        private ToolHandlerResult IndexArchive(ToolHandlerContext context, CancellationToken token)
        {
            var from = ReadUtcDate(context.Arguments, "fromDate", DateTime.UtcNow.Date.AddMonths(-6));
            var through = ReadUtcDate(context.Arguments, "throughDate", DateTime.UtcNow.Date);
            var to = through.AddDays(1);
            var includePst = ToolArgumentReader.Boolean(context.Arguments, "includePst", true);
            var refresh = ToolArgumentReader.Boolean(context.Arguments, "refresh", false);
            var maxBatches = ToolArgumentReader.Int32(context.Arguments, "maxBatches", 20);
            if (from >= to || maxBatches < 1 || maxBatches > 20)
                return new ToolHandlerResult(RuntimeResult.Error("Invalid archive period or batch count."),
                    ToolEffectEvidence.None);
            var index = _adapter.ArchiveIndex;
            if (index == null)
                return new ToolHandlerResult(RuntimeResult.Error("Outlook archive indexing is unavailable."),
                    ToolEffectEvidence.None);
            var manifest = index.Open(_session.DocumentKey, from, to, includePst, refresh);
            for (var i = 0; i < maxBatches && !manifest.Complete; i++)
            {
                token.ThrowIfCancellationRequested();
                var request = new OutlookArchiveScanRequest { FromUtc = from, ToUtc = to,
                    IncludePst = includePst, Cursor = manifest.Cursor,
                    ExpectedSourceSignature = manifest.SourceSignature, MaxMessages = 25 };
                var batch = _runtime.ReadDocument(Target(_session), token, delegate {
                    context.MarkDispatchPossible();
                    return _adapter.ScanArchive(request, token);
                });
                index.Append(manifest, batch);
            }
            var summary = new { fromDate = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                throughDate = through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                includePst, manifest.Complete, manifest.ExaminedItems, manifest.IndexedMessages,
                manifest.UniqueMessages,
                manifest.CapturedBodies, manifest.FailedBodies, manifest.AttachmentCandidates,
                manifest.Errors, archivePages = manifest.Pages.Count,
                firstPageTarget = manifest.Pages.Count == 0 ? null : "Outlook archive page: " +
                    from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." +
                    through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    (includePst ? " / mailbox+PST" : " / mailbox") + " / page 1",
                firstDigestTarget = manifest.UniqueMessages == 0 ? null : "Outlook archive digest: " +
                    from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." +
                    through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    (includePst ? " / mailbox+PST" : " / mailbox") + " / page 1",
                coverage = manifest.Complete && manifest.Errors == 0 && manifest.FailedBodies == 0
                    ? "Enumerated folders scanned with no observed read errors; messages can change during a scan. Attachment contents remain unexamined."
                    : "Incomplete: inspect errors, failed bodies, remaining scan or unexamined attachments." };
            return new ToolHandlerResult(RuntimeResult.Ok(
                manifest.Complete ? "Archive scan finished." : "Archive scan checkpoint saved; repeat to continue.",
                JsonConvert.SerializeObject(summary)), ToolEffectEvidence.None);
        }

        private static DateTime ReadUtcDate(IDictionary<string, object> arguments, string name, DateTime fallback)
        {
            var value = ToolArgumentReader.String(arguments, name, string.Empty);
            if (string.IsNullOrWhiteSpace(value)) return DateTime.SpecifyKind(fallback.Date, DateTimeKind.Utc);
            DateTime parsed;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
                throw new OutlookBackendException(name + " must use yyyy-MM-dd.", "invalid_arguments", false);
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        private static ToolHandlerResult Result(OutlookOutcome outcome)
        {
            if (outcome == null)
                throw new InvalidOperationException(
                    "Outlook operation returned no outcome.");
            RuntimeResult result;
            if (outcome.Status == OutlookOutcomeStatus.Ok)
                result = RuntimeResult.Ok(outcome.Message, outcome.DataJson);
            else if (outcome.Status == OutlookOutcomeStatus.Unknown)
                result = RuntimeResult.Unknown(outcome.Message, outcome.DataJson);
            else result = RuntimeResult.Error(outcome.Message, outcome.DataJson);
            return new ToolHandlerResult(result, Effect(outcome.Effect),
                recovery: outcome.Status == OutlookOutcomeStatus.Error
                    ? OfficeToolFailure.DefiniteDomain(outcome.Retryable) : null);
        }

        private static ToolEffectEvidence Effect(OutlookEffect effect)
        {
            switch (effect)
            {
                case OutlookEffect.VerifiedNoChange:
                    return ToolEffectEvidence.VerifiedNoChange;
                case OutlookEffect.VerifiedChange:
                    return ToolEffectEvidence.VerifiedChange;
                case OutlookEffect.Unknown:
                    return ToolEffectEvidence.Unknown;
                default:
                    return ToolEffectEvidence.None;
            }
        }

        private static OfficeDocumentExecutionExpectation Target(
            ChatSession session)
        {
            return new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null
                    ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

    }
}
