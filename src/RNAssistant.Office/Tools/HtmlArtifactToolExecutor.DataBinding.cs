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
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlArtifactToolExecutor
    {
        private static bool IsEligibleDataSourceTool(ToolDefinition tool)
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

        private string BuildBindDescription()
        {
            return "Workspace: Execute one approved read-only Office tool, save its JSON as a refreshable HTML data source, and bind it to the active document. " +
                "Choose sourceTool first and pass only arguments declared by that exact tool in sourceArguments; do not copy selector fields from another source. " +
                "Use transform=table only when the source returns row arrays. Available sources: " +
                string.Join(", ", _dataSourceTools.Keys.ToArray()) + ".";
        }

        private string BuildBindSchema()
        {
            var sourceIds = _dataSourceTools.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            var sourceProperties = new JObject();
            var alternatives = new JArray();
            foreach (var tool in _dataSourceTools.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                JObject schema;
                string error;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out error)) continue;
                foreach (var property in ((JObject)schema["properties"]).Properties())
                {
                    if (sourceProperties[property.Name] == null)
                    {
                        sourceProperties[property.Name] = property.Value.DeepClone();
                    }
                }

                schema["description"] = "Arguments accepted by " + tool.Id + "; omit every field not declared in this selected source schema.";
                alternatives.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = BindProperties(new[] { tool.Id }, schema),
                    ["required"] = new JArray("dataName", "sourceTool", "sourceArguments"),
                    ["additionalProperties"] = false
                });
            }

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = BindProperties(sourceIds, new JObject
                {
                    ["type"] = "object",
                    ["description"] = "Arguments for the selected sourceTool; the matching anyOf branch defines the exact allowed fields.",
                    ["properties"] = sourceProperties,
                    ["required"] = new JArray(),
                    ["additionalProperties"] = false
                }),
                ["required"] = new JArray("dataName", "sourceTool", "sourceArguments"),
                ["additionalProperties"] = false,
                ["anyOf"] = alternatives
            }.ToString(Formatting.None);
        }

        private static JObject BindProperties(IEnumerable<string> sourceIds, JObject sourceArgumentsSchema)
        {
            return new JObject
            {
                ["dataName"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Stable data-source name exposed to HTML as window.RNAssistantData[dataName].",
                    ["minLength"] = 1,
                    ["maxLength"] = 128
                },
                ["sourceTool"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray((sourceIds ?? new string[0]).ToArray()),
                    ["description"] = "Exact approved read-only Office tool whose result will be stored and refreshed."
                },
                ["sourceArguments"] = sourceArgumentsSchema,
                ["transform"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("raw", "table"),
                    ["description"] = "raw preserves source JSON; table converts a returned row array to columns plus object rows.",
                    ["default"] = "raw"
                },
                ["headers"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("firstRow", "none"),
                    ["description"] = "For transform=table, firstRow uses row 1 as column labels; none generates column labels.",
                    ["default"] = "firstRow"
                },
                ["refreshPolicy"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("manual", "on_preview"),
                    ["description"] = "manual refreshes only through common.html_data_refresh; on_preview also refreshes when HTML preview opens.",
                    ["default"] = "on_preview"
                }
            };
        }

        private ToolResult BindDataSource(ChatSession session, ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            EnsureAdapterMatchesSession(session);
            var name = NormalizeDataName(ToolArgumentReader.String(command.Arguments, "dataName", string.Empty));
            var sourceToolId = ToolArgumentReader.String(command.Arguments, "sourceTool", string.Empty);
            var transform = NormalizeTransform(ToolArgumentReader.String(command.Arguments, "transform", "raw"));
            var headers = NormalizeHeaders(ToolArgumentReader.String(command.Arguments, "headers", "firstRow"));
            var refreshPolicy = NormalizeRefreshPolicy(ToolArgumentReader.String(command.Arguments, "refreshPolicy", "on_preview"));
            var sourceArguments = ReadObjectArgument(command, "sourceArguments");
            ToolDefinition sourceTool;
            JObject normalizedSourceArguments;
            var sourceCommand = BuildSourceCommand(sourceToolId, sourceArguments, out sourceTool, out normalizedSourceArguments);

            if (dryRun)
            {
                return ToolResult.Ok(
                    "Dry run: would bind HTML data " + name + " to " + sourceTool.Id + ".",
                    DataBindingResultJson(session, name, sourceTool.Id, transform, refreshPolicy, "dry_run", false, 0));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sourceResult = ExecuteDataSource(session, sourceCommand, cancellationToken);
            if (!sourceResult.Success)
            {
                return ToolResult.Fail(
                    "Could not bind HTML data " + name + ": " + (sourceResult.Message ?? "Office source failed."),
                    sourceResult.DataJson,
                    sourceResult.ErrorCode ?? "html_data_source_failed",
                    sourceResult.Retryable);
            }

            var json = TransformSourceJson(sourceResult.DataJson, transform, headers);
            ValidateDataSource(name, json);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var id = DataSourceId(name);
            ValidateWorkspaceCapacity(session.HtmlWorkspace, null, null, id, json);
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
                PayloadCompleteness = SourcePayloadCompleteness(sourceResult.DataJson),
                ContentSha256 = TextPatternEngine.Sha256(json),
                CreatedUtc = now,
                UpdatedUtc = now,
                LastRefreshUtc = now
            };
            data.UpdatedUtc = now;
            session.HtmlWorkspace.UpdatedUtc = now;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML bound data: " + name);
            return ToolResult.Ok(
                "HTML data bound and loaded: " + name + ".",
                DataBindingResultJson(session, name, sourceTool.Id, transform, refreshPolicy, "ready", true, json.Length));
        }

        private ToolResult RefreshDataSources(ChatSession session, ToolCommand command, bool dryRun, CancellationToken cancellationToken)
        {
            EnsureAdapterMatchesSession(session);
            var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
            var policy = ToolArgumentReader.String(command.Arguments, "policy", "all");
            if (!string.Equals(policy, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(policy, "on_preview", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("policy must be all or on_preview.");
            }

            var workspace = dryRun ? NormalizedWorkspaceCopy(session.HtmlWorkspace) : NormalizeWorkspace(session.HtmlWorkspace);
            List<HtmlWorkspaceDataSource> targets;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var exact = FindDataSource(workspace, name);
                if (exact.Binding == null) throw new InvalidOperationException("HTML workspace data source is not bound: " + exact.Name);
                targets = new List<HtmlWorkspaceDataSource> { exact };
            }
            else
            {
                targets = workspace.DataSources.Where(item => item != null && item.Binding != null &&
                    (!string.Equals(policy, "on_preview", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Binding.RefreshPolicy, "on_preview", StringComparison.OrdinalIgnoreCase))).ToList();
            }

            if (targets.Count == 0)
            {
                return ToolResult.Ok("No matching bound HTML data sources to refresh.", RefreshResultJson(new JArray(), 0, 0, dryRun));
            }

            if (dryRun)
            {
                foreach (var target in targets) ValidateBinding(target.Binding);
                return ToolResult.Ok(
                    "Dry run: would refresh " + targets.Count + " HTML data source(s).",
                    RefreshResultJson(new JArray(targets.Select(item => new JObject
                    {
                        ["name"] = item.Name,
                        ["sourceTool"] = item.Binding.ToolId,
                        ["status"] = "dry_run"
                    })), targets.Count, 0, true));
            }

            var summaries = new JArray();
            var succeeded = 0;
            var failed = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refreshed = RefreshDataSource(session, target, cancellationToken);
                if (refreshed.Success) succeeded += 1;
                else failed += 1;
                summaries.Add(ResultSummary(target, refreshed));
            }

            var dataJson = RefreshResultJson(summaries, succeeded, failed, false);
            if (failed > 0)
            {
                return ToolResult.PartialFailure(
                    "Refreshed " + succeeded + " HTML data source(s); " + failed + " failed and kept their previous JSON.",
                    dataJson,
                    "html_data_refresh_partial");
            }
            return ToolResult.Ok("Refreshed " + succeeded + " HTML data source(s).", dataJson);
        }

        private ToolResult RefreshDataSource(ChatSession session, HtmlWorkspaceDataSource data, CancellationToken cancellationToken)
        {
            var binding = data == null ? null : data.Binding;
            try
            {
                ValidateBinding(binding);
                var arguments = JObject.Parse(binding.ArgumentsJson);
                ToolDefinition sourceTool;
                var sourceCommand = BuildSourceCommand(binding.ToolId, arguments, out sourceTool);
                cancellationToken.ThrowIfCancellationRequested();
                var result = ExecuteDataSource(session, sourceCommand, cancellationToken);
                if (!result.Success)
                {
                    MarkBindingError(session, data, result.Message);
                    return ToolResult.Fail(result.Message ?? "Office source failed.", null, result.ErrorCode ?? "html_data_source_failed", result.Retryable);
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
                return ToolResult.Ok(changed ? "Data changed." : "Data is unchanged.", JsonConvert.SerializeObject(new { changed = changed }));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkBindingError(session, data, ex.Message);
                return ToolResult.Fail(ex.Message, null, "html_data_refresh_failed", false);
            }
        }

        private ToolResult FreezeDataSource(ChatSession session, ToolCommand command, bool dryRun)
        {
            var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
            var workspace = dryRun ? NormalizedWorkspaceCopy(session.HtmlWorkspace) : NormalizeWorkspace(session.HtmlWorkspace);
            var data = FindDataSource(workspace, name);
            if (data.Binding == null) throw new InvalidOperationException("HTML workspace data source is not bound: " + data.Name);
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would freeze HTML data " + data.Name + ".", DataBindingResultJson(session, data.Name, data.Binding.ToolId, data.Binding.Transform, data.Binding.RefreshPolicy, "dry_run", false, (data.Json ?? string.Empty).Length));
            }

            var sourceToolId = data.Binding.ToolId;
            var transform = data.Binding.Transform;
            var refreshPolicy = data.Binding.RefreshPolicy;
            data.Binding = null;
            data.UpdatedUtc = DateTime.UtcNow;
            session.HtmlWorkspace.UpdatedUtc = data.UpdatedUtc;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML frozen data: " + data.Name);
            return ToolResult.Ok("HTML data frozen: " + data.Name + ".", DataBindingResultJson(session, data.Name, sourceToolId, transform, refreshPolicy, "frozen", true, (data.Json ?? string.Empty).Length));
        }

        private ToolCommand BuildSourceCommand(string sourceToolId, JObject arguments, out ToolDefinition sourceTool)
        {
            JObject ignored;
            return BuildSourceCommand(sourceToolId, arguments, out sourceTool, out ignored);
        }

        private ToolResult ExecuteDataSource(
            ChatSession session,
            ToolCommand sourceCommand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (_beginLiveOfficeRead == null ? null : _beginLiveOfficeRead(session))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_executeOfficeDataSource != null)
                        return _executeOfficeDataSource(sourceCommand, cancellationToken) ??
                            ToolResult.Fail("Office data source returned no result.");
                    if (sourceCommand != null && ExcelReadToolIds.Owns(sourceCommand.ToolId))
                        return _standaloneExcelRead == null
                            ? ToolResult.Fail("Excel read adapter is unavailable.", null, "excel_read_backend_missing", false)
                            : _standaloneExcelRead.ExecuteDataSource(sourceCommand, cancellationToken);
                    return _adapter.ExecuteTool(sourceCommand) ?? ToolResult.Fail("Office data source returned no result.");
                }
            }
            catch (ResourceRequestException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
        }

        private ToolCommand BuildSourceCommand(string sourceToolId, JObject arguments, out ToolDefinition sourceTool, out JObject normalizedArguments)
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
            var command = new ToolCommand { ToolId = sourceTool.Id };
            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
            return command;
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
            ToolDefinition ignored;
            BuildSourceCommand(binding.ToolId, arguments, out ignored);
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

        private static JObject ReadObjectArgument(ToolCommand command, string name)
        {
            object raw;
            if (command == null || command.Arguments == null || !command.Arguments.TryGetValue(name, out raw) || raw == null)
            {
                return new JObject();
            }
            var token = raw as JToken;
            if (token is JObject) return (JObject)token.DeepClone();
            var text = raw as string;
            if (text != null) return JObject.Parse(text);
            return JObject.FromObject(raw);
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

        private static JObject ResultSummary(HtmlWorkspaceDataSource data, ToolResult result)
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
                ["ok"] = result != null && result.Success,
                ["changed"] = changed,
                ["status"] = data == null || data.Binding == null ? "error" : data.Binding.Status,
                ["message"] = result == null ? "Refresh failed." : result.Message
            };
        }

        private static string RefreshResultJson(JArray results, int succeeded, int failed, bool dryRun)
        {
            return new JObject
            {
                ["type"] = "rnassistant.htmlDataRefresh",
                ["version"] = 1,
                ["dryRun"] = dryRun,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["results"] = results ?? new JArray()
            }.ToString(Formatting.None);
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
            return string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "on_preview";
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
