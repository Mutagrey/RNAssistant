using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ResourceGatewayService
    {
        internal ResourceReadSelection ReadJsonTree(ChatSession session, ResourceRef reference,
            ResourceDescriptor descriptor, string pointer, int depth, int offset, int limit,
            int maximumCharacters, CancellationToken token)
        {
            if (!(ProviderFor(reference.Uri) is ChatArtifactResourceProvider) || descriptor == null ||
                descriptor.Kind != ChatArtifactKinds.File && descriptor.Kind != ChatArtifactKinds.Attachment ||
                !IsJsonSource(descriptor))
                throw new ResourceRequestException("JSON tree requires a complete JSON file artifact or attachment.",
                    "resource_json_view_unsupported", false);
            using (var reader = new PagedJsonTextReader(this, session, reference, token))
            {
                var tree = ResourceJsonTreeSelector.Select(reader, pointer, depth, offset, limit,
                    maximumCharacters, token);
                var source = reader.Source;
                var body = JsonConvert.SerializeObject(tree);
                var coverage = tree.Coverage();
                source.Resource.Representations.Add(ResourceRepresentations.Tree);
                source.Resource.Coverage = coverage;
                return new ResourceReadSelection { Result = new ResourceReadResult {
                    Resource = source.Resource, Representation = ResourceRepresentations.Tree, Tree = tree, Text = body,
                    ContentSha256 = source.ContentSha256, Coverage = coverage, Complete = false,
                    SourceComplete = false, Truncated = true, ReturnedCharacters = body.Length,
                    TotalCharacters = body.Length }, ResourceRefs = new[] { source.Resource.Reference } };
            }
        }

        internal static bool IsJsonSource(ResourceDescriptor descriptor)
        {
            var mime = descriptor.MimeType ?? string.Empty;
            var mediaType = mime.Split(';')[0].Trim();
            return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                (descriptor.Title ?? string.Empty).EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        // Page through the provider's cursor while retaining one revision and
        // physical text hash. JSON parsing never builds a whole-source string.
        private sealed class PagedJsonTextReader : TextReader
        {
            private readonly ResourceGatewayService _gateway;
            private readonly ChatSession _session;
            private readonly CancellationToken _token;
            private readonly HashSet<string> _cursors = new HashSet<string>(StringComparer.Ordinal);
            private ResourceRef _reference;
            private ResourceReadResult _source;
            private string _page = string.Empty;
            private string _cursor;
            private int _position;
            private long _nextOffset;
            private bool _finished;

            internal ResourceReadResult Source { get { return _source; } }

            internal PagedJsonTextReader(ResourceGatewayService gateway, ChatSession session,
                ResourceRef reference, CancellationToken token)
            { _gateway = gateway; _session = session; _reference = reference; _token = token; }

            public override int Read(char[] buffer, int index, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                while (_position == _page.Length && !_finished) NextPage();
                if (_position == _page.Length) return 0;
                var length = Math.Min(count, _page.Length - _position);
                _page.CopyTo(_position, buffer, index, length);
                _position += length;
                return length;
            }

            private void NextPage()
            {
                _token.ThrowIfCancellationRequested();
                var page = _gateway.Read(_session, new ResourceReadRequest { Reference = _reference,
                    Representation = ResourceRepresentations.Text, Cursor = _cursor,
                    MaxChars = ResourceReadRequest.MaximumCharacters }, _token);
                var result = page?.Result;
                if (result?.Resource?.Reference?.IsExact != true || result.Representation != ResourceRepresentations.Text ||
                    result.Text == null || result.SourceComplete == false || result.Offset != _nextOffset ||
                    result.ReturnedCharacters != result.Text.Length || result.TotalCharacters < 0 ||
                    result.Offset + (long)result.Text.Length > result.TotalCharacters ||
                    _source != null && (result.Resource.Reference.Uri != _source.Resource.Reference.Uri ||
                        result.Resource.Reference.Revision != _source.Resource.Reference.Revision ||
                        result.TotalCharacters != _source.TotalCharacters || result.ContentSha256 != _source.ContentSha256))
                    throw new ResourceRequestException("JSON source pages are incomplete or crossed revisions.",
                        "resource_json_source_incomplete", false);
                if (_source == null) { _source = result; _reference = result.Resource.Reference.Copy(); }
                _page = result.Text;
                _position = 0;
                _nextOffset += _page.Length;
                _finished = result.Complete;
                if (_finished)
                {
                    if (result.Truncated || _nextOffset != result.TotalCharacters ||
                        !string.IsNullOrEmpty(result.NextCursor))
                        throw new ResourceRequestException("JSON source ended before the complete exact text view.",
                            "resource_json_source_incomplete", false);
                }
                else if (_page.Length == 0 || string.IsNullOrEmpty(result.NextCursor) ||
                    !_cursors.Add(result.NextCursor))
                    throw new ResourceRequestException("JSON source continuation is unavailable.",
                        "resource_json_source_incomplete", false);
                _cursor = result.NextCursor;
            }
        }
    }
}
