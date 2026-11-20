# Security policy

## Supported versions

Security fixes go into the latest minor version (currently **0.19.x**). Older versions do not receive patches; update the package to get them.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting instead: **Security → Report a vulnerability** on this repository. Include the affected version, the steps to reproduce, and the impact you expect. You should get a first answer within a week.

## What is in scope

The package lets an AI agent drive a Unity Editor, so these boundaries matter:

- The MCP endpoint, broker and bridge bind to loopback only (`127.0.0.1`, `::1`) and require a bearer token. Anything that accepts remote connections, skips the token, or accepts a non-loopback `Origin` is a vulnerability.
- Capability switches (writes, reflection, destructive tools, packages, builds, project settings, Play control, runtime commands) and the optional high-risk confirmation must not be bypassable.
- The reflection allow/deny policy must not let a tool reach the filesystem, processes or modal menus it blocks.
- The orphan cleanup must never terminate an Editor, Unity Hub or a process outside the Unity/broker trees it documents.

Out of scope: an attacker who already runs code as your Windows user (they can read the token and drive Unity anyway), and the behaviour of third-party MCP clients.

## Handling secrets

The token lives in the per-user environment variable `UNITY_MCP_BROKER_TOKEN` and in Unity's EditorPrefs; client configurations reference the variable, not the value. If a token leaks, use **Regenerate token** in **Window → Unity MCP Transport**, which updates Unity, the broker and clients that use the headers helper.
