using System;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public event Action<AppSettings> SettingsChanged;

        public SettingsResponse GetSettings()
        {
            return new SettingsResponse
            {
                AppVersion = ApplicationVersionService.Current,
                Settings = SettingsControlsDto.From(_settingsService.Load()),
                Prompts = _toolExecutor.GetPromptLibrary(),
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                HasHistorySecret = !string.IsNullOrWhiteSpace(_settingsService.LoadHistorySecret())
            };
        }

        public async Task<ModelCatalogResponse> GetModelCatalogAsync(AppSettings settings, string apiKey)
        {
            settings = settings ?? _settingsService.Load();
            var configUrl = LlmClient.BuildModelsConfigUrl(settings);
            var json = await _llmClient.GetModelsConfigJsonAsync(
                settings,
                string.IsNullOrWhiteSpace(apiKey) ? null : apiKey).ConfigureAwait(false);
            var catalog = ModelCapabilityService.ParseCatalog(json, configUrl);
            var storedSettings = _settingsService.Load();
            if (ModelCapabilityService.Merge(storedSettings, catalog))
            {
                _settingsService.Save(storedSettings);
            }

            return new ModelCatalogResponse
            {
                ConfigUrl = configUrl,
                Catalog = catalog
            };
        }

        public Task<PromptSourceReadResponse> ReadPromptSourceAsync(PromptSourceReadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            var session = LoadAddressedSession(request.ChatId);
            var source = new ChatSession { Id = session.Id, Host = session.Host, DocumentKey = session.DocumentKey,
                DocumentAuthorityId = session.DocumentAuthorityId };
            return Task.Run(() => new PromptEditorResourceService(_toolExecutor.ResourceGateway, _resourceData)
                .Open(source, request, token), token);
        }

        public ResourceUploadOpenResponse BeginPromptMutationUpload(PromptMutationUploadRequest request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            return WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                new PromptEditorResourceService(_toolExecutor.ResourceGateway, _resourceData).BeginUpload(session, request, token));
        }

        public ResourceDataCloseResponse CancelPromptMutationUpload(ResourceUploadLeaseRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId))
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: an explicit chat is required.");
            _resourceData.CloseUpload(request.ChatId, request.LeaseId, PromptEditorResourceService.Owner);
            return new ResourceDataCloseResponse { Closed = true };
        }

        public SettingsResponse SaveSettings(SaveSettingsPayload request, CancellationToken token)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatId) || request.Settings == null)
                throw new InvalidOperationException("RESOURCE_ACCESS_DENIED: addressed settings controls are required.");
            try
            {
                using (_chatRuns.ReserveMaintenance())
                {
                    EnsureNoActiveRuns();
                    WithReservedSession(LoadAddressedSession(request.ChatId), session =>
                    {
                        var changes = new PromptEditorResourceService(_toolExecutor.ResourceGateway, _resourceData).ReadMutation(session, request, token);
                        token.ThrowIfCancellationRequested();
                        _toolExecutor.SaveSettingsControls(_settingsService.Load(), request, changes,
                            intended => _settingsService.Save(intended, request.ApiKey, request.HistorySecret, request.ReviewAgentPrompts));
                        return true;
                    });
                }
            }
            finally
            {
                if (request.UploadLeaseId != null) _resourceData.CloseUpload(request.ChatId, request.UploadLeaseId, PromptEditorResourceService.Owner);
            }
            var response = GetSettings();
            var settingsChanged = SettingsChanged;
            if (settingsChanged != null)
            {
                try
                {
                    settingsChanged(_settingsService.Load());
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Settings change notification failed.", ex);
                }
            }
            return response;
        }

        private void PersistTokenEstimateCalibration(AppSettings source)
        {
            if (source == null || !source.AutoCalibrateTokenEstimate)
            {
                return;
            }
            try
            {
                lock (_syncRoot)
                {
                    var stored = _settingsService.Load();
                    if (TokenEstimateCalibration.MergeModel(stored, source, source.Model))
                    {
                        _settingsService.Save(stored);
                    }
                }
            }
            catch
            {
                // Calibration is best-effort and must never fail a chat turn.
            }
        }
    }
}
