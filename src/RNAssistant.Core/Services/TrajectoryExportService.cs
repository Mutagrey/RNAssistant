using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;

namespace RNAssistant.Core.Services
{
    /// <summary>
    /// Builds a bounded, disposable ZIP projection from an already validated chat event stream.
    /// It never writes an index or mutates canonical events/CAS.
    /// </summary>
    public sealed class TrajectoryExportService
    {
        private const int QueryPageSize = 200;
        private const int MaxExportEvents = 5000;
        private const int MaxExportRows = 2000;
        private const long MaxUncompressedBytes = 32L * 1024L * 1024L;
        private const long MaxBundleBytes = 24L * 1024L * 1024L;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly DateTimeOffset ZipTimestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly ChatBlobStore _blobs;
        private readonly ITrajectoryQuery _query;

        public TrajectoryExportService(AppDataPaths paths, Func<StorageProtector> protectionProvider, ITrajectoryQuery query)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _blobs = new ChatBlobStore(paths, protectionProvider);
            _query = query ?? throw new ArgumentNullException("query");
        }

        public TrajectoryExportResult Export(
            string host,
            string documentKey,
            string sessionId,
            IReadOnlyList<SessionEvent> events,
            TrajectoryExportRequest request)
        {
            request = NormalizeRequest(request);
            var source = (events ?? new List<SessionEvent>()).Where(item => item != null)
                .OrderBy(item => item.Sequence).ToList();
            if (source.Count == 0) throw new InvalidOperationException("The chat event stream is empty.");
            if (source.Any(item => !string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The export source contains events from another chat session.");
            }

            var selection = Select(source, request);
            if (selection.Events.Count == 0) throw new InvalidOperationException("The trajectory export selection is empty.");
            var references = CollectReferences(selection.Events);
            var generatedUtc = DateTime.UtcNow;
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            AddFile(files, "events.jsonl", SerializeEvents(selection.Events, request.RedactionMode));
            if (selection.Rows.Count > 0)
            {
                AddFile(files, "views/" + request.View + ".json", SerializeRows(selection.Rows, request.RedactionMode));
            }
            AddFile(files, "README.txt", Readme(request));

            var includedBlobCount = 0;
            if (request.IncludeCasPayloads)
            {
                foreach (var reference in references.Values.OrderBy(item => item.Reference.Sha256, StringComparer.Ordinal))
                {
                    var bytes = _blobs.ReadBytes(reference.Reference);
                    if (bytes == null)
                    {
                        throw new InvalidOperationException("Referenced CAS payload is missing, corrupt, or protected with another key: " +
                            reference.Reference.Sha256 + ".");
                    }
                    reference.Included = true;
                    reference.ExportPath = "cas/" + reference.Reference.Sha256.ToLowerInvariant() + ".blob";
                    reference.ExportSha256 = Sha256(bytes);
                    AddFile(files, reference.ExportPath, bytes);
                    includedBlobCount += 1;
                }
            }

            var manifest = BuildManifest(
                host,
                documentKey,
                sessionId,
                source,
                selection,
                references.Values,
                request,
                generatedUtc,
                files);
            AddFile(files, "manifest.json", Utf8.GetBytes(manifest.ToString(Formatting.Indented)));
            AddFile(files, "checksums.sha256", Checksums(files));
            var uncompressedBytes = files.Values.Sum(item => (long)item.Length);
            if (uncompressedBytes > MaxUncompressedBytes)
            {
                throw new InvalidOperationException("Trajectory export exceeds the " + MaxUncompressedBytes.ToString(CultureInfo.InvariantCulture) +
                    " byte uncompressed limit. Narrow the selection or exclude CAS payloads.");
            }

            var bundle = CreateZip(files);
            if (bundle.LongLength > MaxBundleBytes)
            {
                throw new InvalidOperationException("Trajectory export exceeds the " + MaxBundleBytes.ToString(CultureInfo.InvariantCulture) +
                    " byte bundle limit. Narrow the selection or exclude CAS payloads.");
            }
            var suffix = string.Equals(request.RedactionMode, TrajectoryExportRedactionModes.None, StringComparison.Ordinal)
                ? "full"
                : request.RedactionMode;
            return new TrajectoryExportResult
            {
                FileName = "rnassistant-trajectory-" + SafeId(sessionId) + "-" +
                    generatedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix + ".zip",
                BundleBytes = bundle,
                BundleSha256 = Sha256(bundle),
                RedactionMode = request.RedactionMode,
                CasPayloadsIncluded = request.IncludeCasPayloads,
                EventCount = selection.Events.Count,
                DerivedRowCount = selection.Rows.Count,
                ReferencedBlobCount = references.Count,
                IncludedBlobCount = includedBlobCount,
                UncompressedByteLength = uncompressedBytes
            };
        }

        private ExportSelection Select(IReadOnlyList<SessionEvent> source, TrajectoryExportRequest request)
        {
            if (string.Equals(request.View, TrajectoryViews.Raw, StringComparison.Ordinal))
            {
                var selected = new List<SessionEvent>();
                string cursor = null;
                do
                {
                    var page = _query.Query(source, RawQuery(request, cursor));
                    if (page.TotalMatches > MaxExportEvents)
                    {
                        throw new InvalidOperationException("Trajectory export is limited to " + MaxExportEvents + " selected events.");
                    }
                    selected.AddRange(page.Records.Select(item => item.Event));
                    cursor = page.NextCursor;
                }
                while (!string.IsNullOrWhiteSpace(cursor));
                return new ExportSelection
                {
                    Events = selected.OrderBy(item => item.Sequence).ToList(),
                    Rows = new List<TrajectoryViewRow>()
                };
            }

            var rows = new List<TrajectoryViewRow>();
            string viewCursor = null;
            do
            {
                var page = _query.QueryView(source, ViewQuery(request, viewCursor));
                if (page.TotalMatches > MaxExportRows)
                {
                    throw new InvalidOperationException("Trajectory export is limited to " + MaxExportRows + " selected derived rows.");
                }
                rows.AddRange(page.Rows);
                viewCursor = page.NextCursor;
            }
            while (!string.IsNullOrWhiteSpace(viewCursor));
            var sequences = new HashSet<long>(rows.SelectMany(item => item.SourceEventSeqs ?? new List<long>()));
            var selectedEvents = source.Where(item => sequences.Contains(item.Sequence)).ToList();
            if (selectedEvents.Count > MaxExportEvents)
            {
                throw new InvalidOperationException("Trajectory export is limited to " + MaxExportEvents + " source events.");
            }
            return new ExportSelection { Events = selectedEvents, Rows = rows };
        }

        private static TrajectoryQueryRequest RawQuery(TrajectoryExportRequest source, string cursor)
        {
            return new TrajectoryQueryRequest
            {
                Cursor = cursor,
                PageSize = QueryPageSize,
                Search = source.Search,
                MinSequence = source.MinSequence,
                MaxSequence = source.MaxSequence,
                EventTypes = source.EventTypes ?? new List<string>(),
                RunId = source.RunId,
                TurnId = source.TurnId,
                StepId = source.StepId,
                ToolCallId = source.ToolCallId,
                ArtifactId = source.ArtifactId,
                ResourceUri = source.ResourceUri,
                Status = source.Status,
                Visibility = source.Visibility
            };
        }

        private static TrajectoryViewQueryRequest ViewQuery(TrajectoryExportRequest source, string cursor)
        {
            return new TrajectoryViewQueryRequest
            {
                View = source.View,
                Cursor = cursor,
                PageSize = QueryPageSize,
                Search = source.Search,
                MinSequence = source.MinSequence,
                MaxSequence = source.MaxSequence,
                RunId = source.RunId,
                TurnId = source.TurnId,
                StepId = source.StepId,
                ToolCallId = source.ToolCallId,
                ArtifactId = source.ArtifactId,
                Status = source.Status
            };
        }

        private static TrajectoryExportRequest NormalizeRequest(TrajectoryExportRequest request)
        {
            request = request ?? new TrajectoryExportRequest();
            request.View = string.IsNullOrWhiteSpace(request.View)
                ? TrajectoryViews.Raw
                : request.View.Trim().ToLowerInvariant();
            if (!TrajectoryViews.IsSupported(request.View))
            {
                throw new ArgumentException("Unsupported trajectory export view: " + request.View + ".", "request");
            }
            request.ResourceUri = string.IsNullOrWhiteSpace(request.ResourceUri) ? null : request.ResourceUri.Trim();
            if (request.ResourceUri != null && !string.Equals(request.View, TrajectoryViews.Raw, StringComparison.Ordinal))
            {
                throw new ArgumentException("resourceUri is available only for raw trajectory export.", "request");
            }
            if (!string.IsNullOrWhiteSpace(request.RedactionMode) &&
                !TrajectoryExportRedactionModes.IsValid(request.RedactionMode.Trim()))
            {
                throw new ArgumentException("Unsupported trajectory export redaction mode: " + request.RedactionMode + ".", "request");
            }
            request.RedactionMode = TrajectoryExportRedactionModes.Normalize(request.RedactionMode);
            if (request.IncludeCasPayloads &&
                !string.Equals(request.RedactionMode, TrajectoryExportRedactionModes.None, StringComparison.Ordinal))
            {
                throw new ArgumentException("CAS payload bodies can be exported only with explicit redactionMode=none.", "request");
            }
            request.EventTypes = request.EventTypes ?? new List<string>();
            return request;
        }

        private static SortedDictionary<string, ExportReference> CollectReferences(IEnumerable<SessionEvent> events)
        {
            var result = new SortedDictionary<string, ExportReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                AddReference(result, sessionEvent.Payload, "event#" + sessionEvent.Sequence + ".payload");
                ScanTokenReferences(result, sessionEvent.Data, "event#" + sessionEvent.Sequence + ".data");
            }
            return result;
        }

