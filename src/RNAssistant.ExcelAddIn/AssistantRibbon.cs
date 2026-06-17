using System.Runtime.InteropServices;
using Microsoft.Office.Core;
using RNAssistant.Office.Ribbon;

namespace RNAssistant.ExcelAddIn
{
    [ComVisible(true)]
    public sealed class AssistantRibbon : IRibbonExtensibility
    {
        private readonly ThisAddIn _addIn;

        public AssistantRibbon(ThisAddIn addIn)
        {
            _addIn = addIn;
        }

        public string GetCustomUI(string ribbonId)
        {
            return AssistantRibbonXml.Create("Excel");
        }

        public void OnRibbonLoad(IRibbonUI ribbon) { }
        public void OpenAssistant(IRibbonControl control) { _addIn.ShowAssistant(); }
        public void Summarize(IRibbonControl control) { _addIn.ShowAssistant("summarize"); }
        public void ExplainSelection(IRibbonControl control) { _addIn.ShowAssistant("explain-selection"); }
        public void DraftRewrite(IRibbonControl control) { _addIn.ShowAssistant("draft-rewrite"); }
        public void RunSkill(IRibbonControl control) { _addIn.ShowAssistant("run-skill"); }
        public void OpenSettings(IRibbonControl control) { _addIn.ShowAssistant("settings"); }
        public void OpenContext(IRibbonControl control) { _addIn.ShowAssistant("context"); }
    }
}

