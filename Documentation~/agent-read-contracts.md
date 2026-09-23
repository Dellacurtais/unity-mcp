# Bounded reads for agents

`read_hierarchy` keeps its existing name and identity fields, but an empty call
now returns a **page**, not an unbounded scene export. Defaults are 250 nodes,
descendant depth 64 and 262,144 UTF-8 bytes of `structuredContent`; hard maxima
are 1,000 nodes, depth 256 and 1,048,576 bytes. The byte budget does not include
the MCP envelope or the compatibility text copy of structured data.

Use `scene` for an exact loaded scene path, GUID or unique name. Use `root` for
a stable `ToolTargetRef` (preferred) or a session-local instance ID. Depth zero
includes only the selected root, or the scene roots when no root is selected.
An ambiguous scene name and a root outside the scene scope fail explicitly.

```json
{"scene":"Assets/Scenes/Level.unity","limit":100,"maxBytes":65536,"fields":["componentTypes","isActive"]}
```

Every node includes `instanceId`, `name`, `parentId` and its captured `target`.
Omitting `fields` returns all legacy node fields; `fields: []` returns only these
identity fields. `childIds` can be large on a root with many direct children;
omit it when only identities and components are needed.

- `count`: total objects in all loaded scenes, preserving the legacy meaning.
- `scopeCount`: objects in the selected scene/subtree before the depth filter.
- `matched`: objects in that scope within the requested depth.
- `offset`, `returned`, `hasMore`, `nextCursor`: page position and continuation.
- `depthLimited`: some descendants are outside the depth filter. Read a deeper
  subtree to inspect them; the cursor only pages the current query.
- `truncated`: `hasMore` or `depthLimited`.
- `snapshotVersion`: current snapshot revision and Editor domain generation.

For the next page repeat `root`, `scene`, `depth` and `fields`, supplying the
returned `nextCursor` as `cursor`. `limit` and `maxBytes` may change. A caller can
also pass an expected `snapshotVersion`. Do not edit or decode cursors. A scene
mutation, snapshot invalidation or domain reload produces `SNAPSHOT_STALE`;
restart the read without its old cursor/version. Hierarchy, Undo and Play Mode
events mark the snapshot stale without scanning the scene in the event callback.
Pages describe the captured snapshot; this is not a live per-frame gameplay feed.

If metadata or a single next node cannot fit, the tool returns
`BYTE_BUDGET_TOO_SMALL`, never an empty page with a cursor that cannot advance.
Narrow the scene/subtree, select fewer fields, or raise the budget.

`get_tool_inventory` materializes schemas only for the selected page and uses
the immutable registered schemas. Existing module/category/capability filters
and offset/limit arguments remain available.

`get_server_health({"compact":true})` retains health checks and counts but omits
loaded-module listings, recent job rows and successful package-compatibility
rows. `detail` identifies `compact` or `full`. The full default is unchanged.
Compact health still executes the self-test; it reduces response size, not the
cost or scope of verification. Empty omitted-detail arrays are not claims that
no modules or retained jobs exist; use the accompanying counts or request full
health to inspect them.
