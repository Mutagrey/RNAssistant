using System;
using System.Collections.Generic;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    public static class HtmlWorkspaceCopyService
    {
        public static HtmlWorkspace CloneCurrent(HtmlWorkspace workspace)
        {
            if (workspace == null)
            {
                return new HtmlWorkspace();
            }

            return new HtmlWorkspace
            {
                ActiveFileId = workspace.ActiveFileId,
                Files = CloneFiles(workspace.Files),
                DataSources = CloneDataSources(workspace.DataSources),
                History = new List<HtmlWorkspaceSnapshot>(),
                RedoBranches = new List<HtmlWorkspaceRedoBranch>(),
                UpdatedUtc = workspace.UpdatedUtc
            };
        }

        public static HtmlWorkspaceSnapshot CaptureSnapshot(HtmlWorkspace workspace, string label)
        {
            workspace = workspace ?? new HtmlWorkspace();
            return new HtmlWorkspaceSnapshot
            {
                Label = label,
                ActiveFileId = workspace.ActiveFileId,
                Files = CloneFiles(workspace.Files),
                DataSources = CloneDataSources(workspace.DataSources),
                CreatedUtc = DateTime.UtcNow
            };
        }

        public static HtmlWorkspace CreateWorkspaceFromSnapshot(HtmlWorkspaceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new HtmlWorkspace();
            }

            return new HtmlWorkspace
            {
                ActiveFileId = snapshot.ActiveFileId,
                Files = CloneFiles(snapshot.Files),
                DataSources = CloneDataSources(snapshot.DataSources),
                History = new List<HtmlWorkspaceSnapshot>(),
                RedoBranches = new List<HtmlWorkspaceRedoBranch>(),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        public static List<HtmlWorkspaceFile> CloneFiles(IEnumerable<HtmlWorkspaceFile> files)
        {
            var result = new List<HtmlWorkspaceFile>();
            foreach (var file in files ?? new HtmlWorkspaceFile[0])
            {
                if (file == null) continue;
                result.Add(new HtmlWorkspaceFile
                {
                    Id = file.Id,
                    Path = file.Path,
                    Kind = file.Kind,
                    Content = file.Content,
                    CreatedUtc = file.CreatedUtc,
                    UpdatedUtc = file.UpdatedUtc
                });
            }
            return result;
        }

        public static List<HtmlWorkspaceDataSource> CloneDataSources(IEnumerable<HtmlWorkspaceDataSource> dataSources)
        {
            var result = new List<HtmlWorkspaceDataSource>();
            foreach (var dataSource in dataSources ?? new HtmlWorkspaceDataSource[0])
            {
                if (dataSource == null) continue;
                result.Add(new HtmlWorkspaceDataSource
                {
                    Id = dataSource.Id,
                    Name = dataSource.Name,
                    Binding = CloneBinding(dataSource.Binding),
                    CreatedUtc = dataSource.CreatedUtc,
                    UpdatedUtc = dataSource.UpdatedUtc
                });
            }
            return result;
        }

        private static HtmlWorkspaceDataBinding CloneBinding(HtmlWorkspaceDataBinding binding)
        {
            if (binding == null) return null;
            return new HtmlWorkspaceDataBinding
            {
                Resource = binding.Resource?.Copy(),
                Policy = binding.Policy,
                View = binding.View,
                ViewPath = binding.ViewPath,
                Schema = binding.Schema?.Copy(),
                Mapping = binding.Mapping?.Copy()
            };
        }
    }
}
