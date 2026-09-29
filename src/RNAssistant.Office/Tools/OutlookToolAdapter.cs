using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class OutlookToolAdapter
    {
        private readonly OutlookService _service;
        private readonly IOutlookArchiveBackend _archiveBackend;
        internal OutlookArchiveIndexService ArchiveIndex { get; private set; }

        internal OutlookToolAdapter(IOutlookBackend backend, OutlookArchiveIndexService archiveIndex = null)
        {
            _service = new OutlookService(
                backend ?? throw new ArgumentNullException(nameof(backend)));
            _archiveBackend = backend as IOutlookArchiveBackend;
            ArchiveIndex = archiveIndex;
        }

        internal OutlookArchiveScanBatch ScanArchive(OutlookArchiveScanRequest request, CancellationToken token)
        {
            if (_archiveBackend == null || ArchiveIndex == null)
                throw new OutlookBackendException("Outlook archive indexing is unavailable.",
                    "outlook_archive_unavailable", false);
            return _archiveBackend.ScanArchive(request, token);
        }

        internal OutlookOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(
                toolId, OutlookToolIds.CreateDraft, StringComparison.Ordinal))
                return _service.CreateDraft(new OutlookCreateDraftRequest
                {
                    Kind = ToolArgumentReader.String(
                        arguments, "kind", string.Empty),
                    To = ToolArgumentReader.String(
                        arguments, "to", string.Empty),
                    Cc = ToolArgumentReader.String(
                        arguments, "cc", string.Empty),
                    Bcc = ToolArgumentReader.String(
                        arguments, "bcc", string.Empty),
                    Subject = ToolArgumentReader.String(
                        arguments, "subject", string.Empty),
                    Body = ToolArgumentReader.String(
                        arguments, "body", string.Empty)
                }, markDispatchPossible, cancellationToken);
            if (string.Equals(
                toolId, OutlookToolIds.UpdateMail, StringComparison.Ordinal))
                return _service.UpdateMail(new OutlookUpdateMailRequest
                {
                    Kind = ToolArgumentReader.String(
                        arguments, "kind", string.Empty),
                    HasCategories = arguments.ContainsKey("categories"),
                    Categories = ToolArgumentReader.String(
                        arguments, "categories", string.Empty)
                }, markDispatchPossible, cancellationToken);
            return OutlookOutcome.Error(
                "Unsupported Outlook tool: " + toolId,
                new JObject
                {
                    ["code"] = "unknown_tool",
                    ["retryable"] = false
                }.ToString(Formatting.None),
                "unknown_tool", false);
        }

    }
}
