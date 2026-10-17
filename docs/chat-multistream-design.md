# In-game Chat + MultiStream — design notes

## Product scope

Add an in-game stream chat renderer with a SA-MP-inspired presentation and a MultiStream source manager. The same source manager should serve both the chat renderer and stream voting, but chat visibility and voting-source selection remain independently configurable.

Supported source adapters in this project: Twitch, YouTube Live, and Kick. MultiStream should allow two or three configured sources simultaneously. Follows, subscriptions, bits, Kicks, memberships, donations, and other alert events are explicitly out of scope.

## GUI organization

### In-game Chat
- Enable/disable in-game chat.
- Control visible line count, message lifetime, text scale, background opacity, and optional platform tag.
- Checkbox: **Hide chat during “Hide Chaos Mod GUI” effects**.
- Proposed default: unchecked, so the chat remains visible unless the streamer explicitly asks to hide it.
- Per-source connection status and a test/preview action.

The chat renderer must be independent of the existing Chaos Mod UI visibility gate. It must not be suppressed merely because Drawing.Enabled is off or hide_chaos_ui is active; the new chat-specific checkbox decides whether hide_chaos_ui also hides this chat.

### MultiStream
- Configure up to three sources. Each slot has a platform selector and the credentials/channel identifier needed by that adapter.
- Each configured source can be enabled separately for **in-game chat** and **voting**.
- One-source mode is simply MultiStream with one enabled source, not a separate voting implementation.
- A failed source should show its own error/status and must not silently disable other connected sources.

## Shared chat pipeline

Each platform adapter emits normalized plain-text messages containing:
- platform ID/name;
- username;
- text;
- optional username color only when the adapter provides a reliable value;
- source message ID when available.

The aggregator applies length/history limits and enqueues messages for the game renderer. Network callbacks must never draw directly into the game's render thread; the C++ renderer consumes a bounded, thread-safe queue.

### Emotes and rich media

The game will render text only. Do not download, decode, embed, or draw platform emote images, animated emotes, stickers, avatars, badges, or other media. Adapters should take only the plain-text representation already provided by the platform library/API. Ignore rich-media payloads and discard messages that have no visible plain text after filtering. This avoids adding image dependencies, asset memory, or animation work to GTA San Andreas.

## Shared voting pipeline

Voting should be moved to one round/coordinator shared by every source enabled for voting:
1. Generate the three candidate effects **once per round**.
2. Broadcast the same candidate names and current vote totals to all connected platform adapters and the game/OBS overlay.
3. Normalize each incoming message and feed it to the same vote parser.
4. Track a voter by a composite identity such as platform + normalized username so usernames that happen to match on separate platforms do not collide.
5. Count at most one accepted vote per platform identity per round, subject to the existing voting rules.
6. When the round ends, select/execute the result once, regardless of how many platforms are connected.

There is no reliable way to determine that accounts on two unrelated platforms belong to the same real person. Unless a platform offers an authenticated cross-platform identity (not currently assumed), one account on each connected platform can be counted as a separate voter.

The regular single-platform path should use the same coordinator with only one adapter enabled. This prevents single-platform and MultiStream voting behavior from drifting apart.

## Debug commands

Add a separate GUI tab with an opt-in setting for chat commands. When enabled, the automatic main/stream timer must stop advancing; it resumes when the setting is disabled and the timer was otherwise running.

Accepted commands: `!chaos <effect_id>` and `!chaos clear`. Parse them before normal vote handling and consume them so they cannot count as votes. Resolve exact effect IDs, execute only the requested effect with its effective normal duration (including the per-effect JSON override, multiplier, and mode cap), bypass normal effect cooldown for this manual test, and report malformed/unknown IDs in the GUI. The `clear` alias invokes the existing `clear_active_effects` action. The command handler is local/read-only: it does not send messages back to any platform. Keep the feature disabled by default.

## Emote and empty-message policy

All game/chat overlay text must be plain text. Strip message spans that the adapter identifies as emotes, and never load emote images, GIFs, stickers or avatar assets. Kick DOM parsing removes rich-media nodes before reading text; YouTube only uses ordinary text runs; Twitch can use TwitchLib's emote start/end metadata. If cleanup leaves a blank or whitespace-only message, discard it before adding anything to overlay history.

## OBS voting overlay

- The instruction should be platform-neutral by default (not hard-coded to Kick).
- Add a thin progress strip along the bottom of the instruction footer.
- Use the existing WebSocket time event: when mode is Voting, update the strip from remaining/cooldown milliseconds, decreasing smoothly from full to empty. Hide/reset the strip outside voting.
- The bar belongs to the OBS overlay itself and must continue to work when the Chaos Mod's in-game UI is hidden.
- Vote counts and options must be the combined totals from the shared voting coordinator.

## Implementation status on this branch

- The OBS footer progress strip and platform-neutral instruction are implemented.
- The GUI includes MultiStream source profiles and a shared voting coordinator, with up to three sources and per-source chat/voting toggles.
- Chat options are in an independent **Chat** tab that remains available for single-platform streams and while connected. They apply live and persist to `config.cfg`.
- Chat controls include visibility, hiding with the Chaos UI effect, 1–15 messages, 3–120 seconds lifetime, 70–200% font size, and a test-message button. The new default font is 135% of the former size; old saved configurations without this field receive that default.
- Changing chat font size immediately rewraps existing messages and scales row height, while the panel stays bounded by the viewport.
- A native plain-text chat renderer is wired through the existing websocket protocol. The GUI exposes enable/disable, maximum visible messages, message lifetime, and whether chat should hide during `hide_chaos_ui`.
- Chat messages and chat settings are not buffered while the game is disconnected. When the native client sends its `game` handshake on connect/reconnect, the GUI sends fresh chat settings.
- YouTube paid-message and membership events are intentionally excluded; platform adapters publish normalized text only.
- Chat rows use word wrapping and a bounded panel height instead of truncating each message to one line.
- UTF-8 text is converted to the game's Western single-byte font encoding before drawing, including voting option names and active-effect names/subtext.

Build workflows must still be checked, and in-game tests should cover one, two, and three sources; reconnects; vote identity and updates; message expiry; and the hide-UI option.
