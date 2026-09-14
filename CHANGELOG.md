# Changelog

## 0.6.0 (in progress)

- Show connected collaborators in the list during initial file reconciliation instead of waiting for Assets synchronization to finish.
- Show host-side Scene view status for each guest actively receiving synchronized files, using the collaborator's color and guest-reported receive percentage.
- Use a shared bottom-right Scene view status stack ordered by priority and then time; collaborator connect/disconnect events are high priority and remain visible for 8 seconds.
- Show white `Session started` and `Session ended` statuses for 8 seconds without treating package/script-reload reconnections as new sessions.
- Bump the UnitySync wire protocol to version 17 for guest download progress reporting. Both editors must use this build.
- Replace automatic package syncing with a registered-package version checklist showing missing packages, version differences, and guest-only packages.
- Add manual install/change/remove guidance, Copy checklist, and Recheck packages before Assets and scene synchronization.
- Never transfer or mutate Packages, including during Debug restore; the restore now targets Assets and ProjectSettings.
- Use explicit request scopes and wire protocol 15 so package rechecks work repeatedly. Both editors must use this build.

## 0.4.4

- Restore scene component serialization and staging behavior exactly to the implementation used before PR #41.
- Remove the PR #41/#42 stale-PPtr destination clearing and additional staging validation paths that regressed array-backed component synchronization.
- Restore initial snapshot application for component arrays and object-reference fields to the pre-0.4.2 behavior.

## 0.4.3

- Restore the dedicated typed staging-reference validator so valid object references in arrays are not rejected during initial scene synchronization.
- Keep raw instance-ID validation and clearing restricted to live destination components, preserving the stale-PPtr cast fix from 0.4.2.
- Fix snapshot application for array-backed references such as VRCSceneDescriptor spawns and MeshRenderer materials, as well as staged MeshFilter mesh references.

## 0.4.2

- Prevent initial scene synchronization from dereferencing stale local serialized object references while copying validated staging data back onto guest components.
- Clear only incompatible raw PPtr instance IDs before the final staging copy, then apply the host's already type-validated object references normally.
- Validate staged object references immediately before mutating the live component so an invalid staging state is rejected without touching the scene.

## 0.4.1

- Include each loaded scene's RenderSettings skybox material in the host scene snapshot and reapply it on guests after the object snapshot completes.
- Reimport newly synchronized dependency assets before materials so textures, shaders, and imported assets are available when material state is rebuilt.
- Force-reimport all project materials under Assets after file synchronization, covering cases where a material file already hash-matched but one of its referenced asset .meta files changed.
- Keep SerializedUdonPrograms excluded from the additional dependency/material reimport pass.
- Bump the UnitySync wire protocol to version 11 for the new per-scene skybox reference.

## 0.4.0

- Add the first host-to-guest project file synchronization pass. Guests now reconcile host files before requesting the initial scene snapshot.
- Build a SHA-256 manifest for files under Assets and transfer only files that are missing or whose local hash differs from the host.
- Include .meta files so synchronized assets retain the host project's GUIDs and references.
- Transfer large files in 512 KiB encrypted chunks instead of requiring an entire asset to fit inside one UnitySync frame.
- Keep guest-only files untouched; this initial implementation only creates or replaces files represented by the host manifest.
- Exclude every directory named SerializedUdonPrograms and its folder .meta from file synchronization.
- Validate synchronized paths so file writes cannot escape Assets or enter an excluded SerializedUdonPrograms tree.
- Show a modal Unity progress bar while the guest compares, downloads, imports, and compiles synchronized Assets, preventing normal editor interaction during the join reconciliation phase.
- Suppress guest scene publishing and ignore interim remote scene edits until file reconciliation completes, then request a fresh host scene snapshot.
- Refresh/import synchronized Assets before scene synchronization and automatically reconnect after an assembly reload when synchronized scripts trigger recompilation.
- Bump the UnitySync wire protocol to version 10 for file manifest, request, chunk, and abort messages.

## 0.3.6

- Fix the custom stacked selection-outline compositor being vertically inverted on graphics APIs whose render textures use a top-origin UV convention.
- Increase stacked collaborator outline bands from 1 px to 2 px so outlines outside the local Unity selection remain visually comparable to the native selection outline.

## 0.3.5

