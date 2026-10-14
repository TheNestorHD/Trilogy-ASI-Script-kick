# GUI Kick overlay

This overlay is applied to a pinned checkout of `gta-chaos-mod/Trilogy-GUI-Tool` (upstream commit `688dccc2622ad85985d893bb683f0582f59d9b98`) by GitHub Actions. The official GUI repository is not writable from this project, so these files preserve a buildable patched copy without requiring the user to patch files manually.

Kick read-only transport uses the public channel slug, resolves `chatroom.id` from Kick's public web API, and subscribes to Pusher channel `chatrooms.{id}.v2`. No login, public webhook, public endpoint, or Kick chat write permission is used. The Pusher key and web endpoint are unofficial web internals and may change; CI proves compilation only, not that Kick has not changed its runtime protocol.
