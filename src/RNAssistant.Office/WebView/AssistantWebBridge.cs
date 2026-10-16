using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;

namespace RNAssistant.Office.WebView
{
    public sealed class AssistantWebBridge : IDisposable
    {
        private AssistantController _controller;
        private readonly WorkspaceApplicationService _workspaces;
        private readonly Action<string> _postMessageJson;
        private readonly BridgeRequestCancellationRegistry _cancellations;
        private readonly string _bridgeToken;
        private readonly object _hostSwitchSync = new object();
        private int _requestsInFlight;
        private bool _hostSwitchPending;
        private bool _disposed;
        private readonly TaskCompletionSource<bool> _requestsDrained =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _sourceWorkDrained =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _resourceChangesSync = new object();
        private readonly Dictionary<string, ResourceChangedMessage> _resourceChanges = new Dictionary<string, ResourceChangedMessage>();
        private readonly Timer _resourceChangesTimer;
        private bool _resourceChangesDisposed;
        public Action<string, string, bool> MailboxNavigationRequested { get; set; }
        public Func<string, Task<OfficeHostChatResponse>> OfficeHostChatRequested { get; set; }
        public Func<string, Task<OfficeHostChatResponse>> OfficeChatSelectionRequested { get; set; }

        public bool HostSwitchPending
        {
            get { lock (_hostSwitchSync) return _hostSwitchPending; }
        }

        public Task RequestsDrained { get { return DrainRequestsAsync(); } }
        private async Task DrainRequestsAsync()
        {
            await _requestsDrained.Task.ConfigureAwait(false);
            await _sourceWorkDrained.Task.ConfigureAwait(false);
            await _controller.InboxWorkersDrained.ConfigureAwait(false);
        }

