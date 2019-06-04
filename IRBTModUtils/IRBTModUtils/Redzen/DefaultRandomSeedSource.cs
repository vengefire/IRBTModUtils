﻿using System;
using System.Security.Cryptography;
using System.Threading;

namespace us.frostraptor.modUtils.Redzen {
    /// <summary>
    /// Default implementation of IRandomSeedSource.
    /// A source of seed values for use by pseudo-random number generators (PRNGs).
    /// </summary>
    /// <remarks>
    /// This implementation uses multiple seed PRNGs initialised with high quality crypto random seed state. 
    /// 
    /// New seeds are generated by rotating through the seed PRNGs to generate seed values. Using multiple seed PRNGs
    /// in this way (A) increases the state space that is being sampled from, (B) improves thread concurrency by 
    /// allowing each PRNG to be in use concurrently, and (C) greatly improves performance compared to using
    /// a crypto random source for all PRNGs. I.e. this class is a compromise between using perfectly random 
    /// (crypto) seed data, versus using pseudo-random data but with increased performance.
    /// </remarks>
    public class DefaultRandomSeedSource : IRandomSeedSource {
        #region Instance Fields

        readonly uint _concurrencyLevel;
        readonly Xoshiro256StarStarRandom[] _seedRngArr;
        readonly Semaphore _semaphore;
        // Round robin accumulator.
        int _roundRobinAcc = 0;

        #endregion

        #region Constructors

        /// <summary>
        /// Construct with the default concurrency level.
        /// </summary>
        public DefaultRandomSeedSource()
            : this(Environment.ProcessorCount) { }

        /// <summary>
        /// Construct with the specified minimum concurrency level.
        /// </summary>
        /// <remarks>
        /// minConcurrencyLevel must be at least one, an exception is thrown if it is less than 1 (i.e. zero or negative).
        /// The actual concurrency level is required to be a power of two, thus the actual level is chosen to be the 
        /// nearest power of two that is greater than or equal to minConcurrencyLevel/
        /// </remarks>
        public DefaultRandomSeedSource(int minConcurrencyLevel) {
            if (minConcurrencyLevel < 1) {
                throw new ArgumentException("Must be at least 1.", nameof(minConcurrencyLevel));
            }

            // The actual concurrency level is required to be a power of two, thus the actual level is chosen
            // to be the nearest power of two that is greater than or equal to minConcurrencyLevel.
            int concurrencyLevel = MathUtils.CeilingToPowerOfTwo(minConcurrencyLevel);
            _concurrencyLevel = (uint)concurrencyLevel;

            // Create high quality random bytes to init the seed PRNGs.
            byte[] buf = GetCryptoRandomBytes(concurrencyLevel * 8);

            // Init the seed PRNGs and associated sync lock objects.
            _seedRngArr = new Xoshiro256StarStarRandom[concurrencyLevel];

            for (int i = 0; i < concurrencyLevel; i++) {
                // Init rng.
                ulong seed = BitConverter.ToUInt64(buf, i * 8);
                _seedRngArr[i] = new Xoshiro256StarStarRandom(seed);
            }

            // Create a semaphore that will allow N threads into a critical section, and no more.
            _semaphore = new Semaphore(minConcurrencyLevel, concurrencyLevel);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Get a new seed value.
        /// </summary>
        public ulong GetSeed() {
            // Limit the number of threads that can enter the critical section below.
            //
            // Wait for the semaphore counter to become non-zero, and decrement the counter as we enter the 
            // critical section. This blocks if the maximum concurrency level has been reached, until one 
            // of the other callers to this method completes and calls Release().
            _semaphore.WaitOne();

            try {
                // Rotate through the seedRNG array.
                // Notes.
                // We are inside the semaphore gated critical section, but there can still be multiple 
                // concurrent threads executing here, thus Interlocked is used as a cheap/fast way to 
                // synchronise access to _roundRobinAcc
                //
                // _concurrencyLevel is required to be a power of two, so that the modulus result cycles 
                // through the seed RNG indexes without jumping when _roundRobinAcc transitions from 
                // 0xffff_ffff to 0x0000_0000.
                // Note. The modulus operation is generally expensive to compute; here a much cheaper/faster
                // alternative method can be used because _concurrencyLevel is guaranteed to be a power
                // of two.
                uint idx = ((uint)Interlocked.Increment(ref _roundRobinAcc)) & (_concurrencyLevel - 1);

                // Obtain a random sample from the selected seed RNG.
                // Note. Only the current thread will be using the selected RNG.
                return _seedRngArr[idx].NextULong();
            } finally {
                // Increment the semaphore counter.
                _semaphore.Release();
            }
        }

        #endregion

        #region Private Static Methods

        private static byte[] GetCryptoRandomBytes(int count) {
            // Note. Generating crypto random bytes can be very slow, relative to a PRNG; we may even have to wait
            // for the OS to have sufficient entropy for generating the bytes.
            byte[] buf = new byte[count];
            //using (RNGCryptoServiceProvider cryptoRng = new RNGCryptoServiceProvider()) {
            //    cryptoRng.GetBytes(buf);
            //}

            RNGCryptoServiceProvider cryptoRng = new RNGCryptoServiceProvider();
            cryptoRng.GetBytes(buf);

            return buf;
        }

        #endregion
    }
}
