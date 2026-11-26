#pragma once
#include <cstdint>

// Best-effort breadcrumbs survive a software/watchdog reset, not power loss.
struct ResetTrace { uint32_t magic; volatile uint32_t loop, control, audio; };
extern ResetTrace resetTrace;
constexpr uint32_t kResetTraceMagic = 0x52545243;
