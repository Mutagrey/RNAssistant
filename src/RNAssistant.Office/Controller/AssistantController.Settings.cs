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
    }
}
