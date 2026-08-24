using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ModelCapabilityService
    {
        public static JToken ParseCatalog(string json, string endpoint)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw InvalidCatalog(endpoint, "response body is empty", string.Empty, null);
            }

            JToken catalog;
            try
            {
                catalog = JToken.Parse(json.TrimStart('\uFEFF'));
            }
            catch (JsonException ex)
            {
                throw InvalidCatalog(endpoint, "response is not JSON", json, ex);
            }

            if (catalog.Type != JTokenType.Object && catalog.Type != JTokenType.Array)
            {
                throw InvalidCatalog(endpoint, "JSON root must be an object or array", json, null);
            }
            if (!ContainsModelArray(catalog))
            {
                throw InvalidCatalog(endpoint, "JSON does not contain a supported model array", json, null);
            }
            return catalog;
        }

        private static bool ContainsModelArray(JToken catalog)
        {
            if (catalog is JArray)
            {
                return true;
            }

            var root = catalog as JObject;
            if (root == null)
            {
                return false;
            }
            var source = root["catalog"] ?? root["Catalog"] ?? root;
            if (source is JArray)
            {
                return true;
            }
            var sourceObject = source as JObject;
            return sourceObject != null &&
                (sourceObject["models"] is JArray ||
                 sourceObject["Models"] is JArray ||
                 sourceObject["data"] is JArray ||
                 sourceObject["Data"] is JArray);
        }

        public static bool Merge(AppSettings settings, JToken catalog)
        {
            if (settings == null || catalog == null)
            {
                return false;
            }
            if (settings.ModelCapabilities == null)
            {
                settings.ModelCapabilities = new Dictionary<string, ModelCapabilitySettings>(StringComparer.OrdinalIgnoreCase);
            }
            if (settings.AttachmentModelPriority == null)
            {
                settings.AttachmentModelPriority = new List<string>();
            }
            var changed = false;
            var root = catalog as JObject;
            var source = root == null
                ? catalog
                : (root["catalog"] ?? root["Catalog"] ?? catalog);
            var sourceObject = source as JObject;
            var models = source as JArray;
            if (models == null && sourceObject != null)
            {
                models = sourceObject["models"] as JArray ??
                    sourceObject["Models"] as JArray ??
                    sourceObject["data"] as JArray ??
                    sourceObject["Data"] as JArray;
            }
            if (models == null)
            {
                return false;
            }
            foreach (var model in models.Children<JObject>())
            {
                var value = ReadString(model, "id", "Id", "Value", "value");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                var capability = new ModelCapabilitySettings
                {
                    MaxContextTokens = ReadNullableInt(
                        model,
                        "MaxContextTokens", "max_context_tokens", "maxContextTokens",
                        "context_window", "contextWindow", "context_length", "contextLength", "max_sequence_length"),
                    MaxOutputTokens = ReadNullableInt(
                        model,
                        "MaxOutputTokens", "max_output_tokens", "maxOutputTokens",
                        "max_completion_tokens", "maxCompletionTokens", "output_token_limit", "max_generation_tokens"),
                    SupportsImages = ReadNullableBool(model, "SupportsImages", "supports_images", "supportsImages", "supports_vision", "supportsVision"),
                    SupportsReasoning = ReadNullableBool(model, "SupportsReasoning", "supports_reasoning", "supportsReasoning"),
                    SupportsAudio = ReadNullableBool(model, "SupportsAudio", "supports_audio", "supportsAudio"),
                    MaxImagesPerPrompt = ReadNullableInt(model, "MaxImagesPerPrompt", "max_images_per_prompt", "maxImagesPerPrompt"),
                    ReasoningRequestMode = ReasoningRequestModes.NormalizeOverride(ReadString(
                        model,
                        "ReasoningRequestMode", "reasoning_request_mode", "reasoningRequestMode", "reasoning_transport"))
                };
                if (!capability.SupportsImages.HasValue)
                {
                    var modalities = model["InputModalities"] ?? model["input_modalities"] ?? model["inputModalities"];
                    if (modalities != null)
                    {
                        capability.SupportsImages = false;
                        foreach (var modality in modalities.Values<string>())
                        {
                            if (string.Equals(modality, "image", StringComparison.OrdinalIgnoreCase))
                            {
                                capability.SupportsImages = true;
                                break;
                            }
                        }
                    }
                }
                if (!capability.SupportsAudio.HasValue)
                {
                    var modalities = model["InputModalities"] ?? model["input_modalities"] ?? model["inputModalities"];
                    if (modalities != null)
                    {
                        capability.SupportsAudio = false;
                        foreach (var modality in modalities.Values<string>())
                        {
                            if (string.Equals(modality, "audio", StringComparison.OrdinalIgnoreCase))
                            {
                                capability.SupportsAudio = true;
                                break;
                            }
                        }
                    }
                }
                ModelCapabilitySettings storedCapability;
                if (!settings.ModelCapabilities.TryGetValue(value, out storedCapability) || storedCapability == null)
                {
                    settings.ModelCapabilities[value] = capability;
                    storedCapability = capability;
                    changed = true;
                }
                else if (MergeMissing(storedCapability, capability))
                {
                    changed = true;
                }
                if ((storedCapability.SupportsImages == true || storedCapability.SupportsAudio == true) &&
                    !settings.AttachmentModelPriority.Exists(item =>
                        string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.AttachmentModelPriority.Add(value);
                    changed = true;
                }
            }
            return changed;
        }

        private static bool MergeMissing(ModelCapabilitySettings target, ModelCapabilitySettings source)
        {
            var changed = false;
            if (target.MaxContextTokens.GetValueOrDefault() <= 0 && source.MaxContextTokens.GetValueOrDefault() > 0)
            {
                target.MaxContextTokens = source.MaxContextTokens;
                changed = true;
            }
            if (target.MaxOutputTokens.GetValueOrDefault() <= 0 && source.MaxOutputTokens.GetValueOrDefault() > 0)
            {
                target.MaxOutputTokens = source.MaxOutputTokens;
                changed = true;
            }
            if (!target.SupportsImages.HasValue && source.SupportsImages.HasValue)
            {
                target.SupportsImages = source.SupportsImages;
                changed = true;
            }
            if (!target.SupportsReasoning.HasValue && source.SupportsReasoning.HasValue)
            {
                target.SupportsReasoning = source.SupportsReasoning;
                changed = true;
            }
            if (!target.SupportsAudio.HasValue && source.SupportsAudio.HasValue)
            {
                target.SupportsAudio = source.SupportsAudio;
                changed = true;
            }
            if (target.MaxImagesPerPrompt.GetValueOrDefault() <= 0 && source.MaxImagesPerPrompt.GetValueOrDefault() > 0)
            {
                target.MaxImagesPerPrompt = source.MaxImagesPerPrompt;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(target.ReasoningRequestMode) && !string.IsNullOrWhiteSpace(source.ReasoningRequestMode))
            {
                target.ReasoningRequestMode = source.ReasoningRequestMode;
                changed = true;
            }
            return changed;
        }

        private static LlmRequestException InvalidCatalog(string endpoint, string reason, string response, Exception inner)
        {
            var preview = (response ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (preview.Length > 240) preview = preview.Substring(0, 240) + "…";
            return new LlmRequestException(
                LlmFailureKind.InvalidResponse,
                "Каталог моделей не загружен: " + reason + ". Проверьте URL каталога" +
                (string.IsNullOrWhiteSpace(endpoint) ? string.Empty : " (" + endpoint + ")") + "." +
                (preview.Length == 0 ? string.Empty : " Начало ответа: " + preview),
                inner);
        }

        private static string ReadString(JObject value, params string[] names)
        {
            foreach (var name in names)
            {
                var token = value[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.Value<string>();
                }
            }
            return null;
        }

        private static int? ReadNullableInt(JObject value, params string[] names)
        {
            foreach (var name in names)
            {
                var token = value[name];
                int parsed;
                if (token != null && token.Type != JTokenType.Null && int.TryParse(token.ToString(), out parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        private static bool? ReadNullableBool(JObject value, params string[] names)
        {
            foreach (var name in names)
            {
                var token = value[name];
                bool parsed;
                if (token != null && token.Type != JTokenType.Null && bool.TryParse(token.ToString(), out parsed))
                {
                    return parsed;
                }
            }
            return null;
        }
    }
}
