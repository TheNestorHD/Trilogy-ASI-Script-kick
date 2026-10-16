#include "DrawChat.h"

#include "util/GenericUtil.h"
#include "util/Globals.h"
#include "util/TextEncoding.h"

#include <CFont.h>
#include <extensions/FontPrint.h>

#include <algorithm>
#include <utility>
#include <vector>

using namespace plugin;

void
DrawChat::PruneExpiredLocked (std::chrono::steady_clock::time_point now)
{
    const auto lifetime = std::chrono::seconds (durationSeconds);
    while (!messages.empty () && now - messages.front ().CreatedAt >= lifetime)
        messages.pop_front ();

    while (static_cast<int> (messages.size ()) > maxMessages)
        messages.pop_front ();
}

std::vector<std::string>
DrawChat::WrapMessage (const std::string &prefix, const std::string &message)
{
    // Text is converted to the game's single-byte encoding before wrapping.
    // This prevents UTF-8 continuation bytes from counting as extra glyphs.
    constexpr size_t maxLineCharacters = 84;
    constexpr size_t maxLinesPerMessage = 7;

    std::vector<std::string> lines;
    std::string remaining = message;
    const size_t firstCapacity
        = maxLineCharacters > prefix.size () ? maxLineCharacters - prefix.size ()
                                             : 1;

    while (!remaining.empty () && lines.size () < maxLinesPerMessage)
    {
        const bool firstLine = lines.empty ();
        const size_t capacity = firstLine ? firstCapacity : maxLineCharacters - 2;
        std::string segment;

        if (remaining.size () <= capacity)
        {
            segment = std::move (remaining);
            remaining.clear ();
        }
        else
        {
            size_t split = remaining.find_last_of (" ", capacity);
            if (split == std::string::npos || split == 0)
                split = capacity;

            segment = remaining.substr (0, split);
            remaining.erase (0, split);
            while (!remaining.empty () && remaining.front () == ' ')
                remaining.erase (remaining.begin ());
        }

        lines.push_back ((firstLine ? prefix : "  ") + segment);
    }

    // If an unusually long post exceeds the per-message row limit, mark the
    // continuation instead of silently cutting it off at the end of row one.
    if (!remaining.empty () && !lines.empty ())
    {
        std::string &lastLine = lines.back ();
        if (lastLine.size () > maxLineCharacters - 3)
            lastLine.resize (maxLineCharacters - 3);
        lastLine.append ("...");
    }

    return lines;
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
    std::string cleanUsername = TextEncoding::ToGameText (username);
    std::string cleanMessage = TextEncoding::ToGameText (message);
    std::string cleanPlatform
        = TextEncoding::ToGameText (platform.empty () ? "Chat" : platform);

    auto isBlank = [] (const std::string &value)
    {
        return value.find_first_not_of (" \t\r\n") == std::string::npos;
    };
    if (isBlank (cleanUsername) || isBlank (cleanMessage)) return;

    if (cleanPlatform.size () > 12) cleanPlatform.resize (12);
    if (cleanUsername.size () > 24) cleanUsername.resize (24);

    const std::string prefix
        = "[" + cleanPlatform + "] " + cleanUsername + ": ";
    std::vector<std::string> lines = WrapMessage (prefix, cleanMessage);
    if (lines.empty ()) return;

    std::lock_guard<std::mutex> lock (mutex);
    if (!enabled) return;

    const auto now = std::chrono::steady_clock::now ();
    PruneExpiredLocked (now);
    messages.push_back ({ std::move (lines), now });
    while (static_cast<int> (messages.size ()) > maxMessages)
        messages.pop_front ();
}

void
DrawChat::Draw ()
{
    if (GenericUtil::IsMenuActive ()) return;

    std::vector<std::string> visibleLines;
    bool hideDuringHideUi = false;
    {
        std::lock_guard<std::mutex> lock (mutex);
        if (!enabled) return;

        PruneExpiredLocked (std::chrono::steady_clock::now ());
        for (const auto &message : messages)
            visibleLines.insert (visibleLines.end (), message.Lines.begin (),
                                 message.Lines.end ());
        hideDuringHideUi = hideDuringHideChaosUi;
    }

    if (visibleLines.empty ()) return;
    if (hideDuringHideUi && Globals::enabledEffects["hide_chaos_ui"]) return;

    // Bound panel height and retain the newest rows when several long messages
    // arrive together. The configured message count/lifetime still use messages.
    constexpr size_t maxVisibleRows = 16;
    if (visibleLines.size () > maxVisibleRows)
        visibleLines.erase (visibleLines.begin (),
                            visibleLines.end () - maxVisibleRows);

    const float x = SCREEN_COORD_LEFT (14.0f);
    const float y = SCREEN_COORD_TOP (48.0f);
    const float rowHeight = SCREEN_COORD (18.0f);
    const float width = SCREEN_COORD (545.0f);
    const float padding = SCREEN_COORD (5.0f);

    CRect background (
        x - padding, y - padding, x + width,
        y + rowHeight * static_cast<float> (visibleLines.size ()) + padding);
    CSprite2d::DrawRect (background, CRGBA (0, 0, 0, 175));

    for (size_t i = 0; i < visibleLines.size (); ++i)
    {
        const float lineY = y + rowHeight * static_cast<float> (i);
        gamefont::PrintUnscaled (
            visibleLines[i], x, lineY, FONT_DEFAULT,
            SCREEN_MULTIPLIER (0.42f), SCREEN_MULTIPLIER (0.65f),
            color::White, gamefont::AlignLeft, 1, color::Black, true);
    }
}
