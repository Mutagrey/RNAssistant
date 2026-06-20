using System;
using System.Collections.Generic;
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
        public IReadOnlyList<OfficeTargetDescriptor> ListOpenTargets(string host)
        {
            return OfficeTargetEnumerator.ListOpenTargets(host, GetActiveOfficeObject);
        }

        public IOfficeApplicationAdapter Create(string host, OfficeTargetDescriptor target)
        {
            host = NormalizeHost(host, target);
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                var application = (Excel.Application)GetActiveOfficeObject("Excel.Application");
                ValidateTargetWindow("Excel", application, target);
                return new ExcelAdapter(application, target);
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                var application = (Word.Application)GetActiveOfficeObject("Word.Application");
                ValidateTargetWindow("Word", application, target);
                return new WordAdapter(application, target);
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                var application = (PowerPoint.Application)GetActiveOfficeObject("PowerPoint.Application");
                ValidateTargetWindow("PowerPoint", application, target);
                return new PowerPointAdapter(application, target);
            }

            if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
            {
                var application = (Outlook.Application)GetActiveOfficeObject("Outlook.Application");
                ValidateTargetWindow("Outlook", application, target);
                return new OutlookAdapter(application, target);
            }

            throw new InvalidOperationException("Unsupported Office host: " + (host ?? string.Empty));
        }

        private static void ValidateTargetWindow(string host, object application, OfficeTargetDescriptor target)
        {
            if (target == null || target.Hwnd == 0 || application == null)
            {
                return;
            }

            var applicationHwnd = ResolveApplicationHwnd(host, application);
            if (applicationHwnd == 0 || applicationHwnd == target.Hwnd)
            {
                return;
            }

            var expectedProcessId = target.ProcessId != 0 ? target.ProcessId : NativeWindowInfo.GetProcessId(target.Hwnd);
            var actualProcessId = NativeWindowInfo.GetProcessId(applicationHwnd);
            if (expectedProcessId != 0 && actualProcessId != 0 && expectedProcessId == actualProcessId)
            {
                return;
            }

            throw new InvalidOperationException(
                "Active " + host + " COM object does not match requested window. " +
                "Requested hwnd=" + target.Hwnd + ", resolved hwnd=" + applicationHwnd + ".");
        }

        private static long ResolveApplicationHwnd(string host, object application)
        {
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                return NativeWindowInfo.ReadLongMemberPath(application, "Hwnd");
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                return NativeWindowInfo.ReadLongMemberPath(application, "ActiveWindow", "Hwnd");
            }

            if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
            {
                var hwnd = NativeWindowInfo.ReadLongMemberPath(application, "HWND");
                return hwnd != 0 ? hwnd : NativeWindowInfo.ReadLongMemberPath(application, "Hwnd");
            }

            return 0;
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
