using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        private static bool IsEligibleDataSourceTool(ToolCatalogEntry tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id) || !tool.Enabled || !tool.AgentCanRun ||
                !tool.CanSourceHtmlData || tool.MutatesDocument || tool.MutatesLocalState || tool.RequiresConfirmation)
            {
                return false;
            }

            JObject ignoredSchema;
            string ignoredError;
            return ToolSchemaSupport.TryParse(tool, out ignoredSchema, out ignoredError);
        }

        internal string BuildBindDescription()
        {
            return "Workspace: Bind the most recent successful approved Office read from this Agent run as a refreshable HTML data source. " +
                "Run the intended source read first; its exact tool and arguments are reused from accepted runtime evidence. " +
                "Use transform=table only when that result contains row arrays; it returns an rnassistant.table.v1 envelope with columns, object rows, rowCount, and scalar source metadata. " +
                "Page code reads the saved value through RNAssistant.data.get(name). Eligible reads: " +
                string.Join(", ", _dataSourceTools.Keys.ToArray()) + ".";
        }

        internal static string BindSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["name"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Stable data-source name exposed through window.RNAssistantData.",
                        ["minLength"] = 1,
                        ["maxLength"] = 128
                    },
                    ["transform"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("raw", "table"),
                        ["description"] = "raw preserves source JSON; table returns {schema:'rnassistant.table.v1',source,columns:[{key,label,type}],rows:[{...}],rowCount}.",
                        ["default"] = "raw"
                    },
                    ["headers"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("firstRow", "none"),
                        ["description"] = "For table transform, firstRow uses row 1 as labels; none generates labels.",
                        ["default"] = "firstRow"
                    }
                },
                ["required"] = new JArray("name"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private HtmlWorkspaceToolOutcome BindDataSource(
            ChatSession session,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            EnsureAdapterMatchesSession(session);
            var name = NormalizeDataName(ToolArgumentReader.String(
                arguments, "name", string.Empty));
            var transform = NormalizeTransform(ToolArgumentReader.String(
                arguments, "transform", "raw"));
            var headers = NormalizeHeaders(ToolArgumentReader.String(
                arguments, "headers", "firstRow"));
            const string refreshPolicy = "on_preview";
            var accepted = HtmlAcceptedReadSourceResolver.Resolve(
                session, _dataSourceTools);
            ToolCatalogEntry sourceTool;
            JObject normalizedSourceArguments;
            var normalizedArguments = BuildSourceArguments(
                accepted.ToolId, accepted.Arguments, out sourceTool,
                out normalizedSourceArguments);
            if (normalizedArguments == null)
                throw new InvalidOperationException(
                    "Accepted HTML binding source arguments are unavailable.");

            cancellationToken.ThrowIfCancellationRequested();
            var json = TransformSourceJson(
                accepted.DataJson, transform, headers);
            ValidateDataSource(name, json);
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var id = DataSourceId(name);
            ValidateWorkspaceCapacity(workspace, null, null, id, json);
            markDispatchPossible();
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var now = DateTime.UtcNow;
            var data = session.HtmlWorkspace.DataSources.FirstOrDefault(item =>
                item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (data == null)
            {
                data = new HtmlWorkspaceDataSource { Id = id, Name = name, CreatedUtc = now };
                session.HtmlWorkspace.DataSources.Add(data);
            }

            data.Name = name;
            data.Json = json;
            data.Binding = new HtmlWorkspaceDataBinding
            {
                ToolId = sourceTool.Id,
                ArgumentsJson = normalizedSourceArguments.ToString(Formatting.None),
                Transform = transform,
                Headers = headers,
                RefreshPolicy = refreshPolicy,
                Host = _adapter.HostName,
                DocumentKey = session.DocumentKey,
                DocumentTitle = session.DocumentTitle,
                Status = "ready",
                LastError = null,
                PayloadCompleteness = SourcePayloadCompleteness(accepted.DataJson),
                ContentSha256 = TextPatternEngine.Sha256(json),
                CreatedUtc = now,
                UpdatedUtc = now,
                LastRefreshUtc = now
            };
            data.UpdatedUtc = now;
            session.HtmlWorkspace.UpdatedUtc = now;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML bound data: " + name);
            return HtmlWorkspaceToolOutcome.Ok(
                "HTML data bound and loaded: " + name + ".",
                DataBindingResultJson(session, name, sourceTool.Id, transform,
                    refreshPolicy, "ready", true, json.Length),
                HtmlWorkspaceEffect.VerifiedChange);
        }

        private HtmlWorkspaceToolOutcome RefreshDataSources(
            ChatSession session,
            IDictionary<string, object> arguments,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            EnsureAdapterMatchesSession(session);
            var name = ToolArgumentReader.String(
                arguments, "name", string.Empty);

            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            List<HtmlWorkspaceDataSource> targets;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var exact = FindDataSource(workspace, name);
                if (exact.Binding == null) throw new InvalidOperationException("HTML workspace data source is not bound: " + exact.Name);
                targets = new List<HtmlWorkspaceDataSource> { exact };
            }
            else
            {
                targets = workspace.DataSources.Where(item =>
                    item != null && item.Binding != null).ToList();
            }

            if (targets.Count == 0)
            {
                return HtmlWorkspaceToolOutcome.Ok(
                    "No matching bound HTML data sources to refresh.",
                    RefreshResultJson(null, new JArray(), 0, 0, false),
                    HtmlWorkspaceEffect.VerifiedNoChange);
            }

            foreach (var target in targets) ValidateBinding(target.Binding);
            var targetNames = targets.Select(item => item.Name).ToArray();
            markDispatchPossible();
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            targets = targetNames.Select(item =>
                FindDataSource(session.HtmlWorkspace, item)).ToList();

            var summaries = new JArray();
            var succeeded = 0;
            var failed = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refreshed = RefreshDataSource(session, target, cancellationToken);
                if (refreshed.Status == HtmlWorkspaceOutcomeStatus.Ok)
                    succeeded += 1;
                else failed += 1;
                summaries.Add(ResultSummary(target, refreshed));
            }

            var previousArtifactId = session.ActiveHtmlArtifactId;
            var refreshedArtifactId = HtmlWorkspaceArtifactService.CaptureRefresh(
                session,
                targetNames.Length == 1
                    ? "HTML refreshed data: " + targetNames[0]
                    : "HTML refreshed data");
            var revisionChanged = !string.Equals(
                previousArtifactId,
                refreshedArtifactId,
                StringComparison.OrdinalIgnoreCase);
            var dataJson = RefreshResultJson(
                session, summaries, succeeded, failed, false);
            if (failed > 0)
            {
                return HtmlWorkspaceToolOutcome.Unknown(
                    "Refreshed " + succeeded + " HTML data source(s); " + failed + " failed and kept their previous JSON.",
                    dataJson,
                    "html_data_refresh_partial");
            }
            return HtmlWorkspaceToolOutcome.Ok(
                "Refreshed " + succeeded + " HTML data source(s).", dataJson,
                revisionChanged
                    ? HtmlWorkspaceEffect.VerifiedChange
                    : HtmlWorkspaceEffect.VerifiedNoChange);
        }

        private HtmlWorkspaceToolOutcome RefreshDataSource(
            ChatSession session,
            HtmlWorkspaceDataSource data,
            CancellationToken cancellationToken)
        {
            var binding = data == null ? null : data.Binding;
            try
            {
                ValidateBinding(binding);
                var arguments = JObject.Parse(binding.ArgumentsJson);
                ToolCatalogEntry sourceTool;
                var normalizedArguments = BuildSourceArguments(
                    binding.ToolId, arguments, out sourceTool);
                cancellationToken.ThrowIfCancellationRequested();
                var result = ExecuteDataSource(
                    session, sourceTool.Id, normalizedArguments,
                    cancellationToken);
                if (!result.Success)
                {
                    MarkBindingError(session, data, result.Message);
                    return HtmlWorkspaceToolOutcome.Error(
                        result.Message ?? "Office source failed.",
                        result.DataJson,
                        result.ErrorCode ?? "html_data_source_failed",
                        result.Retryable);
                }

                var json = TransformSourceJson(result.DataJson, binding.Transform, binding.Headers);
                ValidateDataSource(data.Name, json);
                ValidateWorkspaceCapacity(session.HtmlWorkspace, null, null, data.Id, json);
                var now = DateTime.UtcNow;
                var hash = TextPatternEngine.Sha256(json);
                var changed = !string.Equals(binding.ContentSha256, hash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(data.Json, json, StringComparison.Ordinal);
                if (changed)
                {
                    data.Json = json;
                    data.UpdatedUtc = now;
                }
                binding.ContentSha256 = hash;
                binding.DocumentKey = session.DocumentKey;
                binding.DocumentTitle = session.DocumentTitle;
                binding.Status = "ready";
                binding.LastError = null;
                binding.PayloadCompleteness = SourcePayloadCompleteness(result.DataJson);
                binding.LastRefreshUtc = now;
                binding.UpdatedUtc = now;
                session.HtmlWorkspace.UpdatedUtc = now;
                return HtmlWorkspaceToolOutcome.Ok(
                    changed ? "Data changed." : "Data is unchanged.",
                    JsonConvert.SerializeObject(new { changed = changed }),
                    changed ? HtmlWorkspaceEffect.VerifiedChange :
                        HtmlWorkspaceEffect.VerifiedNoChange);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkBindingError(session, data, ex.Message);
                return HtmlWorkspaceToolOutcome.Error(
                    ex.Message, null, "html_data_refresh_failed", false);
            }
        }

        private HtmlWorkspaceToolOutcome FreezeDataSource(
            ChatSession session,
            IDictionary<string, object> arguments,
            Action markDispatchPossible)
        {
            var name = ToolArgumentReader.String(
                arguments, "name", string.Empty);
            var preview = FindDataSource(
                NormalizedWorkspaceCopy(session.HtmlWorkspace), name);
            if (preview.Binding == null)
                throw new InvalidOperationException(
                    "HTML workspace data source is not bound: " + preview.Name);

            markDispatchPossible();
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var data = FindDataSource(session.HtmlWorkspace, name);
            var sourceToolId = data.Binding.ToolId;
            var transform = data.Binding.Transform;
            var refreshPolicy = data.Binding.RefreshPolicy;
            data.Binding = null;
            data.UpdatedUtc = DateTime.UtcNow;
            session.HtmlWorkspace.UpdatedUtc = data.UpdatedUtc;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML frozen data: " + data.Name);
            return HtmlWorkspaceToolOutcome.Ok(
                "HTML data frozen: " + data.Name + ".",
                DataBindingResultJson(session, data.Name, sourceToolId,
                    transform, refreshPolicy, "frozen", true,
                    (data.Json ?? string.Empty).Length),
                HtmlWorkspaceEffect.VerifiedChange);
        }

        private IDictionary<string, object> BuildSourceArguments(
            string sourceToolId, JObject arguments,
            out ToolCatalogEntry sourceTool)
        {
            JObject ignored;
            return BuildSourceArguments(
                sourceToolId, arguments, out sourceTool, out ignored);
        }

        private HtmlDataSourceReadOutcome ExecuteDataSource(
            ChatSession session,
            string sourceToolId,
            IDictionary<string, object> sourceArguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_executeOfficeDataSource != null)
                    return _executeOfficeDataSource(
                        session, sourceToolId, sourceArguments,
                        cancellationToken) ??
                        HtmlDataSourceReadOutcome.Error(
                            "Office data source returned no result.",
                            null, "html_data_source_result_missing", false);

                using (_beginLiveOfficeRead == null ? null : _beginLiveOfficeRead(session))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ExcelReadToolIds.Owns(sourceToolId))
                    {
                        if (_standaloneExcelRead == null)
                            return MissingSource(
                                "Excel", "excel_read_backend_missing");
                        var outcome = _standaloneExcelRead.ExecuteOutcome(
                            sourceToolId, sourceArguments);
                        return outcome.Success
                            ? HtmlDataSourceReadOutcome.Ok(
                                outcome.Message, outcome.DataJson)
                            : HtmlDataSourceReadOutcome.Error(
                                outcome.Message, outcome.DataJson,
                                outcome.ErrorCode, outcome.Retryable);
                    }
                    if (WordToolIds.IsRead(sourceToolId))
                    {
                        if (_standaloneWordRead == null)
                            return MissingSource(
                                "Word", "word_read_backend_missing");
                        var outcome = _standaloneWordRead.Execute(
                            sourceToolId, sourceArguments, null,
                            cancellationToken);
                        return outcome.Status == WordOutcomeStatus.Ok
                            ? HtmlDataSourceReadOutcome.Ok(
                                outcome.Message, outcome.DataJson)
                            : HtmlDataSourceReadOutcome.Error(
                                outcome.Message, outcome.DataJson,
                                outcome.ErrorCode, outcome.Retryable);
                    }
                    if (PowerPointToolIds.IsRead(sourceToolId))
                    {
                        if (_standalonePowerPointRead == null)
                            return MissingSource(
                                "PowerPoint",
                                "powerpoint_read_backend_missing");
                        var outcome = _standalonePowerPointRead.Execute(
                            sourceToolId, sourceArguments, null,
                            cancellationToken);
                        return outcome.Status == PowerPointOutcomeStatus.Ok
                            ? HtmlDataSourceReadOutcome.Ok(
                                outcome.Message, outcome.DataJson)
                            : HtmlDataSourceReadOutcome.Error(
                                outcome.Message, outcome.DataJson,
                                outcome.ErrorCode, outcome.Retryable);
                    }
                    if (OutlookToolIds.IsRead(sourceToolId))
                    {
                        if (_standaloneOutlookRead == null)
                            return MissingSource(
                                "Outlook", "outlook_read_backend_missing");
                        var outcome = _standaloneOutlookRead.Execute(
                            sourceToolId, sourceArguments, null,
                            cancellationToken);
                        return outcome.Status == OutlookOutcomeStatus.Ok
                            ? HtmlDataSourceReadOutcome.Ok(
                                outcome.Message, outcome.DataJson)
                            : HtmlDataSourceReadOutcome.Error(
                                outcome.Message, outcome.DataJson,
                                outcome.ErrorCode, outcome.Retryable);
                    }
                    return HtmlDataSourceReadOutcome.Error(
                        "HTML data source tool has no typed backend: " +
                        sourceToolId + ".", null,
                        "html_data_source_backend_missing", false);
                }
            }
            catch (ResourceRequestException ex)
            {
                return HtmlDataSourceReadOutcome.Error(
                    ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
        }

        private static HtmlDataSourceReadOutcome MissingSource(
            string host, string errorCode)
        {
            return HtmlDataSourceReadOutcome.Error(
                host + " read adapter is unavailable.", null,
                errorCode, false);
        }

        private IDictionary<string, object> BuildSourceArguments(
            string sourceToolId, JObject arguments,
            out ToolCatalogEntry sourceTool,
            out JObject normalizedArguments)
        {
            if (!_dataSourceTools.TryGetValue(sourceToolId ?? string.Empty, out sourceTool))
            {
                throw new InvalidOperationException("HTML data source tool is unavailable or not approved: " + sourceToolId);
            }

            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(sourceTool, out schema, out schemaError))
            {
                throw new InvalidOperationException(schemaError);
            }
            arguments = arguments == null ? new JObject() : (JObject)arguments.DeepClone();
            ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            string argumentError;
            if (!ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError))
            {
                throw new InvalidOperationException("Invalid arguments for " + sourceTool.Id + ": " + argumentError);
            }

            normalizedArguments = (JObject)arguments.DeepClone();
            var normalized = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            ToolArgumentNormalizer.AddProperties(arguments, normalized);
            return normalized;
        }

        private void ValidateBinding(HtmlWorkspaceDataBinding binding)
        {
            if (binding == null) throw new InvalidOperationException("HTML data binding is missing.");
            if (!string.IsNullOrWhiteSpace(binding.Host) &&
                !string.Equals(binding.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HTML data binding belongs to " + binding.Host + ", not " + _adapter.HostName + ".");
            }
            JObject arguments;
            try
            {
                arguments = JObject.Parse(string.IsNullOrWhiteSpace(binding.ArgumentsJson) ? "{}" : binding.ArgumentsJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Stored HTML data source arguments are invalid: " + ex.Message);
            }
            ToolCatalogEntry ignored;
            BuildSourceArguments(binding.ToolId, arguments, out ignored);
        }

        private void EnsureAdapterMatchesSession(ChatSession session)
        {
            if (_adapter == null) throw new InvalidOperationException("HTML data binding requires an Office adapter.");
            if (session != null && !string.IsNullOrWhiteSpace(session.Host) &&
                !string.Equals(session.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HTML workspace belongs to " + session.Host + ", not " + _adapter.HostName + ".");
            }
        }

        private static string TransformSourceJson(string sourceJson, string transform, string headers)
        {
            if (string.IsNullOrWhiteSpace(sourceJson))
            {
                throw new InvalidOperationException("Office data source returned no JSON data.");
            }
            JToken source;
            try
            {
                source = JToken.Parse(sourceJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Office data source returned invalid JSON: " + ex.Message);
            }
            return string.Equals(transform, "table", StringComparison.OrdinalIgnoreCase)
                ? BuildTableEnvelope(source, headers).ToString(Formatting.None)
                : source.ToString(Formatting.None);
        }

        private static JObject BuildTableEnvelope(JToken source, string headers)
        {
            JArray sourceRows = source as JArray;
            var sourceMetadata = new JObject();
            if (sourceRows == null)
            {
                var sourceObject = source as JObject;
                if (sourceObject != null)
                {
                    var preferredNames = new[] { "values", "rows", "items", "results", "messages", "slides", "objects", "data" };
                    foreach (var preferred in preferredNames)
                    {
                        var property = sourceObject.Properties().FirstOrDefault(item => string.Equals(item.Name, preferred, StringComparison.OrdinalIgnoreCase) && item.Value is JArray);
                        if (property != null)
                        {
                            sourceRows = (JArray)property.Value;
                            break;
                        }
                    }
                    if (sourceRows == null)
                    {
                        var firstArray = sourceObject.Properties().FirstOrDefault(item => item.Value is JArray);
                        sourceRows = firstArray == null ? null : (JArray)firstArray.Value;
                    }
                    foreach (var property in sourceObject.Properties().Where(item => !(item.Value is JContainer)))
                    {
                        sourceMetadata[property.Name] = property.Value.DeepClone();
                    }
                }
            }
            if (sourceRows == null)
            {
                throw new InvalidOperationException("transform=table requires a JSON array or an object containing an array.");
            }

            var columns = new JArray();
            var rows = new JArray();
            var nonNull = sourceRows.FirstOrDefault(item => item != null && item.Type != JTokenType.Null);
            if (nonNull is JObject)
            {
                BuildObjectRows(sourceRows, columns, rows);
            }
            else if (nonNull is JArray || sourceRows.Count == 0)
            {
                BuildArrayRows(sourceRows, columns, rows, headers);
            }
            else
            {
                columns.Add(Column("value", "Value", InferType(sourceRows)));
                foreach (var value in sourceRows)
                {
                    rows.Add(new JObject { ["value"] = value == null ? JValue.CreateNull() : value.DeepClone() });
                }
            }

            return new JObject
            {
                ["schema"] = "rnassistant.table.v1",
                ["source"] = sourceMetadata,
                ["columns"] = columns,
                ["rows"] = rows,
                ["rowCount"] = rows.Count
            };
        }

        private static void BuildObjectRows(JArray sourceRows, JArray columns, JArray rows)
        {
            var names = new List<string>();
            foreach (var row in sourceRows.OfType<JObject>())
            {
                foreach (var property in row.Properties())
                {
                    if (!names.Contains(property.Name, StringComparer.Ordinal)) names.Add(property.Name);
                }
            }
            foreach (var name in names)
            {
                columns.Add(Column(name, name, InferType(sourceRows.OfType<JObject>().Select(item => item[name]))));
            }
            foreach (var token in sourceRows)
            {
                var sourceRow = token as JObject;
                var row = new JObject();
                foreach (var name in names)
                {
                    row[name] = sourceRow == null || sourceRow[name] == null ? JValue.CreateNull() : sourceRow[name].DeepClone();
                }
                rows.Add(row);
            }
        }

        private static void BuildArrayRows(JArray sourceRows, JArray columns, JArray rows, string headers)
        {
            var arrays = sourceRows.Select(item => item as JArray ?? new JArray(item == null ? JValue.CreateNull() : item.DeepClone())).ToList();
            var headerRow = arrays.Count > 0 && string.Equals(headers, "firstRow", StringComparison.OrdinalIgnoreCase) ? arrays[0] : null;
            var dataRows = headerRow == null ? arrays : arrays.Skip(1).ToList();
            var count = Math.Max(headerRow == null ? 0 : headerRow.Count, dataRows.Count == 0 ? 0 : dataRows.Max(item => item.Count));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var label = headerRow != null && index < headerRow.Count && headerRow[index].Type != JTokenType.Null
                    ? (headerRow[index] is JValue ? Convert.ToString(((JValue)headerRow[index]).Value) : headerRow[index].ToString(Formatting.None))
                    : "Column " + (index + 1);
                var key = UniqueColumnKey(label, index, keys);
                names.Add(key);
                columns.Add(Column(key, string.IsNullOrWhiteSpace(label) ? "Column " + (index + 1) : label, InferType(dataRows.Select(item => index < item.Count ? item[index] : null))));
            }
            foreach (var sourceRow in dataRows)
            {
                var row = new JObject();
                for (var index = 0; index < names.Count; index++)
                {
                    row[names[index]] = index < sourceRow.Count ? sourceRow[index].DeepClone() : JValue.CreateNull();
                }
                rows.Add(row);
            }
        }

        private static JObject Column(string key, string label, string type)
        {
            return new JObject { ["key"] = key, ["label"] = label, ["type"] = type };
        }

        private static string UniqueColumnKey(string label, int index, ISet<string> existing)
        {
            var builder = new StringBuilder();
            foreach (var character in (label ?? string.Empty).Trim())
            {
                if (char.IsLetterOrDigit(character) || character == '_') builder.Append(char.ToLowerInvariant(character));
                else if (builder.Length > 0 && builder[builder.Length - 1] != '_') builder.Append('_');
            }
            var value = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(value)) value = "column_" + (index + 1);
            if (char.IsDigit(value[0])) value = "column_" + value;
            var candidate = value;
            var suffix = 2;
            while (existing.Contains(candidate)) candidate = value + "_" + suffix++;
            existing.Add(candidate);
            return candidate;
        }

        private static string InferType(IEnumerable<JToken> values)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? new JToken[0])
            {
                if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) continue;
                if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) types.Add("number");
                else if (value.Type == JTokenType.Boolean) types.Add("boolean");
                else if (value.Type == JTokenType.Array) types.Add("array");
                else if (value.Type == JTokenType.Object) types.Add("object");
                else types.Add("string");
            }
            return types.Count == 0 ? "null" : (types.Count == 1 ? types.First() : "mixed");
        }

        private static void MarkBindingError(ChatSession session, HtmlWorkspaceDataSource data, string message)
        {
            if (data == null || data.Binding == null) return;
            var now = DateTime.UtcNow;
            data.Binding.Status = "error";
            data.Binding.LastError = string.IsNullOrWhiteSpace(message) ? "Refresh failed." : message;
            data.Binding.LastRefreshUtc = now;
            data.Binding.UpdatedUtc = now;
            if (session != null && session.HtmlWorkspace != null) session.HtmlWorkspace.UpdatedUtc = now;
        }

        private static JObject ResultSummary(
            HtmlWorkspaceDataSource data,
            HtmlWorkspaceToolOutcome result)
        {
            var changed = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(result == null ? null : result.DataJson)) changed = JObject.Parse(result.DataJson)["changed"].Value<bool>();
            }
            catch
            {
            }
            return new JObject
            {
                ["name"] = data == null ? string.Empty : data.Name,
                ["sourceTool"] = data == null || data.Binding == null ? string.Empty : data.Binding.ToolId,
                ["ok"] = result != null &&
                    result.Status == HtmlWorkspaceOutcomeStatus.Ok,
                ["changed"] = changed,
                ["status"] = data == null || data.Binding == null ? "error" : data.Binding.Status,
                ["message"] = result == null ? "Refresh failed." : result.Message
            };
        }

        private static string RefreshResultJson(
            ChatSession session,
            JArray results,
            int succeeded,
            int failed,
            bool dryRun)
        {
            return AddWorkspaceResourceRefs(session, new JObject
            {
                ["type"] = "rnassistant.htmlDataRefresh",
                ["version"] = 1,
                ["dryRun"] = dryRun,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["results"] = results ?? new JArray(),
                ["revisionArtifactId"] = session == null
                    ? null : session.ActiveHtmlArtifactId
            }).ToString(Formatting.None);
        }

        private static string DataBindingResultJson(ChatSession session, string name, string sourceTool, string transform, string refreshPolicy, string status, bool saved, int jsonCharacters)
        {
            return AddWorkspaceResourceRefs(session, new JObject
            {
                ["type"] = "rnassistant.htmlDataBinding",
                ["version"] = 2,
                ["name"] = name,
                ["sourceTool"] = sourceTool,
                ["transform"] = transform,
                ["refreshPolicy"] = refreshPolicy,
                ["status"] = status,
                ["saved"] = saved,
                ["jsonCharacters"] = jsonCharacters,
                ["revisionArtifactId"] = session == null ? null : session.ActiveHtmlArtifactId
            }).ToString(Formatting.None);
        }

        private static string NormalizeTransform(string value)
        {
            return string.Equals(value, "table", StringComparison.OrdinalIgnoreCase) ? "table" : "raw";
        }

        private static string NormalizeHeaders(string value)
        {
            return string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "firstRow";
        }

        private static string NormalizeRefreshPolicy(string value)
        {
            return string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
                ? "manual" : "on_preview";
        }

        private static string SourcePayloadCompleteness(string sourceJson)
        {
            try
            {
                var source = JToken.Parse(sourceJson ?? string.Empty) as JObject;
                var truncated = source == null ? null : source.GetValue("truncated", StringComparison.OrdinalIgnoreCase);
                if (truncated != null && truncated.Type == JTokenType.Boolean)
                {
                    return truncated.Value<bool>() ? "truncated" : "complete";
                }
                var complete = source == null ? null : source.GetValue("complete", StringComparison.OrdinalIgnoreCase);
                if (complete != null && complete.Type == JTokenType.Boolean)
                {
                    return complete.Value<bool>() ? "complete" : "truncated";
                }
            }
            catch (JsonException)
            {
            }
            return "bounded";
        }

        private static object BindingDetails(HtmlWorkspaceDataBinding binding)
        {
            if (binding == null) return null;
            JToken arguments;
            try
            {
                arguments = JToken.Parse(string.IsNullOrWhiteSpace(binding.ArgumentsJson) ? "{}" : binding.ArgumentsJson);
            }
            catch (JsonException)
            {
                arguments = new JObject();
            }
            return new
            {
                binding.ToolId,
                sourceArguments = arguments,
                binding.Transform,
                binding.Headers,
                binding.RefreshPolicy,
                binding.Host,
                binding.DocumentTitle,
                binding.Status,
                binding.LastError,
                binding.PayloadCompleteness,
                binding.LastRefreshUtc,
                binding.CreatedUtc,
                binding.UpdatedUtc
            };
        }

        private static void NormalizeBinding(HtmlWorkspaceDataBinding binding, HtmlWorkspaceDataSource dataSource)
        {
            if (binding == null) return;
            binding.ToolId = (binding.ToolId ?? string.Empty).Trim();
            binding.ArgumentsJson = string.IsNullOrWhiteSpace(binding.ArgumentsJson) ? "{}" : binding.ArgumentsJson;
            binding.Transform = NormalizeTransform(binding.Transform);
            binding.Headers = NormalizeHeaders(binding.Headers);
            binding.RefreshPolicy = NormalizeRefreshPolicy(binding.RefreshPolicy);
            binding.Status = string.Equals(binding.Status, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "ready";
            binding.PayloadCompleteness = NormalizePayloadCompleteness(binding.PayloadCompleteness);
            if (binding.CreatedUtc == default(DateTime)) binding.CreatedUtc = dataSource == null || dataSource.CreatedUtc == default(DateTime) ? DateTime.UtcNow : dataSource.CreatedUtc;
            if (binding.UpdatedUtc == default(DateTime)) binding.UpdatedUtc = binding.CreatedUtc;
            if (string.IsNullOrWhiteSpace(binding.ContentSha256) && dataSource != null)
            {
                binding.ContentSha256 = TextPatternEngine.Sha256(dataSource.Json);
            }
            else if (dataSource != null && !string.Equals(
                binding.ContentSha256,
                TextPatternEngine.Sha256(dataSource.Json),
                StringComparison.OrdinalIgnoreCase))
            {
                binding.Status = "error";
                binding.LastError = "Bound data payload failed its integrity check; refresh or freeze it before use.";
            }
        }

        private static string NormalizePayloadCompleteness(string value)
        {
            if (string.Equals(value, "complete", StringComparison.OrdinalIgnoreCase)) return "complete";
            if (string.Equals(value, "truncated", StringComparison.OrdinalIgnoreCase)) return "truncated";
            return "bounded";
        }

    }
}
