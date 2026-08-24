using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public SettingsResponse GetSettings()
        {
            return new SettingsResponse
            {
                AppVersion = ApplicationVersionService.Current,
                Settings = _settingsService.Load(),
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey())
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

        public SettingsResponse SaveSettings(AppSettings settings, string apiKey)
        {
            _settingsService.Save(settings);
            if (apiKey != null)
            {
                _settingsService.SaveApiKey(apiKey);
            }

            return GetSettings();
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
