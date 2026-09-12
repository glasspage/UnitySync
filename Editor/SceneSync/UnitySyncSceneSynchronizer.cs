using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncSceneSynchronizer
    {
        private enum PendingKind
        {
            GameObject,
            Component,
            Structure
        }

        private sealed class PendingChange
        {
            internal int GameObjectInstanceId;
            internal int ComponentIndex;
            internal PendingKind Kind;
        }

        private sealed class SnapshotJob
        {
            internal Guid TargetPlayerId;
            internal List<GameObject> Objects;
            internal int Index;
        }

        private const double FlushIntervalSeconds = 0.05;
        private const int MaximumChangesPerUpdate = 64;
        private const int SnapshotObjectsPerUpdate = 8;

        private static readonly Dictionary<string, PendingChange> Pending =
            new Dictionary<string, PendingChange>();
        private static readonly Dictionary<string, string> KnownHashes =
            new Dictionary<string, string>();
        private static readonly Queue<SnapshotJob> SnapshotJobs = new Queue<SnapshotJob>();

        private static bool _active;
        private static bool _applyingRemoteChange;
        private static double _nextFlushTime;

        static UnitySyncSceneSynchronizer()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        internal static void BeginSession()
        {
            _active = true;
            _nextFlushTime = 0d;
            Pending.Clear();
            KnownHashes.Clear();
            SnapshotJobs.Clear();
        }

        internal static void EndSession()
        {
            _active = false;
            Pending.Clear();
            KnownHashes.Clear();
            SnapshotJobs.Clear();
        }

        internal static void QueueFullSceneSnapshot(Guid targetPlayerId)
        {
            if (!_active)
            {
                return;
            }

            SnapshotJobs.Enqueue(new SnapshotJob
            {
                TargetPlayerId = targetPlayerId,
                Objects = UnitySyncSceneSerializer.GetAllSceneObjects(),
                Index = 0
            });
        }

        internal static void Flush(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (!_active || transport == null || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextFlushTime)
            {
                return;
            }

            _nextFlushTime = now + FlushIntervalSeconds;
            FlushPendingChanges(transport, localPlayerId);
            FlushSnapshot(transport, localPlayerId);
        }

        internal static bool ApplyRemoteChange(UnitySyncSceneObjectChange change, out string error)
        {
            try
            {
                if (TryCaptureLocalState(change, out UnitySyncSceneObjectChange localState) &&
                    GetHash(localState) == GetHash(change))
                {
                    Remember(change);
                    error = string.Empty;
                    return true;
                }
            }
            catch (Exception)
            {
                // Fall through and let the normal apply path report a useful error if needed.
            }

            _applyingRemoteChange = true;
            try
            {
                if (!UnitySyncSceneSerializer.Apply(change, out error))
                {
                    return false;
                }

                Remember(change);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                _applyingRemoteChange = false;
            }
        }

        private static bool TryCaptureLocalState(
            UnitySyncSceneObjectChange incoming,
            out UnitySyncSceneObjectChange local)
        {
            local = null;
            if (incoming == null || incoming.Address == null)
            {
                return false;
            }

            GameObject gameObject = UnitySyncSceneSerializer.ResolveAddress(incoming.Address);
            if (gameObject == null)
            {
                return false;
            }

            if (incoming.ReconcileComponents)
            {
                if (!UnitySyncSceneSerializer.TryCaptureFullObject(gameObject, out local))
                {
                    return false;
                }

                local.Address = incoming.Address;
                return true;
            }

            local = new UnitySyncSceneObjectChange
            {
                Address = incoming.Address
            };

            if (incoming.GameObject != null)
            {
                if (!UnitySyncSceneSerializer.TryCaptureGameObject(
                        gameObject,
                        out UnitySyncSceneObjectChange gameObjectState))
                {
                    return false;
                }

                local.GameObject = gameObjectState.GameObject;
            }

            UnitySyncComponentState[] incomingComponents =
                incoming.Components ?? new UnitySyncComponentState[0];
            local.Components = new UnitySyncComponentState[incomingComponents.Length];
            Component[] components = gameObject.GetComponents<Component>();
            for (int index = 0; index < incomingComponents.Length; index++)
            {
                int componentIndex = incomingComponents[index].ComponentIndex;
                if (componentIndex < 0 ||
                    componentIndex >= components.Length ||
                    components[componentIndex] == null ||
                    !UnitySyncSceneSerializer.TryCaptureComponent(
                        components[componentIndex],
                        out UnitySyncSceneObjectChange componentState))
                {
                    return false;
                }

                local.Components[index] = componentState.Components[0];
            }

            return true;
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!_active || _applyingRemoteChange || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            for (int eventIndex = 0; eventIndex < stream.length; eventIndex++)
            {
                switch (stream.GetEventType(eventIndex))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(
                            eventIndex,
                            out ChangeGameObjectOrComponentPropertiesEventArgs propertiesEvent);
                        MarkPropertiesChanged(EditorUtility.InstanceIDToObject(propertiesEvent.instanceId));
                        break;

                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(
                            eventIndex,
                            out ChangeGameObjectStructureEventArgs structureEvent);
                        MarkStructureChanged(
                            EditorUtility.InstanceIDToObject(structureEvent.instanceId) as GameObject);
                        break;

                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(
                            eventIndex,
                            out ChangeGameObjectStructureHierarchyEventArgs hierarchyEvent);
                        MarkHierarchyStructureChanged(
                            EditorUtility.InstanceIDToObject(hierarchyEvent.instanceId) as GameObject);
                        break;
                }
            }
        }

        private static void MarkPropertiesChanged(Object changedObject)
        {
            if (changedObject is GameObject gameObject)
            {
                AddPending(gameObject, PendingKind.GameObject, -1);
                return;
            }

            if (!(changedObject is Component component) || component == null)
            {
                return;
            }

            Component[] components = component.gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] == component)
                {
                    AddPending(component.gameObject, PendingKind.Component, index);
                    return;
                }
            }
        }

        private static void MarkStructureChanged(GameObject gameObject)
        {
            AddPending(gameObject, PendingKind.Structure, -1);
        }

        private static void MarkHierarchyStructureChanged(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            MarkStructureChanged(gameObject);
            for (int childIndex = 0; childIndex < gameObject.transform.childCount; childIndex++)
            {
                MarkHierarchyStructureChanged(gameObject.transform.GetChild(childIndex).gameObject);
            }
        }

        private static void AddPending(GameObject gameObject, PendingKind kind, int componentIndex)
        {
            if (gameObject == null ||
                !UnitySyncSceneSerializer.TryCreateAddress(gameObject, out UnitySyncSceneObjectAddress address))
            {
                return;
            }

            int instanceId = gameObject.GetInstanceID();
            string structureKey = instanceId + ":s";
            if (kind != PendingKind.Structure && Pending.ContainsKey(structureKey))
            {
                return;
            }

            if (kind == PendingKind.Structure)
            {
                List<string> obsoleteKeys = new List<string>();
                foreach (string key in Pending.Keys)
                {
                    if (key.StartsWith(instanceId + ":", StringComparison.Ordinal))
                    {
                        obsoleteKeys.Add(key);
                    }
                }

                foreach (string key in obsoleteKeys)
                {
                    Pending.Remove(key);
                }
            }

            string pendingKey = kind == PendingKind.GameObject
                ? instanceId + ":g"
                : kind == PendingKind.Structure
                    ? structureKey
                    : instanceId + ":c:" + componentIndex;
            Pending[pendingKey] = new PendingChange
            {
                GameObjectInstanceId = instanceId,
                ComponentIndex = componentIndex,
                Kind = kind
            };
        }

        private static void FlushPendingChanges(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            List<string> keys = new List<string>(Pending.Keys);
            int count = Mathf.Min(MaximumChangesPerUpdate, keys.Count);
            for (int index = 0; index < count; index++)
            {
                string pendingKey = keys[index];
                if (!Pending.TryGetValue(pendingKey, out PendingChange pending))
                {
                    continue;
                }

                Pending.Remove(pendingKey);
                GameObject gameObject = EditorUtility.InstanceIDToObject(pending.GameObjectInstanceId) as GameObject;
                if (!TryCapture(pending, gameObject, out UnitySyncSceneObjectChange change))
                {
                    continue;
                }

                string stateKey = GetStateKey(change);
                if (TryGetHash(change, out string hash) &&
                    KnownHashes.TryGetValue(stateKey, out string knownHash) &&
                    knownHash == hash)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(hash))
                {
                    KnownHashes[stateKey] = hash;
                }

                transport.SendSceneObjectChange(localPlayerId, change);
            }
        }

        private static bool TryCapture(
            PendingChange pending,
            GameObject gameObject,
            out UnitySyncSceneObjectChange change)
        {
            change = null;
            if (gameObject == null)
            {
                return false;
            }

            switch (pending.Kind)
            {
                case PendingKind.GameObject:
                    return UnitySyncSceneSerializer.TryCaptureGameObject(gameObject, out change);

                case PendingKind.Structure:
                    return UnitySyncSceneSerializer.TryCaptureFullObject(gameObject, out change);

                case PendingKind.Component:
                    Component[] components = gameObject.GetComponents<Component>();
                    return pending.ComponentIndex >= 0 &&
                           pending.ComponentIndex < components.Length &&
                           components[pending.ComponentIndex] != null &&
                           UnitySyncSceneSerializer.TryCaptureComponent(
                               components[pending.ComponentIndex],
                               out change);

                default:
                    return false;
            }
        }

        private static void FlushSnapshot(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (SnapshotJobs.Count == 0)
            {
                return;
            }

            SnapshotJob job = SnapshotJobs.Peek();
            int sent = 0;
            while (job.Index < job.Objects.Count && sent < SnapshotObjectsPerUpdate)
            {
                GameObject gameObject = job.Objects[job.Index++];
                if (gameObject != null &&
                    UnitySyncSceneSerializer.TryCaptureFullObject(
                        gameObject,
                        out UnitySyncSceneObjectChange change))
                {
                    transport.SendSceneObjectChange(localPlayerId, change, job.TargetPlayerId);
                    sent++;
                }
            }

            if (job.Index >= job.Objects.Count)
            {
                SnapshotJobs.Dequeue();
            }
        }

        private static void Remember(UnitySyncSceneObjectChange change)
        {
            if (change == null || change.Address == null)
            {
                return;
            }

            KnownHashes[GetStateKey(change)] = GetHash(change);

            if (change.ReconcileComponents)
            {
                UnitySyncSceneObjectChange gameObjectOnly = new UnitySyncSceneObjectChange
                {
                    Address = change.Address,
                    GameObject = change.GameObject
                };
                if (change.GameObject != null)
                {
                    KnownHashes[GetStateKey(gameObjectOnly)] = GetHash(gameObjectOnly);
                }

                foreach (UnitySyncComponentState component in change.Components)
                {
                    UnitySyncSceneObjectChange componentOnly = new UnitySyncSceneObjectChange
                    {
                        Address = change.Address,
                        Components = new[] { component }
                    };
                    KnownHashes[GetStateKey(componentOnly)] = GetHash(componentOnly);
                }
            }
        }

        private static string GetStateKey(UnitySyncSceneObjectChange change)
        {
            if (change.ReconcileComponents)
            {
                return change.Address.Key + "|s";
            }

            if (change.GameObject != null)
            {
                return change.Address.Key + "|g";
            }

            int componentIndex = change.Components != null && change.Components.Length > 0
                ? change.Components[0].ComponentIndex
                : -1;
            return change.Address.Key + "|c:" + componentIndex;
        }

        private static string GetHash(UnitySyncSceneObjectChange change)
        {
            byte[] payload = UnitySyncProtocol.CreateSceneObjectChange(Guid.Empty, change);
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(payload));
            }
        }

        private static bool TryGetHash(UnitySyncSceneObjectChange change, out string hash)
        {
            try
            {
                hash = GetHash(change);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is InvalidDataException ||
                exception is OverflowException)
            {
                hash = string.Empty;
                return false;
            }
        }
    }
}
