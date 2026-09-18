# UnitySync

UnitySync is a real-time collaboration add-on for **Unity Editor 2021.3+**. It lets multiple people work in the same Unity project at the same time with live scene editing, project-file synchronization, shared Scene view presence, spectating, and host-authoritative conflict ordering.

> [!IMPORTANT]
> UnitySync directly changes scenes and project files while a session is active. It creates local scene backups before sessions, but using source control or another project backup is still strongly recommended.

## Features

- **Live scene editing** — synchronizes GameObjects, hierarchy changes, Transforms/RectTransforms, components, serialized properties, prefab identity, and supported scene environment settings.
- **Cross-scene collaboration** — scene identity is tracked so edits and collaborator viewport indicators stay associated with the correct scene.
- **Project-file sync** — synchronizes supported files under **Assets**, including scripts, `.asmdef`, `.asmref`, their `.meta` files, and supported **ProjectSettings** changes.
- **Host-authoritative ordering** — scene changes, scene settings, and project-file updates are ordered through the host so collaborators converge on the same observed state.
- **Build-target sync** — collaborators follow the host's active build target, including across Unity domain reloads.
- **Initial project reconciliation** — joining collaborators compare against the host, receive changed project files, import them, then receive the live scene snapshot.
- **Package compatibility checks** — package differences are shown before project synchronization begins so collaborators can resolve them manually.
- **Scene view presence** — collaborators appear as colored viewport indicators, with optional direction indicators, status messages, and a session pill.
- **Spectating** — follow another collaborator's Scene view from the **Collaborators** list.
- **Encrypted sessions** — session traffic is encrypted and authenticated using the generated join code.
- **Pre-session scene backups** — UnitySync backs up project scenes locally under `Library/UnitySync/SceneBackup` and exposes restore controls in the Debug section.

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

Git must be installed on the computer running Unity.

After installation, a **UnitySync** menu is added to the top bar of the Unity Editor.

## Requirements

Collaborators must use the **same Unity editor version**. UnitySync checks this during the connection handshake and rejects mismatched versions before synchronization begins.

Collaborators should also have compatible third-party dependencies and the same required packages installed. UnitySync checks package names and versions, but it does not install, remove, or update packages automatically.

UnitySync connects directly to the host over IPv4. A reachable LAN/VPN address or other direct network route is required.

## Network setup

