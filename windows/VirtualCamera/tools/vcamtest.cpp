// vcamtest: end-to-end check of the Phone Webcam virtual camera.
//
//   vcamtest [--format nv12|rgb32] [--size 1920x1080] [--frames 90] [--hold SECONDS] [--snapshot out.bmp]
//
// 1. Registers the virtual camera (session lifetime, current user) via MFCreateVirtualCamera.
// 2. Enumerates video capture devices like any app would and finds it.
// 3. Opens it through the Frame Server, negotiates the format, reads frames, measures fps.
// 4. Optionally saves a frame as BMP and keeps the camera registered (--hold) so other apps can be tested.

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>

#include <chrono>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

static const wchar_t* kClsid = L"{98B1EDB8-1245-434D-ACDF-5DD189BE7595}";

#define CHECK(expr)                                                                       \
    do {                                                                                  \
        HRESULT _hr = (expr);                                                             \
        if (FAILED(_hr)) {                                                                \
            fwprintf(stderr, L"FAILED 0x%08X at line %d: %S\n", _hr, __LINE__, #expr);    \
            return 1;                                                                     \
        }                                                                                 \
    } while (0)

static std::wstring SubtypeName(const GUID& g)
{
    if (g == MFVideoFormat_NV12) return L"NV12";
    if (g == MFVideoFormat_RGB32) return L"RGB32";
    if (g == MFVideoFormat_YUY2) return L"YUY2";
    if (g == MFVideoFormat_MJPG) return L"MJPG";
    wchar_t s[64];
    StringFromGUID2(g, s, 64);
    return s;
}

static void SaveBmp(const wchar_t* path, const std::vector<BYTE>& bgrx, UINT32 w, UINT32 h)
{
    BITMAPFILEHEADER file{};
    BITMAPINFOHEADER info{};
    info.biSize = sizeof(info);
    info.biWidth = (LONG)w;
    info.biHeight = -(LONG)h;
    info.biPlanes = 1;
    info.biBitCount = 32;
    file.bfType = 0x4D42;
    file.bfOffBits = sizeof(file) + sizeof(info);
    file.bfSize = file.bfOffBits + (DWORD)bgrx.size();
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") == 0 && f) {
        fwrite(&file, sizeof(file), 1, f);
        fwrite(&info, sizeof(info), 1, f);
        fwrite(bgrx.data(), 1, bgrx.size(), f);
        fclose(f);
    }
}

static BYTE Clamp(int v) { return (BYTE)(v < 0 ? 0 : v > 255 ? 255 : v); }

