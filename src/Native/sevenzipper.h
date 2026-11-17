#pragma once
#include <stdint.h>
#include <wchar.h>

#ifdef SEVENZIPPER_EXPORTS
#define SZ_API __declspec(dllexport)
#else
#define SZ_API __declspec(dllimport)
#endif
#define SZ_CALL __cdecl

#ifdef __cplusplus
extern "C" {
#endif

enum { SZ_OK, SZ_CANCELED, SZ_ERROR, SZ_PASSWORD_REQUIRED };
enum { SZ_7Z, SZ_ZIP, SZ_ZIP_ZSTD };
enum { SZ_DIRECTORY = 1, SZ_ENCRYPTED = 2, SZ_UNSAFE = 4 };

typedef struct sz_entry {
    uint32_t index;
    uint32_t flags;
    const wchar_t* path;
    const wchar_t* method;
    uint64_t size;
    uint64_t packed_size;
    uint64_t modified; /* UTC Windows FILETIME, zero when unavailable */
} sz_entry;

typedef struct sz_input {
    const wchar_t* source;
    const wchar_t* name;
    uint32_t flags; /* SZ_DIRECTORY if a directory */
} sz_input;

typedef struct sz_create_options {
    uint32_t format;
    uint32_t level;
    uint32_t threads;
    uint32_t encrypt_headers;
    uint64_t memory_limit;
    uint64_t volume_size;
    const wchar_t* password; /* NULL means no encryption */
} sz_create_options;

typedef struct sz_read_options {
    uint32_t threads;
    uint64_t memory_limit;
    const wchar_t* password;
} sz_read_options;

typedef int (SZ_CALL *sz_entry_cb)(void* context, const sz_entry* entry);
typedef int (SZ_CALL *sz_progress_cb)(void* context, const wchar_t* stage,
    uint64_t completed, uint64_t total, const wchar_t* current_file);

SZ_API int SZ_CALL sz_list(const wchar_t* archive, const sz_read_options* options,
    sz_entry_cb entry, sz_progress_cb progress, void* context);
SZ_API int SZ_CALL sz_create(const wchar_t* archive, const sz_input* inputs, uint32_t count,
    const sz_create_options* options, sz_progress_cb progress, void* context);
/* NULL indices means all; non-NULL with zero count means none. */
SZ_API int SZ_CALL sz_extract(const wchar_t* archive, const wchar_t* destination,
    const uint32_t* indices, uint32_t count, const sz_read_options* options,
    sz_progress_cb progress, void* context);
SZ_API int SZ_CALL sz_test(const wchar_t* archive, const sz_read_options* options,
    sz_progress_cb progress, void* context);
/* Thread-local; valid until the next call on the invoking thread. */
SZ_API const wchar_t* SZ_CALL sz_last_error(void);
SZ_API const wchar_t* SZ_CALL sz_version(void);

#ifdef __cplusplus
}
#endif
