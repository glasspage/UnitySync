# Changelog

## 0.2.4

- Apply incoming component data to a hidden disposable staging component instead of mutating the live scene component through `SerializedObject`.
- Validate every staged object reference through its raw instance ID before copying any values into the scene.
- Copy validated component state into the destination in one operation, while transforms use explicit hierarchy-safe value assignments.
- Rebuild already-corrupted components from clean staged defaults instead of copying their invalid PPtrs forward.

## 0.2.3

- Preserve matching local object references instead of rewriting their serialized pointers.
- Resolve assignable assets only by exact GUID and local file ID.
- Remove the broad loaded-resource fallback that could select an editor-internal built-in asset with an incompatible native identity.
- Improve object-reference resolution errors with the local serialized field type.

## 0.2.2

- Prevented invalid PPtr casts by validating serialized field and referenced object types before assignment.
- Removed dereferencing of the receiver's existing object reference while applying a remote update.
- Added type validation for synchronized scene-object references.
- Removed the pre-apply local snapshot comparison that dereferenced already-invalid receiver PPtrs.

## 0.2.1

- Fixed built-in Unity resources such as primitive meshes resolving to incompatible objects during scene synchronization.
- Added asset path, type, and name validation to synchronized object references.

## 0.2.0

- Added live synchronization for existing scene GameObjects, transforms, component lists, and serialized component settings.
- Added generic third-party component support through Unity's editor serialization APIs, including scene-object and asset references.
- Added host-authoritative scene snapshots when a collaborator joins.
- Added component add, remove, and reorder replication on existing GameObjects.
- Excluded UnitySync's temporary hierarchy from scene addressing and synchronization.
- Protected Unity's internal Transform hierarchy references from serialization and remote application.

## 0.1.2

- Moved UnitySync windows into a dedicated top-level UnitySync menu.
- Grouped all temporary hierarchy objects beneath `[UnitySync]`, with remote viewports under `Collaborators`.
- Added persistent visual options for viewport direction-line length and viewport opacity.
- Improved collaborator-name readability with colored bold text and a dark shadow.
- Smoothed remote viewport position and rotation updates.

## 0.1.1

- Fixed remote viewport objects multiplying because Unity cannot attach an Editor-assembly MonoBehaviour.
- Moved viewport gizmo rendering into the Editor callback while retaining transient per-user GameObjects.
- Added cleanup for orphaned UnitySync collaborator roots left by an affected session.

## 0.1.0

- Added encrypted host and client sessions over a direct TCP connection.
- Added compact shareable join codes containing the host endpoint and session key.
- Added live Scene view presence markers under a generated `EditorOnly` root.
- Added the UnitySync Editor window.
