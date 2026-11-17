cmake_minimum_required(VERSION 3.20)
project(SevenZipperNative LANGUAGES C CXX)

if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "SevenZipper.Native requires an x64 MSVC developer environment.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(UNICODE _UNICODE _WIN32_WINNT=0x0A00 _CRT_SECURE_NO_WARNINGS)
add_compile_options($<$<COMPILE_LANGUAGE:CXX>:/utf-8>)
set(DEPS "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party")
set(LZMA "${DEPS}/lzma-26.06")
set(ZIP "${DEPS}/minizip-ng-4.2.2")
set(ZLIB "${DEPS}/zlib-1.4")
set(ZSTD "${DEPS}/zstd-1.5.7")
foreach(required IN ITEMS "${LZMA}/C/Lzma2Enc.c" "${ZIP}/mz_zip.c" "${ZLIB}/CMakeLists.txt" "${ZSTD}/build/cmake/CMakeLists.txt")
    if(NOT EXISTS "${required}")
        message(FATAL_ERROR "Missing ${required}. Run scripts/fetch-native.ps1.")
    endif()
endforeach()

set(ZLIB_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(ZLIB_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(ZLIB_BUILD_STATIC ON CACHE BOOL "" FORCE)
add_subdirectory("${ZLIB}" zlib EXCLUDE_FROM_ALL)
set(ZSTD_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_STATIC ON CACHE BOOL "" FORCE)
set(ZSTD_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_DICTBUILDER OFF CACHE BOOL "" FORCE)
set(ZSTD_LEGACY_SUPPORT OFF CACHE BOOL "" FORCE)
set(ZSTD_MULTITHREAD_SUPPORT ON CACHE BOOL "" FORCE)
add_subdirectory("${ZSTD}/build/cmake" zstd EXCLUDE_FROM_ALL)

# Upstream drops the AES stream without verifying the MAC on read close.
file(READ "${ZIP}/mz_zip.c" zip_source)
set(aes_close_anchor "mz_zip_entry_close_int(zip);\n\n    return err;\n}\n\nint32_t mz_zip_entry_write_close")
string(FIND "${zip_source}" "${aes_close_anchor}" anchor)
if(anchor EQUAL -1)
    message(FATAL_ERROR "minizip-ng AES verification patch must be reviewed for this version.")
endif()
string(REPLACE "${aes_close_anchor}" "if (err == MZ_OK && (zip->file_info.flag & MZ_ZIP_FLAG_ENCRYPTED))\n        err = mz_stream_close(zip->crypt_stream);\n    mz_zip_entry_close_int(zip);\n\n    return err;\n}\n\nint32_t mz_zip_entry_write_close" zip_source "${zip_source}")
# This application supplies compressed plaintext when opening a raw writer with a password.
set(raw_encrypt_anchor "if (!zip->entry_raw)\n                use_crypt = 1;")
string(FIND "${zip_source}" "${raw_encrypt_anchor}" anchor)
if(anchor EQUAL -1)
    message(FATAL_ERROR "minizip-ng raw encryption patch must be reviewed for this version.")
endif()
string(REPLACE "${raw_encrypt_anchor}" "use_crypt = 1;" zip_source "${zip_source}")

# Central directory seeks use the concatenated read view; local headers retain disk-relative offsets.
function(patch_zip before after)
    string(FIND "${zip_source}" "${before}" anchor)
    if(anchor EQUAL -1)
        message(FATAL_ERROR "minizip-ng split ZIP patch must be reviewed for this version.")
    endif()
    string(REPLACE "${before}" "${after}" zip_source "${zip_source}")
    set(zip_source "${zip_source}" PARENT_SCOPE)
endfunction()
patch_zip([=[#include "mz_zip.h"]=] [=[#include "mz_zip.h"
int64_t sz_zip_disk_start(void *stream, uint32_t disk);
uint32_t sz_zip_last_disk(void *stream);
int32_t sz_zip_finish_split(void *stream, void *directory, uint64_t entries, uint16_t version, const char *comment);]=])
patch_zip([=[    /* Total number of disks */
    if (err == MZ_OK)
        err = mz_stream_read_uint32(stream, &value32);
    /* Goto end of central directory record */]=] [=[    if (err == MZ_OK) {
        const int64_t start = sz_zip_disk_start(stream, value32);
        if (start < 0 || offset < 0 || offset > INT64_MAX - start)
            return MZ_FORMAT_ERROR;
        offset += start;
    }
    /* Total number of disks */
    if (err == MZ_OK) {
        err = mz_stream_read_uint32(stream, &value32);
        if (err == MZ_OK && value32 != sz_zip_last_disk(stream) + 1)
            return MZ_FORMAT_ERROR;
    }
    /* Goto end of central directory record */]=])
patch_zip([=[    uint64_t number_entry_cd64 = 0;]=] [=[    uint32_t end_disk = 0;
    uint64_t number_entry_cd64 = 0;]=])
patch_zip([=[    /* Read and cache central directory records */]=] [=[    end_disk = sz_zip_last_disk(zip->stream);
    /* Read and cache central directory records */]=])
patch_zip([=[        /* Number of the disk with the start of the central directory */
        if (err == MZ_OK)
            err = mz_stream_read_uint16(zip->stream, &value16);]=] [=[        if (err == MZ_OK && ((value16 != UINT16_MAX && value16 != end_disk) ||
                            eocd_pos < sz_zip_disk_start(zip->stream, end_disk)))
            return MZ_FORMAT_ERROR;
        /* Number of the disk with the start of the central directory */
        if (err == MZ_OK)
            err = mz_stream_read_uint16(zip->stream, &value16);]=])
patch_zip([=[        if (number_entry_cd != zip->number_entry && !zip->recover)
            err = MZ_FORMAT_ERROR;]=] [=[        if (zip->number_entry > number_entry_cd ||
            (end_disk == zip->disk_number_with_cd && number_entry_cd != zip->number_entry && !zip->recover))
            err = MZ_FORMAT_ERROR;
        zip->number_entry = number_entry_cd;]=])
patch_zip([=[        if ((err == MZ_OK) && ((number_entry_cd == UINT16_MAX) || (zip->cd_offset == UINT32_MAX))) {]=] [=[
        if (err == MZ_OK && (number_entry_cd == UINT16_MAX || zip->cd_offset == UINT32_MAX ||
            zip->cd_size == UINT32_MAX || zip->disk_number_with_cd == UINT16_MAX)) {]=])
patch_zip([=[                if (zip->number_entry != number_entry_cd64)
                    err = MZ_FORMAT_ERROR;]=] [=[                if (zip->number_entry > number_entry_cd64 ||
                    (value32 == zip->disk_number_with_cd && zip->number_entry != number_entry_cd64))
                    err = MZ_FORMAT_ERROR;
                zip->number_entry = number_entry_cd64;]=])
patch_zip([=[            } else if ((zip->number_entry == UINT16_MAX) || (number_entry_cd != zip->number_entry) ||
                       (zip->cd_size == UINT16_MAX) || (zip->cd_offset == UINT32_MAX)) {]=] [=[            } else {]=])
patch_zip([=[        /* Verify central directory signature exists at offset */
        err = mz_stream_seek(zip->stream, zip->cd_offset, MZ_SEEK_SET);]=] [=[        const int64_t start = sz_zip_disk_start(zip->stream, zip->disk_number_with_cd);
        if (start < 0 || zip->cd_offset > INT64_MAX - start)
            return MZ_FORMAT_ERROR;
        zip->cd_offset += start;
        /* Verify central directory signature exists at offset */
        err = mz_stream_seek(zip->stream, zip->cd_offset, MZ_SEEK_SET);]=])
patch_zip([=[            /* If cd exists in large file and no zip-64 support, error for recover */
            if (eocd_pos > UINT32_MAX && eocd_pos64 == 0)]=] [=[            /* Offset compensation is only valid for an ordinary, single-disk ZIP. */
            if (end_disk > 0 || (eocd_pos > UINT32_MAX && eocd_pos64 == 0))]=])
patch_zip([=[        } else if ((uint64_t)eocd_pos < (uint64_t)zip->cd_offset + zip->cd_size) {
            /* Truncate size of cd if incorrect size or offset provided */]=] [=[        } else if ((uint64_t)eocd_pos < (uint64_t)zip->cd_offset + zip->cd_size) {
            if (end_disk > 0)
                return MZ_FORMAT_ERROR;
            /* Truncate size of cd if incorrect size or offset provided */]=])
patch_zip([=[    if (mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_NUMBER, &disk_number) == MZ_OK)
        zip->disk_number_with_cd = (uint32_t)disk_number;
    if (mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_SIZE, &disk_size) == MZ_OK && disk_size > 0)
        zip->disk_number_with_cd += 1;]=] [=[    if (mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_SIZE, &disk_size) == MZ_OK && disk_size > 0)
        return sz_zip_finish_split(zip->stream, zip->cd_mem_stream, zip->number_entry, zip->version_madeby, zip->comment);
    if (mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_NUMBER, &disk_number) == MZ_OK)
        zip->disk_number_with_cd = (uint32_t)disk_number;]=])
