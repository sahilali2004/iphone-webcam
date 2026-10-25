#pragma once

#include "Common.h"

/// Produces the pixels for each sample the Frame Server requests.
/// Phase 5: a generated test card ("waiting for phone"). Phase 6 adds frames from the companion app.
class FrameProducer {
public:
    FrameProducer() = default;
    ~FrameProducer();
    FrameProducer(const FrameProducer&) = delete;
    FrameProducer& operator=(const FrameProducer&) = delete;

    HRESULT Configure(UINT32 width, UINT32 height, const GUID& subtype);

    /// Writes one frame into the sample's buffer in the configured format.
    HRESULT Fill(IMFSample* sample);

private:
    void RenderTestCard();
    void Release();

    UINT32 _width = 0;
    UINT32 _height = 0;
    GUID _subtype = GUID_NULL;
    UINT64 _frameCount = 0;

    // GDI 32-bpp top-down DIB used as the BGRX canvas.
    HDC _dc = nullptr;
    HBITMAP _bitmap = nullptr;
    HGDIOBJ _oldBitmap = nullptr;
    HFONT _titleFont = nullptr;
    HFONT _bodyFont = nullptr;
    BYTE* _pixels = nullptr;
};
