#include <windows.h>
#include <shobjidl_core.h>
#include <winternl.h>

#include <atomic>
#include <cstring>
#include <new>
#include <string>
#include <string_view>
#include <vector>

namespace
{
constexpr CLSID WindowCommandClsid =
    {0x9b27df23, 0xb443, 0x4dad, {0xa1, 0x26, 0x29, 0xa2, 0x78, 0x8d, 0x5d, 0xf2}};
constexpr GUID WindowCommandCanonicalName =
    {0x2fabe699, 0x3fc4, 0x4437, {0x89, 0x72, 0x5d, 0x0b, 0xfb, 0x0d, 0x12, 0xbd}};
constexpr CLSID WorkspaceCommandClsid =
    {0x7f24896e, 0x79f7, 0x44f3, {0xb5, 0xf7, 0x7a, 0x3e, 0x62, 0x02, 0x49, 0x6b}};
constexpr GUID WorkspaceCommandCanonicalName =
    {0x2144f91c, 0x0bfe, 0x40d5, {0x8f, 0x6d, 0xa3, 0x01, 0xb7, 0xc0, 0x21, 0x93}};
constexpr wchar_t StateKey[] = L"Software\\cmux\\ShellIntegration";
constexpr wchar_t StateValue[] = L"Enabled";

HINSTANCE moduleInstance = nullptr;
std::atomic<long> objectCount = 0;
std::atomic<long> serverLocks = 0;

enum class LaunchMode
{
    Window,
    Workspace,
};

HRESULT CopyString(std::wstring_view value, PWSTR* result)
{
    if (result == nullptr)
    {
        return E_POINTER;
    }

    *result = nullptr;
    const auto bytes = (value.size() + 1) * sizeof(wchar_t);
    auto* copy = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
    if (copy == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    std::memcpy(copy, value.data(), value.size() * sizeof(wchar_t));
    copy[value.size()] = L'\0';
    *result = copy;
    return S_OK;
}

bool IsWindows11OrGreater()
{
    const auto ntdll = GetModuleHandleW(L"ntdll.dll");
    if (ntdll == nullptr)
    {
        return false;
    }

    using RtlGetVersionFunction = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    const auto rtlGetVersion = reinterpret_cast<RtlGetVersionFunction>(
        GetProcAddress(ntdll, "RtlGetVersion"));
    if (rtlGetVersion == nullptr)
    {
        return false;
    }

    RTL_OSVERSIONINFOW version{};
    version.dwOSVersionInfoSize = sizeof(version);
    return rtlGetVersion(&version) == 0
        && (version.dwMajorVersion > 10
            || (version.dwMajorVersion == 10 && version.dwBuildNumber >= 22000));
}

bool IsEnabled()
{
    DWORD enabled = 0;
    DWORD size = sizeof(enabled);
    return RegGetValueW(
               HKEY_CURRENT_USER,
               StateKey,
               StateValue,
               RRF_RT_REG_DWORD,
               nullptr,
               &enabled,
               &size) == ERROR_SUCCESS
        && enabled != 0;
}

std::wstring CurrentLocaleName()
{
    wchar_t localeName[LOCALE_NAME_MAX_LENGTH]{};
    if (GetUserDefaultLocaleName(localeName, LOCALE_NAME_MAX_LENGTH) == 0)
    {
        return {};
    }
    return localeName;
}

bool UsesChineseUi()
{
    const auto locale = CurrentLocaleName();
    return locale.size() >= 2
        && (locale[0] == L'z' || locale[0] == L'Z')
        && (locale[1] == L'h' || locale[1] == L'H');
}

std::wstring ModuleDirectory()
{
    std::vector<wchar_t> buffer(512);
    while (true)
    {
        const auto length = GetModuleFileNameW(
            moduleInstance,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (length == 0)
        {
            return {};
        }
        if (length < buffer.size() - 1)
        {
            std::wstring path(buffer.data(), length);
            const auto separator = path.find_last_of(L"\\/");
            return separator == std::wstring::npos ? std::wstring{} : path.substr(0, separator);
        }
        buffer.resize(buffer.size() * 2);
    }
}

std::wstring ExecutablePath()
{
    auto directory = ModuleDirectory();
    if (directory.empty())
    {
        return {};
    }
    directory += L"\\CmuxGui.exe";
    return directory;
}

HRESULT FolderFromSelection(IShellItemArray* selection, std::wstring& folder)
{
    if (selection == nullptr)
    {
        return E_INVALIDARG;
    }

    IShellItem* item = nullptr;
    auto result = selection->GetItemAt(0, &item);
    if (FAILED(result))
    {
        return result;
    }

    PWSTR path = nullptr;
    result = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
    item->Release();
    if (FAILED(result))
    {
        return result;
    }

    folder.assign(path);
    CoTaskMemFree(path);
    const auto attributes = GetFileAttributesW(folder.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
    {
        return HRESULT_FROM_WIN32(ERROR_DIRECTORY);
    }
    return S_OK;
}

std::wstring QuoteArgument(std::wstring_view argument)
{
    std::wstring quoted;
    quoted.reserve(argument.size() + 2);
    quoted.push_back(L'"');

    size_t backslashes = 0;
    for (const auto character : argument)
    {
        if (character == L'\\')
        {
            ++backslashes;
            continue;
        }

        if (character == L'"')
        {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(L'"');
        }
        else
        {
            quoted.append(backslashes, L'\\');
            quoted.push_back(character);
        }
        backslashes = 0;
    }

    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'"');
    return quoted;
}

HRESULT LaunchCmux(const std::wstring& folder, LaunchMode mode)
{
    const auto executable = ExecutablePath();
    if (executable.empty() || GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    const auto argument = mode == LaunchMode::Window ? L" --new-window " : L" --new-workspace ";
    auto commandLine = QuoteArgument(executable) + argument + QuoteArgument(folder);
    std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back(L'\0');

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    PROCESS_INFORMATION processInfo{};
    if (!CreateProcessW(
            executable.c_str(),
            mutableCommandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_UNICODE_ENVIRONMENT,
            nullptr,
            nullptr,
            &startupInfo,
            &processInfo))
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);
    return S_OK;
}

class ExplorerCommand final : public IExplorerCommand
{
public:
    explicit ExplorerCommand(LaunchMode mode) : mode_(mode)
    {
        ++objectCount;
    }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }
        *object = nullptr;

        if (interfaceId == IID_IUnknown || interfaceId == IID_IExplorerCommand)
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(++references_);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const auto references = --references_;
        if (references == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(references);
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    {
        if (mode_ == LaunchMode::Window)
        {
            return CopyString(
                UsesChineseUi() ? L"在 cmux 新窗口中打开" : L"Open in new cmux window",
                title);
        }
        return CopyString(
            UsesChineseUi() ? L"在 cmux 新工作区中打开" : L"Open in new cmux workspace",
            title);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        const auto executable = ExecutablePath();
        return executable.empty() ? E_FAIL : CopyString(executable, icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* toolTip) override
    {
        if (mode_ == LaunchMode::Window)
        {
            return CopyString(
                UsesChineseUi() ? L"在新的 cmux 窗口中打开此文件夹" : L"Open this folder in a new cmux window",
                toolTip);
        }
        return CopyString(
            UsesChineseUi() ? L"在新的 cmux 工作区中打开此文件夹" : L"Open this folder in a new cmux workspace",
            toolTip);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) override
    {
        if (canonicalName == nullptr)
        {
            return E_POINTER;
        }
        *canonicalName = mode_ == LaunchMode::Window
            ? WindowCommandCanonicalName
            : WorkspaceCommandCanonicalName;
        return S_OK;
    }

    IFACEMETHODIMP GetState(
        IShellItemArray* selection,
        BOOL,
        EXPCMDSTATE* state) override
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        if (!IsWindows11OrGreater() || !IsEnabled())
        {
            *state = ECS_HIDDEN;
            return S_OK;
        }

        std::wstring folder;
        *state = SUCCEEDED(FolderFromSelection(selection, folder)) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) override
    {
        std::wstring folder;
        const auto result = FolderFromSelection(selection, folder);
        return FAILED(result) ? result : LaunchCmux(folder, mode_);
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr)
        {
            return E_POINTER;
        }
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands != nullptr)
        {
            *commands = nullptr;
        }
        return E_NOTIMPL;
    }

private:
    ~ExplorerCommand()
    {
        --objectCount;
    }

    const LaunchMode mode_;
    std::atomic<long> references_ = 1;
};

class CommandFactory final : public IClassFactory
{
public:
    explicit CommandFactory(LaunchMode mode) : mode_(mode)
    {
        ++objectCount;
    }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }
        *object = nullptr;

        if (interfaceId == IID_IUnknown || interfaceId == IID_IClassFactory)
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(++references_);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const auto references = --references_;
        if (references == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(references);
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID interfaceId, void** object) override
    {
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto* command = new (std::nothrow) ExplorerCommand(mode_);
        if (command == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        const auto result = command->QueryInterface(interfaceId, object);
        command->Release();
        return result;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
        {
            ++serverLocks;
        }
        else
        {
            --serverLocks;
        }
        return S_OK;
    }

private:
    ~CommandFactory()
    {
        --objectCount;
    }

    const LaunchMode mode_;
    std::atomic<long> references_ = 1;
};
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        moduleInstance = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    void** object)
{
    LaunchMode mode;
    if (classId == WindowCommandClsid)
    {
        mode = LaunchMode::Window;
    }
    else if (classId == WorkspaceCommandClsid)
    {
        mode = LaunchMode::Workspace;
    }
    else
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) CommandFactory(mode);
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const auto result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    return objectCount == 0 && serverLocks == 0 ? S_OK : S_FALSE;
}
