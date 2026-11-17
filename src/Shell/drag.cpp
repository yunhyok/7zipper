#include "windows_raii.h"
#include <shlobj.h>
#include <winternl.h>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <limits>
#include <new>
#include <string>
#include <string_view>
#include <vector>

namespace {
std::atomic<LONG> activeDrags;
thread_local bool oleHeld = false;

void ReleaseIdleOle() noexcept {
    // The STA keeps pumping messages for Explorer's async proxy after DoDragDrop returns.
    if (oleHeld && !activeDrags) {
        oleHeld = false;
        OleUninitialize();
    }
}

bool StartsWith(std::wstring_view value, std::wstring_view prefix) noexcept {
    return value.size() >= prefix.size() &&
           CompareStringOrdinal(value.data(), static_cast<int>(prefix.size()), prefix.data(),
                                static_cast<int>(prefix.size()), TRUE) == CSTR_EQUAL;
}

bool GuidName(std::wstring_view value) noexcept {
    if (value.size() != 32) return false;
    for (auto ch : value)
        if (!(ch >= L'0' && ch <= L'9') && !(ch >= L'a' && ch <= L'f') && !(ch >= L'A' && ch <= L'F'))
            return false;
    return true;
}

HRESULT OpenRelative(HANDLE parent, std::wstring_view name, ACCESS_MASK access, Handle& result,
                     bool* directory = nullptr) noexcept {
    if (name.empty() || name.size() > 255 || name == L"." || name == L".." ||
        name.find_first_of(L"\\/:") != std::wstring_view::npos) return E_INVALIDARG;
    auto length = static_cast<USHORT>(name.size() * sizeof(wchar_t));
    UNICODE_STRING path{length, length, const_cast<wchar_t*>(name.data())};
    OBJECT_ATTRIBUTES attrs;
    InitializeObjectAttributes(&attrs, &path, OBJ_CASE_INSENSITIVE, parent, nullptr);
    IO_STATUS_BLOCK status{};
    HANDLE raw;
    const auto ntStatus = NtOpenFile(&raw, access | FILE_READ_ATTRIBUTES | SYNCHRONIZE, &attrs, &status,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE,
                                     FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT);
    if (!NT_SUCCESS(ntStatus)) return HRESULT_FROM_WIN32(RtlNtStatusToDosError(ntStatus));
    Handle node(raw);
    FILE_ATTRIBUTE_TAG_INFO info;
    if (!GetFileInformationByHandleEx(raw, FileAttributeTagInfo, &info, sizeof(info))) return LastError();
    if (info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) return HRESULT_FROM_WIN32(ERROR_REPARSE_TAG_INVALID);
    if (directory) {
        *directory = (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    } else if (!(info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
        return HRESULT_FROM_WIN32(ERROR_DIRECTORY);
    }
    result = std::move(node);
    return S_OK;
}

bool DeleteNode(HANDLE handle) noexcept {
    FILE_DISPOSITION_INFO_EX info{FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                                  FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE};
    if (SetFileInformationByHandle(handle, FileDispositionInfoEx, &info, sizeof(info))) return true;
    FILE_DISPOSITION_INFO legacy{TRUE};
    return SetFileInformationByHandle(handle, FileDispositionInfo, &legacy, sizeof(legacy)) != FALSE;
}

struct FolderCursor {
    Handle folder;
    alignas(FILE_ID_BOTH_DIR_INFO) std::array<std::byte, 4096> entries;
    size_t offset = 0;
    bool restart = true;
    bool hasEntries = false;
};

void DeleteTree(Handle root) {
    // All opens are relative to a pinned directory; a rename or junction cannot redirect a deletion.
    std::vector<FolderCursor> folders;
    folders.emplace_back(std::move(root));
    while (!folders.empty()) {
        auto& current = folders.back();
        if (!current.hasEntries) {
            auto mode = current.restart ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo;
            if (!GetFileInformationByHandleEx(current.folder.get(), mode, current.entries.data(),
                                               static_cast<DWORD>(current.entries.size()))) {
                if (GetLastError() != ERROR_NO_MORE_FILES || !DeleteNode(current.folder.get())) return;
                folders.pop_back();
                continue;
            }
            current.restart = false;
            current.offset = 0;
            current.hasEntries = true;
        }
        const auto entry = reinterpret_cast<const FILE_ID_BOTH_DIR_INFO*>(current.entries.data() + current.offset);
        const std::wstring_view name(entry->FileName, entry->FileNameLength / sizeof(wchar_t));
        current.hasEntries = entry->NextEntryOffset != 0;
        current.offset += entry->NextEntryOffset;
        if (name == L"." || name == L"..") continue;

        Handle child;
        bool directory;
        if (FAILED(OpenRelative(current.folder.get(), name, DELETE | FILE_LIST_DIRECTORY, child, &directory)))
            return;
        if (directory) folders.emplace_back(std::move(child));
        else if (!DeleteNode(child.get())) return;
    }
}

class Staging {
public:
    Staging() = default;
    Staging(Staging&&) = default;
    ~Staging() {
        if (!job_ || !clean_) return;
        try {
            FILE_ID_INFO expected, actual;
            const bool identified = GetFileInformationByHandleEx(job_.get(), FileIdInfo, &expected, sizeof(expected));
            job_.reset();
            Handle deleting;
            if (identified && SUCCEEDED(OpenRelative(drag_.get(), name_, DELETE | FILE_LIST_DIRECTORY, deleting)) &&
                GetFileInformationByHandleEx(deleting.get(), FileIdInfo, &actual, sizeof(actual)) &&
                actual.VolumeSerialNumber == expected.VolumeSerialNumber &&
                std::memcmp(actual.FileId.Identifier, expected.FileId.Identifier, sizeof(expected.FileId.Identifier)) == 0)
                DeleteTree(std::move(deleting));
        } catch (...) {
            // Leave leftovers for manual cleanup after reboot rather than follow a replacement path.
        }
    }

    HRESULT Open(std::wstring_view path) {
        wchar_t* rawLocal = nullptr;
        HRESULT hr = SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &rawLocal);
        TaskString local(rawLocal);
        if (FAILED(hr)) return hr;
        std::wstring base = std::wstring(local.get()) + L"\\7Zipper\\Drag\\";
        if (!StartsWith(path, base) || !GuidName(path.substr(base.size()))) return E_INVALIDARG;
        name_ = path.substr(base.size());
        prefix_ = std::wstring(path) + L'\\';
        auto raw = CreateFileW(local.get(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (raw == INVALID_HANDLE_VALUE) return LastError();
        local_.reset(raw);
        FILE_ATTRIBUTE_TAG_INFO attrs;
        if (!GetFileInformationByHandleEx(raw, FileAttributeTagInfo, &attrs, sizeof(attrs))) return LastError();
        if ((attrs.FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != FILE_ATTRIBUTE_DIRECTORY)
            return E_INVALIDARG;
        if (FAILED(hr = OpenRelative(local_.get(), L"7Zipper", 0, product_))) return hr;
        if (FAILED(hr = OpenRelative(product_.get(), L"Drag", 0, drag_))) return hr;
        return OpenRelative(drag_.get(), name_, 0, job_);
    }

    HRESULT CheckPath(std::wstring_view path) const noexcept {
        if (path.size() >= 32768 || !StartsWith(path, prefix_)) return E_INVALIDARG;
        path.remove_prefix(prefix_.size());
        HANDLE current = job_.get();
        Handle owned;
        for (;;) {
            const auto slash = path.find(L'\\');
            Handle child;
            bool directory;
            const HRESULT hr = OpenRelative(current, path.substr(0, slash), 0, child,
                                            slash == std::wstring_view::npos ? &directory : nullptr);
            if (FAILED(hr) || slash == std::wstring_view::npos) return hr;
            owned = std::move(child);
            current = owned.get();
            path.remove_prefix(slash + 1);
        }
    }
    void Preserve() noexcept { clean_ = false; }

private:
    Handle local_, product_, drag_, job_;
    std::wstring name_, prefix_;
    bool clean_ = true;
};

class Medium final : public IUnknown {
public:
    Medium(HGLOBAL data, IDataObject* owner) noexcept : data_(data), owner_(owner) { owner_->AddRef(); }
    ~Medium() { GlobalFree(data_); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != __uuidof(IUnknown)) return E_NOINTERFACE;
        *output = static_cast<IUnknown*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
private:
    HGLOBAL data_;
    ComPtr<IDataObject> owner_;
    std::atomic<ULONG> refs_{1};
};

struct DragActivity {
    DragActivity() noexcept { ++activeDrags; }
    ~DragActivity() { --activeDrags; }
};

class DataObject final : public IDataObject, public IDataObjectAsyncCapability {
public:
    DataObject(std::vector<std::byte> drop, Staging stage) noexcept
        : stage_(std::move(stage)), drop_(std::move(drop)) {}
    ~DataObject() {
        if (!knownOutcome_ || transfer_ == failed || (transfer_ == running && !performedCopy_)) stage_.Preserve();
    }

    void SetOutcome(HRESULT hr, DWORD effect) noexcept {
        knownOutcome_ = hr == DRAGDROP_S_CANCEL ||
                        (hr == DRAGDROP_S_DROP && (effect & ~DROPEFFECT_COPY) == 0);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IDataObject)) *output = static_cast<IDataObject*>(this);
        else if (iid == __uuidof(IDataObjectAsyncCapability)) *output = static_cast<IDataObjectAsyncCapability*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetData(FORMATETC* format, STGMEDIUM* result) override {
        if (!result) return E_POINTER;
        *result = {};
        const auto hr = QueryGetData(format);
        if (FAILED(hr)) return hr;
        constexpr DWORD copy = DROPEFFECT_COPY;
        const bool hdrop = format->cfFormat == CF_HDROP;
        const size_t bytes = hdrop ? drop_.size() : sizeof(copy);
        const void* data = hdrop ? static_cast<const void*>(drop_.data()) : &copy;
        const auto global = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (!global) return E_OUTOFMEMORY;
        auto pointer = GlobalLock(global);
        if (!pointer) { GlobalFree(global); return E_OUTOFMEMORY; }
        std::memcpy(pointer, data, bytes);
        GlobalUnlock(global);
        auto release = new (std::nothrow) Medium(global, this);
        if (!release) { GlobalFree(global); return E_OUTOFMEMORY; }
        result->tymed = TYMED_HGLOBAL;
        result->hGlobal = global;
        result->pUnkForRelease = release;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetDataHere(FORMATETC*, STGMEDIUM*) override { return DATA_E_FORMATETC; }
    HRESULT STDMETHODCALLTYPE QueryGetData(FORMATETC* format) override {
        if (!format) return E_POINTER;
        if (format->cfFormat != CF_HDROP && (!preferred_ || format->cfFormat != preferred_)) return DV_E_FORMATETC;
        if (format->dwAspect != DVASPECT_CONTENT) return DV_E_DVASPECT;
        if (format->lindex != -1) return DV_E_LINDEX;
        if (!(format->tymed & TYMED_HGLOBAL)) return DV_E_TYMED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalFormatEtc(FORMATETC*, FORMATETC* result) override {
        if (!result) return E_POINTER;
        *result = {};
        return DATA_S_SAMEFORMATETC;
    }
    HRESULT STDMETHODCALLTYPE SetData(FORMATETC* format, STGMEDIUM* medium, BOOL release) override {
        if (!format || !medium) return E_POINTER;
        if (!format->cfFormat || (format->cfFormat != performed_ && format->cfFormat != pasteSucceeded_))
            return DV_E_FORMATETC;
        if (format->dwAspect != DVASPECT_CONTENT) return DV_E_DVASPECT;
        if (format->lindex != -1) return DV_E_LINDEX;
        if (!(format->tymed & TYMED_HGLOBAL) || medium->tymed != TYMED_HGLOBAL) return DV_E_TYMED;
        if (!medium->hGlobal || GlobalSize(medium->hGlobal) < sizeof(DWORD)) return DV_E_STGMEDIUM;
        const auto value = GlobalLock(medium->hGlobal);
        if (!value) return DV_E_STGMEDIUM;
        DWORD effect;
        std::memcpy(&effect, value, sizeof(effect));
        GlobalUnlock(medium->hGlobal);
        // Explorer may report the completed copy here without calling EndOperation.
        if (format->cfFormat == performed_) performedCopy_ = effect == DROPEFFECT_COPY;
        if (release) ReleaseStgMedium(medium);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumFormatEtc(DWORD direction, IEnumFORMATETC** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (direction != DATADIR_GET) return E_NOTIMPL;
        FORMATETC formats[] = {{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL},
                               {preferred_, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL}};
        return SHCreateStdEnumFmtEtc(preferred_ ? 2 : 1, formats, result);
    }
    HRESULT STDMETHODCALLTYPE DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return OLE_E_ADVISENOTSUPPORTED; }
    HRESULT STDMETHODCALLTYPE DUnadvise(DWORD) override { return OLE_E_ADVISENOTSUPPORTED; }
    HRESULT STDMETHODCALLTYPE EnumDAdvise(IEnumSTATDATA**) override { return OLE_E_ADVISENOTSUPPORTED; }

    HRESULT STDMETHODCALLTYPE SetAsyncMode(BOOL value) override { async_ = value != FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAsyncMode(BOOL* value) override {
        if (!value) return E_POINTER;
        *value = async_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE StartOperation(IBindCtx*) override {
        Transfer expected = idle;
        return transfer_.compare_exchange_strong(expected, running) ? S_OK : E_UNEXPECTED;
    }
    HRESULT STDMETHODCALLTYPE InOperation(BOOL* value) override {
        if (!value) return E_POINTER;
        *value = transfer_ == running;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EndOperation(HRESULT result, IBindCtx*, DWORD effect) override {
        Transfer expected = running;
        return transfer_.compare_exchange_strong(expected, SUCCEEDED(result) && (effect & DROPEFFECT_COPY) ? complete : failed)
             ? S_OK : E_UNEXPECTED;
    }

private:
    enum Transfer { idle, running, complete, failed };
    DragActivity activity_;
    Staging stage_;
    std::vector<std::byte> drop_;
    std::atomic<ULONG> refs_{1};
    std::atomic<Transfer> transfer_{idle};
    std::atomic<bool> async_{false}, knownOutcome_{false}, performedCopy_{false};
    CLIPFORMAT preferred_ = static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_PREFERREDDROPEFFECT));
    CLIPFORMAT performed_ = static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_PERFORMEDDROPEFFECT));
    CLIPFORMAT pasteSucceeded_ = static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_PASTESUCCEEDED));
};

class Source final : public IDropSource {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IDropSource)) return E_NOINTERFACE;
        *output = static_cast<IDropSource*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --refs_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE QueryContinueDrag(BOOL escape, DWORD keys) override {
        if (escape || (keys & MK_RBUTTON)) return DRAGDROP_S_CANCEL;
        return (keys & MK_LBUTTON) ? S_OK : DRAGDROP_S_DROP;
    }
    HRESULT STDMETHODCALLTYPE GiveFeedback(DWORD) override { return DRAGDROP_S_USEDEFAULTCURSORS; }
private:
    std::atomic<ULONG> refs_{1};
};

HRESULT BuildDrop(const Staging& stage, const wchar_t* const* paths, int count, std::vector<std::byte>& bytes) {
    if (!paths || count <= 0) return E_INVALIDARG;
    DROPFILES header{sizeof(DROPFILES), {}, FALSE, TRUE};
    bytes.resize(sizeof(header));
    std::memcpy(bytes.data(), &header, sizeof(header));
    for (int index = 0; index < count; ++index) {
        const auto path = paths[index];
        if (!path) return E_INVALIDARG;
        std::wstring_view view(path);
        const HRESULT hr = stage.CheckPath(view);
        if (FAILED(hr)) return hr;
        const auto begin = reinterpret_cast<const std::byte*>(path);
        bytes.insert(bytes.end(), begin, begin + (view.size() + 1) * sizeof(wchar_t));
    }
    bytes.resize(bytes.size() + sizeof(wchar_t));
    return S_OK;
}
} // namespace

