# Kick integration

## Scope

Kick chat reading belongs to the stream/UI side of Trilogy Chaos Mod, not to the game ASI itself.

The important requirement for this project is that the user should only need to enter the Kick channel name. The mod does not need its own public HTTP endpoint, a hosted service, or a Kick broadcaster login just to read public chat messages.

The ASI already exposes the exact rendering primitive needed by any chat provider:

```json
{
  "type": "votes",
  "data": {
    "effects": ["effect_a", "effect_b", "effect_c"],
    "votes": [12, 7, 3],
    "pickedChoice": -1
  }
}
```

Sending this message makes the existing `DrawVoting` renderer display the three choices and vote bars exactly like the current YouTube path.

## Recommended Kick architecture

Use Kick's public web chat transport directly instead of the official webhook system.

Current Kick web clients expose chat through a Pusher WebSocket channel. A practical desktop implementation can follow the same model:

1. The user enters a channel slug, for example `eskrotos`.
2. Resolve that slug to the channel's numeric `chatroom.id`.
3. Open Kick's public Pusher WebSocket connection.
4. Subscribe to `chatrooms.{chatroom_id}.v2`.
5. Parse incoming chat events and normalize them to the common internal shape:
   - platform = `kick`
   - messageId
   - userId
   - username
   - message
   - createdAt
6. Feed those messages directly into the existing voting engine.
7. Send the resulting `votes` message to the ASI.

There is no OAuth flow, webhook registration, public HTTPS endpoint, or Kick bot account involved in this read-only path.

### Resolving the chatroom ID

The channel slug and the chatroom ID are different values. Current community implementations resolve the slug through Kick's web/channel data and then use the returned `chatroom.id` for the Pusher subscription.

The resolver should try to obtain the ID automatically from the channel name and cache it locally. Because Kick's web/API endpoints are protected by Cloudflare in some environments, the resolver should have a browser-context fallback rather than depending on a server-side HTTP request always succeeding.

Once the chatroom ID has been found, the actual live chat connection is a direct WebSocket subscription.

## Voting behavior

The Kick chat should behave like the existing YouTube voting path.

When a vote is active, messages containing exactly `1`, `2`, or `3` count as votes for the corresponding choice.

A user gets one active vote per voting round. Sending another valid choice replaces that user's previous choice instead of adding another vote.

Messages that are not valid vote commands are still readable for logging/debugging, but they do not affect the vote counters.

## Reliability requirements

The Kick reader should reconnect automatically when the Pusher connection drops and keep the current voting state in the GUI.

The chat reader should deduplicate messages using the message ID when one is available. Reconnection must not cause old messages to be counted twice.

The UI should expose a simple status such as:

- Kick: desconectado
- Kick: buscando sala de chat
- Kick: conectado
- Kick: reconectando

The GUI log should also show the received chat messages during testing so it is obvious that `1`, `2`, and `3` are being detected.

## Important boundary

The repository linked for the ASI contains the game plugin and does not contain the WinForms/stream UI that currently owns the YouTube/Twitch connection classes. Therefore the direct Kick chat reader must be implemented in that GUI application, while this repository only needs to expose the game-side websocket contract.

This fork now makes the game-side contract game-aware through the websocket handshake:

```json
{
  "type": "game",
  "data": {
    "id": "gta3 | vice_city | san_andreas",
    "protocolVersion": 2
  }
}
```

A GUI can use this to select the correct effect catalog and platform configuration without hardcoding a particular game.

## Implementation note

This direct-chat approach intentionally uses an unofficial/private web transport rather than Kick's official event webhook API. That is a deliberate tradeoff for this project: zero account setup and zero public infrastructure in exchange for the possibility that Kick changes its web chat protocol in the future.

The code should isolate the Kick transport behind a small `KickChatConnection`/stream-connection interface so a future official API implementation could replace it without changing the voting engine.

## References

- Kick web chat currently uses Pusher channels such as `chatrooms.{chatroom_id}.v2`.
- Kick's public web/channel data exposes the chatroom ID needed to subscribe.
- Official Kick webhooks remain available as a separate, more formal integration path, but are not required for this mod's read-only chat use case.
