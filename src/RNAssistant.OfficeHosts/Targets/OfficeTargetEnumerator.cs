using System;
using System.Collections.Generic;
using Excel = Microsoft.Office.Interop.Excel;
using Outlook = Microsoft.Office.Interop.Outlook;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    internal static class OfficeTargetEnumerator
    {
        public delegate object OfficeObjectResolver(string progId);

        public static IReadOnlyList<OfficeTargetDescriptor> ListOpenTargets(string host, OfficeObjectResolver resolver)
        {
            var result = new List<OfficeTargetDescriptor>();
            if (resolver == null)
            {
                return result;
            }

            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "All", StringComparison.OrdinalIgnoreCase))
            {
                AddTargets(result, "Excel", resolver);
                AddTargets(result, "Word", resolver);
                AddTargets(result, "PowerPoint", resolver);
                AddTargets(result, "Outlook", resolver);
                return result;
            }

            AddTargets(result, host, resolver);
            return result;
        }

        private static void AddTargets(List<OfficeTargetDescriptor> result, string host, OfficeObjectResolver resolver)
        {
            try
            {
                if (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase))
                {
                    AddExcelTargets(result, (Excel.Application)resolver("Excel.Application"));
                }
                else if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
                {
                    AddWordTargets(result, (Word.Application)resolver("Word.Application"));
                }
                else if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
                {
                    AddPowerPointTargets(result, (PowerPoint.Application)resolver("PowerPoint.Application"));
                }
                else if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
                {
                    AddOutlookTargets(result, (Outlook.Application)resolver("Outlook.Application"));
                }
            }
            catch
            {
            }
        }

        private static void AddExcelTargets(List<OfficeTargetDescriptor> result, Excel.Application application)
        {
            var hwnd = NativeWindowInfo.ReadLongMemberPath(application, "Hwnd");
            var processId = NativeWindowInfo.GetProcessId(hwnd);
            foreach (Excel.Workbook workbook in application.Workbooks)
            {
                result.Add(new OfficeTargetDescriptor
                {
                    Host = "Excel",
                    Hwnd = hwnd,
                    ProcessId = processId,
                    FullName = SafeString(delegate { return workbook.FullName; }),
                    Path = SafeString(delegate { return workbook.FullName; }),
                    Name = SafeString(delegate { return workbook.Name; })
                });
            }
        }

        private static void AddWordTargets(List<OfficeTargetDescriptor> result, Word.Application application)
        {
            var hwnd = NativeWindowInfo.ReadLongMemberPath(application, "ActiveWindow", "Hwnd");
            var processId = NativeWindowInfo.GetProcessId(hwnd);
            foreach (Word.Document document in application.Documents)
            {
                result.Add(new OfficeTargetDescriptor
                {
                    Host = "Word",
                    Hwnd = hwnd,
                    ProcessId = processId,
                    FullName = SafeString(delegate { return document.FullName; }),
                    Path = SafeString(delegate { return document.FullName; }),
                    Name = SafeString(delegate { return document.Name; })
                });
            }
        }

        private static void AddPowerPointTargets(List<OfficeTargetDescriptor> result, PowerPoint.Application application)
        {
            var hwnd = NativeWindowInfo.ReadLongMemberPath(application, "HWND");
            var processId = NativeWindowInfo.GetProcessId(hwnd);
            foreach (PowerPoint.Presentation presentation in application.Presentations)
            {
                result.Add(new OfficeTargetDescriptor
                {
                    Host = "PowerPoint",
                    Hwnd = hwnd,
                    ProcessId = processId,
                    FullName = SafeString(delegate { return presentation.FullName; }),
                    Path = SafeString(delegate { return presentation.FullName; }),
                    Name = SafeString(delegate { return presentation.Name; })
                });
            }
        }

        private static void AddOutlookTargets(List<OfficeTargetDescriptor> result, Outlook.Application application)
        {
            try
            {
                var inspector = application.ActiveInspector();
                var mail = inspector == null ? null : inspector.CurrentItem as Outlook.MailItem;
                if (mail != null)
                {
                    var hwnd = NativeWindowInfo.ReadLongMemberPath(inspector, "HWND");
                    result.Add(new OfficeTargetDescriptor
                    {
                        Host = "Outlook",
                        Hwnd = hwnd,
                        ProcessId = NativeWindowInfo.GetProcessId(hwnd),
                        EntryId = SafeString(delegate { return mail.EntryID; }),
                        Name = SafeString(delegate { return mail.Subject; })
                    });
                }
            }
            catch
            {
            }

            try
            {
                var explorer = application.ActiveExplorer();
                var hwnd = NativeWindowInfo.ReadLongMemberPath(explorer, "HWND");
                var folder = explorer == null ? null : explorer.CurrentFolder as Outlook.MAPIFolder;
                if (folder != null)
                {
                    result.Add(new OfficeTargetDescriptor
                    {
                        Host = "Outlook",
                        Hwnd = hwnd,
                        ProcessId = NativeWindowInfo.GetProcessId(hwnd),
                        FolderPath = SafeString(delegate { return folder.FolderPath; }),
                        Name = SafeString(delegate { return folder.Name; })
                    });
                }
            }
            catch
            {
            }
        }

        private delegate string StringGetter();

        private static string SafeString(StringGetter getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }
    }
}
