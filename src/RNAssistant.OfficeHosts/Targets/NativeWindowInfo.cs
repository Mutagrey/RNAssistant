using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RNAssistant.OfficeHosts
{
    internal static class NativeWindowInfo
    {
        public static int GetProcessId(long hwnd)
        {
            if (hwnd == 0)
            {
                return 0;
            }

            int processId;
            GetWindowThreadProcessId(new IntPtr(hwnd), out processId);
            return processId;
        }

        public static long ReadLongMemberPath(object instance, params string[] names)
        {
            object current = instance;
            foreach (var name in names)
            {
                if (current == null)
                {
                    return 0;
                }

                try
                {
                    current = current.GetType().InvokeMember(
                        name,
                        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                        null,
                        current,
                        null);
                }
                catch
                {
                    return 0;
                }
            }

            try
            {
                return Convert.ToInt64(current);
            }
            catch
            {
                return 0;
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    }
}
