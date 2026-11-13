using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RNAssistant.Office.WebView
{
    // Hardware messages are observed outside renderer JS. CDP input does not traverse
    // these OS hooks, so isTrusted alone never distinguishes agent and human input.
    internal sealed class LocalBrowserPhysicalInput : IDisposable
    {
        private readonly Hook _mouseCallback, _keyCallback;
        private readonly Action<int, int, bool> _input;
        private IntPtr _mouse, _keys;
        private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal Point Position; internal uint MouseData, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Keyboard { internal uint Key, Scan, Flags, Time; internal UIntPtr Extra; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        internal LocalBrowserPhysicalInput(Action<int, int, bool> input)
        {
            _input = input; _mouseCallback = MouseInput; _keyCallback = KeyInput;
            var module = GetModuleHandle(null);
            _mouse = SetWindowsHookEx(14, _mouseCallback, module, 0);
            _keys = SetWindowsHookEx(13, _keyCallback, module, 0);
            if (_mouse == IntPtr.Zero || _keys == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("Native physical input attribution is unavailable."); }
        }
        private IntPtr MouseInput(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0) { var value = Marshal.PtrToStructure<Mouse>(data); if ((value.Flags & 1) == 0) try { _input(value.Position.X, value.Position.Y, false); } catch { } }
            return CallNextHookEx(_mouse, code, message, data);
        }
        private IntPtr KeyInput(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0) { var value = Marshal.PtrToStructure<Keyboard>(data); if ((value.Flags & 0x10) == 0) try { _input(0, 0, true); } catch { } }
            return CallNextHookEx(_keys, code, message, data);
        }
        public void Dispose()
        { if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse); if (_keys != IntPtr.Zero) UnhookWindowsHookEx(_keys); _mouse = _keys = IntPtr.Zero; }
    }
}
