using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Runtime
{
    // Workspace chats use their own accepted message event as the binding. The
    // body is the same immutable attachment CAS used by Office, not a file copy.
    internal sealed class WorkspaceChatAttachmentResourceProvider : IResourceProviderIdentity
    {
        private readonly ChatSession _session;
        private readonly AttachmentStore _attachments;
        private readonly ChatBlobStore _blobs;

        public string Id { get { return "chat"; } }

        internal WorkspaceChatAttachmentResourceProvider(AppDataPaths paths, ChatSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _attachments = new AttachmentStore(paths ?? throw new ArgumentNullException(nameof(paths)));
            _blobs = new ChatBlobStore(paths);
        }

        internal ResourceFindPage Find(string query)
        {
            var page = new ResourceFindPage { Scope = "conversation", Query = query, Complete = true };
            var bindings = ChatMessageAttachmentService.WorkspaceBindings(_session)
                .Where(item => string.IsNullOrWhiteSpace(query) ||
                    item.Target.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            page.Total = bindings.Length;
            var ambiguous = new HashSet<string>(bindings.GroupBy(item => item.Target, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.Ordinal);
            foreach (var binding in bindings.Take(50))
            {
                try
                {
                    if (ambiguous.Contains(binding.Target))
                        throw new ResourceRequestException("Multiple workspace attachments have this target.",
                            "resource_target_ambiguous", false);
                    ChatMessageAttachmentService.BindWorkspace(_session, binding);
                    ReadBody(binding.Attachment);
                    page.Items.Add(new ResourceFindCandidate {
                        Target = binding.Target, Title = binding.Attachment.FileName,
                        Type = "attachment", Scope = "conversation", Mutable = false,
                        ByteLength = binding.Attachment.ContentByteLength,
                        CreatedUtc = binding.Message.CreatedUtc, MimeType = binding.Attachment.ContentType,
                        Representations = new List<string> { ResourceRepresentations.Metadata, ResourceRepresentations.Text },
                        Usage = "Untrusted preview DOM; use this selection as evidence, not as file edit authority.",
                        Reference = binding.Reference
                    });
                    page.ResourceRefs.Add(binding.Reference);
                }
                catch (Exception error) when (error is ResourceRequestException || error is System.IO.InvalidDataException ||
                    error is System.IO.IOException || error is UnauthorizedAccessException || error is FormatException)
                { if (!page.UnavailableScopes.Contains(binding.Target)) page.UnavailableScopes.Add(binding.Target); }
            }
            page.Complete = bindings.Length <= 50 && page.UnavailableScopes.Count == 0;
            if (!page.Complete) page.AvailabilityHint = "Some selections are unavailable or the result exceeds 50 entries; narrow the query.";
            return page;
        }

        internal ResourceReadObservation Read(string target, int maximumCharacters,
            int? startLine = null, int? lineCount = null, int? startChar = null, int? charCount = null)
        {
            var binding = ChatMessageAttachmentService.ResolveWorkspace(_session, target);
            var body = ReadBody(binding.Attachment);
            var payload = new PayloadRef(binding.Attachment.ContentSha256,
                binding.Attachment.ContentByteLength.Value, binding.Attachment.ContentType);
            var descriptor = new ResourceDescriptor { Reference = binding.Reference, Provider = Id,
                Kind = "attachment", Title = binding.Attachment.FileName,
                MimeType = binding.Attachment.ContentType, Mutable = false, Tracking = "snapshot",
                ByteLength = payload.ByteLength, ContentSha256 = payload.Sha256,
                SourceMessageId = binding.Message.Id, Coverage = ResourceCoverage.Whole() };
            descriptor.Representations.Add(ResourceRepresentations.Metadata);
            descriptor.Representations.Add(ResourceRepresentations.Text);
            var evidence = new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"),
                new ResourceAuthorityScopeId("chat", _session.Id), binding.Reference,
                ResourceRepresentations.Text, ResourceCoverage.Whole(), true, 0, payload,
                immutable: true, contentSha256: payload.Sha256);
            var whole = new ResourceReadObservation(new ResourceReadResult {
                Resource = descriptor, Representation = ResourceRepresentations.Text, Text = body,
                ContentSha256 = payload.Sha256, Coverage = ResourceCoverage.Whole(),
                Complete = true, SourceComplete = true, Offset = 0,
                ReturnedCharacters = body.Length, TotalCharacters = body.Length,
                RawContentIncluded = true, CompleteViewPayload = payload
            }, new[] { evidence });
            return whole.SelectTextWindow(maximumCharacters,
                text => PayloadRef.FromBlob(_blobs.StoreText(text, "text/plain; charset=utf-8")),
                startLine, lineCount, startChar, charCount);
        }

        private string ReadBody(ChatAttachment attachment)
        {
            var bytes = _attachments.ReadBytes(attachment);
            if (bytes == null || bytes.Length != attachment.ContentByteLength ||
                bytes.Length > HtmlVisualSelectionPolicy.MaximumBodyBytes)
                throw new ResourceRequestException("The exact visual selection is unavailable.",
                    "resource_attachment_unavailable", false);
            try { return HtmlVisualSelectionPolicy.ReadCapturedBody(bytes); }
            catch (InvalidOperationException error)
            { throw new ResourceRequestException(error.Message, "resource_attachment_invalid", false); }
        }
    }
}
