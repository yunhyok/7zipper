#include "windows_raii.h"
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace fs = std::filesystem;
constexpr CLSID clsid{0x9df247fa, 0x3897, 0x4193, {0x8f, 0x57, 0x58, 0x06, 0x20, 0x56, 0x86, 0x2d}};
constexpr struct Verb {
    std::wstring_view option, title;
    const wchar_t* outputExtension;
} verbs[] = {
    {L"compress-7z", L"7z로 압축", L".7z"},
    {L"compress-zip", L"ZIP으로 압축", L".zip"},
    {L"extract-here", L"여기에 풀기", nullptr},
    {L"extract-smart", L"스마트 풀기", nullptr},
    {L"convert-7z", L"7z로 변환", L".7z"},
};
constexpr auto filename = L"탐색기 한글 📦 & 파일.txt";
constexpr std::string_view fixture = "7Zipper installed context menu integration\n";

bool Checked(const wchar_t* step, HRESULT hr) {
    std::wprintf(L"%ls -> 0x%08lX\n", step, static_cast<unsigned long>(hr));
    std::fflush(stdout);
    return SUCCEEDED(hr);
}

bool ExportBitmap(HBITMAP bitmap, const fs::path& path) {
    BITMAP dimensions{};
    if (!bitmap || !GetObjectW(bitmap, sizeof(dimensions), &dimensions) || dimensions.bmWidth <= 0 ||
        dimensions.bmHeight <= 0 || dimensions.bmWidth > 512 || dimensions.bmHeight > 512) {
        std::wprintf(L"No exportable bitmap for %ls\n", path.c_str());
        return false;
    }
    BITMAPINFO image{};
    auto& layout = image.bmiHeader;
    layout.biSize = sizeof(layout);
    layout.biWidth = dimensions.bmWidth;
    layout.biHeight = dimensions.bmHeight;
    layout.biPlanes = 1;
    layout.biBitCount = 32;
    layout.biSizeImage = layout.biWidth * layout.biHeight * 4;
    std::vector<std::byte> pixels(layout.biSizeImage);
    const auto screen = GetDC(nullptr);
    if (!screen) return false;
    const int lines = GetDIBits(screen, bitmap, 0, layout.biHeight, pixels.data(), &image, DIB_RGB_COLORS);
    ReleaseDC(nullptr, screen);
    if (lines != layout.biHeight) return false;
    BITMAPFILEHEADER file{};
    file.bfType = 0x4D42;
    file.bfOffBits = sizeof(file) + sizeof(layout);
    file.bfSize = file.bfOffBits + layout.biSizeImage;
    std::ofstream out(path, std::ios::binary);
    if (!out.write(reinterpret_cast<const char*>(&file), sizeof(file)) ||
        !out.write(reinterpret_cast<const char*>(&layout), sizeof(layout)) ||
        !out.write(reinterpret_cast<const char*>(pixels.data()), pixels.size())) return false;
    std::wprintf(L"Bitmap %ldx%ld: %ls\n", layout.biWidth, layout.biHeight, path.c_str());
    return true;
}

bool ExportIcon(HICON icon, const fs::path& path) {
    ICONINFO image{};
    if (!icon || !GetIconInfo(icon, &image)) return false;
    const bool exported = ExportBitmap(image.hbmColor, path);
    if (image.hbmColor) DeleteObject(image.hbmColor);
    if (image.hbmMask) DeleteObject(image.hbmMask);
    return exported;
}

bool ExportResourceIcon(const wchar_t* path, int index, int size, const fs::path& destination) {
    HICON icon = nullptr;
    const auto hr = SHDefExtractIconW(path, index, 0, &icon, nullptr, MAKELONG(size, 0));
    std::wprintf(L"Resource icon index=%d, requested=%d, returned=%d\n", index, size, icon != nullptr);
    const bool exported = Checked(L"Icon extraction", hr) && ExportIcon(icon, destination);
    if (icon) DestroyIcon(icon);
    return exported;
}

