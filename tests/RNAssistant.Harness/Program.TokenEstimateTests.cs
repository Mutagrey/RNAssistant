using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TokenEstimateMultiplierAppliesToPromptParts()
        {
            var text = new string('a', 300);
            var baseline = ModelContextBudget.EstimateTextTokens(text);
            AssertEqual(100, ModelContextBudget.EstimateTextTokens(new string('a', 400)),
                "ASCII estimate uses UTF-8 bytes per token");
            AssertEqual(50, ModelContextBudget.EstimateTextTokens(new string('я', 100)),
                "non-ASCII estimate uses UTF-8 size rather than character count");
            var settings = new AppSettings
            {
                TokenEstimateMultiplier = 0.5,
                AutoCalibrateTokenEstimate = false
            };
            var adjusted = ModelContextBudget.EstimateTextTokens(text, settings);
            AssertEqual((int)Math.Ceiling(baseline * 0.5), adjusted, "manual token estimate multiplier");

            var message = new ChatMessage { Role = "user", Content = text };
            AssertTrue(
                ModelContextBudget.EstimateMessageTokens(message, settings) < ModelContextBudget.EstimateMessageTokens(message),
                "message estimate uses settings");
            AssertTrue(
                ModelContextBudget.TruncateText(text, 30, settings).Length > ModelContextBudget.TruncateText(text, 30).Length,
                "truncation uses the same multiplier");
        }

        private static void TokenEstimateRequestAdmissionReserves()
        {
            var settings = new AppSettings();
            var input = ModelContextBudget.InputBudgetTokens(settings);
            AssertEqual((int)Math.Ceiling(input / (double)ModelContextBudget.ContinuationReserveDivisor),
                ModelContextBudget.ContinuationReserveTokens(settings),
                "default continuation reserve is proportional to the admitted input");
            AssertEqual(ModelContextBudget.MinimumContinuationReserveTokens,
                ModelContextBudget.ContinuationReserveTokens(new AppSettings
                {
                    ContextWindowOverrideTokens = 4096
                }),
                "small contexts retain the minimum continuation reserve");
            AssertEqual(ModelContextBudget.MaximumContinuationReserveTokens,
                ModelContextBudget.ContinuationReserveTokens(new AppSettings
                {
                    ContextWindowOverrideTokens = 200000
                }),
                "large contexts cap the continuation reserve");

            var messages = new[] { new ChatMessage { Role = "user", Content = "Request" } };
            var options = new LlmRequestOptions { ResponseFormat = LlmResponseFormats.JsonObject };
            var requestTokens = ModelContextBudget.EstimateRequestTokens(messages, options, settings);
            var continuation = ModelContextBudget.ContinuationReserveTokens(settings);
            AssertEqual(requestTokens + 123 + continuation,
                ModelContextBudget.EstimateAdmittedRequestTokens(
                    messages, options, settings, 123, continuation),
                "admission adds messages, actual options, repair, and continuation exactly once");
        }

        private static void TokenEstimateCalibrationLearnsFromApiUsage()
        {
            var settings = new AppSettings
            {
                Model = "model-a",
                TokenEstimateMultiplier = 1.0,
                AutoCalibrateTokenEstimate = true
            };

            AssertTrue(TokenEstimateCalibration.Observe(settings, "model-a", 1000, 600), "first API sample accepted");
            AssertEqual(1, TokenEstimateCalibration.SampleCount(settings, "model-a"), "sample count stored");
            AssertNear(0.6, TokenEstimateCalibration.EffectiveMultiplier(settings, "model-a"), 0.001,
                "first sample calibrates estimate");
            var calibration = TokenEstimateCalibration.Get(settings, "model-a");
            AssertEqual(1000, calibration.LastEstimatedPromptTokens, "last estimated prompt stored");
            AssertEqual(1000, calibration.LastBaseEstimatedPromptTokens, "last base prompt estimate stored");
            AssertEqual(600, calibration.LastActualPromptTokens, "last actual prompt stored");
            AssertTrue(TokenEstimateCalibration.Observe(settings, "model-a", 600, 600), "second API sample accepted");
            AssertNear(0.6, TokenEstimateCalibration.EffectiveMultiplier(settings, "model-a"), 0.001,
                "calibration remains stable when estimate matches usage");

            var target = new AppSettings();
            AssertTrue(TokenEstimateCalibration.MergeModel(target, settings, "model-a"), "calibration merges into stored settings");
            AssertNear(0.6, TokenEstimateCalibration.EffectiveMultiplier(target, "model-a"), 0.001,
                "merged calibration is effective");
        }

        private static void TokenEstimateCalibrationLearnsLinearOverhead()
        {
            var settings = new AppSettings
            {
                Model = "model-linear",
                TokenEstimateMultiplier = 1.0,
                AutoCalibrateTokenEstimate = true
            };

            AssertTrue(TokenEstimateCalibration.Observe(settings, "model-linear", 1000, 1000, 700),
                "first linear sample accepted");
            var secondPrediction = TokenEstimateCalibration.PredictPromptTokens(settings, 3000, "model-linear");
            AssertTrue(TokenEstimateCalibration.Observe(settings, "model-linear", 3000, secondPrediction, 1700),
                "second linear sample accepted");

            AssertNear(0.5, TokenEstimateCalibration.EffectiveMultiplier(settings, "model-linear"), 0.001,
                "linear fit learns slope");
            AssertEqual(200, TokenEstimateCalibration.EffectiveInterceptTokens(settings, "model-linear"),
                "linear fit learns fixed model overhead");
            AssertEqual(2200, TokenEstimateCalibration.PredictPromptTokens(settings, 4000, "model-linear"),
                "linear fit predicts a different prompt size");
            var usage = JObject.FromObject(ContextUsageEstimator.FromPrompt(
                new[] { new ChatMessage { Role = "user", Content = "hello" } }, settings, null));
            AssertEqual(200, usage["estimateInterceptTokens"].Value<int>(), "usage exposes fitted overhead");
            AssertEqual(2, usage.SelectToken("calibrationProfile.FitSampleCount").Value<int>(),
                "usage carries regression state for safe settings saves");
        }

        private static void TokenEstimateCalibrationCanBeDisabled()
        {
            var settings = new AppSettings
            {
                Model = "model-a",
                TokenEstimateMultiplier = 1.25,
                AutoCalibrateTokenEstimate = false,
                TokenEstimateCalibrations = new Dictionary<string, TokenEstimateCalibrationSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["model-a"] = new TokenEstimateCalibrationSettings { Multiplier = 0.5, SampleCount = 4 }
                }
            };

            AssertNear(1.25, TokenEstimateCalibration.EffectiveMultiplier(settings, "model-a"), 0.001,
                "disabled auto calibration uses manual multiplier");
            AssertTrue(!TokenEstimateCalibration.Observe(settings, "model-a", 1000, 500),
                "disabled calibration ignores API samples");
        }

        private static void TokenEstimateUsesActualApiUsage()
        {
            var settings = new AppSettings
            {
                Model = "model-actual",
                ContextWindowOverrideTokens = 8000,
                AutoCalibrateTokenEstimate = true,
                TokenEstimateCalibrations = new Dictionary<string, TokenEstimateCalibrationSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["model-actual"] = new TokenEstimateCalibrationSettings
                    {
                        Multiplier = 0.5,
                        InterceptTokens = 200,
                        SampleCount = 2
                    }
                }
            };
            var messages = new[] { new ChatMessage { Role = "user", Content = "hello" } };
            var actual = JObject.FromObject(ContextUsageEstimator.FromPrompt(messages, settings, 321));
            AssertEqual(321, actual["usedTokens"].Value<int>(), "API prompt usage bypasses calibration and remains exact");
            AssertTrue(actual["actual"].Value<bool>(), "API prompt usage is marked actual");

            var estimated = JObject.FromObject(ContextUsageEstimator.FromPrompt(messages, settings, null));
            AssertTrue(!estimated["actual"].Value<bool>(), "missing API usage remains approximate");
            AssertTrue(estimated["usedTokens"].Value<int>() > 0, "missing API usage has an estimate");
        }

        private static void AssertNear(double expected, double actual, double tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
            }
        }
    }
}
