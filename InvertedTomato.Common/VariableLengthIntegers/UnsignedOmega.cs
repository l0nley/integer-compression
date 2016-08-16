﻿using System;
using System.Collections.Generic;
using System.IO;

namespace InvertedTomato.VariableLengthIntegers {
    /// <summary>
    /// Implementation of Elias Omega encoding for unsigned values. Optionally (and by default) zeros are permitted by passing TRUE in
    /// the constructor. Keep in mind that doing this breaks standard.
    /// 
    /// Example values with allowZeros enabled:
    /// 
    ///      VALUE  ENCODED
    ///          0  0_______
    ///          1  100_____  
    ///          2  110_____  
    ///          3  101000__  
    ///          6  101110__  
    ///          7  1110000_  
    ///         14  1111110_  
    ///         15  10100100 000_____ 
    ///         31  10101100 0000____ 
    ///         99  10110110 01000___ 
    ///        999  11100111 11101000 0_______ 
    ///      9,999  11110110 01110001 00000___ 
    ///     99,999  10100100 00110000 11010100 0000____ 
    ///    999,999  10100100 11111101 00001001 0000000_
    /// 
    /// For more information on Elias Omega see https://en.wikipedia.org/wiki/Elias_omega_coding.
    /// To see how Elias compares to other universal codes, see https://en.wikipedia.org/wiki/Elias_omega_coding
    /// 
    /// This implementation is loosely based on http://www.dupuis.me/node/39.
    /// </summary>
    public class UnsignedOmega {
        private readonly bool AllowZeros;
        public UnsignedOmega(bool allowZeros = true) {
            AllowZeros = allowZeros;
        }

        public void Encode(ulong value, Func<byte> read, Action<byte> write, Action move, ref int offset) {
            // #1 Place a "0" at the end of the code.
            // #2 If N=1, stop; encoding is complete.
            // #3 Prepend the binary representation of N to the beginning of the code (this will be at least two bits, the first bit of which is a 1)
            // #4 Let N equal the number of bits just prepended, minus one.
            // #5 Return to step 2 to prepend the encoding of the new N.

            // Offset value to allow for 0s
            if (AllowZeros) {
                value++;
            } else if (value == 0) {
                throw new ArgumentOutOfRangeException("Zeros are not permitted without AllowZeros enabled in constructor.");
            }

            // Prepare buffer
            var buffer = new Stack<KeyValuePair<ulong, byte>>();

            // #1 Place a "0" at the end of the code.
            buffer.Push(new KeyValuePair<ulong, byte>(0, 1));

            // #2 If N=1, stop; encoding is complete.
            while (value > 1) {
                // Calculate the length of value
                var length = CountBits(value);

                // #3 Prepend the binary representation of N to the beginning of the code (this will be at least two bits, the first bit of which is a 1)
                buffer.Push(new KeyValuePair<ulong, byte>(value, length));

                // #4 Let N equal the number of bits just prepended, minus one.
                value = (ulong)length - 1;
            }

            // Load current byte (skip if 0 offset - optimization)
            var currentByte = offset > 0 ? read() : (byte)0x00;

            // Write buffer
            foreach (var item in buffer) {
                var bits = item.Value;
                var group = item.Key;

                while (bits > 0) {
                    // Calculate size of chunk
                    var chunk = (byte)Math.Min(bits, 8 - offset);

                    // Add to byte
                    if (offset + bits > 8) {
                        currentByte |= (byte)(group >> (bits - chunk));
                    } else {
                        currentByte |= (byte)(group << (8 - offset - chunk));
                    }

                    // Update length available
                    bits -= chunk;

                    // Detect if byte is full
                    offset += chunk;
                    if (offset == 8) {
                        // Write byte
                        write(currentByte);

                        // Move to next position
                        move();

                        // Reset offset
                        offset = 0;

                        // Clear byte
                        currentByte = 0;
                    }
                }
            }

            // Write out final byte if partially used
            if (offset > 0) {
                write(currentByte);
            }
        }

