using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncProjectSynchronizer
    {
        private sealed class PendingDirtyAsset
        {
            internal Object Asset;
            internal double DueTime;
        }

        private readonly struct FileFingerprint
        {
            internal readonly long Length;
            internal readonly ulong Hash;

            internal FileFingerprint(long length, ulong hash)
            {
                Length = length;
                Hash = hash;
            }
        }

        private sealed class RemoteTransfer
        {
            internal Guid PlayerId;
            internal Guid SyncId;
            internal string Path;
            internal long Length;
            internal ulong Hash;
            internal string TempPath;
            internal FileStream Stream;
            internal long Received;
        }

        private const double ChangeDebounceSeconds = 0.2d;
        private const double DirtyAssetSaveDelaySeconds = 0.05d;
        private const double MaterialSyncDelaySeconds = 1.0d;
        private const double DirtyMaterialScanIntervalSeconds = 0.25d;
        private const double LoadedMaterialDiscoveryIntervalSeconds = 2.0d;
        private const double DirtyAssetWorkBudgetSeconds = 0.002d;
        private const int MaximumMaterialsPerUpdate = 32;
        private const int MaximumDirtyAssetSavesPerUpdate = 4;
        private const double RemoteEchoSuppressionSeconds = 2.0d;
        private const double HashCacheSaveIntervalSeconds = 5.0d;

        private static readonly ProfilerMarker MaterialScanMarker =
            new ProfilerMarker("UnitySync.ScanDirtyMaterials");
        private static readonly ProfilerMarker MaterialDiscoveryMarker =
            new ProfilerMarker("UnitySync.DiscoverLoadedMaterials");
        private static readonly ProfilerMarker DirtyAssetSaveMarker =
            new ProfilerMarker("UnitySync.SaveDirtyAssets");
        private static readonly object PendingLock = new object();
        private static readonly Dictionary<string, double> PendingLocalChanges =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> SuppressedUntil =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FileFingerprint> KnownFiles =
            new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        private static readonly Dictionary<int, PendingDirtyAsset> PendingDirtyAssets =
            new Dictionary<int, PendingDirtyAsset>();
        private static readonly Dictionary<int, string> MaterialEditFingerprints =
            new Dictionary<int, string>();
        private static readonly Dictionary<int, int> MaterialDirtyCounts =
            new Dictionary<int, int>();
        private static readonly Dictionary<Guid, RemoteTransfer> RemoteTransfers =
            new Dictionary<Guid, RemoteTransfer>();

        private static bool _active;
        private static string _projectRoot = string.Empty;
        private static FileSystemWatcher _assetsWatcher;
        private static FileSystemWatcher _projectSettingsWatcher;
        private static double _nextDirtyMaterialScanTime;
        private static double _nextLoadedMaterialDiscoveryTime;
        private static double _nextHashCacheSaveTime;
        private static Material[] _loadedMaterials = new Material[0];
        private static int _dirtyMaterialScanIndex;

        internal static void BeginSession()
        {
            EndSession();

            _projectRoot = GetProjectRoot();
            _active = true;
            _nextDirtyMaterialScanTime = 0d;
            _nextHashCacheSaveTime =
                GetMonotonicSeconds() + HashCacheSaveIntervalSeconds;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            StartWatcher(
                Path.Combine(_projectRoot, "Assets"),
                out _assetsWatcher);
            StartWatcher(
                Path.Combine(_projectRoot, "ProjectSettings"),
                out _projectSettingsWatcher);
        }

        internal static void EndSession()
        {
            _active = false;
            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            DisposeWatcher(ref _assetsWatcher);
            DisposeWatcher(ref _projectSettingsWatcher);
            UnitySyncFileHashCache.SaveIfDirty(_projectRoot);

            lock (PendingLock)
            {
                PendingLocalChanges.Clear();
                SuppressedUntil.Clear();
            }

            KnownFiles.Clear();
            PendingDirtyAssets.Clear();
            MaterialEditFingerprints.Clear();
            MaterialDirtyCounts.Clear();
            _loadedMaterials = new Material[0];
            _dirtyMaterialScanIndex = 0;
            _nextLoadedMaterialDiscoveryTime = 0d;
            _nextDirtyMaterialScanTime = 0d;
            _nextHashCacheSaveTime = 0d;
            foreach (RemoteTransfer transfer in RemoteTransfers.Values)
            {
                CleanupTransfer(transfer);
            }

            RemoteTransfers.Clear();
            _projectRoot = string.Empty;
        }

        internal static void Update(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (!_active || transport == null || EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                return;
            }

            double now = GetMonotonicSeconds();
            if (now >= _nextHashCacheSaveTime)
            {
                UnitySyncFileHashCache.SaveIfDirty(_projectRoot);
                _nextHashCacheSaveTime = now + HashCacheSaveIntervalSeconds;
            }

            using (MaterialScanMarker.Auto())
            {
                ScanLoadedDirtyMaterials(now);
            }

            using (DirtyAssetSaveMarker.Auto())
            {
                FlushDirtyAssets(now);
            }

            List<string> ready = new List<string>();
            lock (PendingLock)
            {
                foreach (KeyValuePair<string, double> pair in PendingLocalChanges)
                {
                    if (pair.Value <= now)
                    {
                        ready.Add(pair.Key);
                    }
                }

                foreach (string path in ready)
                {
                    PendingLocalChanges.Remove(path);
                }

                List<string> expiredSuppressions = null;
                foreach (KeyValuePair<string, double> pair in SuppressedUntil)
                {
                    if (pair.Value <= now)
                    {
                        if (expiredSuppressions == null)
                        {
                            expiredSuppressions = new List<string>();
                        }

                        expiredSuppressions.Add(pair.Key);
                    }
                }

                if (expiredSuppressions != null)
                {
                    foreach (string path in expiredSuppressions)
                    {
                        SuppressedUntil.Remove(path);
                    }
                }
            }

            ready.Sort(StringComparer.Ordinal);
            foreach (string path in ready)
            {
                if (IsSuppressed(path, now))
                {
                    continue;
                }

                TrySendLocalChange(transport, localPlayerId, path);
            }
        }

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] modifications)
        {
            if (!_active || modifications == null)
            {
                return modifications;
            }

            double now = GetMonotonicSeconds();
            foreach (UndoPropertyModification modification in modifications)
            {
                PropertyModification current = modification.currentValue;
                PropertyModification previous = modification.previousValue;
                Object target = current != null && current.target != null
                    ? current.target
                    : previous != null
                        ? previous.target
                        : null;
                if (target == null)
                {
                    continue;
                }

                if (target is RenderSettings)
                {
                    UnitySyncSceneSynchronizer.MarkSceneSettingsChanged();
                    continue;
                }

                double delay = target is Material
                    ? MaterialSyncDelaySeconds
                    : DirtyAssetSaveDelaySeconds;
                QueueDirtyAsset(target, now + delay);
            }

            return modifications;
        }

        private static void OnUndoRedoPerformed()
        {
            if (!_active)
            {
                return;
            }

            UnitySyncSceneSynchronizer.MarkSceneSettingsChanged();
            MaterialDirtyCounts.Clear();
            _nextDirtyMaterialScanTime = 0d;
        }

        private static void ScanLoadedDirtyMaterials(double now)
        {
            if (now < _nextDirtyMaterialScanTime)
            {
                return;
            }

            // Discovery allocates an array of every loaded material. Reuse that array
            // across scans, and never restart an unfinished pass in a large project.
            if (_dirtyMaterialScanIndex == 0 && now >= _nextLoadedMaterialDiscoveryTime)
            {
                using (MaterialDiscoveryMarker.Auto())
                {
                    _loadedMaterials = Resources.FindObjectsOfTypeAll<Material>();
                }
                _nextLoadedMaterialDiscoveryTime = now + LoadedMaterialDiscoveryIntervalSeconds;
                HashSet<int> loadedIds = new HashSet<int>();
                foreach (Material material in _loadedMaterials)
                {
                    if (material != null)
                    {
                        loadedIds.Add(material.GetInstanceID());
                    }
                }

                List<int> stale = new List<int>();
                foreach (int instanceId in MaterialDirtyCounts.Keys)
                {
                    if (!loadedIds.Contains(instanceId))
                    {
                        stale.Add(instanceId);
                    }
                }

                foreach (int instanceId in stale)
                {
                    MaterialDirtyCounts.Remove(instanceId);
                    MaterialEditFingerprints.Remove(instanceId);
                }

                // Discovery is one indivisible Unity call; yield before serialization.
                return;
            }

            double started = GetMonotonicSeconds();
            int processed = 0;
            while (_dirtyMaterialScanIndex < _loadedMaterials.Length &&
                   processed < MaximumMaterialsPerUpdate &&
                   (processed == 0 || GetMonotonicSeconds() - started < DirtyAssetWorkBudgetSeconds))
            {
                Material material = _loadedMaterials[_dirtyMaterialScanIndex++];
                processed++;
                if (material == null || !EditorUtility.IsPersistent(material))
                {
                    continue;
                }

                int instanceId = material.GetInstanceID();
                if (!EditorUtility.IsDirty(material))
                {
                    MaterialDirtyCounts.Remove(instanceId);
                    MaterialEditFingerprints.Remove(instanceId);
                    continue;
                }

                int dirtyCount = EditorUtility.GetDirtyCount(material);
                if (MaterialDirtyCounts.TryGetValue(instanceId, out int previousCount) &&
                    previousCount == dirtyCount)
                {
                    continue;
                }

                MaterialDirtyCounts[instanceId] = dirtyCount;
                string path = AssetDatabase.GetAssetPath(material) ?? string.Empty;
                if (!IsLiveSyncPath(path))
                {
                    continue;
                }

                string fingerprint = GetMaterialEditFingerprint(material);
                if (MaterialEditFingerprints.TryGetValue(instanceId, out string previous) &&
                    string.Equals(previous, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                MaterialEditFingerprints[instanceId] = fingerprint;
                QueueDirtyAsset(material, now + MaterialSyncDelaySeconds);
            }

            if (_dirtyMaterialScanIndex >= _loadedMaterials.Length)
            {
                _dirtyMaterialScanIndex = 0;
                _nextDirtyMaterialScanTime = now + DirtyMaterialScanIntervalSeconds;
            }
        }

        private static string GetMaterialEditFingerprint(Material material)
        {
            string json = EditorJsonUtility.ToJson(material, false);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json ?? string.Empty);
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
        }

        private static void QueueDirtyAsset(Object asset, double dueTime)
        {
            if (asset == null || !EditorUtility.IsPersistent(asset))
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(asset) ?? string.Empty;
            if (!IsLiveSyncPath(path))
            {
                return;
            }

            int instanceId = asset.GetInstanceID();
            if (PendingDirtyAssets.TryGetValue(instanceId, out PendingDirtyAsset pending))
            {
                pending.Asset = asset;
                pending.DueTime = dueTime;
                return;
            }

            PendingDirtyAssets.Add(
                instanceId,
                new PendingDirtyAsset
                {
                    Asset = asset,
                    DueTime = dueTime
                });
        }

        private static void FlushDirtyAssets(double now)
        {
            if (PendingDirtyAssets.Count == 0)
            {
                return;
            }

            List<int> ready = new List<int>();
            foreach (KeyValuePair<int, PendingDirtyAsset> pair in PendingDirtyAssets)
            {
                if (pair.Value.DueTime <= now)
                {
                    ready.Add(pair.Key);
                }
            }

            double started = GetMonotonicSeconds();
            int processed = 0;
            foreach (int instanceId in ready)
            {
                // Saving one asset can itself exceed the budget. Always make progress,
                // but leave the rest queued for later editor updates.
                if (processed >= MaximumDirtyAssetSavesPerUpdate ||
                    (processed > 0 && GetMonotonicSeconds() - started >= DirtyAssetWorkBudgetSeconds))
                {
                    break;
                }

                processed++;
                if (!PendingDirtyAssets.TryGetValue(instanceId, out PendingDirtyAsset pending))
                {
                    continue;
                }

                PendingDirtyAssets.Remove(instanceId);
                Object asset = pending.Asset;
                if (asset == null || !EditorUtility.IsPersistent(asset))
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(asset) ?? string.Empty;
                if (!IsLiveSyncPath(path))
                {
                    continue;
                }

                if (EditorUtility.IsDirty(asset))
                {
                    AssetDatabase.SaveAssetIfDirty(asset);
                }

                // Saving resets Unity's dirty counter. Clear our baseline now, since
                // another edit may reach the same count before the next material scan.
                MaterialDirtyCounts.Remove(instanceId);
                MaterialEditFingerprints.Remove(instanceId);
                QueueAssetPath(path);
            }
        }

        private static void QueueAssetPath(string path)
        {
            if (!_active || !IsLiveSyncPath(path))
            {
                return;
            }

            lock (PendingLock)
            {
                // The dirty-asset timer already debounced this edit. Queue it immediately so
                // Material synchronization starts as soon as the 1.5-second quiet period ends.
                PendingLocalChanges[path.Replace('\\', '/')] = GetMonotonicSeconds();
            }
        }

        internal static bool EnsureSceneAssetReferencesQueued(
            UnitySyncTransport transport,
            Guid localPlayerId,
            UnitySyncSceneObjectChange change)
        {
            if (!_active || transport == null || change == null)
            {
                return true;
            }

            HashSet<string> referencedPaths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UnitySyncComponentState component in
                     change.Components ?? new UnitySyncComponentState[0])
            {
                if (component == null)
                {
                    continue;
                }

                foreach (UnitySyncSerializedPropertyState property in
                         component.Properties ?? new UnitySyncSerializedPropertyState[0])
                {
                    UnitySyncObjectReferenceState reference =
                        property != null ? property.ObjectReference : null;
                    if (reference == null ||
                        reference.Kind != UnitySyncObjectReferenceKind.Asset ||
                        string.IsNullOrEmpty(reference.AssetPath))
                    {
                        continue;
                    }

                    string assetPath = reference.AssetPath.Replace('\\', '/');
                    if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        referencedPaths.Add(assetPath);
                    }
                }
            }

            foreach (string assetPath in referencedPaths)
            {
                Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset != null && EditorUtility.IsDirty(asset))
                {
                    AssetDatabase.SaveAssetIfDirty(asset);
                    UnitySyncFileHashCache.Invalidate(_projectRoot, assetPath);
                }

                if (!TryGetFullProjectPath(assetPath, out string fullAssetPath) ||
                    !File.Exists(fullAssetPath))
                {
                    return false;
                }

                string metaPath = assetPath + ".meta";
                if (!TryGetFullProjectPath(metaPath, out string fullMetaPath) ||
                    !File.Exists(fullMetaPath))
                {
                    return false;
                }

                // Queue the source meta and asset on the same transport before the scene
                // packet. QueueMessage is FIFO across these non-coalesced project-file
                // messages, so the receiver can establish/import the asset before it sees
                // the component reference.
                TrySendLocalChange(transport, localPlayerId, assetPath);

                if (ProjectFileNeedsSend(metaPath, fullMetaPath) ||
                    ProjectFileNeedsSend(assetPath, fullAssetPath))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool HandleMessage(
            UnitySyncMessageType messageType,
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            switch (messageType)
            {
                case UnitySyncMessageType.ProjectFileBegin:
                    return BeginRemoteTransfer(playerId, message, out error);

                case UnitySyncMessageType.ProjectFileChunk:
                    return HandleRemoteChunk(playerId, message, out error);

                case UnitySyncMessageType.ProjectFileDelete:
                    return ApplyRemoteDelete(message, out error);

                default:
                    error = "Unsupported live project sync message.";
                    return false;
            }
        }

        private static void StartWatcher(string root, out FileSystemWatcher watcher)
        {
            watcher = null;
            if (!Directory.Exists(root))
            {
                return;
            }

            FileSystemWatcher created = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };

            created.Changed += OnFileChanged;
            created.Created += OnFileChanged;
            created.Deleted += OnFileChanged;
            created.Renamed += OnFileRenamed;
            created.EnableRaisingEvents = true;
            watcher = created;
        }

        private static void DisposeWatcher(ref FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Deleted -= OnFileChanged;
            watcher.Renamed -= OnFileRenamed;
            watcher.Dispose();
            watcher = null;
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs args)
        {
            QueuePath(args.FullPath);
        }

        private static void OnFileRenamed(object sender, RenamedEventArgs args)
        {
            if (Directory.Exists(args.FullPath))
            {
                foreach (string movedFile in Directory.GetFiles(
                             args.FullPath,
                             "*",
                             SearchOption.AllDirectories))
                {
                    string suffix = movedFile.Substring(args.FullPath.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    QueuePath(Path.Combine(args.OldFullPath, suffix));
                    QueuePath(movedFile);
                }

                return;
            }

            QueuePath(args.OldFullPath);
            QueuePath(args.FullPath);
        }

        private static void QueuePath(string fullPath)
        {
            if (!_active ||
                !TryGetProjectRelativePath(fullPath, out string path) ||
                !IsLiveSyncPath(path))
            {
                return;
            }

            double now = GetMonotonicSeconds();
            UnitySyncFileHashCache.Invalidate(_projectRoot, path);

            double due = now + ChangeDebounceSeconds;
            lock (PendingLock)
            {
                PendingLocalChanges[path] = due;
            }
        }

        private static bool ProjectFileNeedsSend(string path, string fullPath)
        {
            try
            {
                ulong hash = UnitySyncFileHashCache.GetOrCompute(
                    _projectRoot,
                    path,
                    fullPath,
                    out long length);
                return !KnownFiles.TryGetValue(path, out FileFingerprint known) ||
                       known.Length != length ||
                       known.Hash != hash;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is CryptographicException)
            {
                return true;
            }
        }

        private static void TrySendLocalChange(
            UnitySyncTransport transport,
            Guid localPlayerId,
            string path)
        {
            if (!TryGetFullProjectPath(path, out string fullPath))
            {
                return;
            }

            // New Unity assets must arrive with the source editor's .meta identity before
            // the asset itself is imported remotely. Otherwise Unity can generate/import
            // the asset under a different GUID and a simultaneous scene reference to the
            // host GUID cannot resolve. Send a changed companion meta first when needed.
            if (path.StartsWith("Assets/", StringComparison.Ordinal) &&
                !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath))
            {
                string metaPath = path + ".meta";
                if (TryGetFullProjectPath(metaPath, out string fullMetaPath) &&
                    File.Exists(fullMetaPath) &&
                    ProjectFileNeedsSend(metaPath, fullMetaPath))
                {
                    TrySendLocalChange(transport, localPlayerId, metaPath);
                    if (ProjectFileNeedsSend(metaPath, fullMetaPath))
                    {
                        Requeue(path);
                        return;
                    }
                }
            }

            if (!File.Exists(fullPath))
            {
                if (KnownFiles.Remove(path))
                {
                    transport.SendProjectFileDelete(localPlayerId, path);
                }
                else
                {
                    // A deletion can be the first event observed after a rename or refresh.
                    transport.SendProjectFileDelete(localPlayerId, path);
                }

                return;
            }

            try
            {
                ulong hash = UnitySyncFileHashCache.GetOrCompute(
                    _projectRoot,
                    path,
                    fullPath,
                    out long length);
                if (KnownFiles.TryGetValue(path, out FileFingerprint known) &&
                    known.Length == length &&
                    known.Hash == hash)
                {
                    return;
                }

                Guid syncId = Guid.NewGuid();
                using (FileStream stream = new FileStream(
                           fullPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete,
                           1024 * 1024,
                           FileOptions.SequentialScan))
                {
                    if (stream.Length != length)
                    {
                        Requeue(path);
                        return;
                    }

                    transport.SendProjectFileBegin(
                        localPlayerId,
                        new UnitySyncFileSyncMessage
                        {
                            SyncId = syncId,
                            Path = path,
                            Length = length,
                            Hash = hash
                        });

                    long offset = 0;
                    if (length == 0)
                    {
                        transport.SendProjectFileChunk(
                            localPlayerId,
                            new UnitySyncFileSyncMessage
                            {
                                SyncId = syncId,
                                Path = path,
                                Length = 0,
                                Offset = 0,
                                Hash = hash,
                                Data = new byte[0]
                            });
                    }

                    while (offset < length)
                    {
                        int size = (int)Math.Min(
                            UnitySyncProtocol.MaximumFileChunkBytes,
                            length - offset);
                        byte[] data = new byte[size];
                        int read = stream.Read(data, 0, size);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException();
                        }

                        if (read != data.Length)
                        {
                            Array.Resize(ref data, read);
                        }

                        transport.SendProjectFileChunk(
                            localPlayerId,
                            new UnitySyncFileSyncMessage
                            {
                                SyncId = syncId,
                                Path = path,
                                Length = length,
                                Offset = offset,
                                Hash = hash,
                                Data = data
                            });
                        offset += read;
                    }
                }

                KnownFiles[path] = new FileFingerprint(length, hash);
            }
            catch (IOException)
            {
                Requeue(path);
            }
            catch (UnauthorizedAccessException)
            {
                Requeue(path);
            }
            catch (CryptographicException)
            {
                Requeue(path);
            }
        }

        private static bool BeginRemoteTransfer(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (message == null ||
                message.SyncId == Guid.Empty ||
                message.Length < 0 ||
                !IsLiveSyncPath(message.Path))
            {
                error = "Received an invalid project file update.";
                return false;
            }

            if (RemoteTransfers.TryGetValue(message.SyncId, out RemoteTransfer existing))
            {
                CleanupTransfer(existing);
                RemoteTransfers.Remove(message.SyncId);
            }

            try
            {
                string tempRoot = Path.Combine(
                    GetProjectRoot(),
                    "Library",
                    "UnitySyncLiveProject");
                Directory.CreateDirectory(tempRoot);
                string tempPath = Path.Combine(
                    tempRoot,
                    message.SyncId.ToString("N") + ".tmp");

                RemoteTransfer transfer = new RemoteTransfer
                {
                    PlayerId = playerId,
                    SyncId = message.SyncId,
                    Path = message.Path,
                    Length = message.Length,
                    Hash = message.Hash,
                    TempPath = tempPath,
                    Stream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None)
                };
                RemoteTransfers.Add(message.SyncId, transfer);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                error = "Could not prepare a synchronized project file: " + exception.Message;
                return false;
            }
        }

        private static bool HandleRemoteChunk(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (message == null ||
                message.SyncId == Guid.Empty ||
                !RemoteTransfers.TryGetValue(message.SyncId, out RemoteTransfer transfer) ||
                transfer.PlayerId != playerId ||
                !string.Equals(transfer.Path, message.Path, StringComparison.Ordinal) ||
                transfer.Length != message.Length ||
                transfer.Hash != message.Hash ||
                message.Data == null ||
                message.Offset != transfer.Received ||
                transfer.Received + message.Data.Length > transfer.Length)
            {
                error = "Received an invalid project file chunk.";
                return false;
            }

            try
            {
                if (message.Data.Length > 0)
                {
                    transfer.Stream.Write(message.Data, 0, message.Data.Length);
                    transfer.Received += message.Data.Length;
                }

                if (transfer.Received != transfer.Length)
                {
                    return true;
                }

                transfer.Stream.Dispose();
                transfer.Stream = null;

                if (!UnitySyncXxHash64.MatchesFile(
                    transfer.TempPath,
                    transfer.Length,
                    transfer.Hash))
                {
                    error = "A synchronized project file failed its hash check: " +
                            transfer.Path;
                    CleanupTransfer(transfer);
                    RemoteTransfers.Remove(transfer.SyncId);
                    return false;
                }

                bool applied = ApplyRemoteFile(transfer, out error);
                CleanupTransfer(transfer);
                RemoteTransfers.Remove(transfer.SyncId);
                return applied;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is CryptographicException)
            {
                error = "Could not apply a synchronized project file: " + exception.Message;
                CleanupTransfer(transfer);
                RemoteTransfers.Remove(transfer.SyncId);
                return false;
            }
        }

        private static bool ApplyRemoteFile(RemoteTransfer transfer, out string error)
        {
            error = string.Empty;
            if (!TryGetFullProjectPath(transfer.Path, out string targetPath))
            {
                error = "A synchronized project file had an unsafe path.";
                return false;
            }

            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SuppressPath(transfer.Path);
            if (File.Exists(targetPath))
            {
                FileAttributes attributes = File.GetAttributes(targetPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            File.Copy(transfer.TempPath, targetPath, true);
            UnitySyncFileHashCache.RecordVerifiedFile(
                _projectRoot,
                transfer.Path,
                targetPath,
                transfer.Length,
                transfer.Hash);
            KnownFiles[transfer.Path] =
                new FileFingerprint(transfer.Length, transfer.Hash);
            RefreshUnityForPath(transfer.Path);
            return true;
        }

        private static bool ApplyRemoteDelete(
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (message == null ||
                !IsLiveSyncPath(message.Path) ||
                !TryGetFullProjectPath(message.Path, out string fullPath))
            {
                error = "Received an invalid project file deletion.";
                return false;
            }

            try
            {
                SuppressPath(message.Path);
                if (File.Exists(fullPath))
                {
                    FileAttributes attributes = File.GetAttributes(fullPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.Delete(fullPath);
                }

                KnownFiles.Remove(message.Path);
                UnitySyncFileHashCache.Invalidate(_projectRoot, message.Path);
                RefreshUnityForPath(message.Path);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                error = "Could not delete a synchronized project file: " + exception.Message;
                return false;
            }
        }

        private static void RefreshUnityForPath(string path)
        {
            if (path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                UnitySyncSession.BeginSynchronizedAssetImport();
                try
                {
                    bool isMeta = path.EndsWith(
                        ".meta",
                        StringComparison.OrdinalIgnoreCase);
                    string importPath = isMeta
                        ? path.Substring(0, path.Length - ".meta".Length)
                        : path;

                    // A new asset and its source .meta must be present together before
                    // Unity sees either one. Importing the asset first can generate a local
                    // GUID; refreshing an orphan .meta first can make Unity delete it.
                    string assetFullPath = Path.Combine(
                        GetProjectRoot(),
                        importPath.Replace('/', Path.DirectorySeparatorChar));
                    string metaFullPath = assetFullPath + ".meta";
                    if (!File.Exists(assetFullPath) || !File.Exists(metaFullPath))
                    {
                        return;
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    if (!string.IsNullOrEmpty(importPath) &&
                        File.Exists(Path.Combine(
                            GetProjectRoot(),
                            importPath.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        AssetDatabase.ImportAsset(
                            importPath,
                            ImportAssetOptions.ForceUpdate |
                            ImportAssetOptions.ForceSynchronousImport);
                    }
                }
                finally
                {
                    UnitySyncSession.EndSynchronizedAssetImport();
                }

                return;
            }

            if (path.StartsWith("ProjectSettings/", StringComparison.Ordinal))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }

        private static void SuppressPath(string path)
        {
            lock (PendingLock)
            {
                PendingLocalChanges.Remove(path);
                SuppressedUntil[path] =
                    GetMonotonicSeconds() + RemoteEchoSuppressionSeconds;
            }
        }

        private static bool IsSuppressed(string path, double now)
        {
            lock (PendingLock)
            {
                return SuppressedUntil.TryGetValue(path, out double until) && until > now;
            }
        }

        private static void Requeue(string path)
        {
            lock (PendingLock)
            {
                PendingLocalChanges[path] =
                    GetMonotonicSeconds() + ChangeDebounceSeconds;
            }
        }

        private static bool TryGetProjectRelativePath(string fullPath, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(_projectRoot))
            {
                return false;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(fullPath);
            }
            catch
            {
                return false;
            }

            string root = _projectRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(root, comparison))
            {
                return false;
            }

            path = candidate.Substring(root.Length)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            return true;
        }

        private static bool TryGetFullProjectPath(string path, out string fullPath)
        {
            fullPath = string.Empty;
            if (!IsLiveSyncPath(path))
            {
                return false;
            }

            string root = GetProjectRoot();
            string candidate = Path.GetFullPath(Path.Combine(
                root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootPrefix, comparison))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }

        private static bool IsLiveSyncPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
                !normalized.StartsWith("ProjectSettings/", StringComparison.Ordinal))
            {
                return false;
            }

            string[] segments = normalized.Split('/');
            foreach (string segment in segments)
            {
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                {
                    return false;
                }
            }

            if (normalized.IndexOf(
                    "/SerializedUdonPrograms/",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.EndsWith(
                    "/SerializedUdonPrograms",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string effectivePath = normalized;
            if (effectivePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                effectivePath = effectivePath.Substring(
                    0,
                    effectivePath.Length - ".meta".Length);
            }

            if (effectivePath.EndsWith(
                    "/SerializedUdonPrograms",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string extension = Path.GetExtension(effectivePath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".asmref", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string GetProjectRoot()
        {
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            return parent != null ? parent.FullName : Application.dataPath;
        }

        private static double GetMonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private static void CleanupTransfer(RemoteTransfer transfer)
        {
            if (transfer == null)
            {
                return;
            }

            try
            {
                transfer.Stream?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            transfer.Stream = null;
            if (!string.IsNullOrEmpty(transfer.TempPath) && File.Exists(transfer.TempPath))
            {
                try
                {
                    File.Delete(transfer.TempPath);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }
}
