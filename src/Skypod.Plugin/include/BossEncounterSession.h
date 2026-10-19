#pragma once
#include <chrono>
#include <string>
#include <string_view>

// No engine pointers or save data are retained by encounter state.
class BossEncounterSession
{
public:
    using Clock = std::chrono::steady_clock;
    std::string active;
    bool suppressed{ false };
    Clock::time_point lastSeen{};

    void Observe(std::string_view match, bool ended, Clock::time_point now)
    {
        if (ended) { Clear(); return; }
        if (!match.empty()) {
            if (active.empty()) suppressed = false;
            active = match;
            lastSeen = now;
        } else if (!active.empty() && now - lastSeen >= std::chrono::seconds(4)) {
            Clear();
        }
    }
    void Pause(Clock::time_point now) { if (!active.empty()) lastSeen = now; }
    void Clear() { active.clear(); suppressed = false; }
};
