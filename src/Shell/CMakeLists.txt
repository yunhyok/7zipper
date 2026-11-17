cmake_minimum_required(VERSION 3.21)
project(SevenZipperShell LANGUAGES CXX RC)

if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR
   CMAKE_CXX_COMPILER_ARCHITECTURE_ID MATCHES "ARM")
    message(FATAL_ERROR "The 7Zipper shell extension requires x64 MSVC.")
endif()

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_library(SevenZipper.Shell SHARED commands.cpp drag.cpp shell.def shell.rc)
target_compile_features(SevenZipper.Shell PRIVATE cxx_std_20)
target_compile_definitions(SevenZipper.Shell PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
target_compile_options(SevenZipper.Shell PRIVATE /W4 /utf-8 /permissive-)
target_link_libraries(SevenZipper.Shell PRIVATE ntdll ole32 shell32 shlwapi user32 uuid)

include(CTest)
if(BUILD_TESTING)
    add_subdirectory("../../tests/shell" "tests")
endif()
