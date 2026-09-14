# UnitySync

UnitySync is an experimental real-time collaboration add-on for **Unity Editor 2021.3+**. It lets multiple people work in the same project with live scene editing, project asset syncing, shared Scene view presence, and spectating.

> [!WARNING]
> UnitySync is *not* considered a fully-functioning add-on yet and issues are expected. ***Use a backup project for testing!***

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

Git must be installed on the computer running Unity.

After installation, a **UnitySync** menu is added to the top bar of the Unity Editor.

## Network setup

UnitySync connects collaborators directly to the host over IPv4.
[Radmin VPN](https://www.radmin-vpn.com/) is the simplest way to connect users together, but other direct network setups work as well.

The default port is **47832**.

## Host a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Host a session**.
4. Enter an IPv4 address the other collaborators can reach.
5. Leave the port at **47832**, or choose another available TCP port.
6. Select **Start Hosting**.
7. Share the generated join code with your collaborators.

When using Radmin VPN, the host address should be the host's Radmin VPN IPv4 address.

## Join a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Join a session**.
4. Paste the host's join code.
5. Select **Connect**.

When someone first joins, UnitySync brings their project and loaded scenes in line with the host before normal collaboration begins. This can involve importing assets or recompiling scripts, so the initial connection may take longer.

## What it syncs

UnitySync synchronizes loaded scene edits and supported project files between connected editors. This includes normal GameObject, Transform, hierarchy, component, and serialized property changes, along with project assets under **Assets** and supported **ProjectSettings** changes.

Connected collaborators also appear in the Scene view with colored viewport indicators and can be spectated from the **Collaborators** list.

## Visual options

Open **UnitySync > Visual Options** to adjust collaborator viewport indicators.

## Security

Session traffic is encrypted and authenticated. Keep join codes private.

UnitySync does not currently provide accounts, permissions, host approval prompts, relay servers, matchmaking, or automatic NAT traversal.

## Limitations

- Direct IPv4 connectivity to the host is required.
- **Packages** are checked by installed name and version, never transferred or changed. Before Assets sync, the guest sees a checklist of missing packages, version differences, and guest-only packages to remove. Make those changes manually, then choose **Recheck packages**; **Copy checklist** keeps the instructions available if Unity must close.
- The host-approved Debug restore replaces **Assets** and **ProjectSettings** only; it preserves **Packages**.
- Collaborators should use compatible Unity versions, packages, and third-party dependencies.
- UnitySync does not automatically open, close, or save scenes.
- There is no merge or conflict-resolution system for simultaneous edits.

## Troubleshooting

If someone cannot connect:

- Confirm they can reach the host's chosen IPv4 address.
- Confirm TCP port **47832** is available, or choose another port.
- If using a VPN, confirm everyone is connected to the same VPN network.

If synchronization appears incomplete, let any initial asset import or script compilation finish, check the **Activity Log** in **UnitySync > Session**, and confirm everyone has the required packages and dependencies installed.

## AI assistance disclosure

The code in this repository was created with assistance from AI tools. I made the architecture, design and implementation decisions; AI was used to write the code based on them.
