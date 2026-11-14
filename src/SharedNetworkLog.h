#pragma once
#include <stdio.h>
#include <share.h>

// Permit diagnostic readers while keeping a single writer.
inline FILE* OpenNetworkLog(const wchar_t* path) {
    return _wfsopen(path, L"wb", _SH_DENYWR);
}
