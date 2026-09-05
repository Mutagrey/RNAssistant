using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    // An owned, disposable binary projection, not a new published resource/head or store.
    internal sealed class ResourceDownloadContent
    {
        internal byte[] Bytes;
        internal string ContentType;
    }

    internal sealed partial class ResourceDataPlaneService
    {
        internal const int MaximumDownloadChunkBytes = 256 * 1024;
        private readonly Dictionary<string, Download> _downloads = new Dictionary<string, Download>(StringComparer.Ordinal);
        private int LeaseCount { get { return _access.Count + _openings.Count + _uploads.Count + _downloads.Count; } }
        private int OpeningCount { get { return _openings.Count + _downloads.Values.Count(item => item.Capturing); } }
        private long TransferBytes { get { return _uploads.Values.Sum(item => (long)item.Bytes.Length) + _downloads.Values.Sum(item => item.ReservedBytes); } }

        internal ResourceDownloadOpenResponse OpenDownload(ChatSession session, string workspaceId, long maximumBytes,
            Func<CancellationToken, ResourceDownloadContent> capture, CancellationToken token = default(CancellationToken))
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(workspaceId) || capture == null ||
                _ownerIsActive != null && !_ownerIsActive(session.Id, workspaceId))
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit live owner is required for a download.");
            if (maximumBytes < 1 || maximumBytes > AttachmentStore.MaxMessageBytes)
                throw Error("RESOURCE_BATCH_TOO_LARGE", "The download reservation exceeds the transfer budget.");
            Download access;
            lock (_sync)
            {
                EnsureActive(); Expire(); token.ThrowIfCancellationRequested();
                if (LeaseCount >= 64 || _downloads.Count >= 2) throw Error("RESOURCE_LEASE_LIMIT", "Close existing downloads before creating another.");
                if (OpeningCount >= 4 || TransferBytes + maximumBytes > AttachmentStore.MaxMessageBytes)
                    throw Error("RESOURCE_BACKPRESSURE", "The shared capture or transfer budget is occupied.");
                access = new Download { Id = NewLeaseId(), ChatId = session.Id, WorkspaceId = workspaceId,
                    ReservedBytes = maximumBytes, Capturing = true, Busy = true, ExpiresUtc = _utcNow().AddMinutes(10) };
                _downloads.Add(access.Id, access);
            }
            try
            {
                var content = capture(token);
                RequireDownloadActive(access, token);
                if (content?.Bytes == null || content.Bytes.LongLength < 1 || content.Bytes.LongLength > maximumBytes ||
                    string.IsNullOrWhiteSpace(content.ContentType) || content.ContentType.Length > 128 || content.ContentType.Any(char.IsControl))
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The captured download violates its negotiated bounds.");
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(content.Bytes)).Replace("-", string.Empty).ToLowerInvariant();
                var payload = new PayloadRef(hash, content.Bytes.LongLength, content.ContentType);
                RequireDownloadActive(access, token);
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (access.Cancelled) throw Error("RESOURCE_LEASE_EXPIRED", "The download owner closed during capture.");
                    access.Bytes = content.Bytes; access.Payload = payload; access.ReservedBytes = content.Bytes.LongLength;
                    access.Capturing = false;
                }
                return new ResourceDownloadOpenResponse { LeaseId = access.Id, Url = Origin + "/v1/download/" + access.Id,
                    Payload = payload, ExpiresUtc = access.ExpiresUtc, MaxChunkBytes = MaximumDownloadChunkBytes };
            }
            catch { lock (_sync) CancelDownload(access); throw; }
            finally { ExitDownload(access); }
        }

        internal byte[] ReadDownload(string leaseId, int offset, int count, CancellationToken token, out string contentType)
        {
            Download access;
            lock (_sync)
            {
                EnsureActive(); Expire();
                if (!_downloads.TryGetValue(leaseId ?? string.Empty, out access) || access.Cancelled)
                    throw Error("RESOURCE_LEASE_EXPIRED", "The download is unknown, closed or expired.");
                if (access.Busy) throw Error("RESOURCE_BACKPRESSURE", "Only one operation may be in flight per download.");
                access.Busy = true;
            }
            try
            {
                RequireDownloadActive(access, token);
                if (offset != access.Offset || offset >= access.Bytes.Length)
                    throw Error("RESOURCE_CURSOR_INVALID", "Download chunks must continue this exact payload in sequence.");
                if (count < 1 || count > MaximumDownloadChunkBytes || count > access.Bytes.Length - offset)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The download chunk exceeds its negotiated bounds.");
                var bytes = new byte[count];
                Buffer.BlockCopy(access.Bytes, offset, bytes, 0, count);
                RequireDownloadActive(access, token);
                contentType = access.Payload.ContentType;
                access.Offset += count;
                return bytes;
            }
            catch { lock (_sync) CancelDownload(access); throw; }
            finally { ExitDownload(access); }
        }

        private void RequireDownloadActive(Download access, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (access.Cancelled || access.ExpiresUtc <= _utcNow() || _ownerIsActive != null && !_ownerIsActive(access.ChatId, access.WorkspaceId))
                throw Error("RESOURCE_LEASE_EXPIRED", "The download lease or its owner has closed.");
        }

        private void CancelDownload(Download access)
        {
            access.Cancelled = true;
            if (!access.Busy)
            {
                _downloads.Remove(access.Id); access.Bytes = null;
            }
        }

        private void ExitDownload(Download access)
        {
            lock (_sync) { access.Busy = false; if (access.Cancelled) CancelDownload(access); }
        }

        private sealed class Download
        {
            internal string Id, ChatId, WorkspaceId;
            internal DateTime ExpiresUtc;
            internal bool Capturing, Busy;
            internal byte[] Bytes;
            internal PayloadRef Payload;
            internal long ReservedBytes;
            internal int Offset;
            internal volatile bool Cancelled;
        }
    }
}
