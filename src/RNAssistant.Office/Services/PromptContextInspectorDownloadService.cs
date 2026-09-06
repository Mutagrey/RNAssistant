using System;
using System.Text;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed class PromptContextInspectorDownloadService
    {
        internal const string Owner = "context-inspector";
        internal const int MaximumBytes = 2 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly ResourceDataPlaneService _data;

        internal PromptContextInspectorDownloadService(ResourceDataPlaneService data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        internal PromptContextInspectorResponse Open(ChatSession session,
            Func<PromptContextInspectorResponse> capture, CancellationToken token)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            PromptContextInspectorResponse response = null;
            var lease = _data.OpenDownload(session, Owner, MaximumBytes, cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                response = capture();
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (response == null || response.ChatId != session.Id || response.RawRequestJson == null ||
                        Utf8.GetByteCount(response.RawRequestJson) > MaximumBytes)
                        throw new InvalidOperationException("RESOURCE_SNAPSHOT_UNAVAILABLE: invalid Inspector capture.");
                    return new ResourceDownloadContent { Bytes = Utf8.GetBytes(response.RawRequestJson), ContentType = "text/plain; charset=utf-8" };
                }
                finally { if (response != null) response.RawRequestJson = null; }
            }, token);
            try
            {
                token.ThrowIfCancellationRequested();
                response.RawData = lease;
                return response;
            }
            catch { _data.Close(session.Id, Owner, lease.LeaseId); throw; }
            finally { if (response != null) response.RawRequestJson = null; }
        }
    }
}
