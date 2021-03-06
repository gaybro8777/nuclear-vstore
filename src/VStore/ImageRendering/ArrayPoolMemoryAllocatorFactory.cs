﻿using SixLabors.Memory;

namespace NuClear.VStore.ImageRendering
{
    public static class ArrayPoolMemoryAllocatorFactory
    {
        /// <summary>
        /// Similar to <see cref="ArrayPoolMemoryAllocator.CreateWithModeratePooling"/> option, but with one bucket per every pool only
        /// </summary>
        /// <returns>The memory manager</returns>
        public static MemoryAllocator CreateWithLimitedSmallPooling()
        {
            return new ArrayPoolMemoryAllocator(350 * 350 * 4, 1, 1, 1);
        }

        /// <summary>
        /// Similar to <see cref="ArrayPoolMemoryAllocator.CreateWithAggressivePooling"/> option, but with less amount of buckets per every pool
        /// </summary>
        /// <returns>The memory manager</returns>
        public static MemoryAllocator CreateWithLimitedLargePooling()
        {
            return new ArrayPoolMemoryAllocator(32 * 1024 * 1024 * 4, 8 * 1024 * 1024 * 4, 2, 4);
        }
    }
}