"""Run: python tests/native/smoke.py <SevenZipper.Native.dll> [optional 7za.exe]."""
import ctypes as C
from pathlib import Path
import random
import struct
import subprocess
import sys
import tempfile
import zipfile


class Input(C.Structure):
    _fields_ = [("source", C.c_wchar_p), ("name", C.c_wchar_p), ("flags", C.c_uint32)]


class Create(C.Structure):
    _fields_ = [(x, C.c_uint32) for x in ("format", "level", "threads", "headers")] + [
        ("memory", C.c_uint64), ("volume", C.c_uint64), ("password", C.c_wchar_p)]


class Read(C.Structure):
    _fields_ = [("threads", C.c_uint32), ("memory", C.c_uint64), ("password", C.c_wchar_p)]


class Entry(C.Structure):
    _fields_ = [("index", C.c_uint32), ("flags", C.c_uint32), ("path", C.c_wchar_p), ("method", C.c_wchar_p),
                ("size", C.c_uint64), ("packed", C.c_uint64), ("modified", C.c_uint64)]


EntryCallback = C.CFUNCTYPE(C.c_int, C.c_void_p, C.POINTER(Entry))
Progress = C.CFUNCTYPE(C.c_int, C.c_void_p, C.c_wchar_p, C.c_uint64, C.c_uint64, C.c_wchar_p)
assert C.sizeof(Create) == 40 and C.sizeof(Entry) == 48
assert C.sizeof(Read) == C.sizeof(Input) == 24
dll = C.CDLL(str(Path(sys.argv[1]).resolve()))
dll.sz_last_error.restype = dll.sz_version.restype = C.c_wchar_p
dll.sz_create.argtypes = (C.c_wchar_p, C.POINTER(Input), C.c_uint32, C.POINTER(Create), Progress, C.c_void_p)
dll.sz_list.argtypes = (C.c_wchar_p, C.POINTER(Read), EntryCallback, Progress, C.c_void_p)
dll.sz_test.argtypes = (C.c_wchar_p, C.POINTER(Read), Progress, C.c_void_p)
dll.sz_extract.argtypes = (C.c_wchar_p, C.c_wchar_p, C.POINTER(C.c_uint32), C.c_uint32,
                           C.POINTER(Read), Progress, C.c_void_p)
exe = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
MiB = 1024 * 1024


def check(code, expected=0, label=""):
    assert code == expected, (label, code, expected, dll.sz_last_error())


def cli(*args, cwd):
    result = subprocess.run([str(exe), *map(str, args)], cwd=cwd, capture_output=True)
    assert result.returncode == 0, result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace")


def list_names(path, password=None, expected=0):
    output = []

    @EntryCallback
    def listed(_, entry):
        e = entry.contents
        output.append((e.path.replace("\\", "/"), e.flags, e.method, e.size))
        return 1

    check(dll.sz_list(str(path), Read(4, 96 * MiB, password), listed, Progress(), None), expected, path)
    return output


def test(path, password=None, expected=0):
    check(dll.sz_test(str(path), Read(4, 96 * MiB, password), Progress(), None), expected, path)


def extract(path, out, indices=None, password=None, expected=0):
    out.mkdir()
    selection = (C.c_uint32 * len(indices))(*indices) if indices is not None else None
    check(dll.sz_extract(str(path), str(out), selection, len(indices or ()), Read(4, 96 * MiB, password),
                         Progress(), None), expected, path)


def create(path, items, fmt, password=None, headers=False, split=0, level=5, **kw):
    options = Create(fmt, level, 4, headers, kw.get("memory", 96 * MiB), split, password)
    inputs = (Input * len(items))(*(Input(str(path), name, flags) for path, name, flags in items))
    return dll.sz_create(str(path), inputs, len(inputs), options, kw.get("progress", Progress()), None)


