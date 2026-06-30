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
            var changed = false;
            var models = catalog["models"] ?? catalog["Models"];
            if (models == null)
            {
                return false;
            }
            foreach (var model in models.Children<JObject>())
            {
                var value = ReadString(model, "Value", "value");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                var capability = new ModelCapabilitySettings
                {
                    MaxContextTokens = ReadNullableInt(model, "MaxContextTokens", "max_context_tokens", "maxContextTokens"),
                    SupportsImages = ReadNullableBool(model, "SupportsImages", "supports_images", "supportsImages"),
                    MaxImagesPerPrompt = ReadNullableInt(model, "MaxImagesPerPrompt", "max_images_per_prompt", "maxImagesPerPrompt")
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
                settings.ModelCapabilities[value] = capability;
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
