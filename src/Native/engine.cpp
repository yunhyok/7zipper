#include "internal.h"
#include <objbase.h>
#include <cwctype>

namespace sz {
thread_local W error;

void fail(W message, int code) { throw Failure{std::move(message), code}; }
void password_error() { fail(L"A password is required, or the supplied password is incorrect.", SZ_PASSWORD_REQUIRED); }
void windows_error(WV action, WV path) {
    const auto code = GetLastError();
    fail(W(action) + L": " + W(path) + L" [Windows " + std::to_wstring(code) + L"]");
}
W long_path(const fs::path& path) {
    W value = fs::absolute(path).lexically_normal().native();
    if (value.starts_with(L"\\\\?\\")) return value;
    if (value.starts_with(L"\\\\")) return L"\\\\?\\UNC\\" + value.substr(2);
    return L"\\\\?\\" + value;
}
bool safe_name(WV name) {
    if (name.empty() || name.size() > 31500) return false;
    for (size_t begin = 0;;) {
        const auto end = name.find_first_of(L"/\\", begin);
        const WV part = name.substr(begin, end == WV::npos ? end : end - begin);
        if (part.empty() || part.size() > 255 || part.back() == L'.' || part.back() == L' ') return false;
        for (wchar_t ch : part)
            if (ch < 32 || WV(L"<>:\"|?*").find(ch) != WV::npos) return false;
        auto stem = part.substr(0, part.find(L'.'));
        while (!stem.empty() && stem.back() == L' ') stem.remove_suffix(1);
        W upper(stem);
        for (auto& ch : upper) if (ch >= L'a' && ch <= L'z') ch -= L'a' - L'A';
        if (upper == L"CON" || upper == L"PRN" || upper == L"AUX" || upper == L"NUL" ||
            upper == L"CONIN$" || upper == L"CONOUT$" ||
            (upper.size() == 4 && (upper.starts_with(L"COM") || upper.starts_with(L"LPT")) &&
             WV(L"0123456789\u00b9\u00b2\u00b3").find(upper.back()) != WV::npos)) return false;
        if (end == WV::npos) return true;
        begin = end + 1;
    }
}
bool safe_attributes(DWORD attr) {
    if (attr & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DEVICE)) return false;
    if (!(attr & 0x8000)) return true;
    const auto type = (attr >> 16) & 0170000;
    return type == 0 || type == 0100000 || type == 0040000;
}
W utf16(std::string_view text, UINT page) {
    if (text.empty()) return {};
    if (text.size() > INT_MAX) fail(L"Text is too long.");
    const auto count = static_cast<int>(text.size());
    const DWORD flags = page == CP_UTF8 ? MB_ERR_INVALID_CHARS : 0;
    const auto size = MultiByteToWideChar(page, flags, text.data(), count, nullptr, 0);
    if (!size) fail(L"Invalid archive text encoding.");
    W wide(size, L'\0');
    if (!MultiByteToWideChar(page, flags, text.data(), count, wide.data(), size)) fail(L"Invalid archive text.");
    return wide;
}
std::string utf8(WV text) {
    if (text.empty()) return {};
    if (text.size() > INT_MAX) fail(L"Text is too long.");
    const auto count = static_cast<int>(text.size());
    const auto size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), count, nullptr, 0, nullptr, nullptr);
    if (!size) fail(L"Invalid UTF-16 text.");
    std::string encoded(size, '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), count, encoded.data(), size, nullptr, nullptr))
        fail(L"Invalid UTF-16 text.");
    return encoded;
}
U64 filetime(FILETIME t) { return (static_cast<U64>(t.dwHighDateTime) << 32) | t.dwLowDateTime; }
FILETIME filetime(U64 t) { return {static_cast<DWORD>(t), static_cast<DWORD>(t >> 32)}; }

