#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <string>
#include <msclr/marshal_cppstd.h>

#ifndef WDA_EXCLUDEFROMCAPTURE
#define WDA_EXCLUDEFROMCAPTURE 0x00000011
#endif

using namespace System;
using namespace System::Drawing;
using namespace System::IO;
using namespace System::Reflection;
using namespace System::Windows::Forms;
using namespace msclr::interop;

public enum class PanelOwnerMode
{
    None = 0,
    OwnerWindow = 1,
    TopMostDebug = 2
};

public ref class Win32WindowWrapper sealed : public IWin32Window
{
private:
    IntPtr _handle;

public:
    explicit Win32WindowWrapper(IntPtr handle)
        : _handle(handle)
    {
    }

    virtual property IntPtr Handle
    {
        IntPtr get()
        {
            return _handle;
        }
    }
};

public ref class ManagedPanelHost abstract sealed
{
private:
    static Form^ _form = nullptr;
    static Object^ _session = nullptr;
    static String^ _rootPath = nullptr;
    static IntPtr _officeHwnd = IntPtr::Zero;
    static int _hostKind = 0;
    static PanelOwnerMode _ownerMode = PanelOwnerMode::OwnerWindow;
    static String^ _lastError = String::Empty;
    static bool _resolverInstalled = false;
    static bool _screenCaptureProtectionEnabled = true;
    static EventInfo^ _screenCaptureProtectionChangedEvent = nullptr;
    static Action<bool>^ _screenCaptureProtectionChangedHandler = nullptr;

public:
    static int ShowPanel(IntPtr officeHwnd, String^ rootPath, int hostKind)
    {
        try
        {
            ValidateShowArguments(officeHwnd, rootPath, hostKind);
            rootPath = Path::GetFullPath(rootPath);
            ConfigureAssemblyResolution(rootPath);
            Log(String::Format(
                "Host_ShowPanelEx: hostKind={0}, hwnd={1}, root={2}",
                hostKind,
                officeHwnd.ToInt64(),
                rootPath));

            PanelOwnerMode requestedMode = ReadOwnerMode(rootPath);
            bool mustRecreate = _form == nullptr
                || _form->IsDisposed
                || _hostKind != hostKind
                || _officeHwnd != officeHwnd
                || !String::Equals(_rootPath, rootPath, StringComparison::OrdinalIgnoreCase);

            if (mustRecreate)
            {
                if (ClosePanel() != 0)
                {
                    throw gcnew InvalidOperationException("Existing panel could not be closed.");
                }
                CreatePanel(officeHwnd, rootPath, hostKind, requestedMode);
            }
            else
            {
                _ownerMode = requestedMode;
                ApplyOwnerMode();
            }

            PositionNearOffice(_form, officeHwnd);
            ShowForm();
            ClearError();
            return 0;
        }
        catch (Exception^ ex)
        {
            return Fail(-1, "Could not show RN Assistant panel.", ex, officeHwnd);
        }
    }

    static int ClosePanel()
    {
        try
        {
            if (_form != nullptr && !_form->IsDisposed)
            {
                Log("Closing panel.");
                _form->Close();
            }
            else
            {
                DisposeSession();
                _form = nullptr;
            }

            ClearError();
            return 0;
        }
        catch (Exception^ ex)
        {
            return Fail(-1, "Could not close RN Assistant panel.", ex, _officeHwnd);
        }
    }

    static int SetVisible(bool visible)
    {
        try
        {
            if (_form == nullptr || _form->IsDisposed)
            {
                SetError("Panel has not been created.");
                return -3;
            }

            if (visible)
            {
                PositionNearOffice(_form, _officeHwnd);
                ShowForm();
            }
            else
            {
                _form->Hide();
            }

            ClearError();
            return 0;
        }
        catch (Exception^ ex)
        {
            return Fail(-1, "Could not change panel visibility.", ex, _officeHwnd);
        }
    }

    static String^ LastError()
    {
        return _lastError == nullptr ? String::Empty : _lastError;
    }

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
            String^ candidate = Path::Combine(_rootPath, simpleName + ".dll");
            return File::Exists(candidate) ? Assembly::LoadFrom(candidate) : nullptr;
        }
        catch (Exception^ ex)
        {
            SetError("Managed assembly resolution failed: " + ex);
            return nullptr;
        }
    }

    static void CreatePanel(IntPtr officeHwnd, String^ rootPath, int hostKind, PanelOwnerMode ownerMode)
    {
        try
        {
            String^ assemblyPath = Path::Combine(rootPath, "RNAssistant.OfficeHosts.dll");
            if (!File::Exists(assemblyPath))
            {
                throw gcnew FileNotFoundException("RNAssistant.OfficeHosts.dll was not found.", assemblyPath);
            }

            Assembly^ assembly = Assembly::LoadFrom(assemblyPath);
            Type^ sessionType = assembly->GetType("RNAssistant.OfficeHosts.InProcessPanelSession", true);
            MethodInfo^ createMethod = sessionType->GetMethod(
                "Create",
                BindingFlags::Public | BindingFlags::Static);
            if (createMethod == nullptr)
            {
                throw gcnew MissingMethodException(sessionType->FullName, "Create");
            }

            array<Object^>^ arguments = gcnew array<Object^>(3);
            arguments[0] = hostKind;
            arguments[1] = officeHwnd.ToInt64();
            arguments[2] = rootPath;
            _session = createMethod->Invoke(nullptr, arguments);

            PropertyInfo^ captureProtectionProperty = sessionType->GetProperty("ScreenCaptureProtectionEnabled");
            if (captureProtectionProperty == nullptr)
            {
                throw gcnew MissingMemberException(
                    sessionType->FullName + ".ScreenCaptureProtectionEnabled was not found.");
            }
            _screenCaptureProtectionEnabled = safe_cast<bool>(
                captureProtectionProperty->GetValue(_session, nullptr));

            _screenCaptureProtectionChangedEvent = sessionType->GetEvent("ScreenCaptureProtectionChanged");
            if (_screenCaptureProtectionChangedEvent == nullptr)
            {
                throw gcnew MissingMemberException(
                    sessionType->FullName + ".ScreenCaptureProtectionChanged was not found.");
            }
            _screenCaptureProtectionChangedHandler =
                gcnew Action<bool>(&ManagedPanelHost::OnScreenCaptureProtectionChanged);
            _screenCaptureProtectionChangedEvent->AddEventHandler(
                _session,
                _screenCaptureProtectionChangedHandler);

            PropertyInfo^ panelProperty = sessionType->GetProperty("PanelControl");
            Control^ panel = panelProperty == nullptr
                ? nullptr
                : dynamic_cast<Control^>(panelProperty->GetValue(_session, nullptr));
            if (panel == nullptr)
            {
                throw gcnew InvalidOperationException("Managed panel factory did not return a WinForms control.");
            }

            _form = gcnew Form();
            _form->Text = "RN Assistant";
            _form->Width = 1200;
            _form->Height = 720;
            _form->MinimumSize = System::Drawing::Size(420, 300);
            _form->StartPosition = FormStartPosition::Manual;
            _form->ShowInTaskbar = false;
            _form->FormBorderStyle = FormBorderStyle::Sizable;
            _form->MaximizeBox = true;
            _form->MinimizeBox = false;
            _form->Controls->Add(panel);
            _form->HandleCreated += gcnew EventHandler(&ManagedPanelHost::OnFormHandleCreated);
            _form->FormClosed += gcnew FormClosedEventHandler(&ManagedPanelHost::OnFormClosed);

            _officeHwnd = officeHwnd;
            _hostKind = hostKind;
            _rootPath = rootPath;
            _ownerMode = ownerMode;
            ApplyOwnerMode();
        }
        catch (Exception^)
        {
            if (_form != nullptr && !_form->IsDisposed)
            {
                delete _form;
            }
            _form = nullptr;
            DisposeSession();
            throw;
        }
    }

    static void ApplyOwnerMode()
    {
        if (_form == nullptr || _form->IsDisposed)
        {
            return;
        }

        _form->TopMost = _ownerMode == PanelOwnerMode::TopMostDebug;
    }

    static void ShowForm()
    {
        if (_form->Visible)
        {
            _form->WindowState = FormWindowState::Normal;
        }
        else if (_ownerMode == PanelOwnerMode::OwnerWindow)
        {
            _form->Show(gcnew Win32WindowWrapper(_officeHwnd));
        }
        else
        {
            _form->Show();
        }

        _form->BringToFront();
        _form->Activate();
    }

    static void OnFormHandleCreated(Object^ sender, EventArgs^)
    {
        Form^ form = dynamic_cast<Form^>(sender);
        if (form == nullptr || form->IsDisposed)
        {
            return;
        }

        ApplyScreenCaptureProtection(form);
    }

    static void OnScreenCaptureProtectionChanged(bool enabled)
    {
        _screenCaptureProtectionEnabled = enabled;
        Form^ form = _form;
        if (form == nullptr || form->IsDisposed || !form->IsHandleCreated)
        {
            return;
        }

        if (form->InvokeRequired)
        {
            array<Object^>^ arguments = gcnew array<Object^>(1);
            arguments[0] = enabled;
            form->BeginInvoke(_screenCaptureProtectionChangedHandler, arguments);
            return;
        }

        ApplyScreenCaptureProtection(form);
    }

    static void ApplyScreenCaptureProtection(Form^ form)
    {
        HWND hwnd = static_cast<HWND>(form->Handle.ToPointer());
        DWORD affinity = _screenCaptureProtectionEnabled
            ? WDA_EXCLUDEFROMCAPTURE
            : WDA_NONE;
        String^ affinityName = _screenCaptureProtectionEnabled
            ? "WDA_EXCLUDEFROMCAPTURE"
            : "WDA_NONE";
        SetLastError(ERROR_SUCCESS);
        if (SetWindowDisplayAffinity(hwnd, affinity))
        {
            Log(String::Format(
                "Screen capture protection updated. hwnd={0}, affinity={1}.",
                form->Handle.ToInt64(),
                affinityName));
            return;
        }

        DWORD error = GetLastError();
        Log(String::Format(
            "WARNING: Screen capture protection could not be updated. hwnd={0}, affinity={1}, win32Error={2}.",
            form->Handle.ToInt64(),
            affinityName,
            error));
    }

    static PanelOwnerMode ReadOwnerMode(String^ rootPath)
    {
        String^ value = Environment::GetEnvironmentVariable("RNASSISTANT_PANEL_OWNER_MODE");
        String^ configPath = Path::Combine(rootPath, "panel-owner-mode.txt");
        if (String::IsNullOrWhiteSpace(value) && File::Exists(configPath))
        {
            value = File::ReadAllText(configPath);
        }

        value = String::IsNullOrWhiteSpace(value) ? "OwnerWindow" : value->Trim();
        if (String::Equals(value, "None", StringComparison::OrdinalIgnoreCase))
        {
            return PanelOwnerMode::None;
        }

        if (String::Equals(value, "TopMostDebug", StringComparison::OrdinalIgnoreCase))
        {
            return PanelOwnerMode::TopMostDebug;
        }

        return PanelOwnerMode::OwnerWindow;
    }

    static void PositionNearOffice(Form^ form, IntPtr officeHwnd)
    {
        RECT rect = {};
        HWND hwnd = static_cast<HWND>(officeHwnd.ToPointer());
        if (!GetWindowRect(hwnd, &rect))
        {
            throw gcnew InvalidOperationException("GetWindowRect failed for Office HWND.");
        }

        System::Drawing::Rectangle workingArea =
            Screen::FromHandle(officeHwnd)->WorkingArea;
        const int panelWidth = 1200;
        const int topOffset = 120;
        const int rightMargin = 20;
        const int bottomMargin = 40;

        int width = Math::Min(panelWidth, workingArea.Width);
        int desiredHeight = (rect.bottom - rect.top) - topOffset - bottomMargin;
        int height = Math::Max(300, Math::Min(desiredHeight, workingArea.Height));
        int left = rect.right - width - rightMargin;
        int top = rect.top + topOffset;
        left = Math::Max(workingArea.Left, Math::Min(left, workingArea.Right - width));
        top = Math::Max(workingArea.Top, Math::Min(top, workingArea.Bottom - height));

        form->Bounds = System::Drawing::Rectangle(left, top, width, height);
    }

    static void OnFormClosed(Object^, FormClosedEventArgs^)
    {
        DisposeSession();
        _form = nullptr;
        _officeHwnd = IntPtr::Zero;
        _hostKind = 0;
    }

    static void DisposeSession()
    {
        if (_session != nullptr &&
            _screenCaptureProtectionChangedEvent != nullptr &&
            _screenCaptureProtectionChangedHandler != nullptr)
        {
            try
            {
                _screenCaptureProtectionChangedEvent->RemoveEventHandler(
                    _session,
                    _screenCaptureProtectionChangedHandler);
            }
            catch (Exception^)
            {
            }
        }
        IDisposable^ disposable = dynamic_cast<IDisposable^>(_session);
        if (disposable != nullptr)
        {
            delete disposable;
        }
        _session = nullptr;
        _screenCaptureProtectionChangedEvent = nullptr;
        _screenCaptureProtectionChangedHandler = nullptr;
        _screenCaptureProtectionEnabled = true;
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
