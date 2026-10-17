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

size_t
DrawChat::MaxLineCharacters ()
{
    // Keep a constant panel width as the font changes. Extra space in
    // proportional glyphs is covered by the conservative base capacity.
    return std::max<size_t> (24, 84 * 100 / fontSizePercent);
}

std::vector<std::string>
DrawChat::WrapMessage (const std::string &prefix, const std::string &message)
{
    // Text is converted to the game's single-byte encoding before wrapping.
    // This prevents UTF-8 continuation bytes from counting as extra glyphs.
    const size_t maxLineCharacters = MaxLineCharacters ();
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
                        int _maxMessages, int _durationSeconds,
                        int _fontSizePercent)
{
    std::lock_guard<std::mutex> lock (mutex);
    enabled = _enabled;
    hideDuringHideChaosUi = _hideDuringHideChaosUi;
    maxMessages = std::clamp (_maxMessages, 1, 15);
    durationSeconds = std::clamp (_durationSeconds, 3, 120);
    const int newFontSize = std::clamp (_fontSizePercent, 70, 200);
    if (newFontSize != fontSizePercent)
    {
        fontSizePercent = newFontSize;
        // Existing messages immediately reflow when the viewer changes scale.
        for (auto &entry : messages)
            entry.Lines = WrapMessage (entry.Prefix, entry.Message);
    }

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

    std::lock_guard<std::mutex> lock (mutex);
    if (!enabled) return;

    // Reserve enough room for at least some message text at large font sizes.
    const size_t maxPrefix = MaxLineCharacters () - 12;
    const size_t overhead = cleanPlatform.size () + 5;
    if (overhead + cleanUsername.size () > maxPrefix)
        cleanUsername.resize (maxPrefix > overhead ? maxPrefix - overhead : 1);

    const std::string prefix
        = "[" + cleanPlatform + "] " + cleanUsername + ": ";
    std::vector<std::string> lines = WrapMessage (prefix, cleanMessage);
    if (lines.empty ()) return;

    const auto now = std::chrono::steady_clock::now ();
    PruneExpiredLocked (now);
    messages.push_back ({ prefix, cleanMessage, std::move (lines), now });
    while (static_cast<int> (messages.size ()) > maxMessages)
        messages.pop_front ();
}

void
DrawChat::Draw ()
{
    if (GenericUtil::IsMenuActive ()) return;

    std::vector<std::string> visibleLines;
    bool hideDuringHideUi = false;
    int visibleFontPercent = 100;
    {
        std::lock_guard<std::mutex> lock (mutex);
        if (!enabled) return;

        PruneExpiredLocked (std::chrono::steady_clock::now ());
        for (const auto &message : messages)
            visibleLines.insert (visibleLines.end (), message.Lines.begin (),
                                 message.Lines.end ());
        hideDuringHideUi = hideDuringHideChaosUi;
        visibleFontPercent = fontSizePercent;
    }

    if (visibleLines.empty ()) return;
    if (hideDuringHideUi && Globals::enabledEffects["hide_chaos_ui"]) return;

    const float x = SCREEN_COORD_LEFT (14.0f);
    const float y = SCREEN_COORD_TOP (48.0f);
    const float scaleRatio = static_cast<float> (visibleFontPercent) / 100.0f;
    const float rowHeight = SCREEN_COORD (18.0f * scaleRatio);
    const float width = SCREEN_COORD (545.0f);
    const float padding = SCREEN_COORD (5.0f);

    // Bound the panel to the viewport, even with 200% text and many messages.
    const float availableHeight
        = std::max (rowHeight, SCREEN_HEIGHT - y - padding * 2);
    const size_t maxVisibleRows = std::min<size_t> (
        16, std::max (1, static_cast<int> (availableHeight / rowHeight)));
    if (visibleLines.size () > maxVisibleRows)
        visibleLines.erase (visibleLines.begin (),
                            visibleLines.end () - maxVisibleRows);

    CRect background (
        x - padding, y - padding, x + width,
        y + rowHeight * static_cast<float> (visibleLines.size ()) + padding);
    CSprite2d::DrawRect (background, CRGBA (0, 0, 0, 175));

    for (size_t i = 0; i < visibleLines.size (); ++i)
    {
        const float lineY = y + rowHeight * static_cast<float> (i);
        gamefont::PrintUnscaled (
            visibleLines[i], x, lineY, FONT_DEFAULT,
            SCREEN_MULTIPLIER (0.42f * scaleRatio),
            SCREEN_MULTIPLIER (0.65f * scaleRatio),
            color::White, gamefont::AlignLeft, 1, color::Black, true);
    }
}
