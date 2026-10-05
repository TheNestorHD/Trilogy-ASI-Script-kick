# Kick integration

## Scope

Kick chat reading belongs to the stream/UI side of Trilogy Chaos Mod, not to the game ASI itself.

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

Use the official Kick Developer API and webhook event `chat.message.sent` version 1.

The connection flow should be:

1. The GUI/stream service performs OAuth 2.1 authorization for the broadcaster.
2. Request `events:subscribe`. Add `chat:write` only when the bot should also announce voting in Kick chat.
3. Subscribe the broadcaster to `chat.message.sent` through `POST /public/v1/events/subscriptions`.
4. Receive webhook POSTs on a public HTTPS endpoint.
5. Validate `Kick-Event-Signature` using the current public key fetched from `https://api.kick.com/public/v1/public-key`. Do not hardcode the key.
6. Use `Kick-Event-Message-Id` as the idempotency key.
7. Normalize each event to the same internal shape used by the other platforms:
   - platform = `kick`
   - messageId
   - userId
   - username
   - message
   - createdAt
8. Feed the normalized message into the existing voting engine.
9. Send the resulting `votes` message to the ASI. No separate Kick renderer is required in-game.

## Reliability requirements

The Kick implementation should have explicit states for OAuth, webhook registration, webhook delivery, and game websocket connection.

A dropped game connection must not lose the active vote state. Duplicate webhook deliveries must not double-count a user. A stale subscription must be detected and re-created.

The webhook handler should acknowledge quickly and move vote processing to an internal queue. Long-running work must not block the HTTP request.

Kick can automatically remove a webhook subscription after prolonged delivery failures, so health monitoring and re-subscription are part of the production implementation.

## Important boundary

The repository linked for the ASI contains the game plugin and does not contain the WinForms/stream UI that currently owns the YouTube/Twitch connection classes. Therefore the true end-to-end Kick connector cannot be fully implemented inside this repository alone.

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

## References

- https://docs.kick.com/events/event-types#chat-message
- https://docs.kick.com/events/subscribe-to-events
- https://docs.kick.com/events/webhook-security
- https://docs.kick.com/scopes/scopes
