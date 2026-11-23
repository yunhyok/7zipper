#include "internal.h"
#include <initguid.h>
#include "Common/MyCom.h"
#include "Windows/PropVariant.h"
#include "7zip/Archive/7z/7zHandler.h"
#include "7zip/IPassword.h"
#include <exception>

namespace sz {
using NWindows::NCOM::CPropVariant;
struct Bridge {
    Job& job;
    std::mutex lock;
    std::exception_ptr error;
    explicit Bridge(Job& job) : job(job) {}
    template<class F> HRESULT call(F&& f) noexcept {
        try { f(); return S_OK; }
        catch (...) {
            const std::lock_guard guard(lock);
            if (!error) error = std::current_exception();
            return E_ABORT;
        }
    }
    void verify(HRESULT result, WV action) {
        const std::lock_guard guard(lock);
        if (error) std::rethrow_exception(error);
        if (result == E_OUTOFMEMORY) fail(L"7z codec exceeded the memory limit.");
        if (result != S_OK) fail(W(action) + L" [7z " + std::to_wstring(static_cast<uint32_t>(result)) + L"]");
    }
};

static fs::path volume_path(const fs::path& base, U64 i) {
    W digits = std::to_wstring(i);
    return fs::path(base.native() + L"." + W(digits.size() < 3 ? 3 - digits.size() : 0, L'0') + digits);
}
static U64 offset_to(U64 current, U64 end, Int64 offset, UInt32 origin) {
    if (origin > STREAM_SEEK_END) fail(L"Invalid stream seek.");
    const U64 base = origin == STREAM_SEEK_SET ? 0 : origin == STREAM_SEEK_CUR ? current : end;
    const auto magnitude = offset < 0 ? static_cast<U64>(-(offset + 1)) + 1 : static_cast<U64>(offset);
    if ((offset < 0 && magnitude > base) || (offset >= 0 && magnitude > INT64_MAX - base)) fail(L"Invalid stream seek.");
    return offset < 0 ? base - magnitude : base + magnitude;
}

Z7_CLASS_IMP_COM_1(InStream, IInStream)
    Z7_IFACE_COM7_IMP(ISequentialInStream)
    Bridge& bridge_;
    std::vector<fs::path> paths_;
    std::vector<U64> end_;
    U64 position_ = 0;
    File file_;
    size_t active_ = 0;
    const Input* input_ = nullptr;
public:
    InStream(Bridge& bridge, const Input& input)
        : bridge_(bridge), end_{input.size}, file_(open_input(input)), input_(&input) {}
    InStream(Bridge& bridge, const fs::path& path) : bridge_(bridge) {
        file_ = File::open(path, GENERIC_READ, OPEN_EXISTING);
        end_.push_back(file_.size());
        if (path.extension() != L".001") return;
        paths_.push_back(path);
        auto base = path;
        base.replace_extension();
        for (U64 i = 2; i <= bridge_.job.memory / 256; ++i) {
            const auto next = volume_path(base, i);
            const auto attr = GetFileAttributesW(long_path(next).c_str());
            if (attr == INVALID_FILE_ATTRIBUTES) {
                if (GetLastError() == ERROR_FILE_NOT_FOUND) return;
                windows_error(L"Cannot inspect archive volume", next.native());
            }
            const auto size = File::open(next, GENERIC_READ, OPEN_EXISTING).size();
            if (!size || size > INT64_MAX - end_.back()) fail(L"Invalid archive volume size.");
            end_.push_back(end_.back() + size);
            paths_.push_back(next);
            bridge_.job.check();
        }
        fail(L"Too many archive volumes for the memory limit.");
    }
};
Z7_COM7F_IMF(InStream::Seek(Int64 offset, UInt32 origin, UInt64* position)) {
    return bridge_.call([&] {
        position_ = offset_to(position_, end_.back(), offset, origin);
        if (position) *position = position_;
    });
}
Z7_COM7F_IMF(InStream::Read(void* raw, UInt32 count, UInt32* processed)) {
    if (processed) *processed = 0;
    return bridge_.call([&] {
        bridge_.job.check();
        UInt32 done = 0;
        auto* out = static_cast<unsigned char*>(raw);
        while (done < count && position_ < end_.back()) {
            const size_t index = std::upper_bound(end_.begin(), end_.end(), position_) - end_.begin();
            if (index != active_) {
                file_ = File::open(paths_[index], GENERIC_READ, OPEN_EXISTING);
                active_ = index;
            }
            const auto start = index ? end_[index - 1] : 0;
            file_.seek(position_ - start);
            const auto amount = static_cast<DWORD>((std::min)(U64(count - done), end_[index] - position_));
            const auto read = file_.read(out + done, amount);
            if (!read) fail(L"Unexpected end of file.");
            done += read;
            position_ += read;
        }
        if (input_ && position_ == end_.back()) check_input(*input_, file_);
        if (processed) *processed = done;
    });
}