        private static void ScanTokenReferences(IDictionary<string, ExportReference> references, JToken token, string location)
        {
            if (token == null) return;
            var value = token as JObject;
            if (value != null)
            {
                AddPair(references, value, "Sha256", "ByteLength", "ContentType", location);
                AddPair(references, value, "ContentSha256", "ContentByteLength", "MimeType", location);
                AddPair(references, value, "ExtractedTextSha256", "ExtractedTextByteLength", null, location);
                foreach (var property in value.Properties())
                {
                    ScanTokenReferences(references, property.Value, location + "." + property.Name);
                }
                return;
            }
            var array = token as JArray;
            if (array == null) return;
            for (var index = 0; index < array.Count; index++)
            {
                ScanTokenReferences(references, array[index], location + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
            }
        }

        private static void AddPair(
            IDictionary<string, ExportReference> references,
            JObject value,
            string hashProperty,
            string lengthProperty,
            string contentTypeProperty,
            string location)
        {
            var hash = (string)value[hashProperty];
            if (string.IsNullOrWhiteSpace(hash)) return;
            var lengthToken = value[lengthProperty];
            AddReference(references, new ChatBlobReference
            {
                Sha256 = hash,
                ByteLength = lengthToken != null && lengthToken.Type == JTokenType.Integer ? (long)lengthToken : -1,
                ContentType = contentTypeProperty == null ? null : (string)value[contentTypeProperty],
                Encryption = (string)value["Encryption"],
                ProtectionKeyId = (string)value["ProtectionKeyId"]
            }, location + "." + hashProperty);
        }

        private static void AddReference(
            IDictionary<string, ExportReference> references,
            ChatBlobReference reference,
            string location)
        {
            if (reference == null) return;
            if (!ChatBlobStore.ValidReference(reference))
            {
                throw new InvalidOperationException("The selected trajectory contains an invalid CAS reference at " + location + ".");
            }
            ExportReference existing;
            var sha256 = reference.Sha256.ToLowerInvariant();
            if (references.TryGetValue(sha256, out existing))
            {
                if (existing.Reference.ByteLength != reference.ByteLength)
                {
                    throw new InvalidOperationException("The selected trajectory contains conflicting CAS lengths for " + sha256 + ".");
                }
                existing.Locations.Add(location);
                if (string.IsNullOrWhiteSpace(existing.Reference.ContentType) && !string.IsNullOrWhiteSpace(reference.ContentType))
                {
                    existing.Reference.ContentType = reference.ContentType;
                }
                return;
            }
            references.Add(sha256, new ExportReference
            {
                Reference = new ChatBlobReference
                {
                    Sha256 = sha256,
                    ByteLength = reference.ByteLength,
                    ContentType = reference.ContentType,
                    Encryption = reference.Encryption,
                    ProtectionKeyId = reference.ProtectionKeyId
                },
                Locations = new SortedSet<string>(StringComparer.Ordinal) { location }
            });
        }

        private static byte[] SerializeEvents(IEnumerable<SessionEvent> events, string redactionMode)
        {
            var builder = new StringBuilder();
            foreach (var item in events ?? new List<SessionEvent>())
            {
                var data = ExportData(item.Data, redactionMode);
                var exported = new JObject
                {
                    ["sourceSchemaVersion"] = item.SchemaVersion,
                    ["sessionId"] = item.SessionId,
                    ["sequence"] = item.Sequence,
                    ["eventId"] = item.EventId,
                    ["createdUtc"] = item.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    ["type"] = item.Type,
                    ["runId"] = StringToken(item.RunId),
                    ["turnId"] = StringToken(item.TurnId),
                    ["stepId"] = StringToken(item.StepId),
                    ["sourcePreviousHash"] = StringToken(item.PreviousHash),
                    ["sourceHashAlgorithm"] = item.HashAlgorithm,
                    ["sourceHash"] = item.Hash,
                    ["dataRedacted"] = !string.Equals(redactionMode, TrajectoryExportRedactionModes.None, StringComparison.Ordinal),
                    ["data"] = data ?? JValue.CreateNull(),
                    ["payload"] = ReferenceToken(item.Payload)
                };
                builder.Append(exported.ToString(Formatting.None)).Append('\n');
            }
            return Utf8.GetBytes(builder.ToString());
        }

        private static byte[] SerializeRows(IEnumerable<TrajectoryViewRow> rows, string redactionMode)
        {
            var result = new JArray();
            foreach (var row in rows ?? new List<TrajectoryViewRow>())
            {
                result.Add(new JObject
                {
                    ["id"] = row.Id,
                    ["view"] = row.View,
                    ["kind"] = row.Kind,
                    ["title"] = string.Equals(redactionMode, TrajectoryExportRedactionModes.Metadata, StringComparison.Ordinal)
                        ? row.Kind
                        : row.Title,
                    ["status"] = StringToken(row.Status),
                    ["createdUtc"] = row.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    ["completedUtc"] = row.CompletedUtc.HasValue
                        ? new JValue(row.CompletedUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))
                        : JValue.CreateNull(),
                    ["durationMs"] = NullableToken(row.DurationMs),
                    ["firstSequence"] = row.FirstSequence,
                    ["lastSequence"] = row.LastSequence,
                    ["runId"] = StringToken(row.RunId),
                    ["turnId"] = StringToken(row.TurnId),
                    ["stepId"] = StringToken(row.StepId),
                    ["toolCallId"] = StringToken(row.ToolCallId),
                    ["toolId"] = StringToken(row.ToolId),
                    ["artifactId"] = StringToken(row.ArtifactId),
                    ["parentArtifactId"] = StringToken(row.ParentArtifactId),
                    ["attemptCount"] = row.AttemptCount,
                    ["failureCount"] = row.FailureCount,
                    ["promptTokens"] = NullableToken(row.PromptTokens),
                    ["completionTokens"] = NullableToken(row.CompletionTokens),
                    ["totalTokens"] = NullableToken(row.TotalTokens),
                    ["estimatedPromptTokens"] = NullableToken(row.EstimatedPromptTokens),
                    ["costUsd"] = row.CostUsd.HasValue ? new JValue(row.CostUsd.Value) : JValue.CreateNull(),
                    ["dataRedacted"] = !string.Equals(redactionMode, TrajectoryExportRedactionModes.None, StringComparison.Ordinal),
                    ["data"] = ExportData(row.Data, redactionMode) ?? JValue.CreateNull(),
                    ["sourceEventSeqs"] = new JArray(row.SourceEventSeqs ?? new List<long>()),
                    ["sourceEventIds"] = new JArray(row.SourceEventIds ?? new List<string>())
                });
            }
            return Utf8.GetBytes(result.ToString(Formatting.Indented));
        }

