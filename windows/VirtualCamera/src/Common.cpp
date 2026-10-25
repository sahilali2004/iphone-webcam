#include "Common.h"

#include <cstdarg>
#include <cstdio>

// {98b1edb8-1245-434d-acdf-5dd189be7595}
const GUID CLSID_PhoneWebcamSource = {0x98b1edb8, 0x1245, 0x434d, {0xac, 0xdf, 0x5d, 0xd1, 0x89, 0xbe, 0x75, 0x95}};

std::atomic<long> g_objectCount{0};

void Trace(const wchar_t* format, ...)
{
    wchar_t buffer[1024];
    int prefix = swprintf_s(buffer, L"[PhoneWebcamSource %lu] ", GetCurrentProcessId());
    va_list args;
    va_start(args, format);
    vswprintf_s(buffer + prefix, _countof(buffer) - prefix - 2, format, args);
    va_end(args);
    wcscat_s(buffer, L"\n");
    OutputDebugStringW(buffer);
}
