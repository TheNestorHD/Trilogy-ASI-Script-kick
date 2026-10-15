# GUI Kick overlay

This overlay is applied to a pinned checkout of `gta-chaos-mod/Trilogy-GUI-Tool` (upstream commit `688dccc2622ad85985d893bb683f0582f59d9b98`) by GitHub Actions. The official GUI repository is not writable from this project, so these files preserve a buildable patched copy without requiring the user to patch files manually.

## Kick chat reader

Kick mode opens the channel page in an embedded Microsoft Edge WebView2 window and observes the rendered chat DOM for newly added messages. Those messages are normalized and sent to the existing voting engine. The browser window is muted so that opening a channel page does not duplicate stream audio.

This path does not open a separate raw Pusher WebSocket, ask the user to inspect developer tools, or require an Access Token just to read public chat. The app requires the Microsoft Edge WebView2 Runtime to be installed. If Kick changes the DOM structure of its chat panel, the selectors in `KickChatConnection.cs` may need an update.

The browser reader intentionally does not replay existing chat history as new votes when it first attaches. It reads new messages as they appear after the chat container is found.

## Per-effect duration settings

On first launch, the GUI creates `effect_durations.json` beside `GTAChaos.exe`. Each effect ID has a `name` and a `duration_seconds` field.

- Edit `name` to customize the effect's display name. The custom name is used in the OBS voting overlay and wherever the game/GUI displays that effect's name.
- Leave `duration_seconds` as `null` to use that effect's normal duration.
- Set `duration_seconds` to a number, for example `12`, to override that effect to 12 seconds.
- In stream mode, the final duration sent to the game is always capped at 30 seconds, including effects with built-in durations or multipliers.
- Restart the GUI after editing the JSON file so the changes are loaded.

Example:
```json
{
  "effect_example": {
    "name": "¡Modo caos total!",
    "duration_seconds": 12
  }
}
```

The generated file is merged with the current effect list on startup, so newly added effects are added automatically while existing custom names and duration choices are preserved.

## Animated OBS voting overlay

The GUI build includes `obs-voting-overlay.html`, a transparent animated poll for OBS Browser Source. It connects to the GUI's local WebSocket (port `42069` by default), updates vote bars live, and shows winning option(s) when the voting closes.

1. Download and extract the `GTAChaos.KickChat` artifact from the GitHub Actions build.
2. In OBS, add a **Browser** source and enable **Local file**. Select `obs-voting-overlay.html` from the extracted folder.
3. Start the GTA Chaos GUI on the same PC. Keep the GUI running so its WebSocket server can send voting data to the overlay.
4. Set the browser source to a canvas size such as `1000 × 440`, then position and scale it in your scene. The page background is transparent.
5. Enable **Shutdown source when not visible** in the Browser source properties if you want hiding/removing the source to disconnect it.

When the OBS overlay connects, the GUI automatically sends `???` for the three in-game voting labels while sending the real effect names to OBS. When the Browser source disconnects, the GUI stops treating the overlay as active. The regular **Hide voting effects ingame** option remains respected as well.

The overlay uses `ws://127.0.0.1:42069` by default. If you changed the WebSocket port in the GUI, update the default port in the overlay script (or provide a `?port=YOUR_PORT` query parameter when loading it as a URL).