HRESULT CreateExplorerCommand(HMODULE library, IExplorerCommand** command) {
    if (!library) return CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(command));
    const auto getFactory = reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(library, "DllGetClassObject"));
    if (!getFactory) return LastError();
    IClassFactory* rawFactory = nullptr;
    const auto hr = getFactory(clsid, IID_PPV_ARGS(&rawFactory));
    if (!Checked(L"DllGetClassObject", hr)) return hr;
    ComPtr<IClassFactory> factory(rawFactory);
    return factory->CreateInstance(nullptr, IID_PPV_ARGS(command));
}

bool InvokeExplorerCommand(PCIDLIST_ABSOLUTE item, const fs::path& evidence, std::wstring_view title, HMODULE library = nullptr) {
    IExplorerCommand* rawRoot = nullptr;
    if (!Checked(library ? L"Create IExplorerCommand from explicit DLL" : L"CoCreateInstance registered IExplorerCommand",
        CreateExplorerCommand(library, &rawRoot))) return false;
    ComPtr<IExplorerCommand> root(rawRoot);
    IShellItemArray* rawSelection = nullptr;
    if (!Checked(L"SHCreateShellItemArrayFromIDLists", SHCreateShellItemArrayFromIDLists(1, &item, &rawSelection))) return false;
    ComPtr<IShellItemArray> selection(rawSelection);

    PWSTR rawIcon = nullptr;
    if (!Checked(L"Root GetIcon", root->GetIcon(selection.get(), &rawIcon))) return false;
    TaskString icon(rawIcon);
    if (!icon) return false;
    std::wprintf(L"GetIcon icon resource: %ls\n", icon.get());
    const int index = PathParseIconLocationW(icon.get());
    const UINT dpi = GetDpiForSystem();
    const int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
    const bool exported = ExportResourceIcon(icon.get(), index, size, evidence / L"registered-resource-icon.bmp");
    if (title.empty()) {
        for (const auto extraSize : {48, 256}) {
            const auto file = evidence / (L"registered-resource-" + std::to_wstring(extraSize) + L"px.bmp");
            ExportResourceIcon(icon.get(), index, extraSize, file);
        }
        return exported;
    }
    if (!exported) return false;

    IEnumExplorerCommand* rawCommands = nullptr;
    if (!Checked(L"EnumSubCommands", root->EnumSubCommands(&rawCommands))) return false;
    ComPtr<IEnumExplorerCommand> commands(rawCommands);
    for (;;) {
        IExplorerCommand* rawAction = nullptr;
        if (!Checked(L"Next subcommand", commands->Next(1, &rawAction, nullptr)) || !rawAction) break;
        ComPtr<IExplorerCommand> action(rawAction);
        PWSTR rawTitle = nullptr;
        if (!Checked(L"GetTitle", action->GetTitle(selection.get(), &rawTitle))) return false;
        TaskString name(rawTitle);
        if (name.get() != title) continue;
        EXPCMDSTATE state;
        if (!Checked(L"GetState", action->GetState(selection.get(), FALSE, &state)) || state != ECS_ENABLED) return false;
        std::wprintf(L"Invoking Explorer subcommand: %ls\n", name.get());
        return Checked(L"IExplorerCommand::Invoke", action->Invoke(selection.get(), nullptr));
    }
    std::wprintf(L"7Zipper Explorer subcommand missing\n");
    return false;
}

