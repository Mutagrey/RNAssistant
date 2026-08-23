using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlArtifactToolExecutor
    {
        public const string ReadWorkspaceToolId = "common.html_workspace_read";
        public const string UpsertFileToolId = "common.html_workspace_upsert_file";
        public const string UpsertDataToolId = "common.html_workspace_upsert_data";
        public const string DeleteFileToolId = "common.html_workspace_delete_file";
        public const string DeleteDataToolId = "common.html_workspace_delete_data";
        public const string SetActiveToolId = "common.html_workspace_set_active";

        private const int MaxHtmlChars = 300000;
        private const int MaxDataChars = 300000;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(ReadWorkspaceToolId, "Common", "Read-only: Read the active chat HTML workspace files and JSON data sources.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", name: "html_workspace_read");
            yield return ControllerToolDefinition.Create(UpsertFileToolId, "Common", "Workspace: Create or update a text file in the active chat HTML workspace. Use kind html, css, script, or js for index.html, styles.css, and app.js files.", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Workspace-relative file path.\",\"default\":\"index.html\"},\"kind\":{\"type\":\"string\",\"enum\":[\"html\",\"css\",\"script\",\"js\"],\"description\":\"Workspace file kind; omit to infer it from path.\"},\"content\":{\"type\":\"string\",\"description\":\"Complete text content of the workspace file.\"},\"setActive\":{\"type\":\"boolean\",\"description\":\"Whether the written HTML file becomes the active preview file.\",\"default\":true}},\"required\":[\"content\"],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_workspace_upsert_file");
            yield return ControllerToolDefinition.Create(UpsertDataToolId, "Common", "Workspace: Create or update a JSON data source for the active chat HTML workspace. Preview exposes it as window.RNAssistantData[name].", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Stable data-source name exposed under window.RNAssistantData.\"},\"json\":{\"type\":\"string\",\"description\":\"Complete valid JSON value serialized as text.\"}},\"required\":[\"name\",\"json\"],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_workspace_upsert_data");
            yield return ControllerToolDefinition.Create(DeleteFileToolId, "Common", "Workspace: Delete one file from the active chat HTML workspace. The deletion can be reverted with workspace undo.", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Exact workspace-relative file path.\"}},\"required\":[\"path\"],\"additionalProperties\":false}", mutatesLocalState: true, riskLevel: 1, name: "html_workspace_delete_file");
            yield return ControllerToolDefinition.Create(DeleteDataToolId, "Common", "Workspace: Delete one JSON data source from the active chat HTML workspace. The deletion can be reverted with workspace undo.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Exact data-source name.\"}},\"required\":[\"name\"],\"additionalProperties\":false}", mutatesLocalState: true, riskLevel: 1, name: "html_workspace_delete_data");
            yield return ControllerToolDefinition.Create(SetActiveToolId, "Common", "Workspace: Select the active HTML file displayed on the HTML tab for the active chat.", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Exact workspace-relative HTML file path.\",\"default\":\"index.html\"}},\"required\":[],\"additionalProperties\":false}", mutatesLocalState: true, name: "html_workspace_set_active");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, ChatSession session, bool dryRun)
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

                if (string.Equals(command.ToolId, DeleteFileToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var path = ToolArgumentReader.String(command.Arguments, "path", string.Empty);
                    if (dryRun)
                    {
                        FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), path, false);
                        return ToolResult.Ok("Dry run: would delete HTML workspace file " + NormalizePath(path) + ".", WorkspaceDataJson(session.HtmlWorkspace));
                    }

                    var file = DeleteFile(session, path);
                    return ToolResult.Ok("HTML workspace file deleted: " + file.Path, WorkspaceDataJson(session.HtmlWorkspace));
                }

                if (string.Equals(command.ToolId, DeleteDataToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var name = ToolArgumentReader.String(command.Arguments, "name", string.Empty);
                    if (dryRun)
                    {
                        FindDataSource(NormalizedWorkspaceCopy(session.HtmlWorkspace), name);
                        return ToolResult.Ok("Dry run: would delete HTML workspace data source " + NormalizeDataName(name) + ".", WorkspaceDataJson(session.HtmlWorkspace));
                    }

                    var data = DeleteDataSource(session, name);
                    return ToolResult.Ok("HTML workspace data source deleted: " + data.Name, WorkspaceDataJson(session.HtmlWorkspace));
                }

                if (string.Equals(command.ToolId, SetActiveToolId, StringComparison.OrdinalIgnoreCase))
                {
                    var path = ToolArgumentReader.String(command.Arguments, "path", "index.html");
                    if (dryRun)
                    {
                        FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), path, true);
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
            PushHistory(session.HtmlWorkspace, "Before saving " + NormalizePath(path));
            ClearRedoHistory(session.HtmlWorkspace);
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
            PushHistory(session.HtmlWorkspace, "Before saving data " + NormalizeDataName(name));
            ClearRedoHistory(session.HtmlWorkspace);
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

            InsertSnapshot(session.HtmlWorkspace.RedoHistory, CreateSnapshot(session.HtmlWorkspace, "Before undo"));
            ApplySnapshot(session.HtmlWorkspace, snapshot);
            session.HtmlWorkspace.History.RemoveAll(h => h != null && string.Equals(h.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            NormalizeWorkspace(session.HtmlWorkspace);
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML workspace restored");
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

            InsertSnapshot(session.HtmlWorkspace.History, CreateSnapshot(session.HtmlWorkspace, "Before redo"));
            ApplySnapshot(session.HtmlWorkspace, snapshot);
            session.HtmlWorkspace.RedoHistory.RemoveAll(h => h != null && string.Equals(h.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            NormalizeWorkspace(session.HtmlWorkspace);
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML workspace redone");
            return snapshot;
        }

        private static string WorkspaceDataJson(HtmlWorkspace workspace)
        {
            var copy = new HtmlWorkspace
            {
                ActiveFileId = workspace == null ? string.Empty : workspace.ActiveFileId,
                Files = (workspace == null ? new List<HtmlWorkspaceFile>() : workspace.Files ?? new List<HtmlWorkspaceFile>())
                    .Where(file => file != null)
                    .Select(CloneFile)
                    .ToList(),
                DataSources = (workspace == null ? new List<HtmlWorkspaceDataSource>() : workspace.DataSources ?? new List<HtmlWorkspaceDataSource>())
                    .Where(dataSource => dataSource != null)
                    .Select(CloneDataSource)
                    .ToList(),
                History = new List<HtmlWorkspaceSnapshot>(),
                RedoHistory = new List<HtmlWorkspaceSnapshot>(),
                UpdatedUtc = workspace == null ? DateTime.UtcNow : workspace.UpdatedUtc
            };
            return JsonConvert.SerializeObject(new
            {
                type = "rnassistant.htmlWorkspace",
                version = 1,
                workspace = NormalizeWorkspace(copy)
            });
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

            return new HtmlWorkspaceSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = string.IsNullOrWhiteSpace(label) ? "HTML workspace snapshot" : label,
                ActiveFileId = workspace.ActiveFileId,
                Files = workspace.Files.Where(f => f != null).Select(CloneFile).ToList(),
                DataSources = workspace.DataSources.Where(d => d != null).Select(CloneDataSource).ToList(),
                CreatedUtc = DateTime.UtcNow
            };
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
            workspace.Files = (snapshot.Files ?? new List<HtmlWorkspaceFile>()).Where(f => f != null).Select(CloneFile).ToList();
            workspace.DataSources = (snapshot.DataSources ?? new List<HtmlWorkspaceDataSource>()).Where(d => d != null).Select(CloneDataSource).ToList();
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

        private static HtmlWorkspaceFile CloneFile(HtmlWorkspaceFile file)
        {
            return new HtmlWorkspaceFile
            {
                Id = file.Id,
                Path = file.Path,
                Kind = file.Kind,
                Content = file.Content,
                CreatedUtc = file.CreatedUtc,
                UpdatedUtc = file.UpdatedUtc
            };
        }

        private static HtmlWorkspaceDataSource CloneDataSource(HtmlWorkspaceDataSource dataSource)
        {
            return new HtmlWorkspaceDataSource
            {
                Id = dataSource.Id,
                Name = dataSource.Name,
                Json = dataSource.Json,
                CreatedUtc = dataSource.CreatedUtc,
                UpdatedUtc = dataSource.UpdatedUtc
            };
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
