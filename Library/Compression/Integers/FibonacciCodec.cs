﻿using System;
using InvertedTomato.IO.Buffers;

namespace InvertedTomato.Compression.Integers {
    public class FibonacciCodec : IIntegerCodec {
        /// <summary>
        /// The maximum value of a symbol this codec can support.
        /// </summary>
        public const ulong MaxValue = ulong.MaxValue - 1;

        /// <summary>
        /// Lookup table of Fibonacci numbers that can fit in a ulong.
        /// </summary>
        public static readonly ulong[] Lookup = new ulong[92];

        /// <summary>
        /// The most significant bit in a byte.
        /// </summary>
        private const byte MSB = 0x80;

        static FibonacciCodec() {
            // Compute all Fibonacci numbers that can fit in a ulong.
            Lookup[0] = 1;
            Lookup[1] = 2;
            for (var i = 2; i < Lookup.Length; i++) {
                Lookup[i] = Lookup[i - 1] + Lookup[i - 2];
            }
        }

        /// <summary>
        /// The guessed size of buffer when there is no indication otherwise.
        /// </summary>
        public int BufferDefaultSize { get; set; } = 8;

        /// <summary>
        /// When BufferDefaultSize proves to be too small, increase the size by this factor.
        /// </summary>
        public int BufferGrowthFactor { get; set; } = 2;
        
        public bool IncludeHeader { get; set; }
        public Buffer<ulong> DecompressedSet { get; set; }
        public Buffer<byte> CompressedSet { get; set; }


        public void Compress() {
#if DEBUG
            if (null == DecompressedSet) {
                throw new InvalidOperationException("DecompressedSet is null.");
            }
#endif

            // Quickly handle empty sets with no headers - they'll cause issues later if not handled here
            if (!IncludeHeader && DecompressedSet.IsEmpty) {
                CompressedSet = new Buffer<byte>(0);
                return;
            }

            // Allocate buffer for compressed output - we assume the worst-case compression (could be optimised to lazy grow?)
            CompressedSet = new Buffer<byte>((DecompressedSet.Used + 1) * 12);

            // Clear currently worked-on byte
            var current = new BitBuffer();

            // Get first symbol
            var value = IncludeHeader ? (ulong)DecompressedSet.Used : DecompressedSet.Dequeue();

            // Iterate through all symbols
            do {
#if DEBUG
                if (value > MaxValue) {
                    throw new OverflowException("Exceeded FibonacciCodec's maximum supported symbol value of " + MaxValue + ".");
                }
#endif

                // Fibbonacci doesn't support 0s, so add 1 to allow for them
                value++;

                // #1 Find the largest Fibonacci number equal to or less than N; subtract this number from N, keeping track of the remainder.
                // #3 Repeat the previous steps, substituting the remainder for N, until a remainder of 0 is reached.
                bool[] map = null;
                for (var fibIdx = Lookup.Length - 1; fibIdx >= 0; fibIdx--) {
                    // #2 If the number subtracted was the ith Fibonacci number F(i), put a 1 in place i−2 in the code word(counting the left most digit as place 0).
                    if (value >= Lookup[fibIdx]) {
                        // Detect if this is the largest fib and store
                        if (null == map) {
                            map = new bool[fibIdx + 1];
                        }

                        // Write to map
                        map[fibIdx] = true;

                        // Deduct Fibonacci number from value
                        value -= Lookup[fibIdx];
                    }
                }

                // Output the bits of the map in reverse order
                foreach (var bit in map) {
                    if (current.Append(bit)) {
                        CompressedSet.Enqueue(current.Clear());
                    }
                }

                // #4 Place an additional 1 after the rightmost digit in the code word.
                if (current.Append(true)) {
                    CompressedSet.Enqueue(current.Clear());
                }
            } while (DecompressedSet.TryDequeue(out value));


            // Flush bit buffer
            if (current.IsDirty) {
                CompressedSet.Enqueue(current.Clear());
            }
        }
        
        // Current symbol being decoded.
        ulong DecompressSymbol = 0;

        // Next Fibonacci number to test.
        int DecompressNextFibIndex = 0;

        // State of the last bit while decoding.
        bool DecompressLastBit = false;

        public int Decompress() {
#if DEBUG
            if (null == CompressedSet) {
                throw new InvalidOperationException("CompressedSet is null.");
            }
#endif

            // If there's no header, lets assume the set is "default" sized
            if (!IncludeHeader && null == DecompressedSet) {
                DecompressedSet = new Buffer<ulong>(BufferDefaultSize);
            }

            byte input;
            while (CompressedSet.TryDequeue(out input)) {
                // For each bit of buffer
                for (var inputPosition = 0; inputPosition < 8; inputPosition++) {
                    // If bit is set...
                    if (((input << inputPosition) & MSB) > 0) {
                        // If double 1 bits
                        if (DecompressLastBit) {
                            // Remove zero offset
                            DecompressSymbol--;

                            // If output hasn't been allocated...
                            if (null == DecompressedSet) {
                                // Allocate output
                                DecompressedSet = new Buffer<ulong>((int)DecompressSymbol);
                            } else {
                                // Add to output
                                DecompressedSet.Enqueue(DecompressSymbol);

                                // If we've run out of output buffer
                                if (DecompressedSet.IsFull) {
                                    // This had a header, so that must be all the data
                                    if (IncludeHeader) {
                                        // Return
                                        return 0;
                                    } else {
                                        // There's no header - we don't know how big the set it, and the output is full - grow it
                                        DecompressedSet = DecompressedSet.Resize(DecompressedSet.Used * BufferGrowthFactor);
                                    }
                                }
                            }

                            // Reset for next symbol
                            DecompressSymbol = 0;
                            DecompressNextFibIndex = 0;
                            DecompressLastBit = false;
                            continue;
                        }

                        // Add value to current symbol
                        DecompressSymbol += Lookup[DecompressNextFibIndex];

                        // Note bit for next cycle
                        DecompressLastBit = true;
                    } else {
                        // Note bit for next cycle
                        DecompressLastBit = false;
                    }

                    // Increment bit position
                    DecompressNextFibIndex++;

#if DEBUG
                    // Check for overflow
                    if (DecompressNextFibIndex > Lookup.Length) {
                        throw new OverflowException("Value too large to decode. Max 64bits supported.");  // TODO: Handle this so that it doesn't allow for DoS attacks!
                    }
#endif
                }
            }

            // Without a header we didn't know how much data to expect anyway. This must be all.
            if (!IncludeHeader) {
                return 0;
            }

            // No complete sets were found
            return 1;
        }
    }
}