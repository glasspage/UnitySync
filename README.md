# UnitySync

UnitySync is a real-time collaboration add-on for **Unity Editor 2021.3+**. It lets multiple people work in the same Unity project with live scene editing, project-file synchronization, shared Scene view presence, and spectating.

> [!IMPORTANT]
> UnitySync directly changes scenes and project files while a session is active. It creates local scene backups before sessions, but source control or another project backup is still recommended.

## Features

- **Live scene editing** — synchronizes GameObjects, hierarchy, Transforms/RectTransforms, components, serialized properties, prefabs, and scene settings.
- **Project-file sync** — synchronizes supported files under **Assets**, including scripts, metadata, and ProjectSettings changes.
- **Host-authoritative ordering** — live scene, scene-setting, and project-file changes are ordered through the host.
- **Scene view collaboration** — see collaborator viewports, status information, and spectate other users.
- **Build-target sync** — collaborators follow the host's active build target.
- **Package compatibility checks** — package differences are shown before project synchronization begins.
- **Encrypted sessions** — session traffic is encrypted and authenticated using the generated join code.
- **Scene backups** — project scenes are backed up locally before sessions and can be restored from UnitySync's Debug section.

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

This method requires [Git](https://git-scm.com/install/) to be installed.

You can also install manually by downloading the code and placing it in your project's Packages folder.

After installation, a **UnitySync** menu is added to the top bar of the Unity Editor.

## Requirements

Collaborators must use the same Unity editor version.

UnitySync connects users directly over IPv4. A reachable LAN/VPN address or other direct network route is required.

## Network setup

[Hamachi](https://www.vpn.net/) is the recommended simple VPN option, but any setup that gives collaborators direct IPv4 connectivity can work.

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

Starting a host session saves the host's open scenes and creates a local scene backup before synchronization begins.

## Join a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Join a session**.
4. Paste the host's join code.
5. Select **Connect**.

On join, UnitySync verifies the Unity version and package state, then lets the guest review the host file download before applying it. Initial synchronization may trigger asset imports, compilation, domain reloads, or a build-target change; UnitySync resumes the session around supported reloads.

After project synchronization finishes, UnitySync applies the host scene snapshot and live collaboration begins.

## What it syncs

### Scenes

UnitySync synchronizes supported changes in loaded scenes, including GameObject creation/deletion, hierarchy, Transforms/RectTransforms, components, serialized properties, prefab identity, object references, and supported scene environment settings.

Scene view presence is associated with the matching scene.

### Project files

UnitySync synchronizes supported changes under **Assets** and **ProjectSettings**, including Unity assets, metadata, `.cs`, `.asmdef`, and `.asmref` files.

`.unity` scene files are handled by scene synchronization rather than the normal project-file watcher. `.dll` files are not live-synchronized.

### Packages

The **Packages** directory is not transferred or modified.

Before project synchronization, guests receive a checklist for missing packages, version differences, and guest-only packages to remove. Resolve those changes manually, then choose **Recheck packages**. **Copy checklist** keeps the instructions available if Unity must close while making package changes.

## Scene view tools

Open **UnitySync > Visual Options** to configure collaborator viewport indicators, direction indicators, and status display.

Use the **Collaborators** list in **UnitySync > Session** to spectate another collaborator.

## Backups

Before hosting or joining, UnitySync backs up project scenes under:

```text
Library/UnitySync/SceneBackup
```

Backups can be restored from the Debug section while no UnitySync session is active. They are a safeguard, not a replacement for source control.

## Limitations

- UnitySync does not automatically open or close scenes to match another collaborator.
- Simultaneous edits are host-ordered rather than semantically merged.
- Some Unity or third-party serialized data may not have a stable cross-editor identity and may require special handling.

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