int wmain(int argc, wchar_t** argv)
{
    std::wstring format = L"nv12", snapshot;
    UINT32 width = 1920, height = 1080;
    int frames = 90, hold = 0;
    for (int i = 1; i < argc; i++) {
        std::wstring a = argv[i];
        if (a == L"--format" && i + 1 < argc) format = argv[++i];
        else if (a == L"--size" && i + 1 < argc) swscanf_s(argv[++i], L"%ux%u", &width, &height);
        else if (a == L"--frames" && i + 1 < argc) frames = _wtoi(argv[++i]);
        else if (a == L"--hold" && i + 1 < argc) hold = _wtoi(argv[++i]);
        else if (a == L"--snapshot" && i + 1 < argc) snapshot = argv[++i];
    }
    const GUID wanted = format == L"rgb32" ? MFVideoFormat_RGB32 : MFVideoFormat_NV12;

    CHECK(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
    CHECK(MFStartup(MF_VERSION));

    ComPtr<IMFVirtualCamera> vcam;
    CHECK(MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                                MFVirtualCameraAccess_CurrentUser, L"Phone Webcam", kClsid, nullptr, 0, &vcam));
    CHECK(vcam->Start(nullptr));
    wprintf(L"Virtual camera registered and started\n");

    // Enumerate like an app would.
    ComPtr<IMFAttributes> query;
    CHECK(MFCreateAttributes(&query, 1));
    CHECK(query->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    ComPtr<IMFActivate> ours;
    for (int attempt = 0; attempt < 20 && !ours; attempt++) {
        CHECK(MFEnumDeviceSources(query.Get(), &devices, &count));
        for (UINT32 i = 0; i < count; i++) {
            wchar_t* name = nullptr;
            UINT32 len = 0;
            devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len);
            if (attempt == 0 || (name && wcsstr(name, L"Phone Webcam"))) wprintf(L"  device: %s\n", name ? name : L"?");
            if (name && wcsstr(name, L"Phone Webcam")) ours = devices[i];
            CoTaskMemFree(name);
            devices[i]->Release();
        }
        CoTaskMemFree(devices);
        if (!ours) std::this_thread::sleep_for(std::chrono::milliseconds(250));
    }
    if (!ours) {
        fwprintf(stderr, L"FAILED: Phone Webcam not found among capture devices\n");
        vcam->Remove();
        return 2;
    }
    wprintf(L"FOUND in device list\n");

    ComPtr<IMFMediaSource> source;
    CHECK(ours->ActivateObject(IID_PPV_ARGS(&source)));
    ComPtr<IMFSourceReader> reader;
    CHECK(MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader));

    DWORD stream = (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM;
    ComPtr<IMFMediaType> chosen;
    for (DWORD i = 0;; i++) {
        ComPtr<IMFMediaType> type;
        if (FAILED(reader->GetNativeMediaType(stream, i, &type))) break;
        GUID sub;
        UINT32 w = 0, h = 0, num = 0, den = 0;
        type->GetGUID(MF_MT_SUBTYPE, &sub);
        MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
        MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
        wprintf(L"  native type %u: %s %ux%u @ %u/%u\n", i, SubtypeName(sub).c_str(), w, h, num, den);
        if (sub == wanted && w == width && h == height) chosen = type;
    }
    if (!chosen) {
        fwprintf(stderr, L"FAILED: requested format not offered\n");
        vcam->Remove();
        return 3;
    }
    CHECK(reader->SetCurrentMediaType(stream, nullptr, chosen.Get()));
    wprintf(L"Reading %d frames as %s %ux%u...\n", frames, SubtypeName(wanted).c_str(), width, height);

    std::vector<BYTE> lastFrame;
    LONGLONG firstTime = -1, lastTime = 0;
    auto wallStart = std::chrono::steady_clock::now();
    int got = 0;
    for (int i = 0; i < frames + 10 && got < frames; i++) {
        DWORD flags = 0;
        LONGLONG time = 0;
        ComPtr<IMFSample> sample;
        CHECK(reader->ReadSample(stream, 0, nullptr, &flags, &time, &sample));
        if (flags & MF_SOURCE_READERF_ERROR) {
            fwprintf(stderr, L"FAILED: stream error\n");
            return 4;
        }
        if (!sample) continue;
        if (got == 5) wallStart = std::chrono::steady_clock::now();  // skip warm-up
        if (firstTime < 0) firstTime = time;
        lastTime = time;
        got++;
        ComPtr<IMFMediaBuffer> buffer;
        CHECK(sample->ConvertToContiguousBuffer(&buffer));
        BYTE* data = nullptr;
        DWORD length = 0;
        CHECK(buffer->Lock(&data, nullptr, &length));
        lastFrame.assign(data, data + length);
        buffer->Unlock();
    }
    double wall = std::chrono::duration<double>(std::chrono::steady_clock::now() - wallStart).count();
    double fps = (got - 6) / wall;
    const UINT32 expected = wanted == MFVideoFormat_NV12 ? width * height * 3 / 2 : width * height * 4;
    wprintf(L"RESULT frames=%d fps=%.1f bytes=%zu expected=%u\n", got, fps, lastFrame.size(), expected);

    if (!snapshot.empty() && lastFrame.size() >= expected) {
        std::vector<BYTE> bgrx((size_t)width * height * 4);
        if (wanted == MFVideoFormat_RGB32) {
            bgrx.assign(lastFrame.begin(), lastFrame.begin() + bgrx.size());
        } else {
            const BYTE* y = lastFrame.data();
            const BYTE* uv = y + (size_t)width * height;
            for (UINT32 r = 0; r < height; r++)
                for (UINT32 c = 0; c < width; c++) {
                    int Y = y[(size_t)r * width + c] - 16;
                    int U = uv[(size_t)(r / 2) * width + (c & ~1u)] - 128;
                    int V = uv[(size_t)(r / 2) * width + (c & ~1u) + 1] - 128;
                    BYTE* p = &bgrx[((size_t)r * width + c) * 4];
                    p[2] = Clamp((298 * Y + 459 * V + 128) >> 8);            // BT.709 limited → RGB
                    p[1] = Clamp((298 * Y - 55 * U - 136 * V + 128) >> 8);
                    p[0] = Clamp((298 * Y + 541 * U + 128) >> 8);
                    p[3] = 255;
                }
        }
        SaveBmp(snapshot.c_str(), bgrx, width, height);
        wprintf(L"Snapshot saved: %s\n", snapshot.c_str());
    }

    reader.Reset();
    source->Shutdown();
    source.Reset();

    if (hold > 0) {
        wprintf(L"HOLDING camera for %d s\n", hold);
        fflush(stdout);
        std::this_thread::sleep_for(std::chrono::seconds(hold));
    }

    HRESULT hr = vcam->Remove();
    wprintf(L"Virtual camera removed (0x%08X)\n", hr);
    MFShutdown();
    return (got >= frames && lastFrame.size() >= expected && fps > 25) ? 0 : 5;
}
