﻿using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace Thruster
{
    public class FastMemoryPool<T> : MemoryPool<T>
    {
        const int ChunkSize = 4 * 1024;
        const int LeasingOffset = Util.CacheLineSize / 8;

        readonly T[] memory;
        readonly int processorCount;
        readonly long[] leasing;

        bool disposed;

        public FastMemoryPool()
        {
            var count = Math.Min(Environment.ProcessorCount, 64);
            var chunkCount = count * 64;
            var allocSize = chunkCount * ChunkSize;
            
            leasing = new long[(count + 2) * LeasingOffset];

            memory = new T[allocSize];

            processorCount = count;
        }

        public override IMemoryOwner<T> Rent(int size = -1)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("MemoryPool has been disposed.");
            }

            if (size <= 0)
            {
                size = 1;
            }

            var capacity = size.AlignToMultipleOf(ChunkSize);
            var chunkCount = capacity / ChunkSize;

            var processorId = GetProcessorId();

            //try local processor first
            var owner = Lease(processorId, chunkCount, capacity);
            if (owner != null)
            {
                return owner;
            }

            return LeaseSlowPath(processorId, chunkCount, capacity);
        }

        Owner Lease(int processorId, int chunkCount, int capacity)
        {
            var index = (short)((processorId + 1) * LeasingOffset);
            ref var slot = ref leasing[index];

            var lease = Leasing.Lease(ref slot, chunkCount, 3);
            if (lease >= 0)
            {
                var offset = processorId * 64 + lease;
                return new Owner(new Memory<T>(memory, offset, capacity), index, lease, leasing);
            }

            return default;
        }

        IMemoryOwner<T> LeaseSlowPath(int processorId, int chunkCount, int capacity)
        {
            var spin = new SpinWait();
            for (var i = 0; i < processorCount; i++)
            {
                spin.SpinOnce();
                processorId = (processorId + i) % processorCount;

                var owner = Lease(processorId, chunkCount, capacity);
                if (owner != null)
                {
                    return owner;
                }
            }

            // allocate if none is found
            return new Owner(new Memory<T>(new T[capacity]), 0, 0, null);
        }

        public override int MaxBufferSize => 32 * ChunkSize; // half of the max is provided

        protected override void Dispose(bool disposing)
        {
            disposed = true;
        }

        class Owner : IMemoryOwner<T>
        {
            readonly short leasingIndex;
            readonly short lease;
            readonly long[] leasing;

            public Owner(Memory<T> memory, short leasingIndex, short lease, long[] leasing)
            {
                Memory = memory;
                this.leasingIndex = leasingIndex;
                this.lease = lease;
                this.leasing = leasing;
            }

            public void Dispose()
            {
                if (leasing != null)
                {
                    Leasing.Release(ref leasing[leasingIndex], Memory.Length / ChunkSize, lease);
                }
            }

            public Memory<T> Memory { get; }
        }

        int GetProcessorId()
        {
            int processorId;
#if NETCOREAPP2_1
            processorId = Thread.GetCurrentProcessorId();
            if (processorId < 0)
            {
                processorId = Environment.CurrentManagedThreadId;
            }
#else
            processorId = Environment.CurrentManagedThreadId;
#endif
            // Add offset to make it clear that it is not guaranteed to be 0-based processor number 
            processorId += 100;

            return processorId % processorCount;
        }
    }

}