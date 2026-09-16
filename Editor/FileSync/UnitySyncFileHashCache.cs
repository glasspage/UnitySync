using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Glasspage.UnitySync
{
    internal static class UnitySyncFileHashCache
    {
        private sealed class CacheEntry
        {
            internal long Length;
            internal long LastWriteTicks;
            internal ulong Hash;
        }

        private const int CacheMagic = 0x55485348;
        private const int CacheVersion = 1;
        private const int MaximumCacheEntries = 1000000;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Entries =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        private static string _loadedProjectRoot = string.Empty;
        private static bool _dirty;

        internal static ulong GetOrCompute(
            string projectRoot,
            string projectPath,
            string fullPath,
            CancellationToken cancellationToken,
            out long length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo before = new FileInfo(fullPath);
            before.Refresh();
            if (!before.Exists)
            {
                throw new FileNotFoundException(
                    "File disappeared before it could be hashed.",
                    fullPath);
            }

            long beforeLength = before.Length;
            long beforeLastWriteTicks = before.LastWriteTimeUtc.Ticks;
            string key = NormalizeProjectPath(projectPath);

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                if (Entries.TryGetValue(key, out CacheEntry cached) &&
                    cached.Length == beforeLength &&
                    cached.LastWriteTicks == beforeLastWriteTicks)
                {
                    length = cached.Length;
                    return cached.Hash;
                }
            }

            ulong hash = UnitySyncXxHash64.ComputeFile(
                fullPath,
                cancellationToken,
                out long hashedLength);

            FileInfo after = new FileInfo(fullPath);
            after.Refresh();
            if (!after.Exists ||
                hashedLength != beforeLength ||
                after.Length != beforeLength ||
                after.LastWriteTimeUtc.Ticks != beforeLastWriteTicks)
            {
                throw new IOException(
                    "File changed while its cached hash was being generated.");
            }

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                Entries[key] = new CacheEntry
                {
                    Length = hashedLength,
                    LastWriteTicks = beforeLastWriteTicks,
                    Hash = hash
                };
                _dirty = true;
            }

            length = hashedLength;
            return hash;
        }

        internal static ulong GetOrCompute(
            string projectRoot,
            string projectPath,
            string fullPath,
            out long length)
        {
            return GetOrCompute(
                projectRoot,
                projectPath,
                fullPath,
                CancellationToken.None,
                out length);
        }

        internal static bool TryGetCachedHash(
            string projectRoot,
            string projectPath,
            string fullPath,
            out long length,
            out ulong hash)
        {
            length = 0;
            hash = 0UL;

            FileInfo info = new FileInfo(fullPath);
            info.Refresh();
            if (!info.Exists)
            {
                return false;
            }

            length = info.Length;
            long lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            string key = NormalizeProjectPath(projectPath);

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                if (!Entries.TryGetValue(key, out CacheEntry cached) ||
                    cached.Length != length ||
                    cached.LastWriteTicks != lastWriteTicks)
                {
                    return false;
                }

                hash = cached.Hash;
                return true;
            }
        }

        internal static bool MatchesFile(
            string projectRoot,
            string projectPath,
            string fullPath,
            long expectedLength,
            ulong expectedHash)
        {
            FileInfo info = new FileInfo(fullPath);
            info.Refresh();
            if (!info.Exists || info.Length != expectedLength)
            {
                return false;
            }

            ulong actualHash = GetOrCompute(
                projectRoot,
                projectPath,
                fullPath,
                CancellationToken.None,
                out long actualLength);
            return actualLength == expectedLength && actualHash == expectedHash;
        }

        internal static void Invalidate(string projectRoot, string projectPath)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(projectPath))
            {
                return;
            }

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                if (Entries.Remove(NormalizeProjectPath(projectPath)))
                {
                    _dirty = true;
                }
            }
        }

        internal static void RecordVerifiedFile(
            string projectRoot,
            string projectPath,
            string fullPath,
            long expectedLength,
            ulong hash)
        {
            if (string.IsNullOrEmpty(projectRoot) ||
                string.IsNullOrEmpty(projectPath) ||
                string.IsNullOrEmpty(fullPath))
            {
                return;
            }

            FileInfo info = new FileInfo(fullPath);
            info.Refresh();
            if (!info.Exists || info.Length != expectedLength)
            {
                Invalidate(projectRoot, projectPath);
                return;
            }

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                Entries[NormalizeProjectPath(projectPath)] = new CacheEntry
                {
                    Length = expectedLength,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                    Hash = hash
                };
                _dirty = true;
            }
        }

        internal static void SaveIfDirty(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return;
            }

            lock (CacheLock)
            {
                EnsureLoadedLocked(projectRoot);
                TrySaveLocked();
            }
        }

        private static void EnsureLoadedLocked(string projectRoot)
        {
            string normalizedRoot = NormalizeProjectRoot(projectRoot);
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(_loadedProjectRoot, normalizedRoot, comparison))
            {
                return;
            }

            if (_dirty && !string.IsNullOrEmpty(_loadedProjectRoot))
            {
                TrySaveLocked();
            }

            Entries.Clear();
            _loadedProjectRoot = normalizedRoot;
            _dirty = false;
            TryLoadLocked();
        }

        private static void TryLoadLocked()
        {
            string cachePath = GetCachePath(_loadedProjectRoot);
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
            {
                return;
            }

            try
            {
                using (FileStream stream = new FileStream(
                           cachePath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != CacheMagic ||
                        reader.ReadInt32() != CacheVersion)
                    {
                        return;
                    }

                    int count = reader.ReadInt32();
                    if (count < 0 || count > MaximumCacheEntries)
                    {
                        throw new InvalidDataException(
                            "Invalid UnitySync hash cache entry count.");
                    }

                    for (int index = 0; index < count; index++)
                    {
                        string path = NormalizeProjectPath(reader.ReadString());
                        long length = reader.ReadInt64();
                        long lastWriteTicks = reader.ReadInt64();
                        ulong hash = reader.ReadUInt64();
                        if (string.IsNullOrEmpty(path) ||
                            length < 0 ||
                            lastWriteTicks < 0)
                        {
                            throw new InvalidDataException(
                                "Invalid UnitySync hash cache entry.");
                        }

                        Entries[path] = new CacheEntry
                        {
                            Length = length,
                            LastWriteTicks = lastWriteTicks,
                            Hash = hash
                        };
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException(
                            "Unexpected trailing UnitySync hash cache data.");
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is EndOfStreamException ||
                exception is InvalidDataException ||
                exception is FormatException)
            {
                Entries.Clear();
                try
                {
                    File.Delete(cachePath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void TrySaveLocked()
        {
            if (!_dirty || string.IsNullOrEmpty(_loadedProjectRoot))
            {
                return;
            }

            string cachePath = GetCachePath(_loadedProjectRoot);
            string directory = Path.GetDirectoryName(cachePath);
            string tempPath = cachePath + ".tmp";

            try
            {
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(
                           tempPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(CacheMagic);
                    writer.Write(CacheVersion);
                    writer.Write(Entries.Count);
                    foreach (KeyValuePair<string, CacheEntry> pair in Entries)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Length);
                        writer.Write(pair.Value.LastWriteTicks);
                        writer.Write(pair.Value.Hash);
                    }
                }

                File.Copy(tempPath, cachePath, true);
                File.Delete(tempPath);
                _dirty = false;
            }
            catch (IOException)
            {
                TryDeleteTempFile(tempPath);
            }
            catch (UnauthorizedAccessException)
            {
                TryDeleteTempFile(tempPath);
            }
        }

        private static void TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string GetCachePath(string projectRoot)
        {
            return string.IsNullOrEmpty(projectRoot)
                ? string.Empty
                : Path.Combine(
                    projectRoot,
                    "Library",
                    "UnitySync",
                    "FileHashCache-v1.bin");
        }

        private static string NormalizeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return string.Empty;
            }

            return Path.GetFullPath(projectRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeProjectPath(string projectPath)
        {
            return (projectPath ?? string.Empty).Replace('\\', '/');
        }
    }
}