bool InvokeClassic(PCIDLIST_ABSOLUTE item, const fs::path& evidence, std::wstring_view verb) {
    IShellFolder* rawFolder = nullptr;
    PCUITEMID_CHILD child = nullptr;
    if (!Checked(L"SHBindToParent", SHBindToParent(item, IID_PPV_ARGS(&rawFolder), &child))) return false;
    ComPtr<IShellFolder> folder(rawFolder);
    std::unique_ptr<HWND__, decltype([](HWND window) { DestroyWindow(window); })> owner;
    if (verb.empty()) {
        owner.reset(CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", L"", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr,
            GetModuleHandleW(nullptr), nullptr));
        if (!owner) return Checked(L"Create hidden menu owner", LastError());
        std::wprintf(L"Hidden menu owner DPI=%u\n", GetDpiForWindow(owner.get()));
    }
    IContextMenu* rawContext = nullptr;
    if (!Checked(L"GetUIObjectOf IContextMenu", folder->GetUIObjectOf(owner.get(), 1, &child, IID_IContextMenu, nullptr,
        reinterpret_cast<void**>(&rawContext)))) return false;
    ComPtr<IContextMenu> context(rawContext);
    std::unique_ptr<HMENU__, decltype([](HMENU menu) { DestroyMenu(menu); })> menu(CreatePopupMenu());
    if (!menu) return Checked(L"CreatePopupMenu", LastError());
    constexpr UINT first = 1, last = 0x7fff;
    if (!Checked(L"QueryContextMenu", context->QueryContextMenu(menu.get(), 0, first, last, CMF_NORMAL))) return false;

    for (int i = 0; i < GetMenuItemCount(menu.get()); ++i) {
        wchar_t title[256]{};
        GetMenuStringW(menu.get(), i, title, ARRAYSIZE(title), MF_BYPOSITION);
        if (std::wstring_view(title) != L"7Zipper") continue;
        MENUITEMINFOW details{};
        details.cbSize = sizeof(details);
        details.fMask = MIIM_BITMAP | MIIM_FTYPE | MIIM_STATE;
        bool exported = false;
        if (GetMenuItemInfoW(menu.get(), i, TRUE, &details)) {
            std::wprintf(L"7Zipper root bitmap=%p type=0x%X state=0x%X\n", static_cast<void*>(details.hbmpItem), details.fType, details.fState);
            exported = ExportBitmap(details.hbmpItem, evidence / L"classic-menu-icon.bmp");
        }
        if (verb.empty()) return exported;
        auto submenu = GetSubMenu(menu.get(), i);
        if (!submenu) return false;
        IContextMenu2* rawContext2 = nullptr;
        if (!Checked(L"QueryInterface IContextMenu2", context->QueryInterface(IID_PPV_ARGS(&rawContext2)))) return false;
        ComPtr<IContextMenu2> context2(rawContext2);
        if (!Checked(L"HandleMenuMsg WM_INITMENUPOPUP", context2->HandleMenuMsg(WM_INITMENUPOPUP,
            reinterpret_cast<WPARAM>(submenu), MAKELPARAM(i, FALSE)))) return false;
        for (int sub = 0; sub < GetMenuItemCount(submenu); ++sub) {
            GetMenuStringW(submenu, sub, title, ARRAYSIZE(title), MF_BYPOSITION);
            if (title != verb) continue;
            const auto id = GetMenuItemID(submenu, sub);
            const auto state = GetMenuState(submenu, sub, MF_BYPOSITION);
            std::wprintf(L"Invoking classic subcommand: %ls, id=%u, state=0x%X\n", title, id, state);
            if (id < first || id > last || (state & (MF_DISABLED | MF_GRAYED))) return false;
            CMINVOKECOMMANDINFOEX request{};
            request.cbSize = sizeof(request);
            request.fMask = CMIC_MASK_UNICODE;
            request.lpVerb = MAKEINTRESOURCEA(id - first);
            request.lpVerbW = MAKEINTRESOURCEW(id - first);
            request.nShow = SW_SHOWNORMAL;
            return Checked(L"Classic InvokeCommand", context->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&request)));
        }
    }
    std::wprintf(L"7Zipper classic subcommand missing\n");
    return false;
}

bool WaitForFile(const fs::path& path, std::optional<uintmax_t> expectedLength = std::nullopt) {
    const auto ready = [&] {
        std::error_code error;
        const auto length = fs::file_size(path, error);
        return !error && (!expectedLength || length == *expectedLength);
    };
    for (const auto start = GetTickCount64(); !ready();) {
        if (GetTickCount64() - start > 15000) {
            std::wprintf(L"Timed out waiting for %ls\n", path.c_str());
            return false;
        }
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 50, QS_ALLINPUT);
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
    }
    return true;
}

