# UnitySync

UnitySync is an experimental real-time collaboration add-on for **Unity Editor 2021.3+**. It lets multiple Unity Editors join the same encrypted session, see one another in the Scene view, spectate collaborators, synchronize loaded scene edits, and exchange project asset changes without requiring a central server.

The current package version is **0.4.4**.

> [!WARNING]
> UnitySync can modify loaded scenes, overwrite project files, delete eligible synchronized files, and apply ProjectSettings changes received from collaborators. Use version control or another backup, and only connect to people you trust.

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

Git must be installed on the computer running Unity.

After installation, UnitySync adds its own **UnitySync** menu to the Unity Editor.

## Start a session

Open **UnitySync > Session**.

### Host

1. Set your **Username** and viewport **Color**.
2. Under **Host a session**, enter an IPv4 address the other collaborators can reach.
3. Leave the default port at `47832`, or choose another open TCP port.
4. Select **Start Hosting**.
5. Share the generated join code privately.

The host listens on all local interfaces. The **Host address** field only controls which IPv4 address is embedded in the join code, so it should be the address collaborators can actually reach.

For local-network use, this can be the host's LAN address. For remote collaboration, use a VPN or other network setup that gives collaborators direct IPv4 reachability to the host; Radmin VPN is one example.

### Join

1. Open **UnitySync > Session**.
2. Set your **Username** and viewport **Color**.
3. Expand **Join a session**.
4. Paste the host's join code.
5. Select **Connect**.

When a guest first connects, UnitySync performs an initial synchronization before normal live editing begins:

1. The guest receives a manifest of the host's `Assets` files.
2. Missing or different host files are downloaded and imported locally.
3. Unity may compile scripts or reload assemblies if synchronized files require it; UnitySync attempts to reconnect and continue automatically after the reload.
4. The guest requests the host's loaded-scene hierarchy snapshot.
5. The guest reconciles its loaded scene objects and components to the host snapshot.
6. Normal bidirectional live scene and project synchronization begins.

The host is therefore the starting authority for a session. Initial file synchronization copies missing or different files that exist in the host's `Assets` folder, but it does **not** delete unrelated guest files that are absent from the host manifest.

## What UnitySync synchronizes

### Loaded scenes

UnitySync synchronizes loaded-scene editing through Unity's editor serialization system.

Current scene synchronization includes:

- GameObject creation and deletion
- Names, active state, tags, and layers
- Parenting and sibling order
- Transform position, rotation, and scale
- Component addition, removal, and ordering
- Serialized component properties
- Scene-object references
- Project asset references
- Arrays and other values exposed through `SerializedProperty`
- Scene settings handled by the scene synchronizer

This is intentionally generic rather than being limited to a hard-coded component list, so it can synchronize Unity components and many third-party components, including custom MonoBehaviours, UdonBehaviours, and PhysBones when their state is exposed through Unity serialization.

UnitySync assigns session-only IDs to synchronized GameObjects. It does not add tracking components or persist UnitySync IDs into scene files.

When a guest receives the host's initial scene snapshot, UnitySync can create missing objects, reconcile component layouts, parenting, and ordering, and remove extra non-UnitySync objects from matching loaded scenes after a complete snapshot succeeds. If the snapshot cannot be captured or applied safely, cleanup is skipped rather than blindly deleting unmatched objects.

UnitySync does not coordinate which scenes each collaborator has open, and live scene edits are not automatically saved to disk. Normal Unity scene-saving behavior still applies.

### Scene update frequency

UnitySync avoids continuously resending unchanged serialized state.

- **Transforms:** at most **10 Hz**, with linear interpolation on remote editors
- **Other live scene edits:** at most **once per second**
- **Unchanged state:** skipped using change tracking and serialized-state hashes

This keeps frequently moving objects responsive without treating every serialized property as a high-frequency stream.

### Project files

UnitySync has two project-file synchronization stages.

#### Initial host Assets reconciliation

When a guest connects, the host builds a manifest of its `Assets` folder. The guest compares file sizes and SHA-256 hashes and requests files that are missing or different.

The initial pass includes normal files under `Assets`, including their `.meta` files, and excludes generated `SerializedUdonPrograms` content.

Because this is a full initial `Assets` comparison, synchronized files can include scenes, scripts, assemblies definitions, materials, prefabs, shaders, textures, and other assets stored under `Assets`.

#### Live project synchronization

After the initial sync completes, UnitySync watches both:

- `Assets/`
- `ProjectSettings/`

Eligible file creates, changes, renames, and deletions are propagated through the session. Live project-file updates are bidirectional: changes from a guest are sent through the host to the other connected editors.

To avoid disruptive reload loops and to leave loaded-scene collaboration to the scene synchronizer, live project-file sync currently excludes:

- `.unity`
- `.cs`
- `.dll`
- `.asmdef`
- `.asmref`
- `SerializedUdonPrograms`

Their matching `.meta` paths are excluded as well.

