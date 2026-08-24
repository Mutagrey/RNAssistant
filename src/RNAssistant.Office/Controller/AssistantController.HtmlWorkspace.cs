using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Tools;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public Task<HtmlFetchResponse> HtmlFetchAsync(HtmlFetchRequest request, CancellationToken cancellationToken)
        {
            return _htmlNetwork.FetchAsync(request, cancellationToken);
        }

        public HtmlOriginPermissionResponse AllowHtmlNetworkOrigin(string origin)
        {
            return new HtmlOriginPermissionResponse
            {
                Origin = _htmlNetwork.AllowOrigin(origin),
                Allowed = true
            };
        }

        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null)
        {
            var session = LoadSession(chatId);
            return HtmlWorkspaceState(session);
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                HtmlArtifactToolExecutor.UpsertFile(session, path, kind, content, setActive);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                HtmlArtifactToolExecutor.UpsertDataSource(session, name, json);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(string chatId, string path)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                HtmlArtifactToolExecutor.DeleteFile(session, path);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(string chatId, string name)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                HtmlArtifactToolExecutor.DeleteDataSource(session, name);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                HtmlArtifactToolExecutor.SetActiveFile(session, path);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var targetId = string.IsNullOrWhiteSpace(snapshotId)
                    ? session.HtmlWorkspace.History.Select(item => item == null ? null : item.Id)
                        .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
                    : snapshotId;
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    throw new System.InvalidOperationException("HTML workspace snapshot was not found.");
                }
                if (!_chatStore.LoadArtifactBody(session, targetId))
                {
                    throw new System.InvalidOperationException("HTML workspace artifact body is missing or corrupt.");
                }
                HtmlArtifactToolExecutor.RestoreSnapshot(session, targetId);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(string chatId, string snapshotId)
        {
            return WithReservedSession(LoadSession(chatId), session =>
            {
                var branches = HtmlWorkspaceNavigationService.GetRedoBranches(session);
                if (string.IsNullOrWhiteSpace(snapshotId) && branches.Count > 1)
                {
                    return HtmlWorkspaceState(session, true);
                }
                var branch = string.IsNullOrWhiteSpace(snapshotId)
                    ? branches.SingleOrDefault()
                    : branches.FirstOrDefault(item => string.Equals(item.Id, snapshotId, System.StringComparison.OrdinalIgnoreCase));
                if (branch == null)
                {
                    throw new System.InvalidOperationException("HTML workspace redo target must be a direct child revision.");
                }
                var targetId = branch.Id;
                if (!_chatStore.LoadArtifactBody(session, targetId))
                {
                    throw new System.InvalidOperationException("HTML workspace artifact body is missing or corrupt.");
                }
                HtmlArtifactToolExecutor.RedoSnapshot(session, targetId);
                SaveSessionChanges(session);
                return HtmlWorkspaceState(session);
            });
        }

        private static HtmlWorkspaceResponse HtmlWorkspaceState(ChatSession session, bool redoChoiceRequired = false)
        {
            return new HtmlWorkspaceResponse
            {
                ActiveChatId = session.Id,
                Workspace = HtmlWorkspaceDto.From(session == null ? null : HtmlArtifactToolExecutor.NormalizeWorkspace(session.HtmlWorkspace)),
                RedoChoiceRequired = redoChoiceRequired
            };
        }
    }
}
