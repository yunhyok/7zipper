#pragma once
#include <windows.h>
#include <objbase.h>
#include <memory>

struct CloseHandleDeleter {
    void operator()(HANDLE handle) const noexcept { CloseHandle(handle); }
};
using Handle = std::unique_ptr<void, CloseHandleDeleter>;

struct FreeTaskMemory {
    void operator()(void* pointer) const noexcept { CoTaskMemFree(pointer); }
};
using TaskString = std::unique_ptr<wchar_t, FreeTaskMemory>;

struct ReleaseInterface {
    void operator()(IUnknown* pointer) const noexcept { pointer->Release(); }
};
template<class T> using ComPtr = std::unique_ptr<T, ReleaseInterface>;

inline HRESULT LastError() noexcept {
    return HRESULT_FROM_WIN32(GetLastError());
}
