#pragma once
#include <stdint.h>

// Host byte order. Do not include CGNAT, public ranges, multicast, or all of 172/8.
inline constexpr bool IsLanIPv4(uint32_t ip) {
    return (ip & 0xff000000u) == 0x0a000000u ||
           (ip & 0xfff00000u) == 0xac100000u ||
           (ip & 0xffff0000u) == 0xc0a80000u ||
           (ip & 0xffff0000u) == 0xa9fe0000u;
}
