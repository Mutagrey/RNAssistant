using System;
using System.Collections.Generic;
using System.IO;
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
        // Already authenticated by the source owner, for example CAS OpenVerifiedRead.
        // Ownership transfers to the download lease after a successful open.
        internal Stream VerifiedStream;
        internal PayloadRef VerifiedPayload;
        internal string ContentType;
    }

    internal sealed partial class ResourceDataPlaneService
    {
        internal const int MaximumDownloadChunkBytes = 256 * 1024;
        internal const long MaximumInFlightTransferBytes = 50L * 1024 * 1024;
        private readonly Dictionary<string, Download> _downloads = new Dictionary<string, Download>(StringComparer.Ordinal);
        private int LeaseCount { get { return _access.Count + _openings.Count + _uploads.Count + _downloads.Count; } }
        private int OpeningCount { get { return _openings.Count + _downloads.Values.Count(item => item.Capturing); } }
        private long TransferBytes { get { return (long)_uploads.Count * MaximumUploadChunkBytes + _downloads.Values.Sum(item => item.ReservedBytes) +
            _openings.Sum(item => item.ReservedBytes) + _access.Values.Sum(item => item.ReservedBytes); } }

        internal ResourceDownloadOpenResponse OpenDownload(ChatSession session, string workspaceId, long maximumBytes,
            Func<CancellationToken, ResourceDownloadContent> capture, CancellationToken token = default(CancellationToken))
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(workspaceId) || capture == null ||
                _ownerIsActive != null && !_ownerIsActive(session.Id, workspaceId))
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit live owner is required for a download.");
            if (maximumBytes < 1)
                throw Error("RESOURCE_BATCH_TOO_LARGE", "The download reservation exceeds the transfer budget.");
            Download access;
            lock (_sync)
            {
                EnsureActive(); Expire(); token.ThrowIfCancellationRequested();
                if (LeaseCount >= 64 || _downloads.Count >= 2) throw Error("RESOURCE_LEASE_LIMIT", "Close existing downloads before creating another.");
                if (OpeningCount >= 4 || TransferBytes + MaximumDownloadChunkBytes > MaximumInFlightTransferBytes)
                    throw Error("RESOURCE_BACKPRESSURE", "The shared capture or transfer budget is occupied.");
                access = new Download { Id = NewLeaseId(), ChatId = session.Id, WorkspaceId = workspaceId,
                    ReservedBytes = MaximumDownloadChunkBytes, Capturing = true, Busy = true, ExpiresUtc = _utcNow().AddMinutes(10) };
                _downloads.Add(access.Id, access);
            }
            ResourceDownloadContent content = null;
            try
            {
                content = capture(token);
                RequireDownloadActive(access, token);
                var streamed = content?.VerifiedStream != null;
                if (content == null || streamed == (content.Bytes != null) ||
                    streamed && (content.VerifiedPayload == null || !content.VerifiedStream.CanRead) ||
                    (streamed ? content.VerifiedPayload.ByteLength : content.Bytes.LongLength) > maximumBytes ||
                    string.IsNullOrWhiteSpace(content.ContentType) || content.ContentType.Length > 128 || content.ContentType.Any(char.IsControl))
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The captured download violates its negotiated bounds.");
                PayloadRef payload;
                if (streamed)
                    payload = new PayloadRef(content.VerifiedPayload.Sha256, content.VerifiedPayload.ByteLength, content.ContentType);
                else
                {
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(content.Bytes)).Replace("-", string.Empty).ToLowerInvariant();
                    payload = new PayloadRef(hash, content.Bytes.LongLength, content.ContentType);
                }
                RequireDownloadActive(access, token);
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (access.Cancelled) throw Error("RESOURCE_LEASE_EXPIRED", "The download owner closed during capture.");
                    access.Bytes = content.Bytes; access.Stream = content.VerifiedStream; access.Payload = payload;
                    if (streamed) access.Hash = SHA256.Create();
                    content.VerifiedStream = null;
                    access.Capturing = false;
                }
                return new ResourceDownloadOpenResponse { LeaseId = access.Id, Url = Origin + "/v1/download/" + access.Id,
                    Payload = payload, ExpiresUtc = access.ExpiresUtc, MaxChunkBytes = MaximumDownloadChunkBytes };
            }
            catch { content?.VerifiedStream?.Dispose(); lock (_sync) CancelDownload(access); throw; }
            finally { ExitDownload(access); }
        }

        internal byte[] ReadDownload(string leaseId, long offset, int count, CancellationToken token, out string contentType)
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
                if (offset != access.Offset || offset >= access.Payload.ByteLength)
                    throw Error("RESOURCE_CURSOR_INVALID", "Download chunks must continue this exact payload in sequence.");
                if (count < 1 || count > MaximumDownloadChunkBytes || count > access.Payload.ByteLength - offset)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The download chunk exceeds its negotiated bounds.");
                var bytes = new byte[count];
                if (access.Stream == null) Buffer.BlockCopy(access.Bytes, checked((int)offset), bytes, 0, count);
                else
                {
                    var received = 0;
                    while (received < count)
                    {
                        token.ThrowIfCancellationRequested();
                        var read = access.Stream.Read(bytes, received, count - received);
                        if (read <= 0) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The verified download ended before its advertised length.");
                        received += read;
                    }
                }
                RequireDownloadActive(access, token);
                if (access.Hash != null)
                {
                    access.Hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    if (offset + count == access.Payload.ByteLength)
                    {
                        access.Hash.TransformFinalBlock(new byte[0], 0, 0);
                        var actual = BitConverter.ToString(access.Hash.Hash).Replace("-", string.Empty).ToLowerInvariant();
                        if (!string.Equals(actual, access.Payload.Sha256, StringComparison.Ordinal))
                            throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The streamed download differs from its verified source identity.");
                        access.Hash.Dispose(); access.Hash = null;
                    }
                }
                contentType = access.Payload.ContentType;
                access.Offset += count;
                if (access.Offset == access.Payload.ByteLength)
                { access.Stream?.Dispose(); access.Stream = null; }
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
                access.Stream?.Dispose(); access.Stream = null;
                access.Hash?.Dispose(); access.Hash = null;
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
            internal Stream Stream;
            internal SHA256 Hash;
            internal PayloadRef Payload;
            internal long ReservedBytes;
            internal long Offset;
            internal volatile bool Cancelled;
        }
    }
}
