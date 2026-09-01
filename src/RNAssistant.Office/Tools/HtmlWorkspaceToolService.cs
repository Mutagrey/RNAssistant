using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Outlook;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class HtmlWorkspaceToolService
    {
        private const int MaxHtmlChars = 300000;
        private const int MaxDataChars = 300000;
        private const int MaxWorkspaceItems = 100;
        private const int MaxWorkspaceCharacters = 1500000;

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly Dictionary<string, ToolCatalogEntry> _dataSourceTools;
        private readonly Func<ChatSession, IDisposable> _beginLiveOfficeRead;
        private readonly Func<string, IDictionary<string, object>,
            CancellationToken, HtmlDataSourceReadOutcome> _executeOfficeDataSource;
        private readonly ExcelReadToolAdapter _standaloneExcelRead;
        private readonly WordToolAdapter _standaloneWordRead;
        private readonly PowerPointToolAdapter _standalonePowerPointRead;
        private readonly OutlookToolAdapter _standaloneOutlookRead;

        public HtmlWorkspaceToolService()
            : this(null, null, null, null)
        {
        }

        public HtmlWorkspaceToolService(
            IOfficeApplicationAdapter adapter,
            IEnumerable<ToolCatalogEntry> adapterTools)
            : this(adapter, adapterTools, null, null)
        {
        }

        internal HtmlWorkspaceToolService(
            IOfficeApplicationAdapter adapter,
            IEnumerable<ToolCatalogEntry> adapterTools,
            Func<ChatSession, IDisposable> beginLiveOfficeRead)
            : this(adapter, adapterTools, beginLiveOfficeRead, null)
        {
        }

        internal HtmlWorkspaceToolService(
            IOfficeApplicationAdapter adapter,
            IEnumerable<ToolCatalogEntry> adapterTools,
            Func<ChatSession, IDisposable> beginLiveOfficeRead,
            Func<string, IDictionary<string, object>, CancellationToken,
                HtmlDataSourceReadOutcome> executeOfficeDataSource)
        {
            _adapter = adapter;
            _beginLiveOfficeRead = beginLiveOfficeRead;
            _executeOfficeDataSource = executeOfficeDataSource;
            var excel = adapter as IExcelBackendProvider;
            _standaloneExcelRead = excel == null || excel.ExcelReadBackend == null
                ? null : new ExcelReadToolAdapter(excel.ExcelReadBackend);
            var word = adapter as IWordBackendProvider;
            _standaloneWordRead = word == null || word.WordBackend == null
                ? null : new WordToolAdapter(word.WordBackend);
            var powerPoint = adapter as IPowerPointBackendProvider;
            _standalonePowerPointRead = powerPoint == null ||
                powerPoint.PowerPointBackend == null
                ? null : new PowerPointToolAdapter(
                    powerPoint.PowerPointBackend);
            var outlook = adapter as IOutlookBackendProvider;
            _standaloneOutlookRead = outlook == null ||
                outlook.OutlookBackend == null
                ? null : new OutlookToolAdapter(outlook.OutlookBackend);
            _dataSourceTools = (adapterTools ?? new ToolCatalogEntry[0])
                .Where(IsEligibleDataSourceTool)
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(tool => tool.Id, tool => tool.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        internal bool HasDataSourceTools
        {
            get { return _dataSourceTools.Count > 0; }
        }

        internal HtmlWorkspaceToolOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            ChatSession session,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var dispatched = false;
            Action mark = delegate
            {
                if (dispatched) return;
                dispatched = true;
                if (markDispatchPossible != null) markDispatchPossible();
            };
            try
            {
                if (session == null)
                    return HtmlWorkspaceToolOutcome.Error(
                        "HTML workspace requires an active chat session.", null,
                        "html_workspace_session_required", false);

                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.InspectWorkspaceToolId,
                    StringComparison.Ordinal))
                    return InspectWorkspace(session, arguments, cancellationToken);

                HtmlWorkspaceArtifactService.EnsureMutable(session);
                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                    StringComparison.Ordinal))
                {
                    return ApplyWorkspacePatch(
                        session, arguments, mark, cancellationToken);
                }
                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.BindDataToolId,
                    StringComparison.Ordinal))
                {
                    return BindDataSource(
                        session, arguments, mark, cancellationToken);
                }
                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.RefreshDataToolId,
                    StringComparison.Ordinal))
                {
                    return RefreshDataSources(
                        session, arguments, mark, cancellationToken);
                }
                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.FreezeDataToolId,
                    StringComparison.Ordinal))
                {
                    return FreezeDataSource(session, arguments, mark);
                }
                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.UpsertToolId,
                    StringComparison.Ordinal))
                {
                    var resourceType = ToolArgumentReader.String(
                        arguments, "resourceType", string.Empty);
                    var name = ToolArgumentReader.String(
                        arguments, "name", string.Empty);
                    var content = ToolArgumentReader.String(
                        arguments, "content", string.Empty);
                    var setActive = ToolArgumentReader.Boolean(
                        arguments, "setActive", true);
                    var mode = ToolArgumentReader.String(
                        arguments, "mode", "upsert");
                    var modeError = ValidateWorkspaceUpsertMode(session, resourceType, name, mode);
                    if (modeError != null) return modeError;
                    if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateFile(name, string.Empty, content);
                        var normalizedPath = NormalizePath(name);
                        ValidateWorkspaceCapacity(
                            NormalizedWorkspaceCopy(session.HtmlWorkspace),
                            FileId(normalizedPath), content, null, null);
                        mark();
                        var file = UpsertFile(session, name, string.Empty, content, setActive);
                        return HtmlWorkspaceToolOutcome.Ok(
                            "HTML workspace file saved: " + file.Path,
                            WorkspaceMutationJson(session, "file", file.Path),
                            HtmlWorkspaceEffect.VerifiedChange);
                    }
                    if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateDataSource(name, content);
                        var normalizedName = NormalizeDataName(name);
                        ValidateWorkspaceCapacity(
                            NormalizedWorkspaceCopy(session.HtmlWorkspace),
                            null, null, DataSourceId(normalizedName), content);
                        mark();
                        var data = UpsertDataSource(session, name, content);
                        return HtmlWorkspaceToolOutcome.Ok(
                            "HTML workspace data saved: " + data.Name,
                            WorkspaceMutationJson(session, "data", data.Name),
                            HtmlWorkspaceEffect.VerifiedChange);
                    }
                    return HtmlWorkspaceToolOutcome.Error(
                        "resourceType must be file or data.", null,
                        "invalid_arguments", true);
                }

                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.DeleteToolId,
                    StringComparison.Ordinal))
                {
                    var resourceType = ToolArgumentReader.String(
                        arguments, "resourceType", string.Empty);
                    var name = ToolArgumentReader.String(
                        arguments, "name", string.Empty);
                    if (string.Equals(resourceType, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), name, false);
                        mark();
                        var file = DeleteFile(session, name);
                        return HtmlWorkspaceToolOutcome.Ok(
                            "HTML workspace file deleted: " + file.Path,
                            WorkspaceMutationJson(session, "file", file.Path),
                            HtmlWorkspaceEffect.VerifiedChange);
                    }
                    if (string.Equals(resourceType, "data", StringComparison.OrdinalIgnoreCase))
                    {
                        FindDataSource(NormalizedWorkspaceCopy(session.HtmlWorkspace), name);
                        mark();
                        var data = DeleteDataSource(session, name);
                        return HtmlWorkspaceToolOutcome.Ok(
                            "HTML workspace data source deleted: " + data.Name,
                            WorkspaceMutationJson(session, "data", data.Name),
                            HtmlWorkspaceEffect.VerifiedChange);
                    }
                    return HtmlWorkspaceToolOutcome.Error(
                        "resourceType must be file or data.", null,
                        "invalid_arguments", true);
                }

                if (string.Equals(toolId,
                    HtmlWorkspaceToolCatalog.SetActiveToolId,
                    StringComparison.Ordinal))
                {
                    var path = ToolArgumentReader.String(
                        arguments, "name", "index.html");
                    FindFile(NormalizedWorkspaceCopy(session.HtmlWorkspace), path, true);
                    mark();
                    var file = SetActiveFile(session, path);
                    return HtmlWorkspaceToolOutcome.Ok(
                        "HTML workspace active file selected: " + file.Path,
                        WorkspaceMutationJson(session, "file", file.Path),
                        HtmlWorkspaceEffect.VerifiedChange);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                return Failure(dispatched,
                    "Invalid HTML workspace JSON data source: " + ex.Message,
                    "invalid_html_workspace_json", true);
            }
            catch (InvalidOperationException ex)
            {
                return Failure(dispatched, ex.Message,
                    "invalid_html_workspace", true);
            }

            return HtmlWorkspaceToolOutcome.Error(
                "Unknown HTML workspace tool: " + toolId, null,
                "unknown_tool", false);
        }

        private static HtmlWorkspaceToolOutcome Failure(
            bool dispatched, string message, string code, bool retryable)
        {
            return dispatched
                ? HtmlWorkspaceToolOutcome.Unknown(message, null,
                    "html_workspace_effect_unknown")
                : HtmlWorkspaceToolOutcome.Error(
                    message, null, code, retryable);
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
            if (workspace.RedoBranches == null)
            {
                workspace.RedoBranches = new List<HtmlWorkspaceRedoBranch>();
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
            workspace.RedoBranches = workspace.RedoBranches.Where(item => item != null).ToList();

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

            HtmlWorkspaceArtifactService.EnsureMutable(session);
            ValidateFile(path, kind, content);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var normalizedPath = NormalizePath(path);
            var id = FileId(normalizedPath);
            ValidateWorkspaceCapacity(session.HtmlWorkspace, id, content, null, null);
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

            HtmlWorkspaceArtifactService.EnsureMutable(session);
            ValidateDataSource(name, json);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var normalizedName = NormalizeDataName(name);
            var id = DataSourceId(normalizedName);
            ValidateWorkspaceCapacity(session.HtmlWorkspace, null, null, id, json);
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

            HtmlWorkspaceArtifactService.EnsureMutable(session);
            ValidatePath(path);
            session.HtmlWorkspace = NormalizeWorkspace(session.HtmlWorkspace);
            var file = FindFile(session.HtmlWorkspace, path, true);
            session.HtmlWorkspace.ActiveFileId = file.Id;
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML active file: " + file.Path);
            return file;
        }

        public static HtmlWorkspaceFile DeleteFile(ChatSession session, string path)
        {
            HtmlWorkspaceArtifactService.EnsureMutable(session);
            var file = FindFile(session, path);
            session.HtmlWorkspace.Files.Remove(file);
            session.HtmlWorkspace.UpdatedUtc = DateTime.UtcNow;
            NormalizeWorkspace(session.HtmlWorkspace);
            HtmlWorkspaceArtifactService.CaptureCurrent(session, "HTML deleted: " + file.Path);
            return file;
        }

        public static HtmlWorkspaceDataSource DeleteDataSource(ChatSession session, string name)
        {
            HtmlWorkspaceArtifactService.EnsureMutable(session);
            var data = FindDataSource(session, name);
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
            var branches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
            if (string.IsNullOrWhiteSpace(snapshotId) && branches.Count > 1)
            {
                throw new InvalidOperationException("HTML workspace redo has multiple branches; an explicit snapshot id is required.");
            }
            var branch = string.IsNullOrWhiteSpace(snapshotId)
                ? branches.SingleOrDefault()
                : branches.FirstOrDefault(item => string.Equals(item.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
            if (branch == null)
            {
                throw new InvalidOperationException("HTML workspace redo snapshot was not found.");
            }

            if (!HtmlWorkspaceArtifactService.Restore(session, branch.Id))
            {
                throw new InvalidOperationException("HTML workspace artifact could not be restored.");
            }
            var restored = HtmlWorkspaceCopyService.CaptureSnapshot(session.HtmlWorkspace, branch.Label);
            restored.Id = branch.Id;
            restored.CreatedUtc = branch.CreatedUtc;
            return restored;
        }

        private static string WorkspaceMutationJson(ChatSession session, string itemType, string itemId)
        {
            var workspace = NormalizedWorkspaceCopy(session == null ? null : session.HtmlWorkspace);
            return AddWorkspaceResourceRefs(session, new JObject
            {
                ["type"] = "rnassistant.htmlWorkspaceMutation",
                ["version"] = 2,
                ["itemType"] = itemType,
                ["itemId"] = itemId,
                ["revisionArtifactId"] = session == null ? null : session.ActiveHtmlArtifactId,
                ["activeFileId"] = workspace.ActiveFileId,
                ["fileCount"] = workspace.Files.Count,
                ["dataSourceCount"] = workspace.DataSources.Count,
                ["boundDataSourceCount"] = workspace.DataSources.Count(item => item != null && item.Binding != null),
                ["updatedUtc"] = workspace.UpdatedUtc
            }).ToString(Formatting.None);
        }

        private static JObject AddWorkspaceResourceRefs(ChatSession session, JObject data)
        {
            var artifact = (session == null ? null : session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id,
                    session.ActiveHtmlArtifactId,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            var exactArtifact = artifact.Count == 1 ? artifact[0] : null;
            var artifactRef = exactArtifact == null
                ? null
                : ChatResourceUri.CreateArtifactRevision(session, exactArtifact);
            var members = exactArtifact == null
                ? new object[0]
                : new ChatHtmlResourceCatalog(null).DescribeMembers(session, exactArtifact)
                    .Select(item => (object)new
                    {
                        memberType = string.Equals(item.Kind, ChatHtmlResourceCatalog.FileKind, StringComparison.Ordinal)
                            ? "file" : "data",
                        path = item.Title,
                        uri = item.Reference.Uri,
                        revision = item.Reference.Revision
                    })
                    .ToArray();
            data = data ?? new JObject();
            data["artifactRef"] = artifactRef == null ? JValue.CreateNull() : JToken.FromObject(artifactRef);
            data["members"] = JArray.FromObject(members);
            return data;
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

        internal static string NormalizeWorkspacePath(string path)
        {
            return NormalizePath(path);
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
