using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceDataPlaneService
    {
        internal const string UploadOwner = "attachment-upload";
        internal const int MaximumUploadChunkBytes = 256 * 1024;
        private readonly Dictionary<string, Upload> _uploads = new Dictionary<string, Upload>(StringComparer.Ordinal);

        internal ResourceUploadOpenResponse OpenUpload(ChatSession session, ResourceUploadOpenRequest request,
            CancellationToken cancellationToken = default(CancellationToken), string owner = UploadOwner,
            long maximumBytes = AttachmentStore.MaxFileBytes, bool allowEmpty = false)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || request == null || request.ChatId != session.Id ||
                string.IsNullOrWhiteSpace(owner) || _ownerIsActive != null && !_ownerIsActive(session.Id, owner))
                throw Error("RESOURCE_ACCESS_DENIED", "An explicit addressed chat is required for upload.");
            if (maximumBytes < 1 || maximumBytes > AttachmentStore.MaxFileBytes ||
                request.ByteLength < (allowEmpty ? 0 : 1) || request.ByteLength > maximumBytes)
                throw Error("RESOURCE_BATCH_TOO_LARGE", "The upload exceeds its owner's byte bounds.");
            if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
                request.ContentType == null || request.ContentType.Length > 128 || request.ContentType.Any(char.IsControl))
                throw Error("RESOURCE_UPLOAD_INVALID", "Bounded file metadata is required.");
            lock (_sync)
            {
                EnsureActive(); Expire(); cancellationToken.ThrowIfCancellationRequested();
                if (LeaseCount >= 64 || _uploads.Count >= 4)
                    throw Error("RESOURCE_LEASE_LIMIT", "Only four uploads may be open at once.");
                if (TransferBytes + request.ByteLength > AttachmentStore.MaxMessageBytes)
                    throw Error("RESOURCE_BACKPRESSURE", "Uploads may reserve at most 50 MiB in total.");
                // One exact-sized transient buffer, reserved before reading any body. No second
                // durable store or CAS publication; completed drafts use the existing ingestion owner.
                var upload = new Upload { Id = NewLeaseId(), ChatId = session.Id, Owner = owner, FileName = request.FileName,
                    ContentType = request.ContentType, Bytes = new byte[checked((int)request.ByteLength)],
                    ExpiresUtc = _utcNow().AddMinutes(10) };
                _uploads.Add(upload.Id, upload);
                return new ResourceUploadOpenResponse { LeaseId = upload.Id, Url = Origin + "/v1/upload/" + upload.Id,
                    ByteLength = request.ByteLength, MaxChunkBytes = MaximumUploadChunkBytes, ExpiresUtc = upload.ExpiresUtc };
            }
        }

        internal void ValidateUpload(string leaseId)
        {
            Upload upload;
            lock (_sync) { upload = FindUpload(leaseId); }
            RequireActiveUpload(upload, CancellationToken.None);
        }

        internal ResourceUploadBatchResponse WriteUpload(string leaseId, int offset, int count, Stream body, CancellationToken token)
        {
            var upload = EnterUpload(leaseId, null);
            try
            {
                RequireActiveUpload(upload, token);
                if (offset != upload.Received)
                    throw Error("RESOURCE_CURSOR_INVALID", "Upload chunks must continue the exact acknowledged byte offset.");
                if (count < 1 || count > MaximumUploadChunkBytes || count > upload.Bytes.Length - offset)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "The upload chunk exceeds the negotiated bounds.");
                if (body == null || !body.CanRead) throw Error("RESOURCE_UPLOAD_INVALID", "A binary request body is required.");
                var read = 0;
                while (read < count)
                {
                    RequireActiveUpload(upload, token, false);
                    var next = body.Read(upload.Bytes, offset + read, count - read);
                    if (next == 0) throw Error("RESOURCE_UPLOAD_INVALID", "The upload chunk is truncated.");
                    read += next;
                }
                if (body.ReadByte() != -1) throw Error("RESOURCE_BATCH_TOO_LARGE", "The upload body exceeds its declared chunk length.");
                RequireActiveUpload(upload, token);
                upload.Received += count;
                return new ResourceUploadBatchResponse { LeaseId = upload.Id, NextOffset = upload.Received };
            }
            catch { lock (_sync) CancelUpload(upload); throw; }
            finally { ExitUpload(upload); }
        }

        internal ChatResourceDraftResponse CompleteUpload(ChatSession session, string leaseId,
            ChatResourceIngestionService ingestion, CancellationToken token = default(CancellationToken))
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || ingestion == null)
                throw Error("RESOURCE_ACCESS_DENIED", "An addressed ingestion owner is required.");
            ChatAttachment draft = null;
            try
            {
                return ConsumeUpload(session, leaseId, UploadOwner, (bytes, fileName, contentType) =>
                {
                    draft = ingestion.Stage(session, fileName, contentType, bytes);
                    return new ChatResourceDraftResponse { Resource = draft };
                }, token);
            }
            catch
            {
                if (draft != null) ingestion.Discard(session, draft.Id);
                throw;
            }
        }

        internal T ConsumeUpload<T>(ChatSession session, string leaseId, string owner,
            Func<byte[], string, string, T> consume, CancellationToken token)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(owner) || consume == null)
                throw Error("RESOURCE_ACCESS_DENIED", "An addressed upload consumer is required.");
            var upload = EnterUpload(leaseId, session.Id, owner);
            try
            {
                RequireActiveUpload(upload, token);
                if (upload.Received != upload.Bytes.Length)
                    throw Error("RESOURCE_UPLOAD_INCOMPLETE", "Every byte must be acknowledged before consuming the upload.");
                var result = consume(upload.Bytes, upload.FileName, upload.ContentType);
                RequireActiveUpload(upload, token);
                return result;
            }
            finally
            {
                // Completion consumes the capability, including failed/partial staging. It is
                // never automatically replayed after a lost response.
                lock (_sync) CancelUpload(upload);
                ExitUpload(upload);
            }
        }

        internal void CloseUpload(string chatId, string leaseId, string owner = UploadOwner)
        {
            lock (_sync)
            {
                Upload upload;
                if (!_uploads.TryGetValue(leaseId ?? string.Empty, out upload)) return;
                if (upload.ChatId != chatId || upload.Owner != owner)
                    throw Error("RESOURCE_ACCESS_DENIED", "The upload belongs to another chat or consumer.");
                CancelUpload(upload);
            }
        }

        internal void CloseTransfers(string chatId = null)
        {
            lock (_sync)
            {
                foreach (var upload in _uploads.Values.Where(item => chatId == null || item.ChatId == chatId).ToArray()) CancelUpload(upload);
                foreach (var download in _downloads.Values.Where(item => chatId == null || item.ChatId == chatId).ToArray()) CancelDownload(download);
            }
        }

        private Upload EnterUpload(string leaseId, string chatId, string owner = null)
        {
            lock (_sync)
            {
                var upload = FindUpload(leaseId);
                if (chatId != null && upload.ChatId != chatId || owner != null && upload.Owner != owner)
                    throw Error("RESOURCE_ACCESS_DENIED", "The upload belongs to another chat or consumer.");
                if (upload.Busy) throw Error("RESOURCE_BACKPRESSURE", "Only one operation may be in flight per upload.");
                upload.Busy = true;
                return upload;
            }
        }

        private Upload FindUpload(string leaseId)
        {
            EnsureActive(); Expire();
            Upload upload;
            if (!_uploads.TryGetValue(leaseId ?? string.Empty, out upload) || upload.Cancelled)
                throw Error("RESOURCE_LEASE_EXPIRED", "The upload lease is unknown, closed or expired.");
            return upload;
        }

        private void RequireActiveUpload(Upload upload, CancellationToken token, bool checkOwner = true)
        {
            token.ThrowIfCancellationRequested();
            if (upload.Cancelled || upload.ExpiresUtc <= _utcNow() ||
                checkOwner && _ownerIsActive != null && !_ownerIsActive(upload.ChatId, upload.Owner))
                throw Error("RESOURCE_LEASE_EXPIRED", "The upload lease or its chat owner has closed.");
        }

        private void CancelUpload(Upload upload)
        {
            upload.Cancelled = true;
            // Retain the reservation until an in-flight read/extraction leaves its lease.
            // Closing cannot grant capacity still occupied by a slow producer.
            if (!upload.Busy) { _uploads.Remove(upload.Id); upload.Bytes = null; }
        }

        private void ExitUpload(Upload upload)
        {
            lock (_sync) { upload.Busy = false; if (upload.Cancelled) CancelUpload(upload); }
        }

        private sealed class Upload
        {
            internal string Id, ChatId, Owner, FileName, ContentType;
            internal byte[] Bytes;
            internal int Received;
            internal DateTime ExpiresUtc;
            internal bool Busy;
            internal volatile bool Cancelled;
        }
    }
}
