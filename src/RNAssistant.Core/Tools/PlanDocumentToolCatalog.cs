using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Tools
{
    // Stable private journal operation names. These are no longer public tool descriptors.
    public static class PlanDocumentToolCatalog
    {
        public const string SaveToolId = "plan.document.save";
        public const string RestoreToolId = "plan.document.restore";
        public const string DeleteToolId = "plan.document.delete";

        public static bool Owns(string toolId)
        {
            return string.Equals(toolId, SaveToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, RestoreToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, DeleteToolId, StringComparison.Ordinal);
        }

    }
}
