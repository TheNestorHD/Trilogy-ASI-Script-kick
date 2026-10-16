#pragma once

#include <string>

// GTA's classic font consumes single-byte text, not UTF-8. Convert strings
// from JSON/WebSocket to the game's encoding before drawing.
class TextEncoding
{
public:
    static std::string ToGameText (const std::string &text);
};