patch_zip([=[    if (disk_number == zip->disk_number_with_cd) {
        mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_SIZE, &disk_size);
        if ((disk_size == 0) || ((zip->open_mode & MZ_OPEN_MODE_WRITE) == 0))
            disk_number = (uint32_t)-1;
    }]=] [=[    if ((zip->open_mode & MZ_OPEN_MODE_WRITE) && disk_number == zip->disk_number_with_cd) {
        mz_stream_get_prop_int64(zip->stream, MZ_STREAM_PROP_DISK_SIZE, &disk_size);
        if (disk_size == 0)
            disk_number = (uint32_t)-1;
    }]=])
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/mz_zip_checked.c" "${zip_source}")

set(zip_sources mz_crypt.c mz_crypt_winvista.c mz_os.c mz_os_win32.c mz_strm.c
    mz_strm_mem.c mz_strm_os_win32.c mz_strm_pkcrypt.c mz_strm_split.c mz_strm_wzaes.c
    mz_strm_zlib.c mz_strm_zstd.c)
list(TRANSFORM zip_sources PREPEND "${ZIP}/")
add_library(minizip STATIC ${zip_sources} "${CMAKE_CURRENT_BINARY_DIR}/mz_zip_checked.c")
set(HAVE_STDINT_H 1)
set(HAVE_INTTYPES_H 1)
configure_file("${ZIP}/mz_config.h.cmakein" "${CMAKE_CURRENT_BINARY_DIR}/mz_config.h" @ONLY)
target_include_directories(minizip PUBLIC "${ZIP}" "${CMAKE_CURRENT_BINARY_DIR}")
target_compile_definitions(minizip PRIVATE HAVE_ZLIB ZLIB_COMPAT HAVE_ZSTD HAVE_PKCRYPT HAVE_WZAES)
target_link_libraries(minizip PRIVATE ZLIB::ZLIBSTATIC libzstd_static bcrypt)

