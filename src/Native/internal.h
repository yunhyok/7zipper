#pragma once
#define NOMINMAX
#include <windows.h>
#include "sevenzipper.h"

#include <algorithm>
#include <atomic>
#include <climits>
#include <cstdint>
#include <filesystem>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace sz {
namespace fs = std::filesystem;
using W = std::wstring;
using WV = std::wstring_view;
using U64 = uint64_t;
constexpr U64 MiB = 1024 * 1024;
struct Failure { W text; int code; };
[[noreturn]] void fail(W message, int code = SZ_ERROR);
[[noreturn]] void password_error();
[[noreturn]] void windows_error(WV action, WV path);
W long_path(const fs::path& path);
bool safe_name(WV name);
bool safe_attributes(DWORD attributes);
W utf16(std::string_view text, UINT page = CP_UTF8);
std::string utf8(WV text);
U64 filetime(FILETIME value);
FILETIME filetime(U64 value);

class File {
    HANDLE value_ = INVALID_HANDLE_VALUE;
public:
    File() = default;
    File(const File&) = delete;
    File& operator=(const File&) = delete;
    File(File&& other) noexcept : value_(std::exchange(other.value_, INVALID_HANDLE_VALUE)) {}
    File& operator=(File&& other) noexcept { close(); std::swap(value_, other.value_); return *this; }
    ~File() { close(); }
    void close() noexcept { if (*this) CloseHandle(value_); value_ = INVALID_HANDLE_VALUE; }
    explicit operator bool() const { return value_ != INVALID_HANDLE_VALUE; }
    HANDLE get() const { return value_; }
    static File open(const fs::path& path, DWORD access, DWORD creation, DWORD flags = 0);
    static File temporary(const fs::path& dir);
    DWORD read(void* data, DWORD size) const;
    void write(const void* data, DWORD size) const;
    U64 seek(int64_t offset, DWORD origin = FILE_BEGIN) const;
    U64 size() const;
    void resize(U64 size) const;
    BY_HANDLE_FILE_INFORMATION info() const;
    void modified(U64 time) const;
};

struct PathLess { bool operator()(const W& a, const W& b) const; };
struct OwnedFiles {
    std::set<W, PathLess> paths;
    bool keep = false;
    ~OwnedFiles();
    File write_new(const fs::path& path, bool reopen = false);
};

struct Entry {
    W path, method;
    U64 size = 0, packed = 0, time = 0;
    uint32_t index = 0, flags = 0;
    bool directory() const { return flags & SZ_DIRECTORY; }
    bool encrypted() const { return flags & SZ_ENCRYPTED; }
    bool unsafe() const { return flags & SZ_UNSAFE; }
};
struct Input {
    fs::path source;
    W name;
    U64 size, time;
    bool directory;
};
struct Selection {
    const uint32_t* indices;
    uint32_t count;
    bool all() const { return indices == nullptr; }
    bool includes(uint32_t index) const {
        return all() || std::binary_search(indices, indices + count, index);
    }
};
struct Job {
    W stage;
    const wchar_t* password;
    uint32_t threads;
    U64 memory, total = 0;
    sz_progress_cb progress;
    void* context;
    std::mutex callback_lock;
    U64 last = 0;
    std::atomic_bool stopped = false;
    void report(U64 completed, const wchar_t* path = nullptr);
    void check() { report(0); }
};
class Extraction {
    fs::path root_;
    std::map<W, std::pair<bool, bool>, PathLess> paths_;
    std::vector<std::pair<fs::path, U64>> dirs_;
public:
    explicit Extraction(const fs::path& root);
    void validate(const Entry& entry);
    fs::path prepare(const Entry& entry);
    void finish() const;
};
void list_entry(Job& job, sz_entry_cb callback, const Entry& item);
Input get_input(const sz_input& input);
File open_input(const Input& input);
void check_input(const Input& input, const File& file);

void seven_create(Job&, const fs::path&, const std::vector<Input>&, const sz_create_options&);
void seven_read(Job&, const fs::path&, Extraction*, Selection, sz_entry_cb);
void zip_create(Job&, const fs::path&, const std::vector<Input>&, const sz_create_options&);
void zip_read(Job&, const fs::path&, Extraction*, Selection, sz_entry_cb);
}
