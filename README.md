# UnitySync

UnitySync is an experimental real-time collaboration add-on for **Unity Editor 2021.3+**. It lets multiple people work in the same Unity project together with live scene editing, project file syncing, shared Scene view presence, spectating, and selection indicators.

> [!WARNING]
> UnitySync can modify scenes and project files on connected computers. Use version control or another backup, and only connect to people you trust.

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

Git must be installed on the computer running Unity.

After installation, UnitySync adds a **UnitySync** menu to the Unity Editor.

## Network setup

UnitySync currently connects collaborators directly to the host over IPv4.

Everyone must be able to reach the host's computer. You can use:

- The same local network
- A VPN such as Radmin VPN
- Another network setup that provides direct IPv4 connectivity

The default UnitySync port is **47832**. Make sure Unity is allowed through the host's firewall and that the selected port is not being blocked or used by another application.

## Host a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Host a session**.
4. Enter an IPv4 address the other collaborators can reach.
5. Leave the port at **47832**, or choose another available TCP port.
6. Select **Start Hosting**.
7. Share the generated join code privately with your collaborators.

The **Host address** should be the address other users will connect to. For example, when using Radmin VPN, enter the host's Radmin VPN IPv4 address.

## Join a session

1. Open **UnitySync > Session**.
2. Set your **Username** and **Color**.
3. Expand **Join a session**.
4. Paste the host's join code.
5. Select **Connect**.

When joining, UnitySync first brings the guest project in line with the host before normal collaboration begins. This may involve importing assets or recompiling scripts, so the first connection can take longer than later live edits.

The host's project and loaded scenes are used as the starting state for newly connected collaborators.

## Features

### Live scene editing

Edits made to loaded scenes are synchronized between connected editors, including common GameObject, Transform, hierarchy, component, and serialized property changes.

UnitySync is designed to work with normal Unity components as well as many third-party components that use Unity's standard serialization.

### Project file syncing

UnitySync can synchronize project assets between collaborators.

When someone first joins, required files from the host's **Assets** folder are copied to the guest as needed. During the session, supported changes under **Assets** and **ProjectSettings** can also be shared between collaborators.

Some files that would cause disruptive Unity reloads are intentionally excluded from live syncing.

### Collaborator viewports

Connected users appear in the Scene view with colored viewport indicators showing where they are looking.

Each collaborator has:

- A username
- A custom color
- A viewport wireframe
- A direction indicator

UnitySync creates these as temporary editor-only objects under:

```text
[UnitySync]
└── Collaborators
    └── Username (Viewport)
```

They are not saved into the scene and are removed when the session ends.

### Spectating

The **Collaborators** section in **UnitySync > Session** lets you spectate another user's Scene view.

While spectating, your Scene view follows theirs. UnitySync also shows a small status when you are spectating someone or when someone is spectating you.

### Selection indicators

UnitySync can show colored outlines around GameObjects selected by other collaborators, making it easier to see what everyone is working on.

### Visual options

Open **UnitySync > Visual Options** to configure collaborator indicators.

Available options include:

- **Direction Line:** Off, Short, or Long
- **Viewport Opacity**
- **Contrast Intensity**
- **Selection Outlines**

The default direction line is **Short** at **1.25 m**. **Long** is **3 m**.

## Security

Session traffic is encrypted and authenticated.

Treat the join code like a temporary password. Anyone who has the code and can reach the host may be able to join the session.

UnitySync currently does not include accounts, host approval prompts, permissions, relay servers, matchmaking, or automatic internet/NAT traversal.

## Important limitations

UnitySync is still experimental.

- Direct IPv4 connectivity to the host is required.
- UnitySync is not a replacement for Git or other version control.
- There is no merge or conflict-resolution system for simultaneous edits.
- Collaborators should use compatible Unity versions, packages, and third-party dependencies.
- UnitySync does not automatically open, close, or save scenes for collaborators.
- The **Packages** folder and package dependencies are not synchronized.
- Some project files are intentionally excluded from live syncing to avoid disruptive reloads.

## Troubleshooting

If someone cannot connect:

- Confirm everyone can reach the host's chosen IPv4 address.
- Confirm the host used that same address when creating the session.
- Allow the Unity Editor through the firewall.
- Confirm TCP port **47832** is available, or choose another port.
- If using a VPN, confirm everyone is connected to the same VPN network.
- Stop and restart hosting to generate a new join code if necessary.

If synchronization appears incomplete:

- Open the **Activity Log** in **UnitySync > Session**.
- Let any initial asset import or script compilation finish.
- Confirm everyone has the required Unity packages and dependencies installed.
- Check version control if you need to restore a project file or scene.

## AI assistance disclosure

The code in this repository was created with assistance from AI tools. The project architecture, design, and implementation decisions are made by the project author; AI tools are used to help write code based on those decisions.
