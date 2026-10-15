# GUI Kick overlay

This overlay is applied to a pinned checkout of `gta-chaos-mod/Trilogy-GUI-Tool` (upstream commit `688dccc2622ad85985d893bb683f0582f59d9b98`) by GitHub Actions. The official GUI repository is not writable from this project, so these files preserve a buildable patched copy without requiring the user to patch files manually.

## Kick chat reader

Kick mode opens the channel page in an embedded Microsoft Edge WebView2 window and observes the rendered chat DOM for newly added messages. Those messages are normalized and sent to the existing voting engine. The browser window is muted so that opening a channel page does not duplicate stream audio.

This path does not open a separate raw Pusher WebSocket, ask the user to inspect developer tools, or require an Access Token just to read public chat. The app requires the Microsoft Edge WebView2 Runtime to be installed. If Kick changes the DOM structure of its chat panel, the selectors in `KickChatConnection.cs` may need an update.

The browser reader intentionally does not replay existing chat history as new votes when it first attaches. It reads new messages as they appear after the chat container is found.
