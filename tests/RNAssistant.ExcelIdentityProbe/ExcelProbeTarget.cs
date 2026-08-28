using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RNAssistant.ExcelIdentityProbe
{
    public static class ExcelProbeTarget
    {
        // Explicit window selection for diagnostics only; no ROT/ActiveWorkbook fallback.
        public static object ResolveApplication(long hwnd)
        {
            ComIdentityLease.RequireWindowsSta();
            if (hwnd == 0) throw new ArgumentException("An explicit Excel window HWND is required.", "hwnd");
            var root = new IntPtr(hwnd);
            var native = IsExcelWindow(root) ? root : IntPtr.Zero;
            if (native == IntPtr.Zero)
                EnumChildWindows(root, (child, state) =>
                {
                    if (!IsExcelWindow(child)) return true;
                    native = child;
                    return false;
                }, IntPtr.Zero);
            if (native == IntPtr.Zero) throw new InvalidOperationException("No EXCEL7 child in the selected window.");
            object window;
            var iid = new Guid("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(native, unchecked((uint)0xfffffff0), ref iid, out window));
            var application = window.GetType().InvokeMember("Application", BindingFlags.GetProperty,
                null, window, null);
            var appHwnd = Convert.ToInt64(application.GetType().InvokeMember("Hwnd", BindingFlags.GetProperty,
                null, application, null));
            if (ProcessId(hwnd) == 0 || ProcessId(hwnd) != ProcessId(appHwnd))
                throw new InvalidOperationException("Native object does not belong to the selected Excel process.");
            return application;
        }

        // Pointer equality is used only inside this one apartment to check membership,
        // never as the candidate identity exported to other clients.
        public static bool SameLocalObject(object left, object right)
        {
            ComIdentityLease.RequireWindowsSta();
            if (left == null || right == null || !Marshal.IsComObject(left) || !Marshal.IsComObject(right)) return false;
            var first = Marshal.GetIUnknownForObject(left);
            try
            {
                var second = Marshal.GetIUnknownForObject(right);
                try { return first == second; }
                finally { Marshal.Release(second); }
            }
            finally { Marshal.Release(first); }
        }

        public static uint ProcessId(long hwnd)
        {
            ComIdentityLease.RequireWindowsSta();
            uint processId;
            GetWindowThreadProcessId(new IntPtr(hwnd), out processId);
            return processId;
        }

        private static bool IsExcelWindow(IntPtr hwnd)
        {
            var name = new StringBuilder(64);
            return GetClassName(hwnd, name, name.Capacity) != 0 && name.ToString() == "EXCEL7";
        }

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr state);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
            [MarshalAs(UnmanagedType.IDispatch)] out object nativeObject);
    }
}
