#include "FrameProducer.h"

#include <cstdio>

namespace {

// BGRX → NV12, BT.709 limited range (matches MF_MT_YUV_MATRIX/MF_MT_VIDEO_NOMINAL_RANGE on the NV12 types).
void ConvertToNv12(const BYTE* bgrx, UINT32 width, UINT32 height, BYTE* y, LONG yPitch, BYTE* uv, LONG uvPitch)
{
    for (UINT32 row = 0; row < height; row++) {
        const BYTE* src = bgrx + static_cast<size_t>(row) * width * 4;
        BYTE* dst = y + static_cast<ptrdiff_t>(row) * yPitch;
        for (UINT32 col = 0; col < width; col++, src += 4) {
            int b = src[0], g = src[1], r = src[2];
            dst[col] = static_cast<BYTE>(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8));
        }
    }
    for (UINT32 row = 0; row < height / 2; row++) {
        const BYTE* top = bgrx + static_cast<size_t>(row * 2) * width * 4;
        const BYTE* bottom = top + static_cast<size_t>(width) * 4;
        BYTE* dst = uv + static_cast<ptrdiff_t>(row) * uvPitch;
        for (UINT32 col = 0; col < width / 2; col++) {
            const BYTE* a = top + col * 8;
            const BYTE* c = bottom + col * 8;
            // Average the 2x2 block.
            int b = (a[0] + a[4] + c[0] + c[4] + 2) >> 2;
            int g = (a[1] + a[5] + c[1] + c[5] + 2) >> 2;
            int r = (a[2] + a[6] + c[2] + c[6] + 2) >> 2;
            dst[col * 2] = static_cast<BYTE>(128 + ((-26 * r - 86 * g + 112 * b + 128) >> 8));
            dst[col * 2 + 1] = static_cast<BYTE>(128 + ((112 * r - 102 * g - 10 * b + 128) >> 8));
        }
    }
}

// Bilinear NV12 resize (contiguous planes, stride = width). Used when the app asked for a different size than the phone sends.
void ScalePlane(const BYTE* s, UINT32 sw, UINT32 sh, UINT32 sStride, BYTE* d, UINT32 dw, UINT32 dh, UINT32 dStride, int channels)
{
    const UINT32 xStep = ((sw - 1) << 16) / (dw > 1 ? dw - 1 : 1);
    const UINT32 yStep = ((sh - 1) << 16) / (dh > 1 ? dh - 1 : 1);
    for (UINT32 y = 0; y < dh; y++) {
        const UINT32 fy = y * yStep, y0 = fy >> 16, y1 = y0 + 1 < sh ? y0 + 1 : y0, wy = (fy >> 8) & 0xFF;
        const BYTE* r0 = s + static_cast<size_t>(y0) * sStride;
        const BYTE* r1 = s + static_cast<size_t>(y1) * sStride;
        BYTE* out = d + static_cast<size_t>(y) * dStride;
        for (UINT32 x = 0; x < dw; x++) {
            const UINT32 fx = x * xStep, x0 = fx >> 16, x1 = x0 + 1 < sw ? x0 + 1 : x0, wx = (fx >> 8) & 0xFF;
            for (int c = 0; c < channels; c++) {
                const int a = r0[x0 * channels + c], b = r0[x1 * channels + c];
                const int e = r1[x0 * channels + c], f = r1[x1 * channels + c];
                const int top = a * (256 - static_cast<int>(wx)) + b * static_cast<int>(wx);
                const int bottom = e * (256 - static_cast<int>(wx)) + f * static_cast<int>(wx);
                out[x * channels + c] = static_cast<BYTE>((top * (256 - static_cast<int>(wy)) + bottom * static_cast<int>(wy) + 32768) >> 16);
            }
        }
    }
}

void ScaleNv12(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, UINT32 dw, UINT32 dh)
{
    ScalePlane(src, sw, sh, sw, dst, dw, dh, dw, 1);
    ScalePlane(src + static_cast<size_t>(sw) * sh, sw / 2, sh / 2, sw, dst + static_cast<size_t>(dw) * dh, dw / 2, dh / 2, dw, 2);
}

BYTE Clamp255(int v)
{
    return static_cast<BYTE>(v < 0 ? 0 : v > 255 ? 255 : v);
}

