#undef NDEBUG
#include "drag.cpp"
#include <shellapi.h>
#include <cassert>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;

fs::path NewStage() {
    wchar_t* rawLocal = nullptr;
    assert(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &rawLocal)));
    TaskString local(rawLocal);
    GUID id;
    wchar_t raw[40];
    assert(SUCCEEDED(CoCreateGuid(&id)));
    assert(StringFromGUID2(id, raw, 40));
    std::wstring name;
    for (auto ch : raw) if (iswxdigit(ch)) name += ch;
    fs::path job = fs::path(local.get()) / L"7Zipper" / L"Drag" / name;
    assert(fs::create_directories(job));
    return job;
}

int wmain() {
    assert(SUCCEEDED(OleInitialize(nullptr)));
    const auto job = NewStage();
    const auto empty = job / L"빈 폴더";
    const auto folder = job / L"자료📂";
    assert(fs::create_directory(empty));
    assert(fs::create_directory(folder));
    for (int i = 0; i < 90; ++i)
        assert(std::ofstream(folder / (std::to_wstring(i) + L" 한글 자료.txt")) << "file");
    assert(SetFileAttributesW((folder / L"7 한글 자료.txt").c_str(), FILE_ATTRIBUTE_READONLY));

    Staging stage;
    assert(SUCCEEDED(stage.Open(job.native())));
    assert(FAILED(stage.CheckPath((job / L".." / L"unrelated.txt").native())));
    const wchar_t* paths[] = {folder.c_str(), empty.c_str()};
    std::vector<std::byte> drop;
    assert(SUCCEEDED(BuildDrop(stage, paths, 2, drop)));
    auto data = new DataObject(std::move(drop), std::move(stage));
    ComPtr<IDataObject> source(data);
    assert(sz_drag_active() == 1);
    assert(SUCCEEDED(data->SetAsyncMode(TRUE)));

    IDataObjectAsyncCapability* rawAsync = nullptr;
    assert(SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&rawAsync))));
    ComPtr<IDataObjectAsyncCapability> target(rawAsync);
    BOOL status = FALSE;
    assert(SUCCEEDED(target->GetAsyncMode(&status)) && status);
    assert(SUCCEEDED(target->StartOperation(nullptr)));
    assert(SUCCEEDED(target->InOperation(&status)) && status);
    FORMATETC fmt{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium{};
    assert(SUCCEEDED(source->GetData(&fmt, &medium)));
    auto hdrop = static_cast<HDROP>(medium.hGlobal);
    assert(DragQueryFileW(hdrop, 0xffffffff, nullptr, 0) == 2);
    wchar_t path[MAX_PATH];
    assert(DragQueryFileW(hdrop, 1, path, MAX_PATH) == empty.native().size() && empty == path);

    data->SetOutcome(DRAGDROP_S_DROP, DROPEFFECT_COPY);
    source.reset();
    assert(sz_drag_active() == 1 && fs::exists(empty));
    assert(SUCCEEDED(target->EndOperation(S_OK, nullptr, DROPEFFECT_COPY)));
    target.reset();
    assert(sz_drag_active() == 1 && fs::exists(folder));
    ReleaseStgMedium(&medium);
    assert(sz_drag_active() == 0 && !fs::exists(job));

    const auto canceled = NewStage();
    Staging canceledStage;
    assert(SUCCEEDED(canceledStage.Open(canceled.native())));
    auto canceledData = new DataObject({}, std::move(canceledStage));
    canceledData->SetOutcome(DRAGDROP_S_CANCEL, DROPEFFECT_NONE);
    canceledData->Release();
    assert(!fs::exists(canceled));

    const auto unknown = NewStage();
    Staging unknownStage;
    assert(SUCCEEDED(unknownStage.Open(unknown.native())));
    auto unknownData = new DataObject({}, std::move(unknownStage));
    assert(SUCCEEDED(unknownData->StartOperation(nullptr)));
    unknownData->SetOutcome(DRAGDROP_S_DROP, DROPEFFECT_COPY);
    unknownData->Release(); // A target that abandoned all references cannot finish the transfer.
    assert(sz_drag_active() == 0 && fs::exists(unknown));
    { Staging cleanTestJob; assert(SUCCEEDED(cleanTestJob.Open(unknown.native()))); }
    assert(!fs::exists(unknown));
    OleUninitialize();
}