with tempfile.TemporaryDirectory(prefix="7zipper-test-") as where:
    root = Path(where)
    source = root / "input"
    source.mkdir()
    name = "\ud55c\uae00.txt"
    (source / name).write_bytes(random.Random(9).randbytes(400_000))
    (source / "small").write_bytes(b"a" * 700_000)
    (source / "empty").write_bytes(b"")
    (source / "folder").mkdir()
    paths = [(source / name, f"folder/{name}", 0), (source / "folder", "folder", 1),
             (source / "small", "small", 0), (source / "empty", "empty", 0)]

    for fmt, suffix, method in [(0, ".7z", "LZMA2"), (1, ".zip", "Deflate"), (2, ".zip", "Zstandard")]:
        base = root / f"roundtrip{fmt}{suffix}"
        check(create(base, paths, fmt), label=base)
        rows = list_names(base)
        by_name = {p: (i, flags, codec, size) for i, (p, flags, codec, size) in enumerate(rows)}
        assert set(by_name) == {p[1] for p in paths}, rows
        assert method in by_name["small"][2] and by_name["folder"][1] & 1, rows
        test(base)
        out = root / f"selected{fmt}"
        extract(base, out, [by_name[f"folder/{name}"][0]])
        assert (out / "folder" / name).read_bytes() == (source / name).read_bytes()
        assert not (out / "small").exists()
        out = root / f"all{fmt}"
        extract(base, out)
        for original, dest, flags in paths:
            if not flags:
                assert (out / dest).read_bytes() == original.read_bytes(), dest
        check(create(base, paths, fmt), expected=2, label="no output overwrite")
        test(base)
        if exe:
            cli("t", base, cwd=root)

    for fmt, ext in [(0, ".7z"), (1, ".zip"), (2, ".zip")]:
        archive = root / f"encrypted{fmt}{ext}"
        password = "correct horse"
        check(create(archive, paths, fmt, password, fmt == 0), label=archive)
        if fmt == 0:
            list_names(archive, expected=3)
            list_names(archive, "wrong password", expected=3)
        assert any(flag & 2 for _, flag, _, _ in list_names(archive, password))
        test(archive, expected=3)
        test(archive, "wrong password", expected=3)
        test(archive, password)
        if exe:
            cli("t", f"-p{password}", archive, cwd=root)

    encrypted = root / "encrypted1.zip"
    tampered = bytearray(encrypted.read_bytes())
    with zipfile.ZipFile(encrypted) as archive:
        first = archive.infolist()[0]
    assert first.compress_type == 99 and first.compress_size > 30
    head = first.header_offset
    flen, elen = struct.unpack_from("<HH", tampered, head + 26)
    tampered[head + 30 + flen + elen + first.compress_size - 1] ^= 1
    corrupt = root / "mac-tampered.zip"
    corrupt.write_bytes(tampered)
    test(corrupt, "correct horse", expected=2)
    assert "authentication" in dll.sz_last_error().lower(), dll.sz_last_error()

    for fmt, ext, tail in [(0, ".7z", ".001"), (1, ".zip", ""), (2, ".zip", "")]:
        base = root / f"split{fmt}{ext}"
        volume = 64 * 1024
        check(create(base, paths, fmt, split=volume), label=base)
        parts = list(root.glob(f"split{fmt}*"))
        assert len(parts) > 2 and all(p.stat().st_size <= volume for p in parts), parts
        actual = Path(str(base) + tail)
        test(actual)
        extract(actual, root / f"splitout{fmt}")
        if exe:
            cli("t", actual, cwd=root)
        next(p for p in parts if p.suffix in (".002", ".z02")).rename(root / f"missing{fmt}")
        test(actual, expected=2)

    # A PKWARE split archive can contain payload and the central directory on the final disk.
    apart = root / "mixed.zip"
    with zipfile.ZipFile(apart, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.write(source / name, "bytes.dat")
    original = apart.read_bytes()
    split_at = 128 * 1024 - 4
    final = bytearray(original[split_at:])
    eocd = len(final) - 22
    cd = struct.unpack_from("<I", final, eocd + 16)[0] - split_at
    struct.pack_into("<I", final, cd + 42, 4)
    struct.pack_into("<HH", final, eocd + 4, 1, 1)
    struct.pack_into("<I", final, eocd + 16, cd)
    apart.with_suffix(".z01").write_bytes(b"PK\x07\x08" + original[:split_at])
    apart.write_bytes(final)
    test(apart)
    extract(apart.with_suffix(".z01"), root / "mixed-out")
    assert (root / "mixed-out" / "bytes.dat").read_bytes() == (source / name).read_bytes()
    if exe:
        cli("t", apart, cwd=root)

    # A generated PKWARE split archive keeps every CD record within one volume.
    wide = root / "many-entries.zip"
    empty_entries = [(source / "empty", f"file-{i:04}-abcdefghijklmnopqrstuvwxyz.txt", 0) for i in range(900)]
    check(create(wide, empty_entries, 1, split=64 * 1024), label=wide)
    assert all(part.stat().st_size <= 64 * 1024 for part in root.glob("many-entries.*"))
    end = struct.unpack("<4sHHHHIIH", wide.read_bytes()[-22:])
    _, disk, start_disk, disk_entries, count, _, start_offset, _ = end
    assert disk > start_disk and disk_entries < count == len(empty_entries) and start_offset == 0, end
    assert len(list_names(wide)) == len(empty_entries)
    test(wide)
    if exe:
        cli("t", wide, cwd=root)

    # Build an external split archive with two CD records on each disk and a nonzero CD start offset.
    # In the ZIP64 variant the locator and classic EOCD are on the final disk.
    for wide_end in (False, True):
        apart = root / f"spanned-central-{wide_end}.zip"
        with zipfile.ZipFile(apart, "w", compression=zipfile.ZIP_STORED) as archive:
            for i in range(4):
                archive.writestr(f"item{i}", bytes([i]))
        binary = bytearray(apart.read_bytes())
        eocd = len(binary) - 22
        cd = struct.unpack_from("<I", binary, eocd + 16)[0]
        cursor = cd
        for i in range(4):
            offset = struct.unpack_from("<I", binary, cursor + 42)[0]
            struct.pack_into("<I", binary, cursor + 42, offset + 4)
            cursor += 46 + sum(struct.unpack_from("<HHH", binary, cursor + 28))
            if i == 1:
                split_at = cursor
        assert cursor == eocd
        first = b"PK\x07\x08" + binary[:split_at]
        final = binary[split_at:eocd]
        if wide_end:
            zip64_offset = len(final)
            final += struct.pack("<4sQHHIIQQQQ", b"PK\x06\x06", 44, 45, 45, 1, 0, 2, 4, eocd - cd, cd + 4)
            final += struct.pack("<4sIQI", b"PK\x06\x07", 1, zip64_offset, 2)
        struct.pack_into("<HHHH", binary, eocd + 4, 1, 0, 2, 0xFFFF if wide_end else 4)
        struct.pack_into("<I", binary, eocd + 16, 0xFFFFFFFF if wide_end else cd + 4)
        apart.with_suffix(".z01").write_bytes(first)
        apart.write_bytes(final + binary[eocd:])
        assert len(list_names(apart)) == 4
        test(apart)
        out = root / f"spanned-out-{wide_end}"
        extract(apart, out, [0, 3])
        assert (out / "item0").read_bytes() == b"\0" and (out / "item3").read_bytes() == b"\3"
        if exe and not wide_end:
            cli("t", apart, cwd=root)

    occupied = root / "occupied.zip"
    occupied.write_bytes(b"existing archive")
    check(create(occupied, paths, 1, split=64 * 1024), 2)
    assert occupied.read_bytes() == b"existing archive" and not list(root.glob("occupied.z[0-9]*"))

    unsafe = root / "unsafe.zip"
    with zipfile.ZipFile(unsafe, "w") as archive:
        archive.writestr("../outside", b"no")
        archive.writestr("file:stream", b"no")
        archive.writestr("CON.txt", b"no")
        archive.writestr("normal", b"yes")
    assert sum(bool(flags & 4) for _, flags, _, _ in list_names(unsafe)) == 3
    extract(unsafe, root / "unsafe-out", expected=2)
    assert not (root / "outside").exists()
    extract(unsafe, root / "selected-safe-out", [3])
    assert (root / "selected-safe-out" / "normal").read_bytes() == b"yes"

    unsafe_link = root / "symlink.zip"
    with zipfile.ZipFile(unsafe_link, "w") as archive:
        link = zipfile.ZipInfo("linked")
        link.create_system = 3
        link.external_attr = 0o120777 << 16
        archive.writestr(link, "../outside")
    assert list_names(unsafe_link)[0][1] & 4
    extract(unsafe_link, root / "symlink-out", expected=2)

    conflict = root / "conflict.zip"
    with zipfile.ZipFile(conflict, "w") as archive:
        archive.writestr("Collision", b"first")
        archive.writestr("collision", b"second")
    extract(conflict, root / "conflict-out", expected=2)
    assert not list((root / "conflict-out").iterdir())

    def check_legacy_name(path, raw_name, system, expected_name):
        placeholder = "X" * len(raw_name) + ".txt"
        info = zipfile.ZipInfo(placeholder)
        info.create_system = system
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(info, b"legacy")
        binary = path.read_bytes()
        assert binary.count(placeholder.encode()) == 2
        path.write_bytes(binary.replace(placeholder.encode(), raw_name + b".txt"))
        rows = list_names(path)
        assert [row[0] for row in rows] == [expected_name], (path.name, rows)
        out = path.with_suffix("")
        extract(path, out)
        assert (out / expected_name).read_bytes() == b"legacy", path.name

    check_legacy_name(root / "unix-utf8.zip", name[:-4].encode("utf-8"), 3, name)
    oem_page = C.windll.kernel32.GetOEMCP()
    for sample in ("한글명", "日本語", "Çüéâäà"):
        try:
            raw = sample.encode(f"cp{oem_page}")
        except (LookupError, UnicodeError):
            continue
        check_legacy_name(root / "oem-name.zip", raw, 0, sample + ".txt")
        break
    if oem_page in (437, 949):
        # These DOS names are CP437; the lead bytes have invalid CP949 trails.
        for raw, expected_name in [(b"\x82", "é.txt"), (b"\x84", "ä.txt"), (b"\x81\x30", "ü0.txt")]:
            check_legacy_name(root / f"cp437-{raw.hex()}.zip", raw, 0, expected_name)

    apart = root / "corrupt-data.zip"
    with zipfile.ZipFile(apart, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.writestr("x", b"correct")
    binary = apart.read_bytes()
    apart.write_bytes(binary.replace(b"correct", b"damaged", 1))
    test(apart, expected=2)
    assert "CRC" in dll.sz_last_error()

    apart = root / "bad-directory-header.zip"
    with zipfile.ZipFile(apart, "w") as archive:
        archive.writestr("directory/", b"")
    binary = bytearray(apart.read_bytes())
    binary[0] = 0
    apart.write_bytes(binary)
    test(apart, expected=2)

    for fmt in range(3):
        canceled = root / f"cancel{fmt}.{'7z' if fmt == 0 else 'zip'}"
        @Progress
        def check_cancel(_, stage, done, total, current):
            return current is None

        check(create(canceled, paths, fmt, progress=check_cancel, split=64 * 1024), 1)
        assert not list(root.glob(f"cancel{fmt}*"))

    large = source / "large"
    large.write_bytes(random.Random(12).randbytes(10 * MiB))
    big_archive = root / "zstd-large.zip"
    check(create(big_archive, [(large, "large", 0)], 2, memory=1024 * MiB))
    with zipfile.ZipFile(big_archive) as archive:
        assert archive.getinfo("large").compress_type == 93
    test(big_archive)

    if exe:
        fixture = root / "zipcrypto.zip"
        cli("a", "-tzip", "-psecret", "-mem=ZipCrypto", fixture, "small", cwd=source)
        test(fixture, "secret")
        test(fixture, "wrong", expected=3)
        for codec in ("LZMA", "PPMd"):
            fixture = root / f"external-{codec}.7z"
            cli("a", "-t7z", f"-m0={codec}", fixture, "small", cwd=source)
            test(fixture)
            extract(fixture, root / f"external-{codec}-out")
            assert (root / f"external-{codec}-out" / "small").read_bytes() == (source / "small").read_bytes()

print(dll.sz_version())
print("Native round trips, selection, encryption, MAC + CRC tampering, splits, safe paths and cancellation passed.")