        public void RebindController(AssistantController controller)
        {
            if (controller == null) throw new ArgumentNullException("controller");
            lock (_hostSwitchSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AssistantWebBridge));
                if (!_hostSwitchPending || _requestsInFlight != 1)
                    throw new InvalidOperationException("Office host switch is not exclusive.");
                _controller.ModelRequestDiagnostics -= ReportModelRequestDiagnostics;
                _controller.ResourceAuthorityChanged -= ReportResourceChanged;
                _controller = controller;
                _controller.ModelRequestDiagnostics += ReportModelRequestDiagnostics;
                _controller.ResourceAuthorityChanged += ReportResourceChanged;
            }
            lock (_resourceChangesSync) _resourceChanges.Clear();
        }

        public AssistantWebBridge(AssistantController controller, Action<string> postMessageJson)
            : this(controller, postMessageJson, new WorkspaceApplicationService(controller.StoragePaths))
        { }

        public AssistantWebBridge(AssistantController controller, Action<string> postMessageJson,
            WorkspaceApplicationService workspaces)
        {
            _controller = controller;
            _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
            _postMessageJson = postMessageJson;
            _cancellations = new BridgeRequestCancellationRegistry();
            _bridgeToken = Guid.NewGuid().ToString("N");
            _resourceChangesTimer = new Timer(ignored => FlushResourceChanges(), null, Timeout.Infinite, Timeout.Infinite);
            _controller.ModelRequestDiagnostics += ReportModelRequestDiagnostics;
            _controller.ResourceAuthorityChanged += ReportResourceChanged;
        }

        public Task<string> HandleMessageAsync(string requestJson)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<BridgeRequest>(requestJson);
                if (request == null) throw new InvalidOperationException("WebView bridge request is missing.");
                return HandleMessageAsync(request);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("WebView bridge request envelope failed.", ex);
                return Task.FromResult(SerializeFailure(null, "WebView transport failed.",
                    ex.Message, "bridge_transport_failed", true));
            }
        }

        internal async Task<string> HandleMessageAsync(BridgeRequest request)
        {
            string id = null;
            CancellationTokenSource cancellationSource = null;
            bool registered = false;
            try
            {
                if (request == null) throw new InvalidOperationException("WebView bridge request is missing.");
                id = request.Id;
                if (request.ContractVersion != BridgeOperationCatalog.Version)
                    return SerializeFailure(id, "WebView bridge contract mismatch. Reload the panel.",
                        "The UI and host must use the same bridge contract.", "bridge_contract_mismatch", false);
                var operation = BridgeOperationCatalog.Resolve(request.Type);
                var type = operation.Id;
                lock (_hostSwitchSync)
                {
                    if (_disposed) throw new OperationCanceledException("Bridge is closing.");
                    var telemetry = type == BridgeOperationId.ReportClientTiming;
                    var needsExclusiveBinding = type == BridgeOperationId.CreateOfficeHostChat ||
                        type == BridgeOperationId.ClearRuntimeData;
                    if (!telemetry && (_hostSwitchPending || (needsExclusiveBinding && _requestsInFlight != 0)))
                        throw new BridgeSwitchBusyException();
                    if (!telemetry)
                    {
                        _requestsInFlight++;
                        registered = true;
                    }
                    if (needsExclusiveBinding) _hostSwitchPending = true;
                }
                var timer = Stopwatch.StartNew();
                var payload = request.Payload ?? JValue.CreateNull();
                if (type != BridgeOperationId.Init && !string.Equals(request.BridgeToken, _bridgeToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Invalid WebView bridge token.");
                }
                object responsePayload;
                if (type == BridgeOperationId.CancelRequest)
                {
                    responsePayload = new CancellationResponse
                    {
                        Cancelled = _cancellations.Cancel(Payload<CancelRequestPayload>(payload).RequestId)
                    };
                    return Success(id, responsePayload);
                }
                if (type == BridgeOperationId.CancelChatRun)
                {
                    var cancelRun = Payload<CancelChatRunPayload>(payload);
                    responsePayload = new CancellationResponse
                    {
                        Cancelled = _controller.CancelChatRun(cancelRun.ChatId, cancelRun.RunId)
                    };
                    return Success(id, responsePayload);
                }

                try { cancellationSource = _cancellations.Create(id, operation); }
                catch (ObjectDisposedException) { throw new OperationCanceledException("Bridge is closing."); }
                var cancellationToken = cancellationSource == null ? CancellationToken.None : cancellationSource.Token;

                switch (type)
                {
                    case BridgeOperationId.GetWorkspaceState:
                        responsePayload = await RunBridgeWorkAsync(_workspaces.ReadState, cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.OpenWorkspace:
                        var workspaceOpen = Payload<OpenWorkspaceRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.OpenWorkspace(workspaceOpen), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CreateTabularProject:
                        var projectCreate = Payload<CreateTabularProjectRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.CreateTabularProject(projectCreate), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.OpenTabularProject:
                        var projectOpen = Payload<WorkspaceProjectPathRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.OpenTabularProject(projectOpen), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ReconcileWorkspaceFile:
                        var workspaceReconcile = Payload<WorkspaceProjectPathRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.ReconcileFile(workspaceReconcile), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.PrepareWorkspaceSources:
                        var sourcePrepare = Payload<PrepareWorkspaceSourcesRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.PrepareSources(sourcePrepare), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BrowseWorkspaceFiles:
                        var browseFiles = Payload<BrowseWorkspaceFilesRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _workspaces.BrowseFiles(browseFiles, cancellationToken), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetWorkspaceSourceQueue:
                        responsePayload = _workspaces.ReadSourceQueue(Payload<WorkspaceSourcePageRequest>(payload));
                        break;
                    case BridgeOperationId.ControlWorkspaceSourceQueue:
                        responsePayload = _workspaces.ControlSourceQueue(Payload<WorkspaceSourceControlRequest>(payload));
                        break;
                    case BridgeOperationId.ReportClientTiming:
                        var clientTiming = Payload<ClientTimingPayload>(payload);
                        if ((string.Equals(clientTiming.Kind, "startup", StringComparison.Ordinal) ||
                                string.Equals(clientTiming.Kind, "chatResponse", StringComparison.Ordinal) ||
                                string.Equals(clientTiming.Kind, "chatNavigation", StringComparison.Ordinal) ||
                                string.Equals(clientTiming.Kind, "skillSource", StringComparison.Ordinal)) &&
                            clientTiming.BridgeMs >= 0 && clientTiming.BridgeMs <= 600000 &&
                            clientTiming.RenderMs >= 0 && clientTiming.RenderMs <= 600000 &&
                            clientTiming.Messages >= 0 && clientTiming.Messages <= 100000 &&
                            clientTiming.BridgeMs + clientTiming.RenderMs >=
                                (string.Equals(clientTiming.Kind, "startup", StringComparison.Ordinal) ? 500 : 250))
                            RuntimeLog.Info("WebView render timing: kind=" + clientTiming.Kind +
                                ", bridge=" + clientTiming.BridgeMs + "ms, render=" +
                                clientTiming.RenderMs + "ms, messages=" + clientTiming.Messages + ".");
                        responsePayload = null;
                        break;
                    case BridgeOperationId.Init:
                        responsePayload = await RunBridgeWorkAsync(
                            () => Bootstrap(_controller.Initialize()),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ListChats:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ListChats(),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetChatState:
                        var getChat = Payload<ChatPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.GetChatDetail(getChat.ChatId),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetPreviousChatMessages:
                        var previousMessages = Payload<ChatMessagePagePayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.GetPreviousChatMessages(
                                previousMessages.ChatId, previousMessages.BeforeIndex),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetChatTrajectory:
                        var trajectoryRequest = Payload<ChatTrajectoryRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.GetChatTrajectory(trajectoryRequest),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ExportChatTrajectory:
                        responsePayload = await _controller.ExportChatTrajectoryAsync(Payload<ChatTrajectoryExportRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetChatEventPayload:
                        var chatEventPayload = Payload<ChatEventPayloadRequest>(payload);
                        responsePayload = await _controller.GetChatEventPayloadAsync(chatEventPayload.ChatId, chatEventPayload.EventId, cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CreateChat:
                        var createChat = Payload<CreateChatPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.CreateChat(createChat.Title), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CreateDocumentChat:
                        var createDocumentChat = Payload<CreateDocumentChatPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _controller.CreateDocumentChat(
                            createDocumentChat.Title,
                            createDocumentChat.Host,
                            createDocumentChat.DocumentKey,
                            createDocumentChat.DocumentTitle,
                            createDocumentChat.DocumentPath), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SelectChat:
                        var selectChat = Payload<ChatPayload>(payload);
                        if (OfficeChatSelectionRequested == null)
                            responsePayload = await RunBridgeWorkAsync(
                                () => _controller.SelectChat(selectChat.ChatId, shell => ReportChatSelection(id, shell)),
                                cancellationToken).ConfigureAwait(false);
                        else
                        {
                            // Selecting a chat on the bound document does not replace the
                            // controller or the document session captured by an active run.
                            var currentDocumentChat = await RunBridgeWorkAsync(
                                () => _controller.TrySelectCurrentDocumentChat(selectChat.ChatId),
                                cancellationToken).ConfigureAwait(false);
                            if (currentDocumentChat != null)
                            {
                                responsePayload = currentDocumentChat;
                                break;
                            }
                            lock (_hostSwitchSync)
                            {
                                if (_disposed) throw new OperationCanceledException("Bridge is closing.");
                                if (_hostSwitchPending || _requestsInFlight != 1)
                                    throw new BridgeSwitchBusyException();
                                _hostSwitchPending = true;
                            }
                            var selectedOfficeChat = await Task.Run(() => OfficeChatSelectionRequested(
                                selectChat.ChatId), cancellationToken).ConfigureAwait(false);
                            if (selectedOfficeChat == null ||
                                !string.Equals(selectedOfficeChat.ChatId, selectChat.ChatId, StringComparison.Ordinal) ||
                                string.IsNullOrWhiteSpace(selectedOfficeChat.Host) ||
                                (selectedOfficeChat.State == null && selectedOfficeChat.Init == null) ||
                                (selectedOfficeChat.State != null &&
                                 !string.Equals(selectedOfficeChat.State.ActiveChatId, selectChat.ChatId, StringComparison.Ordinal)) ||
                                (selectedOfficeChat.Init != null &&
                                 (!string.Equals(selectedOfficeChat.Init.ActiveChatId, selectChat.ChatId, StringComparison.Ordinal) ||
                                  !string.Equals(selectedOfficeChat.Init.Host, selectedOfficeChat.Host, StringComparison.Ordinal))))
                                throw new InvalidOperationException("Office chat selection did not verify the target binding.");
                            responsePayload = selectedOfficeChat;
                        }
                        break;
                    case BridgeOperationId.OpenDocument:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.OpenDocument(Payload<ChatPayload>(payload).ChatId), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ActivateDocument:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ActivateDocument(Payload<DocumentPayload>(payload).DocumentKey), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.AttachOutlookMailbox:
                        var mailbox = Payload<OutlookMailboxNavigationPayload>(payload);
                        if (MailboxNavigationRequested == null || string.IsNullOrWhiteSpace(mailbox.DocumentKey) ||
                            !mailbox.DocumentKey.StartsWith("outlook-mailbox:", StringComparison.Ordinal))
                            throw new InvalidOperationException("Outlook mailbox navigation is unavailable.");
                        MailboxNavigationRequested(mailbox.DocumentKey, mailbox.ChatId, mailbox.CreateNew);
                        responsePayload = new OutlookMailboxNavigationResponse { Scheduled = true };
                        break;
                    case BridgeOperationId.CreateOfficeHostChat:
                        var hostChat = Payload<OfficeHostChatPayload>(payload);
                        if (OfficeHostChatRequested == null || !IsSupportedOfficeHost(hostChat.Host))
                            throw new InvalidOperationException("Office host chat creation is unavailable.");
                        var createdHostChat = await Task.Run(() => OfficeHostChatRequested(hostChat.Host),
                            cancellationToken).ConfigureAwait(false);
                        if (createdHostChat == null || string.IsNullOrWhiteSpace(createdHostChat.ChatId) ||
                            !string.Equals(createdHostChat.Host, hostChat.Host, StringComparison.Ordinal))
                            throw new InvalidOperationException("Office host chat creation returned no verified chat.");
                        if (createdHostChat.State != null &&
                            !string.Equals(createdHostChat.State.ActiveChatId, createdHostChat.ChatId, StringComparison.Ordinal))
                            throw new InvalidOperationException("Office host chat state does not match the created chat.");
                        if (createdHostChat.Init != null &&
                            (!string.Equals(createdHostChat.Init.Host, createdHostChat.Host, StringComparison.Ordinal) ||
                             !string.Equals(createdHostChat.Init.ActiveChatId, createdHostChat.ChatId, StringComparison.Ordinal)))
                            throw new InvalidOperationException("Office host binding does not match the created chat.");
                        responsePayload = createdHostChat;
                        break;
                    case BridgeOperationId.DeleteDocument:
                        var deleteDocument = Payload<DocumentPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.DeleteDocument(deleteDocument.Host, deleteDocument.DocumentKey), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.RenameChat:
                        var renameChat = Payload<RenameChatPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.RenameChat(renameChat.ChatId, renameChat.Title), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SetChatModel:
                        var setChatModel = Payload<SetChatModelPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.SetChatModel(setChatModel.ChatId, setChatModel.Model), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SetChatMode:
                        var setChatMode = Payload<SetChatModePayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.SetChatMode(setChatMode.ChatId, setChatMode.Mode), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SetChatMutationPolicy:
                        var setChatPolicy = Payload<SetChatMutationPolicyPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.SetChatMutationPolicy(setChatPolicy.ChatId, setChatPolicy.MutationPolicy), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SetChatReasoning:
                        var setChatReasoning = Payload<SetChatReasoningPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.SetChatReasoning(setChatReasoning.ChatId, setChatReasoning.Enabled == true), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.AllowHtmlNetworkOrigin:
                        responsePayload = _controller.AllowHtmlNetworkOrigin(Payload<HtmlOriginPayload>(payload).Origin);
                        break;
                    case BridgeOperationId.HtmlFetch:
                        responsePayload = await _controller.HtmlFetchAsync(Payload<HtmlFetchRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.ClearChat:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ClearChat(Payload<ChatPayload>(payload).ChatId), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CompactChatContext:
                        var compactChat = Payload<ChatPayload>(payload);
                        responsePayload = await _controller.CompactChatContextAsync(
                            compactChat.ChatId,
                            (phase, message, activity) => ReportProgress(id, compactChat.ChatId, string.Empty, phase, message, activity),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.DeleteChat:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.DeleteChat(Payload<ChatPayload>(payload).ChatId), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.StopChatInbox:
                        responsePayload = await RunBridgeWorkAsync(() => _controller.StopChatInbox(Payload<ChatPayload>(payload).ChatId), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetChatInbox:
                        var inboxRequest = Payload<ChatInboxRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _controller.GetChatInbox(
                            inboxRequest.ChatId, inboxRequest.ReceiptOperationIds), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SubmitChatInput:
                        var inboxInput = Payload<SubmitChatInputPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _controller.SubmitChatInput(inboxInput,
                            (nextRun, phase, message, activity) => ReportProgress("inbox:" + inboxInput.ChatId, inboxInput.ChatId, nextRun, phase, message, activity),
                            ReportChatState), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.EditChatInput:
                    case BridgeOperationId.RemoveChatInput:
                    case BridgeOperationId.SteerChatInput:
                        var inboxUpdate = Payload<UpdateChatInputPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(() => _controller.UpdateChatInput(inboxUpdate,
                            type == BridgeOperationId.RemoveChatInput ? "remove" : type == BridgeOperationId.SteerChatInput ? "steer" : "edit",
                            (nextRun, phase, message, activity) => ReportProgress("inbox:" + inboxUpdate.ChatId, inboxUpdate.ChatId, nextRun, phase, message, activity),
                            ReportChatState), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ResumeChatInbox:
                        var inboxChat = Payload<ChatPayload>(payload).ChatId;
                        responsePayload = await RunBridgeWorkAsync(() => _controller.ResumeChatInbox(inboxChat,
                            (nextRun, phase, message, activity) => ReportProgress("inbox:" + inboxChat, inboxChat, nextRun, phase, message, activity),
                            ReportChatState), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BeginChatResourceUpload:
                        responsePayload = _controller.BeginChatResourceUpload(Payload<ResourceUploadOpenRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CompleteChatResourceUpload:
                        responsePayload = await _controller.CompleteChatResourceUploadAsync(Payload<ResourceUploadLeaseRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CancelChatResourceUpload:
                        responsePayload = _controller.CancelChatResourceUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.DiscardChatResourceDraft:
                        var discardResource = Payload<DiscardChatResourceDraftPayload>(payload);
                        responsePayload = _controller.DiscardChatResourceDraft(discardResource.ChatId, discardResource.Id);
                        break;
                    case BridgeOperationId.DeleteMessage:
                        var deleteMessage = Payload<MessageActionPayload>(payload);
                        responsePayload = _controller.DeleteMessage(deleteMessage.Id, deleteMessage.Index ?? -1, deleteMessage.ChatId);
                        break;
                    case BridgeOperationId.ForkChat:
                        var forkChat = Payload<MessageActionPayload>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ForkChat(forkChat.Id, forkChat.Index ?? -1, forkChat.ChatId), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.EditMessage:
                        var editMessage = Payload<EditMessagePayload>(payload);
                        var editRunId = Guid.NewGuid().ToString("N");
                        responsePayload = await _controller.EditMessageAsync(
                            editMessage.Text,
                            editMessage.Id,
                            editMessage.Index ?? -1,
                            editMessage.ChatId,
                            (phase, message, activity) => ReportProgress(id, editMessage.ChatId, editRunId, phase, message, activity),
                            ReportChatState,
                            cancellationToken,
                            editRunId);
                        break;
                    case BridgeOperationId.UpdateMessageActivityData:
                        var updateActivityData = Payload<UpdateMessageActivityDataPayload>(payload);
                        responsePayload = _controller.UpdateMessageActivityData(
                            updateActivityData.MessageId,
                            updateActivityData.DataJson,
                            updateActivityData.ChatId);
                        break;
                    case BridgeOperationId.GetSettings:
                        responsePayload = _controller.GetSettings();
                        break;
                    case BridgeOperationId.GetCasHealth:
                        responsePayload = _controller.GetCasHealth();
                        break;
                    case BridgeOperationId.CollectCasGarbage:
                        responsePayload = _controller.CollectCasGarbage();
                        break;
                    case BridgeOperationId.GetModelCatalog:
                        var modelCatalog = Payload<ModelCatalogPayload>(payload);
                        responsePayload = await Task.Run(() => _controller.GetModelCatalogAsync(
                            modelCatalog.Settings, modelCatalog.ApiKey, cancellationToken), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SaveSettings:
                        responsePayload = _controller.SaveSettings(payload.ToObject<SaveSettingsPayload>(JsonSerializer.Create(
                            new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case BridgeOperationId.ReadPromptSource:
                        responsePayload = await _controller.ReadPromptSourceAsync(Payload<PromptSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BeginPromptMutationUpload:
                        responsePayload = _controller.BeginPromptMutationUpload(Payload<PromptMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CancelPromptMutationUpload:
                        responsePayload = _controller.CancelPromptMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.TestModelCompatibility:
                        responsePayload = await _controller.TestModelCompatibilityAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.TestModelConnection:
                        responsePayload = await _controller.TestModelConnectionAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ClearRuntimeData:
                        _workspaces.EnsureNoSourceWork();
                        responsePayload = Bootstrap(_controller.ClearRuntimeData());
                        _workspaces.Reset();
                        break;
                    case BridgeOperationId.GetTools:
                        responsePayload = _controller.GetTools();
                        break;
                    case BridgeOperationId.GetToolDocumentation:
                        responsePayload = await _controller.GetToolDocumentationAsync(
                            Payload<ToolLibraryDocumentationRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SaveTools:
                        responsePayload = await _controller.SaveToolsAsync(Payload<ToolMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ReadToolSource:
                        responsePayload = await _controller.ReadToolSourceAsync(Payload<ToolSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BeginToolMutationUpload:
                        responsePayload = _controller.BeginToolMutationUpload(Payload<ToolMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CancelToolMutationUpload:
                        responsePayload = _controller.CancelToolMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.InstallVbaTool:
                        var installVbaTool = Payload<VbaToolPackagePayload>(payload);
                        responsePayload = _controller.InstallVbaTool(installVbaTool.Id, installVbaTool.DryRun);
                        break;
                    case BridgeOperationId.UninstallVbaTool:
                        responsePayload = _controller.UninstallVbaTool(Payload<VbaToolPackagePayload>(payload).Id);
                        break;
                    case BridgeOperationId.GetSkills:
                        responsePayload = _controller.GetSkills();
                        break;
                    case BridgeOperationId.SaveSkills:
                        responsePayload = await _controller.SaveSkillsAsync(Payload<SkillMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BeginSkillMutationUpload:
                        responsePayload = _controller.BeginSkillMutationUpload(Payload<SkillMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CancelSkillMutationUpload:
                        responsePayload = _controller.CancelSkillMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.ReadSkillSource:
                        responsePayload = await _controller.ReadSkillSourceAsync(Payload<SkillSourceReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SaveSkillReference:
                        responsePayload = await _controller.SaveSkillReferenceAsync(Payload<SkillMutationWriteRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.DeleteSkillReference:
                        var deleteSkillReference = Payload<SkillReferencePayload>(payload);
                        responsePayload = _controller.DeleteSkillReference(
                            deleteSkillReference);
                        break;
                    case BridgeOperationId.RunTool:
                        var runTool = Payload<RunToolPayload>(payload);
                        var runArguments = ToArguments(runTool.Arguments);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.RunTool(
                                runTool.ToolId,
                                runArguments,
                                runTool.DryRun,
                                runTool.ChatId,
                                (phase, message) => ReportProgress(id, phase, message),
                                cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ConfirmAgentTool:
                        var confirmAgentTool = Payload<PendingAgentToolPayload>(payload);
                        var confirmRunId = Guid.NewGuid().ToString("N");
                        responsePayload = await Task.Run(() => _controller.ConfirmAgentToolAsync(
                                confirmAgentTool.PendingId,
                                confirmAgentTool.ChatId,
                                (phase, message, activity) => ReportProgress(id, confirmAgentTool.ChatId, confirmRunId, phase, message, activity),
                                cancellationToken,
                                confirmRunId,
                                ReportChatState),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CancelAgentTool:
                        var cancelAgentTool = Payload<PendingAgentToolPayload>(payload);
                        responsePayload = _controller.CancelAgentTool(cancelAgentTool.PendingId, cancelAgentTool.ChatId);
                        break;
                    case BridgeOperationId.GetVbaProject:
                        responsePayload = _controller.GetVbaProject();
                        break;
                    case BridgeOperationId.GetVbaModule:
                        responsePayload = await _controller.GetVbaModuleAsync(Payload<VbaEditorReadRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetVbaMutations:
                        responsePayload = _controller.GetVbaMutations(Payload<VbaMutationQueryPayload>(payload));
                        break;
                    case BridgeOperationId.GetToolResultPresentation:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ReadToolResultPresentation(Payload<ToolResultPresentationRequest>(payload)),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetRunChanges:
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.ReadRunChanges(Payload<RunChangesRequest>(payload)),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetVbaMutationDetail:
                        responsePayload = _controller.GetVbaMutationDetail(
                            Payload<VbaMutationDetailPayload>(payload).MutationId);
                        break;
                    case BridgeOperationId.SaveVbaModule:
                        responsePayload = await _controller.SaveVbaModuleAsync(Payload<VbaModulePayload>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.CreateVbaModule:
                        responsePayload = await _controller.CreateVbaModuleAsync(Payload<VbaCreateModulePayload>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.BeginVbaModuleUpload:
                        responsePayload = _controller.BeginVbaModuleUpload(Payload<VbaEditorUploadRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CancelVbaModuleUpload:
                        responsePayload = _controller.CancelVbaModuleUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.DeleteVbaModule:
                        var deleteVbaModule = Payload<VbaDeleteModulePayload>(payload);
                        responsePayload = _controller.DeleteVbaModule(deleteVbaModule.ModuleName);
                        break;
                    case BridgeOperationId.RestoreVbaBackup:
                        var restoreVba = Payload<RestoreVbaBackupPayload>(payload);
                        responsePayload = _controller.RestoreVbaBackup(restoreVba.BackupId, restoreVba.ModuleName);
                        break;
                    case BridgeOperationId.RunVbaMacro:
                        responsePayload = _controller.RunVbaMacro(
                            Payload<RunVbaMacroPayload>(payload).MacroName,
                            cancellationToken);
                        break;
                    case BridgeOperationId.ResourceDataOpen:
                        responsePayload = _controller.OpenResourceData(Payload<ResourceDataOpenRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.ResourceDataClose:
                        responsePayload = _controller.CloseResourceData(Payload<ResourceDataCloseRequest>(payload));
                        break;
                    case BridgeOperationId.GetHtmlWorkspace:
                        responsePayload = _controller.GetHtmlWorkspace(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case BridgeOperationId.ReadHtmlWorkspaceSource:
                        responsePayload = await _controller.ReadHtmlWorkspaceSourceAsync(Payload<HtmlWorkspaceSourceRequest>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.SaveHtmlWorkspaceFile:
                        responsePayload = _controller.SaveHtmlWorkspaceFile(
                            payload.ToObject<HtmlWorkspaceFilePayload>(JsonSerializer.Create(
                                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case BridgeOperationId.SaveHtmlWorkspaceData:
                        responsePayload = _controller.SaveHtmlWorkspaceData(
                            payload.ToObject<HtmlWorkspaceDataPayload>(JsonSerializer.Create(
                                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })), cancellationToken);
                        break;
                    case BridgeOperationId.BeginHtmlWorkspaceMutationUpload:
                        responsePayload = _controller.BeginHtmlWorkspaceMutationUpload(Payload<HtmlWorkspaceMutationUploadRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CancelHtmlWorkspaceMutationUpload:
                        responsePayload = _controller.CancelHtmlWorkspaceMutationUpload(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.ListArtifactCatalog:
                        responsePayload = _controller.ListArtifactCatalog(Payload<ArtifactCatalogRequest>(payload));
                        break;
                    case BridgeOperationId.GetArtifactLibraryPage:
                        var libraryPage = Payload<ArtifactLibraryPageRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.GetArtifactLibraryPage(libraryPage), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetArtifactLibraryHistory:
                        var libraryHistory = Payload<ArtifactLibraryHistoryRequest>(payload);
                        responsePayload = await RunBridgeWorkAsync(
                            () => _controller.GetArtifactLibraryHistory(libraryHistory), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.ExportArtifact:
                        responsePayload = _controller.ExportArtifact(Payload<ArtifactTransferRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CopyArtifact:
                        responsePayload = _controller.CopyArtifact(Payload<ArtifactTransferRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.BeginArtifactImport:
                        responsePayload = _controller.BeginArtifactImport(Payload<ResourceUploadOpenRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.ImportArtifact:
                        responsePayload = _controller.ImportArtifact(Payload<ArtifactTransferRequest>(payload), cancellationToken);
                        break;
                    case BridgeOperationId.CloseArtifactTransfer:
                        responsePayload = _controller.CloseArtifactTransfer(Payload<ResourceUploadLeaseRequest>(payload));
                        break;
                    case BridgeOperationId.ChangeArtifactLink:
                        responsePayload = _controller.ChangeArtifactLink(Payload<ArtifactLinkChangeRequest>(payload));
                        break;
                    case BridgeOperationId.DeleteDocumentArtifact:
                        responsePayload = _controller.DeleteDocumentArtifact(Payload<ArtifactDeleteRequest>(payload));
                        break;
                    case BridgeOperationId.ReadArtifactViewerPage:
                        var artifactViewerPage = Payload<ArtifactViewerPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactViewerPage(
                            artifactViewerPage.ChatId,
                            artifactViewerPage.ResourceUri,
                            artifactViewerPage.Cursor, cancellationToken);
                        break;
                    case BridgeOperationId.ReadArtifactImage:
                        var artifactImage = Payload<ArtifactImageViewerPayload>(payload);
                        responsePayload = _controller.ReadArtifactImage(
                            artifactImage.ChatId,
                            artifactImage.ResourceUri, cancellationToken);
                        break;
                    case BridgeOperationId.ReadArtifactImageThumbnail:
                        var artifactImageThumbnail = Payload<ArtifactImageThumbnailPayload>(payload);
                        responsePayload = _controller.ReadArtifactImageThumbnail(
                            artifactImageThumbnail.ChatId,
                            artifactImageThumbnail.ResourceUri, cancellationToken);
                        break;
                    case BridgeOperationId.ReadArtifactPdfInfo:
                        var artifactPdf = Payload<ArtifactPdfViewerPayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfInfo(
                            artifactPdf.ChatId,
                            artifactPdf.ResourceUri);
                        break;
                    case BridgeOperationId.ReadArtifactPdfPage:
                        var artifactPdfPage = Payload<ArtifactPdfPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfPage(
                            artifactPdfPage.ChatId,
                            artifactPdfPage.ResourceUri,
                            artifactPdfPage.PageIndex, cancellationToken);
                        break;
                    case BridgeOperationId.ReadArtifactPdfThumbnail:
                        var artifactPdfThumbnail = Payload<ArtifactPdfPagePayload>(payload);
                        responsePayload = _controller.ReadArtifactPdfThumbnail(
                            artifactPdfThumbnail.ChatId,
                            artifactPdfThumbnail.ResourceUri,
                            artifactPdfThumbnail.PageIndex, cancellationToken);
                        break;
                    case BridgeOperationId.ImportUploadedHtmlToWorkspace:
                        var htmlImport = Payload<HtmlWorkspaceImportPayload>(payload);
                        responsePayload = _controller.ImportUploadedHtmlToWorkspace(htmlImport);
                        break;
                    case BridgeOperationId.PrepareHtmlWorkspaceExport:
                        var htmlExport = Payload<HtmlWorkspaceExportPayload>(payload);
                        responsePayload = _controller.PrepareHtmlWorkspaceExport(htmlExport, cancellationToken);
                        break;
                    case BridgeOperationId.DeleteHtmlWorkspaceFile:
                        var deleteHtmlFile = Payload<HtmlWorkspaceDeleteFilePayload>(payload);
                        responsePayload = _controller.DeleteHtmlWorkspaceFile(deleteHtmlFile);
                        break;
                    case BridgeOperationId.DeleteHtmlWorkspaceData:
                        var deleteHtmlData = Payload<HtmlWorkspaceDeleteDataPayload>(payload);
                        responsePayload = _controller.DeleteHtmlWorkspaceData(deleteHtmlData);
                        break;
                    case BridgeOperationId.SetActiveHtmlWorkspaceFile:
                        var htmlActive = Payload<HtmlWorkspaceActiveFilePayload>(payload);
                        responsePayload = _controller.SetActiveHtmlWorkspaceFile(htmlActive);
                        break;
                    case BridgeOperationId.RestoreHtmlWorkspaceSnapshot:
                        var htmlRestore = Payload<HtmlWorkspaceRestorePayload>(payload);
                        responsePayload = _controller.RestoreHtmlWorkspaceSnapshot(htmlRestore);
                        break;
                    case BridgeOperationId.RedoHtmlWorkspaceSnapshot:
                        var htmlRedo = Payload<HtmlWorkspaceRestorePayload>(payload);
                        responsePayload = _controller.RedoHtmlWorkspaceSnapshot(htmlRedo);
                        break;
                    case BridgeOperationId.GetContext:
                        responsePayload = _controller.GetContext(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case BridgeOperationId.GetModelContext:
                        responsePayload = await _controller.GetModelContextAsync(Payload<ModelContextQuery>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.GetModelContextPayload:
                        responsePayload = await _controller.GetModelContextPayloadAsync(Payload<ModelContextPayloadQuery>(payload), cancellationToken).ConfigureAwait(false);
                        break;
                    case BridgeOperationId.InspectModelContextPreview:
                        var modelPreview = Payload<PromptContextInspectorPayload>(payload);
                        responsePayload = _controller.InspectPromptContext(modelPreview.ChatId, modelPreview.Text,
                            modelPreview.ResourceDraftIds, true, true);
                        break;
                    case BridgeOperationId.InspectPromptContext:
                        var inspectContext = Payload<PromptContextInspectorPayload>(payload);
                        responsePayload = _controller.InspectPromptContext(
                            inspectContext.ChatId,
                            inspectContext.Text,
                            inspectContext.ResourceDraftIds,
                            inspectContext.IncludeRaw);
                        break;
                    case BridgeOperationId.GetAgentArsenal:
                        responsePayload = _controller.GetAgentArsenal(Payload<AgentArsenalPayload>(payload));
                        break;
                    case BridgeOperationId.StageSelectionInput:
                        var stagedSelection = Payload<SelectionContextPayload>(payload);
                        responsePayload = _controller.StageSelectionInput(stagedSelection.Mode, stagedSelection.ChatId);
                        break;
                    case BridgeOperationId.AddSelectionContext:
                        var selectionContext = Payload<SelectionContextPayload>(payload);
                        responsePayload = _controller.AddSelectionContextFromBridge(selectionContext.Mode, selectionContext.ChatId);
                        break;
                    case BridgeOperationId.AddTextContext:
                        var textContext = Payload<TextContextPayload>(payload);
                        responsePayload = _controller.AddTextContext(
                            textContext.Role,
                            textContext.Kind,
                            textContext.Title,
                            textContext.Reference,
                            textContext.Text,
                            textContext.DetailsJson,
                            textContext.ChatId);
                        break;
                    case BridgeOperationId.RemoveContextItem:
                        var removeContextItem = Payload<RemoveContextItemPayload>(payload);
                        responsePayload = _controller.RemoveContextItem(removeContextItem.Id, removeContextItem.ChatId);
                        break;
                    case BridgeOperationId.ClearContext:
                        responsePayload = _controller.ClearContext(Payload<ChatPayload>(payload).ChatId);
                        break;
                    case BridgeOperationId.QuickAction:
                        responsePayload = await _controller.RunQuickActionAsync(Payload<QuickActionPayload>(payload).Action);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown bridge message: " + type);
                }

                var handleMs = timer.ElapsedMilliseconds;
                var serialized = Success(id, responsePayload);
                var serializeMs = timer.ElapsedMilliseconds - handleMs;
                if ((type == BridgeOperationId.Init &&
                        handleMs + serializeMs >= 500) ||
                    ((handleMs + serializeMs >= 250) && (type == BridgeOperationId.SubmitChatInput ||
                        type == BridgeOperationId.GetChatState ||
                        type == BridgeOperationId.SelectChat ||
                        type == BridgeOperationId.ListChats)))
                    RuntimeLog.Info("Bridge response timing: type=" + type + ", handle=" + handleMs +
                        "ms, serialize=" + serializeMs + "ms, chars=" + serialized.Length + ".");
                return serialized;
            }
            catch (OperationCanceledException ex)
            {
                return SerializeFailure(id, "Request cancelled.",
                    string.IsNullOrWhiteSpace(ex.Message) ? "Request cancelled." : ex.Message,
                    "bridge_request_cancelled", false, true);
            }
            catch (BridgeSwitchBusyException ex)
            {
                return SerializeFailure(id, ex.Message, ex.Message, "bridge_switch_busy", false);
            }
            catch (WorkspaceApplicationException ex)
            { return SerializeFailure(id, ex.Message, ex.Message, ex.Code, false); }
            catch (WorkspaceFileException ex)
            { return SerializeFailure(id, ex.Message, ex.Message, ex.Code, false); }
            catch (TabularProjectException ex)
            { return SerializeFailure(id, ex.Message, ex.Message, ex.Code, false); }
            catch (Exception ex)
            {
                RuntimeLog.Error("WebView bridge request failed.", ex);
                return SerializeFailure(id, ex.Message,
                    "Request failed. See the local runtime log for details.",
                    "bridge_request_failed", false);
            }
            finally
            {
                _cancellations.Release(id, cancellationSource);
                if (registered)
                {
                    lock (_hostSwitchSync)
                    {
                        _requestsInFlight--;
                        if (_requestsInFlight == 0) _hostSwitchPending = false;
                        if (_disposed && _requestsInFlight == 0) _requestsDrained.TrySetResult(true);
                    }
                }
            }
        }

        private static bool IsSupportedOfficeHost(string host)
        {
            return string.Equals(host, "Excel", StringComparison.Ordinal) ||
                string.Equals(host, "Word", StringComparison.Ordinal) ||
                string.Equals(host, "PowerPoint", StringComparison.Ordinal) ||
                string.Equals(host, "Outlook", StringComparison.Ordinal);
        }

        private BridgeBootstrapResponse Bootstrap(InitResponse response)
        {
            if (response == null) throw new InvalidOperationException("Application initialization returned no state.");
            response.OfficeHostChatAvailable = OfficeHostChatRequested != null;
            return new BridgeBootstrapResponse { BridgeToken = _bridgeToken, Init = response };
        }

        public void Dispose()
        {
            lock (_hostSwitchSync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_requestsInFlight == 0) _requestsDrained.TrySetResult(true);
            }
            _controller.StopInboxWorkers();
            _controller.ModelRequestDiagnostics -= ReportModelRequestDiagnostics;
            _controller.ResourceAuthorityChanged -= ReportResourceChanged;
            lock (_resourceChangesSync)
            {
                _resourceChangesDisposed = true;
                _resourceChanges.Clear();
                _resourceChangesTimer.Dispose();
            }
            _cancellations.Dispose();
            _ = DrainWorkspaceSourcesAsync();
        }

        private async Task DrainWorkspaceSourcesAsync()
        {
            try { await Task.Run(_workspaces.CloseAsync).ConfigureAwait(false); _sourceWorkDrained.TrySetResult(true); }
            catch (Exception error) { _sourceWorkDrained.TrySetException(error); }
        }

        private sealed class BridgeSwitchBusyException : InvalidOperationException
        {
            public BridgeSwitchBusyException()
                : base("Дождитесь завершения текущего запроса и повторите переключение.")
            {
            }
        }

        private static string Success(string id, object payload)
        {
            return Serialize(new BridgeResponse
            {
                Id = id,
                Ok = true,
                Payload = payload ?? JValue.CreateNull()
            });
        }

        private static string Serialize(BridgeResponse response)
        {
            return JsonConvert.SerializeObject(response);
        }

        internal static string SerializeFailure(string id, string error, string detail,
            string errorCode, bool transportFailure, bool cancelled = false)
        {
            return Serialize(new BridgeResponse
            {
                Id = id,
                Ok = false,
                Error = error,
                ErrorDetail = detail,
                ErrorCode = errorCode,
                TransportFailure = transportFailure ? (bool?)true : null,
                Cancelled = cancelled ? (bool?)true : null
            });
        }

        private static Task<T> RunBridgeWorkAsync<T>(Func<T> work, CancellationToken cancellationToken)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(work, cancellationToken);
        }

        private void ReportProgress(string id, string phase, string message)
        {
            ReportProgress(id, null, null, phase, message, null);
        }

        private void ReportProgress(string id, string phase, string message, ChatActivity activity)
        {
            ReportProgress(id, null, activity == null ? null : activity.RunId, phase, message, activity);
        }

        private void ReportProgress(string id, string chatId, string runId, string phase, string message, ChatActivity activity)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            var streaming = string.Equals(phase, "streaming", StringComparison.OrdinalIgnoreCase);
            var streamReset = streaming && string.IsNullOrEmpty(message);
            var reasoning = activity != null && string.Equals(activity.Kind, "reasoning", StringComparison.OrdinalIgnoreCase);
            _postMessageJson(JsonConvert.SerializeObject(new ProgressMessage
            {
                Type = "progress",
                Id = id,
                Payload = new ProgressPayload
                {
                    ChatId = chatId,
                    RunId = runId,
                    Phase = phase,
                    Message = streaming ? null : message,
                    Activity = reasoning ? null : activity,
                    ContentDelta = streaming && !streamReset ? message : null,
                    ContentReset = streamReset ? (bool?)true : null,
                    ReasoningReset = streamReset ? (bool?)true : null,
                    ReasoningDelta = reasoning ? activity.ResultMessage : null,
                    ReasoningComplete = reasoning
                        ? (bool?)(string.Equals(activity.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        : null
                }
            }));
        }

        private void ReportResourceChanged(object sender, ResourceAuthorityChangedEventArgs change)
        {
            if (_postMessageJson == null || change == null) return;
            ResourceChangedMessage[] overflow = null;
            lock (_resourceChangesSync)
            {
                if (_resourceChangesDisposed) return;
                var scope = change.ScopeId.ToString();
                ResourceChangedMessage pending;
                if (!_resourceChanges.TryGetValue(scope, out pending))
                {
                    if (_resourceChanges.Count >= 16) { overflow = _resourceChanges.Values.ToArray(); _resourceChanges.Clear(); }
                    pending = new ResourceChangedMessage { Scope = scope, Resources = new string[0] };
                    if (_resourceChanges.Count == 0) _resourceChangesTimer.Change(100, Timeout.Infinite);
                    _resourceChanges.Add(scope, pending);
                }
                var resources = pending.Resources.Concat(change.AffectedResources.Select(item => item.Uri)).Distinct().Take(65).ToArray();
                pending.AllInScope |= resources.Length > 64;
                pending.Resources = pending.AllInScope ? new string[0] : resources;
                if (change.Generation >= pending.Generation) { pending.Generation = change.Generation; pending.CommitId = change.CommitId; }
            }
            if (overflow != null) PostResourceChanges(overflow);
        }

        private void FlushResourceChanges()
        {
            ResourceChangedMessage[] messages;
            lock (_resourceChangesSync)
            {
                if (_resourceChangesDisposed) return;
                messages = _resourceChanges.Values.ToArray(); _resourceChanges.Clear();
            }
            PostResourceChanges(messages);
        }

        private void PostResourceChanges(IEnumerable<ResourceChangedMessage> messages)
        {
            foreach (var message in messages)
                try { _postMessageJson(JsonConvert.SerializeObject(message)); }
                catch { /* Advisory only; fresh requests still capture shared authority. */ }
        }

        private void ReportModelRequestDiagnostics(LlmRequestDiagnosticUpdate update)
        {
            if (_postMessageJson == null || update == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new ModelRequestDiagnosticsMessage
            {
                Type = "modelDiagnostics",
                Payload = ModelRequestDiagnosticsDto.From(update)
            }));
        }

        private void ReportChatState(ChatStateResponse state)
        {
            if (_postMessageJson == null)
            {
                return;
            }

            _postMessageJson(JsonConvert.SerializeObject(new ChatStateMessage
            {
                Type = "chatState",
                Scope = state != null && state.Messages != null ? "full" : "catalog",
                Payload = state
            }));
        }

        private void ReportChatSelection(string requestId, ChatStateResponse shell)
        {
            if (_postMessageJson == null || shell == null) return;
            _postMessageJson(JsonConvert.SerializeObject(new ChatStateMessage
            {
                Type = "chatState", Id = requestId, Scope = "selection", Payload = shell
            }));
        }

        private static T Payload<T>(JToken payload) where T : class, new()
        {
            return payload == null ? new T() : (payload.ToObject<T>() ?? new T());
        }

        private static Dictionary<string, object> ToArguments(IDictionary<string, object> arguments)
        {
            return ToolArgumentNormalizer.NormalizeDictionary(arguments);
        }
    }
}
