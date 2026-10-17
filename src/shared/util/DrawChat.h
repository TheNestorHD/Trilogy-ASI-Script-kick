#pragma once

#include <chrono>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

class DrawChat
{
    struct ChatLine
    {
        std::string Prefix;
        std::string Message;
        std::vector<std::string> Lines;
        std::chrono::steady_clock::time_point CreatedAt;
    };

    static inline std::mutex mutex;
    static inline std::deque<ChatLine> messages;
    static inline bool enabled = true;
    static inline bool hideDuringHideChaosUi = false;
    static inline int maxMessages = 6;
    static inline int durationSeconds = 10;
    static inline int fontSizePercent = 135;

    static std::vector<std::string> WrapMessage (const std::string &prefix,
                                                 const std::string &message);
    static void PruneExpiredLocked (std::chrono::steady_clock::time_point now);
    static size_t MaxLineCharacters ();

public:
    static void UpdateConfig (bool _enabled, bool _hideDuringHideChaosUi,
                             int _maxMessages, int _durationSeconds,
                             int _fontSizePercent);
    static void AddMessage (const std::string &platform,
                            const std::string &username,
                            const std::string &message);
    static void Draw ();
};
