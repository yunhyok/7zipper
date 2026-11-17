"""Native throughput comparison; python tests/benchmark.py DLL OUTPUT_JSON [MiB=256]."""
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import random
import statistics
import sys
import tempfile
import time
import threading

class Input(C.Structure):
    _fields_ = [('source', C.c_wchar_p), ('name', C.c_wchar_p), ('flags', C.c_uint32)]
class Create(C.Structure):
    _fields_ = [(x, C.c_uint32) for x in ('format', 'level', 'threads', 'headers')] + [('memory', C.c_uint64), ('volume', C.c_uint64), ('password', C.c_wchar_p)]
class Read(C.Structure):
    _fields_ = [('threads', C.c_uint32), ('memory', C.c_uint64), ('password', C.c_wchar_p)]
Progress = C.CFUNCTYPE(C.c_int, C.c_void_p, C.c_wchar_p, C.c_uint64, C.c_uint64, C.c_wchar_p)
dll = C.CDLL(str(Path(sys.argv[1]).resolve()))
dll.sz_last_error.restype = C.c_wchar_p
dll.sz_create.argtypes = (C.c_wchar_p, C.POINTER(Input), C.c_uint32, C.POINTER(Create), Progress, C.c_void_p)
dll.sz_extract.argtypes = (C.c_wchar_p, C.c_wchar_p, C.POINTER(C.c_uint32), C.c_uint32, C.POINTER(Read), Progress, C.c_void_p)
total_mib = int(sys.argv[3]) if len(sys.argv) > 3 else 256
rows = []

class MemoryCounters(C.Structure):
    _fields_ = [('size', C.c_uint32), ('faults', C.c_uint32)] + [(name, C.c_size_t) for name in
        ('peak_working', 'working', 'peak_paged', 'paged', 'peak_nonpaged', 'nonpaged', 'pagefile', 'peak_pagefile', 'private')]
C.windll.psapi.GetProcessMemoryInfo.argtypes = (C.c_void_p, C.POINTER(MemoryCounters), C.c_uint32)
C.windll.kernel32.GetCurrentProcess.restype = C.c_void_p

def measure(action):
    stopped = threading.Event()
    samples = []
    def sample():
        while not stopped.is_set():
            counters = MemoryCounters(size=C.sizeof(MemoryCounters))
            if C.windll.psapi.GetProcessMemoryInfo(C.windll.kernel32.GetCurrentProcess(), C.byref(counters), C.sizeof(counters)):
                samples.append(counters.working)
            stopped.wait(.01)
    thread = threading.Thread(target=sample)
    thread.start()
    started, cpu = time.perf_counter(), time.process_time()
    try: action()
    finally:
        elapsed, cpu_seconds = time.perf_counter()-started, time.process_time()-cpu
        stopped.set(); thread.join()
    return elapsed, cpu_seconds, max(samples, default=0) / 1024**2

def check(status):
    if status: raise RuntimeError(dll.sz_last_error())

with tempfile.TemporaryDirectory(prefix='7zipper-bench-') as temp:
    root = Path(temp)
    rng = random.Random(70)
    files = []
    # Repeated 16 KiB records with unique 4 KiB suffixes: mixed compressible CPU work.
    record = rng.randbytes(16 * 1024)
    for i in range(64):
        path = root / f'input-{i:03}.bin'
        size = total_mib * 1024 * 1024 // 64
        with path.open('wb') as f:
            while f.tell() < size:
                block = record + rng.randbytes(4096)
                f.write(block[:min(len(block), size-f.tell())])
        files.append(path)
    inputs = (Input * len(files))(*(Input(str(p), p.name, 0) for p in files))
    source_hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    for fmt, label, level in ((0, '7z LZMA2', 5), (1, 'ZIP Deflate', 5), (2, 'ZIP Zstandard', 3)):
        for threads in (1, os.cpu_count()):
            create_times, extract_times, output_sizes = [], [], []
            create_cpu, extract_cpu, create_memory, extract_memory = [], [], [], []
            for repeat in range(3):
                archive = root / f'{fmt}-{threads}-{repeat}.{"7z" if fmt == 0 else "zip"}'
                elapsed, cpu, memory = measure(lambda: check(dll.sz_create(str(archive), inputs, len(inputs), Create(fmt, level, threads, 0, 8*1024**3, 0, None), Progress(), None)))
                create_times.append(elapsed); create_cpu.append(cpu); create_memory.append(memory)
                output_sizes.append(archive.stat().st_size)
                destination = root / f'out-{fmt}-{threads}-{repeat}'
                destination.mkdir()
                elapsed, cpu, memory = measure(lambda: check(dll.sz_extract(str(archive), str(destination), None, 0, Read(threads, 8*1024**3, None), Progress(), None)))
                extract_times.append(elapsed); extract_cpu.append(cpu); extract_memory.append(memory)
                for name, digest in source_hashes.items():
                    output = destination / name
                    assert hashlib.sha256(output.read_bytes()).hexdigest() == digest
                    output.unlink()
                destination.rmdir()
                archive.unlink()
            row = dict(format=label, threads=threads, input_mib=total_mib,
                       compression_seconds=statistics.median(create_times), extraction_seconds=statistics.median(extract_times),
                       output_bytes=statistics.median(output_sizes), compression_runs=create_times, extraction_runs=extract_times,
                       compression_cpu_seconds=statistics.median(create_cpu), extraction_cpu_seconds=statistics.median(extract_cpu),
                       peak_compression_working_mib=max(create_memory), peak_extraction_working_mib=max(extract_memory))
            rows.append(row)
            print(json.dumps(row), flush=True)
Path(sys.argv[2]).write_text(json.dumps({'logical_processors':os.cpu_count(),'repeats':3,'rows':rows}, indent=2), encoding='utf-8')
