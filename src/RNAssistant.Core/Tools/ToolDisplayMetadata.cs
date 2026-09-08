using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RNAssistant.Core.Tools
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ToolDisplayOperation { Command, Read, Search, Write, Delete, Export, Learn, Question, Plan, Chart, Package, Check }

    // Catalog-owned UI hints. Never execution policy, model wire or effect evidence.
    public sealed class ToolDisplayMetadata
    {
        [JsonProperty("action")] public string Action { get; private set; }
        [JsonProperty("runningAction")] public string RunningAction { get; private set; }
        [JsonProperty("operation")] public ToolDisplayOperation Operation { get; private set; }
        [JsonProperty("targetArguments", NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyList<string> TargetArguments { get; private set; }

        [JsonConstructor]
        public ToolDisplayMetadata(string action, string runningAction = null,
            ToolDisplayOperation operation = ToolDisplayOperation.Command, IEnumerable<string> targetArguments = null)
        {
            if (string.IsNullOrWhiteSpace(action) || action.Length > 200 || action.Any(char.IsControl))
                throw new ArgumentException("Display action must be a single line of 1–200 characters.", nameof(action));
            if (runningAction != null && (string.IsNullOrWhiteSpace(runningAction) || runningAction.Length > 200 || runningAction.Any(char.IsControl)))
                throw new ArgumentException("Display runningAction must be a single line of 1–200 characters.", nameof(runningAction));
            if (!Enum.IsDefined(typeof(ToolDisplayOperation), operation)) throw new ArgumentOutOfRangeException(nameof(operation));
            var targets = (targetArguments ?? new string[0]).ToArray();
            if (targets.Length > 8 || targets.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl)) ||
                targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
                throw new ArgumentException("Display targetArguments must contain at most eight distinct parameter names.", nameof(targetArguments));
            Action = action;
            RunningAction = runningAction ?? action;
            Operation = operation;
            TargetArguments = targetArguments == null ? null : Array.AsReadOnly(targets);
        }
    }
}