Z7_CLASS_IMP_COM_1(OutStream, IOutStream)
    Z7_IFACE_COM7_IMP(ISequentialOutStream)
    Bridge& bridge_;
    fs::path base_;
    U64 volume_, position_ = 0, size_ = 0;
    U64 active_ = 0;
    OwnedFiles files_;
    File file_;
    fs::path path(U64 i) const { return volume_ ? volume_path(base_, i) : base_; }
    File& disk(U64 index) {
        if (active_ != index) {
            file_.close();
            file_ = files_.write_new(path(index), true);
            active_ = index;
        }
        return file_;
    }
public:
    OutStream(Bridge& bridge, const fs::path& base, U64 volume) : bridge_(bridge), base_(base), volume_(volume) { disk(1); }
    void finish() { file_.close(); files_.keep = true; }
};
Z7_COM7F_IMF(OutStream::Write(const void* raw, UInt32 count, UInt32* processed)) {
    if (processed) *processed = 0;
    return bridge_.call([&] {
        bridge_.job.check();
        if (count > INT64_MAX - position_) fail(L"7z output is too large.");
        const auto* data = static_cast<const unsigned char*>(raw);
        UInt32 done = 0;
        while (done < count) {
            const U64 index = volume_ ? position_ / volume_ + 1 : 1;
            const U64 offset = volume_ ? position_ % volume_ : position_;
            const auto amount = volume_ ? static_cast<DWORD>((std::min)(U64(count - done), volume_ - offset)) : count - done;
            auto& file = disk(index);
            file.seek(offset);
            file.write(data + done, amount);
            done += amount;
            position_ += amount;
        }
        size_ = (std::max)(position_, size_);
        if (processed) *processed = done;
    });
}
Z7_COM7F_IMF(OutStream::Seek(Int64 offset, UInt32 origin, UInt64* position)) {
    return bridge_.call([&] {
        position_ = offset_to(position_, size_, offset, origin);
        if (position) *position = position_;
    });
}
Z7_COM7F_IMF(OutStream::SetSize(UInt64 size)) {
    return bridge_.call([&] {
        if (size > INT64_MAX) fail(L"7z output is too large.");
        if (!volume_) disk(1).resize(size);
        else {
            const U64 last = size ? (size - 1) / volume_ + 1 : 1;
            const U64 previous = size_ ? (size_ - 1) / volume_ + 1 : 1;
            for (U64 i = previous; i < last; ++i) disk(i).resize(volume_);
            disk(last).resize(size ? (size - 1) % volume_ + 1 : 0);
            for (U64 i = last + 1; i <= previous; ++i) {
                const auto where = long_path(path(i));
                if (files_.paths.contains(where)) {
                    if (!DeleteFileW(where.c_str())) windows_error(L"Cannot remove unused volume", where);
                    files_.paths.erase(where);
                }
            }
        }
        size_ = size;
    });
}

Z7_CLASS_IMP_COM_2(Update, IArchiveUpdateCallback, ICryptoGetTextPassword2)
    Bridge& bridge_;
    const std::vector<Input>& inputs_;
