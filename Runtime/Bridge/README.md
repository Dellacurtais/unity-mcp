# McpCommandBridge — quick start

The Unity MCP extension lets an AI agent run scripted gameplay in
play mode by sending **named commands** to a `MonoBehaviour` you place in
your scene. This is the runtime-side component of that channel.

## TL;DR

1. In your scene, ask the AI to call `bridge_install`, or add the
   **McpCommandBridge** component manually once compiled.
2. In your own MonoBehaviours, **register handlers** for the commands you
   want the AI to be able to invoke.
3. The AI calls `send_runtime_commands` with a list of `{ name, payload }`
   steps. Each step invokes the matching handler, in order, with the
   payload as a JSON string. You parse it however you like.

## Example — making "jump" controllable from the agent

```csharp
using UnityEngine;
using Maicon.SceneAnalyzer.Bridge;

public class PlayerController : MonoBehaviour
{
    Rigidbody _rb;

    void Start()
    {
        _rb = GetComponent<Rigidbody>();

        // Register on Start so it happens once per play session.
        McpCommandBridge.Register("jump", (json, complete) =>
        {
            // payload is a JSON string. Use whatever parser you have —
            // JsonUtility, Newtonsoft, hand-rolled. Empty string is OK.
            float force = 5f;
            if (!string.IsNullOrEmpty(json))
            {
                var args = JsonUtility.FromJson<JumpArgs>(json);
                if (args != null && args.force > 0) force = args.force;
            }

            _rb.AddForce(Vector3.up * force, ForceMode.Impulse);

            // Always call complete(). Pass success + optional error string.
            complete(true, null);
        });
    }

    [System.Serializable]
    class JumpArgs { public float force; }
}
```

The agent can now run:

```jsonc
// send_runtime_commands
{
  "commands": [
    { "name": "jump", "payload": "{\"force\":7}", "captureScreenshotAfter": true },
    { "name": "jump", "payload": "{\"force\":3}", "delayFrames": 30, "captureScreenshotAfter": true }
  ]
}
```

and get back two screenshots showing the player after each jump, plus the
per-step ok/error/duration.

## Handler contract

```csharp
delegate void CommandHandler(string payloadJson, Action<bool, string> complete);
```

- `payloadJson` — opaque JSON string. Parse if you need structure. Never null,
  but may be empty.
- `complete(ok, error)` — **you must call this exactly once**, either
  synchronously (most cases) or after an async operation finishes. If you
  never call it, the runtime gives up after the first configured limit
  (by default 600 frames or 10 realtime seconds) and reports `HANDLER_TIMEOUT`.
- Exceptions thrown out of your handler are caught and reported as failures.

## Async handlers

Coroutines work great:

```csharp
McpCommandBridge.Register("walk_to_corner", (json, complete) =>
{
    StartCoroutine(WalkTo(new Vector3(10, 0, 10), () => complete(true, null)));
});
```

Or `async void` — just be sure to call `complete` in the final continuation.

## Lifecycle gotchas

- The active bridge instance stores handlers in a dictionary keyed by name.
  A domain reload or play-mode restart recreates the instance; your `Start()`
  re-runs and re-registers. No bookkeeping is needed on your side.
- The bridge GameObject persists in the scene by default. If you want it
  to survive scene loads, the editor-side `bridge_install` tool sets
  `DontDestroyOnLoad` for you.
- Handlers are invoked on the Unity main thread — interact with Unity API
  freely.

## Security note

Anyone who can talk to the MCP HTTP server can invoke any registered
handler. The server is bound to `127.0.0.1` with a bearer token, so it's
local-only by default. Even so: **validate payloads** in your handlers.
The bridge is intentionally a thin pipe — it does not sanitize.
