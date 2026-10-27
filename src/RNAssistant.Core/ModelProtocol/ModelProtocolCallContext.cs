using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.ModelProtocol
{
    public sealed class ModelProtocolCallContext
    {
        public IReadOnlyList<string> SequentialBatchToolIds { get; private set; }
        public bool ReflectionEnabled { get; private set; }
        public string Error { get; private set; }
        public bool IsComplete { get { return string.IsNullOrEmpty(Error); } }

        public ModelProtocolCallContext(IEnumerable<string> sequentialBatchToolIds, string error = null,
            bool reflectionEnabled = true)
        {
            ReflectionEnabled = reflectionEnabled;
            Error = !string.IsNullOrWhiteSpace(error) ? error
                : sequentialBatchToolIds == null ? "Model protocol sequential-batch context is incomplete." : null;
            SequentialBatchToolIds = sequentialBatchToolIds == null ? null : Snapshot(sequentialBatchToolIds);
        }

        // Schema generation and acceptance use the same explicit local authority.
        // A name or absence of mutation flags alone never grants batch membership.
        internal bool AllowsSequentialBatch(ToolCatalogEntry tool)
        {
            return IsComplete && tool != null && SequentialBatchToolIds.Contains(tool.Id, StringComparer.Ordinal) &&
                !tool.MutatesDocument && !tool.MutatesLocalState && !tool.RequiresConfirmation &&
                (tool.Policy == null || tool.Policy.IndependentLocalRead);
        }

        private static IReadOnlyList<string> Snapshot(IEnumerable<string> values)
        {
            return Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
    }

}
