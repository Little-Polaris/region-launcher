#include "../src/LanRoute.h"
static_assert(IsLanIPv4(0x0a000000) && IsLanIPv4(0x0affffff));
static_assert(IsLanIPv4(0xac100000) && IsLanIPv4(0xac1fffff));
static_assert(IsLanIPv4(0xc0a80000) && IsLanIPv4(0xc0a8ffff));
static_assert(IsLanIPv4(0xa9fe0000) && IsLanIPv4(0xa9feffff));
static_assert(!IsLanIPv4(0x09ffffff) && !IsLanIPv4(0x0b000000));
static_assert(!IsLanIPv4(0xac0fffff) && !IsLanIPv4(0xac200000));
static_assert(!IsLanIPv4(0xc0a7ffff) && !IsLanIPv4(0xc0a90000));
static_assert(!IsLanIPv4(0xa9fdffff) && !IsLanIPv4(0xa9ff0000));
static_assert(!IsLanIPv4(0x64400001) && !IsLanIPv4(0xc6120001));
static_assert(!IsLanIPv4(0xcb00712a) && !IsLanIPv4(0xe00000fb));
int main() { return 0; }