LONG ActiveDrags() noexcept { return activeDrags; }

extern "C" __declspec(dllexport) int __cdecl sz_drag_active() noexcept {
    ReleaseIdleOle();
    return activeDrags != 0;
}

extern "C" __declspec(dllexport) int __cdecl sz_drag_files(void* ownerHwnd, const wchar_t* const* paths,
                                                           int count, const wchar_t* stagingDirectory) noexcept {
    if (!stagingDirectory) return E_INVALIDARG;
    struct OleScope { ~OleScope() { ReleaseIdleOle(); } } oleScope;
    try {
        Staging stage;
        HRESULT hr = stage.Open(stagingDirectory);
        if (FAILED(hr)) return hr;
        if (ownerHwnd && GetWindowThreadProcessId(static_cast<HWND>(ownerHwnd), nullptr) != GetCurrentThreadId())
            return RPC_E_WRONG_THREAD;
        if (activeDrags) return HRESULT_FROM_WIN32(ERROR_BUSY);
        // GetAsyncKeyState reads physical buttons; a drag is held with the logical left button.
        const int dragButton = GetSystemMetrics(SM_SWAPBUTTON) ? VK_RBUTTON : VK_LBUTTON;
        if (!(GetAsyncKeyState(dragButton) & 0x8000)) return DRAGDROP_S_CANCEL;
        if (!oleHeld) {
            hr = OleInitialize(nullptr);
            if (FAILED(hr)) return hr;
            oleHeld = true;
        }
        std::vector<std::byte> drop;
        hr = BuildDrop(stage, paths, count, drop);
        if (FAILED(hr)) return hr;
        ComPtr<Source> source(new (std::nothrow) Source);
        if (!source) return E_OUTOFMEMORY;
        auto data = new (std::nothrow) DataObject(std::move(drop), std::move(stage));
        if (!data) return E_OUTOFMEMORY;
        ComPtr<IDataObject> dataRef(data);
        data->SetAsyncMode(TRUE);
        DWORD effect = DROPEFFECT_NONE;
        hr = DoDragDrop(data, source.get(), DROPEFFECT_COPY, &effect);
        effect &= ~DROPEFFECT_SCROLL;
        data->SetOutcome(hr, effect);
        if (hr == DRAGDROP_S_DROP && effect == DROPEFFECT_NONE) return DRAGDROP_S_CANCEL;
        if (hr == DRAGDROP_S_DROP && effect != DROPEFFECT_COPY) return E_UNEXPECTED;
        return hr;
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}
