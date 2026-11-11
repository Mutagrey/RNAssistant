#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <string>
#include <msclr/marshal_cppstd.h>

using namespace System;
using namespace System::IO;
using namespace System::Reflection;
using namespace msclr::interop;

// Export/assembly-loading shim only. Managed OfficeHosts owns all HWND/session policy.
public ref class ManagedPanelHost abstract sealed
{
private:
    static Object^ _host = nullptr;
    static String^ _rootPath = nullptr;
    static IntPtr _officeHwnd = IntPtr::Zero;
    static String^ _lastError = String::Empty;
    static bool _resolverInstalled = false;

    static Object^ Invoke(String^ name, array<Object^>^ arguments)
    {
        try { return _host->GetType()->GetMethod(name)->Invoke(_host, arguments); }
        catch (TargetInvocationException^ ex) { throw ex->InnerException == nullptr ? ex : ex->InnerException; }
    }

public:
    static int ShowPanel(IntPtr officeHwnd, String^ rootPath, int hostKind)
    {
        try
        {
            ValidateShowArguments(officeHwnd, rootPath, hostKind);
            rootPath = Path::GetFullPath(rootPath);
            bool alive = _host != nullptr && !safe_cast<bool>(_host->GetType()->GetProperty("IsDisposed")->GetValue(_host, nullptr));
            array<Object^>^ arguments = gcnew array<Object^>(3);
            arguments[0] = hostKind; arguments[1] = officeHwnd.ToInt64(); arguments[2] = rootPath;
            if (!alive)
            {
                ConfigureAssemblyResolution(rootPath);
                String^ path = Path::Combine(Path::Combine(rootPath, "lib"), "RNAssistant.OfficeHosts.dll");
                Type^ type = Assembly::LoadFrom(path)->GetType("RNAssistant.OfficeHosts.Windows.InProcessWindowHost", true);
                _host = type->GetMethod("Create", BindingFlags::Public | BindingFlags::Static)->Invoke(nullptr, arguments);
            }
            // Validate profile in the managed owner before changing resolver/root or target.
            Invoke("Show", arguments);
            _officeHwnd = officeHwnd;
            Log("Panel resumed by launcher.");
            ClearError(); return 0;
        }
        catch (Exception^ ex) { return Fail(-1, "Could not show RN Assistant panel.", ex, officeHwnd); }
    }
    static int ClosePanel()
    {
        try
        {
            if (_host != nullptr) Invoke("Shutdown", nullptr);
            _host = nullptr; ClearError(); return 0;
        }
        catch (Exception^ ex) { return Fail(-1, "Could not close RN Assistant panel.", ex, _officeHwnd); }
    }
    static int SetVisible(bool visible)
    {
        try
        {
            if (_host == nullptr || safe_cast<bool>(_host->GetType()->GetProperty("IsDisposed")->GetValue(_host, nullptr)))
            { SetError("Panel has not been created."); return -3; }
            array<Object^>^ arguments = gcnew array<Object^>(1); arguments[0] = visible;
            Invoke("SetVisible", arguments);
            ClearError(); return 0;
        }
        catch (Exception^ ex) { return Fail(-1, "Could not change panel visibility.", ex, _officeHwnd); }
    }
    static String^ LastError() { return _lastError == nullptr ? String::Empty : _lastError; }
private:
    static void ValidateShowArguments(IntPtr officeHwnd, String^ rootPath, int hostKind)
    {
        HWND hwnd = static_cast<HWND>(officeHwnd.ToPointer());
        if (hwnd == nullptr || !IsWindow(hwnd))
        {
            throw gcnew ArgumentException("Office HWND is invalid.");
        }

        if (IsIconic(hwnd))
        {
            throw gcnew InvalidOperationException("Office window is minimized. Restore it before showing the panel.");
        }

        if (String::IsNullOrWhiteSpace(rootPath))
        {
            throw gcnew ArgumentException("Portable root path is empty.");
        }

        if (!Directory::Exists(rootPath))
        {
            throw gcnew DirectoryNotFoundException("Portable root path was not found: " + rootPath);
        }

        if (hostKind < 1 || hostKind > 4)
        {
            throw gcnew ArgumentOutOfRangeException("hostKind", "Host kind must be 1..4.");
        }
    }

    static void ConfigureAssemblyResolution(String^ rootPath)
    {
        array<Char>^ separators = gcnew array<Char>(2);
        separators[0] = Path::DirectorySeparatorChar;
        separators[1] = Path::AltDirectorySeparatorChar;
        if (_resolverInstalled && !String::Equals(
            _rootPath->TrimEnd(separators), rootPath->TrimEnd(separators), StringComparison::OrdinalIgnoreCase))
            throw gcnew InvalidOperationException("Restart Office to use another RNAssistant portable root; loaded assemblies cannot be replaced in this process.");
        _rootPath = rootPath;
        if (!_resolverInstalled)
        {
            AppDomain::CurrentDomain->AssemblyResolve +=
                gcnew ResolveEventHandler(&ManagedPanelHost::ResolveAssembly);
            _resolverInstalled = true;
        }
    }

    static Assembly^ ResolveAssembly(Object^, ResolveEventArgs^ args)
    {
        try
        {
            AssemblyName^ assemblyName = gcnew AssemblyName(args->Name);
            String^ simpleName = assemblyName->Name;
            String^ candidate = Path::Combine(Path::Combine(_rootPath, "lib"), simpleName + ".dll");
            return File::Exists(candidate) ? Assembly::LoadFrom(candidate) : nullptr;
        }
        catch (Exception^ ex)
        {
            SetError("Managed assembly resolution failed: " + ex);
            return nullptr;
        }
    }

    static int Fail(int result, String^ message, Exception^ exception, IntPtr owner)
    {
        String^ detail = message + Environment::NewLine + exception;
        SetError(detail);
        Log("ERROR: " + detail);
        HWND hwnd = static_cast<HWND>(owner.ToPointer());
        MessageBoxW(hwnd, marshal_as<std::wstring>(detail).c_str(), L"RN Assistant", MB_OK | MB_ICONERROR);
        return result;
    }

    static void Log(String^ message)
    {
        if (String::IsNullOrWhiteSpace(_rootPath))
        {
            return;
        }

        try
        {
            String^ logDirectory = Path::Combine(_rootPath, "logs");
            Directory::CreateDirectory(logDirectory);
            String^ line = DateTime::Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " [NATIVE] " + message + Environment::NewLine;
            File::AppendAllText(Path::Combine(logDirectory, "native-host.log"), line);
        }
        catch (Exception^)
        {
        }
    }

    static void SetError(String^ message)
    {
        _lastError = message == nullptr ? String::Empty : message;
    }

    static void ClearError()
    {
        _lastError = String::Empty;
    }
};

