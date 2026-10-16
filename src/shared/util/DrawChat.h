#pragma once

#include <chrono>
#include <deque>
#include <mutex>
#include <string>

class DrawChat
{
    struct ChatLine
    {
        std::string Text;
        std::chrono::steady_clock::time_point CreatedAt;
    };

    static inline std::mutex mutex;
    static inline std::deque<ChatLine> messages;
    static inline bool enabled = true;
    static inline bool hideDuringHideChaosUi = false;
    static inline int maxMessages = 6;
    static inline int durationSeconds = 10;

    static std::string ToGameText (const std::string &text);
    static void PruneExpiredLocked (std::chrono::steady_clock::time_point now);

public:
    static void UpdateConfig (bool _enabled, bool _hideDuringHideChaosUi,
                             int _maxMessages, int _durationSeconds);
    static void AddMessage (const std::string &platform,
                            const std::string &username,
                            const std::string &message);
    static void Draw ();
};