public:
    Update(Bridge& bridge, const std::vector<Input>& inputs) : bridge_(bridge), inputs_(inputs) {}
    Z7_IFACE_COM7_IMP(IProgress)
};
Z7_COM7F_IMF(Update::SetTotal(UInt64)) { return S_OK; }
Z7_COM7F_IMF(Update::SetCompleted(const UInt64* bytes)) {
    return bridge_.call([&] { bridge_.job.report(bytes ? *bytes : 0); });
}
Z7_COM7F_IMF(Update::GetUpdateItemInfo(UInt32, Int32* data, Int32* properties, UInt32* previous)) {
    if (data) *data = 1;
    if (properties) *properties = 1;
    if (previous) *previous = static_cast<UInt32>(-1);
    return S_OK;
}
Z7_COM7F_IMF(Update::GetProperty(UInt32 index, PROPID id, PROPVARIANT* output)) {
    return bridge_.call([&] {
        const auto& item = inputs_[index];
        CPropVariant property;
        switch (id) {
        case kpidPath: {
            W name(item.name);
            std::replace(name.begin(), name.end(), L'\\', L'/');
            property = name.c_str();
            break;
        }
        case kpidIsDir: property = item.directory; break;
        case kpidSize: property = static_cast<UInt64>(item.size); break;
        case kpidMTime: property = filetime(item.time); break;
        case kpidAttrib: property = static_cast<UInt32>(item.directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_ARCHIVE); break;
        default: break;
        }
        if (property.Detach(output) != S_OK) fail(L"Cannot set 7z archive property.");
    });
}
Z7_COM7F_IMF(Update::GetStream(UInt32 index, ISequentialInStream** stream)) {
    *stream = nullptr;
    return bridge_.call([&] {
        const auto& item = inputs_[index];
        bridge_.job.report(0, item.name.c_str());
        if (!item.directory) {
            CMyComPtr<ISequentialInStream> input(new InStream(bridge_, item));
            *stream = input.Detach();
        }
    });
}
Z7_COM7F_IMF(Update::SetOperationResult(Int32 result)) {
    return bridge_.call([&] { if (result) fail(L"7z could not compress an input."); });
}
Z7_COM7F_IMF(Update::CryptoGetTextPassword2(Int32* defined, BSTR* password)) {
    *defined = bridge_.job.password ? 1 : 0;
    *password = bridge_.job.password ? SysAllocString(bridge_.job.password) : nullptr;
    return *defined && !*password ? E_OUTOFMEMORY : S_OK;
}

Z7_CLASS_IMP_COM_2(Open, IArchiveOpenCallback, ICryptoGetTextPassword)
    Bridge& bridge_;
public:
    bool password_used = false;
    explicit Open(Bridge& bridge) : bridge_(bridge) {}
};
Z7_COM7F_IMF(Open::SetTotal(const UInt64*, const UInt64*)) { return S_OK; }
Z7_COM7F_IMF(Open::SetCompleted(const UInt64*, const UInt64*)) {
    return bridge_.call([&] { bridge_.job.check(); });
}
Z7_COM7F_IMF(Open::CryptoGetTextPassword(BSTR* password)) {
    *password = nullptr;
    password_used = true;
    return bridge_.call([&] {
        if (!bridge_.job.password) password_error();
        *password = SysAllocString(bridge_.job.password);
        if (!*password) fail(L"Cannot allocate 7z password.");
    });
}

static CPropVariant property(Bridge& bridge, IInArchive* archive, UInt32 index, PROPID id) {
    CPropVariant value;
    bridge.verify(archive->GetProperty(index, id, &value), L"Cannot read 7z entry property");
    return value;
}
static W text(const CPropVariant& value) {
    return (value.vt == VT_BSTR && value.bstrVal) ? W(value.bstrVal, SysStringLen(value.bstrVal)) : W{};
}
static U64 number(const CPropVariant& value) {
    return value.vt == VT_UI8 ? value.uhVal.QuadPart : value.vt == VT_UI4 ? value.ulVal : 0;
}
static bool yes(const CPropVariant& value) { return value.vt == VT_BOOL && value.boolVal != VARIANT_FALSE; }
static Entry get_entry(Bridge& bridge, IInArchive* archive, UInt32 index) {
    const auto get = [&](PROPID id) { return property(bridge, archive, index, id); };
    Entry item;
    item.index = index;
    item.path = text(get(kpidPath));
    item.method = text(get(kpidMethod));
    item.size = number(get(kpidSize));
    item.packed = number(get(kpidPackSize));
    const auto stamp = get(kpidMTime);
    if (stamp.vt == VT_FILETIME) item.time = filetime(stamp.filetime);
    if (yes(get(kpidIsDir))) item.flags |= SZ_DIRECTORY;
    if (yes(get(kpidEncrypted))) item.flags |= SZ_ENCRYPTED;
    if (!safe_name(item.path) || !safe_attributes(static_cast<DWORD>(number(get(kpidAttrib)))) ||
        !text(get(kpidSymLink)).empty() || !text(get(kpidHardLink)).empty() || yes(get(kpidIsAltStream)) ||
        get(kpidNtReparse).vt != VT_EMPTY) item.flags |= SZ_UNSAFE;
    return item;
}
static void check_decoder_memory(const Entry& item, U64 limit) {
    WV methods(item.method);
    while (!methods.empty()) {
        const auto end = methods.find(L' ');
        auto method = methods.substr(0, end);
        WV amount;
        if (method.starts_with(L"LZMA:") || method.starts_with(L"LZMA2:")) amount = method.substr(method.find(L':') + 1);
        else if (method.starts_with(L"PPMD:")) {
            const auto memory = method.find(L":mem");
            if (memory != WV::npos) amount = method.substr(memory + 4);
        }
        if (!amount.empty()) {
            U64 n = 0;
            size_t i = 0;
            while (i < amount.size() && amount[i] >= L'0' && amount[i] <= L'9') {
                n = n * 10 + amount[i++] - L'0';
                if (n > UINT32_MAX) fail(L"Invalid 7z decoder dictionary.");
            }
            if (!i) fail(L"Invalid 7z decoder dictionary.");
            const auto suffix = i == amount.size() ? L':' : amount[i];
            U64 bytes = n;
            if (suffix == L'k') bytes *= 1024;
            else if (suffix == L'm') bytes *= MiB;
            else if (suffix == L':') bytes = n <= 32 ? U64(1) << n : UINT64_MAX;
            if (bytes > limit) fail(L"7z decoder dictionary exceeds the memory limit: " + item.path);
        }
        if (end == WV::npos) break;
        methods.remove_prefix(end + 1);
    }
}

