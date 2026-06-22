using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlArtifactToolExecutor
    {
        public const string RenderHtmlToolId = "common.render_html";
        public const string ReadWorkspaceToolId = "common.html_workspace_read";
        public const string UpsertFileToolId = "common.html_workspace_upsert_file";
        public const string UpsertDataToolId = "common.html_workspace_upsert_data";
        public const string SetActiveToolId = "common.html_workspace_set_active";

        private const int MaxHtmlChars = 300000;
        private const int MaxDataChars = 300000;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return new ToolDefinition
            {
                Id = RenderHtmlToolId,
                Host = "Common",
                Name = "render_html",
                Description = "Read-only: Render a raw HTML component in chat when unsafe HTML artifacts are enabled.",
                ArgumentSchemaJson = "{\"title\":\"Component title\",\"html\":\"<html or fragment>\",\"height\":360}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
            yield return new ToolDefinition
            {
                Id = ReadWorkspaceToolId,
                Host = "Common",
                Name = "html_workspace_read",
                Description = "Read-only: Read the active chat HTML workspace files and JSON data sources.",
                ArgumentSchemaJson = "{}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
            yield return new ToolDefinition
            {
                Id = UpsertFileToolId,
                Host = "Common",
                Name = "html_workspace_upsert_file",
                Description = "Workspace: Create or update a text file in the active chat HTML workspace. Use kind html, css, or script for index.html, styles.css, and app.js files.",
                ArgumentSchemaJson = "{\"path\":\"index.html\",\"kind\":\"html|css|script\",\"content\":\"<html>...</html>\",\"setActive\":true}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
            yield return new ToolDefinition
            {
                Id = UpsertDataToolId,
                Host = "Common",
                Name = "html_workspace_upsert_data",
                Description = "Workspace: Create or update a JSON data source for the active chat HTML workspace. Preview exposes it as window.RNAssistantData[name].",
                ArgumentSchemaJson = "{\"name\":\"sales\",\"json\":\"{\\\"rows\\\":[]}\"}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
            yield return new ToolDefinition
            {
                Id = SetActiveToolId,
                Host = "Common",
                Name = "html_workspace_set_active",
                Description = "Workspace: Select the active HTML file displayed on the HTML tab for the active chat.",
                ArgumentSchemaJson = "{\"path\":\"index.html\"}",
                BuiltIn = true,
                Enabled = true,
                MutatesDocument = false,
                AgentCanRun = true
            };
        }

        public bool IsControllerTool(string toolId)
        {
            return string.Equals(toolId, RenderHtmlToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, UpsertFileToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, UpsertDataToolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, SetActiveToolId, StringComparison.OrdinalIgnoreCase);
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings, ChatSession session, bool dryRun)
        {
            if (command == null)
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            if (string.Equals(command.ToolId, RenderHtmlToolId, StringComparison.OrdinalIgnoreCase))
            {
                return RenderHtmlArtifact(command, settings);
            }

            try
            {
                if (session == null)
                {
                    return ToolResult.Fail("HTML workspace requires an active chat session.");
                }

                if (string.Equals(command.ToolId, ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase))
                {
                    NormalizeWorkspace(session.HtmlWorkspace);
                    return ToolResult.Ok("HTML workspace read.", WorkspaceDataJson(session.HtmlWorkspace));
                }

                if (string.Equals(command.ToolId, UpsertFileToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var path = ToolArgumentReader.String(command.Arguments, "path", "index.html");
                    var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty);
                    var content = ToolArgumentReader.String(command.Arguments, "content", string.Empty);
                    var setActive = ToolArgumentReader.Boolean(command.Arguments, "setActive", true);
                    if (dryRun)
                    {
                        ValidateFile(path, kind, content);
                        return ToolResult.Ok("Dry run: would save HTML workspace file " + NormalizePath(path) + ".", WorkspaceDataJson(session.HtmlWorkspace));
                    }

                    var file = UpsertFile(session, path, kind, content, setActive);
                    return ToolResult.Ok("HTML workspace file saved: " + file.Path, WorkspaceDataJson(session.HtmlWorkspace));
                }

                if (string.Equals(command.ToolId, UpsertDataToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
                    var json = ToolArgumentReader.String(command.Arguments, "json", string.Empty);
                    if (dryRun)
                    {
                        ValidateDataSource(name, json);
                        return ToolResult.Ok("Dry run: would save HTML workspace data source " + NormalizeDataName(name) + ".", WorkspaceDataJson(session.HtmlWorkspace));
                    }

                    var data = UpsertDataSource(session, name, json);
                    return ToolResult.Ok("HTML workspace data saved: " + data.Name, WorkspaceDataJson(session.HtmlWorkspace));
                }

                if (string.Equals(command.ToolId, SetActiveToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var path = ToolArgumentReader.String(command.Arguments, "path", "index.html");
                    if (dryRun)
                    {
                        ValidatePath(path);
                        return ToolResult.Ok("Dry run: would select HTML workspace file " + NormalizePath(path) + ".", WorkspaceDataJson(session.HtmlWorkspace));
                    }

                    var file = SetActiveFile(session, path);
                    return ToolResult.Ok("HTML workspace active file selected: " + file.Path, WorkspaceDataJson(session.HtmlWorkspace));
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
                if (dataSource.CreatedUtc == default(DateTime))
                {
                    dataSource.CreatedUtc = DateTime.UtcNow;
                }
                if (dataSource.UpdatedUtc == default(DateTime))
                {
                    dataSource.UpdatedUtc = dataSource.CreatedUtc;
                }
            }

            if (string.IsNullOrWhiteSpace(workspace.ActiveFileId) ||
                !workspace.Files.Any(f => f != null && string.Equals(f.Id, workspace.ActiveFileId, StringComparison.OrdinalIgnoreCase)))
            {
                var firstHtml = workspace.Files.FirstOrDefault(f => f != null && string.Equals(f.Kind, "html", StringComparison.OrdinalIgnoreCase));
                var first = firstHtml ?? workspace.Files.FirstOrDefault(f => f != null);
                workspace.ActiveFileId = first == null ? string.Empty : first.Id;
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
            if (setActive || string.IsNullOrWhiteSpace(session.HtmlWorkspace.ActiveFileId))
            {
                session.HtmlWorkspace.ActiveFileId = file.Id;
            }

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
            data.UpdatedUtc = now;
            session.HtmlWorkspace.UpdatedUtc = now;
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
            var id = FileId(NormalizePath(path));
            var file = session.HtmlWorkspace.Files.FirstOrDefault(f =>
                f != null && string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                throw new InvalidOperationException("HTML workspace file was not found: " + NormalizePath(path));
            }

            session.HtmlWorkspace.ActiveFileId = file.Id;
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            return file;
        }

        private static ToolResult RenderHtmlArtifact(ToolCommand command, AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            if (!settings.AllowUnsafeHtmlArtifacts)
            {
                return ToolResult.Fail("Unsafe HTML artifacts are disabled. Enable them in Settings > Interface before using common.render_html.");
            }

            var html = ToolArgumentReader.String(command.Arguments, "html", string.Empty);
            if (string.IsNullOrWhiteSpace(html))
            {
                return ToolResult.Fail("html is required.");
            }
            if (html.Length > MaxHtmlChars)
            {
                return ToolResult.Fail("HTML artifact is too large. Limit is " + MaxHtmlChars + " characters.");
            }

            var height = Math.Max(180, Math.Min(900, ToolArgumentReader.Int32(command.Arguments, "height", 360)));
            var title = ToolArgumentReader.String(command.Arguments, "title", "HTML component");
            return ToolResult.Ok("HTML artifact created: " + title, JsonConvert.SerializeObject(new
            {
                type = "rnassistant.html",
                version = 1,
                title = title,
                html = html,
                height = height
            }));
        }

        private static string WorkspaceDataJson(HtmlWorkspace workspace)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "rnassistant.htmlWorkspace",
                version = 1,
                workspace = NormalizeWorkspace(workspace)
            });
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

        private static void ValidateDataSource(string name, string json)
        {
            NormalizeDataName(name);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("json is required.");
            }
            if (json.Length > MaxDataChars)
            {
                throw new InvalidOperationException("HTML workspace data is too large. Limit is " + MaxDataChars + " characters.");
            }

            JToken.Parse(json);
        }

        private static void ValidatePath(string path)
        {
            NormalizePath(path);
        }

        private static string NormalizePath(string path)
        {
            var value = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
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