// NV12 (BT.709 limited, contiguous) -> BGRX.
void Nv12ToBgrx(const BYTE* nv12, UINT32 width, UINT32 height, BYTE* dst, LONG pitch)
{
    const BYTE* yPlane = nv12;
    const BYTE* uvPlane = nv12 + static_cast<size_t>(width) * height;
    for (UINT32 row = 0; row < height; row++) {
        const BYTE* y = yPlane + static_cast<size_t>(row) * width;
        const BYTE* uv = uvPlane + static_cast<size_t>(row / 2) * width;
        BYTE* out = dst + static_cast<ptrdiff_t>(row) * pitch;
        for (UINT32 col = 0; col < width; col++) {
            const int c = 298 * (y[col] - 16);
            const int u = uv[col & ~1u] - 128, v = uv[(col & ~1u) + 1] - 128;
            out[col * 4 + 0] = Clamp255((c + 541 * u + 128) >> 8);
            out[col * 4 + 1] = Clamp255((c - 55 * u - 136 * v + 128) >> 8);
            out[col * 4 + 2] = Clamp255((c + 459 * v + 128) >> 8);
            out[col * 4 + 3] = 255;
        }
    }
}

}  // namespace

FrameProducer::~FrameProducer()
{
    Release();
}

void FrameProducer::Release()
{
    if (_dc) {
        if (_oldBitmap) SelectObject(_dc, _oldBitmap);
        DeleteDC(_dc);
    }
    if (_bitmap) DeleteObject(_bitmap);
    if (_titleFont) DeleteObject(_titleFont);
    if (_bodyFont) DeleteObject(_bodyFont);
    _dc = nullptr;
    _bitmap = nullptr;
    _oldBitmap = nullptr;
    _titleFont = _bodyFont = nullptr;
    _pixels = nullptr;
}