Z7_CLASS_IMP_COM_1(ExtractFile, ISequentialOutStream)
    Bridge& bridge_;
    File file_;
    U64 left_, time_;
public:
    ExtractFile(Bridge& bridge, const fs::path& path, const Entry& item)
        : bridge_(bridge), file_(File::open(path, GENERIC_WRITE, CREATE_NEW)), left_(item.size), time_(item.time) {}
    void finish() {
        if (left_) fail(L"7z entry ended before its declared size.");
        file_.modified(time_);
    }
};
Z7_COM7F_IMF(ExtractFile::Write(const void* data, UInt32 size, UInt32* processed)) {
    if (processed) *processed = 0;
    return bridge_.call([&] {
        bridge_.job.check();
        if (size > left_) fail(L"7z entry exceeds its declared size.");
        file_.write(data, size);
        left_ -= size;
        if (processed) *processed = size;
    });
}

Z7_CLASS_IMP_COM_2(Extract, IArchiveExtractCallback, ICryptoGetTextPassword)
    Bridge& bridge_;
    IInArchive* archive_;
    Extraction* staging_;
    ExtractFile* output_ = nullptr;
    CMyComPtr<ISequentialOutStream> file_;
    bool encrypted_ = false;
public:
    Extract(Bridge& bridge, IInArchive* archive, Extraction* staging)
        : bridge_(bridge), archive_(archive), staging_(staging) {}
    Z7_IFACE_COM7_IMP(IProgress)
};
Z7_COM7F_IMF(Extract::SetTotal(UInt64 total)) {
    const std::lock_guard guard(bridge_.job.callback_lock);
    bridge_.job.total = total;
    return S_OK;
}
Z7_COM7F_IMF(Extract::SetCompleted(const UInt64* value)) {
    return bridge_.call([&] { bridge_.job.report(value ? *value : 0); });
}
Z7_COM7F_IMF(Extract::GetStream(UInt32 index, ISequentialOutStream** stream, Int32 mode)) {
    *stream = nullptr;
    return bridge_.call([&] {
        using namespace NArchive::NExtract;
        file_.Release();
        output_ = nullptr;
        if (mode == NAskMode::kSkip) return;
        const auto item = get_entry(bridge_, archive_, index);
        encrypted_ = item.encrypted();
        bridge_.job.report(0, item.path.c_str());
        if (mode != NAskMode::kExtract || !staging_) return;
        const auto output = staging_->prepare(item);
        if (!item.directory()) {
            output_ = new ExtractFile(bridge_, output, item);
            file_ = output_;
            static_cast<ISequentialOutStream*>(output_)->AddRef();
            *stream = output_;
        }
    });
}
Z7_COM7F_IMF(Extract::PrepareOperation(Int32)) { return S_OK; }
Z7_COM7F_IMF(Extract::SetOperationResult(Int32 result)) {
    return bridge_.call([&] {
        using namespace NArchive::NExtract::NOperationResult;
        if (result == kWrongPassword || (encrypted_ && (result == kDataError || result == kCRCError))) password_error();
        if (result == kCRCError) fail(L"7z entry failed CRC verification.");
        if (result == kUnsupportedMethod) fail(L"Unsupported 7z compression method.");
        if (result != kOK) fail(L"7z entry is damaged or incomplete [7z " + std::to_wstring(result) + L"]");
        if (output_) output_->finish();
        output_ = nullptr;
        file_.Release();
    });
}
Z7_COM7F_IMF(Extract::CryptoGetTextPassword(BSTR* password)) {
    *password = nullptr;
    return bridge_.call([&] {
        if (!bridge_.job.password) password_error();
        *password = SysAllocString(bridge_.job.password);
        if (!*password) fail(L"Cannot allocate 7z password.");
    });
}