        public void Encode(ulong value, byte[] output, ref int position, ref int offset) {
            var innerPosition = position;

            Encode(value,
                () => {
                    return output[innerPosition];
                },
                (b) => {
                    output[innerPosition] = b;
                },
                () => {
                    innerPosition++;
                },
                ref offset
            );

            position = innerPosition;
        }

        public void Encode(ulong value, Stream output, ref int offset) {
            Encode(value,
                () => {
                    var b = output.ReadByte();
                    if (b < 0) {
                        throw new EndOfStreamException();
                    }
                    return (byte)b;
                },
                (b) => {
                    output.WriteByte(b);
                    output.Position--;
                },
                () => {
                    output.Position++;
                },
                ref offset
            );
        }

        public byte[] Encode(ulong value) {
            // Encode to buffer
            var buffer = new byte[10];
            var position = 0;
            var offset = 0;

            Encode(value,
                () => {
                    return buffer[position];
                },
                (b) => {
                    buffer[position] = b;
                },
                () => {
                    position++;
                },
                ref offset
            );

            // If there's a partial byte at the end, include it in output
            if (offset > 0) {
                position++;
            }

            // Trim unneeded bytes
            var output = new byte[position];
            Buffer.BlockCopy(buffer, 0, output, 0, output.Length);
            return output;
        }

        public ulong Decode(Func<byte> read, Action move, ref int offset) {
            // #1 Start with a variable N, set to a value of 1.
            // #2 If the next bit is a "0", stop. The decoded number is N.
            // #3 If the next bit is a "1", then read it plus N more bits, and use that binary number as the new value of N.
            // #4 Go back to step 2.

            // Load current byte
            var currentByte = read();

            // #1 Start with a variable N, set to a value of 1.
            var value = (ulong)1;

            // #2 If the next bit is a "0", stop. The decoded number is N.
            while ((currentByte & 1 << (7 - offset)) > 0) {
                // #3 If the next bit is a "1", then read it plus N more bits, and use that binary number as the new value of N.
                var length = (byte)value + 1;
                value = 0;
                while (length > 0) {
                    // Calculate size of chunk
                    var chunk = Math.Min(length, (byte)(8 - offset));

                    // Add to byte
                    var mask = byte.MaxValue;
                    mask <<= 8 - chunk;
                    mask >>= offset;
                    value <<= chunk;
                    value += (ulong)(currentByte & mask) >> (8 - chunk - offset);

                    // Update length available
                    length -= chunk;

                    // Increment offset, and load next byte if required
                    if ((offset += chunk) == 8) {
                        // Move to next position
                        move();

                        // Read byte
                        currentByte = read();

                        // Reset offset
                        offset = 0;
                    }
                }
            }

            // Increment offset for termination bit
            if (offset++ == 8) {
                move();
            }

            // Offset value to allow for 0s
            if (AllowZeros) {
                return value - 1;
            } else {
                return value;
            }
        }

        public ulong Decode(byte[] input, ref int position, ref int offset) {
            if (null == input) {
                throw new ArgumentNullException("input");
            }

            var innerPosition = position;
            var value = Decode(
                () => {
                    return input[innerPosition];
                },
                () => {
                    innerPosition++;
                },
                ref offset
            );
            position = innerPosition;

            return value;
        }

        public ulong Decode(Stream input, ref int offset) {
            if (null == input) {
                throw new ArgumentNullException("input");
            }

            return Decode(
                () => {
                    var b = input.ReadByte();
                    if (b < 0) {
                        throw new EndOfStreamException();
                    }
                    input.Position--;
                    return (byte)b;
                },
                () => {
                    input.Position++;
                },
                ref offset
            );
        }

        public ulong Decode(byte[] input) {
            if (null == input) {
                throw new ArgumentNullException("input");
            }

            var position = 0;
            var offset = 0;
            var value = Decode(
                () => {
                    return input[position];
                },
                () => {
                    position++;
                },
                ref offset
            );

            return value;
        }

        private byte CountBits(ulong value) {
            byte bits = 0;

            do {
                bits++;
                value >>= 1;
            } while (value > 0);

            return bits;
        }
    }
}
