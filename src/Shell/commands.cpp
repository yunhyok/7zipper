#include "windows_raii.h"
#include <shobjidl.h>
#include <shlwapi.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cwchar>
#include <new>
#include <string>
#include <string_view>

namespace {
constexpr CLSID commandClsid{0x9df247fa,0x3897,0x4193,{0x8f,0x57,0x58,0x06,0x20,0x56,0x86,0x2d}};
constexpr size_t maxRequestBytes = 16 * 1024 * 1024;
HMODULE module;
std::atomic<long> objects, serverLocks, pendingWrites;

enum CommandId { menu, sevenZip, zip, options, extractHere, extractSmart, commandCount };
constexpr struct { const wchar_t* title; const char* operation; } commands[] = {
    { L"7Zipper", nullptr },
    { L"7z로 압축", "compress-7z" },
    { L"ZIP으로 압축", "compress-zip" },
    { L"압축 옵션…", "compress-options" },
    { L"여기에 풀기", "extract-here" },
    { L"스마트 풀기", "extract-smart" },
};

std::wstring ModulePath() {
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length == path.size()) return {};
    path.resize(length);
    return path;
}

bool IsArchivePath(const wchar_t* path) noexcept {
    const auto ext = std::wcsrchr(path, L'.');
    const auto slash = std::wcsrchr(path, L'\\');
    if (!ext || (slash && ext < slash)) return false;
    for (auto supported : {L".7z", L".zip"}) {
        if (CompareStringOrdinal(ext, -1, supported, -1, TRUE) == CSTR_EQUAL) return true;
    }
    if (ext - path >= 3 && CompareStringOrdinal(ext - 3, -1, L".7z.001", -1, TRUE) == CSTR_EQUAL)
        return true;
    if (ext[0] != L'.' || (ext[1] != L'z' && ext[1] != L'Z')) return false;
    const wchar_t* end = ext + 2;
    while (*end >= L'0' && *end <= L'9') ++end;
    return !*end && end - ext >= 4;
}

HRESULT AppendPath(std::string& request, std::wstring_view path) {
    if (path.empty() || path.size() >= 32768) return E_INVALIDARG;
    const int length = static_cast<int>(path.size());
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path.data(), length,
                                         nullptr, 0, nullptr, nullptr);
    if (!size) return LastError();
    std::string utf8(static_cast<size_t>(size), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path.data(), length, utf8.data(), size,
                             nullptr, nullptr)) return LastError();

    constexpr char hex[] = "0123456789abcdef";
    request += '"';
    for (unsigned char ch : utf8) {
        if (ch == '"' || ch == '\\') request += '\\';
        if (ch < 0x20) {
            request += "\\u00";
            request += hex[ch >> 4];
            request += hex[ch & 15];
        } else {
            request += static_cast<char>(ch);
        }
        if (request.size() > maxRequestBytes - 3)
            return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    }
    request += '"';
    return S_OK;
}

struct PipeRequest {
    Handle writer;
    std::string payload;
    HMODULE keepModuleLoaded = nullptr;
};

DWORD WINAPI WriteRequest(void* context) noexcept {
    HMODULE keptModule;
    {
        std::unique_ptr<PipeRequest> request(static_cast<PipeRequest*>(context));
        keptModule = request->keepModuleLoaded;
        size_t sent = 0;
        while (sent < request->payload.size()) {
            DWORD written = 0;
            const auto size = static_cast<DWORD>(request->payload.size() - sent);
            if (!WriteFile(request->writer.get(), request->payload.data() + sent, size, &written, nullptr) ||
                !written) break;
            sent += written;
        }
    }
    --pendingWrites;
    FreeLibraryAndExitThread(keptModule, 0);
}

struct DeleteAttributes {
    void operator()(LPPROC_THREAD_ATTRIBUTE_LIST list) const noexcept { DeleteProcThreadAttributeList(list); }
};

HRESULT Launch(std::string payload) {
    const std::wstring path = ModulePath();
    const auto slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos) return E_UNEXPECTED;
    const auto directory = path.substr(0, slash + 1);
    const auto application = directory + L"7Zipper.exe";

    HANDLE readEndRaw, writeEndRaw;
    if (!CreatePipe(&readEndRaw, &writeEndRaw, nullptr, 0)) return LastError();
    Handle readEnd(readEndRaw);
    auto request = std::make_unique<PipeRequest>(Handle(writeEndRaw), std::move(payload));
    if (!SetHandleInformation(readEnd.get(), HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) return LastError();
    auto commandLine = L"\"" + application + L"\" --shell-request " +
                       std::to_wstring(reinterpret_cast<uintptr_t>(readEnd.get()));

    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    if (!bytes) return LastError();
    auto buffer = std::make_unique<std::byte[]>(bytes);
    auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(buffer.get());
    if (!InitializeProcThreadAttributeList(list, 1, 0, &bytes)) return LastError();
    std::unique_ptr<_PROC_THREAD_ATTRIBUTE_LIST, DeleteAttributes> attributes(list);
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                   &readEndRaw, sizeof(readEndRaw), nullptr, nullptr)) return LastError();
    STARTUPINFOEXW info{};
    info.StartupInfo.cb = sizeof(info);
    info.lpAttributeList = list;
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(application.c_str(), commandLine.data(), nullptr, nullptr, TRUE,
                        EXTENDED_STARTUPINFO_PRESENT, nullptr, directory.c_str(), &info.StartupInfo, &process))
        return LastError();
    Handle child(process.hProcess), initialThread(process.hThread);
    readEnd.reset();

    HMODULE keep;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(&WriteRequest), &keep)) return LastError();
    request->keepModuleLoaded = keep;
    auto parameter = request.release();
    ++pendingWrites;
    Handle writerThread(CreateThread(nullptr, 0, WriteRequest, parameter, 0, nullptr));
    if (!writerThread) {
        const HRESULT error = LastError();
        --pendingWrites;
        delete parameter;
        FreeLibrary(keep);
        return error;
    }
    return S_OK;
}