[Hamachi](https://www.vpn.net/) is the recommended simple VPN option, but any setup that gives collaborators direct IPv4 connectivity to the host can work.

The default TCP port is **47832**.

## Host a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Host a session**.
4. Enter an IPv4 address the other collaborators can reach.
5. Leave the port at **47832**, or choose another available TCP port.
6. Select **Start Hosting**.
7. Share the generated join code with your collaborators.

When using Hamachi or another VPN, use the host's VPN-provided IPv4 address.

Starting a host session saves the host's open scenes before synchronization begins and creates a local scene backup.

## Join a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Join a session**.
4. Paste the host's join code.
5. Select **Connect**.

On join, UnitySync verifies the Unity version and package state before reconciling project files. The guest can review the host file download before it is applied. Asset imports, script compilation, assembly-definition changes, and build-target changes can trigger normal Unity compilation/domain reloads during this process; UnitySync preserves and resumes the session around supported reloads.

After project reconciliation finishes, UnitySync applies the host scene snapshot and normal live collaboration begins.

## Synchronization behavior

The host is the authority for live collaborative ordering. Guest edits are sent to the host, applied in the host-observed order, and redistributed to collaborators. Continuous edits remain responsive locally while authoritative echoes are coalesced and applied after the originating edit settles.

This provides deterministic convergence for overlapping edits, but it is **not a semantic merge system**. If multiple people change the same property or file at nearly the same time, the host-observed ordering determines the resulting state.

### Scenes

UnitySync synchronizes supported changes in loaded scenes, including:

- GameObject creation, deletion, hierarchy, names, active state, layers, and tags.
- Transform and RectTransform changes.
- Component addition/removal and serialized property changes.
- Object references with stable cross-editor identity.
- Live-added prefab identity and instance overrides.
- Supported lighting, RenderSettings, and other scene environment settings.

Scene assets themselves are not transferred through the ordinary live project-file watcher. Scene collaboration is handled by UnitySync's scene synchronization system.

UnitySync tracks scene identity across collaborators. It does not automatically mirror which scenes each editor has open, and Scene view presence is only shown when collaborators are viewing the corresponding scene.

### Project files

UnitySync synchronizes supported project changes under **Assets** and supported **ProjectSettings** changes.

Live project-file sync includes Unity assets and metadata as well as `.cs`, `.asmdef`, and `.asmref` files. Script and assembly-definition changes are flushed before Unity compilation/reload so they are not lost when the editor domain reloads.

`.unity` scene files and `.dll` files are excluded from the ordinary live project-file watcher.

### Packages

The **Packages** directory is not transferred or modified.

Before project-file synchronization, the guest receives a checklist for missing packages, version differences, and guest-only packages that should be removed. Resolve those changes manually, then choose **Recheck packages**. **Copy checklist** keeps the instructions available if Unity needs to close while package changes are made.

### Build target

The host's active build target is sent during the initial connection and synchronized during the session. UnitySync preserves supported sessions across build-target-triggered script/domain reloads.

## Scene view tools

Connected collaborators appear in the Scene view with colored viewport indicators. Open **UnitySync > Visual Options** to configure viewport appearance, direction indicators, status-log visibility, and status placement.

The Scene view status UI can show session state, collaborator connect/disconnect events, asset-import activity, scene synchronization progress, and the current collaborator count.

Use the **Collaborators** list in **UnitySync > Session** to spectate a collaborator once their Scene view presence is available.

## Backups and recovery

Before a session starts or joins, UnitySync creates a local backup of scene assets under:

```text
Library/UnitySync/SceneBackup
```

Guest backups preserve dirty loaded scene state without replacing the working scene. The Debug section can restore the backed-up project scenes when no UnitySync session is active.

UnitySync also keeps recovery state for failed incoming live scene edits and exposes recovery/debug controls when synchronization cannot be completed normally.

These recovery systems are intended as safeguards, not a replacement for source control.

## Security

Session traffic is encrypted and authenticated. Keep join codes private.

UnitySync does not provide accounts, per-user permissions, host approval prompts, relay servers, matchmaking, or automatic NAT traversal.

## Limitations

- Direct IPv4 connectivity to the host is required.
- All collaborators must use the exact same Unity editor version.
- Packages and third-party dependencies must be made compatible manually.
- UnitySync does not automatically open or close scenes to match another collaborator.
- `.dll` files are not live-synchronized.
- There is no semantic merge/conflict-resolution system for simultaneous edits to the same state.
- Some Unity or third-party serialized data may not have a stable cross-editor identity or may require special handling. UnitySync reports failed synchronization in the Activity Log and Unity Console rather than silently applying an unsafe reference.

## Troubleshooting

If someone cannot connect:

- Confirm everyone is using the same Unity editor version.
- Confirm they can reach the host's chosen IPv4 address.
- Confirm TCP port **47832** is available, or choose another port.
- If using a VPN:
  - Confirm everyone is connected to the same VPN network.
  - Ensure the firewall is not blocking VPN traffic or UnitySync's TCP connection.

If synchronization appears incomplete, let any asset import, script compilation, assembly reload, or build-target switch finish. Check the **Activity Log** in **UnitySync > Session** and the Unity Console for `[UnitySync]` errors, then confirm everyone has the required packages and dependencies installed.

## AI assistance disclosure

The code in this repository was created with assistance from AI tools. I made the architecture, design and implementation decisions; AI was used to write the code based on them.