- Synchronize each collaborator's current Scene selection as lightweight session presence without creating new scene object identities just from clicking an object.
- Render remote selections in each collaborator's color using renderer silhouettes, with a native-style ~2 px innermost outline and 1 px outward stacking for additional collaborators selecting the same object.
- Stack the first remote outline outside the local Unity selection outline when the same object or one of its selected ancestors is selected locally.
- Use Unity's native Handles.DrawOutline path when the running Editor exposes it, with a supersampled screen-space silhouette fallback for supported renderers on older Unity versions.
- Add a Selection Outlines toggle to UnitySync Visual Options, enabled by default.
- Bump the UnitySync wire protocol to version 9 for synchronized selection presence.

## 0.3.4

- Resolve Unity built-in materials such as Default-Diffuse and Default-Skybox through Unity's built-in extra-resource API instead of assuming every built-in material is the primitive renderer's default material.
- Retain the primitive-derived default material path as a compatibility fallback.

## 0.3.3

- Remember the receiver's actual post-apply component state instead of the sender's serialized hash, preventing remotely applied Transform and component changes from being echoed back as fresh local edits and rubber-banding the active editor.
- Resolve one unambiguous same-path, same-type, same-name asset even when its local file ID or serialized file hash differs between editors, improving material reference synchronization without using ambiguous name-only matching across the project.

## 0.3.2

- Adopt matching pre-existing GameObjects during the initial host hierarchy snapshot instead of duplicating an already-matching scene hierarchy.
- Resolve a unique same-path asset with the same type and local file ID as the local equivalent, preventing valid material references from being dropped when project GUID/content metadata differs between collaborators.
- Apply VRC UdonBehaviour staging data and its Unity object references atomically so OnAfterDeserialize never sees a partially updated public-variable table.

## 0.3.1

- Add the missing Unity .meta file for the scene object registry so package installs import and compile the registry correctly.
- Fix scene-change hash deduplication to initialize its hash state before conditional capture, resolving the compiler's definite-assignment error.

## 0.3.0

- Replace sibling-path object identity with per-session IDs, so unrelated hierarchy differences no longer redirect scene updates to the wrong GameObject.
- Add host-authoritative hierarchy snapshots that create missing GameObjects, synchronize parenting and sibling order, build component layouts before values, and remove extra non-UnitySync objects from matching loaded scenes when the snapshot completes.
- Keep unmatched local objects when either side cannot serialize or apply a complete host snapshot, avoiding destructive cleanup after a partial update.
- Synchronize live GameObject creation, deletion, reparenting, scene moves, and reordering, including newly created hierarchy subtrees in a two-pass layout/value update.
- Resolve scene-object references through the per-session identity map instead of an assumed matching hierarchy path.
- Keep UnitySync's generated `[UnitySync]` hierarchy outside all snapshot and cleanup operations.

## 0.2.9

- Resolve and type-check every incoming object reference before changing a component, then apply references directly to the real component after staging its non-reference values.
- Stop storing scene-object and asset references on detached staging components, fixing object-reference arrays such as renderer materials and scene-descriptor spawn transforms.
- Add cached source-file SHA-256 identities to the scene-sync protocol and require exact content identity for cross-path asset searches.
- Remove name-only asset fallback matching so a collaborator never receives a different same-type asset when the requested asset cannot be proven identical.
- Reject a component capture instead of silently omitting a non-null object reference that has no stable cross-editor identity.

## 0.2.8

- Validate object references on disposable staging components through Unity's typed object-reference getter, preventing valid references inside arrays from being rejected.
- Keep the non-dereferencing validation path for live scene components so previously corrupted PPtrs are never read before staging.

## 0.2.7

- Select Unity's `long` local-file-ID overload explicitly when validating built-in assets, fixing an ambiguous overload compile error in Unity 2021.3.

## 0.2.6

- Assign staged object references through Unity's typed `objectReferenceValue` API instead of writing raw instance IDs into serialized PPtrs.
- Resolve Unity primitive meshes by creating the matching local primitive and reading the mesh Unity assigned to it.
- Resolve Unity's default material from a local primitive rather than guessing a built-in resource filename.
- Identify project and package assets by normalized path, concrete type, subasset name, and local file ID instead of requiring matching GUIDs.
- Search deterministically for a unique type/name/file match when collaborators store the same asset at different paths.
- Remove the receiver-local built-in asset cache and reflection-based resource loading workaround.

## 0.2.5

- Preserve validated built-in asset identities locally before synchronized fields are cleared.
- Resolve reassigned built-in meshes, materials, shaders, and other resources by exact type, GUID, and local file ID.
- Load known Unity built-in resource paths only as candidates, and reject them unless their persistent identity exactly matches the sender.
- Keep staged component application atomic when a built-in asset cannot be resolved safely.

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
