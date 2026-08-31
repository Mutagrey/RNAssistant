using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
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

        public IOfficeApplicationAdapter Create(
            string host,
            OfficeTargetDescriptor target,
            IOfficeStaDispatcher dispatcher)
        {
            host = NormalizeHost(host, target);
            if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
            {
                var application = ResolveExcelApplication(target);
                ValidateTargetWindow("Excel", application, target);
                var workbook = ResolveExcelWorkbook(application, target);
                return new ExcelAdapter(
                    workbook.Application ?? application,
                    workbook,
                    dispatcher,
                    "desktop-native-owner");
            }

            if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
            {
                var application = (Word.Application)GetActiveOfficeObject("Word.Application");
                ValidateTargetWindow("Word", application, target);
                var document = ResolveWordDocument(application, target);
                return new WordAdapter(
                    document.Application ?? application,
                    document,
                    dispatcher);
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

        private static Excel.Application ResolveExcelApplication(OfficeTargetDescriptor target)
        {
            var application = target == null ? null : ExcelNativeObjectResolver.ResolveApplication(target.Hwnd);
            return application ?? (Excel.Application)GetActiveOfficeObject("Excel.Application");
        }

        private static Excel.Workbook ResolveExcelWorkbook(
            Excel.Application application,
            OfficeTargetDescriptor target)
        {
            if (application == null)
            {
                throw new InvalidOperationException("Excel application is unavailable.");
            }

            if (!HasExcelDocumentIdentity(target))
            {
                if (target == null || target.Hwnd == 0)
                {
                    throw new InvalidOperationException(
                        "An exact Excel window is required to bind the current workbook.");
                }

                Excel.Workbook windowMatch = null;
                foreach (Excel.Workbook workbook in application.Workbooks)
                {
                    if (!HasExcelWindow(workbook, target.Hwnd)) continue;
                    if (windowMatch != null)
                        throw new InvalidOperationException(
                            "The requested Excel window maps to more than one workbook.");
                    windowMatch = workbook;
                }
                if (windowMatch == null)
                    throw new InvalidOperationException(
                        "The workbook for the requested Excel window could not be resolved.");
                return windowMatch;
            }

            Excel.Workbook match = null;
            foreach (Excel.Workbook workbook in application.Workbooks)
            {
                if (!MatchesExcelTarget(workbook, target))
                {
                    continue;
                }

                if (match != null)
                {
                    throw new InvalidOperationException("The requested Excel workbook identity is ambiguous.");
                }
                match = workbook;
            }

            if (match == null)
            {
                throw new InvalidOperationException("The requested Excel workbook is not open.");
            }
            return match;
        }

        private static Word.Document ResolveWordDocument(
            Word.Application application,
            OfficeTargetDescriptor target)
        {
            if (application == null)
                throw new InvalidOperationException(
                    "Word application is unavailable.");
            if (!HasWordDocumentIdentity(target))
            {
                if (target == null || target.Hwnd == 0)
                    throw new InvalidOperationException(
                        "An exact Word window is required to bind the current document.");
                Word.Document windowMatch = null;
                foreach (Word.Document document in application.Documents)
                {
                    if (!HasWordWindow(document, target.Hwnd)) continue;
                    if (windowMatch != null)
                        throw new InvalidOperationException(
                            "The requested Word window maps to more than one document.");
                    windowMatch = document;
                }
                if (windowMatch == null)
                    throw new InvalidOperationException(
                        "The document for the requested Word window could not be resolved.");
                return windowMatch;
            }

            Word.Document match = null;
            foreach (Word.Document document in application.Documents)
            {
                if (!MatchesWordTarget(document, target)) continue;
                if (match != null)
                    throw new InvalidOperationException(
                        "The requested Word document identity is ambiguous.");
                match = document;
            }
            if (match == null)
                throw new InvalidOperationException(
                    "The requested Word document is not open.");
            return match;
        }

        private static bool HasWordWindow(Word.Document document, long hwnd)
        {
            if (document == null || hwnd == 0) return false;
            try
            {
                foreach (Word.Window window in document.Windows)
                {
                    if (NativeWindowInfo.ReadLongMemberPath(window, "Hwnd") == hwnd)
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool HasWordDocumentIdentity(
            OfficeTargetDescriptor target)
        {
            return target != null &&
                (!string.IsNullOrWhiteSpace(target.DocumentKey) ||
                 !string.IsNullOrWhiteSpace(target.FullName) ||
                 !string.IsNullOrWhiteSpace(target.Path) ||
                 !string.IsNullOrWhiteSpace(target.Name));
        }

        private static bool MatchesWordTarget(
            Word.Document document, OfficeTargetDescriptor target)
        {
            if (document == null || target == null) return false;
            if (!string.IsNullOrWhiteSpace(target.DocumentKey))
                return string.Equals(
                    WordDocumentKey(document), target.DocumentKey.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            var fullName = SafeString(delegate { return document.FullName; });
            if (!string.IsNullOrWhiteSpace(target.FullName))
                return SamePath(fullName, target.FullName);
            if (!string.IsNullOrWhiteSpace(target.Path))
                return SamePath(fullName, target.Path);
            return string.Equals(
                SafeString(delegate { return document.Name; }),
                target.Name == null ? string.Empty : target.Name.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string WordDocumentKey(Word.Document document)
        {
            return WordDocumentSession.StableKey(
                document,
                DocumentIdentity.RuntimeKey("Word", document));
        }

        private static bool HasExcelWindow(Excel.Workbook workbook, long hwnd)
        {
            if (workbook == null || hwnd == 0) return false;
            try
            {
                foreach (Excel.Window window in workbook.Windows)
                {
                    if (Convert.ToInt64(window.Hwnd) == hwnd) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool HasExcelDocumentIdentity(OfficeTargetDescriptor target)
        {
            return target != null
                && (!string.IsNullOrWhiteSpace(target.DocumentKey)
                    || !string.IsNullOrWhiteSpace(target.FullName)
                    || !string.IsNullOrWhiteSpace(target.Path)
                    || !string.IsNullOrWhiteSpace(target.Name));
        }

        private static bool MatchesExcelTarget(
            Excel.Workbook workbook,
            OfficeTargetDescriptor target)
        {
            if (workbook == null || target == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(target.DocumentKey))
            {
                return string.Equals(
                    ExcelDocumentKey(workbook),
                    target.DocumentKey.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }

            var fullName = SafeString(delegate { return workbook.FullName; });
            if (!string.IsNullOrWhiteSpace(target.FullName))
            {
                return SamePath(fullName, target.FullName);
            }
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                return SamePath(fullName, target.Path);
            }

            return string.Equals(
                SafeString(delegate { return workbook.Name; }),
                target.Name == null ? string.Empty : target.Name.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ExcelDocumentKey(Excel.Workbook workbook)
        {
            return ExcelDocumentSession.StableKey(
                workbook,
                DocumentIdentity.RuntimeKey("Excel", workbook));
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }

        private static bool SamePath(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
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
