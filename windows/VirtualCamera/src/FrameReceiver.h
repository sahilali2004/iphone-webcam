#pragma once

#include "Common.h"

#include <thread>

/// Wire format of \\.\pipe\PhoneWebcam.Frames (companion app → virtual camera). Little-endian.
/// Must match windows/Core/VirtualCamera/FramePublisher.cs.
#pragma pack(push, 1)
struct PipeFrameHeader {
    UINT32 magic;        // 'PWF1' = 0x31465750
    UINT32 width;
    UINT32 height;
    UINT32 format;       // 1 = NV12 (stride = width)
    UINT32 payloadSize;  // width * height * 3 / 2 for NV12
    UINT32 flags;        // reserved, 0
    UINT64 frameId;
};
#pragma pack(pop)
static_assert(sizeof(PipeFrameHeader) == 32, "pipe header layout");

constexpr UINT32 kPipeMagic = 0x31465750;
constexpr UINT32 kPipeFormatNv12 = 1;
constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\PhoneWebcam.Frames";

/// Connects to the companion app's pipe (retrying while it isn't there) and keeps the newest frame.
/// Runs its own thread while started; never blocks the Frame Server's threads.
class FrameReceiver {
public:
    FrameReceiver() = default;
    ~FrameReceiver();
    FrameReceiver(const FrameReceiver&) = delete;
    FrameReceiver& operator=(const FrameReceiver&) = delete;

    void Start();
    void Stop();

    /// Copies the newest frame if it arrived within maxAgeMs. Returns false otherwise.
    bool CopyLatest(std::vector<BYTE>& nv12, UINT32& width, UINT32& height, ULONGLONG maxAgeMs);

private:
    void Run();
    bool ReadExact(HANDLE pipe, void* buffer, DWORD size);

    std::thread _thread;
    HANDLE _stopEvent = nullptr;
    std::atomic<bool> _stopping{false};

    std::mutex _lock;
    std::vector<BYTE> _latest;
    UINT32 _width = 0;
    UINT32 _height = 0;
    ULONGLONG _receivedAt = 0;
};