File File::open(const fs::path& path, DWORD access, DWORD creation, DWORD flags) {
    File result;
    result.value_ = CreateFileW(long_path(path).c_str(), access, FILE_SHARE_READ, nullptr, creation,
        flags | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (!result) windows_error(L"Cannot open file", path.native());
    const auto attrs = result.info().dwFileAttributes;
    if (!safe_attributes(attrs) || ((attrs & FILE_ATTRIBUTE_DIRECTORY) && !(flags & FILE_FLAG_BACKUP_SEMANTICS)))
        fail(L"Unsupported link or special file: " + path.native());
    return result;
}
File File::temporary(const fs::path& dir) {
    GUID id{};
    if (FAILED(CoCreateGuid(&id))) fail(L"Cannot allocate a temporary file name.");
    wchar_t name[40]{};
    StringFromGUID2(id, name, 40);
    return open(dir / name, GENERIC_READ | GENERIC_WRITE | DELETE, CREATE_NEW,
        FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE);
}
DWORD File::read(void* data, DWORD size) const {
    DWORD read = 0;
    if (!ReadFile(value_, data, size, &read, nullptr)) windows_error(L"Read failed", L"");
    return read;
}
void File::write(const void* data, DWORD size) const {
    DWORD written = 0;
    if (!WriteFile(value_, data, size, &written, nullptr)) windows_error(L"Write failed", L"");
    if (written != size) fail(L"Incomplete write.");
}
U64 File::seek(int64_t offset, DWORD origin) const {
    LARGE_INTEGER in{}, out{};
    in.QuadPart = offset;
    if (!SetFilePointerEx(value_, in, &out, origin)) windows_error(L"Seek failed", L"");
    return out.QuadPart;
}
U64 File::size() const {
    LARGE_INTEGER result{};
    if (!GetFileSizeEx(value_, &result)) windows_error(L"Cannot read file size", L"");
    return result.QuadPart;
}
void File::resize(U64 size) const {
    if (size > INT64_MAX) fail(L"Output is too large.");
    seek(static_cast<int64_t>(size));
    if (!SetEndOfFile(value_)) windows_error(L"Cannot resize file", L"");
}
BY_HANDLE_FILE_INFORMATION File::info() const {
    BY_HANDLE_FILE_INFORMATION result{};
    if (!GetFileInformationByHandle(value_, &result)) windows_error(L"Cannot read file information", L"");
    return result;
}
void File::modified(U64 time) const {
    if (!time) return;
    const auto value = filetime(time);
    if (!SetFileTime(value_, nullptr, nullptr, &value)) windows_error(L"Cannot set file timestamp", L"");
}

bool PathLess::operator()(const W& a, const W& b) const {
    return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()), b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_LESS_THAN;
}
OwnedFiles::~OwnedFiles() {
    if (!keep) for (const auto& path : paths) DeleteFileW(path.c_str());
}
File OwnedFiles::write_new(const fs::path& path, bool reopen) {
    const auto qualified = long_path(path);
    if (auto pos = paths.find(qualified); pos != paths.end()) {
        if (!reopen) fail(L"Duplicate archive output: " + path.native());
        return File::open(*pos, GENERIC_READ | GENERIC_WRITE, OPEN_EXISTING);
    }
    const auto [it, inserted] = paths.insert(qualified);
    try { return File::open(*it, GENERIC_READ | GENERIC_WRITE, CREATE_NEW); }
    catch (...) { if (inserted) paths.erase(it); throw; }
}
void Job::report(U64 completed, const wchar_t* path) {
    if (stopped) fail(L"Canceled.", SZ_CANCELED);
    if (!progress) return;
    const std::lock_guard guard(callback_lock);
    last = (std::max)(last, completed);
    if (stopped || !progress(context, stage.c_str(), last, total, path)) {
        stopped = true;
        fail(L"Canceled.", SZ_CANCELED);
    }
}
void list_entry(Job& job, sz_entry_cb callback, const Entry& item) {
    const sz_entry entry{item.index, item.flags, item.path.c_str(), item.method.c_str(), item.size, item.packed, item.time};
    if (!callback(job.context, &entry)) fail(L"Canceled.", SZ_CANCELED);
}
static void valid_directory(const fs::path& where, bool create) {
    const auto full = long_path(where);
    if (create && !CreateDirectoryW(full.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
        windows_error(L"Cannot create directory", where.native());
    const auto attrs = GetFileAttributesW(full.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES) windows_error(L"Cannot inspect directory", where.native());
    if (!(attrs & FILE_ATTRIBUTE_DIRECTORY) || !safe_attributes(attrs))
        fail(L"Expected a normal directory: " + where.native());
}
Extraction::Extraction(const fs::path& root) : root_(fs::absolute(root).lexically_normal()) {
    valid_directory(root_, false);
    if (!fs::is_empty(long_path(root_))) fail(L"Extraction staging directory must be empty.");
}
static W canonical_name(const Entry& item) {
    if (item.unsafe() || !safe_name(item.path)) fail(L"Unsafe archive entry: " + item.path);
    W name(item.path);
    std::replace(name.begin(), name.end(), L'/', L'\\');
    return name;
}
void Extraction::validate(const Entry& item) {
    const auto name = canonical_name(item);
    for (size_t sep = name.find(L'\\'); sep != W::npos; sep = name.find(L'\\', sep + 1)) {
        auto [it, added] = paths_.try_emplace(name.substr(0, sep), true, false);
        if (!it->second.first) fail(L"Archive file conflicts with a directory: " + name);
    }
    auto [it, added] = paths_.try_emplace(name, item.directory(), true);
    if (added) return;
    if (!it->second.second && it->second.first && item.directory()) { it->second.second = true; return; }
    fail(L"Duplicate or conflicting archive path: " + name);
}
fs::path Extraction::prepare(const Entry& item) {
    const W name = canonical_name(item);
    for (size_t sep = name.find(L'\\'); sep != W::npos; sep = name.find(L'\\', sep + 1))
        valid_directory(root_ / name.substr(0, sep), true);
    const auto full = root_ / name;
    if (item.directory()) {
        valid_directory(full, true);
        if (item.time) dirs_.emplace_back(full, item.time);
    }
    return full;
}
void Extraction::finish() const {
    for (const auto& [where, time] : dirs_)
        File::open(where, FILE_WRITE_ATTRIBUTES, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS).modified(time);
}
Input get_input(const sz_input& item) {
    if (!item.source || !*item.source || !item.name || !safe_name(item.name) || (item.flags & ~SZ_DIRECTORY))
        fail(L"Invalid compression input.");
    const bool directory = (item.flags & SZ_DIRECTORY) != 0;
    const auto file = File::open(item.source, FILE_READ_ATTRIBUTES, OPEN_EXISTING,
        directory ? FILE_FLAG_BACKUP_SEMANTICS : 0);
    const auto info = file.info();
    if (directory != !!(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) fail(L"Source type changed.");
    return {item.source, item.name, directory ? 0 : file.size(), filetime(info.ftLastWriteTime), directory};
}
File open_input(const Input& input) {
    auto file = File::open(input.source, GENERIC_READ, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN);
    check_input(input, file);
    return file;
}
void check_input(const Input& input, const File& file) {
    if (file.size() != input.size || filetime(file.info().ftLastWriteTime) != input.time)
        fail(L"Source file changed: " + input.source.native());
}
static U64 memory_limit(U64 value) {
    if (value) {
        if (value < 8 * MiB) fail(L"Memory limit must be at least 8 MiB.");
        return value;
    }
    MEMORYSTATUSEX status{};
    status.dwLength = sizeof(status);
    return GlobalMemoryStatusEx(&status) ? (std::max)(8 * MiB, status.ullAvailPhys / 2) : 256 * MiB;
}
static uint32_t thread_limit(uint32_t value) {
    return (std::max)(1u, (std::min)(value ? value : static_cast<uint32_t>(GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)), 256u));
}
static bool uses_seven(const fs::path& path) {
    unsigned char buffer[6]{};
    const auto data = File::open(path, GENERIC_READ, OPEN_EXISTING);
    const auto size = data.read(buffer, sizeof(buffer));
    const unsigned char signature[] = {'7', 'z', 0xbc, 0xaf, 0x27, 0x1c};
    if (size == sizeof(signature) && std::equal(std::begin(buffer), std::end(buffer), signature)) return true;
    const auto ext = path.extension().native();
    return _wcsicmp(ext.c_str(), L".7z") == 0 || _wcsicmp(ext.c_str(), L".001") == 0;
}
static fs::path read_archive_path(const wchar_t* original) {
    if (!original || !*original) fail(L"An archive path is required.");
    fs::path path(original);
    const auto ext = path.extension().native();
    if (ext.size() >= 4 && (ext[1] == L'z' || ext[1] == L'Z') &&
        std::all_of(ext.begin() + 2, ext.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }))
        return path.replace_extension(L".zip");
    if (ext.size() >= 4 && ext != L".001" &&
        std::all_of(ext.begin() + 1, ext.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }))
        return path.replace_extension(L".001");
    return path;
}
static void read_archive(const wchar_t* archive, const wchar_t* destination, Selection selection,
    const sz_read_options* opt, sz_entry_cb entry, sz_progress_cb progress, void* context) {
    if (!opt || (!selection.indices && selection.count)) fail(L"Invalid read options.");
    if (!selection.all() && selection.count > 1) {
        for (uint32_t i = 1; i < selection.count; ++i)
            if (selection.indices[i] <= selection.indices[i - 1]) fail(L"Entry indices must be sorted and unique.");
    }
    const auto path = read_archive_path(archive);
    Job job{entry ? L"List" : destination ? L"Extract" : L"Test", opt->password,
            thread_limit(opt->threads), memory_limit(opt->memory_limit), 0, progress, context};
    job.check();
    std::unique_ptr<Extraction> staging;
    if (destination) staging = std::make_unique<Extraction>(destination);
    if (uses_seven(path)) seven_read(job, path, staging.get(), selection, entry);
    else zip_read(job, path, staging.get(), selection, entry);
    if (staging) staging->finish();
}
template<class F> int run(F&& f) noexcept {
    error.clear();
    try { f(); return SZ_OK; }
    catch (Failure& e) { error.swap(e.text); return e.code; }
    catch (const std::bad_alloc&) { try { error = L"Memory limit exceeded."; } catch (...) {} }
    catch (const std::exception& e) { try { error = utf16(e.what(), CP_ACP); } catch (...) {} }
    catch (...) { try { error = L"Unexpected native error."; } catch (...) {} }
    return SZ_ERROR;
}
}
using namespace sz;

