using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
    internal sealed class HtmlArtifactToolExecutor
    {
        public const string ReadWorkspaceToolId = "common.html_workspace_read";
        public const string UpsertToolId = "common.html_workspace_upsert";
        public const string DeleteToolId = "common.html_workspace_delete";
        public const string SetActiveToolId = "common.html_workspace_set_active";
        public const string BindDataToolId = "common.html_data_bind";
        public const string RefreshDataToolId = "common.html_data_refresh";
        public const string FreezeDataToolId = "common.html_data_freeze";

        private const int MaxHtmlChars = 300000;
        private const int MaxDataChars = 300000;
        private const int MaxWorkspaceItems = 100;
        private const int MaxWorkspaceCharacters = 1500000;

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Dictionary<string, ToolDefinition> _dataSourceTools;

        public HtmlArtifactToolExecutor()
            : this(null, null)
        {
        }

        public HtmlArtifactToolExecutor(IOfficeApplicationAdapter adapter, IEnumerable<ToolDefinition> adapterTools)
        {
            _adapter = adapter;
            _dataSourceTools = (adapterTools ?? new ToolDefinition[0])
                .Where(IsEligibleDataSourceTool)
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(tool => tool.Id, tool => tool.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(ReadWorkspaceToolId, "Common", "Read-only: List the active chat HTML workspace when called without arguments, or read one exact file/data source.", "{\"type\":\"object\",\"properties\":{\"resourceType\":{\"type\":\"string\",\"enum\":[\"file\",\"data\"],\"description\":\"Optional resource type; omit both resourceType and name to read the compact workspace manifest.\"},\"name\":{\"type\":\"string\",\"description\":\"Exact file path or data-source name for the selected resource type.\",\"maxLength\":260}},\"required\":[],\"additionalProperties\":false}", name: "html_workspace_read", scope: "session");
            yield return ControllerToolDefinition.Create(UpsertToolId, "Common", "Workspace: Create or update one file or JSON data source. File kind is inferred from its extension; missing items are created automatically.", "{\"type\":\"object\",\"properties\":{\"resourceType\":{\"type\":\"string\",\"enum\":[\"file\",\"data\"],\"description\":\"Resource to write: file or data.\"},\"name\":{\"type\":\"string\",\"description\":\"Workspace-relative file path or stable data-source name.\",\"maxLength\":260},\"content\":{\"type\":\"string\",\"description\":\"Complete file text or valid JSON text for a data source.\",\"maxLength\":300000},\"setActive\":{\"type\":\"boolean\",\"description\":\"For an HTML file, make it the active preview after writing. Ignored for data.\",\"default\":true}},\"required\":[\"resourceType\",\"name\",\"content\"],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_workspace_upsert", scope: "session");
            yield return ControllerToolDefinition.Create(DeleteToolId, "Common", "Workspace: Delete one exact file or JSON data source. Workspace history keeps the operation recoverable.", "{\"type\":\"object\",\"properties\":{\"resourceType\":{\"type\":\"string\",\"enum\":[\"file\",\"data\"],\"description\":\"Resource to delete: file or data.\"},\"name\":{\"type\":\"string\",\"description\":\"Exact workspace-relative file path or data-source name.\",\"maxLength\":260}},\"required\":[\"resourceType\",\"name\"],\"additionalProperties\":false}", mutatesLocalState: true, riskLevel: 1, name: "html_workspace_delete", scope: "session");
            yield return ControllerToolDefinition.Create(SetActiveToolId, "Common", "Workspace: Select the active HTML file displayed on the HTML tab for the active chat.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact workspace-relative HTML file path.\",\"default\":\"index.html\",\"maxLength\":260}},\"required\":[],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_workspace_set_active", scope: "session");
            if (_dataSourceTools.Count > 0)
            {
                yield return ControllerToolDefinition.Create(BindDataToolId, "Common", BuildBindDescription(), BuildBindSchema(), mutatesLocalState: true, name: "html_data_bind", scope: "session");
            }
            yield return ControllerToolDefinition.Create(RefreshDataToolId, "Common", "Workspace: Re-run a bound read-only Office source and replace its JSON without another model request. Omit name to refresh all matching bound sources.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Optional exact bound data-source name; omit to refresh all matching sources.\",\"maxLength\":128},\"policy\":{\"type\":\"string\",\"enum\":[\"all\",\"on_preview\"],\"description\":\"Refresh all bound sources or only sources configured for preview refresh.\",\"default\":\"all\"}},\"required\":[],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_data_refresh", scope: "session");
            yield return ControllerToolDefinition.Create(FreezeDataToolId, "Common", "Workspace: Keep the current JSON of one bound data source but remove its Office binding so future refreshes cannot overwrite it.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact bound data-source name.\",\"maxLength\":128}},\"required\":[\"name\"],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_data_freeze", scope: "session");
        }

        internal bool RequiresOfficeDocument(string toolId)
        {
            return string.Equals(toolId, BindDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, RefreshDataToolId, StringComparison.OrdinalIgnoreCase);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (command == null)
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            try
            {
                if (session == null)
                {
                    return ToolResult.Fail("HTML workspace requires an active chat session.");
                }

                if (string.Equals(command.ToolId, ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Ok("HTML workspace read.", ReadWorkspaceDataJson(session, command));
                }

                if (string.Equals(command.ToolId, BindDataToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return BindDataSource(session, command, dryRun, cancellationToken);
                }

                if (string.Equals(command.ToolId, RefreshDataToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return RefreshDataSources(session, command, dryRun, cancellationToken);
                }

                if (string.Equals(command.ToolId, FreezeDataToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return FreezeDataSource(session, command, dryRun);
                }

                if (string.Equals(command.ToolId, UpsertToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var resourceType = ToolArgumentReader.String(command.Arguments, "resourceType", string.Empty);
                    var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
                    var content = ToolArgumentReader.String(command.Arguments, "content", string.Empty);
                    var setActive = ToolArgumentReader.Boolean(command.Arguments, "setActive", true);
                    if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dryRun)
                        {
                            ValidateFile(name, string.Empty, content);
                            var normalizedPath = NormalizePath(name);
                            ValidateWorkspaceCapacity(NormalizedWorkspaceCopy(session.HtmlWorkspace), FileId(normalizedPath), content, null, null);
                            return ToolResult.Ok("Dry run: would save HTML workspace file " + normalizedPath + ".", WorkspaceMutationJson(session, "file", normalizedPath));
                        }
                        var file = UpsertFile(session, name, string.Empty, content, setActive);
                        return ToolResult.Ok("HTML workspace file saved: " + file.Path, WorkspaceMutationJson(session, "file", file.Path));
                    }
                    if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dryRun)
                        {
                            ValidateDataSource(name, content);
                            var normalizedName = NormalizeDataName(name);
                            ValidateWorkspaceCapacity(NormalizedWorkspaceCopy(session.HtmlWorkspace), null, null, DataSourceId(normalizedName), content);
                            return ToolResult.Ok("Dry run: would save HTML workspace data source " + normalizedName + ".", WorkspaceMutationJson(session, "data", normalizedName));
                        }
                        var data = UpsertDataSource(session, name, content);
                        return ToolResult.Ok("HTML workspace data saved: " + data.Name, WorkspaceMutationJson(session, "data", data.Name));
                    }
                    return ToolResult.Fail("resourceType must be file or data.");
                }

                if (string.Equals(command.ToolId, DeleteToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var resourceType = ToolArgumentReader.String(command.Arguments, "resourceType", string.Empty);
                    var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
                    if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dryRun)
                        {
                            FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), name, false);
                            return ToolResult.Ok("Dry run: would delete HTML workspace file " + NormalizePath(name) + ".", WorkspaceMutationJson(session, "file", NormalizePath(name)));
                        }
                        var file = DeleteFile(session, name);
                        return ToolResult.Ok("HTML workspace file deleted: " + file.Path, WorkspaceMutationJson(session, "file", file.Path));
                    }
                    if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dryRun)
                        {
                            FindDataSource(NormalizedWorkspaceCopy(session.HtmlWorkspace), name);
                            return ToolResult.Ok("Dry run: would delete HTML workspace data source " + NormalizeDataName(name) + ".", WorkspaceMutationJson(session, "data", NormalizeDataName(name)));
                        }
                        var data = DeleteDataSource(session, name);
                        return ToolResult.Ok("HTML workspace data source deleted: " + data.Name, WorkspaceMutationJson(session, "data", data.Name));
                    }
                    return ToolResult.Fail("resourceType must be file or data.");
                }

                if (string.Equals(command.ToolId, SetActiveToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var path = ToolArgumentReader.String(command.Arguments, "name", "index.html");
                    if (dryRun)
                    {
                        FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), path, true);
                        return ToolResult.Ok("Dry run: would select HTML workspace file " + NormalizePath(path) + ".", WorkspaceMutationJson(session, "file", NormalizePath(path)));
                    }

                    var file = SetActiveFile(session, path);
                    return ToolResult.Ok("HTML workspace active file selected: " + file.Path, WorkspaceMutationJson(session, "file", file.Path));
                }
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Invalid HTML workspace JSON data source: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ToolResult.Fail(ex.Message);
            }

            return ToolResult.Fail("Unknown HTML workspace tool: " + command.ToolId);
        }

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
            return "Workspace: Bind a JSON data source to one approved read-only Office tool, execute it now, and save refresh metadata. " +
                "Use transform=table for a row array that should become columns plus object rows. Available sources: " +
                string.Join(", ", _dataSourceTools.Keys.ToArray()) + ".";
        }

        private string BuildBindSchema()
        {
            var sourceProperties = new JObject();
            foreach (var tool in _dataSourceTools.Values)
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
            }

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["dataName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Stable HTML workspace data-source name exposed under window.RNAssistantData.",
                        ["maxLength"] = 128
                    },
                    ["sourceTool"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray(_dataSourceTools.Keys.ToArray()),
                        ["description"] = "Exact approved read-only Office tool to execute on bind and refresh."
                    },
                    ["sourceArguments"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Native JSON arguments for sourceTool. Use only fields from that selected tool's schema.",
                        ["properties"] = sourceProperties,
                        ["required"] = new JArray(),
                        ["additionalProperties"] = false
                    },
                    ["transform"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("raw", "table"),
                        ["description"] = "Keep source JSON unchanged or normalize a row array to a table envelope.",
                        ["default"] = "raw"
                    },
                    ["headers"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("firstRow", "none"),
                        ["description"] = "For array rows with transform=table, use the first row as column labels or generate labels.",
                        ["default"] = "firstRow"
                    },
                    ["refreshPolicy"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("manual", "on_preview"),
                        ["description"] = "Refresh only on request or whenever the user opens HTML preview.",
                        ["default"] = "on_preview"
                    }
                },
                ["required"] = new JArray("dataName", "sourceTool", "sourceArguments"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
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
            var sourceResult = _adapter.ExecuteTool(sourceCommand) ?? ToolResult.Fail("Office data source returned no result.");
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
            PushHistory(session.HtmlWorkspace, "Before binding data " + name);
            ClearRedoHistory(session.HtmlWorkspace);
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
                ContentSha256 = Sha256(json),
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
                var result = _adapter.ExecuteTool(sourceCommand) ?? ToolResult.Fail("Office data source returned no result.");
                if (!result.Success)
                {
                    MarkBindingError(session, data, result.Message);
                    return ToolResult.Fail(result.Message ?? "Office source failed.", null, result.ErrorCode ?? "html_data_source_failed", result.Retryable);
                }

                var json = TransformSourceJson(result.DataJson, binding.Transform, binding.Headers);
                ValidateDataSource(data.Name, json);
                ValidateWorkspaceCapacity(session.HtmlWorkspace, null, null, data.Id, json);
                var now = DateTime.UtcNow;
                var hash = Sha256(json);
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
            PushHistory(session.HtmlWorkspace, "Before freezing data " + data.Name);
            ClearRedoHistory(session.HtmlWorkspace);
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
            return new JObject
            {
                ["type"] = "rnassistant.htmlDataBinding",
                ["version"] = 1,
                ["name"] = name,
                ["sourceTool"] = sourceTool,
                ["transform"] = transform,
                ["refreshPolicy"] = refreshPolicy,
                ["status"] = status,
                ["saved"] = saved,
                ["jsonCharacters"] = jsonCharacters,
                ["revisionArtifactId"] = session == null ? null : session.ActiveHtmlArtifactId
            }.ToString(Formatting.None);
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

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }

        public static HtmlWorkspace NormalizeWorkspace(HtmlWorkspace workspace)
        {
            if (workspace == null)
            {
                workspace = new HtmlWorkspace();
            }

            if (workspace.Files == null)
            {
                workspace.Files = new List<HtmlWorkspaceFile>();
            }
            if (workspace.DataSources == null)
            {
                workspace.DataSources = new List<HtmlWorkspaceDataSource>();
            }
            if (workspace.History == null)
            {
                workspace.History = new List<HtmlWorkspaceSnapshot>();
            }
            if (workspace.RedoHistory == null)
            {
                workspace.RedoHistory = new List<HtmlWorkspaceSnapshot>();
            }

            foreach (var file in workspace.Files.Where(f => f != null))
            {
                file.Path = NormalizePath(string.IsNullOrWhiteSpace(file.Path) ? file.Id : file.Path);
                file.Id = FileId(file.Path);
                file.Kind = NormalizeKind(file.Kind, file.Path);
                file.Content = file.Content ?? string.Empty;
                if (file.CreatedUtc == default(DateTime))
                {
                    file.CreatedUtc = DateTime.UtcNow;
                }
                if (file.UpdatedUtc == default(DateTime))
                {
                    file.UpdatedUtc = file.CreatedUtc;
                }
            }

            foreach (var dataSource in workspace.DataSources.Where(d => d != null))
            {
                dataSource.Name = NormalizeDataName(string.IsNullOrWhiteSpace(dataSource.Name) ? dataSource.Id : dataSource.Name);
                dataSource.Id = DataSourceId(dataSource.Name);
                dataSource.Json = dataSource.Json ?? "{}";
                if (dataSource.Binding != null)
                {
                    NormalizeBinding(dataSource.Binding, dataSource);
                }
                if (dataSource.CreatedUtc == default(DateTime))
                {
                    dataSource.CreatedUtc = DateTime.UtcNow;
                }
                if (dataSource.UpdatedUtc == default(DateTime))
                {
                    dataSource.UpdatedUtc = dataSource.CreatedUtc;
                }
            }

            workspace.History = NormalizeSnapshots(workspace.History);
            workspace.RedoHistory = NormalizeSnapshots(workspace.RedoHistory);

            if (string.IsNullOrWhiteSpace(workspace.ActiveFileId) ||
                !workspace.Files.Any(f => f != null &&
                    string.Equals(f.Id, workspace.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Kind, "html", StringComparison.OrdinalIgnoreCase)))
            {
                var firstHtml = workspace.Files.FirstOrDefault(f => f != null && string.Equals(f.Kind, "html", StringComparison.OrdinalIgnoreCase));
                workspace.ActiveFileId = firstHtml == null ? string.Empty : firstHtml.Id;
            }

            if (workspace.UpdatedUtc == default(DateTime))
            {
                workspace.UpdatedUtc = DateTime.UtcNow;
            }

            return workspace;
        }

        public static HtmlWorkspaceFile UpsertFile(ChatSession session, string path, string kind, string content, bool setActive)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            ValidateFile(path, kind, content);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var normalizedPath = NormalizePath(path);
            var id = FileId(normalizedPath);
            ValidateWorkspaceCapacity(session.HtmlWorkspace, id, content, null, null);
            PushHistory(session.HtmlWorkspace, "Before saving " + normalizedPath);
            ClearRedoHistory(session.HtmlWorkspace);
            var now = DateTime.UtcNow;
            var file = session.HtmlWorkspace.Files.FirstOrDefault(f =>
                f != null && string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                file = new HtmlWorkspaceFile
                {
                    Id = id,
                    Path = normalizedPath,
                    CreatedUtc = now
                };
                session.HtmlWorkspace.Files.Add(file);
            }

            file.Path = normalizedPath;
            file.Kind = NormalizeKind(kind, normalizedPath);
            file.Content = content ?? string.Empty;
            file.UpdatedUtc = now;
            session.HtmlWorkspace.UpdatedUtc = now;
            if (string.Equals(file.Kind, "html", StringComparison.OrdinalIgnoreCase) &&
                (setActive || string.IsNullOrWhiteSpace(session.HtmlWorkspace.ActiveFileId)))
            {
                session.HtmlWorkspace.ActiveFileId = file.Id;
            }
            else if (!string.Equals(file.Kind, "html", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.HtmlWorkspace.ActiveFileId, file.Id, StringComparison.OrdinalIgnoreCase))
            {
                session.HtmlWorkspace.ActiveFileId = string.Empty;
                NormalizeWorkspace(session.HtmlWorkspace);
            }

            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML: " + normalizedPath);

            return file;
        }

        public static HtmlWorkspaceDataSource UpsertDataSource(ChatSession session, string name, string json)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            ValidateDataSource(name, json);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var normalizedName = NormalizeDataName(name);
            var id = DataSourceId(normalizedName);
            ValidateWorkspaceCapacity(session.HtmlWorkspace, null, null, id, json);
            PushHistory(session.HtmlWorkspace, "Before saving data " + normalizedName);
            ClearRedoHistory(session.HtmlWorkspace);
            var now = DateTime.UtcNow;
            var data = session.HtmlWorkspace.DataSources.FirstOrDefault(d =>
                d != null && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (data == null)
            {
                data = new HtmlWorkspaceDataSource
                {
                    Id = id,
                    Name = normalizedName,
                    CreatedUtc = now
                };
                session.HtmlWorkspace.DataSources.Add(data);
            }

            data.Name = normalizedName;
            data.Json = json ?? "{}";
            data.Binding = null;
            data.UpdatedUtc = now;
            session.HtmlWorkspace.UpdatedUtc = now;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML data: " + normalizedName);
            return data;
        }

        public static HtmlWorkspaceFile SetActiveFile(ChatSession session, string path)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            ValidatePath(path);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var file = FindFile(session.HtmlWorkspace, path, true);
            PushHistory(session.HtmlWorkspace, "Before selecting " + NormalizePath(path));
            ClearRedoHistory(session.HtmlWorkspace);

            session.HtmlWorkspace.ActiveFileId = file.Id;
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML active file: " + file.Path);
            return file;
        }

        public static HtmlWorkspaceFile DeleteFile(ChatSession session, string path)
        {
            var file = FindFile(session, path);
            PushHistory(session.HtmlWorkspace, "Before deleting " + file.Path);
            ClearRedoHistory(session.HtmlWorkspace);
            session.HtmlWorkspace.Files.Remove(file);
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            NormalizeWorkspace(session.HtmlWorkspace);
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML deleted: " + file.Path);
            return file;
        }

        public static HtmlWorkspaceDataSource DeleteDataSource(ChatSession session, string name)
        {
            var data = FindDataSource(session, name);
            PushHistory(session.HtmlWorkspace, "Before deleting data " + data.Name);
            ClearRedoHistory(session.HtmlWorkspace);
            session.HtmlWorkspace.DataSources.Remove(data);
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            NormalizeWorkspace(session.HtmlWorkspace);
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML data deleted: " + data.Name);
            return data;
        }

        public static HtmlWorkspaceSnapshot RestoreSnapshot(ChatSession session, string snapshotId)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var snapshot = string.IsNullOrWhiteSpace(snapshotId)
                ? session.HtmlWorkspace.History.OrderByDescending(h => h.CreatedUtc).FirstOrDefault()
                : session.HtmlWorkspace.History.FirstOrDefault(h => h != null && string.Equals(h.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                throw new InvalidOperationException("HTML workspace snapshot was not found.");
            }

            if (!HtmlWorkspaceArtifactService.Restore(session, snapshot.Id))
            {
                throw new InvalidOperationException("HTML workspace artifact could not be restored.");
            }
            return snapshot;
        }

        public static HtmlWorkspaceSnapshot RedoSnapshot(ChatSession session, string snapshotId)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var snapshot = string.IsNullOrWhiteSpace(snapshotId)
                ? session.HtmlWorkspace.RedoHistory.OrderByDescending(h => h.CreatedUtc).FirstOrDefault()
                : session.HtmlWorkspace.RedoHistory.FirstOrDefault(h => h != null && string.Equals(h.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                throw new InvalidOperationException("HTML workspace redo snapshot was not found.");
            }

            if (!HtmlWorkspaceArtifactService.Restore(session, snapshot.Id))
            {
                throw new InvalidOperationException("HTML workspace artifact could not be restored.");
            }
            return snapshot;
        }

        private static string ReadWorkspaceDataJson(ChatSession session, ToolCommand command)
        {
            var workspace = NormalizedWorkspaceCopy(session.HtmlWorkspace);
            var resourceType = ToolArgumentReader.String(command.Arguments, "resourceType", string.Empty);
            var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
            if (string.IsNullOrWhiteSpace(resourceType) && !string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("resourceType is required when name is supplied.");
            }
            if (!string.IsNullOrWhiteSpace(resourceType) && string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("name is required when resourceType is supplied.");
            }
            if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
            {
                var file = FindFile(workspace, name, false);
                return JsonConvert.SerializeObject(new
                {
                    type = "rnassistant.htmlWorkspaceFile",
                    version = 1,
                    revisionArtifactId = session.ActiveHtmlArtifactId,
                    file = new { file.Id, file.Path, file.Kind, file.Content, file.CreatedUtc, file.UpdatedUtc }
                });
            }
            if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
            {
                var data = FindDataSource(workspace, name);
                return JsonConvert.SerializeObject(new
                {
                    type = "rnassistant.htmlWorkspaceData",
                    version = 1,
                    revisionArtifactId = session.ActiveHtmlArtifactId,
                    data = new { data.Id, data.Name, data.Json, binding = BindingDetails(data.Binding), data.CreatedUtc, data.UpdatedUtc }
                });
            }
            if (!string.IsNullOrWhiteSpace(resourceType))
            {
                throw new InvalidOperationException("resourceType must be file or data.");
            }

            return JsonConvert.SerializeObject(new
            {
                type = "rnassistant.htmlWorkspaceManifest",
                version = 1,
                revisionArtifactId = session.ActiveHtmlArtifactId,
                activeFileId = workspace.ActiveFileId,
                updatedUtc = workspace.UpdatedUtc,
                files = workspace.Files.Where(file => file != null).Select(file => new
                {
                    file.Id,
                    file.Path,
                    file.Kind,
                    contentCharacters = (file.Content ?? string.Empty).Length,
                    active = string.Equals(file.Id, workspace.ActiveFileId, StringComparison.OrdinalIgnoreCase),
                    file.UpdatedUtc
                }),
                dataSources = workspace.DataSources.Where(data => data != null).Select(data => new
                {
                    data.Id,
                    data.Name,
                    jsonCharacters = (data.Json ?? string.Empty).Length,
                    binding = BindingManifest(data.Binding),
                    data.UpdatedUtc
                })
            });
        }

        private static string WorkspaceMutationJson(ChatSession session, string itemType, string itemId)
        {
            var workspace = NormalizedWorkspaceCopy(session == null ? null : session.HtmlWorkspace);
            return JsonConvert.SerializeObject(new
            {
                type = "rnassistant.htmlWorkspaceMutation",
                version = 1,
                itemType = itemType,
                itemId = itemId,
                revisionArtifactId = session == null ? null : session.ActiveHtmlArtifactId,
                activeFileId = workspace.ActiveFileId,
                fileCount = workspace.Files.Count,
                dataSourceCount = workspace.DataSources.Count,
                boundDataSourceCount = workspace.DataSources.Count(item => item != null && item.Binding != null),
                updatedUtc = workspace.UpdatedUtc
            });
        }

        private static object BindingManifest(HtmlWorkspaceDataBinding binding)
        {
            if (binding == null) return null;
            return new
            {
                binding.ToolId,
                binding.Transform,
                binding.Headers,
                binding.RefreshPolicy,
                binding.Host,
                binding.DocumentTitle,
                binding.Status,
                binding.LastError,
                binding.LastRefreshUtc,
                binding.UpdatedUtc
            };
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
            if (binding.CreatedUtc == default(DateTime)) binding.CreatedUtc = dataSource == null || dataSource.CreatedUtc == default(DateTime) ? DateTime.UtcNow : dataSource.CreatedUtc;
            if (binding.UpdatedUtc == default(DateTime)) binding.UpdatedUtc = binding.CreatedUtc;
            if (string.IsNullOrWhiteSpace(binding.ContentSha256) && dataSource != null) binding.ContentSha256 = Sha256(dataSource.Json);
        }

        private static List<HtmlWorkspaceSnapshot> NormalizeSnapshots(List<HtmlWorkspaceSnapshot> snapshots)
        {
            snapshots = snapshots ?? new List<HtmlWorkspaceSnapshot>();
            foreach (var snapshot in snapshots.Where(h => h != null))
            {
                if (string.IsNullOrWhiteSpace(snapshot.Id))
                {
                    snapshot.Id = Guid.NewGuid().ToString("N");
                }
                snapshot.Label = string.IsNullOrWhiteSpace(snapshot.Label) ? "HTML workspace snapshot" : snapshot.Label.Trim();
                if (snapshot.CreatedUtc == default(DateTime))
                {
                    snapshot.CreatedUtc = DateTime.UtcNow;
                }
                if (snapshot.Files == null)
                {
                    snapshot.Files = new List<HtmlWorkspaceFile>();
                }
                if (snapshot.DataSources == null)
                {
                    snapshot.DataSources = new List<HtmlWorkspaceDataSource>();
                }
                foreach (var file in snapshot.Files.Where(f => f != null))
                {
                    file.Path = NormalizePath(string.IsNullOrWhiteSpace(file.Path) ? file.Id : file.Path);
                    file.Id = FileId(file.Path);
                    file.Kind = NormalizeKind(file.Kind, file.Path);
                    file.Content = file.Content ?? string.Empty;
                }
                foreach (var dataSource in snapshot.DataSources.Where(d => d != null))
                {
                    dataSource.Name = NormalizeDataName(string.IsNullOrWhiteSpace(dataSource.Name) ? dataSource.Id : dataSource.Name);
                    dataSource.Id = DataSourceId(dataSource.Name);
                    dataSource.Json = dataSource.Json ?? "{}";
                    if (dataSource.Binding != null) NormalizeBinding(dataSource.Binding, dataSource);
                }
            }

            var ordered = snapshots
                .Where(h => h != null)
                .OrderByDescending(h => h.CreatedUtc)
                .ToList();
            return HtmlWorkspaceHistoryPolicy.Trim(ordered);
        }

        private static void PushHistory(HtmlWorkspace workspace, string label)
        {
            workspace = NormalizeWorkspace(workspace);
            InsertSnapshot(workspace.History, CreateSnapshot(workspace, label));
        }

        private static void ClearRedoHistory(HtmlWorkspace workspace)
        {
            if (workspace == null)
            {
                return;
            }

            workspace.RedoHistory = new List<HtmlWorkspaceSnapshot>();
        }

        private static HtmlWorkspaceSnapshot CreateSnapshot(HtmlWorkspace workspace, string label)
        {
            if (!HasWorkspaceContent(workspace))
            {
                return null;
            }

            return HtmlWorkspaceCopyService.CaptureSnapshot(
                workspace,
                string.IsNullOrWhiteSpace(label) ? "HTML workspace snapshot" : label);
        }

        private static void InsertSnapshot(List<HtmlWorkspaceSnapshot> snapshots, HtmlWorkspaceSnapshot snapshot)
        {
            if (snapshots == null || snapshot == null)
            {
                return;
            }
            if (snapshots.Count > 0 && SnapshotEquals(snapshot, snapshots[0]))
            {
                return;
            }

            snapshots.Insert(0, snapshot);
            var bounded = HtmlWorkspaceHistoryPolicy.Trim(snapshots);
            snapshots.Clear();
            snapshots.AddRange(bounded);
        }

        private static void ApplySnapshot(HtmlWorkspace workspace, HtmlWorkspaceSnapshot snapshot)
        {
            workspace.Files = HtmlWorkspaceCopyService.CloneFiles(snapshot.Files);
            workspace.DataSources = HtmlWorkspaceCopyService.CloneDataSources(snapshot.DataSources);
            workspace.ActiveFileId = snapshot.ActiveFileId;
        }

        private static bool HasWorkspaceContent(HtmlWorkspace workspace)
        {
            return workspace != null &&
                (((workspace.Files != null) && workspace.Files.Any(f => f != null)) ||
                 ((workspace.DataSources != null) && workspace.DataSources.Any(d => d != null)));
        }

        private static bool SnapshotEquals(HtmlWorkspaceSnapshot left, HtmlWorkspaceSnapshot right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.ActiveFileId, right.ActiveFileId, StringComparison.OrdinalIgnoreCase) &&
                JsonConvert.SerializeObject(left.Files) == JsonConvert.SerializeObject(right.Files) &&
                JsonConvert.SerializeObject(left.DataSources) == JsonConvert.SerializeObject(right.DataSources);
        }

        private static void ValidateFile(string path, string kind, string content)
        {
            ValidatePath(path);
            if ((content ?? string.Empty).Length > MaxHtmlChars)
            {
                throw new InvalidOperationException("HTML workspace file is too large. Limit is " + MaxHtmlChars + " characters.");
            }

            NormalizeKind(kind, NormalizePath(path));
        }

        private static void ValidateWorkspaceCapacity(
            HtmlWorkspace workspace,
            string replacingFileId,
            string fileContent,
            string replacingDataId,
            string dataJson)
        {
            workspace = workspace ?? new HtmlWorkspace();
            var files = (workspace.Files ?? new List<HtmlWorkspaceFile>()).Where(item => item != null).ToList();
            var dataSources = (workspace.DataSources ?? new List<HtmlWorkspaceDataSource>()).Where(item => item != null).ToList();
            var addingFile = !string.IsNullOrWhiteSpace(replacingFileId) && files.All(item =>
                !string.Equals(item.Id, replacingFileId, StringComparison.OrdinalIgnoreCase));
            var addingData = !string.IsNullOrWhiteSpace(replacingDataId) && dataSources.All(item =>
                !string.Equals(item.Id, replacingDataId, StringComparison.OrdinalIgnoreCase));
            if (files.Count + dataSources.Count + (addingFile ? 1 : 0) + (addingData ? 1 : 0) > MaxWorkspaceItems)
            {
                throw new InvalidOperationException("HTML workspace has too many files/data sources. Limit is " + MaxWorkspaceItems + ".");
            }

            long characters = files.Sum(item => (long)(item.Content ?? string.Empty).Length) +
                dataSources.Sum(item => (long)(item.Json ?? string.Empty).Length);
            if (!string.IsNullOrWhiteSpace(replacingFileId))
            {
                var existing = files.FirstOrDefault(item => string.Equals(item.Id, replacingFileId, StringComparison.OrdinalIgnoreCase));
                characters -= existing == null ? 0 : (existing.Content ?? string.Empty).Length;
                characters += (fileContent ?? string.Empty).Length;
            }
            if (!string.IsNullOrWhiteSpace(replacingDataId))
            {
                var existing = dataSources.FirstOrDefault(item => string.Equals(item.Id, replacingDataId, StringComparison.OrdinalIgnoreCase));
                characters -= existing == null ? 0 : (existing.Json ?? string.Empty).Length;
                characters += (dataJson ?? string.Empty).Length;
            }
            if (characters > MaxWorkspaceCharacters)
            {
                throw new InvalidOperationException("HTML workspace is too large. Aggregate limit is " + MaxWorkspaceCharacters + " characters.");
            }
        }

        private static HtmlWorkspaceFile FindFile(ChatSession session, string path)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("path is required.");
            }

            ValidatePath(path);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            return FindFile(session.HtmlWorkspace, path, false);
        }

        private static HtmlWorkspaceFile FindFile(HtmlWorkspace workspace, string path, bool requireHtml)
        {
            ValidatePath(path);
            var normalizedPath = NormalizePath(path);
            var id = FileId(normalizedPath);
            var file = (workspace == null ? new List<HtmlWorkspaceFile>() : workspace.Files ?? new List<HtmlWorkspaceFile>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                throw new InvalidOperationException("HTML workspace file was not found: " + normalizedPath);
            }
            if (requireHtml && !string.Equals(file.Kind, "html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HTML workspace active file must have kind html: " + normalizedPath);
            }
            return file;
        }

        private static HtmlWorkspaceDataSource FindDataSource(ChatSession session, string name)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Chat session is required.");
            }

            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            return FindDataSource(session.HtmlWorkspace, name);
        }

        private static HtmlWorkspaceDataSource FindDataSource(HtmlWorkspace workspace, string name)
        {
            var normalizedName = NormalizeDataName(name);
            var id = DataSourceId(normalizedName);
            var data = (workspace == null ? new List<HtmlWorkspaceDataSource>() : workspace.DataSources ?? new List<HtmlWorkspaceDataSource>()).FirstOrDefault(item =>
                item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (data == null)
            {
                throw new InvalidOperationException("HTML workspace data source was not found: " + normalizedName);
            }
            return data;
        }

        private static void ValidateDataSource(string name, string json)
        {
            var normalizedName = NormalizeDataName(name);
            if (normalizedName.Length > 128)
            {
                throw new InvalidOperationException("HTML workspace data-source name is too long.");
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Data content must be non-empty valid JSON.");
            }
            if (json.Length > MaxDataChars)
            {
                throw new InvalidOperationException("HTML workspace data is too large. Limit is " + MaxDataChars + " characters.");
            }

            JToken.Parse(json);
        }

        private static void ValidatePath(string path)
        {
            var normalized = NormalizePath(path);
            if (normalized.Length > 260)
            {
                throw new InvalidOperationException("HTML workspace path is too long.");
            }
        }

        private static string NormalizePath(string path)
        {
            var value = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = "index.html";
            }
            if (value.IndexOf("..", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 || value.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid HTML workspace path: " + path);
            }
            return value;
        }

        private static HtmlWorkspace NormalizedWorkspaceCopy(HtmlWorkspace workspace)
        {
            var copy = workspace == null
                ? new HtmlWorkspace()
                : JsonConvert.DeserializeObject<HtmlWorkspace>(JsonConvert.SerializeObject(workspace));
            return NormalizeWorkspace(copy);
        }

        private static string NormalizeKind(string kind, string path)
        {
            var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "js" || value == "javascript")
            {
                value = "script";
            }
            if (value != "html" && value != "css" && value != "script")
            {
                if ((path ?? string.Empty).EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                {
                    value = "css";
                }
                else if ((path ?? string.Empty).EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    value = "script";
                }
                else
                {
                    value = "html";
                }
            }

            return value;
        }

        private static string NormalizeDataName(string name)
        {
            var value = (name ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
            if (value.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(5);
            }
            if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 5);
            }
            value = value.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf("..", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 || value.IndexOf('/') >= 0)
            {
                throw new InvalidOperationException("Invalid HTML workspace data source name: " + name);
            }
            return value;
        }

        private static string FileId(string path)
        {
            return NormalizePath(path).ToLowerInvariant();
        }

        private static string DataSourceId(string name)
        {
            return NormalizeDataName(name).ToLowerInvariant();
        }
    }
}
