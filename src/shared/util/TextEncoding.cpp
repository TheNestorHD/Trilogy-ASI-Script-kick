#include "TextEncoding.h"

#include <Windows.h>

#include <string>

std::string
TextEncoding::ToGameText (const std::string &text)
{
    if (text.empty ()) return {};

    const int wideLength = MultiByteToWideChar (
        CP_UTF8, MB_ERR_INVALID_CHARS, text.data (),
        static_cast<int> (text.size ()), nullptr, 0);

    if (wideLength <= 0)
    {
        // Preserve safe ASCII for legacy strings that are not valid UTF-8.
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

    // Western single-byte encoding supports Spanish accents and ñ/Ñ.
    const int outputLength = WideCharToMultiByte (
        1252, 0, wide.data (), wideLength, nullptr, 0, "?", nullptr);
    if (outputLength <= 0) return {};

    std::string converted (static_cast<size_t> (outputLength), '\0');
    if (!WideCharToMultiByte (1252, 0, wide.data (), wideLength,
                              converted.data (), outputLength, "?", nullptr))
        return {};

    std::string clean;
    clean.reserve (converted.size ());
    for (unsigned char c : converted)
    {
        if (c == '\t') clean.push_back (' ');
        else if (c >= 0x20) clean.push_back (static_cast<char> (c));
    }
    return clean;
}