std::wstring Quote(const fs::path& path) { return L"\"" + path.native() + L"\""; }

bool Run7za(const fs::path& verifier, const std::wstring& args, const fs::path& workingDirectory = {}) {
    auto command = Quote(verifier) + L" " + args;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(verifier.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
        workingDirectory.empty() ? nullptr : workingDirectory.c_str(), &startup, &process))
        return Checked(L"Launch independent 7za verifier", LastError());
    Handle worker(process.hProcess), thread(process.hThread);
    if (WaitForSingleObject(worker.get(), 10000) != WAIT_OBJECT_0) {
        TerminateProcess(worker.get(), 1);
        std::wprintf(L"Independent 7za verifier timed out\n");
        return false;
    }
    DWORD exit = 1;
    if (!GetExitCodeProcess(worker.get(), &exit) || exit != 0) {
        std::wprintf(L"Independent 7za exit=%lu\n", exit);
        return false;
    }
    return true;
}

bool Matches(const fs::path& path, std::string_view expected) {
    std::ifstream read(path, std::ios::binary);
    std::string bytes((std::istreambuf_iterator<char>(read)), {});
    std::wprintf(L"Content %ls: %zu bytes, match=%d\n", path.c_str(), bytes.size(), bytes == expected);
    return bytes == expected;
}

bool VerifyArchive(const fs::path& verifier, const fs::path& archive) {
    if (!WaitForFile(archive)) return false;
    std::wprintf(L"Created archive: %ls (%llu bytes)\n", archive.c_str(), static_cast<unsigned long long>(fs::file_size(archive)));
    const std::string_view signature = archive.extension() == L".zip" ? "PK\x03\x04" : "7z\xBC\xAF\x27\x1C";
    char header[6];
    std::ifstream read(archive, std::ios::binary);
    if (!read.read(header, signature.size()) || std::string_view(header, signature.size()) != signature) return false;
    read.close();
    const auto destination = archive.parent_path() / L"verified-content";
    return Run7za(verifier, L"x -y -o" + Quote(destination) + L" -- " + Quote(archive)) &&
        Matches(destination / filename, fixture);
}