class Command final : public IExplorerCommand {
public:
    explicit Command(CommandId id) noexcept : id_(id) { ++objects; }
    ~Command() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IExplorerCommand)) return E_NOINTERFACE;
        *result = static_cast<IExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, LPWSTR* result) override {
        return SHStrDupW(commands[id_].title, result);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, LPWSTR* result) override {
        *result = nullptr;
        if (id_ != menu) return E_NOTIMPL;
        try {
            auto path = ModulePath();
            const auto slash = path.find_last_of(L'\\');
            if (slash == std::wstring::npos) return E_FAIL;
            path.replace(slash + 1, std::wstring::npos, L"7zipper.ico");
            return SHStrDupW(path.c_str(), result);
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, LPWSTR* result) override {
        *result = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* result) override {
        *result = commandClsid;
        result->Data1 += static_cast<unsigned>(id_);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* result) override {
        *result = ECS_HIDDEN;
        DWORD count;
        if (!items || FAILED(items->GetCount(&count)) || !count) return S_OK;
        if (id_ == extractHere || id_ == extractSmart) {
            // Filesystem attributes and extension only; no archive is opened inside Explorer.
            for (DWORD index = 0; index < count; ++index) {
                IShellItem* rawItem = nullptr;
                if (FAILED(items->GetItemAt(index, &rawItem))) return S_OK;
                ComPtr<IShellItem> item(rawItem);
                wchar_t* rawPath = nullptr;
                if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath))) return S_OK;
                TaskString path(rawPath);
                if (!IsArchivePath(path.get())) return S_OK;
                SFGAOF attrs;
                if (FAILED(item->GetAttributes(SFGAO_FOLDER, &attrs)) || (attrs & SFGAO_FOLDER)) return S_OK;
            }
        }
        *result = ECS_ENABLED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override {
        if (id_ == menu) return E_NOTIMPL;
        DWORD count;
        if (!items || FAILED(items->GetCount(&count)) || !count) return E_INVALIDARG;
        try {
            std::string request = std::string(R"({"operation":")") + commands[id_].operation + R"(","paths":[)";
            for (DWORD index = 0; index < count; ++index) {
                IShellItem* rawItem = nullptr;
                HRESULT hr = items->GetItemAt(index, &rawItem);
                if (FAILED(hr)) return hr;
                ComPtr<IShellItem> item(rawItem);
                wchar_t* rawPath = nullptr;
                hr = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
                if (FAILED(hr)) return hr;
                TaskString path(rawPath);
                if (index) request += ',';
                hr = AppendPath(request, path.get());
                if (FAILED(hr)) return hr;
            }
            request += "]}";
            return Launch(std::move(request));
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* result) override {
        *result = id_ == menu ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** result) override;
private:
    CommandId id_;
    std::atomic<ULONG> refs_{1};
};

class Commands final : public IEnumExplorerCommand {
public:
    explicit Commands(unsigned index = sevenZip) noexcept : index_(index) { ++objects; }
    ~Commands() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IEnumExplorerCommand)) return E_NOINTERFACE;
        *result = static_cast<IEnumExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** result, ULONG* fetched) override {
        if (!result || (!fetched && count != 1)) return E_POINTER;
        if (fetched) *fetched = 0;
        ULONG found = 0;
        for (; found < count && index_ < commandCount; ++index_, ++found) {
            result[found] = new (std::nothrow) Command(static_cast<CommandId>(index_));
            if (!result[found]) {
                index_ -= found;
                while (found) result[--found]->Release();
                return E_OUTOFMEMORY;
            }
        }
        if (fetched) *fetched = found;
        return found == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {
        const auto available = static_cast<unsigned>(commandCount) - index_;
        index_ += std::min(count, static_cast<ULONG>(available));
        return count <= available ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { index_ = sevenZip; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** result) override {
        *result = new (std::nothrow) Commands(index_);
        return *result ? S_OK : E_OUTOFMEMORY;
    }
private:
    unsigned index_;
    std::atomic<ULONG> refs_{1};
};

HRESULT Command::EnumSubCommands(IEnumExplorerCommand** result) {
    *result = nullptr;
    if (id_ != menu) return E_NOTIMPL;
    *result = new (std::nothrow) Commands;
    return *result ? S_OK : E_OUTOFMEMORY;
}

class Factory final : public IClassFactory {
public:
    Factory() noexcept { ++objects; }
    ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ComPtr<Command> root(new (std::nothrow) Command(menu));
        return root ? root->QueryInterface(iid, result) : E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) ++serverLocks;
        else --serverLocks;
        return S_OK;
    }
private:
    std::atomic<ULONG> refs_{1};
};
} // namespace

LONG ActiveDrags() noexcept;

BOOL WINAPI DllMain(HMODULE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}

STDAPI DllCanUnloadNow() {
    return objects || serverLocks || pendingWrites || ActiveDrags() ? S_FALSE : S_OK;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != commandClsid) return CLASS_E_CLASSNOTAVAILABLE;
    ComPtr<Factory> factory(new (std::nothrow) Factory);
    return factory ? factory->QueryInterface(iid, result) : E_OUTOFMEMORY;
}
