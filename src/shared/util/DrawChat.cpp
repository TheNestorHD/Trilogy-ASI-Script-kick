#include "DrawChat.h"

#include "util/GenericUtil.h"
#include "util/Globals.h"

#include <CFont.h>
#include <Windows.h>
#include <extensions/FontPrint.h>

#include <algorithm>
#include <utility>
#include <vector>

using namespace plugin;

std::string
DrawChat::ToGameText (const std::string &text)
{
    if (text.empty ()) return {};

    int wideLength = MultiByteToWideChar (
        CP_UTF8, MB_ERR_INVALID_CHARS, text.data (),
        static_cast<int> (text.size ()), nullptr, 0);

    if (wideLength <= 0)
    {
        std::string ascii;
        ascii.reserve (text.size ());
        for (unsigned char c : text)
            if (c >= 0x20 && c < 0x7F) ascii.push_back (static_cast<char> (c));
        return ascii;
    }

    std::wstring wide (static_cast<size_t> (wideLength), L'\0');
    if (!MultiByteToWideChar (CP_UTF8, MB_ERR_INVALID_CHARS, text.data (),
                              static_cast<int> (text.size ()), wide.data (),
                              wideLength))
        return {};

    int outputLength = WideCharToMultiByte (
        1252, 0, wide.data (), wideLength, nullptr, 0, "?", nullptr);
    if (outputLength <= 0) return {};

    std::string converted (static_cast<size_t> (outputLength), '\0');
    if (!WideCharToMultiByte (1252, 0, wide.data (), wideLength,
                              converted.data (), outputLength, "?", nullptr))
        return {};

    // GTA's classic font expects a single-byte game code page. Drop control
    // characters and normalize tabs so each chat post occupies one row.
    std::string clean;
    clean.reserve (converted.size ());
    for (unsigned char c : converted)
    {
        if (c == '\t') clean.push_back (' ');
        else if (c >= 0x20) clean.push_back (static_cast<char> (c));
    }
    return clean;
}

void
DrawChat::PruneExpiredLocked (std::chrono::steady_clock::time_point now)
{
    const auto lifetime = std::chrono::seconds (durationSeconds);
    while (!messages.empty () && now - messages.front ().CreatedAt >= lifetime)
        messages.pop_front ();

    while (static_cast<int> (messages.size ()) > maxMessages)
        messages.pop_front ();
}

void
DrawChat::UpdateConfig (bool _enabled, bool _hideDuringHideChaosUi,
                        int _maxMessages, int _durationSeconds)
{
    std::lock_guard<std::mutex> lock (mutex);
    enabled = _enabled;
    hideDuringHideChaosUi = _hideDuringHideChaosUi;
    maxMessages = std::clamp (_maxMessages, 1, 10);
    durationSeconds = std::clamp (_durationSeconds, 3, 30);

    PruneExpiredLocked (std::chrono::steady_clock::now ());
    if (!enabled) messages.clear ();
}

void
DrawChat::AddMessage (const std::string &platform,
                      const std::string &username,
                      const std::string &message)
{
    std::string cleanUsername = ToGameText (username);
    std::string cleanMessage = ToGameText (message);
    std::string cleanPlatform = ToGameText (platform.empty () ? "Chat" : platform);

    auto isBlank = [] (const std::string &value)
    {
        return value.find_first_not_of (" \t\r\n") == std::string::npos;
    };
    if (isBlank (cleanUsername) || isBlank (cleanMessage)) return;

    if (cleanPlatform.size () > 12) cleanPlatform.resize (12);
    if (cleanUsername.size () > 24) cleanUsername.resize (24);

    std::string line = "[" + cleanPlatform + "] " + cleanUsername + ": "
                       + cleanMessage;
    constexpr size_t maxLineLength = 112;
    if (line.size () > maxLineLength)
    {
        line.resize (maxLineLength - 3);
        line.append ("...");
    }

    std::lock_guard<std::mutex> lock (mutex);
    if (!enabled) return;

    const auto now = std::chrono::steady_clock::now ();
    PruneExpiredLocked (now);
    messages.push_back ({ std::move (line), now });
    while (static_cast<int> (messages.size ()) > maxMessages)
        messages.pop_front ();
}

void
DrawChat::Draw ()
{
    if (GenericUtil::IsMenuActive ()) return;

    std::vector<std::string> visibleMessages;
    bool hideDuringHideUi = false;
    {
        std::lock_guard<std::mutex> lock (mutex);
        if (!enabled) return;

        PruneExpiredLocked (std::chrono::steady_clock::now ());
        visibleMessages.reserve (messages.size ());
        for (const auto &line : messages)
            visibleMessages.push_back (line.Text);
        hideDuringHideUi = hideDuringHideChaosUi;
    }

    if (visibleMessages.empty ()) return;
    if (hideDuringHideUi && Globals::enabledEffects["hide_chaos_ui"]) return;

    const float x = SCREEN_COORD_LEFT (14.0f);
    const float y = SCREEN_COORD_TOP (48.0f);
    const float rowHeight = SCREEN_COORD (18.0f);
    const float width = SCREEN_COORD (545.0f);
    const float padding = SCREEN_COORD (5.0f);

    CRect background (x - padding, y - padding,
                      x + width, y + rowHeight * static_cast<float> (visibleMessages.size ()) + padding);
    CSprite2d::DrawRect (background, CRGBA (0, 0, 0, 175));

    for (size_t i = 0; i < visibleMessages.size (); ++i)
    {
        const float lineY = y + rowHeight * static_cast<float> (i);
        gamefont::PrintUnscaled (
            visibleMessages[i], x, lineY, FONT_DEFAULT,
            SCREEN_MULTIPLIER (0.42f), SCREEN_MULTIPLIER (0.65f),
            color::White, gamefont::AlignLeft, 1, color::Black, true);
    }
}
