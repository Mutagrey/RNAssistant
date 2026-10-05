using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebCheckOperation { Click, TextEquals }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum WebCheckStatus { Passed, Failed, NotRun }

    public sealed class WebCheckStep
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; private set; }
        [JsonProperty("operation", Required = Required.Always)] public WebCheckOperation Operation { get; private set; }
        [JsonProperty("selector", Required = Required.Always)] public string Selector { get; private set; }
        [JsonProperty("expected")] public string Expected { get; private set; }

        [JsonConstructor]
        public WebCheckStep(string id, WebCheckOperation operation, string selector, string expected = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl) ||
                !Enum.IsDefined(typeof(WebCheckOperation), operation) || string.IsNullOrWhiteSpace(selector) ||
                selector.Length > 256 || operation == WebCheckOperation.TextEquals && (expected == null || expected.Length > 512) ||
                operation == WebCheckOperation.Click && expected != null)
                throw new ArgumentException("Invalid web check step (id, operation, selector or expected text).");
            Id = id; Operation = operation; Selector = selector; Expected = expected;
        }
    }

    // Caller-owned acceptance input, frozen before the run; never loaded from
    // writable project files by the tool or replaced by model arguments.
    public sealed class WebFunctionalChecks
    {
        public const int MaximumSteps = 32;
        [JsonProperty("entryPath", Required = Required.Always)] public string EntryPath { get; private set; }
        [JsonProperty("steps", Required = Required.Always)] public IReadOnlyList<WebCheckStep> Steps { get; private set; }
        [JsonIgnore] public string Sha256 { get { return TextPatternEngine.Sha256(JsonConvert.SerializeObject(this)); } }

        [JsonConstructor]
        public WebFunctionalChecks(string entryPath, IEnumerable<WebCheckStep> steps)
        {
            var bounded = steps?.Take(MaximumSteps + 1).ToArray();
            if (string.IsNullOrWhiteSpace(entryPath) || entryPath.Length > 512 || entryPath.StartsWith("/", StringComparison.Ordinal) ||
                entryPath.Any(char.IsControl) || entryPath.IndexOfAny(new[] { '\\', ':', '?', '#', '%' }) >= 0 ||
                entryPath.Split('/').Any(part => part.Length == 0 || part == "." || part == "..") ||
                !entryPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !entryPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                bounded == null || bounded.Length == 0 || bounded.Length > MaximumSteps || bounded.Any(step => step == null) ||
                bounded.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != bounded.Length ||
                !bounded.Any(step => step.Operation == WebCheckOperation.TextEquals))
                throw new ArgumentException("Web checks require a relative HTML entry and 1–32 distinct steps including TextEquals.");
            EntryPath = entryPath; Steps = Array.AsReadOnly(bounded);
            if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(this)) > 32768)
                throw new ArgumentException("Web checks exceed 32 KiB.");
        }

        public IReadOnlyList<WebCheckResult> NotRun()
        { return Steps.Select(step => new WebCheckResult(step.Id, WebCheckStatus.NotRun)).ToArray(); }

        public bool Matches(IReadOnlyList<WebCheckResult> results)
        {
            return results != null && results.Count == Steps.Count && results.Select((result, index) =>
                result != null && result.Id == Steps[index].Id && Enum.IsDefined(typeof(WebCheckStatus), result.Status) &&
                (result.Status != WebCheckStatus.Passed || Steps[index].Operation != WebCheckOperation.TextEquals ||
                    result.Actual == Steps[index].Expected)).All(value => value);
        }
    }

    public sealed class WebCheckResult
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; private set; }
        [JsonProperty("status", Required = Required.Always)] public WebCheckStatus Status { get; private set; }
        [JsonProperty("actual")] public string Actual { get; private set; }
        [JsonProperty("error")] public string Error { get; private set; }

        [JsonConstructor]
        public WebCheckResult(string id, WebCheckStatus status, string actual = null, string error = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !Enum.IsDefined(typeof(WebCheckStatus), status) ||
                actual?.Length > 700 || error?.Length > 700 || status == WebCheckStatus.Passed && error != null)
                throw new ArgumentException("Invalid web check result.");
            Id = id; Status = status; Actual = actual; Error = error;
        }
    }
}
