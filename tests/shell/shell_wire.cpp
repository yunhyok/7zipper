#undef NDEBUG
#include "windows_raii.h"
#include <shlobj.h>
#include <algorithm>
#include <array>
#include <cassert>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace fs = std::filesystem;
constexpr CLSID clsid{0x9df247fa, 0x3897, 0x4193, {0x8f, 0x57, 0x58, 0x06, 0x20, 0x56, 0x86, 0x2d}};
constexpr auto captureVar = L"SEVENZIPPER_TEST_CAPTURE";
constexpr auto unrelatedVar = L"SEVENZIPPER_TEST_UNRELATED_HANDLE";
using ReleaseModule = decltype([](HMODULE module) { FreeLibrary(module); });

int Probe(int argc, wchar_t** argv) {
    assert(argc == 3 && std::wstring_view(argv[1]) == L"--shell-request");
    auto handle = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_wcstoui64(argv[2], nullptr, 10)));
    assert(GetFileType(handle) == FILE_TYPE_PIPE);
    wchar_t value[32768];
    assert(GetEnvironmentVariableW(unrelatedVar, value, ARRAYSIZE(value)));
    auto unrelated = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_wcstoui64(value, nullptr, 10)));
    assert(!SetEvent(unrelated));
    assert(GetEnvironmentVariableW(captureVar, value, ARRAYSIZE(value)));
    const auto capture = fs::path(value);
    auto inProgress = capture;
    inProgress += L".tmp";
    std::ofstream out(inProgress, std::ios::binary);
    DWORD read = 0;
    std::array<char, 4096> buf;
    while (ReadFile(handle, buf.data(), static_cast<DWORD>(buf.size()), &read, nullptr) && read)
        assert(out.write(buf.data(), read));
    assert(GetLastError() == ERROR_BROKEN_PIPE);
    CloseHandle(handle);
    out.close();
    fs::rename(inProgress, capture);
    return 0;
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 3) return Probe(argc, argv);
    assert(argc == 2);
    assert(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)));
    wchar_t modulePath[32768];
    assert(GetModuleFileNameW(nullptr, modulePath, ARRAYSIZE(modulePath)));
    const auto dir = fs::path(modulePath).parent_path();
    const fs::path dll(argv[1]);
    // Put a private copy beside the probe: command activation locates its executable from the DLL.
    const auto besideProbe = dir / dll.filename();
    fs::copy_file(dll, besideProbe, fs::copy_options::overwrite_existing);
    std::unique_ptr<HINSTANCE__, ReleaseModule> module(LoadLibraryW(besideProbe.c_str()));
    assert(module);
    auto factoryFunction = reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(module.get(), "DllGetClassObject"));
    assert(factoryFunction);
    {
        IClassFactory* rawFactory = nullptr;
        assert(SUCCEEDED(factoryFunction(clsid, IID_PPV_ARGS(&rawFactory))));
        ComPtr<IClassFactory> factory(rawFactory);
        IExplorerCommand* rawRoot = nullptr;
        assert(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&rawRoot))));
        ComPtr<IExplorerCommand> root(rawRoot);
        EXPCMDFLAGS flags;
        assert(SUCCEEDED(root->GetFlags(&flags)) && (flags & ECF_HASSUBCOMMANDS));
        IEnumExplorerCommand* rawCommands = nullptr;
        assert(SUCCEEDED(root->EnumSubCommands(&rawCommands)));
        ComPtr<IEnumExplorerCommand> commands(rawCommands);
        IExplorerCommand* rawActions[6]{};
        ULONG fetched = 0;
        assert(commands->Next(6, rawActions, &fetched) == S_OK && fetched == 6);
        std::array<ComPtr<IExplorerCommand>, 6> actions;
        for (size_t i = 0; i < actions.size(); ++i) actions[i].reset(rawActions[i]);

        GUID jobId;
        wchar_t name[40];
        assert(SUCCEEDED(CoCreateGuid(&jobId)) && StringFromGUID2(jobId, name, 40));
        const auto job = fs::temp_directory_path() / (std::wstring(L"7Zipper-Wire-") + name);
        assert(fs::create_directory(job));
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        Handle unrelated(CreateEventW(&security, TRUE, FALSE, nullptr));
        assert(unrelated);
        const auto unrelatedString = std::to_wstring(reinterpret_cast<uintptr_t>(unrelated.get()));
        assert(SetEnvironmentVariableW(unrelatedVar, unrelatedString.c_str()));
        const auto capture = job / L"capture.json";
        assert(SetEnvironmentVariableW(captureVar, capture.c_str()));

        constexpr unsigned count = 650;
        std::vector<PCIDLIST_ABSOLUTE> pidls;
        std::vector<fs::path> inputs;
        size_t argvUnits = 0;
        for (unsigned i = 0; i < count; ++i) {
            auto file = job / (std::to_wstring(i) + L" 선택 자료 & 공유 📂.7z");
            assert(std::ofstream(file).put('x'));
            PIDLIST_ABSOLUTE item = nullptr;
            assert(SUCCEEDED(SHParseDisplayName(file.c_str(), nullptr, &item, 0, nullptr)));
            pidls.push_back(item);
            argvUnits += file.native().size();
            inputs.push_back(std::move(file));
        }
        assert(argvUnits > 32767);
        IShellItemArray* rawItems = nullptr;
        assert(SUCCEEDED(SHCreateShellItemArrayFromIDLists(count, pidls.data(), &rawItems)));
        ComPtr<IShellItemArray> items(rawItems);
        for (const auto pidl : pidls) CoTaskMemFree(const_cast<ITEMIDLIST*>(pidl));
        EXPCMDSTATE state;
        assert(SUCCEEDED(actions[3]->GetState(items.get(), FALSE, &state)) && state == ECS_ENABLED);

        const auto archive = job / L"Explorer archive.zip";
        constexpr char emptyZip[22] = {'P', 'K', '\x05', '\x06'};
        assert(std::ofstream(archive, std::ios::binary).write(emptyZip, sizeof(emptyZip)));
        const auto zipPart = job / L"split.z01";
        assert(std::ofstream(zipPart, std::ios::binary).put('x'));
        const auto splitPart = job / L"split.7z.002";
        assert(std::ofstream(splitPart, std::ios::binary).put('x'));
        const auto notSplit = job / L"split.7z.txt";
        assert(std::ofstream(notSplit, std::ios::binary).put('x'));
        const auto folder = job / L"Ordinary folder.7z";
        assert(fs::create_directory(folder));
        const std::tuple<fs::path, EXPCMDSTATE, EXPCMDSTATE> checks[] = {
            {archive, ECS_ENABLED, ECS_ENABLED},
            {zipPart, ECS_ENABLED, ECS_ENABLED},
            {splitPart, ECS_ENABLED, ECS_HIDDEN},
            {notSplit, ECS_HIDDEN, ECS_HIDDEN},
            {folder, ECS_HIDDEN, ECS_HIDDEN}
        };
        for (const auto& [path, expectedExtract, expectedConvert] : checks) {
            PIDLIST_ABSOLUTE pidl = nullptr;
            assert(SUCCEEDED(SHParseDisplayName(path.c_str(), nullptr, &pidl, 0, nullptr)));
            PCIDLIST_ABSOLUTE selection[] = {pidl};
            IShellItemArray* rawSelection = nullptr;
            assert(SUCCEEDED(SHCreateShellItemArrayFromIDLists(1, selection, &rawSelection)));
            CoTaskMemFree(pidl);
            ComPtr<IShellItemArray> singleItem(rawSelection);
            for (auto action : {3, 4})
                assert(SUCCEEDED(actions[action]->GetState(singleItem.get(), FALSE, &state)) && state == expectedExtract);
            assert(SUCCEEDED(actions[5]->GetState(singleItem.get(), FALSE, &state)) && state == expectedConvert);
        }
        assert(fs::remove(archive) && fs::remove(zipPart) && fs::remove(splitPart) && fs::remove(notSplit) && fs::remove(folder));

        assert(SUCCEEDED(actions[0]->Invoke(items.get(), nullptr)));
        for (int retry = 0; retry < 500 && !fs::exists(capture); ++retry) Sleep(10);
        assert(fs::exists(capture));
        std::ifstream in(capture, std::ios::binary);
        const std::string body((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        in.close();
        assert(body.starts_with(R"({"operation":"compress-7z","paths":[)"));
        assert(body.ends_with(R"(자료 & 공유 📂.7z"]})"));
        assert(static_cast<unsigned>(std::count(body.begin(), body.end(), ',')) == count);
        assert(WaitForSingleObject(unrelated.get(), 0) == WAIT_TIMEOUT);
        for (auto& file : inputs) assert(fs::remove(file));
        assert(fs::remove(capture) && fs::remove(job));
    }
    CoUninitialize();
}