Files outside `Assets/` and `ProjectSettings/` are not live-synchronized. In particular, UnitySync does not synchronize `Packages/`, package dependencies, `UserSettings/`, or arbitrary files elsewhere in the project.

ProjectSettings are watched for live changes, but they are not part of the initial host `Assets` manifest.

## Presence and collaboration tools

While connected, UnitySync shows each remote collaborator in the Scene view.

### Viewports

Remote Scene view cameras are represented by temporary viewport indicators under:

```text
[UnitySync]
└── Collaborators
    └── Username (Viewport)
```

These objects are editor-only, are not saved into the scene, and are removed when the session ends.

Viewport presence includes:

- Username
- Per-user color
- Scene view camera position and rotation
- Perspective or orthographic projection
- Scene view pivot and size
- Linear transform interpolation
- Adaptive contrast outlines for viewport wireframes and names
- A forward direction line

Viewport state is sent at most 10 times per second and is not resent when it has not changed.

### Spectating

The **Collaborators** list in the Session window includes a **Spectate** control for remote users.

Spectating drives your Scene view from the selected collaborator's synchronized viewport state. UnitySync also displays short Scene view status text when you are spectating someone or when another collaborator is spectating you.

### Remote selections

UnitySync synchronizes collaborator selections and can draw colored outlines around remotely selected GameObjects.

Remote selection outlines can be disabled in **UnitySync > Visual Options**.

### Visual options

Open **UnitySync > Visual Options** to configure:

- **Direction Line:** Off, Short, or Long
  - Short: **1.25 m** and the default
  - Long: **3 m**
- **Viewport Opacity**
- **Contrast Intensity**
- **Selection Outlines**

The direction line uses reduced opacity relative to the viewport wireframe and uses the same adaptive outline system for visibility against different scene backgrounds.

The Session window also contains a debug foldout that can summon a customizable test viewport without starting a session.

## Networking and security

UnitySync currently uses direct host/client TCP networking over IPv4.

Each hosted session generates a new random **256-bit master secret**. The join code contains:

- The host's advertised IPv4 address
- The TCP port
- The session secret

Messages are encrypted with **AES-256-CBC** using a fresh random IV. Separate encryption and authentication keys are derived from the session secret, and messages are authenticated with **HMAC-SHA256** before being accepted.

Treat the join code like a temporary password. Anyone who has a valid code and can reach the host can attempt to join while that session is running.

UnitySync does not currently provide:

- Account-backed identity
- Per-user permissions
- Host approval prompts
- Relay servers
- Internet matchmaking
- NAT traversal
- Forward secrecy
- Protection for a join code sent through an insecure channel

Encryption protects UnitySync traffic in transit; it does not make an untrusted collaborator safe. Connected users can send scene and eligible project-file changes, so only collaborate with people you trust.

## Authority and conflicts

UnitySync uses a simple collaboration model:

- The **host** is authoritative for the initial `Assets` reconciliation.
- The **host** is authoritative for the initial loaded-scene snapshot.
- After startup synchronization, live scene and eligible project-file edits are collaborative and can originate from connected guests as well as the host.

There is currently no object locking, merge UI, transaction history, or version-control replacement. If multiple users edit the same state at the same time, the last update applied will generally win.

For serious project work, keep the project under normal version control in addition to using UnitySync.

## Current limitations

UnitySync is still experimental. Important current limitations include:

- Direct IPv4 connectivity is required.
- There is no relay or matchmaking service.
- `Packages/` and package dependencies are not synchronized.
- Collaborators should use compatible Unity/package/plugin versions for serialized third-party components.
- Live code and assembly-file synchronization is intentionally excluded after the initial `Assets` reconciliation.
- UnitySync synchronizes loaded scene contents, but it does not coordinate scene opening/closing or automatically save edited scenes.
- Prefab and other asset-file edits synchronize as project files after they are written to disk; there is no separate collaborative Prefab Mode protocol.
- There is no conflict-resolution or revision-history system beyond normal project/version-control tools.
- Generated `SerializedUdonPrograms` content is intentionally excluded.

## Troubleshooting

If a collaborator cannot connect:

- Confirm they can reach the host's advertised IPv4 address.
- Confirm the host generated the join code using that same reachable address.
- Allow the Unity Editor through the operating system firewall on the relevant network.
- Confirm TCP port `47832` is available, or configure another port before hosting.
- If using a VPN, confirm both users are on the same VPN network and direct peer traffic is allowed.
- Stop and restart hosting to generate a fresh join code if the old code was shared accidentally.

If synchronization appears incomplete:

- Open the **Activity Log** foldout in **UnitySync > Session**.
- Let the initial asset import and any script compilation finish before editing.
- Confirm required packages and third-party dependencies are installed in each project.
- Remember that live `.unity`, `.cs`, `.dll`, `.asmdef`, and `.asmref` file changes are intentionally excluded.
- Keep a version-control checkout or backup available while testing experimental synchronization behavior.

## AI assistance disclosure

The code in this repository was created with assistance from AI tools. The project architecture, design, and implementation decisions are made by the project author; AI tools are used to help write code based on those decisions.
