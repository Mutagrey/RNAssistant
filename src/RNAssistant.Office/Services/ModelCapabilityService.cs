using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal static class ModelCapabilityService
    {
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
                var value = ReadString(model, "Value", "value", "id", "Id");
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
                settings.ModelCapabilities[value] = capability;
                if ((capability.SupportsImages == true || capability.SupportsAudio == true) &&
                    !settings.AttachmentModelPriority.Exists(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.AttachmentModelPriority.Add(value);
                }
                changed = true;
            }
            return changed;
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
