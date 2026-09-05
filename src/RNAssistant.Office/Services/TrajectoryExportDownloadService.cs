using System;
using System.Collections.Generic;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class TrajectoryExportDownloadService
    {
        internal const string Owner = "trajectory-export";
        private readonly TrajectoryExportService _export;
        private readonly ResourceDataPlaneService _data;

        internal TrajectoryExportDownloadService(TrajectoryExportService export, ResourceDataPlaneService data)
        { _export = export ?? throw new ArgumentNullException(nameof(export)); _data = data ?? throw new ArgumentNullException(nameof(data)); }

        internal ChatTrajectoryExportResponse Open(ChatSession session, Func<IReadOnlyList<SessionEvent>> readCompleteEvents,
            TrajectoryExportRequest request, CancellationToken token)
        {
            TrajectoryExportResult result = null;
            var lease = _data.OpenDownload(session, Owner, TrajectoryExportService.MaximumBundleBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var events = readCompleteEvents();
                result = _export.Export(session.Host, session.DocumentKey, session.Id, events, request, cancellation);
                return new ResourceDownloadContent { Bytes = result.BundleBytes, ContentType = result.ContentType };
            }, token);
            try
            {
                token.ThrowIfCancellationRequested();
                return ChatTrajectoryExportResponse.From(session.Id, result, lease);
            }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
        }
    }
}
