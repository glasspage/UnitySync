# UnitySync

UnitySync is an experimental real-time collaboration add-on for the Unity Editor.

Version 0.2 adds the first scene-edit synchronization layer. A host can create an encrypted session, collaborators can join with a compact code, everyone can see one another's Scene view camera, and edits to matching scene objects are replicated in real time.

DISCLAIMER: The code in this repository was created with assistance from AI tools. I made the architecture, design and implementation decisions; AI was used to write the code based on them.

## Install

In Unity, open **Window > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/glasspage/UnitySync.git
```

The current package targets Unity 2021.3 or newer.

## Connect through Radmin VPN

1. Put every collaborator on the same Radmin VPN network.
2. On the host, open **UnitySync > Session**.
3. Set **Host address** to the host's Radmin VPN IPv4 address (normally a `26.x.x.x` address), then select **Start Hosting**.
4. Allow Unity through the private-network firewall prompt if the operating system asks.
5. Share the generated join code privately.
6. Collaborators paste it into **Join code** and select **Connect**.

The host listens on all local interfaces. The address field controls the address embedded in the join code, so it should be the address other collaborators can actually reach.

When a collaborator connects, the host sends its current loaded-scene state to that collaborator. The host's scene is therefore the starting authority for a session.

## What appears in the scene

While connected, UnitySync creates temporary objects under `[UnitySync] > Collaborators`. Every remote user gets a child GameObject named `Username (Viewport)` with an `EditorOnly` tag and a Scene view camera gizmo. The generated hierarchy uses `DontSaveInEditor` and is removed when the session stops.

Open **UnitySync > Visual Options** to change the length of remote viewport direction lines, the opacity of viewport indicators, or the intensity of their adaptive contrast outlines. The main UnitySync window also includes a debug foldout that can summon a customizable test viewport without starting a session.

## Security model

Every hosted session generates a new random 256-bit secret. The join code contains the advertised IPv4 endpoint, port, and this secret. Network messages use AES-256-CBC encryption with a fresh random IV and HMAC-SHA256 authentication (encrypt-then-MAC).

Treat a join code like a temporary password: anyone who has it can connect while the host is running. This first version does not provide account identity, a relay, forward secrecy, host approval prompts, NAT traversal, or protection for a join code sent through an insecure chat.

## Current scope

- Direct host/client TCP networking
- Multiple clients per host
- Encrypted and authenticated messages
- Live username, chosen viewport color, camera position, camera rotation, projection, and Scene view pivot
- Live transforms and GameObject settings for existing scene objects
- Serialized settings for Unity and third-party components, including UdonBehaviours, PhysBones, custom MonoBehaviours, scene references, and asset references
- Component add, remove, and reorder synchronization on existing GameObjects
- Initial scene alignment from the host when a collaborator joins
- Automatic cleanup when peers disconnect or the session stops

Not implemented:

- Creating, deleting, reparenting, or reordering GameObjects
- Prefab asset editing
- Asset or file transfer
- Project settings synchronization
- Relay servers or internet matchmaking
- Conflict resolution beyond last received edit wins, or version history

## Scene matching requirement

This first scene-sync version assumes collaborators open the same scenes with the same non-UnitySync GameObjects in the same hierarchy order. Objects are matched by loaded scene and sibling-index path; names may change without breaking the match. UnitySync's temporary `[UnitySync]` hierarchy is ignored when calculating these paths.

Component settings are synchronized through Unity's generic editor serialization layer rather than a list of supported component types. Both projects must have the same third-party packages and referenced assets installed. Properties Unity does not expose through `SerializedProperty`, and transient runtime-only state, are not synchronized.

## Troubleshooting

- Confirm everyone can ping the host's Radmin VPN address.
- Confirm the host used that same address when generating the join code.
- Allow the Unity Editor through the firewall on private networks.
- Confirm no other program is using TCP port `47832`, or choose another port before hosting.
- Generate a fresh code by stopping and starting the host if a code was shared accidentally.
