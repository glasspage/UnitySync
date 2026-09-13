using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncFileSynchronizer
    {
        private enum GuestPhase
        {
            None,
            WaitingForRestoreApproval,
            WaitingForManifest,
            Comparing,
            WaitingForConfirmation,
            Downloading,
            ResolvingPackages,
            ImportingAssets
        }

        private enum GuestDownloadKind
        {
            None,
            Packages,
            Assets
        }

        [Serializable]
        private sealed class PackageJsonData
        {
            public string version;
        }

        private sealed class FileEntry
        {
            internal string Path;
            internal string PackageVersion = string.Empty;
            internal long Length;
            internal byte[] Hash;
        }

        private sealed class HostManifest
        {
            internal Guid TargetPlayerId;
            internal Guid SyncId;
            internal UnitySyncFileSyncScope Scope;
            internal List<FileEntry> Entries;
            internal Dictionary<string, FileEntry> EntriesByPath;
            internal long TotalBytes;
            internal int SendIndex;
            internal int PackageVersionSendIndex;
            internal bool BeginSent;
            internal bool EndSent;
        }

        private sealed class HostTransfer
        {
            internal Guid TargetPlayerId;
            internal Guid SyncId;
            internal FileEntry Entry;
            internal FileStream Stream;
            internal long Offset;
            internal bool EmptyChunkSent;
        }

        private sealed class GuestTransfer
        {
            internal FileEntry Entry;
            internal string TempPath;
            internal FileStream Stream;
            internal long Received;
        }

        private const string ExcludedFolderName = "SerializedUdonPrograms";
        private const int CompareFilesPerUpdate = 12;
        private const int ManifestMessagesPerUpdate = 64;
        private const int FileRequestsPerUpdate = 32;
        private const int FileChunksPerUpdate = 4;
        private const double ImportSettleSeconds = 1.0d;

        private static readonly Dictionary<Guid, HostManifest> HostManifests =
            new Dictionary<Guid, HostManifest>();
        private static readonly Queue<HostManifest> HostManifestSends =
            new Queue<HostManifest>();
        private static readonly Queue<HostTransfer> HostTransfers =
            new Queue<HostTransfer>();
        private static readonly HashSet<string> HostTransferKeys =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<Guid> RestoreRequests = new HashSet<Guid>();
        private static bool _guestForceRestore;
        private static readonly string[] RestoreRoots = { "Assets", "Packages", "ProjectSettings" };
        internal static Guid[] PendingProjectRestores
        {
            get
            {
                Guid[] requests = new Guid[RestoreRequests.Count];
                RestoreRequests.CopyTo(requests);
                return requests;
            }
        }
        internal static bool IsWaitingForRestoreApproval =>
            _guestPhase == GuestPhase.WaitingForRestoreApproval;

        internal static void RequestProjectRestore(UnitySyncTransport transport)
        {
            ResetGuestState();
            _guestPhase = GuestPhase.WaitingForRestoreApproval;
            transport.SendRestoreProjectControl(UnitySyncMessageType.RestoreProjectRequest, Guid.Empty);
        }

        internal static void RespondToProjectRestore(
            UnitySyncTransport transport, Guid localPlayerId, Guid playerId, bool accept)
        {
            if (!RestoreRequests.Remove(playerId))
            {
                return;
            }

            transport.SendRestoreProjectControl(
                accept ? UnitySyncMessageType.RestoreProjectAccepted :
                         UnitySyncMessageType.RestoreProjectDeclined, playerId);
            if (accept)
            {
                BeginHostManifest(transport, localPlayerId, playerId,
                    new UnitySyncFileSyncMessage { Scope = UnitySyncFileSyncScope.Project },
                    out string error);
                transport.LogLocal(string.IsNullOrEmpty(error)
                    ? "Accepted full project restore for " + playerId.ToString("N") + "."
                    : error);
            }
        }

        private static GuestPhase _guestPhase;
        private static Guid _guestHostPlayerId;
        private static Guid _guestSyncId;
        private static UnitySyncFileSyncScope _guestRequestedScope;
        private static int _guestExpectedFileCount;
        private static long _guestExpectedTotalBytes;
        private static readonly List<FileEntry> GuestManifest =
            new List<FileEntry>();
        private static readonly HashSet<string> GuestObsoletePaths =
            new HashSet<string>(StringComparer.Ordinal);
        internal static int GuestPendingDeletionCount => GuestObsoletePaths.Count;

        private static readonly Dictionary<string, FileEntry> GuestManifestByPath =
            new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        private static readonly List<FileEntry> GuestMismatches =
            new List<FileEntry>();
        private static readonly List<FileEntry> GuestPackageMismatches =
            new List<FileEntry>();
        private static readonly List<FileEntry> GuestAssetMismatches =
            new List<FileEntry>();
        private static readonly List<FileEntry> GuestActiveMismatches =
            new List<FileEntry>();
        private static readonly Dictionary<string, FileEntry> GuestMismatchByPath =
            new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> GuestHostPackageVersions =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> GuestPackageRootsToReplace =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GuestTransfer> GuestTransfers =
            new Dictionary<string, GuestTransfer>(StringComparer.Ordinal);
        private static int _guestCompareIndex;
        private static int _guestRequestIndex;
        private static int _guestCompletedFiles;
        private static long _guestDownloadTotalBytes;
        private static long _guestDownloadBytesReceived;
        private static long _guestStageDownloadTotalBytes;
        private static long _guestStageDownloadBytesReceived;
        private static GuestDownloadKind _guestDownloadKind;
        private static bool _guestAutoContinue;
        private static string _guestTempRoot = string.Empty;
        private static bool _guestReadyForSceneSnapshot;
        private static bool _guestAutoRefreshBlocked;
        private static string _guestFailure = string.Empty;
        private static double _guestImportEarliestComplete;

        internal static bool IsGuestSyncing => _guestPhase != GuestPhase.None;
        internal static bool IsGuestReconcilingPackages =>
            IsGuestSyncing &&
            (_guestRequestedScope == UnitySyncFileSyncScope.Packages ||
             _guestRequestedScope == UnitySyncFileSyncScope.Project ||
             IsWaitingForRestoreApproval);
        internal static bool IsGuestAwaitingDownloadConfirmation =>
            _guestPhase == GuestPhase.WaitingForConfirmation;
        internal static int GuestPendingDownloadFileCount =>
            IsGuestAwaitingDownloadConfirmation ? GuestMismatches.Count : 0;
        internal static long GuestPendingDownloadBytes =>
            IsGuestAwaitingDownloadConfirmation ? _guestDownloadTotalBytes : 0;

        internal static void BeginGuestSync(
            UnitySyncTransport transport,
            bool autoContinue)
        {
            ResetGuestState();
            _guestAutoContinue = autoContinue;
            BeginGuestManifestRequest(
                transport,
                UnitySyncFileSyncScope.Packages);
        }

        internal static bool ContinueGuestSync(
            UnitySyncTransport transport,
            out string error)
        {
            error = string.Empty;
            if (transport == null ||
                _guestPhase != GuestPhase.WaitingForConfirmation ||
                _guestSyncId == Guid.Empty)
            {
                error = "There is no pending host file comparison to continue.";
                return false;
            }

            _guestAutoContinue = true;
            return BeginApprovedGuestSync(transport, out error);
        }

        internal static void EndSession()
        {
            RestoreRequests.Clear();
            foreach (HostTransfer transfer in HostTransfers)
            {
                CloseHostTransfer(transfer);
            }

            HostTransfers.Clear();
            HostTransferKeys.Clear();
            HostManifestSends.Clear();
            HostManifests.Clear();

            ResetGuestState();
            _guestReadyForSceneSnapshot = false;
            EditorUtility.ClearProgressBar();
        }

        internal static void RemoveHostPlayer(Guid playerId)
        {
            RestoreRequests.Remove(playerId);
            if (playerId == Guid.Empty)
            {
                return;
            }

            List<Guid> manifestIds = new List<Guid>();
            foreach (KeyValuePair<Guid, HostManifest> pair in HostManifests)
            {
                if (pair.Value.TargetPlayerId == playerId)
                {
                    manifestIds.Add(pair.Key);
                }
            }

            foreach (Guid manifestId in manifestIds)
            {
                HostManifests.Remove(manifestId);
            }

            if (manifestIds.Count == 0)
            {
                return;
            }

            Queue<HostTransfer> retained = new Queue<HostTransfer>();
            while (HostTransfers.Count > 0)
            {
                HostTransfer transfer = HostTransfers.Dequeue();
                if (transfer.TargetPlayerId == playerId)
                {
                    HostTransferKeys.Remove(GetHostTransferKey(transfer));
                    CloseHostTransfer(transfer);
                }
                else
                {
                    retained.Enqueue(transfer);
                }
            }

            while (retained.Count > 0)
            {
                HostTransfers.Enqueue(retained.Dequeue());
            }
        }

        internal static bool HandleMessage(
            UnitySyncTransport transport,
            Guid localPlayerId,
            UnitySyncMessageType messageType,
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            switch (messageType)
            {
                case UnitySyncMessageType.RestoreProjectRequest:
                    if (RestoreRequests.Add(playerId))
                    {
                        transport.LogLocal("Project restore requested by " + playerId.ToString("N") +
                            ". Open Debug to accept or decline.");
                    }
                    return true;

                case UnitySyncMessageType.RestoreProjectDeclined:
                    if (!IsWaitingForRestoreApproval)
                    {
                        error = "Unexpected project restore response.";
                        return false;
                    }
                    FailGuestSync("The host declined the project restore request.");
                    return true;

                case UnitySyncMessageType.RestoreProjectAccepted:
                    if (!IsWaitingForRestoreApproval)
                    {
                        error = "Unexpected project restore approval.";
                        return false;
                    }
                    _guestForceRestore = true;
                    _guestAutoContinue = true;
                    BeginGuestManifestRequest(transport, UnitySyncFileSyncScope.Project, false);
                    return true;

                case UnitySyncMessageType.FileSyncRequest:
                    BeginHostManifest(
                        transport,
                        localPlayerId,
                        playerId,
                        message,
                        out error);
                    return true;

                case UnitySyncMessageType.FileRequest:
                    QueueHostTransfer(transport, localPlayerId, playerId, message, out error);
                    return true;

                case UnitySyncMessageType.FileManifestBegin:
                    return HandleGuestManifestBegin(playerId, message, out error);

                case UnitySyncMessageType.FileManifestEntry:
                    return HandleGuestManifestEntry(playerId, message, out error);

                case UnitySyncMessageType.PackageVersionEntry:
                    return HandleGuestPackageVersionEntry(playerId, message, out error);

                case UnitySyncMessageType.FileManifestEnd:
                    return HandleGuestManifestEnd(playerId, message, out error);

                case UnitySyncMessageType.FileChunk:
                    return HandleGuestFileChunk(message, out error);

                case UnitySyncMessageType.FileSyncAbort:
                    error = string.IsNullOrEmpty(message.Error)
                        ? "The host aborted file synchronization."
                        : message.Error;
                    FailGuestSync(error);
                    return false;

                default:
                    error = "Unsupported file sync message.";
                    return false;
            }
        }

        internal static void Update(UnitySyncTransport transport, Guid localPlayerId)
        {
            UpdateHostManifestSends(transport, localPlayerId);
            UpdateHostTransfers(transport, localPlayerId);

            switch (_guestPhase)
            {
                case GuestPhase.Comparing:
                    UpdateGuestComparison(transport);
                    break;

                case GuestPhase.Downloading:
                    UpdateGuestRequests(transport);
                    UpdateGuestDownloadProgress();
                    break;

                case GuestPhase.ResolvingPackages:
                    UpdateGuestPackageResolution(transport);
                    break;

                case GuestPhase.ImportingAssets:
                    UpdateGuestAssetImport();
                    break;
            }
        }

        internal static bool ConsumeGuestReadyForSceneSnapshot()
        {
            if (!_guestReadyForSceneSnapshot)
            {
                return false;
            }

            _guestReadyForSceneSnapshot = false;
            return true;
        }

        internal static bool ConsumeGuestFailure(out string error)
        {
            error = _guestFailure;
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            _guestFailure = string.Empty;
            return true;
        }

        private static void BeginHostManifest(
            UnitySyncTransport transport,
            Guid localPlayerId,
            Guid targetPlayerId,
            UnitySyncFileSyncMessage request,
            out string error)
        {
            error = string.Empty;
            if (request == null ||
                (request.Scope != UnitySyncFileSyncScope.Packages &&
                 request.Scope != UnitySyncFileSyncScope.Assets &&
                 request.Scope != UnitySyncFileSyncScope.Project))
            {
                error = "A collaborator requested an invalid file sync scope.";
                return;
            }

            try
            {
                List<FileEntry> entries =
                    BuildLocalManifest(request.Scope, out long totalBytes);
                Guid syncId = Guid.NewGuid();
                Dictionary<string, FileEntry> entriesByPath =
                    new Dictionary<string, FileEntry>(StringComparer.Ordinal);
                foreach (FileEntry entry in entries)
                {
                    entriesByPath[entry.Path] = entry;
                }

                HostManifest manifest = new HostManifest
                {
                    TargetPlayerId = targetPlayerId,
                    SyncId = syncId,
                    Scope = request.Scope,
                    Entries = entries,
                    EntriesByPath = entriesByPath,
                    TotalBytes = totalBytes
                };
                HostManifests[syncId] = manifest;
                HostManifestSends.Enqueue(manifest);
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is CryptographicException)
            {
                Guid syncId = Guid.NewGuid();
                transport.SendFileSyncAbort(
                    localPlayerId,
                    syncId,
                    "The host could not build its " + request.Scope +
                    " manifest: " + exception.Message,
                    targetPlayerId);
                error = "File sync could not build the host project manifest: " + exception.Message;
            }
        }

        private static void UpdateHostManifestSends(
            UnitySyncTransport transport,
            Guid localPlayerId)
        {
            int budget = ManifestMessagesPerUpdate;
            while (budget > 0 && HostManifestSends.Count > 0)
            {
                HostManifest manifest = HostManifestSends.Peek();
                if (!HostManifests.ContainsKey(manifest.SyncId))
                {
                    HostManifestSends.Dequeue();
                    continue;
                }

                if (!manifest.BeginSent)
                {
                    transport.SendFileManifestBegin(
                        localPlayerId,
                        new UnitySyncFileSyncMessage
                        {
                            SyncId = manifest.SyncId,
                            Scope = manifest.Scope,
                            FileCount = manifest.Entries.Count,
                            TotalBytes = manifest.TotalBytes
                        },
                        manifest.TargetPlayerId);
                    manifest.BeginSent = true;
                    budget--;
                    continue;
                }

                while (manifest.PackageVersionSendIndex < manifest.Entries.Count)
                {
                    FileEntry versionEntry =
                        manifest.Entries[manifest.PackageVersionSendIndex++];
                    if (string.IsNullOrEmpty(versionEntry.PackageVersion))
                    {
                        continue;
                    }

                    transport.SendPackageVersionEntry(
                        localPlayerId,
                        new UnitySyncFileSyncMessage
                        {
                            SyncId = manifest.SyncId,
                            Path = versionEntry.Path,
                            PackageVersion = versionEntry.PackageVersion
                        },
                        manifest.TargetPlayerId);
                    budget--;
                    break;
                }

                if (budget == 0)
                {
                    continue;
                }

                if (manifest.PackageVersionSendIndex < manifest.Entries.Count)
                {
                    continue;
                }

                if (manifest.SendIndex < manifest.Entries.Count)
                {
                    FileEntry entry = manifest.Entries[manifest.SendIndex++];
                    transport.SendFileManifestEntry(
                        localPlayerId,
                        new UnitySyncFileSyncMessage
                        {
                            SyncId = manifest.SyncId,
                            Path = entry.Path,
                            PackageVersion = entry.PackageVersion,
                            Length = entry.Length,
                            Hash = entry.Hash
                        },
                        manifest.TargetPlayerId);
                    budget--;
                    continue;
                }

                if (!manifest.EndSent)
                {
                    transport.SendFileManifestEnd(
                        localPlayerId,
                        manifest.SyncId,
                        manifest.TargetPlayerId);
                    manifest.EndSent = true;
                    budget--;
                }

                HostManifestSends.Dequeue();
            }
        }

        private static void QueueHostTransfer(
            UnitySyncTransport transport,
            Guid localPlayerId,
            Guid targetPlayerId,
            UnitySyncFileSyncMessage request,
            out string error)
        {
            error = string.Empty;
            if (request == null ||
                !HostManifests.TryGetValue(request.SyncId, out HostManifest manifest) ||
                manifest.TargetPlayerId != targetPlayerId ||
                !manifest.EntriesByPath.TryGetValue(request.Path ?? string.Empty, out FileEntry entry))
            {
                error = "A collaborator requested a file outside its active host manifest.";
                if (request != null && request.SyncId != Guid.Empty)
                {
                    transport.SendFileSyncAbort(
                        localPlayerId,
                        request.SyncId,
                        error,
                        targetPlayerId);
                }

                return;
            }

            string key = targetPlayerId.ToString("N") + "|" +
                         request.SyncId.ToString("N") + "|" + entry.Path;
            if (!HostTransferKeys.Add(key))
            {
                return;
            }

            HostTransfers.Enqueue(new HostTransfer
            {
                TargetPlayerId = targetPlayerId,
                SyncId = request.SyncId,
                Entry = entry
            });
        }

        private static void UpdateHostTransfers(
            UnitySyncTransport transport,
            Guid localPlayerId)
        {
            int budget = FileChunksPerUpdate;
            while (budget > 0 && HostTransfers.Count > 0)
            {
                HostTransfer transfer = HostTransfers.Peek();
                try
                {
                    if (transfer.Stream == null)
                    {
                        if (!TryGetFullSyncPath(transfer.Entry.Path, out string fullPath) ||
                            !File.Exists(fullPath))
                        {
                            AbortHostTransfer(
                                transport,
                                localPlayerId,
                                transfer,
                                "A host file disappeared during synchronization.");
                            continue;
                        }

                        FileInfo info = new FileInfo(fullPath);
                        if (info.Length != transfer.Entry.Length ||
                            !HashesEqual(ComputeHash(fullPath), transfer.Entry.Hash))
                        {
                            AbortHostTransfer(
                                transport,
                                localPlayerId,
                                transfer,
                                "A host file changed while it was being synchronized. Reconnect to retry.");
                            continue;
                        }

                        transfer.Stream = new FileStream(
                            fullPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                    }

                    if (transfer.Entry.Length == 0 && !transfer.EmptyChunkSent)
                    {
                        transport.SendFileChunk(
                            localPlayerId,
                            new UnitySyncFileSyncMessage
                            {
                                SyncId = transfer.SyncId,
                                Path = transfer.Entry.Path,
                                Length = 0,
                                Offset = 0,
                                Hash = transfer.Entry.Hash,
                                Data = new byte[0]
                            },
                            transfer.TargetPlayerId);
                        transfer.EmptyChunkSent = true;
                        CompleteHostTransfer();
                        budget--;
                        continue;
                    }

                    int readSize = (int)Math.Min(
                        UnitySyncProtocol.MaximumFileChunkBytes,
                        transfer.Entry.Length - transfer.Offset);
                    byte[] buffer = new byte[readSize];
                    int read = transfer.Stream.Read(buffer, 0, readSize);
                    if (read <= 0)
                    {
                        AbortHostTransfer(
                            transport,
                            localPlayerId,
                            transfer,
                            "The host could not finish reading a synchronized file.");
                        continue;
                    }

                    if (read != buffer.Length)
                    {
                        Array.Resize(ref buffer, read);
                    }

                    long offset = transfer.Offset;
                    transfer.Offset += read;
                    transport.SendFileChunk(
                        localPlayerId,
                        new UnitySyncFileSyncMessage
                        {
                            SyncId = transfer.SyncId,
                            Path = transfer.Entry.Path,
                            Length = transfer.Entry.Length,
                            Offset = offset,
                            Hash = transfer.Entry.Hash,
                            Data = buffer
                        },
                        transfer.TargetPlayerId);

                    if (transfer.Offset == transfer.Entry.Length)
                    {
                        CompleteHostTransfer();
                    }

                    budget--;
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is CryptographicException)
                {
                    AbortHostTransfer(
                        transport,
                        localPlayerId,
                        transfer,
                        "The host could not send " + transfer.Entry.Path + ": " + exception.Message);
                }
            }
        }

        private static void AbortHostTransfer(
            UnitySyncTransport transport,
            Guid localPlayerId,
            HostTransfer transfer,
            string error)
        {
            transport.SendFileSyncAbort(
                localPlayerId,
                transfer.SyncId,
                error,
                transfer.TargetPlayerId);
            RemoveHostManifestAndTransfers(transfer.SyncId);
        }

        private static void CompleteHostTransfer()
        {
            HostTransfer transfer = HostTransfers.Dequeue();
            HostTransferKeys.Remove(GetHostTransferKey(transfer));
            CloseHostTransfer(transfer);
        }

        private static void RemoveHostManifestAndTransfers(Guid syncId)
        {
            HostManifests.Remove(syncId);
            Queue<HostTransfer> retained = new Queue<HostTransfer>();
            while (HostTransfers.Count > 0)
            {
                HostTransfer transfer = HostTransfers.Dequeue();
                if (transfer.SyncId == syncId)
                {
                    HostTransferKeys.Remove(GetHostTransferKey(transfer));
                    CloseHostTransfer(transfer);
                }
                else
                {
                    retained.Enqueue(transfer);
                }
            }

            while (retained.Count > 0)
            {
                HostTransfers.Enqueue(retained.Dequeue());
            }
        }

        private static string GetHostTransferKey(HostTransfer transfer)
        {
            return transfer.TargetPlayerId.ToString("N") + "|" +
                   transfer.SyncId.ToString("N") + "|" +
                   transfer.Entry.Path;
        }

        private static void CloseHostTransfer(HostTransfer transfer)
        {
            if (transfer == null || transfer.Stream == null)
            {
                return;
            }

            transfer.Stream.Dispose();
            transfer.Stream = null;
        }

        private static bool HandleGuestManifestBegin(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (_guestPhase != GuestPhase.WaitingForManifest ||
                message == null ||
                message.SyncId == Guid.Empty ||
                message.FileCount < 0 ||
                message.TotalBytes < 0)
            {
                error = "The host started an invalid file manifest.";
                FailGuestSync(error);
                return false;
            }

            _guestHostPlayerId = playerId;
            _guestSyncId = message.SyncId;
            _guestExpectedFileCount = message.FileCount;
            _guestExpectedTotalBytes = message.TotalBytes;
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Receiving host " + _guestRequestedScope + " manifest...",
                0.03f);
            return true;
        }

        private static bool HandleGuestManifestEntry(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (_guestPhase != GuestPhase.WaitingForManifest ||
                playerId != _guestHostPlayerId ||
                message == null ||
                message.SyncId != _guestSyncId ||
                message.Hash == null ||
                message.Hash.Length != 32 ||
                message.Length < 0 ||
                GuestManifest.Count >= _guestExpectedFileCount ||
                !IsSafeSyncPath(message.Path) ||
                !PathMatchesScope(message.Path, _guestRequestedScope) ||
                GuestManifestByPath.ContainsKey(message.Path))
            {
                error = "The host sent an invalid file manifest entry.";
                FailGuestSync(error);
                return false;
            }

            FileEntry entry = new FileEntry
            {
                Path = message.Path,
                Length = message.Length,
                Hash = CopyHash(message.Hash)
            };
            GuestManifest.Add(entry);
            GuestManifestByPath.Add(entry.Path, entry);
            return true;
        }

        private static bool HandleGuestPackageVersionEntry(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (_guestPhase != GuestPhase.WaitingForManifest ||
                _guestRequestedScope != UnitySyncFileSyncScope.Packages ||
                playerId != _guestHostPlayerId ||
                message == null ||
                message.SyncId != _guestSyncId ||
                string.IsNullOrEmpty(message.PackageVersion) ||
                !IsEmbeddedPackageJsonPath(message.Path))
            {
                error = "The host sent an invalid package version entry.";
                FailGuestSync(error);
                return false;
            }

            GuestHostPackageVersions[message.Path] =
                message.PackageVersion;
            return true;
        }

        private static bool HandleGuestManifestEnd(
            Guid playerId,
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (_guestPhase != GuestPhase.WaitingForManifest ||
                playerId != _guestHostPlayerId ||
                message == null ||
                message.SyncId != _guestSyncId ||
                GuestManifest.Count != _guestExpectedFileCount)
            {
                error = "The host ended an incomplete file manifest.";
                FailGuestSync(error);
                return false;
            }

            if (_guestRequestedScope == UnitySyncFileSyncScope.Packages &&
                !PrepareGuestPackageVersionComparison(out error))
            {
                FailGuestSync(error);
                return false;
            }

            if (!_guestForceRestore)
            {
                try
                {
                    string root = _guestRequestedScope == UnitySyncFileSyncScope.Packages
                        ? "Packages" : "Assets";
                    List<string> localFiles = new List<string>();
                    EnumerateSyncRoot(Path.Combine(GetProjectRoot(), root), root, root == "Assets", localFiles);
                    foreach (string path in localFiles)
                    {
                        if (!GuestManifestByPath.ContainsKey(path))
                        {
                            GuestObsoletePaths.Add(path);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    error = "Could not compare guest-only files: " + exception.Message;
                    FailGuestSync(error);
                    return false;
                }
            }

            GuestManifest.Sort(CompareManifestEntries);
            _guestCompareIndex = 0;
            _guestPhase = GuestPhase.Comparing;
            return true;
        }

        private static void UpdateGuestComparison(UnitySyncTransport transport)
        {
            int processed = 0;
            try
            {
                while (processed < CompareFilesPerUpdate &&
                       _guestCompareIndex < GuestManifest.Count)
                {
                    FileEntry entry = GuestManifest[_guestCompareIndex++];
                    bool matches = false;
                    bool replaceWholePackage =
                        _guestRequestedScope == UnitySyncFileSyncScope.Packages &&
                        IsUnderPackageRootToReplace(entry.Path);
                    if (!_guestForceRestore && !replaceWholePackage &&
                        TryGetFullSyncPath(entry.Path, out string fullPath) &&
                        File.Exists(fullPath))
                    {
                        FileInfo info = new FileInfo(fullPath);
                        matches = info.Length == entry.Length &&
                                  HashesEqual(ComputeHash(fullPath), entry.Hash);
                    }

                    if (!matches &&
                        _guestRequestedScope == UnitySyncFileSyncScope.Packages &&
                        IsEmbeddedPackageJsonPath(entry.Path) &&
                        TryGetEmbeddedPackageRoot(
                            entry.Path,
                            out string packageRoot,
                            out _))
                    {
                        GuestPackageRootsToReplace.Add(packageRoot);
                    }

                    if (!matches)
                    {
                        GuestMismatches.Add(entry);
                        GuestMismatchByPath.Add(entry.Path, entry);
                        if (IsPackagePath(entry.Path))
                        {
                            GuestPackageMismatches.Add(entry);
                        }
                        else
                        {
                            GuestAssetMismatches.Add(entry);
                        }
                    }

                    processed++;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is CryptographicException)
            {
                FailGuestSync(
                    "File sync failed while comparing project files: " + exception.Message);
                return;
            }

            float compareProgress = GuestManifest.Count == 0
                ? 1f
                : (float)_guestCompareIndex / GuestManifest.Count;
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Comparing host " + _guestRequestedScope + "... " +
                _guestCompareIndex + "/" + GuestManifest.Count,
                Mathf.Lerp(0.05f, 0.45f, compareProgress));

            if (_guestCompareIndex < GuestManifest.Count)
            {
                return;
            }

            if (GuestMismatches.Count == 0 && GuestObsoletePaths.Count == 0)
            {
                if (_guestForceRestore)
                {
                    FailGuestSync("The host returned an empty project restore manifest.");
                    return;
                }

                if (_guestRequestedScope == UnitySyncFileSyncScope.Packages)
                {
                    BeginGuestManifestRequest(
                        transport,
                        UnitySyncFileSyncScope.Assets);
                }
                else
                {
                    CompleteGuestSync();
                }

                return;
            }

            _guestDownloadTotalBytes = 0;
            string[] neededPaths = new string[GuestMismatches.Count];
            for (int index = 0; index < GuestMismatches.Count; index++)
            {
                FileEntry entry = GuestMismatches[index];
                _guestDownloadTotalBytes += entry.Length;
                neededPaths[index] = entry.Path;
            }

            foreach (string path in GuestObsoletePaths)
            {
                transport.LogLocal("Guest-only file to remove: " + path);
            }

            if (_guestAutoContinue)
            {
                if (_guestForceRestore)
                {
                    transport.LogLocal("Restoring " + GuestMismatches.Count + " files (" +
                        _guestDownloadTotalBytes + " bytes) from the host.");
                }
                if (!BeginApprovedGuestSync(transport, out string continueError))
                {
                    FailGuestSync(continueError);
                }
                return;
            }

            _guestPhase = GuestPhase.WaitingForConfirmation;
            EditorUtility.ClearProgressBar();
            UnitySyncSession.ReportFileSyncDownloadRequired(
                neededPaths,
                _guestDownloadTotalBytes);
        }

        private static bool BeginApprovedGuestSync(
            UnitySyncTransport transport,
            out string error)
        {
            error = string.Empty;
            if (!PrepareGuestTempRoot(out error))
            {
                FailGuestSync(error);
                return false;
            }

            _guestDownloadBytesReceived = 0;
            if (_guestForceRestore)
            {
                BeginGuestDownloadStage(GuestDownloadKind.Assets, GuestMismatches);
            }
            else if (_guestRequestedScope == UnitySyncFileSyncScope.Packages)
            {
                BeginGuestDownloadStage(
                    GuestDownloadKind.Packages,
                    GuestPackageMismatches);
            }
            else
            {
                BeginGuestDownloadStage(
                    GuestDownloadKind.Assets,
                    GuestAssetMismatches);
            }

            if (GuestActiveMismatches.Count == 0)
            {
                CompleteGuestDownloadStage();
                return true;
            }

            UpdateGuestRequests(transport);
            UpdateGuestDownloadProgress();
            return true;
        }

        private static bool PrepareGuestTempRoot(out string error)
        {
            error = string.Empty;
            if (!string.IsNullOrEmpty(_guestTempRoot) && Directory.Exists(_guestTempRoot))
            {
                return true;
            }

            _guestTempRoot = Path.Combine(
                GetProjectRoot(),
                "Library",
                "UnitySyncFileSync",
                _guestSyncId.ToString("N"));
            try
            {
                if (Directory.Exists(_guestTempRoot))
                {
                    Directory.Delete(_guestTempRoot, true);
                }

                Directory.CreateDirectory(_guestTempRoot);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                error = "Could not prepare temporary file sync storage: " + exception.Message;
                return false;
            }
        }

        private static void BeginGuestDownloadStage(
            GuestDownloadKind kind,
            List<FileEntry> entries)
        {
            GuestActiveMismatches.Clear();
            GuestActiveMismatches.AddRange(entries);
            _guestDownloadKind = kind;
            _guestRequestIndex = 0;
            _guestCompletedFiles = 0;
            _guestStageDownloadBytesReceived = 0;
            _guestStageDownloadTotalBytes = 0;
            foreach (FileEntry entry in GuestActiveMismatches)
            {
                _guestStageDownloadTotalBytes += entry.Length;
            }

            SetGuestAutoRefreshBlocked(true);
            _guestPhase = GuestPhase.Downloading;
        }

        private static void UpdateGuestRequests(UnitySyncTransport transport)
        {
            int sent = 0;
            while (sent < FileRequestsPerUpdate &&
                   _guestRequestIndex < GuestActiveMismatches.Count)
            {
                FileEntry entry = GuestActiveMismatches[_guestRequestIndex++];
                transport.RequestFile(_guestSyncId, entry.Path);
                sent++;
            }
        }

        private static bool HandleGuestFileChunk(
            UnitySyncFileSyncMessage message,
            out string error)
        {
            error = string.Empty;
            if (_guestPhase != GuestPhase.Downloading ||
                message == null ||
                message.SyncId != _guestSyncId ||
                !GuestMismatchByPath.TryGetValue(message.Path ?? string.Empty, out FileEntry entry) ||
                (_guestDownloadKind == GuestDownloadKind.Packages && !IsPackagePath(entry.Path)) ||
                (_guestDownloadKind == GuestDownloadKind.Assets && !IsAssetPath(entry.Path)) ||
                message.Length != entry.Length ||
                message.Hash == null ||
                !HashesEqual(message.Hash, entry.Hash) ||
                message.Data == null ||
                message.Data.Length > UnitySyncProtocol.MaximumFileChunkBytes)
            {
                error = "The host sent an invalid synchronized file chunk.";
                FailGuestSync(error);
                return false;
            }

            try
            {
                if (!GuestTransfers.TryGetValue(entry.Path, out GuestTransfer transfer))
                {
                    if (message.Offset != 0)
                    {
                        error = "A synchronized file started at an invalid offset.";
                        FailGuestSync(error);
                        return false;
                    }

                    string tempPath = Path.Combine(
                        _guestTempRoot,
                        Guid.NewGuid().ToString("N") + ".tmp");
                    transfer = new GuestTransfer
                    {
                        Entry = entry,
                        TempPath = tempPath,
                        Stream = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None)
                    };
                    GuestTransfers.Add(entry.Path, transfer);
                }

                if (message.Offset != transfer.Received ||
                    transfer.Received + message.Data.Length > entry.Length)
                {
                    error = "A synchronized file chunk arrived out of order.";
                    FailGuestSync(error);
                    return false;
                }

                if (message.Data.Length > 0)
                {
                    transfer.Stream.Write(message.Data, 0, message.Data.Length);
                    transfer.Received += message.Data.Length;
                    _guestDownloadBytesReceived += message.Data.Length;
                    _guestStageDownloadBytesReceived += message.Data.Length;
                }

                if (transfer.Received != entry.Length)
                {
                    return true;
                }

                transfer.Stream.Dispose();
                transfer.Stream = null;
                if (!HashesEqual(ComputeHash(transfer.TempPath), entry.Hash))
                {
                    error = "A synchronized file failed its final hash check: " + entry.Path;
                    FailGuestSync(error);
                    return false;
                }

                if (!TryGetFullSyncPath(entry.Path, out string targetPath))
                {
                    error = "A synchronized file had an unsafe target path: " + entry.Path;
                    FailGuestSync(error);
                    return false;
                }

                if (_guestForceRestore)
                {
                    targetPath = Path.Combine(_guestTempRoot, "Project", entry.Path);
                }
                else
                {
                    targetPath = Path.Combine(_guestTempRoot, "FileStage", entry.Path);
                }

                string targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                if (File.Exists(targetPath))
                {
                    FileAttributes attributes = File.GetAttributes(targetPath);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                File.Copy(transfer.TempPath, targetPath, true);
                File.Delete(transfer.TempPath);
                GuestTransfers.Remove(entry.Path);
                _guestCompletedFiles++;

                if (_guestCompletedFiles == GuestActiveMismatches.Count)
                {
                    CompleteGuestDownloadStage();
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is CryptographicException)
            {
                error = "Could not write a synchronized file: " + exception.Message;
                FailGuestSync(error);
                return false;
            }
        }

        private static void InstallRestoredProject()
        {
            string projectRoot = GetProjectRoot();
            string stagedRoot = Path.Combine(_guestTempRoot, "Project");
            string backupRoot = Path.Combine(projectRoot, "Library", "UnitySyncRestoreBackup",
                _guestSyncId.ToString("N"));
            List<string> movedOriginals = new List<string>();
            List<string> installed = new List<string>();
            bool committed = false;
            EditorApplication.LockReloadAssemblies();
            try
            {
                // Require a complete Unity project, not an empty/partial replacement manifest.
                if (!File.Exists(Path.Combine(stagedRoot, "Packages", "manifest.json")) ||
                    !File.Exists(Path.Combine(stagedRoot, "ProjectSettings", "ProjectVersion.txt")))
                {
                    throw new IOException("The host restore is missing its package manifest or project version.");
                }

                foreach (string root in RestoreRoots)
                {
                    string destination = Path.Combine(projectRoot, root);
                    if (Directory.Exists(destination) &&
                        (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Cannot replace linked project root: " + root);
                    }
                    Directory.CreateDirectory(Path.Combine(stagedRoot, root));
                }

                // Close scenes before replacing their files; the debug request authorizes
                // discarding unsaved guest scene edits.
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                Directory.CreateDirectory(backupRoot);
                foreach (string root in RestoreRoots)
                {
                    string destination = Path.Combine(projectRoot, root);
                    if (Directory.Exists(destination))
                    {
                        Directory.Move(destination, Path.Combine(backupRoot, root));
                        movedOriginals.Add(root);
                    }
                    Directory.Move(Path.Combine(stagedRoot, root), destination);
                    installed.Add(root);
                }

                committed = true;
                _guestForceRestore = false;
                GuestActiveMismatches.Clear();
                _guestDownloadKind = GuestDownloadKind.None;
                UnitySyncSession.PrepareFileSyncReloadReconnect();
            }
            catch (Exception exception)
            {
                string rollbackError = string.Empty;
                if (!committed)
                {
                    try
                    {
                        foreach (string root in installed)
                        {
                            Directory.Move(Path.Combine(projectRoot, root), Path.Combine(stagedRoot, root));
                        }
                        foreach (string root in movedOriginals)
                        {
                            Directory.Move(Path.Combine(backupRoot, root), Path.Combine(projectRoot, root));
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackError = " Original files remain in " + backupRoot +
                            ". Rollback failed: " + rollbackException.Message;
                    }
                }
                FailGuestSync("Project restore failed: " + exception.Message + rollbackError);
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }

            if (committed)
            {
                try
                {
                    Directory.Delete(backupRoot, true);
                }
                catch (IOException) { /* A locked old file must not undo a completed restore. */ }
                catch (UnauthorizedAccessException) { }
                BeginGuestPackageResolution();
            }
        }

        private static void CompleteGuestDownloadStage()
        {
            if (_guestForceRestore)
            {
                InstallRestoredProject();
                return;
            }

            GuestDownloadKind completedKind = _guestDownloadKind;
            if (!ApplyStagedFiles(out string packageError))
            {
                FailGuestSync(packageError);
                return;
            }

            GuestActiveMismatches.Clear();
            _guestDownloadKind = GuestDownloadKind.None;
            _guestRequestIndex = 0;
            _guestCompletedFiles = 0;
            _guestStageDownloadTotalBytes = 0;
            _guestStageDownloadBytesReceived = 0;

            if (completedKind == GuestDownloadKind.Packages)
            {
                BeginGuestPackageResolution();
            }
            else
            {
                BeginGuestAssetImport();
            }
        }

        private static void UpdateGuestDownloadProgress()
        {
            float byteProgress;
            if (_guestStageDownloadTotalBytes > 0)
            {
                byteProgress = Mathf.Clamp01(
                    (float)((double)_guestStageDownloadBytesReceived /
                            _guestStageDownloadTotalBytes));
            }
            else
            {
                byteProgress = GuestActiveMismatches.Count == 0
                    ? 1f
                    : (float)_guestCompletedFiles / GuestActiveMismatches.Count;
            }

            string stageName = _guestDownloadKind == GuestDownloadKind.Packages
                ? "Packages"
                : "Assets";
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Receiving host " + stageName + "... " +
                _guestCompletedFiles + "/" + GuestActiveMismatches.Count,
                Mathf.Lerp(0.45f, 0.82f, byteProgress));
        }

        private static void BeginGuestPackageResolution()
        {
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Resolving synchronized Packages...",
                0.84f);

            UnitySyncSession.PrepareFileSyncReloadReconnect();
            SetGuestAutoRefreshBlocked(false);
            _guestPhase = GuestPhase.ResolvingPackages;
            _guestImportEarliestComplete =
                EditorApplication.timeSinceStartup + Math.Max(2d, ImportSettleSeconds);

            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Client.Resolve();
            }
            catch (Exception exception)
            {
                UnitySyncSession.ClearFileSyncReloadReconnect();
                FailGuestSync(
                    "Could not resolve synchronized Packages: " +
                    exception.Message);
            }
        }

        private static void UpdateGuestPackageResolution(UnitySyncTransport transport)
        {
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                EditorApplication.isCompiling
                    ? "Compiling synchronized Packages..."
                    : "Finishing synchronized Package import...",
                0.88f);

            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < _guestImportEarliestComplete)
            {
                return;
            }

            UnitySyncSession.ClearFileSyncReloadReconnect();
            BeginGuestManifestRequest(
                transport,
                UnitySyncFileSyncScope.Assets);
        }

        private static void BeginGuestAssetImport()
        {
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Importing synchronized Assets...",
                0.94f);

            UnitySyncSession.PrepareFileSyncReloadReconnect();
            SetGuestAutoRefreshBlocked(false);
            _guestPhase = GuestPhase.ImportingAssets;
            _guestImportEarliestComplete =
                EditorApplication.timeSinceStartup + ImportSettleSeconds;

            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ForceReimportSynchronizedDependencies();
                ForceReimportProjectMaterials();
            }
            catch (Exception exception)
            {
                UnitySyncSession.ClearFileSyncReloadReconnect();
                FailGuestSync(
                    "Could not finish importing synchronized Assets: " +
                    exception.Message);
            }
        }

        private static void ForceReimportSynchronizedDependencies()
        {
            List<string> paths = new List<string>();
            foreach (FileEntry entry in GuestAssetMismatches)
            {
                string path = entry.Path ?? string.Empty;
                if (!IsSafeSyncPath(path) ||
                    path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(path);
            }

            paths.Sort(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                AssetDatabase.ImportAsset(
                    path,
                    ImportAssetOptions.ForceUpdate |
                    ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private static void ForceReimportProjectMaterials()
        {
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Reinitializing synchronized materials...",
                0.97f);

            string[] materialGuids = AssetDatabase.FindAssets(
                "t:Material",
                new[] { "Assets" });
            Array.Sort(materialGuids, StringComparer.Ordinal);

            foreach (string guid in materialGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsSafeSyncPath(path) ||
                    !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AssetDatabase.ImportAsset(
                    path,
                    ImportAssetOptions.ForceUpdate |
                    ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private static void UpdateGuestAssetImport()
        {
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                EditorApplication.isCompiling
                    ? "Compiling synchronized scripts..."
                    : "Finishing synchronized asset import...",
                0.98f);

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _guestImportEarliestComplete =
                    EditorApplication.timeSinceStartup + ImportSettleSeconds;
                return;
            }

            if (EditorApplication.timeSinceStartup < _guestImportEarliestComplete)
            {
                return;
            }

            UnitySyncSession.ClearFileSyncReloadReconnect();
            CompleteGuestSync();
        }

        private static void CompleteGuestSync()
        {
            CleanupGuestTransfers();
            DeleteGuestTempRoot();
            SetGuestAutoRefreshBlocked(false);
            _guestPhase = GuestPhase.None;
            _guestReadyForSceneSnapshot = true;
            EditorUtility.ClearProgressBar();
        }

        private static void FailGuestSync(string error)
        {
            CleanupGuestTransfers();
            DeleteGuestTempRoot();
            SetGuestAutoRefreshBlocked(false);
            _guestPhase = GuestPhase.None;
            _guestReadyForSceneSnapshot = false;
            _guestFailure = string.IsNullOrEmpty(error)
                ? "File synchronization failed."
                : error;
            EditorUtility.ClearProgressBar();
        }

        private static void ResetGuestState()
        {
            _guestForceRestore = false;
            CleanupGuestTransfers();
            DeleteGuestTempRoot();
            SetGuestAutoRefreshBlocked(false);

            _guestPhase = GuestPhase.None;
            _guestFailure = string.Empty;
            _guestHostPlayerId = Guid.Empty;
            _guestSyncId = Guid.Empty;
            _guestRequestedScope = default(UnitySyncFileSyncScope);
            _guestExpectedFileCount = 0;
            _guestExpectedTotalBytes = 0;
            GuestManifest.Clear();
            GuestObsoletePaths.Clear();
            GuestManifestByPath.Clear();
            GuestMismatches.Clear();
            GuestPackageMismatches.Clear();
            GuestAssetMismatches.Clear();
            GuestActiveMismatches.Clear();
            GuestMismatchByPath.Clear();
            GuestHostPackageVersions.Clear();
            GuestPackageRootsToReplace.Clear();
            _guestCompareIndex = 0;
            _guestRequestIndex = 0;
            _guestCompletedFiles = 0;
            _guestDownloadTotalBytes = 0;
            _guestDownloadBytesReceived = 0;
            _guestStageDownloadTotalBytes = 0;
            _guestStageDownloadBytesReceived = 0;
            _guestDownloadKind = GuestDownloadKind.None;
            _guestAutoContinue = false;
            _guestImportEarliestComplete = 0d;
        }

        private static void CleanupGuestTransfers()
        {
            foreach (GuestTransfer transfer in GuestTransfers.Values)
            {
                if (transfer.Stream != null)
                {
                    transfer.Stream.Dispose();
                    transfer.Stream = null;
                }

                if (!string.IsNullOrEmpty(transfer.TempPath) && File.Exists(transfer.TempPath))
                {
                    try
                    {
                        File.Delete(transfer.TempPath);
                    }
                    catch (IOException)
                    {
                        // Temporary cleanup is best effort.
                    }
                }
            }

            GuestTransfers.Clear();
        }

        private static void DeleteGuestTempRoot()
        {
            if (string.IsNullOrEmpty(_guestTempRoot))
            {
                return;
            }

            try
            {
                if (Directory.Exists(_guestTempRoot))
                {
                    Directory.Delete(_guestTempRoot, true);
                }
            }
            catch (IOException)
            {
                // Temporary cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary cleanup is best effort.
            }

            _guestTempRoot = string.Empty;
        }

        private static void BeginGuestManifestRequest(
            UnitySyncTransport transport,
            UnitySyncFileSyncScope scope,
            bool sendRequest = true)
        {
            CleanupGuestTransfers();
            DeleteGuestTempRoot();
            GuestManifest.Clear();
            GuestObsoletePaths.Clear();
            GuestManifestByPath.Clear();
            GuestMismatches.Clear();
            GuestPackageMismatches.Clear();
            GuestAssetMismatches.Clear();
            GuestActiveMismatches.Clear();
            GuestMismatchByPath.Clear();
            GuestHostPackageVersions.Clear();
            GuestPackageRootsToReplace.Clear();
            _guestHostPlayerId = Guid.Empty;
            _guestSyncId = Guid.Empty;
            _guestRequestedScope = scope;
            _guestExpectedFileCount = 0;
            _guestExpectedTotalBytes = 0;
            _guestCompareIndex = 0;
            _guestRequestIndex = 0;
            _guestCompletedFiles = 0;
            _guestDownloadTotalBytes = 0;
            _guestDownloadBytesReceived = 0;
            _guestStageDownloadTotalBytes = 0;
            _guestStageDownloadBytesReceived = 0;
            _guestDownloadKind = GuestDownloadKind.None;

            SetGuestAutoRefreshBlocked(true);
            _guestPhase = GuestPhase.WaitingForManifest;
            EditorUtility.DisplayProgressBar(
                "UnitySync — Syncing Files",
                "Waiting for the host " + scope + " manifest...",
                scope == UnitySyncFileSyncScope.Packages ? 0.01f : 0.55f);
            if (sendRequest)
            {
                transport.RequestFileSync(scope);
            }
        }

        private static bool PrepareGuestPackageVersionComparison(out string error)
        {
            error = string.Empty;
            GuestPackageRootsToReplace.Clear();
            try
            {
                foreach (KeyValuePair<string, string> pair in GuestHostPackageVersions)
                {
                    if (!TryGetEmbeddedPackageRoot(
                            pair.Key,
                            out string packageRoot,
                            out string packageName))
                    {
                        continue;
                    }

                    if (!TryGetFullSyncPath(
                            packageRoot + "/package.json",
                            out string localPackageJson))
                    {
                        continue;
                    }

                    string localRoot = Path.GetDirectoryName(localPackageJson);
                    if (string.IsNullOrEmpty(localRoot) || !Directory.Exists(localRoot))
                    {
                        continue;
                    }

                    string localVersion = File.Exists(localPackageJson)
                        ? ReadPackageVersion(localPackageJson)
                        : string.Empty;
                    if (string.Equals(
                            localVersion,
                            pair.Value,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    GuestPackageRootsToReplace.Add(packageRoot);
                    UnitySyncSession.ReportPackageVersionReplacement(
                        packageName,
                        localVersion,
                        pair.Value);
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                error = "Could not compare package versions: " + exception.Message;
                return false;
            }
        }

        private static bool ApplyStagedFiles(out string error)
        {
            error = string.Empty;
            List<string> replacements = new List<string>();
            HashSet<string> obsolete = new HashSet<string>(GuestObsoletePaths, StringComparer.Ordinal);
            string backupRoot = Path.Combine(GetProjectRoot(), "Library", "UnitySyncFileBackup",
                _guestSyncId.ToString("N"));
            List<string> backedUp = new List<string>();
            List<string> installed = new List<string>();
            string currentPath = string.Empty;
            bool startedCommit = false;
            EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (FileEntry entry in GuestActiveMismatches)
                {
                    currentPath = entry.Path;
                    if (!TryGetFullSyncPath(entry.Path, out string target))
                    {
                        throw new IOException("Unsafe sync path: " + entry.Path);
                    }

                    string staged = Path.Combine(_guestTempRoot, "FileStage", entry.Path);
                    if (!File.Exists(staged) || !HashesEqual(ComputeHash(staged), entry.Hash))
                    {
                        throw new IOException("The staged file failed verification: " + entry.Path);
                    }

                    // Native plugins may be mapped into the Editor even when the surrounding
                    // package metadata changes. An identical file needs no write or deletion.
                    if (File.Exists(target) && new FileInfo(target).Length == entry.Length &&
                        HashesEqual(ComputeHash(target), entry.Hash))
                    {
                        continue;
                    }

                    replacements.Add(entry.Path);
                }

                // Probe every changed/removed file before modifying any package content.
                // Removing a UPM dependency cannot unload a native plugin from this process.
                List<string> affected = new List<string>(replacements);
                affected.AddRange(obsolete);
                foreach (string path in affected)
                {
                    currentPath = path;
                    if (!TryGetFullSyncPath(path, out string target))
                    {
                        throw new IOException("Unsafe sync path: " + path);
                    }
                    string parent = Path.GetDirectoryName(target);
                    while (!string.IsNullOrEmpty(parent) &&
                           !string.Equals(parent, GetProjectRoot(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(parent) &&
                            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new IOException("Cannot replace files through a linked directory: " + path);
                        }
                        parent = Path.GetDirectoryName(parent);
                    }
                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Cannot replace a linked file: " + path);
                    }
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                    }
                    using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        // An open/mapped native DLL fails here, before other files are removed.
                    }
                }

                startedCommit = true;
                foreach (string path in affected)
                {
                    currentPath = path;
                    TryGetFullSyncPath(path, out string target);
                    if (File.Exists(target))
                    {
                        string backup = Path.Combine(backupRoot, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup));
                        File.Move(target, backup);
                        backedUp.Add(path);
                    }
                }

                foreach (string path in replacements)
                {
                    currentPath = path;
                    TryGetFullSyncPath(path, out string target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(Path.Combine(_guestTempRoot, "FileStage", path), target);
                    installed.Add(path);
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                string rollbackError = string.Empty;
                if (startedCommit)
                {
                    try
                    {
                        foreach (string path in installed)
                        {
                            TryGetFullSyncPath(path, out string target);
                            File.Delete(target);
                        }
                        foreach (string path in backedUp)
                        {
                            TryGetFullSyncPath(path, out string target);
                            File.Move(Path.Combine(backupRoot, path), target);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackError = " Originals are preserved in " + backupRoot +
                            ". Rollback failed: " + rollbackException.Message;
                    }
                }

                error = "Could not update synchronized file " + currentPath + ": " + exception.Message +
                    (startedCommit ? " File changes were rolled back where possible." :
                        " No project file contents were changed.") +
                    " If this is a loaded native plugin, it must be replaced while Unity is closed; " +
                    "Package Manager removal cannot unload it. Update the guest SDK/package to the " +
                    "host version before reconnecting." + rollbackError;
                return false;
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }

            // Remove emptied guest-only folders too, otherwise Unity recreates their .meta files.
            HashSet<string> obsoleteDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in obsolete)
            {
                string directory = path;
                while (directory.LastIndexOf('/') > 0)
                {
                    directory = directory.Substring(0, directory.LastIndexOf('/'));
                    if (directory == "Assets" || directory == "Packages")
                    {
                        break;
                    }
                    obsoleteDirectories.Add(directory);
                }
            }
            List<string> cleanupDirectories = new List<string>(obsoleteDirectories);
            cleanupDirectories.Sort((left, right) => right.Length.CompareTo(left.Length));
            foreach (string directory in cleanupDirectories)
            {
                if (GuestManifestByPath.ContainsKey(directory + ".meta") ||
                    !TryGetFullSyncPath(directory + "/placeholder", out string placeholder))
                {
                    continue;
                }
                string fullDirectory = Path.GetDirectoryName(placeholder);
                try
                {
                    if (Directory.Exists(fullDirectory) &&
                        (File.GetAttributes(fullDirectory) & FileAttributes.ReparsePoint) == 0 &&
                        Directory.GetFileSystemEntries(fullDirectory).Length == 0)
                    {
                        Directory.Delete(fullDirectory);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            try
            {
                if (Directory.Exists(backupRoot))
                {
                    Directory.Delete(backupRoot, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        }

        private static bool IsUnderPackageRootToReplace(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/');
            foreach (string root in GuestPackageRootsToReplace)
            {
                if (normalized.StartsWith(
                        root + "/",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEmbeddedPackageJsonPath(string path)
        {
            return TryGetEmbeddedPackageRoot(
                path,
                out _,
                out _);
        }

        private static bool TryGetEmbeddedPackageRoot(
            string path,
            out string packageRoot,
            out string packageName)
        {
            packageRoot = string.Empty;
            packageName = string.Empty;
            string normalized = (path ?? string.Empty).Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (segments.Length != 3 ||
                !string.Equals(segments[0], "Packages", StringComparison.Ordinal) ||
                !string.Equals(
                    segments[2],
                    "package.json",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(segments[1]))
            {
                return false;
            }

            packageName = segments[1];
            packageRoot = "Packages/" + packageName;
            return true;
        }

        private static string ReadPackageVersion(string fullPath)
        {
            try
            {
                string json = File.ReadAllText(fullPath);
                PackageJsonData data = JsonUtility.FromJson<PackageJsonData>(json);
                return data != null && !string.IsNullOrEmpty(data.version)
                    ? data.version.Trim()
                    : string.Empty;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException)
            {
                return string.Empty;
            }
        }

        private static int CompareManifestEntries(
            FileEntry left,
            FileEntry right)
        {
            if (_guestRequestedScope == UnitySyncFileSyncScope.Packages)
            {
                int groupCompare =
                    GetPackageManifestSortGroup(left.Path).CompareTo(
                        GetPackageManifestSortGroup(right.Path));
                if (groupCompare != 0)
                {
                    return groupCompare;
                }
            }

            return StringComparer.Ordinal.Compare(left.Path, right.Path);
        }

        private static bool PathMatchesScope(
            string path,
            UnitySyncFileSyncScope scope)
        {
            if (scope == UnitySyncFileSyncScope.Project)
            {
                return IsSafeSyncPath(path);
            }

            return scope == UnitySyncFileSyncScope.Packages
                ? IsPackagePath(path)
                : scope == UnitySyncFileSyncScope.Assets &&
                  IsAssetPath(path);
        }

        private static void SetGuestAutoRefreshBlocked(bool blocked)
        {
            if (_guestAutoRefreshBlocked == blocked)
            {
                return;
            }

            if (blocked)
            {
                AssetDatabase.DisallowAutoRefresh();
            }
            else
            {
                AssetDatabase.AllowAutoRefresh();
            }

            _guestAutoRefreshBlocked = blocked;
        }

        private static List<FileEntry> BuildLocalManifest(
            UnitySyncFileSyncScope scope,
            out long totalBytes)
        {
            totalBytes = 0;
            List<string> files = new List<string>();
            if (scope == UnitySyncFileSyncScope.Project)
            {
                foreach (string root in RestoreRoots)
                {
                    string fullRoot = Path.Combine(GetProjectRoot(), root);
                    if (Directory.Exists(fullRoot) &&
                        (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Cannot restore linked project root: " + root);
                    }
                    EnumerateSyncRoot(fullRoot, root, false, files);
                }
            }
            else if (scope == UnitySyncFileSyncScope.Packages)
            {
                EnumerateSyncRoot(
                    Path.GetFullPath(Path.Combine(GetProjectRoot(), "Packages")),
                    "Packages",
                    false,
                    files);
            }
            else if (scope == UnitySyncFileSyncScope.Assets)
            {
                EnumerateSyncRoot(
                    Path.GetFullPath(Application.dataPath),
                    "Assets",
                    true,
                    files);
            }
            else
            {
                throw new InvalidDataException("Invalid file sync scope.");
            }

            files.Sort((left, right) =>
            {
                if (scope == UnitySyncFileSyncScope.Packages)
                {
                    int groupCompare =
                        GetPackageManifestSortGroup(left).CompareTo(
                            GetPackageManifestSortGroup(right));
                    if (groupCompare != 0)
                    {
                        return groupCompare;
                    }
                }

                return StringComparer.Ordinal.Compare(left, right);
            });

            List<FileEntry> entries = new List<FileEntry>(files.Count);
            foreach (string projectPath in files)
            {
                if (!TryGetFullSyncPath(projectPath, out string fullPath))
                {
                    continue;
                }

                FileInfo info = new FileInfo(fullPath);
                FileEntry entry = new FileEntry
                {
                    Path = projectPath,
                    PackageVersion =
                        scope == UnitySyncFileSyncScope.Packages &&
                        IsEmbeddedPackageJsonPath(projectPath)
                            ? ReadPackageVersion(fullPath)
                            : string.Empty,
                    Length = info.Length,
                    Hash = ComputeHash(fullPath)
                };
                entries.Add(entry);
                totalBytes += entry.Length;
            }

            return entries;
        }

        private static int GetPackageManifestSortGroup(string path)
        {
            if (string.Equals(path, "Packages/manifest.json", StringComparison.Ordinal))
            {
                return 0;
            }

            if (string.Equals(path, "Packages/packages-lock.json", StringComparison.Ordinal))
            {
                return 1;
            }

            return IsEmbeddedPackageJsonPath(path) ? 2 : 3;
        }

        private static void EnumerateSyncRoot(
            string root,
            string rootName,
            bool excludeGeneratedUdon,
            List<string> result)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            Stack<string> directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                foreach (string childDirectory in Directory.GetDirectories(directory))
                {
                    DirectoryInfo info = new DirectoryInfo(childDirectory);
                    if ((excludeGeneratedUdon &&
                         string.Equals(
                             info.Name,
                             ExcludedFolderName,
                             StringComparison.OrdinalIgnoreCase)) ||
                        (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    directories.Push(childDirectory);
                }

                foreach (string file in Directory.GetFiles(directory))
                {
                    FileInfo info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        (excludeGeneratedUdon &&
                         string.Equals(
                             info.Name,
                             ExcludedFolderName + ".meta",
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string relative = file.Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    string projectPath = rootName + "/" + relative;
                    if (IsSafeSyncPath(projectPath))
                    {
                        result.Add(projectPath);
                    }
                }
            }
        }

        private static bool IsAssetPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal);
        }

        private static bool IsPackagePath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal);
        }

        private static bool IsSafeSyncPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                return false;
            }

            string normalized = projectPath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (segments.Length < 2 ||
                (segments[0] != "Assets" && segments[0] != "Packages" &&
                 segments[0] != "ProjectSettings"))
            {
                return false;
            }

            for (int index = 1; index < segments.Length; index++)
            {
                string segment = segments[index];
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    segment.IndexOf(':') >= 0 || segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.EndsWith(".", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetFullSyncPath(string projectPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (!IsSafeSyncPath(projectPath))
            {
                return false;
            }

            string projectRoot = GetProjectRoot();
            string candidate = Path.GetFullPath(Path.Combine(
                projectRoot,
                projectPath.Replace('/', Path.DirectorySeparatorChar)));
            string rootName = projectPath.Replace('\\', '/').Split('/')[0];
            string allowedRoot = Path.GetFullPath(Path.Combine(projectRoot, rootName));
            string allowedPrefix = allowedRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(allowedPrefix, comparison))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }

        private static string GetProjectRoot()
        {
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            return parent != null ? parent.FullName : Application.dataPath;
        }

        private static byte[] ComputeHash(string fullPath)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                return sha256.ComputeHash(stream);
            }
        }

        private static byte[] CopyHash(byte[] hash)
        {
            byte[] copy = new byte[hash.Length];
            Buffer.BlockCopy(hash, 0, copy, 0, hash.Length);
            return copy;
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
    }
}
