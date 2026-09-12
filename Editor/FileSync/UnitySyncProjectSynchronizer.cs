using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncProjectSynchronizer
    {
        private sealed class RemoteTransfer
        {
            internal Guid PlayerId;
            internal Guid SyncId;
            internal string Path;
            internal long Length;
            internal byte[] Hash;
            internal string TempPath;
            internal FileStream Stream;
            internal long Received;
        }

        private const double ChangeDebounceSeconds = 0.2d;
        private const double RemoteEchoSuppressionSeconds = 2.0d;

        private static readonly object PendingLock = new object();
        private static readonly Dictionary<string, double> PendingLocalChanges =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> SuppressedUntil =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, byte[]> KnownHashes =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<Guid, RemoteTransfer> RemoteTransfers =
            new Dictionary<Guid, RemoteTransfer>();

        private static bool _active;
        private static string _projectRoot = string.Empty;
        private static FileSystemWatcher _assetsWatcher;
        private static FileSystemWatcher _projectSettingsWatcher;

        internal static void BeginSession()
        {
            EndSession();

            _projectRoot = GetProjectRoot();
            _active = true;
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
            DisposeWatcher(ref _assetsWatcher);
            DisposeWatcher(ref _projectSettingsWatcher);

            lock (PendingLock)
            {
                PendingLocalChanges.Clear();
                SuppressedUntil.Clear();
            }

            KnownHashes.Clear();
            foreach (RemoteTransfer transfer in RemoteTransfers.Values)
            {
                CleanupTransfer(transfer);
            }

            RemoteTransfers.Clear();
            _projectRoot = string.Empty;
        }

        internal static void Update(UnitySyncTransport transport, Guid localPlayerId)
        {
            if (!_active || transport == null || EditorApplication.isCompiling)
            {
                return;
            }

            double now = GetMonotonicSeconds();
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

            double due = GetMonotonicSeconds() + ChangeDebounceSeconds;
            lock (PendingLock)
            {
                PendingLocalChanges[path] = due;
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

            if (!File.Exists(fullPath))
            {
                if (KnownHashes.Remove(path))
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
                byte[] hash = ComputeHash(fullPath);
                if (KnownHashes.TryGetValue(path, out byte[] knownHash) &&
                    HashesEqual(knownHash, hash))
                {
                    return;
                }

                FileInfo info = new FileInfo(fullPath);
                Guid syncId = Guid.NewGuid();
                transport.SendProjectFileBegin(
                    localPlayerId,
                    new UnitySyncFileSyncMessage
                    {
                        SyncId = syncId,
                        Path = path,
                        Length = info.Length,
                        Hash = hash
                    });

                using (FileStream stream = new FileStream(
                           fullPath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    long offset = 0;
                    if (info.Length == 0)
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

                    while (offset < info.Length)
                    {
                        int size = (int)Math.Min(
                            UnitySyncProtocol.MaximumFileChunkBytes,
                            info.Length - offset);
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
                                Length = info.Length,
                                Offset = offset,
                                Hash = hash,
                                Data = data
                            });
                        offset += read;
                    }
                }

                KnownHashes[path] = CopyHash(hash);
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
                message.Hash == null ||
                message.Hash.Length != 32 ||
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
                    Hash = CopyHash(message.Hash),
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
                !HashesEqual(transfer.Hash, message.Hash) ||
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

                if (!HashesEqual(ComputeHash(transfer.TempPath), transfer.Hash))
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
            KnownHashes[transfer.Path] = CopyHash(transfer.Hash);
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

                KnownHashes.Remove(message.Path);
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
                string importPath = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(0, path.Length - ".meta".Length)
                    : path;

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

        private static byte[] ComputeHash(string fullPath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                return sha.ComputeHash(stream);
            }
        }

        private static bool HashesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static byte[] CopyHash(byte[] hash)
        {
            if (hash == null)
            {
                return null;
            }

            byte[] copy = new byte[hash.Length];
            Buffer.BlockCopy(hash, 0, copy, 0, hash.Length);
            return copy;
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
