using System;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public static class TokenEstimateCalibration
    {
        private const double MinimumObservedRatio = 0.1;
        private const double MaximumObservedRatio = 32.0;

        public static double EffectiveMultiplier(AppSettings settings, string model = null)
        {
            TokenEstimateCalibrationSettings calibration;
            if (settings != null && settings.AutoCalibrateTokenEstimate &&
                TryGet(settings, model, out calibration) && calibration.SampleCount > 0)
            {
                return ClampMultiplier(calibration.Multiplier <= 0 ? 1.0 : calibration.Multiplier);
            }
            return ManualMultiplier(settings);
        }

        public static int EffectiveInterceptTokens(AppSettings settings, string model = null)
        {
            TokenEstimateCalibrationSettings calibration;
            if (settings == null || !settings.AutoCalibrateTokenEstimate ||
                !TryGet(settings, model, out calibration) || calibration.SampleCount <= 0)
            {
                return 0;
            }
            return (int)Math.Ceiling(ClampIntercept(calibration.InterceptTokens));
        }

        public static int PredictPromptTokens(AppSettings settings, int basePromptTokens, string model = null)
        {
            if (basePromptTokens <= 0) return 0;
            var predicted = EffectiveMultiplier(settings, model) * basePromptTokens +
                EffectiveInterceptTokens(settings, model);
            return ClampTokenCount(predicted);
        }

        public static int AddPromptIntercept(AppSettings settings, int scaledPromptTokens, string model = null)
        {
            if (scaledPromptTokens <= 0) return 0;
            return ClampTokenCount(scaledPromptTokens + EffectiveInterceptTokens(settings, model));
        }

        public static int SampleCount(AppSettings settings, string model = null)
        {
            TokenEstimateCalibrationSettings calibration;
            return TryGet(settings, model, out calibration) ? Math.Max(0, calibration.SampleCount) : 0;
        }

        public static TokenEstimateCalibrationSettings Get(AppSettings settings, string model = null)
        {
            TokenEstimateCalibrationSettings calibration;
            return TryGet(settings, model, out calibration) ? calibration : null;
        }

        public static bool Observe(
            AppSettings settings,
            string model,
            int estimatedPromptTokens,
            int actualPromptTokens)
        {
            var slope = Math.Max(0.01, EffectiveMultiplier(settings, model));
            var baseEstimate = Math.Max(
                1,
                (int)Math.Round((estimatedPromptTokens - EffectiveInterceptTokens(settings, model)) / slope));
            return Observe(settings, model, baseEstimate, estimatedPromptTokens, actualPromptTokens);
        }

        public static bool Observe(
            AppSettings settings,
            string model,
            int baseEstimatedPromptTokens,
            int estimatedPromptTokens,
            int actualPromptTokens)
        {
            if (settings == null || !settings.AutoCalibrateTokenEstimate ||
                baseEstimatedPromptTokens < 64 || actualPromptTokens < 1)
            {
                return false;
            }

            var observedRatio = actualPromptTokens / (double)baseEstimatedPromptTokens;
            if (observedRatio < MinimumObservedRatio || observedRatio > MaximumObservedRatio)
            {
                return false;
            }

            var key = ModelKey(settings, model);
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (settings.TokenEstimateCalibrations == null)
            {
                settings.TokenEstimateCalibrations = new System.Collections.Generic.Dictionary<string, TokenEstimateCalibrationSettings>(
                    StringComparer.OrdinalIgnoreCase);
            }

            TokenEstimateCalibrationSettings calibration;
            if (!settings.TokenEstimateCalibrations.TryGetValue(key, out calibration) || calibration == null)
            {
                calibration = new TokenEstimateCalibrationSettings { Multiplier = 1.0 };
                settings.TokenEstimateCalibrations[key] = calibration;
            }

            AddRegressionSample(calibration, baseEstimatedPromptTokens, actualPromptTokens);
            calibration.SampleCount = Math.Min(1000000, Math.Max(0, calibration.SampleCount) + 1);
            calibration.LastBaseEstimatedPromptTokens = baseEstimatedPromptTokens;
            calibration.LastEstimatedPromptTokens = Math.Max(0, estimatedPromptTokens);
            calibration.LastActualPromptTokens = actualPromptTokens;
            calibration.UpdatedUtc = DateTime.UtcNow;
            return true;
        }

        public static bool MergeModel(AppSettings target, AppSettings source, string model = null)
        {
            if (target == null || source == null) return false;
            var key = ModelKey(source, model);
            TokenEstimateCalibrationSettings incoming;
            if (string.IsNullOrWhiteSpace(key) || !TryGet(source, key, out incoming)) return false;
            if (target.TokenEstimateCalibrations == null)
            {
                target.TokenEstimateCalibrations = new System.Collections.Generic.Dictionary<string, TokenEstimateCalibrationSettings>(
                    StringComparer.OrdinalIgnoreCase);
            }

            TokenEstimateCalibrationSettings existing;
            if (target.TokenEstimateCalibrations.TryGetValue(key, out existing) && existing != null &&
                (existing.SampleCount > incoming.SampleCount ||
                 existing.SampleCount == incoming.SampleCount && existing.UpdatedUtc >= incoming.UpdatedUtc))
            {
                return false;
            }
            target.TokenEstimateCalibrations[key] = incoming.Clone();
            return true;
        }

        private static void AddRegressionSample(
            TokenEstimateCalibrationSettings calibration,
            double baseTokens,
            double actualTokens)
        {
            var count = Math.Max(0, calibration.FitSampleCount);
            if (count == 0 || !ValidFit(calibration))
            {
                calibration.FitSampleCount = 1;
                calibration.MeanBasePromptTokens = baseTokens;
                calibration.MeanActualPromptTokens = actualTokens;
                calibration.BasePromptTokenM2 = 0;
                calibration.BaseActualPromptC2 = 0;
                calibration.Multiplier = ClampMultiplier(actualTokens / baseTokens);
                calibration.InterceptTokens = 0;
                return;
            }

            var nextCount = Math.Min(1000000, count + 1);
            var baseDelta = baseTokens - calibration.MeanBasePromptTokens;
            var actualDelta = actualTokens - calibration.MeanActualPromptTokens;
            var nextBaseMean = calibration.MeanBasePromptTokens + baseDelta / nextCount;
            var nextActualMean = calibration.MeanActualPromptTokens + actualDelta / nextCount;
            calibration.BasePromptTokenM2 += baseDelta * (baseTokens - nextBaseMean);
            calibration.BaseActualPromptC2 += baseDelta * (actualTokens - nextActualMean);
            calibration.MeanBasePromptTokens = nextBaseMean;
            calibration.MeanActualPromptTokens = nextActualMean;
            calibration.FitSampleCount = nextCount;
            UpdateLinearFit(calibration);
        }

        private static void UpdateLinearFit(TokenEstimateCalibrationSettings calibration)
        {
            var count = Math.Max(1, calibration.FitSampleCount);
            var meanBase = Math.Max(1.0, calibration.MeanBasePromptTokens);
            var meanActual = Math.Max(1.0, calibration.MeanActualPromptTokens);
            var sumBaseSquared = Math.Max(0, calibration.BasePromptTokenM2) + count * meanBase * meanBase;
            var sumBaseActual = calibration.BaseActualPromptC2 + count * meanBase * meanActual;
            var throughOriginSlope = sumBaseSquared <= 0 ? meanActual / meanBase : sumBaseActual / sumBaseSquared;
            var enoughVariation = calibration.BasePromptTokenM2 >= Math.Max(4096.0, meanBase * meanBase * 0.0001);
            var slope = enoughVariation && calibration.BasePromptTokenM2 > 0
                ? calibration.BaseActualPromptC2 / calibration.BasePromptTokenM2
                : throughOriginSlope;
            if (double.IsNaN(slope) || double.IsInfinity(slope) || slope <= 0) slope = throughOriginSlope;
            slope = ClampMultiplier(slope);

            var intercept = enoughVariation ? meanActual - slope * meanBase : 0;
            if (intercept < 0)
            {
                intercept = 0;
                slope = ClampMultiplier(throughOriginSlope);
            }
            else if (intercept > AppSettings.MaximumTokenEstimateInterceptTokens)
            {
                intercept = AppSettings.MaximumTokenEstimateInterceptTokens;
                slope = ClampMultiplier((meanActual - intercept) / meanBase);
            }

            calibration.Multiplier = slope;
            calibration.InterceptTokens = ClampIntercept(intercept);
        }

        private static bool ValidFit(TokenEstimateCalibrationSettings calibration)
        {
            return calibration != null && calibration.FitSampleCount > 0 &&
                IsFinitePositive(calibration.MeanBasePromptTokens) &&
                IsFinitePositive(calibration.MeanActualPromptTokens) &&
                IsFiniteNonNegative(calibration.BasePromptTokenM2) &&
                !double.IsNaN(calibration.BaseActualPromptC2) &&
                !double.IsInfinity(calibration.BaseActualPromptC2);
        }

        private static bool TryGet(
            AppSettings settings,
            string model,
            out TokenEstimateCalibrationSettings calibration)
        {
            calibration = null;
            var key = ModelKey(settings, model);
            return settings != null && settings.TokenEstimateCalibrations != null &&
                !string.IsNullOrWhiteSpace(key) &&
                settings.TokenEstimateCalibrations.TryGetValue(key, out calibration) &&
                calibration != null;
        }

        private static string ModelKey(AppSettings settings, string model)
        {
            var value = string.IsNullOrWhiteSpace(model)
                ? (settings == null ? null : settings.Model)
                : model;
            return (value ?? string.Empty).Trim();
        }

        private static double ManualMultiplier(AppSettings settings)
        {
            var value = settings == null ? AppSettings.DefaultTokenEstimateMultiplier : settings.TokenEstimateMultiplier;
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                value = AppSettings.DefaultTokenEstimateMultiplier;
            }
            return ClampMultiplier(value);
        }

        private static double ClampMultiplier(double value)
        {
            return Math.Max(
                AppSettings.MinimumTokenEstimateMultiplier,
                Math.Min(AppSettings.MaximumTokenEstimateMultiplier, value));
        }

        private static double ClampIntercept(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Max(0, Math.Min(AppSettings.MaximumTokenEstimateInterceptTokens, value));
        }

        private static int ClampTokenCount(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            if (double.IsInfinity(value) || value >= int.MaxValue) return int.MaxValue;
            return Math.Max(1, (int)Math.Ceiling(value));
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
