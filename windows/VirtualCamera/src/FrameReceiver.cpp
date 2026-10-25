#include "FrameReceiver.h"

FrameReceiver::~FrameReceiver()
{
    Stop();
}

void FrameReceiver::Start()
{
    if (_thread.joinable()) return;
    _stopping = false;
    if (!_stopEvent) _stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ResetEvent(_stopEvent);
    _thread = std::thread([this] { Run(); });
}

void FrameReceiver::Stop()
{
    if (_thread.joinable()) {
        _stopping = true;
        SetEvent(_stopEvent);
        // Unblock a pending synchronous ReadFile on the pipe. Repeat until the thread exits, in case it
        // was between calls when the first cancel landed.
        auto thread = static_cast<HANDLE>(_thread.native_handle());
        do {
            CancelSynchronousIo(thread);
        } while (WaitForSingleObject(thread, 50) == WAIT_TIMEOUT);
        _thread.join();
    }
    if (_stopEvent) {
        CloseHandle(_stopEvent);
        _stopEvent = nullptr;
    }
    std::lock_guard lock(_lock);
    _receivedAt = 0;
}

bool FrameReceiver::CopyLatest(std::vector<BYTE>& nv12, UINT32& width, UINT32& height, ULONGLONG maxAgeMs)
{
    std::lock_guard lock(_lock);
    if (_receivedAt == 0 || GetTickCount64() - _receivedAt > maxAgeMs) return false;
    nv12 = _latest;
    width = _width;
    height = _height;
    return true;
}

bool FrameReceiver::ReadExact(HANDLE pipe, void* buffer, DWORD size)
{
    auto* p = static_cast<BYTE*>(buffer);
    while (size > 0) {
        DWORD read = 0;
        if (!ReadFile(pipe, p, size, &read, nullptr) || read == 0) return false;
        p += read;
        size -= read;
    }
    return true;
}

void FrameReceiver::Run()
{
    std::vector<BYTE> back;
    bool loggedWaiting = false;
    while (!_stopping) {
        HANDLE pipe = CreateFileW(kPipeName, GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            if (!loggedWaiting) {
                Trace(L"FrameReceiver: app pipe not available (%lu); retrying", GetLastError());
                loggedWaiting = true;
            }
            WaitForSingleObject(_stopEvent, 500);
            continue;
        }
        Trace(L"FrameReceiver: connected to app");
        loggedWaiting = false;

        PipeFrameHeader header{};
        while (!_stopping && ReadExact(pipe, &header, sizeof(header))) {
            const UINT64 expected = static_cast<UINT64>(header.width) * header.height * 3 / 2;
            if (header.magic != kPipeMagic || header.format != kPipeFormatNv12 || header.width == 0 || header.height == 0 ||
                header.width > 4096 || header.height > 4096 || (header.width & 1) || (header.height & 1) ||
                header.payloadSize != expected) {
                Trace(L"FrameReceiver: bad header; reconnecting");
                break;
            }
            back.resize(header.payloadSize);
            if (!ReadExact(pipe, back.data(), header.payloadSize)) break;
            std::lock_guard lock(_lock);
            _latest.swap(back);
            _width = header.width;
            _height = header.height;
            _receivedAt = GetTickCount64();
        }
        CloseHandle(pipe);
        Trace(L"FrameReceiver: disconnected from app");
    }
}
