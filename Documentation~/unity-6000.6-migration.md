# Unity 6000.6 migration

The package supports Unity 6000.6 (developed and tested on 6000.6.7f1). Unity 6000.6
replaces integer object and scene identifiers with `EntityId` and `SceneHandle`; this note
records how the package adopted them without breaking MCP clients. Earlier Unity branches
keep compiling through version symbols.

## API changes

- `Object.GetEntityId()` and `EditorUtility.EntityIdToObject()` replace the
  obsolete integer object APIs. `UnityObjectIds` stores `EntityId` values as
  dictionary keys and values; it never truncates or hashes them into an ID.
- `SerializedProperty.objectReferenceEntityIdValue` is used for exact native
  reference comparison and for detecting missing references, including null
  object references that still have serialized identity.
- `UnitySceneIds` stores native `SceneHandle` values directly, without implicit
  integer conversions. Scene selection, snapshots, mutation tracking, Prefab
  Stage reporting and tests use the same adapter.
- On Unity 6000.6, object searches use `FindObjectsByType` overloads without
  `FindObjectsSortMode`. EventSystem discovery uses `FindAnyObjectByType`.
- Earlier Unity branches are selected by compilation symbols. They have not
  been revalidated by this Unity 6000.6 migration.
- Scene-copy destination checks use `AssetPathToGUIDOptions.OnlyExistingAssets`.
  Unity retains GUIDs for assets deleted during the current Editor session;
  the default lookup must not be treated as proof that a destination exists.

## MCP identity contract

The existing JSON integer fields `instanceId`, component/parent IDs and
`sceneHandle` remain integers for client compatibility. On modern Editors they
are **opaque handles allocated by this package**, not raw native Unity IDs.
Use IDs from this package's own read/create responses. IDs from another Unity
automation integration or an older capture are not interchangeable.

Object mappings store the full `EntityId`, not strong references to Unity
objects. Deleted objects resolve to null. Mappings are retained within a domain
so Undo can restore the same native identity. Handles expire on assembly reload;
the counter survives that reload via `SessionState`, so an expired handle cannot
accidentally resolve a newly registered object. Both maps and counters expire
when the Editor session ends. Reacquire transient handles after reconnecting.

Preserve the complete `target` object, including `captureDomainId`, whenever
possible. `globalObjectId`, asset GUID/local file ID and scene identity retain
the package's durable resolution behavior. No EntityId bit layout, sign,
32-bit conversion or JavaScript numeric precision assumption is required.

## Documentation used

The installed documentation is the reference for this update:

`C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Data/Documentation/en`

- `Manual/UpgradeGuideUnity65.html` and `Manual/UpgradeGuideUnity66.html`
- `ScriptReference/EntityId.html`
- `ScriptReference/Object.GetEntityId.html`
- `ScriptReference/EditorUtility.EntityIdToObject.html`
- `ScriptReference/SerializedProperty-objectReferenceEntityIdValue.html`
- `ScriptReference/SceneManagement.SceneHandle.html`
- `ScriptReference/Object.FindObjectsByType.html`
- `ScriptReference/AssetDatabase.AssetPathToGUID.html`

Compilation and test evidence is recorded in the
[validation report](unity-6000.6-validation.md).
Optional modules remain gated by their existing package dependencies. Updating
their source is not equivalent to exercising an absent optional integration.