HRESULT FrameProducer::Configure(UINT32 width, UINT32 height, const GUID& subtype)
{
    _subtype = subtype;
    if (width == _width && height == _height && _dc) return S_OK;

    Release();
    _width = width;
    _height = height;

    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(info.bmiHeader);
    info.bmiHeader.biWidth = static_cast<LONG>(width);
    info.bmiHeader.biHeight = -static_cast<LONG>(height);  // top-down
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    _dc = CreateCompatibleDC(nullptr);
    RETURN_HR_IF(E_FAIL, !_dc);
    void* bits = nullptr;
    _bitmap = CreateDIBSection(_dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    RETURN_HR_IF(E_OUTOFMEMORY, !_bitmap || !bits);
    _pixels = static_cast<BYTE*>(bits);
    _oldBitmap = SelectObject(_dc, _bitmap);

    int titleHeight = static_cast<int>(height / 10);
    int bodyHeight = static_cast<int>(height / 22);
    _titleFont = CreateFontW(-titleHeight, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                             CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    _bodyFont = CreateFontW(-bodyHeight, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                            CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    Trace(L"FrameProducer configured %ux%u", width, height);
    return S_OK;
}

void FrameProducer::RenderTestCard()
{
    const int w = static_cast<int>(_width);
    const int h = static_cast<int>(_height);

    // Dark background with a band of muted colour bars, so it's obviously a test card, not a broken camera.
    RECT all{0, 0, w, h};
    HBRUSH background = CreateSolidBrush(RGB(24, 24, 28));
    FillRect(_dc, &all, background);
    DeleteObject(background);

    static const COLORREF bars[] = {RGB(192, 192, 192), RGB(192, 192, 0), RGB(0, 192, 192), RGB(0, 192, 0),
                                    RGB(192, 0, 192),   RGB(192, 0, 0),   RGB(0, 0, 192)};
    const int barTop = h * 3 / 4;
    const int barHeight = h / 12;
    for (int i = 0; i < 7; i++) {
        RECT bar{w * i / 7, barTop, w * (i + 1) / 7, barTop + barHeight};
        HBRUSH brush = CreateSolidBrush(bars[i]);
        FillRect(_dc, &bar, brush);
        DeleteObject(brush);
    }

    // Moving marker: proves frames are live (a frozen image would stop it).
    const int markerWidth = w / 40;
    const int travel = w - markerWidth;
    const int position = static_cast<int>((_frameCount * 8) % (2 * travel));
    const int x = position < travel ? position : 2 * travel - position;
    RECT marker{x, barTop + barHeight + h / 40, x + markerWidth, barTop + barHeight + h / 40 + h / 60};
    HBRUSH red = CreateSolidBrush(RGB(229, 50, 45));
    FillRect(_dc, &marker, red);
    DeleteObject(red);

    // Tally dot + title + detail.
    SetBkMode(_dc, TRANSPARENT);
    const int left = w / 12;
    const int dot = h / 28;
    HBRUSH amber = CreateSolidBrush(RGB(232, 163, 23));
    HGDIOBJ oldBrush = SelectObject(_dc, amber);
    HGDIOBJ oldPen = SelectObject(_dc, GetStockObject(NULL_PEN));
    Ellipse(_dc, left, h / 4 + dot / 2, left + dot, h / 4 + dot / 2 + dot);
    SelectObject(_dc, oldPen);
    SelectObject(_dc, oldBrush);
    DeleteObject(amber);

    HGDIOBJ oldFont = SelectObject(_dc, _titleFont);
    SetTextColor(_dc, RGB(242, 242, 242));
    TextOutW(_dc, left + dot * 2, h / 4 - h / 40, L"Phone Webcam", 12);

    SelectObject(_dc, _bodyFont);
    SetTextColor(_dc, RGB(160, 160, 166));
    const wchar_t* waiting = L"Waiting for the phone. Start the camera in the Phone Webcam app.";
    TextOutW(_dc, left + dot * 2, h / 4 + h / 10, waiting, static_cast<int>(wcslen(waiting)));

    SYSTEMTIME now;
    GetLocalTime(&now);
    wchar_t info[128];
    int n = swprintf_s(info, L"%u × %u · 30 fps · %02u:%02u:%02u", _width, _height, now.wHour, now.wMinute, now.wSecond);
    TextOutW(_dc, left + dot * 2, h / 4 + h / 10 + h / 16, info, n);
    SelectObject(_dc, oldFont);

    GdiFlush();
}

HRESULT FrameProducer::WritePhoneFrame(BYTE* scanline0, LONG pitch)
{
    const BYTE* frame = _phone.data();
    if (_phoneWidth != _width || _phoneHeight != _height) {
        _scaled.resize(static_cast<size_t>(_width) * _height * 3 / 2);
        ScaleNv12(_phone.data(), _phoneWidth, _phoneHeight, _scaled.data(), _width, _height);
        frame = _scaled.data();
    }

    if (_subtype == MFVideoFormat_NV12) {
        for (UINT32 row = 0; row < _height; row++)
            memcpy(scanline0 + static_cast<ptrdiff_t>(row) * pitch, frame + static_cast<size_t>(row) * _width, _width);
        BYTE* uv = scanline0 + static_cast<ptrdiff_t>(pitch) * _height;
        const BYTE* srcUv = frame + static_cast<size_t>(_width) * _height;
        for (UINT32 row = 0; row < _height / 2; row++)
            memcpy(uv + static_cast<ptrdiff_t>(row) * pitch, srcUv + static_cast<size_t>(row) * _width, _width);
        return S_OK;
    }
    if (_subtype == MFVideoFormat_RGB32) {
        Nv12ToBgrx(frame, _width, _height, scanline0, pitch);
        return S_OK;
    }
    return MF_E_INVALIDMEDIATYPE;
}

HRESULT FrameProducer::WriteTestCard(BYTE* scanline0, LONG pitch)
{
    RenderTestCard();
    if (_subtype == MFVideoFormat_NV12) {
        BYTE* uv = scanline0 + static_cast<ptrdiff_t>(pitch) * _height;
        ConvertToNv12(_pixels, _width, _height, scanline0, pitch, uv, pitch);
        return S_OK;
    }
    if (_subtype == MFVideoFormat_RGB32) {
        for (UINT32 row = 0; row < _height; row++)
            memcpy(scanline0 + static_cast<ptrdiff_t>(row) * pitch, _pixels + static_cast<size_t>(row) * _width * 4, _width * 4);
        return S_OK;
    }
    return MF_E_INVALIDMEDIATYPE;
}

HRESULT FrameProducer::Fill(IMFSample* sample)
{
    RETURN_HR_IF(MF_E_NOT_INITIALIZED, !_dc);
    _frameCount++;

    // Phone video if the app delivered a frame within the last second; otherwise the test card.
    const bool phone = _receiver && _receiver->CopyLatest(_phone, _phoneWidth, _phoneHeight, 1000);

    ComPtr<IMFMediaBuffer> buffer;
    RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));

    BYTE* scanline0 = nullptr;
    LONG pitch = 0;
    ComPtr<IMF2DBuffer2> buffer2d;
    BYTE* start = nullptr;
    DWORD length = 0;
    bool locked2d = false;
    if (SUCCEEDED(buffer.As(&buffer2d)) &&
        SUCCEEDED(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline0, &pitch, &start, &length))) {
        locked2d = true;
    } else {
        DWORD max = 0;
        RETURN_IF_FAILED(buffer->Lock(&start, &max, nullptr));
        scanline0 = start;
        pitch = static_cast<LONG>(_subtype == MFVideoFormat_NV12 ? _width : _width * 4);
    }

    HRESULT hr = phone ? WritePhoneFrame(scanline0, pitch) : WriteTestCard(scanline0, pitch);

    if (locked2d) {
        buffer2d->Unlock2D();
    } else {
        buffer->SetCurrentLength(_subtype == MFVideoFormat_NV12 ? _width * _height * 3 / 2 : _width * _height * 4);
        buffer->Unlock();
    }
    return hr;
}
