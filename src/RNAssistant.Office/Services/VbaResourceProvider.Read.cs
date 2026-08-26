using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed partial class VbaResourceProvider
    {
        private const int MaximumReadCharacters = 32000;
        private const int MaximumMaterializedCharacters = 1000000;
        private const int MaximumSearchCharacters = 1000000;
        private const int MaximumSearchCharactersPerResource = 128000;
        private const string TruncatedMarker = "\n...[truncated]";

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            return _scope.Read(session, delegate
            {
                var resourceUri = request == null || request.Reference == null
                    ? string.Empty
                    : request.Reference.Uri;
                VbaResourceTarget target;
                if (!TryResolveTarget(session, resourceUri, out target))
                {
                    throw MissingResource(resourceUri);
                }
                var representation = NormalizeRepresentation(
                    request == null ? null : request.Representation,
                    target.Project);
                if (representation == ResourceRepresentations.Metadata)
                {
                    ResourceReadCursor.RejectCursor(request);
                    return MetadataSelection(session, resourceUri, target);
                }
                var position = ResourceReadCursor.ParseRevisionBound(request);
                if (target.Project)
                {
                    var manifest = JsonConvert.SerializeObject(new
                    {
                        type = "rnassistant.vbaProject",
                        resource = ProjectUri(session),
                        components = LoadModules().Select(module => DescribeComponent(session, module, null)).ToList(),
                        backupCount = LoadBackups().Count
                    });
                    return SelectText(
                        resourceUri,
                        DescribeProject(session),
                        ResourceRepresentations.Structure,
                        manifest,
                        false,
                        request,
                        position);
                }
                if (target.Module != null)
                {
                    var source = ReadModuleSource(session, target.Module, MaximumMaterializedCharacters);
                    return SelectText(
                        resourceUri,
                        DescribeComponent(session, target.Module, source.CodeSha256),
                        ResourceRepresentations.Source,
                        source.Code,
                        source.Truncated,
                        request,
                        position);
                }
                var backup = ReadBackup(target.Backup);
                return SelectText(
                    resourceUri,
                    DescribeBackup(session, backup),
                    ResourceRepresentations.Source,
                    backup.Code ?? string.Empty,
                    false,
                    request,
                    position);
            });
        }

        public ResourceSearchResult Search(
            ChatSession session,
            string query,
            string kind,
            int limit,
            int maxCharsPerMatch)
        {
            return _scope.Read(session, delegate
            {
                query = (query ?? string.Empty).Trim();
                if (query.Length == 0)
                {
                    throw new ResourceRequestException(
                        "Resource search query is required.",
                        "resource_query_required",
                        true);
                }
                limit = Math.Max(1, Math.Min(20, limit <= 0 ? 10 : limit));
                maxCharsPerMatch = Math.Max(128, Math.Min(2000, maxCharsPerMatch <= 0 ? 600 : maxCharsPerMatch));
                var result = new ResourceSearchResult { Query = query };
                if (string.IsNullOrWhiteSpace(kind) ||
                    string.Equals(kind, ComponentKind, StringComparison.OrdinalIgnoreCase))
                {
                    SearchComponents(session, query, limit, maxCharsPerMatch, result);
                }
                if (result.Matches.Count < limit &&
                    (string.IsNullOrWhiteSpace(kind) ||
                     string.Equals(kind, BackupKind, StringComparison.OrdinalIgnoreCase)))
                {
                    SearchBackups(session, query, limit, maxCharsPerMatch, result);
                }
                return result;
            });
        }

        private void SearchComponents(
            ChatSession session,
            string query,
            int limit,
            int maxCharsPerMatch,
            ResourceSearchResult result)
        {
            foreach (var module in LoadModules())
            {
                if (result.Matches.Count >= limit)
                {
                    result.ScanTruncated = true;
                    break;
                }
                var metadata = module.Name + " " + module.ComponentType;
                var metadataIndex = metadata.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (metadataIndex >= 0)
                {
                    AddMetadataMatch(
                        query,
                        metadata,
                        metadataIndex,
                        maxCharsPerMatch,
                        new ResourceRef(ComponentUri(session, module.Name)),
                        ComponentKind,
                        module.Name,
                        result);
                    continue;
                }
                var remaining = MaximumSearchCharacters - result.ScannedCharacters;
                if (remaining <= 0)
                {
                    result.ScanTruncated = true;
                    break;
                }
                var source = ReadModuleSource(
                    session,
                    module,
                    Math.Min(MaximumSearchCharactersPerResource, remaining));
                result.ScannedCharacters += source.Code.Length;
                result.ScanTruncated = result.ScanTruncated || source.Truncated;
                AddMatches(
                    query,
                    source.Code,
                    limit,
                    maxCharsPerMatch,
                    new ResourceRef(ComponentUri(session, module.Name), source.CodeSha256),
                    ComponentKind,
                    module.Name,
                    result);
            }
        }

        private void SearchBackups(
            ChatSession session,
            string query,
            int limit,
            int maxCharsPerMatch,
            ResourceSearchResult result)
        {
            foreach (var metadata in LoadBackups())
            {
                if (result.Matches.Count >= limit)
                {
                    result.ScanTruncated = true;
                    break;
                }
                var metadataText = string.Join(" ", new[]
                {
                    metadata.BackupId,
                    metadata.ModuleName,
                    metadata.ComponentType,
                    metadata.MutationId
                }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
                var metadataIndex = metadataText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (metadataIndex >= 0)
                {
                    AddMetadataMatch(
                        query,
                        metadataText,
                        metadataIndex,
                        maxCharsPerMatch,
                        new ResourceRef(BackupUri(session, metadata.BackupId), metadata.CodeSha256),
                        BackupKind,
                        metadata.ModuleName + " backup",
                        result);
                    continue;
                }
                var remaining = MaximumSearchCharacters - result.ScannedCharacters;
                if (remaining <= 0)
                {
                    result.ScanTruncated = true;
                    break;
                }
                var backup = ReadBackup(metadata);
                var code = backup.Code ?? string.Empty;
                var scanLength = Math.Min(code.Length, Math.Min(MaximumSearchCharactersPerResource, remaining));
                var scanned = code.Substring(0, scanLength);
                result.ScannedCharacters += scanLength;
                if (scanLength < code.Length) result.ScanTruncated = true;
                AddMatches(
                    query,
                    scanned,
                    limit,
                    maxCharsPerMatch,
                    new ResourceRef(BackupUri(session, backup.BackupId), backup.CodeSha256),
                    BackupKind,
                    backup.ModuleName + " backup",
                    result);
            }
        }

        private static void AddMatches(
            string query,
            string content,
            int limit,
            int maxCharsPerMatch,
            ResourceRef reference,
            string kind,
            string title,
            ResourceSearchResult result)
        {
            content = content ?? string.Empty;
            var index = 0;
            while (result.Matches.Count < limit &&
                (index = content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var start = Math.Max(0, index - maxCharsPerMatch / 3);
                result.Matches.Add(new ResourceSearchMatch
                {
                    Reference = reference,
                    Kind = kind,
                    Title = title,
                    Representation = ResourceRepresentations.Source,
                    MatchOffset = index,
                    MatchLength = query.Length,
                    SnippetOffset = start,
                    Snippet = content.Substring(start, Math.Min(maxCharsPerMatch, content.Length - start))
                });
                index += Math.Max(1, query.Length);
            }
            if (result.Matches.Count >= limit &&
                content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ScanTruncated = true;
            }
        }

        private static void AddMetadataMatch(
            string query,
            string metadata,
            int index,
            int maxCharsPerMatch,
            ResourceRef reference,
            string kind,
            string title,
            ResourceSearchResult result)
        {
            var start = Math.Max(0, index - maxCharsPerMatch / 3);
            result.Matches.Add(new ResourceSearchMatch
            {
                Reference = reference,
                Kind = kind,
                Title = title,
                Representation = ResourceRepresentations.Metadata,
                MatchOffset = index,
                MatchLength = query.Length,
                SnippetOffset = start,
                Snippet = metadata.Substring(start, Math.Min(maxCharsPerMatch, metadata.Length - start))
            });
        }

        private VbaModuleSource ReadModuleSource(ChatSession session, VbaResourceModule module, int maxChars)
        {
            var result = _source.ReadResourceModule(session, module.Name, maxChars);
            EnsureSuccess(result, "VBA component source could not be read: " + module.Name + ".");
            try
            {
                var data = JObject.Parse(result.DataJson ?? "{}");
                var code = (string)data["code"] ?? string.Empty;
                var truncated = (bool?)data["truncated"] == true;
                if (truncated && code.EndsWith(TruncatedMarker, StringComparison.Ordinal))
                {
                    code = code.Substring(0, code.Length - TruncatedMarker.Length);
                }
                return new VbaModuleSource
                {
                    Code = code,
                    CodeSha256 = (string)data["codeSha256"] ?? TextPatternEngine.Sha256(code),
                    Truncated = truncated
                };
            }
            catch (JsonException ex)
            {
                throw new ResourceRequestException(
                    "VBA component source is invalid: " + ex.Message,
                    "vba_read_invalid",
                    true);
            }
        }

        private VbaModuleBackup ReadBackup(VbaModuleBackup metadata)
        {
            if (metadata == null || _journal == null)
            {
                throw new KeyNotFoundException("VBA backup resource was not found.");
            }
            try
            {
                var backup = _journal.Find(
                    _adapter.HostName,
                    _adapter.DocumentKey,
                    metadata.BackupId,
                    null);
                if (backup == null) throw new KeyNotFoundException("VBA backup resource was not found.");
                return backup;
            }
            catch (VbaJournalException ex)
            {
                throw new ResourceRequestException(ex.Message, "vba_backup_unavailable", false);
            }
        }

        private ResourceReadSelection MetadataSelection(
            ChatSession session,
            string resourceUri,
            VbaResourceTarget target)
        {
            var descriptor = target.Project
                ? DescribeProject(session)
                : target.Module != null
                    ? DescribeComponent(session, target.Module, null)
                    : DescribeBackup(session, target.Backup);
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = descriptor,
                    Representation = ResourceRepresentations.Metadata,
                    Complete = true
                },
                ResourceRefs = new[]
                {
                    descriptor.Reference == null
                        ? new ResourceRef(resourceUri)
                        : new ResourceRef(resourceUri, descriptor.Reference.Revision)
                }
            };
        }

        private static ResourceReadSelection SelectText(
            string resourceUri,
            ResourceDescriptor descriptor,
            string representation,
            string content,
            bool sourceTruncated,
            ResourceReadRequest request,
            ResourceReadPosition position)
        {
            content = content ?? string.Empty;
            var offset = position == null ? 0 : position.Offset;
            var maxChars = request == null ? 0 : request.MaxChars;
            maxChars = Math.Max(128, Math.Min(MaximumReadCharacters, maxChars <= 0 ? 8000 : maxChars));
            var contentSha256 = descriptor.ContentSha256 ?? TextPatternEngine.Sha256(content);
            ResourceReadCursor.ValidateLive(request, position, contentSha256);
            if (offset > content.Length)
            {
                throw new ResourceRequestException(
                    "Resource read offset exceeds the available VBA representation.",
                    "resource_cursor_invalid",
                    true);
            }
            var length = Math.Min(maxChars, content.Length - offset);
            var next = offset + length;
            var complete = next >= content.Length && !sourceTruncated;
            descriptor.ContentSha256 = contentSha256;
            if (descriptor.Reference != null) descriptor.Reference.Revision = contentSha256;
            return new ResourceReadSelection
            {
                Result = new ResourceReadResult
                {
                    Resource = descriptor,
                    Representation = representation,
                    Text = content.Substring(offset, length),
                    ContentSha256 = contentSha256,
                    Offset = offset,
                    ReturnedCharacters = length,
                    TotalCharacters = content.Length,
                    NextCursor = next < content.Length
                        ? ResourceReadCursor.CreateRevisionBound(next, contentSha256)
                        : null,
                    Complete = complete,
                    Truncated = !complete,
                    RawContentIncluded = true
                },
                ResourceRefs = new[] { new ResourceRef(resourceUri, contentSha256) }
            };
        }

        private static string NormalizeRepresentation(string value, bool project)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value == "auto")
            {
                return project ? ResourceRepresentations.Structure : ResourceRepresentations.Source;
            }
            if (value == ResourceRepresentations.Metadata) return value;
            if (project && value == ResourceRepresentations.Structure) return value;
            if (!project && value == ResourceRepresentations.Source) return value;
            throw new ResourceRequestException(
                "VBA representation is unavailable: " + value + ".",
                "resource_representation_unavailable",
                true);
        }

        private sealed class VbaModuleSource
        {
            public string Code { get; set; }
            public string CodeSha256 { get; set; }
            public bool Truncated { get; set; }
        }
    }
}
