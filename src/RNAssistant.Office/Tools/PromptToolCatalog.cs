using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    internal static class PromptToolCatalog
    {
        internal const string SaveToolId = "prompts.save";

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, SaveToolId, StringComparison.Ordinal);
        }

    }
}
