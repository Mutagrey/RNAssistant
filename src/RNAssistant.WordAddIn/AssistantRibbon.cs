using System.Runtime.InteropServices;
using Microsoft.Office.Core;
using RNAssistant.Office.Ribbon;

namespace RNAssistant.WordAddIn
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
            return AssistantRibbonXml.Create("Word");
        }

        public void OnRibbonLoad(IRibbonUI ribbon) { }
        public void OpenAssistant(IRibbonControl control) { _addIn.ShowAssistant(); }
    }
}
