#include "internal.h"
#include "mz.h"
#include "mz_strm.h"
#include "mz_strm_mem.h"
#include "mz_zip.h"
#include <zlib.h>
#define ZSTD_STATIC_LINKING_ONLY
#include <zstd.h>
#include <array>
#include <atomic>
#include <cstring>
#include <future>

namespace sz {
namespace {
constexpr size_t chunk = 128 * 1024;
using Bytes = std::array<unsigned char, chunk>;
constexpr unsigned Store = MZ_COMPRESS_METHOD_STORE;
constexpr unsigned Deflate = MZ_COMPRESS_METHOD_DEFLATE;
constexpr unsigned Zstandard = MZ_COMPRESS_METHOD_ZSTD;
constexpr unsigned WinAttributes = MZ_HOST_SYSTEM_WINDOWS_NTFS << 8;

class Password {
    std::string value_;
public:
    explicit Password(const wchar_t* p) : value_(p ? utf8(p) : "") {}
    ~Password() { if (!value_.empty()) SecureZeroMemory(value_.data(), value_.size()); }
    const char* get() const { return value_.empty() ? nullptr : value_.c_str(); }
};

struct ParallelFailure {
    Job& job;
    std::mutex lock;
    std::exception_ptr first;
    void record() noexcept {
        const std::lock_guard guard(lock);
        if (!first) first = std::current_exception();
        job.stopped = true;
    }
};

struct ZipStream {
    mz_stream stream{};
    fs::path base;
    U64 volume = 0;
    OwnedFiles owned;
    File file;
    int64_t disk = -1;
    unsigned last = 0;
    bool writing;
    bool directory_view;
    std::vector<int64_t> disk_starts{0};
    std::exception_ptr failure;

