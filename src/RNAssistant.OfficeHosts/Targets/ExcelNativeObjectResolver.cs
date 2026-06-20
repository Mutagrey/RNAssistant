using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal static class ExcelNativeObjectResolver
    {
        private const int ObjIdNativeOm = unchecked((int)0xFFFFFFF0);

        public static Excel.Application ResolveApplication(long hwnd)
        {
            if (hwnd == 0)
            {
                return null;
            }

            var root = new IntPtr(hwnd);
            var application = ResolveApplicationFromWindow(root);
            if (application != null)
            {
                return application;
            }

            var nativeWindow = FindExcelNativeWindow(root);
            return nativeWindow == IntPtr.Zero ? null : ResolveApplicationFromWindow(nativeWindow);
        }

        private static Excel.Application ResolveApplicationFromWindow(IntPtr hwnd)
        {
            object nativeObject;
            if (!TryGetNativeObject(hwnd, out nativeObject) || nativeObject == null)
            {
                return null;
            }

            var application = nativeObject as Excel.Application;
            if (application != null)
            {
                return application;
            }

            var window = nativeObject as Excel.Window;
            if (window != null)
            {
                return window.Application;
            }

            return ReadApplicationProperty(nativeObject);
        }

        private static Excel.Application ReadApplicationProperty(object nativeObject)
        {
            try
            {
                return nativeObject.GetType().InvokeMember(
                    "Application",
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    null,
                    nativeObject,
                    null) as Excel.Application;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetNativeObject(IntPtr hwnd, out object nativeObject)
        {
            nativeObject = null;
            try
            {
                var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
                return AccessibleObjectFromWindow(hwnd, ObjIdNativeOm, ref iidDispatch, out nativeObject) >= 0
                    && nativeObject != null;
            }
            catch (COMException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IntPtr FindExcelNativeWindow(IntPtr root)
        {
            if (IsExcelNativeWindow(root))
            {
                return root;
            }

            var result = IntPtr.Zero;
            try
            {
                EnumChildWindows(root, delegate(IntPtr child, IntPtr parameter)
                {
                    if (IsExcelNativeWindow(child))
                    {
                        result = child;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                return IntPtr.Zero;
            }

            return result;
        }

        private static bool IsExcelNativeWindow(IntPtr hwnd)
        {
            return string.Equals(GetWindowClassName(hwnd), "EXCEL7", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(256);
                return GetClassName(hwnd, builder, builder.Capacity) == 0 ? string.Empty : builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr parameter);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            int objectId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IDispatch)] out object nativeObject);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    }
}