void seven_create(Job& job, const fs::path& path, const std::vector<Input>& files, const sz_create_options& options) {
    Bridge bridge(job);
    auto* handler = new NArchive::N7z::CHandler;
    CMyComPtr<IOutArchive> archive(handler);
    const UInt32 threads = (std::max)(1u, static_cast<UInt32>((std::min)(U64(job.threads), job.memory / (8 * MiB))));
    U64 dictionary = U64(1) << (options.level <= 1 ? 16 : options.level <= 3 ? 20 : options.level <= 5 ? 24 : options.level <= 7 ? 25 : 26);
    dictionary = (std::max)(U64(64 * 1024), (std::min)(dictionary, job.memory / (16 * threads)));
    const W method = L"LZMA2:d" + std::to_wstring(dictionary) + L"b";
    const wchar_t* names[] = {L"x", L"0", L"mt", L"memuse", L"he", L"mtf", L"s"};
    CPropVariant values[std::size(names)];
    values[0] = static_cast<UInt32>(options.level ? options.level : 1);
    values[1] = options.level ? method.c_str() : L"Copy";
    values[2] = threads;
    values[3] = static_cast<UInt64>(job.memory);
    values[4] = options.encrypt_headers != 0;
    values[5] = threads > 1;
    values[6] = L"256m";
    bridge.verify(static_cast<ISetProperties*>(handler)->SetProperties(names, values, static_cast<UInt32>(std::size(names))), L"Cannot configure 7z compression");
    auto* output = new OutStream(bridge, path, options.volume_size);
    CMyComPtr<IOutStream> stream(output);
    CMyComPtr<IArchiveUpdateCallback> callback(new Update(bridge, files));
    bridge.verify(archive->UpdateItems(stream, static_cast<UInt32>(files.size()), callback), L"Cannot write 7z archive");
    job.report(job.total);
    output->finish();
}

void seven_read(Job& job, const fs::path& path, Extraction* staging, Selection selection, sz_entry_cb list) {
    Bridge bridge(job);
    auto* handler = new NArchive::N7z::CHandler;
    CMyComPtr<IInArchive> archive(handler);
    const wchar_t* names[] = {L"mt", L"memuse", L"mtf"};
    CPropVariant values[std::size(names)];
    values[0] = static_cast<UInt32>(job.threads);
    values[1] = static_cast<UInt64>(job.memory);
    values[2] = job.threads > 1;
    bridge.verify(static_cast<ISetProperties*>(handler)->SetProperties(names, values, static_cast<UInt32>(std::size(names))), L"Cannot configure 7z decompression");
    auto* opener = new Open(bridge);
    CMyComPtr<IArchiveOpenCallback> open(opener);
    CMyComPtr<IInStream> stream(new InStream(bridge, path));
    const UInt64 signature_offset = 0;
    const auto status = archive->Open(stream, &signature_offset, open);
    if (status == S_FALSE && opener->password_used && !bridge.error) password_error();
    bridge.verify(status, L"Cannot open 7z archive (damaged archive or missing volume)");
    CPropVariant flags;
    bridge.verify(archive->GetArchiveProperty(kpidErrorFlags, &flags), L"Cannot inspect 7z header");
    if (number(flags)) fail(L"7z header is damaged or a volume is missing.");
    UInt32 count = 0;
    bridge.verify(archive->GetNumberOfItems(&count), L"Cannot count 7z entries");
    if (!selection.all() && selection.count && selection.indices[selection.count - 1] >= count) fail(L"Entry index is out of range.");
    if (count > job.memory / 256) fail(L"Too many archive entries for the memory limit.");
    for (UInt32 i = 0; i < count; ++i) {
        job.check();
        if (!selection.includes(i)) continue;
        const auto item = get_entry(bridge, archive, i);
        if (list) list_entry(job, list, item);
        else {
            check_decoder_memory(item, job.memory - 2 * MiB);
            if (item.encrypted() && !job.password) password_error();
            if (staging) staging->validate(item);
        }
    }
    if (list || (!selection.all() && !selection.count)) return;
    CMyComPtr<IArchiveExtractCallback> extract(new Extract(bridge, archive, staging));
    bridge.verify(archive->Extract(selection.indices, selection.all() ? static_cast<UInt32>(-1) : selection.count,
        staging ? 0 : 1, extract), L"Cannot read 7z contents");
    job.report(job.total);
}
}
