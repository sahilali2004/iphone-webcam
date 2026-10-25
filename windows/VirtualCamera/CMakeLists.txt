cmake_minimum_required(VERSION 3.20)
project(PhoneWebcamVirtualCamera CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the DLL is loaded by the Windows Frame Server service, which must not depend on a VC++ redist.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
add_compile_options(/W4 /permissive- /utf-8)

add_library(PhoneWebcamSource SHARED
    src/Common.cpp
    src/Activator.cpp
    src/MediaSource.cpp
    src/MediaStream.cpp
    src/FrameProducer.cpp
    src/FrameReceiver.cpp
    src/dllmain.cpp
    src/PhoneWebcamSource.def)
target_link_libraries(PhoneWebcamSource PRIVATE mfplat mfuuid mf mfsensorgroup ole32 propsys gdi32 advapi32)

# Test tool: registers the virtual camera and reads frames back through the Frame Server.
add_executable(vcamtest tools/vcamtest.cpp)
target_link_libraries(vcamtest PRIVATE mfplat mfuuid mf mfreadwrite mfsensorgroup ole32 propsys)
