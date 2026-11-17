"""Run with a Win64 native DLL and an independent 7za.exe:
python tests/native/large.py build/native/SevenZipper.Native.dll .tools/7z2606-extra/x64/7za.exe
The test uses up to 15 GiB of temporary disk space and retains failed fixtures.
"""
import ctypes as C
import hashlib
from pathlib import Path
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zipfile

MIB = 1024 ** 2
GIB = 1024 ** 3
CHUNK = MIB
MEMORY = 128 * MIB
THREADS = 2


class Input(C.Structure):
    _fields_ = [("source", C.c_wchar_p), ("name", C.c_wchar_p), ("flags", C.c_uint32)]


class Create(C.Structure):
    _fields_ = [(name, C.c_uint32) for name in ("format", "level", "threads", "headers")] + [
        ("memory", C.c_uint64), ("volume", C.c_uint64), ("password", C.c_wchar_p)]


class Read(C.Structure):
    _fields_ = [("threads", C.c_uint32), ("memory", C.c_uint64), ("password", C.c_wchar_p)]


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def timed(description, action, *args):
    start = time.perf_counter()
    result = action(*args)
    print(f"{description} ({time.perf_counter() - start:.2f} s)", flush=True)
    return result


def write_pattern(path, size, block):
    digest = hashlib.sha256()
    with path.open("xb", buffering=CHUNK) as output:
        left = size
        while left:
            chunk = block[:min(left, len(block))]
            output.write(chunk)
            digest.update(chunk)
            left -= len(chunk)
    check(path.stat().st_size == size, f"Source length mismatch for {path}")
    return digest.hexdigest()


def digest_file(path):
    with path.open("rb", buffering=CHUNK) as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def run_case(dll, external, directory, label, size, block, level, volume=0):
    source = directory / f"{label}.bin"
    expected = timed(f"{label}: wrote {size:,} source bytes", write_pattern, source, size, block)
    archive = directory / f"{label}.zip"
    options = Create(1, level, THREADS, 0, MEMORY, volume, None)
    read = Read(THREADS, MEMORY, None)
    entry = Input(str(source), source.name, 0)

    def require_status(status):
        check(status == 0, f"Native status {status}: {dll.sz_last_error()}")

    timed(f"{label}: native create", lambda: require_status(
        dll.sz_create(str(archive), C.byref(entry), 1, C.byref(options), None, None)))
    if volume:
        first = archive.with_suffix(".z01")
        second = archive.with_suffix(".z02")
        check(first.stat().st_size == volume, f"Expected a {volume:,}-byte first volume")
        with first.open("rb") as data:
            check(data.read(4) == b"PK\x07\x08", "Missing PKWARE split signature")
        last_data_size = second.stat().st_size
        check(0 < last_data_size <= volume, "Wrong second ZIP volume size")
        with archive.open("rb") as data:
            data.seek(-22, 2)
            signature, disk, cd_disk = struct.unpack("<4sHH", data.read(8))
        check((signature, disk, cd_disk) == (b"PK\x05\x06", 2, 2), "Wrong ZIP end record volume numbers")
        print(f"{label}: volume sizes {volume:,}, {last_data_size:,}, {archive.stat().st_size:,}", flush=True)
    else:
        with zipfile.ZipFile(archive) as zip64:
            info = zip64.getinfo(source.name)
            check(info.file_size == size and info.extract_version >= 45 and info.compress_type == zipfile.ZIP_DEFLATED,
                  "Wrong ZIP64 metadata or compression method")
        print(f"{label}: {archive.stat().st_size:,}-byte ZIP64 archive", flush=True)

    timed(f"{label}: native integrity test", lambda: require_status(
        dll.sz_test(str(archive), C.byref(read), None, None)))
    target = directory / f"{label}-extracted"
    target.mkdir()
    timed(f"{label}: native extract", lambda: require_status(
        dll.sz_extract(str(archive), str(target), None, 0, C.byref(read), None, None)))
    extracted = target / source.name
    check(extracted.stat().st_size == size, f"Extracted length mismatch for {label}")
    actual = timed(f"{label}: extracted SHA-256", digest_file, extracted)
    check(actual == expected, f"Extracted digest mismatch for {label}: {actual} != {expected}")
    print(f"{label}: SHA-256 {actual}", flush=True)

    def validate_external():
        result = subprocess.run([str(external), "t", "-bd", "-bb0", "-mmt=2", str(archive)],
                                cwd=directory, capture_output=True, text=True, errors="replace")
        check(result.returncode == 0, f"7-Zip exit {result.returncode}\n{result.stdout}\n{result.stderr}")
        check("Everything is Ok" in result.stdout, f"7-Zip did not confirm the test\n{result.stdout}")
        print(result.stdout.strip(), flush=True)

    timed(f"{label}: independent 7-Zip test", validate_external)


def main():
    check(len(sys.argv) == 3, "Expected paths to SevenZipper.Native.dll and 7za.exe.")
    dll_path, external = [Path(path).resolve(strict=True) for path in sys.argv[1:]]
    workspace = Path(__file__).resolve().parents[2]
    parent = (workspace / "artifacts" / "native-large").resolve()
    check(parent.is_relative_to(workspace) and parent != workspace, "Scratch must be inside the workspace.")
    parent.mkdir(parents=True, exist_ok=True)
    check(shutil.disk_usage(parent).free >= 15 * GIB, "The large file tests require 15 GiB free disk space.")
    directory = Path(tempfile.mkdtemp(prefix="work-", dir=parent))

    dll = C.CDLL(str(dll_path))
    dll.sz_create.argtypes = (C.c_wchar_p, C.POINTER(Input), C.c_uint32, C.POINTER(Create), C.c_void_p, C.c_void_p)
    dll.sz_test.argtypes = (C.c_wchar_p, C.POINTER(Read), C.c_void_p, C.c_void_p)
    dll.sz_extract.argtypes = (C.c_wchar_p, C.c_wchar_p, C.POINTER(C.c_uint32), C.c_uint32,
                              C.POINTER(Read), C.c_void_p, C.c_void_p)
    dll.sz_version.restype = dll.sz_last_error.restype = C.c_wchar_p
    print(f"{dll.sz_version()}\nDLL SHA-256 {digest_file(dll_path)}\nScratch {directory}", flush=True)
    start = time.perf_counter()
    try:
        run_case(dll, external, directory, "zip64-deflate", 4 * GIB + 17, bytes(CHUNK), 1)
        run_case(dll, external, directory, "split-store", 2 * GIB + 32 * MIB,
                 random.Random(14).randbytes(CHUNK), 0, 2 * GIB + MIB)
    except BaseException:
        print(f"Failed fixtures retained in {directory}", flush=True)
        raise
    cleanup = directory.resolve(strict=True)
    check(cleanup.parent == parent and cleanup.name.startswith("work-"), "Unexpected scratch path.")
    shutil.rmtree(cleanup)
    print(f"PASS both large file cases; elapsed {time.perf_counter() - start:.2f} s; scratch removed.", flush=True)


if __name__ == "__main__":
    main()
