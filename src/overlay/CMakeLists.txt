cmake_minimum_required(VERSION 3.20)
project(HeartOverlay CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(HeartOverlay SHARED
    dllmain.cpp
    overlay.cpp
    index.cpp
    overlay.rc
)

# This DLL gets loaded into explorer.exe, which means it must not drag a runtime
# redistributable in behind it. Static CRT: no vcruntime140.dll dependency, so the
# handler works on a machine that has never seen a Visual Studio installer.
set_property(TARGET HeartOverlay PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

target_link_options(HeartOverlay PRIVATE
    "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/HeartOverlay.def")

target_link_libraries(HeartOverlay PRIVATE shell32 ole32 advapi32)

target_compile_definitions(HeartOverlay PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN STRICT)

target_compile_options(HeartOverlay PRIVATE /W4 /permissive-)

set_target_properties(HeartOverlay PROPERTIES
    OUTPUT_NAME "HeartOverlay"
    PREFIX "")
