# Changelog

## 0.1.1

- Fixed remote viewport objects multiplying because Unity cannot attach an Editor-assembly MonoBehaviour.
- Moved viewport gizmo rendering into the Editor callback while retaining transient per-user GameObjects.
- Added cleanup for orphaned UnitySync collaborator roots left by an affected session.

## 0.1.0

- Added encrypted host and client sessions over a direct TCP connection.
- Added compact shareable join codes containing the host endpoint and session key.
- Added live Scene view presence markers under a generated `EditorOnly` root.
- Added the UnitySync Editor window.
