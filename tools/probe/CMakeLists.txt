cmake_minimum_required(VERSION 3.20)
project(probe CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# index.cpp is compiled from its real location, not copied. A test that runs
# against a duplicate of the code under test proves nothing.
add_executable(probe
    probe.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../../src/overlay/index.cpp
)

set_property(TARGET probe PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_link_libraries(probe PRIVATE shell32 ole32 advapi32)
target_compile_definitions(probe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN STRICT)