        private static JObject BuildManifest(
            string host,
            string documentKey,
            string sessionId,
            IReadOnlyList<SessionEvent> source,
            ExportSelection selection,
            IEnumerable<ExportReference> references,
            TrajectoryExportRequest request,
            DateTime generatedUtc,
            IDictionary<string, byte[]> files)
        {
            var referenceArray = new JArray((references ?? new List<ExportReference>())
                .OrderBy(item => item.Reference.Sha256, StringComparer.Ordinal)
                .Select(item => new JObject
                {
                    ["sourceSha256"] = item.Reference.Sha256,
                    ["byteLength"] = item.Reference.ByteLength,
                    ["contentType"] = item.Reference.ContentType,
                    ["sourceWasEncrypted"] = !string.IsNullOrWhiteSpace(item.Reference.Encryption),
                    ["locations"] = new JArray(item.Locations ?? new SortedSet<string>()),
                    ["included"] = item.Included,
                    ["exportPath"] = StringToken(item.ExportPath),
                    ["exportSha256"] = StringToken(item.ExportSha256)
                }));
            var fileArray = new JArray(files.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new JObject
            {
                ["path"] = item.Key,
                ["byteLength"] = item.Value.LongLength,
                ["sha256"] = Sha256(item.Value)
            }));
            var last = source[source.Count - 1];
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["format"] = "rnassistant.trajectory.export",
                ["generatedUtc"] = generatedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["applicationVersion"] = typeof(TrajectoryExportService).Assembly.GetName().Version.ToString(3),
                ["source"] = new JObject
                {
                    ["sessionId"] = sessionId,
                    ["host"] = host ?? string.Empty,
                    ["documentIdentitySha256"] = Sha256(Utf8.GetBytes((host ?? string.Empty) + "\n" + (documentKey ?? string.Empty))),
                    ["totalEventCount"] = source.Count,
                    ["lastSequence"] = last.Sequence,
                    ["lastEventId"] = last.EventId,
                    ["lastSourceHash"] = last.Hash,
                    ["sourceHashAlgorithm"] = last.HashAlgorithm,
                    ["protectedAtRest"] = source.Any(item => !string.IsNullOrWhiteSpace(item.EncryptedData)),
                    ["sourceIntegrityEvidenceOnly"] = true
                },
                ["selection"] = new JObject
                {
                    ["view"] = request.View,
                    ["search"] = string.Equals(request.RedactionMode, TrajectoryExportRedactionModes.Metadata, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(request.Search) ? "[REDACTED]" : request.Search ?? string.Empty,
                    ["minSequence"] = NullableToken(request.MinSequence),
                    ["maxSequence"] = NullableToken(request.MaxSequence),
                    ["eventTypes"] = new JArray(request.EventTypes ?? new List<string>()),
                    ["runId"] = StringToken(request.RunId),
                    ["turnId"] = StringToken(request.TurnId),
                    ["stepId"] = StringToken(request.StepId),
                    ["toolCallId"] = StringToken(request.ToolCallId),
                    ["artifactId"] = StringToken(request.ArtifactId),
                    ["resourceUri"] = StringToken(request.ResourceUri),
                    ["status"] = StringToken(request.Status),
                    ["visibility"] = StringToken(request.Visibility)
                },
                ["redaction"] = new JObject
                {
                    ["mode"] = request.RedactionMode,
                    ["eventDataRedacted"] = !string.Equals(request.RedactionMode, TrajectoryExportRedactionModes.None, StringComparison.Ordinal),
                    ["casPayloadsIncluded"] = request.IncludeCasPayloads,
                    ["notice"] = RedactionNotice(request.RedactionMode)
                },
                ["counts"] = new JObject
                {
                    ["selectedEvents"] = selection.Events.Count,
                    ["derivedRows"] = selection.Rows.Count,
                    ["referencedBlobs"] = referenceArray.Count,
                    ["includedBlobs"] = referenceArray.Count(item => (bool)item["included"])
                },
                ["references"] = referenceArray,
                ["files"] = fileArray
            };
        }

        private static JToken ExportData(JToken data, string redactionMode)
        {
            if (data == null) return null;
            if (string.Equals(redactionMode, TrajectoryExportRedactionModes.Metadata, StringComparison.Ordinal))
            {
                return new JObject { ["redacted"] = true };
            }
            if (string.Equals(redactionMode, TrajectoryExportRedactionModes.Secrets, StringComparison.Ordinal))
            {
                return RedactSecrets(data);
            }
            return data.DeepClone();
        }

        private static JToken RedactSecrets(JToken token)
        {
            var value = token as JObject;
            if (value != null)
            {
                var result = new JObject();
                foreach (var property in value.Properties())
                {
                    result[property.Name] = SensitiveProperty(property.Name)
                        ? new JValue("[REDACTED]")
                        : RedactSecrets(property.Value);
                }
                return result;
            }
            var array = token as JArray;
            if (array != null) return new JArray(array.Select(RedactSecrets));
            return token.DeepClone();
        }

        private static bool SensitiveProperty(string name)
        {
            var normalized = new string((name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized.Contains("authorization") || normalized.Contains("apikey") ||
                normalized.Contains("password") || normalized.Contains("passwd") ||
                normalized.Contains("clientsecret") || normalized.Contains("historysecret") ||
                normalized.Contains("accesskey") || normalized.Contains("secretkey") ||
                normalized.Contains("accesstoken") || normalized.Contains("refreshtoken") ||
                normalized.Contains("idtoken") || normalized.Contains("credential") ||
                normalized == "cookie" || normalized == "setcookie" || normalized == "bearer";
        }

        private static JToken ReferenceToken(ChatBlobReference reference)
        {
            return reference == null
                ? (JToken)JValue.CreateNull()
                : new JObject
                {
                    ["sourceSha256"] = reference.Sha256,
                    ["byteLength"] = reference.ByteLength,
                    ["contentType"] = reference.ContentType
                };
        }

        private static JToken StringToken(string value)
        {
            return value == null ? (JToken)JValue.CreateNull() : new JValue(value);
        }

        private static JToken NullableToken<T>(T? value) where T : struct
        {
            return value.HasValue ? JToken.FromObject(value.Value) : JValue.CreateNull();
        }

        private static byte[] Readme(TrajectoryExportRequest request)
        {
            var text = "RNAssistant trajectory export\r\n\r\n" +
                "This ZIP is a disposable projection, not the canonical chat event stream.\r\n" +
                "Verify exported files with checksums.sha256. Source event hashes are retained as evidence,\r\n" +
                "but redacted/decrypted export records are intentionally not a replacement hash chain.\r\n\r\n" +
                "Redaction mode: " + request.RedactionMode + "\r\n" + RedactionNotice(request.RedactionMode) + "\r\n";
            return Utf8.GetBytes(text);
        }

        private static string RedactionNotice(string mode)
        {
            if (string.Equals(mode, TrajectoryExportRedactionModes.Metadata, StringComparison.Ordinal))
            {
                return "Event/row data and CAS bodies are excluded; ids, timestamps, source hashes and CAS reference metadata remain.";
            }
            if (string.Equals(mode, TrajectoryExportRedactionModes.Secrets, StringComparison.Ordinal))
            {
                return "Known credential-named fields are redacted, but prompts, document text and other content may remain sensitive; CAS bodies are excluded.";
            }
            return "No redaction: decrypted event data is included and CAS bodies may be included. Treat this bundle as sensitive.";
        }

        private static byte[] Checksums(IDictionary<string, byte[]> files)
        {
            var builder = new StringBuilder();
            foreach (var item in files.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                builder.Append(Sha256(item.Value)).Append("  ").Append(item.Key).Append('\n');
            }
            return Utf8.GetBytes(builder.ToString());
        }

        private static void AddFile(IDictionary<string, byte[]> files, string path, byte[] bytes)
        {
            if (files.ContainsKey(path)) throw new InvalidOperationException("Duplicate trajectory export path: " + path + ".");
            files.Add(path, bytes ?? new byte[0]);
            if (files.Values.Sum(item => (long)item.Length) > MaxUncompressedBytes)
            {
                throw new InvalidOperationException("Trajectory export exceeds the uncompressed size limit. Narrow the selection or exclude CAS payloads.");
            }
        }

        private static byte[] CreateZip(IDictionary<string, byte[]> files)
        {
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var item in files.OrderBy(value => value.Key, StringComparer.Ordinal))
                    {
                        var entry = archive.CreateEntry(item.Key, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTimestamp;
                        using (var stream = entry.Open()) stream.Write(item.Value, 0, item.Value.Length);
                    }
                }
                return output.ToArray();
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes ?? new byte[0]))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string SafeId(string value)
        {
            var safe = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(12).ToArray()).ToLowerInvariant();
            return safe.Length == 0 ? "chat" : safe;
        }

        private sealed class ExportSelection
        {
            public List<SessionEvent> Events { get; set; }
            public List<TrajectoryViewRow> Rows { get; set; }

            public ExportSelection()
            {
                Events = new List<SessionEvent>();
                Rows = new List<TrajectoryViewRow>();
            }
        }

        private sealed class ExportReference
        {
            public ChatBlobReference Reference { get; set; }
            public SortedSet<string> Locations { get; set; }
            public bool Included { get; set; }
            public string ExportPath { get; set; }
            public string ExportSha256 { get; set; }

            public ExportReference()
            {
                Locations = new SortedSet<string>(StringComparer.Ordinal);
            }
        }
    }
}