# Format7zR's handler and C codecs, plus AES and PPMd from the same LZMA SDK.
set(common CRC CrcReg IntToString LzFindPrepare NewHandler MyString StringConvert StringToInt
    MyVector Wildcard Sha256Prepare)
set(win FileDir FileFind FileIO FileName PropVariant Synchronization System TimeUtils)
set(core CreateCoder CWrappers InBuffer InOutTempBuffer FilterCoder LimitedStreams MethodId
    MethodProps OutBuffer ProgressUtils PropId StreamBinder StreamObjects StreamUtils UniqBlocks VirtThread)
set(ar CoderMixer2 HandlerOut InStreamWithCRC ItemNameUtils OutStreamWithCRC ParseProperties)
set(seven 7zCompressionMode 7zDecode 7zEncode 7zExtract 7zFolderInStream 7zHandler
    7zHandlerOut 7zHeader 7zIn 7zOut 7zProperties 7zSpecStream 7zUpdate)
set(codec Bcj2Coder Bcj2Register BcjCoder BcjRegister BranchMisc BranchRegister ByteSwap
    CopyCoder CopyRegister DeltaFilter Lzma2Decoder Lzma2Encoder Lzma2Register LzmaDecoder
    LzmaEncoder LzmaRegister PpmdDecoder PpmdEncoder PpmdRegister)
set(crypt 7zAes 7zAesRegister MyAes MyAesReg RandGen)
set(c_code 7zStream Alloc Bcj2 Bcj2Enc Bra Bra86 BraIA64 CpuArch Delta LzFind LzFindMt LzFindOpt
    Lzma2Dec Lzma2DecMt Lzma2Enc LzmaDec LzmaEnc MtCoder MtDec SwapBytes Threads
    7zCrc 7zCrcOpt Aes AesOpt Sha256 Sha256Opt Sort Ppmd7 Ppmd7Dec Ppmd7Enc)
set(sdk_sources)
macro(sdk_files dir ext values)
    foreach(base IN LISTS ${values})
        list(APPEND sdk_sources "${LZMA}/${dir}/${base}.${ext}")
    endforeach()
endmacro()
sdk_files(CPP/Common cpp common)
sdk_files(CPP/Windows cpp win)
sdk_files(CPP/7zip/Common cpp core)
sdk_files(CPP/7zip/Archive/Common cpp ar)
sdk_files(CPP/7zip/Archive/7z cpp seven)
sdk_files(CPP/7zip/Compress cpp codec)
sdk_files(CPP/7zip/Crypto cpp crypt)
sdk_files(C c c_code)

add_library(SevenZipper.Native SHARED engine.cpp seven.cpp zip.cpp ${sdk_sources})
target_compile_definitions(SevenZipper.Native PRIVATE SEVENZIPPER_EXPORTS)
target_include_directories(SevenZipper.Native PRIVATE "${LZMA}/CPP")
target_link_libraries(SevenZipper.Native PRIVATE minizip ZLIB::ZLIBSTATIC libzstd_static ole32 oleaut32 advapi32)

