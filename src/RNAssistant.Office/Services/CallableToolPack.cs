using System.Collections.Generic;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
namespace RNAssistant.Office.Services
{
    internal static class CallableToolPack
    {
        public static CallableToolSet Create(string mode, string host, string runId,
            IReadOnlyList<ToolCatalogEntry> catalog, IReadOnlyList<ToolPackExtensionEventData> restoredAdmissions = null)
        {
            mode = ConversationRunPolicy.For(mode).Mode;
            return CallableToolSet.Create(mode, host, runId, catalog, CallableToolProfile.Core(),
                tools => ToolPackSnapshotFactory.Capture(mode, host, tools).Revision, restoredAdmissions);
        }
    }
}