int wmain(int argc, wchar_t** argv) {
    _setmode(_fileno(stdout), _O_U8TEXT);
    const bool iconOnly = argc == 3 && std::wstring_view(argv[2]) == L"icon";
    const std::wstring_view mode = iconOnly ? L"icon" : argc >= 4 ? argv[3] : L"";
    const bool libraryMode = mode == L"library" && argc == 6;
    const bool installedMode = (mode == L"direct" || mode == L"classic") && (argc == 4 || argc == 5);
    const auto command = std::ranges::find(verbs, argc >= 5 ? std::wstring_view(argv[4]) : L"compress-7z", &Verb::option);
    if ((!iconOnly && ((!installedMode && !libraryMode) ||
        !fs::is_regular_file(argv[2]) || command == std::end(verbs))) || !fs::is_directory(argv[1])) {
        std::wprintf(L"Usage: shell_installed <existing-artifacts-directory> icon\n"
                     L"       shell_installed <existing-artifacts-directory> <7za.exe> direct|classic [verb]\n"
                     L"       shell_installed <existing-artifacts-directory> <7za.exe> library <verb> <published-shell-dll>\n"
                     L"Verbs: compress-7z, compress-zip, extract-here, extract-smart, convert-7z\n");
        return 2;
    }
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) &&
        !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
        Checked(L"Set PerMonitorV2 DPI awareness", LastError());
        return 1;
    }
    const UINT dpi = GetDpiForSystem();
    std::wprintf(L"PerMonitorV2 system DPI=%u, GetSystemMetrics icons small=%d / large=%d, scaled small=%d / large=%d\n",
        dpi, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CXICON), GetSystemMetricsForDpi(SM_CXSMICON, dpi),
        GetSystemMetricsForDpi(SM_CXICON, dpi));
    if (!Checked(L"OleInitialize", OleInitialize(nullptr))) return 1;
    struct OleExit { ~OleExit() { OleUninitialize(); } } oleExit;
    try {
        std::unique_ptr<HINSTANCE__, decltype([](HMODULE module) { FreeLibrary(module); })> library;
        if (libraryMode) {
            const auto dll = fs::absolute(argv[5]);
            const auto app = dll.parent_path() / L"7Zipper.exe";
            if (!fs::is_regular_file(dll) || !fs::is_regular_file(app)) {
                std::wprintf(L"Expected published DLL and adjacent executable: %ls, %ls\n", dll.c_str(), app.c_str());
                return 2;
            }
            std::wprintf(L"Published DLL: %ls\nAdjacent executable: %ls\n", dll.c_str(), app.c_str());
            library.reset(LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32));
            if (!library) { Checked(L"Load explicit DLL", LastError()); return 1; }
        }
        GUID guid;
        wchar_t id[40];
        if (FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(guid, id, ARRAYSIZE(id))) return 1;
        const auto directory = fs::absolute(argv[1]) / (std::wstring(L"shell-installed-") + std::wstring(mode) + L"-" + id);
        if (!fs::create_directory(directory)) return 1;
        const bool extracting = !iconOnly && !command->outputExtension;
        const bool converting = !iconOnly && command->option == L"convert-7z";
        const auto verifier = iconOnly ? fs::path{} : fs::absolute(argv[2]);
        const auto source = directory / (extracting ? L"선택한 압축 📦.7z" : converting ? L"선택한 압축 📦.zip" : filename);
        constexpr auto secondFile = L"두 번째.txt";
        constexpr std::string_view secondFixture = "Second archive root\n";
        if (extracting || converting) {
            const auto seed = directory / L"seed";
            if (!fs::create_directory(seed) || !(std::ofstream(seed / filename, std::ios::binary) << fixture) ||
                !(std::ofstream(seed / secondFile, std::ios::binary) << secondFixture)) return 1;
            const std::wstring typeArg = converting ? L"-tzip" : L"-t7z";
            if (!Run7za(verifier, L"a " + typeArg + L" -y " + Quote(source) + L" -- " + Quote(filename) + L" " + Quote(secondFile), seed)) return 1;
        } else if (!(std::ofstream(source, std::ios::binary) << fixture)) return 1;
        std::wprintf(L"Fixture: %ls\n", source.c_str());
        PIDLIST_ABSOLUTE rawItem = nullptr;
        if (!Checked(L"SHParseDisplayName", SHParseDisplayName(source.c_str(), nullptr, &rawItem, 0, nullptr))) return 1;
        std::unique_ptr<void, FreeTaskMemory> item(rawItem);
        if (iconOnly) {
            const bool classic = InvokeClassic(rawItem, directory, {});
            return InvokeExplorerCommand(rawItem, directory, {}) && classic ? 0 : 1;
        }
        const bool invoked = mode == L"classic" ? InvokeClassic(rawItem, directory, command->title) :
            InvokeExplorerCommand(rawItem, directory, command->title, library.get());
        if (!invoked) return 1;
        if (extracting) {
            const auto target = command->option == L"extract-smart" ? directory / source.stem() : directory;
            std::wprintf(L"Expected extraction directory: %ls\n", target.c_str());
            const auto first = target / filename, second = target / secondFile;
            return WaitForFile(first, fixture.size()) && WaitForFile(second, secondFixture.size()) &&
                Matches(first, fixture) && Matches(second, secondFixture) && fs::is_regular_file(source) ? 0 : 1;
        }
        auto archive = source;
        archive.replace_extension(command->outputExtension);
        return VerifyArchive(verifier, archive) ? 0 : 1;
    } catch (const std::exception& error) {
        std::wprintf(L"Test error: %hs\n", error.what());
        return 1;
    }
}