    template<class R, class F> R call(R code, F&& f) noexcept {
        if (failure) return code;
        try { return f(); }
        catch (...) { failure = std::current_exception(); return code; }
    }
    fs::path path(unsigned n) const {
        auto part = base;
        const auto digits = std::to_wstring(n + 1);
        return part.replace_extension(L".z" + (digits.size() == 1 ? L"0" + digits : digits));
    }
    void open(int64_t target) {
        if (!writing && target == last) target = -1;
        if (file && disk == target) return;
        if (target > 65533) fail(L"Too many ZIP volumes.");
        file.close();
        disk = target;
        if (writing) {
            file = owned.write_new(disk == -1 || !volume ? base : path(static_cast<unsigned>(disk)));
        } else {
            if (disk >= last) fail(L"ZIP references a missing volume.");
            file = File::open(disk == -1 ? base : path(static_cast<unsigned>(disk)), GENERIC_READ, OPEN_EXISTING);
            const auto index = disk < 0 ? last : disk;
            if (disk_starts.size() == size_t(last) + 2 && file.size() != U64(disk_starts[index + 1] - disk_starts[index]))
                fail(L"ZIP volume size changed while being read.");
        }
    }
    void next() {
        const auto next_disk = disk + 1;
        open(!writing && next_disk == last ? -1 : next_disk);
    }
    void reserve_header(U64 estimate) {
        if (!volume) return;
        if (estimate > volume) fail(L"ZIP volume is too small for an entry header.");
        if (file.seek(0, FILE_CURRENT) + estimate > volume) next();
    }
    int64_t tell() const {
        return static_cast<int64_t>(file.seek(0, FILE_CURRENT))
            + (directory_view ? disk_starts[disk < 0 ? last : disk] : 0);
    }
    void seek(int64_t offset, int32_t origin) {
        if (origin != MZ_SEEK_SET && origin != MZ_SEEK_CUR && origin != MZ_SEEK_END)
            fail(L"Invalid ZIP seek origin.");
        if (!directory_view) {
            file.seek(offset, origin == MZ_SEEK_SET ? FILE_BEGIN : origin == MZ_SEEK_CUR ? FILE_CURRENT : FILE_END);
            return;
        }
        const int64_t from = origin == MZ_SEEK_SET ? 0 : origin == MZ_SEEK_CUR ? tell() : disk_starts.back();
        if (offset < -from || offset > disk_starts.back() - from) fail(L"Invalid ZIP central directory offset.");
        const auto where = from + offset;
        const auto target = (std::min)(static_cast<int64_t>(last),
            static_cast<int64_t>(std::upper_bound(disk_starts.begin(), disk_starts.end(), where) - disk_starts.begin()) - 1);
        open(target);
        file.seek(where - disk_starts[target]);
    }
    void add_disk_size(U64 size) {
        if (size > U64(INT64_MAX - disk_starts.back())) fail(L"ZIP archive is too large.");
        disk_starts.push_back(disk_starts.back() + static_cast<int64_t>(size));
    }
    ZipStream(const fs::path& archive, bool write, U64 split)
        : base(archive), volume(split), writing(write), directory_view(!write) {
        stream.vtbl = &vtable;
        if (!writing) {
            for (; last < 65534; ++last) {
                const auto name = long_path(path(last));
                WIN32_FILE_ATTRIBUTE_DATA attrs{};
                if (!GetFileAttributesExW(name.c_str(), GetFileExInfoStandard, &attrs)) {
                    if (GetLastError() != ERROR_FILE_NOT_FOUND) windows_error(L"Cannot inspect ZIP volume", name);
                    break;
                }
                if (!safe_attributes(attrs.dwFileAttributes) || (attrs.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                    fail(L"Unsafe ZIP volume: " + name);
                add_disk_size((U64(attrs.nFileSizeHigh) << 32) | attrs.nFileSizeLow);
            }
        }
        open(writing && volume ? 0 : -1);
        if (writing && volume) {
            constexpr unsigned signature = 0x08074b50;
            file.write(&signature, sizeof(signature));
        } else if (!writing) {
            if (last) {
                unsigned signature = 0;
                const auto first = File::open(path(0), GENERIC_READ, OPEN_EXISTING);
                if (first.read(&signature, sizeof(signature)) != sizeof(signature) || signature != 0x08074b50) {
                    last = 0;  // A similarly named file can exist beside an ordinary ZIP.
                    disk_starts.resize(1);
                }
            }
            add_disk_size(file.size());
        }
    }
    void finish_directory(void* directory, uint64_t entries, uint16_t version, const char* comment);
    static ZipStream& self(void* ptr) { return *reinterpret_cast<ZipStream*>(ptr); }
    static mz_stream_vtbl vtable;
};
mz_stream_vtbl ZipStream::vtable{
    nullptr,
    [](void* p) -> int32_t { return self(p).file ? MZ_OK : MZ_OPEN_ERROR; },
    [](void* p, void* out, int32_t size) -> int32_t {
        auto& s = self(p);
        return s.call<int32_t>(MZ_READ_ERROR, [&] {
            if (size < 0) fail(L"Invalid ZIP read request.");
            int32_t bytes = 0;
            while (bytes < size) {
                const auto got = s.file.read(static_cast<unsigned char*>(out) + bytes, size - bytes);
                if (got) bytes += got;
                else if (s.disk < 0 || s.writing) break;
                else s.next();
            }
            return bytes;
        });
    },
    [](void* p, const void* in, int32_t size) -> int32_t {
        auto& s = self(p);
        return s.call<int32_t>(MZ_WRITE_ERROR, [&] {
            if (size < 0 || !s.writing) fail(L"Invalid ZIP write request.");
            int32_t bytes = 0;
            while (bytes < size) {
                const U64 pos = s.file.seek(0, FILE_CURRENT);
                if (s.volume && pos == s.volume) {
                    s.next();
                    continue;
                }
                const auto amount = s.volume ? static_cast<DWORD>((std::min)(U64(size - bytes), s.volume - pos)) : size - bytes;
                s.file.write(static_cast<const unsigned char*>(in) + bytes, amount);
                bytes += amount;
            }
            return bytes;
        });
    },
    [](void* p) -> int64_t {
        auto& s = self(p);
        return s.call<int64_t>(MZ_TELL_ERROR, [&] { return s.tell(); });
    },
    [](void* p, int64_t offset, int32_t origin) -> int32_t {
        auto& s = self(p);
        return s.call<int32_t>(MZ_SEEK_ERROR, [&] {
            s.seek(offset, origin);
            return MZ_OK;
        });
    },
    nullptr, nullptr, nullptr, nullptr,
    [](void* p, int32_t property, int64_t* value) -> int32_t {
        const auto& s = self(p);
        if (property == MZ_STREAM_PROP_DISK_SIZE) *value = s.volume;
        else if (property == MZ_STREAM_PROP_DISK_NUMBER) *value = (s.disk == -1 && !s.volume && s.writing) ? 0 : s.disk;
        else return MZ_EXIST_ERROR;
        return MZ_OK;
    },
    [](void* p, int32_t property, int64_t value) -> int32_t {
        auto& s = self(p);
        if (property != MZ_STREAM_PROP_DISK_NUMBER) return MZ_EXIST_ERROR;
        return s.call<int32_t>(MZ_SEEK_ERROR, [&] {
            if (value == UINT32_MAX) value = -1;
            if (value < -1) fail(L"Invalid ZIP volume index.");
            if (s.writing) s.open(s.volume ? value : -1);
            else {
                s.directory_view = value == -1;
                if (!s.directory_view) s.open(value);
            }
            return MZ_OK;
        });
    }
};

#pragma pack(push, 1)
struct ZipEnd {
    uint32_t signature = 0x06054b50;
    uint16_t disk, directory_disk, entries_on_disk, entries;
    uint32_t directory_size, directory_offset;
    uint16_t comment_length;
};
struct Zip64End {
    uint32_t signature = 0x06064b50;
    uint64_t record_size = 44;
    uint16_t version, version_needed = 45;
    uint32_t disk, directory_disk;
    uint64_t entries_on_disk, entries, directory_size, directory_offset;
};
struct Zip64Locator {
    uint32_t signature = 0x07064b50;
    uint32_t disk;
    uint64_t offset;
    uint32_t disks;
};
#pragma pack(pop)
static_assert(sizeof(ZipEnd) == 22 && sizeof(Zip64End) == 56 && sizeof(Zip64Locator) == 20);

void ZipStream::finish_directory(void* directory, uint64_t entries, uint16_t version, const char* comment) {
    int32_t length = 0;
    const void* raw = nullptr;
    mz_stream_mem_get_buffer_length(directory, &length);
    if (mz_stream_mem_get_buffer(directory, &raw) != MZ_OK || length < 0)
        fail(L"Cannot read the generated ZIP central directory.");
    const auto* records = static_cast<const unsigned char*>(raw);
    const auto word = [](const unsigned char* p) { return unsigned(p[0]) | unsigned(p[1]) << 8; };
    next();
    const auto first = static_cast<uint32_t>(disk);
    uint64_t on_disk = 0;
    size_t offset = 0;
    size_t first_on_disk = 0;
    for (uint64_t i = 0; i < entries; ++i) {
        const auto* record = records + offset;
        if (size_t(length) - offset < 46 || std::memcmp(record, "PK\x01\x02", 4) != 0)
            fail(L"Invalid generated ZIP directory record.");
        const auto size = 46 + word(record + 28) + word(record + 30) + word(record + 32);
        if (size > size_t(length) - offset) fail(L"Truncated generated ZIP directory record.");
        if (size > volume) fail(L"ZIP volume is too small for a central directory record.");
        if (offset - first_on_disk + size > volume) {
            file.write(records + first_on_disk, static_cast<DWORD>(offset - first_on_disk));
            next();
            first_on_disk = offset;
            on_disk = 0;
        }
        offset += size;
        ++on_disk;
    }
    if (offset != size_t(length)) fail(L"Unexpected data in the generated ZIP directory.");
    if (offset != first_on_disk) file.write(records + first_on_disk, static_cast<DWORD>(offset - first_on_disk));

    const auto comment_length = comment ? (std::min)(std::strlen(comment), size_t(UINT16_MAX)) : size_t(0);
    const bool zip64 = entries >= UINT16_MAX;
    const auto previous = disk;
    reserve_header(sizeof(ZipEnd) + comment_length + (zip64 ? sizeof(Zip64End) + sizeof(Zip64Locator) : 0));
    if (disk != previous) on_disk = 0;
    const auto final = static_cast<uint32_t>(disk);
    if (zip64) {
        const auto position = file.seek(0, FILE_CURRENT);
        const Zip64End end{.version = version, .disk = final, .directory_disk = first, .entries_on_disk = on_disk,
            .entries = entries, .directory_size = static_cast<uint32_t>(length), .directory_offset = 0};
        const Zip64Locator locator{.disk = final, .offset = position, .disks = final + 1};
        file.write(&end, sizeof(end));
        file.write(&locator, sizeof(locator));
    }
    const ZipEnd end{.disk = static_cast<uint16_t>(final), .directory_disk = static_cast<uint16_t>(first),
        .entries_on_disk = static_cast<uint16_t>((std::min)(on_disk, uint64_t(UINT16_MAX))),
        .entries = static_cast<uint16_t>((std::min)(entries, uint64_t(UINT16_MAX))),
        .directory_size = static_cast<uint32_t>(length), .directory_offset = 0,
        .comment_length = static_cast<uint16_t>(comment_length)};
    file.write(&end, sizeof(end));
    if (comment_length) file.write(comment, static_cast<DWORD>(comment_length));

    const auto part = long_path(path(final));
    const auto renamed = long_path(base);
    const auto [target, inserted] = owned.paths.insert(renamed);
    if (!inserted) fail(L"Duplicate ZIP output path.");
    file.close();
    if (!MoveFileW(part.c_str(), renamed.c_str())) {
        const auto error = GetLastError();
        owned.paths.erase(target);
        SetLastError(error);
        windows_error(L"Cannot publish the final ZIP volume", renamed);
    }
    owned.paths.erase(part);
}

class Zip {
    ZipStream io_;
    void* handle_ = nullptr;
public:
    Zip(const fs::path& path, bool write = false, U64 split = 0) : io_(path, write, split) {
        handle_ = mz_zip_create();
        if (!handle_) throw std::bad_alloc();
        try {
            check(mz_zip_open(handle_, &io_.stream, write ? MZ_OPEN_MODE_WRITE | MZ_OPEN_MODE_CREATE : MZ_OPEN_MODE_READ),
                L"Cannot open ZIP archive");
        } catch (...) { mz_zip_delete(&handle_); throw; }
    }
    ~Zip() { if (handle_) mz_zip_delete(&handle_); }
    operator void*() const { return handle_; }
    void check(int status, WV action) const {
        if (io_.failure) std::rethrow_exception(io_.failure);
        if (status == MZ_PASSWORD_ERROR) password_error();
        if (status == MZ_MEM_ERROR) throw std::bad_alloc();
        if (status != MZ_OK) fail(W(action) + L" [ZIP " + std::to_wstring(status) + L"]");
    }
    void before_header(U64 bytes) { io_.reserve_header(bytes); }
    void finish() {
        check(mz_zip_close(handle_), L"Cannot finish ZIP archive");
        io_.file.close();
        io_.owned.keep = true;
    }
};

struct Zlib {
    z_stream stream{};
    bool encode;
    Zlib(bool compress, int level = 0) : encode(compress) {
        const int code = encode ? deflateInit2(&stream, level, Z_DEFLATED, -MAX_WBITS, 8, Z_DEFAULT_STRATEGY)
            : inflateInit2(&stream, -MAX_WBITS);
        if (code != Z_OK) fail(L"Cannot initialize Deflate.");
    }
    ~Zlib() { if (encode) deflateEnd(&stream); else inflateEnd(&stream); }
};
void zcheck(size_t code) {
    if (ZSTD_isError(code)) fail(L"Zstandard: " + utf16(ZSTD_getErrorName(code)));
}
struct ZstdC {
    ZSTD_CCtx* value = ZSTD_createCCtx();
    ~ZstdC() { ZSTD_freeCCtx(value); }
};
struct ZstdD {
    ZSTD_DCtx* value = ZSTD_createDCtx();
    ~ZstdD() { ZSTD_freeDCtx(value); }
};
int zstd_level(unsigned level) {
    constexpr int levels[]{1, 1, 2, 3, 5, 7, 9, 12, 15, 19};
    return levels[level];
}
struct Packed {
    File bytes;
    U64 size = 0;
    uint32_t crc = 0;
    uint16_t method = Store;
};
static Packed pack(Job& job, const Input& input, const fs::path& scratch, const sz_create_options& opt,
    unsigned codec_threads, U64 memory, std::atomic<U64>& progress) {
    Packed result;
    if (input.directory) return result;
    result.method = opt.format == SZ_ZIP_ZSTD ? Zstandard : opt.level ? Deflate : Store;
    result.bytes = File::temporary(scratch);
    auto source = open_input(input);
    Bytes in{}, out{};
    std::unique_ptr<Zlib> deflater;
    std::unique_ptr<ZstdC> zstd;
    if (result.method == Deflate) deflater = std::make_unique<Zlib>(true, opt.level);
    if (result.method == Zstandard) {
        zstd = std::make_unique<ZstdC>();
        if (!zstd->value) throw std::bad_alloc();
        int level = zstd_level(opt.level);
        ZSTD_parameters parameters{};
        for (;;) {
            parameters = ZSTD_getParams(level, input.size, 0);
            const auto single = ZSTD_estimateCStreamSize_usingCParams(parameters.cParams);
            zcheck(single);
            if (single < memory - MiB) break;
            if (!--level) fail(L"Zstandard compression exceeds the memory limit.");
        }
        auto* c = zstd->value;
        const auto single = ZSTD_estimateCStreamSize_usingCParams(parameters.cParams);
        const auto per_worker = 8 * MiB + single * 2;
        const auto threads = (std::min)(U64(codec_threads), memory / per_worker);
        zcheck(ZSTD_CCtx_setCParams(c, parameters.cParams));
        zcheck(ZSTD_CCtx_setPledgedSrcSize(c, input.size));
        if (threads > 1 && input.size >= 8 * MiB) {
            zcheck(ZSTD_CCtx_setParameter(c, ZSTD_c_nbWorkers, static_cast<int>(threads - 1)));
            zcheck(ZSTD_CCtx_setParameter(c, ZSTD_c_jobSize, 2 * MiB));
        }
    }
    U64 remaining = input.size;
    while (true) {
        job.report(progress.load(), input.name.c_str());
        const auto size = source.read(in.data(), static_cast<DWORD>((std::min)(remaining, U64(in.size()))));
        if (!size && remaining) fail(L"Source ended before its expected size: " + input.source.native());
        remaining -= size;
        const bool end = !remaining;
        result.crc = crc32(result.crc, in.data(), size);
        if (deflater) {
            auto& stream = deflater->stream;
            stream.next_in = in.data();
            stream.avail_in = size;
            do {
                stream.next_out = out.data();
                stream.avail_out = static_cast<uInt>(out.size());
                const int status = deflate(&stream, end ? Z_FINISH : Z_NO_FLUSH);
                if (status != Z_OK && status != Z_STREAM_END) fail(L"Deflate encoding failed.");
                result.bytes.write(out.data(), static_cast<DWORD>(out.size() - stream.avail_out));
                if (status == Z_STREAM_END) break;
            } while (stream.avail_in || end);
        } else if (zstd) {
            ZSTD_inBuffer input_buffer{in.data(), size, 0};
            while (true) {
                ZSTD_outBuffer output{out.data(), out.size(), 0};
                const auto left = ZSTD_compressStream2(zstd->value, &output, &input_buffer, end ? ZSTD_e_end : ZSTD_e_continue);
                zcheck(left);
                result.bytes.write(out.data(), static_cast<DWORD>(output.pos));
                if (input_buffer.pos == input_buffer.size && (!end || !left)) break;
                job.check();
            }
        } else result.bytes.write(in.data(), size);
        progress += size;
        if (end) break;
    }
    check_input(input, source);
    result.size = result.bytes.seek(0, FILE_CURRENT);
    result.bytes.seek(0);
    return result;
}

void write_entry(Zip& zip, Job& job, const Input& input, const Packed& data, const char* password, uint32_t level) {
    auto name = utf8(input.name);
    std::replace(name.begin(), name.end(), '\\', '/');
    if (input.directory) name += '/';
    if (name.size() > UINT16_MAX - 256) fail(L"ZIP entry name is too long.");
    zip.before_header(name.size() + 160);
    mz_zip_file info{};
    info.version_madeby = WinAttributes | 63;
    if (data.method == Zstandard) info.version_needed = 63;
    info.flag = MZ_ZIP_FLAG_UTF8 | MZ_ZIP_FLAG_DATA_DESCRIPTOR;
    info.compression_method = data.method;
    mz_zip_ntfs_to_unix_time(input.time, &info.modified_date);
    info.accessed_date = info.creation_date = info.modified_date;
    info.uncompressed_size = input.size;
    info.compressed_size = data.size;
    info.crc = data.crc;
    info.external_fa = input.directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_ARCHIVE;
    info.filename = name.c_str();
    if (data.size >= UINT32_MAX - 30) info.zip64 = MZ_ZIP64_FORCE;
    if (password && !input.directory) {
        info.flag |= MZ_ZIP_FLAG_ENCRYPTED;
        info.aes_version = 1;
        info.aes_strength = MZ_AES_STRENGTH_256;
    }
    zip.check(mz_zip_entry_write_open(zip, &info, static_cast<int16_t>((std::max)(level, 1u)), 1,
        input.directory ? nullptr : password), L"Cannot write ZIP entry");
    if (data.bytes) {
        Bytes buf{};
        while (const auto size = data.bytes.read(buf.data(), static_cast<DWORD>(buf.size()))) {
            job.check();
            const auto wrote = mz_zip_entry_write(zip, buf.data(), size);
            zip.check(wrote == static_cast<int32_t>(size) ? MZ_OK : wrote < 0 ? wrote : MZ_WRITE_ERROR, L"Cannot write ZIP contents");
        }
    }
    zip.check(mz_zip_entry_write_close(zip, data.crc, data.size, input.size), L"Cannot finish ZIP entry");
}

static W zip_name(const mz_zip_file& info) {
    auto name = std::string_view(info.filename, info.filename_size);
    if (info.flag & MZ_ZIP_FLAG_UTF8) return utf16(name);
    // Info-ZIP Unicode Path extra field overrides legacy names only when its CRC matches.
    const auto* extra = info.extrafield;
    size_t remaining = info.extrafield_size;
    while (remaining >= 4) {
        const auto type = extra[0] | extra[1] << 8;
        const size_t length = extra[2] | extra[3] << 8;
        extra += 4;
        remaining -= 4;
        if (length > remaining) break;
        if (type == 0x7075 && length >= 5 && extra[0] == 1) {
            const auto crc = uint32_t(extra[1]) | uint32_t(extra[2]) << 8 | uint32_t(extra[3]) << 16 | uint32_t(extra[4]) << 24;
            if (crc == crc32_z(0, reinterpret_cast<const unsigned char*>(name.data()), name.size()))
                return utf16({reinterpret_cast<const char*>(extra + 5), length - 5});
        }
        extra += length;
        remaining -= length;
    }
    return utf16(name, 437);
}
static Entry zip_entry(Zip& zip, const mz_zip_file& info, uint32_t index) {
    if (!info.filename || info.compressed_size < 0 || info.uncompressed_size < 0)
        fail(L"Invalid ZIP entry metadata.");
    Entry item;
    item.index = index;
    item.path = zip_name(info);
    if (mz_zip_entry_is_dir(zip) == MZ_OK) {
        item.flags |= SZ_DIRECTORY;
        if (!item.path.empty() && (item.path.back() == L'/' || item.path.back() == L'\\')) item.path.pop_back();
    }
    if (info.flag & MZ_ZIP_FLAG_ENCRYPTED) item.flags |= SZ_ENCRYPTED;
    const auto host = MZ_HOST_SYSTEM(info.version_madeby);
    const auto type = (info.external_fa >> 16) & 0170000;
    if (!safe_name(item.path) || !safe_attributes(info.external_fa & 0xffff) ||
        (((host == MZ_HOST_SYSTEM_UNIX || host == MZ_HOST_SYSTEM_OSX_DARWIN) &&
          type && type != 0100000 && type != 0040000)) ||
        mz_zip_entry_is_symlink(zip) == MZ_OK || (info.linkname && *info.linkname)) item.flags |= SZ_UNSAFE;
    item.size = info.uncompressed_size;
    item.packed = info.compressed_size;
    if (info.modified_date) mz_zip_unix_to_ntfs_time(info.modified_date, &item.time);
    switch (info.compression_method) {
    case Store: item.method = L"Store"; break;
    case Deflate: item.method = L"Deflate"; break;
    case Zstandard: item.method = L"Zstandard"; break;
    default: item.method = L"ZIP method " + std::to_wstring(info.compression_method); break;
    }
    if (item.encrypted()) item.method += info.aes_version ? L" / AES" : L" / ZipCrypto";
    return item;
}
struct ZipTask { Entry item; int64_t position; fs::path destination; };
static void read_entry(Zip& zip, Job& job, const ZipTask& task, U64 memory,
    const char* password, std::atomic<U64>& progress) {
    const auto& item = task.item;
    job.report(progress.load(), item.path.c_str());
    zip.check(mz_zip_goto_entry(zip, task.position), L"Cannot seek to ZIP entry");
    mz_zip_file* info = nullptr;
    zip.check(mz_zip_entry_get_info(zip, &info), L"Cannot read ZIP entry");
    const auto reread = zip_entry(zip, *info, item.index);
    if (reread.size != item.size || reread.packed != item.packed || reread.path != item.path ||
        reread.flags != item.flags || reread.method != item.method) fail(L"ZIP changed while being read.");
    const auto method = info->compression_method;
    if (method != Store && method != Deflate && method != Zstandard) fail(L"Unsupported ZIP compression method: " + item.method);
    const auto aes = info->aes_version;
    const auto expected_crc = info->crc;
    U64 packed = item.packed;
    if (item.encrypted()) {
        if (!password) password_error();
        if (aes && (info->aes_strength < 1 || info->aes_strength > 3 || aes > 2))
            fail(L"Unsupported ZIP AES parameters.");
        const U64 overhead = aes ? 16 + 4 * info->aes_strength : 12;
        if (packed < overhead) fail(L"Truncated ZIP encryption data.");
        packed -= overhead;
    } else if (aes) fail(L"Invalid ZIP encryption metadata.");
    zip.check(mz_zip_entry_read_open(zip, 1, password), L"Cannot open ZIP entry");
    mz_zip_file* local = nullptr;
    zip.check(mz_zip_entry_get_local_info(zip, &local), L"Cannot read local ZIP header");
    if (!local->filename || local->compression_method != method ||
        ((local->flag ^ info->flag) & MZ_ZIP_FLAG_ENCRYPTED) ||
        std::string_view(local->filename, local->filename_size) != std::string_view(info->filename, info->filename_size))
        fail(L"Local ZIP header disagrees with the central directory.");
    if (item.directory() && !item.size && !item.packed) {
        if (expected_crc) fail(L"ZIP directory has a nonzero CRC: " + item.path);
        zip.check(mz_zip_entry_read_close(zip, nullptr, nullptr, nullptr), L"Cannot finish ZIP directory entry");
        return;
    }
    File output;
    if (!task.destination.empty() && !item.directory()) output = File::open(task.destination, GENERIC_WRITE, CREATE_NEW);
    std::unique_ptr<Zlib> deflater;
    std::unique_ptr<ZstdD> zstd;
    size_t frame_left = 1;
    bool end = false;
    if (method == Deflate) deflater = std::make_unique<Zlib>(false);
    if (method == Zstandard) {
        zstd = std::make_unique<ZstdD>();
        if (!zstd->value) throw std::bad_alloc();
        const auto cap = (std::min)(memory / 2, U64(1) << 31);
        zcheck(ZSTD_DCtx_setMaxWindowSize(zstd->value, static_cast<size_t>(cap)));
    }
    U64 left = item.size;
    uint32_t crc = 0;
    const auto deliver = [&](const auto* buffer, size_t size) {
        if (size > left) fail(L"ZIP entry exceeds its declared size: " + item.path);
        if (output) output.write(buffer, static_cast<DWORD>(size));
        crc = crc32_z(crc, buffer, size);
        left -= size;
        progress += size;
    };
    Bytes in{}, out{};
    while (packed) {
        job.report(progress.load(), item.path.c_str());
        const auto n = mz_zip_entry_read(zip, in.data(), static_cast<int32_t>((std::min)(packed, U64(in.size()))));
        if (n <= 0) zip.check(n ? n : MZ_READ_ERROR, L"Truncated ZIP contents");
        packed -= n;
        if (deflater) {
            if (end) fail(L"Trailing data in ZIP Deflate entry.");
            auto& s = deflater->stream;
            s.next_in = in.data();
            s.avail_in = n;
            do {
                s.next_out = out.data();
                s.avail_out = static_cast<uInt>(out.size());
                const auto status = inflate(&s, Z_NO_FLUSH);
                if (status != Z_OK && status != Z_STREAM_END && !(status == Z_BUF_ERROR && !s.avail_in)) {
                    if (item.encrypted() && !aes) password_error();
                    fail(L"Invalid ZIP Deflate data.");
                }
                deliver(out.data(), out.size() - s.avail_out);
                if (status == Z_STREAM_END) {
                    if (s.avail_in || packed) fail(L"Trailing data in ZIP Deflate entry.");
                    end = true;
                    break;
                }
                job.check();
            } while (s.avail_in || !s.avail_out);
        } else if (zstd) {
            ZSTD_inBuffer from{in.data(), static_cast<size_t>(n), 0};
            do {
                ZSTD_outBuffer to{out.data(), out.size(), 0};
                frame_left = ZSTD_decompressStream(zstd->value, &to, &from);
                zcheck(frame_left);
                deliver(out.data(), to.pos);
                job.check();
                if (from.pos == from.size && (to.pos != to.size || frame_left == 0)) break;
            } while (true);
        } else deliver(in.data(), n);
    }
    if (left || (deflater && !end) || (zstd && frame_left)) fail(L"ZIP entry ended before its declared size.");
    if (aes < 2 && expected_crc != crc) {
        if (item.encrypted() && !aes) password_error();
        fail(L"ZIP entry failed CRC verification: " + item.path);
    }
    zip.check(mz_zip_entry_read_close(zip, nullptr, nullptr, nullptr), L"ZIP entry failed authentication");
    if (output) output.modified(item.time);
}
} // namespace

extern "C" int64_t sz_zip_disk_start(void* stream, uint32_t disk) {
    const auto& zip = ZipStream::self(stream);
    return disk <= zip.last && !zip.writing ? zip.disk_starts[disk] : -1;
}
extern "C" uint32_t sz_zip_last_disk(void* stream) { return ZipStream::self(stream).last; }
extern "C" int32_t sz_zip_finish_split(void* stream, void* directory, uint64_t entries, uint16_t version, const char* comment) {
    auto& zip = ZipStream::self(stream);
    return zip.call<int32_t>(MZ_WRITE_ERROR, [&] {
        zip.finish_directory(directory, entries, version, comment);
        return MZ_OK;
    });
}

void zip_create(Job& job, const fs::path& path, const std::vector<Input>& files, const sz_create_options& opt) {
    if (opt.volume_size && _wcsicmp(path.extension().c_str(), L".zip") != 0)
        fail(L"Split ZIP output must have the .zip extension.");
    Password password(job.password);
    const auto scratch = fs::absolute(path).parent_path();
    const auto memory_per_worker = opt.format == SZ_ZIP_ZSTD ? 24 * MiB : 2 * MiB;
    const auto workers = (std::max)(size_t(1), (std::min)({size_t(job.threads), files.size(), size_t(job.memory / memory_per_worker)}));
    // Central directory is buffered by minizip; leave at least half the job budget for codecs.
    U64 directory_budget = (std::min)(job.memory / 4, U64(INT32_MAX) / 2);
    for (const auto& input : files) {
        const auto need = utf8(input.name).size() + 160;
        if (need > directory_budget) fail(L"ZIP central directory exceeds the memory limit.");
        directory_budget -= need;
    }
    Zip zip(path, true, opt.volume_size);
    std::atomic<U64> progress{};
    ParallelFailure failure{job};
    for (size_t i = 0; i < files.size();) {
        const auto end = (std::min)(i + workers, files.size());
        if (workers == 1) {
            auto data = pack(job, files[i], scratch, opt, job.threads, job.memory * 3 / 4, progress);
            write_entry(zip, job, files[i++], data, password.get(), opt.level);
            continue;
        }
        std::vector<std::future<Packed>> batch;
        try {
            for (size_t j = i; j < end; ++j) batch.push_back(std::async(std::launch::async, [&, j] {
                try { return pack(job, files[j], scratch, opt, 1, (job.memory / workers) * 3 / 4, progress); }
                catch (...) { failure.record(); throw; }
            }));
            for (auto& handle : batch)
                write_entry(zip, job, files[i++], handle.get(), password.get(), opt.level);
        } catch (...) { failure.record(); }
        batch.clear();
        if (failure.first) std::rethrow_exception(failure.first);
    }
    job.report(job.total);
    zip.finish();
}
void zip_read(Job& job, const fs::path& path, Extraction* staging, Selection selection, sz_entry_cb list) {
    Zip reader(path);
    uint64_t count = 0;
    reader.check(mz_zip_get_number_entry(reader, &count), L"Cannot count ZIP entries");
    if (count > UINT32_MAX || count > job.memory / 256) fail(L"Too many ZIP entries for the memory limit.");
    if (!selection.all() && selection.count && selection.indices[selection.count - 1] >= count) fail(L"Entry index is out of range.");
    std::vector<ZipTask> tasks;
    int status = count ? mz_zip_goto_first_entry(reader) : MZ_END_OF_LIST;
    U64 names = 0;
    for (uint32_t i = 0; i < count; ++i) {
        job.check();
        reader.check(status, L"Invalid ZIP central directory");
        if (selection.includes(i)) {
            mz_zip_file* info = nullptr;
            reader.check(mz_zip_entry_get_info(reader, &info), L"Cannot read ZIP metadata");
            auto item = zip_entry(reader, *info, i);
            if (list) list_entry(job, list, item);
            else {
                if (item.size > UINT64_MAX - job.total) fail(L"ZIP is too large.");
                if (item.encrypted() && !job.password) password_error();
                job.total += item.size;
                names += item.path.size() * sizeof(wchar_t) * 4 + 256;
                if (names > job.memory / 4) fail(L"ZIP paths exceed the memory limit.");
                if (staging) staging->validate(item);
                tasks.push_back({std::move(item), mz_zip_get_entry(reader), {}});
            }
        }
        if (i + 1 < count) status = mz_zip_goto_next_entry(reader);
    }
    if (list || tasks.empty()) return;
    if (staging) for (auto& task : tasks) task.destination = staging->prepare(task.item);
    Password password(job.password);
    std::atomic<U64> progress{};
    std::atomic<size_t> next{};
    const auto workers = (std::max)(size_t(1), (std::min)({size_t(job.threads), tasks.size(), size_t(job.memory / (16 * MiB))}));
    const U64 budget = (job.memory / workers) * 3 / 4;
    ParallelFailure failure{job};
    const auto work = [&](Zip& zip) {
        for (auto i = next++; i < tasks.size(); i = next++) read_entry(zip, job, tasks[i], budget, password.get(), progress);
    };
    std::vector<std::future<void>> batch;
    try {
        for (size_t i = 1; i < workers; ++i) batch.push_back(std::async(std::launch::async, [&] {
            try { Zip worker(path); work(worker); }
            catch (...) { failure.record(); }
        }));
        work(reader);
    } catch (...) { failure.record(); }
    for (auto& handle : batch) handle.get();
    if (failure.first) std::rethrow_exception(failure.first);
    job.report(job.total);
}
}