extern "C" __declspec(dllexport)
int __stdcall Host_ShowPanelEx(HWND officeHwnd, const wchar_t* rootPath, int hostKind)
{
    try
    {
        String^ managedRoot = gcnew String(rootPath == nullptr ? L"" : rootPath);
        return ManagedPanelHost::ShowPanel(IntPtr(officeHwnd), managedRoot, hostKind);
    }
    catch (Exception^ ex)
    {
        MessageBoxW(officeHwnd, marshal_as<std::wstring>(ex->ToString()).c_str(), L"RN Assistant native host", MB_OK | MB_ICONERROR);
        return -1;
    }
    catch (...)
    {
        MessageBoxW(officeHwnd, L"Unknown native exception.", L"RN Assistant native host", MB_OK | MB_ICONERROR);
        return -2;
    }
}

extern "C" __declspec(dllexport)
int __stdcall Host_ShowPanel(HWND officeHwnd, const wchar_t* rootPath)
{
    return Host_ShowPanelEx(officeHwnd, rootPath, 1);
}

extern "C" __declspec(dllexport)
int __stdcall Host_ClosePanel()
{
    return ManagedPanelHost::ClosePanel();
}

extern "C" __declspec(dllexport)
int __stdcall Host_SetPanelVisible(int visible)
{
    return ManagedPanelHost::SetVisible(visible != 0);
}

extern "C" __declspec(dllexport)
int __stdcall Host_GetLastErrorMessage(wchar_t* buffer, int bufferChars)
{
    try
    {
        std::wstring message = marshal_as<std::wstring>(ManagedPanelHost::LastError());
        int required = static_cast<int>(message.length()) + 1;
        if (buffer == nullptr || bufferChars <= 0)
        {
            return required;
        }

        int count = std::min(static_cast<int>(message.length()), bufferChars - 1);
        if (count > 0)
        {
            wmemcpy_s(buffer, bufferChars, message.c_str(), count);
        }
        buffer[count] = L'\0';
        return count;
    }
    catch (...)
    {
        return -2;
    }
}
