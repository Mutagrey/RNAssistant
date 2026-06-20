using System;
using System.Runtime.InteropServices;
using RNAssistant.Office;
using Excel = Microsoft.Office.Interop.Excel;
using Outlook = Microsoft.Office.Interop.Outlook;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    public sealed class OfficeComAdapterProvider
    {
        public IOfficeApplicationAdapter Create(string host, OfficeTargetDescriptor target)
        {
            host = NormalizeHost(host, target);
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return new ExcelAdapter((Excel.Application)GetActiveOfficeObject("Excel.Application"), target);
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return new WordAdapter((Word.Application)GetActiveOfficeObject("Word.Application"), target);
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                return new PowerPointAdapter((PowerPoint.Application)GetActiveOfficeObject("PowerPoint.Application"), target);
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                return new OutlookAdapter((Outlook.Application)GetActiveOfficeObject("Outlook.Application"), target);
            }

            throw new InvalidOperationException("Unsupported Office host: " + (host ?? string.Empty));
        }

        private static object GetActiveOfficeObject(string progId)
        {
            try
            {
                return Marshal.GetActiveObject(progId);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("Office host is not running: " + progId, ex);
            }
        }

        private static string NormalizeHost(string host, OfficeTargetDescriptor target)
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                return host.Trim();
            }

            if (target != null && !string.IsNullOrWhiteSpace(target.Host))
            {
                return target.Host.Trim();
            }

            throw new InvalidOperationException("Office host was not specified.");
        }
    }
}