int SZ_CALL sz_list(const wchar_t* archive, const sz_read_options* opt, sz_entry_cb entry,
    sz_progress_cb progress, void* context) {
    return run([&] {
        if (!entry) fail(L"An entry callback is required.");
        read_archive(archive, nullptr, {nullptr, 0}, opt, entry, progress, context);
    });
}
int SZ_CALL sz_create(const wchar_t* archive, const sz_input* inputs, uint32_t count,
    const sz_create_options* opt, sz_progress_cb progress, void* context) {
    return run([&] {
        if (!archive || !*archive || !inputs || !count || !opt || opt->format > SZ_ZIP_ZSTD ||
            opt->level > 9 || opt->volume_size > INT64_MAX ||
            (opt->volume_size && opt->volume_size < 64 * 1024) ||
            (opt->password && !*opt->password) ||
            (opt->encrypt_headers && (opt->format != SZ_7Z || !opt->password)))
            fail(L"Invalid create options (split sizes must be at least 64 KiB).");
        Job job{L"Create", opt->password, thread_limit(opt->threads), memory_limit(opt->memory_limit), 0, progress, context};
        job.check();
        if (count > job.memory / 256) fail(L"Too many entries for the memory limit.");
        std::vector<Input> files;
        files.reserve(count);
        std::set<W, PathLess> names;
        U64 metadata = 0;
        for (uint32_t i = 0; i < count; ++i) {
            auto input = get_input(inputs[i]);
            metadata += 256 + input.source.native().size() * 2 + input.name.size() * 4;
            if (metadata > job.memory / 3) fail(L"Input paths exceed the memory limit.");
            if (!input.directory && input.size > UINT64_MAX - job.total) fail(L"Input is too large.");
            job.total += input.size;
            W normalized = input.name;
            std::replace(normalized.begin(), normalized.end(), L'/', L'\\');
            if (!names.insert(std::move(normalized)).second) fail(L"Duplicate compression path: " + input.name);
            files.push_back(std::move(input));
            job.check();
        }
        if (opt->format == SZ_7Z) seven_create(job, archive, files, *opt);
        else zip_create(job, archive, files, *opt);
    });
}
int SZ_CALL sz_extract(const wchar_t* archive, const wchar_t* destination, const uint32_t* indices,
    uint32_t count, const sz_read_options* opt, sz_progress_cb progress, void* context) {
    return run([&] {
        if (!destination || !*destination) fail(L"An extraction staging directory is required.");
        read_archive(archive, destination, {indices, count}, opt, nullptr, progress, context);
    });
}
int SZ_CALL sz_test(const wchar_t* archive, const sz_read_options* opt, sz_progress_cb progress, void* context) {
    return run([&] { read_archive(archive, nullptr, {nullptr, 0}, opt, nullptr, progress, context); });
}
const wchar_t* SZ_CALL sz_last_error() { return error.c_str(); }
const wchar_t* SZ_CALL sz_version() { return L"7Zipper native 0.1.0 / LZMA SDK 26.06 / minizip-ng 4.2.2 / zlib 1.4 / Zstandard 1.5.7"; }
