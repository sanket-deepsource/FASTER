﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FASTER.core
{
    /// <summary>
    /// Log commit manager for a generic IDevice
    /// </summary>
    public class DeviceLogCommitCheckpointManager : ILogCommitManager, ICheckpointManager
    {
        const int indexTokenCount = 2;
        const int logTokenCount = 1;
        const int flogCommitCount = 1;

        private readonly INamedDeviceFactory deviceFactory;
        private readonly ICheckpointNamingScheme checkpointNamingScheme;
        private readonly SemaphoreSlim semaphore;

        private readonly bool removeOutdated;
        private SectorAlignedBufferPool bufferPool;

        /// <summary>
        /// Track historical commits for automatic purging
        /// </summary>
        private readonly Guid[] indexTokenHistory, logTokenHistory;
        private readonly long[] flogCommitHistory;
        private int indexTokenHistoryOffset, logTokenHistoryOffset, flogCommitHistoryOffset;

        /// <summary>
        /// Create new instance of log commit manager
        /// </summary>
        /// <param name="deviceFactory">Factory for getting devices</param>
        /// <param name="checkpointNamingScheme">Checkpoint naming helper</param>
        /// <param name="removeOutdated">Remote older FASTER log commits</param>
        public DeviceLogCommitCheckpointManager(INamedDeviceFactory deviceFactory, ICheckpointNamingScheme checkpointNamingScheme, bool removeOutdated = true)
        {
            this.deviceFactory = deviceFactory;
            this.checkpointNamingScheme = checkpointNamingScheme;

            this.semaphore = new SemaphoreSlim(0);

            this.removeOutdated = removeOutdated;
            if (removeOutdated)
            {
                // We keep two index checkpoints as the latest index might not have a
                // later log checkpoint to work with
                indexTokenHistory = new Guid[2];
                // We only keep the latest log checkpoint
                logTokenHistory = new Guid[2];
                // // We only keep the latest FasterLog commit
                flogCommitHistory = new long[2];
            }
            deviceFactory.Initialize(checkpointNamingScheme.BaseName());
        }

        /// <inheritdoc />
        public void PurgeAll()
        {
            deviceFactory.Delete(new FileDescriptor { directoryName = "" });
        }

        /// <inheritdoc />
        public void Purge(Guid token)
        {
            // Try both because we do not know which type the guid denotes
            deviceFactory.Delete(checkpointNamingScheme.LogCheckpointBase(token));
            deviceFactory.Delete(checkpointNamingScheme.IndexCheckpointBase(token));
        }

        /// <summary>
        /// Create new instance of log commit manager
        /// </summary>
        /// <param name="deviceFactory">Factory for getting devices</param>
        /// <param name="baseName">Overall location specifier (e.g., local path or cloud container name)</param>
        /// <param name="removeOutdated">Remote older FASTER log commits</param>
        public DeviceLogCommitCheckpointManager(INamedDeviceFactory deviceFactory, string baseName, bool removeOutdated = false)
            : this(deviceFactory, new DefaultCheckpointNamingScheme(baseName), removeOutdated)
        { }

        #region ILogCommitManager

        /// <inheritdoc />
        public unsafe void Commit(long beginAddress, long untilAddress, byte[] commitMetadata, long commitNum)
        {
            using var device = deviceFactory.Get(checkpointNamingScheme.FasterLogCommitMetadata(commitNum));

            // Two phase to ensure we write metadata in single Write operation
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(commitMetadata.Length);
            writer.Write(commitMetadata);

            WriteInto(device, 0, ms.ToArray(), (int)ms.Position);

            if (removeOutdated)
            {
                var prior = flogCommitHistory[flogCommitHistoryOffset];
                flogCommitHistory[flogCommitHistoryOffset] = commitNum;
                flogCommitHistoryOffset = (flogCommitHistoryOffset + 1) % flogCommitCount;
                if (prior != default)
                    deviceFactory.Delete(checkpointNamingScheme.FasterLogCommitMetadata(prior));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public IEnumerable<long> ListCommits()
        {
            return deviceFactory.ListContents(checkpointNamingScheme.FasterLogCommitBasePath()).Select(e => checkpointNamingScheme.CommitNumber(e)).OrderByDescending(e => e);
        }

        /// <inheritdoc />
        public void RemoveCommit(long commitNum)
        {
            deviceFactory.Delete(checkpointNamingScheme.FasterLogCommitMetadata(commitNum));
        }

        /// <inheritdoc />
        public void RemoveAllCommits()
        {
            foreach (var commitNum in ListCommits())
                RemoveCommit(commitNum);
        }

        /// <inheritdoc />
        public byte[] GetCommitMetadata(long commitNum)
        {
            using var device = deviceFactory.Get(checkpointNamingScheme.FasterLogCommitMetadata(commitNum));

            ReadInto(device, 0, out byte[] writePad, sizeof(int));
            int size = BitConverter.ToInt32(writePad, 0);

            byte[] body;
            if (writePad.Length >= size + sizeof(int))
                body = writePad;
            else
                ReadInto(device, 0, out body, size + sizeof(int));

            return new Span<byte>(body).Slice(sizeof(int)).ToArray();
        }
        #endregion

        #region ICheckpointManager
        /// <inheritdoc />
        public unsafe void CommitIndexCheckpoint(Guid indexToken, byte[] commitMetadata)
        {
            var device = NextIndexCheckpointDevice(indexToken);

            // Two phase to ensure we write metadata in single Write operation
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(commitMetadata.Length);
            writer.Write(commitMetadata);

            WriteInto(device, 0, ms.ToArray(), (int)ms.Position);
            device.Dispose();

            if (removeOutdated)
            {
                var prior = indexTokenHistory[indexTokenHistoryOffset];
                indexTokenHistory[indexTokenHistoryOffset] = indexToken;
                indexTokenHistoryOffset = (indexTokenHistoryOffset + 1) % indexTokenCount;
                if (prior != default)
                    deviceFactory.Delete(checkpointNamingScheme.IndexCheckpointBase(prior));
            }
        }

        /// <inheritdoc />
        public IEnumerable<Guid> GetIndexCheckpointTokens()
        {
            return deviceFactory.ListContents(checkpointNamingScheme.IndexCheckpointBasePath()).Select(e => checkpointNamingScheme.Token(e));
        }

        /// <inheritdoc />
        public byte[] GetIndexCheckpointMetadata(Guid indexToken)
        {
            var device = deviceFactory.Get(checkpointNamingScheme.IndexCheckpointMetadata(indexToken));

            ReadInto(device, 0, out byte[] writePad, sizeof(int));
            int size = BitConverter.ToInt32(writePad, 0);

            byte[] body;
            if (writePad.Length >= size + sizeof(int))
                body = writePad;
            else
                ReadInto(device, 0, out body, size + sizeof(int));
            device.Dispose();
            return new Span<byte>(body).Slice(sizeof(int)).ToArray();
        }

        /// <inheritdoc />
        public unsafe void CommitLogCheckpoint(Guid logToken, byte[] commitMetadata)
        {
            var device = NextLogCheckpointDevice(logToken);

            // Two phase to ensure we write metadata in single Write operation
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(commitMetadata.Length);
            writer.Write(commitMetadata);

            WriteInto(device, 0, ms.ToArray(), (int)ms.Position);
            device.Dispose();

            if (removeOutdated)
            {
                var prior = logTokenHistory[logTokenHistoryOffset];
                logTokenHistory[logTokenHistoryOffset] = logToken;
                logTokenHistoryOffset = (logTokenHistoryOffset + 1) % logTokenCount;
                if (prior != default)
                    deviceFactory.Delete(checkpointNamingScheme.LogCheckpointBase(prior));
            }
        }

        /// <inheritdoc />
        public unsafe void CommitLogIncrementalCheckpoint(Guid logToken, long version, byte[] commitMetadata, DeltaLog deltaLog)
        {
            deltaLog.Allocate(out int length, out long physicalAddress);
            if (length < commitMetadata.Length)
            {
                deltaLog.Seal(0, DeltaLogEntryType.CHECKPOINT_METADATA);
                deltaLog.Allocate(out length, out physicalAddress);
                if (length < commitMetadata.Length)
                {
                    deltaLog.Seal(0);
                    throw new Exception($"Metadata of size {commitMetadata.Length} does not fit in delta log space of size {length}");
                }
            }
            fixed (byte* ptr = commitMetadata)
            {
                Buffer.MemoryCopy(ptr, (void*)physicalAddress, commitMetadata.Length, commitMetadata.Length);
            }
            deltaLog.Seal(commitMetadata.Length, DeltaLogEntryType.CHECKPOINT_METADATA);
            deltaLog.FlushAsync().Wait();
        }

        /// <inheritdoc />
        public IEnumerable<Guid> GetLogCheckpointTokens()
        {
            return deviceFactory.ListContents(checkpointNamingScheme.LogCheckpointBasePath()).Select(e => checkpointNamingScheme.Token(e));
        }

        /// <inheritdoc />
        public byte[] GetLogCheckpointMetadata(Guid logToken, DeltaLog deltaLog, bool scanDelta, long recoverTo)
        {
            byte[] metadata = null;
            if (deltaLog != null && scanDelta)
            {
                // Try to get latest valid metadata from delta-log
                deltaLog.Reset();
                while (deltaLog.GetNext(out long physicalAddress, out int entryLength, out var type))
                {
                    switch (type)
                    {
                        case DeltaLogEntryType.DELTA:
                            // consider only metadata records
                            continue;
                        case DeltaLogEntryType.CHECKPOINT_METADATA:
                            metadata = new byte[entryLength];
                            unsafe
                            {
                                fixed (byte* m = metadata)
                                    Buffer.MemoryCopy((void*)physicalAddress, m, entryLength, entryLength);
                            }
                            HybridLogRecoveryInfo recoveryInfo = new();
                            using (StreamReader s = new(new MemoryStream(metadata)))
                            {
                                recoveryInfo.Initialize(s);
                                // Finish recovery if only specific versions are requested
                                if (recoveryInfo.version == recoverTo || recoveryInfo.version < recoverTo && recoveryInfo.nextVersion > recoverTo) goto LoopEnd;
                            }
                            continue;
                        default:
                            throw new FasterException("Unexpected entry type");
                    }
                LoopEnd:
                    break;
                }
                if (metadata != null) return metadata;

            }

            var device = deviceFactory.Get(checkpointNamingScheme.LogCheckpointMetadata(logToken));

            ReadInto(device, 0, out byte[] writePad, sizeof(int));
            int size = BitConverter.ToInt32(writePad, 0);

            byte[] body;
            if (writePad.Length >= size + sizeof(int))
                body = writePad;
            else
                ReadInto(device, 0, out body, size + sizeof(int));
            device.Dispose();
            return body.AsSpan().Slice(sizeof(int), size).ToArray();
        }

        /// <inheritdoc />
        public IDevice GetIndexDevice(Guid indexToken)
        {
            return deviceFactory.Get(checkpointNamingScheme.HashTable(indexToken));
        }

        /// <inheritdoc />
        public IDevice GetSnapshotLogDevice(Guid token)
        {
            return deviceFactory.Get(checkpointNamingScheme.LogSnapshot(token));
        }

        /// <inheritdoc />
        public IDevice GetSnapshotObjectLogDevice(Guid token)
        {
            return deviceFactory.Get(checkpointNamingScheme.ObjectLogSnapshot(token));
        }

        /// <inheritdoc />
        public IDevice GetDeltaLogDevice(Guid token)
        {
            return deviceFactory.Get(checkpointNamingScheme.DeltaLog(token));
        }

        /// <inheritdoc />
        public void InitializeIndexCheckpoint(Guid indexToken)
        {
        }

        /// <inheritdoc />
        public void InitializeLogCheckpoint(Guid logToken)
        {
        }

        /// <inheritdoc />
        public void OnRecovery(Guid indexToken, Guid logToken)
        {
            if (!removeOutdated) return;

            // Add recovered tokens to history, for eventual purging
            if (indexToken != default)
            {
                indexTokenHistory[indexTokenHistoryOffset] = indexToken;
                indexTokenHistoryOffset = (indexTokenHistoryOffset + 1) % indexTokenCount;
            }
            if (logToken != default)
            {
                logTokenHistory[logTokenHistoryOffset] = logToken;
                logTokenHistoryOffset = (logTokenHistoryOffset + 1) % logTokenCount;
            }

            // Purge all log checkpoints that were not used for recovery
            foreach (var recoveredLogToken in GetLogCheckpointTokens())
            {
                if (recoveredLogToken != logToken)
                    deviceFactory.Delete(checkpointNamingScheme.LogCheckpointBase(recoveredLogToken));
            }

            // Purge all index checkpoints that were not used for recovery
            foreach (var recoveredIndexToken in GetIndexCheckpointTokens())
            {
                if (recoveredIndexToken != indexToken)
                    deviceFactory.Delete(checkpointNamingScheme.IndexCheckpointBase(recoveredIndexToken));
            }
        }

        /// <inheritdoc />
        public void OnRecovery(long commitNum, bool purgeEarlierCommits)
        {
            if (removeOutdated)
            {
                foreach (var recoveredCommitNum in ListCommits())
                    if (recoveredCommitNum != commitNum) RemoveCommit(recoveredCommitNum);

                // Add recovered tokens to history, for eventual purging
                if (commitNum != default)
                {
                    flogCommitHistory[flogCommitHistoryOffset] = commitNum;
                    flogCommitHistoryOffset = (flogCommitHistoryOffset + 1) % flogCommitCount;
                }
            }
            else if (purgeEarlierCommits)
            {
                foreach (var recoveredCommitNum in ListCommits())
                    if (recoveredCommitNum < commitNum) RemoveCommit(recoveredCommitNum);
            }
        }

        private IDevice NextIndexCheckpointDevice(Guid token)
            => deviceFactory.Get(checkpointNamingScheme.IndexCheckpointMetadata(token));

        private IDevice NextLogCheckpointDevice(Guid token)
            => deviceFactory.Get(checkpointNamingScheme.LogCheckpointMetadata(token));
        #endregion

        private unsafe void IOCallback(uint errorCode, uint numBytes, object context)
        {
            if (errorCode != 0)
            {
                Trace.TraceError("OverlappedStream GetQueuedCompletionStatus error: {0}", errorCode);
            }
            semaphore.Release();
        }

        /// <summary>
        /// Note: will read potentially more data (based on sector alignment)
        /// </summary>
        /// <param name="device"></param>
        /// <param name="address"></param>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private unsafe void ReadInto(IDevice device, ulong address, out byte[] buffer, int size)
        {
            if (bufferPool == null)
                bufferPool = new SectorAlignedBufferPool(1, (int)device.SectorSize);

            long numBytesToRead = size;
            numBytesToRead = ((numBytesToRead + (device.SectorSize - 1)) & ~(device.SectorSize - 1));

            var pbuffer = bufferPool.Get((int)numBytesToRead);
            device.ReadAsync(address, (IntPtr)pbuffer.aligned_pointer,
                (uint)numBytesToRead, IOCallback, null);
            semaphore.Wait();

            buffer = new byte[numBytesToRead];
            fixed (byte* bufferRaw = buffer)
                Buffer.MemoryCopy(pbuffer.aligned_pointer, bufferRaw, numBytesToRead, numBytesToRead);
            pbuffer.Return();
        }

        /// <summary>
        /// Note: pads the bytes with zeros to achieve sector alignment
        /// </summary>
        /// <param name="device"></param>
        /// <param name="address"></param>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private unsafe void WriteInto(IDevice device, ulong address, byte[] buffer, int size)
        {
            if (bufferPool == null)
                bufferPool = new SectorAlignedBufferPool(1, (int)device.SectorSize);

            long numBytesToWrite = size;
            numBytesToWrite = ((numBytesToWrite + (device.SectorSize - 1)) & ~(device.SectorSize - 1));

            var pbuffer = bufferPool.Get((int)numBytesToWrite);
            fixed (byte* bufferRaw = buffer)
            {
                Buffer.MemoryCopy(bufferRaw, pbuffer.aligned_pointer, size, size);
            }

            device.WriteAsync((IntPtr)pbuffer.aligned_pointer, address, (uint)numBytesToWrite, IOCallback, null);
            semaphore.Wait();

            pbuffer.Return();
        }
    }
}