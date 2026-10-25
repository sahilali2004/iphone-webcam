#pragma once

#include "Common.h"
#include "FrameReceiver.h"

/// Produces the pixels for each sample the Frame Server requests: the phone's newest frame from the
/// companion app (scaled to the negotiated size), or a generated test card when no phone video is flowing.
class FrameProducer {
public:
    FrameProducer() = default;
    ~FrameProducer();
    FrameProducer(const FrameProducer&) = delete;
    FrameProducer& operator=(const FrameProducer&) = delete;

    HRESULT Configure(UINT32 width, UINT32 height, const GUID& subtype);

    void SetReceiver(FrameReceiver* receiver) { _receiver = receiver; }

    /// Writes one frame into the sample's buffer in the configured format.
    HRESULT Fill(IMFSample* sample);

private:
    void RenderTestCard();
    HRESULT WritePhoneFrame(BYTE* scanline0, LONG pitch);
    HRESULT WriteTestCard(BYTE* scanline0, LONG pitch);
    void Release();

    UINT32 _width = 0;
    UINT32 _height = 0;
    GUID _subtype = GUID_NULL;
    UINT64 _frameCount = 0;

    FrameReceiver* _receiver = nullptr;
    std::vector<BYTE> _phone;   // newest phone frame (NV12, contiguous)
    std::vector<BYTE> _scaled;  // phone frame scaled to the negotiated size
    UINT32 _phoneWidth = 0;
    UINT32 _phoneHeight = 0;

    // GDI 32-bpp top-down DIB used as the BGRX canvas.
    HDC _dc = nullptr;
    HBITMAP _bitmap = nullptr;
    HGDIOBJ _oldBitmap = nullptr;
    HFONT _titleFont = nullptr;
    HFONT _bodyFont = nullptr;
    BYTE* _pixels = nullptr;
};
