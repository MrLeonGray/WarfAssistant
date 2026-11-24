using System;
using System.IO;
using System.Text;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Minimal managed LZMA1 (13-byte standalone header) decoder for Warframe Public Export index files.
    /// Avoids external NuGet dependencies while natively reading official DE index_ru/en.txt.lzma manifests.
    /// </summary>
    public static class LzmaMinimalDecoder
    {
        private const int NumBitModelTotalBits = 11;
        private const int BitModelTotal = 1 << NumBitModelTotalBits;
        private const int NumMoveBits = 5;
        private const uint TopMask = 0xFF000000u;

        private const int NumStates = 12;
        private const int NumPosStatesBitsMax = 4;
        private const int NumPosStatesMax = 1 << NumPosStatesBitsMax;
        private const int NumLenToPosStates = 4;
        private const int NumAlignBits = 4;
        private const int AlignTableSize = 1 << NumAlignBits;
        private const int StartPosModelIndex = 4;
        private const int EndPosModelIndex = 14;
        private const int NumFullDistances = 1 << (EndPosModelIndex / 2);

        private sealed class RangeDecoder
        {
            private readonly byte[] _data;
            private int _pos;
            private int _overrunBytes;
            public uint Range = 0xFFFFFFFFu;
            public uint Code;

            public RangeDecoder(byte[] data, int offset)
            {
                _data = data;
                _pos = offset + 1; // skip leading byte
                Code = ((uint)GetByte() << 24) | ((uint)GetByte() << 16) | ((uint)GetByte() << 8) | GetByte();
            }

            private byte GetByte()
            {
                if (_pos < _data.Length) return _data[_pos++];
                if (++_overrunBytes > 16)
                    throw new InvalidDataException("Truncated LZMA stream.");
                return 0;
            }

            public void Normalize()
            {
                if (Range < 0x01000000u)
                {
                    Range <<= 8;
                    Code = (Code << 8) | GetByte();
                }
            }

            public int DecodeBit(ushort[] probs, int index)
            {
                Normalize();
                uint prob = probs[index];
                uint bound = (Range >> NumBitModelTotalBits) * prob;
                if (Code < bound)
                {
                    Range = bound;
                    probs[index] = (ushort)(prob + ((BitModelTotal - prob) >> NumMoveBits));
                    return 0;
                }
                Range -= bound;
                Code -= bound;
                probs[index] = (ushort)(prob - (prob >> NumMoveBits));
                return 1;
            }

            public int DecodeDirectBits(int numTotalBits)
            {
                int result = 0;
                for (int i = numTotalBits; i > 0; i--)
                {
                    Normalize();
                    Range >>= 1;
                    uint t = (Code - Range) >> 31;
                    Code -= Range & (t - 1);
                    result = (result << 1) | (int)(1 - t);
                }
                return result;
            }

            public int BitTreeDecode(ushort[] probs, int offset, int numBits)
            {
                int m = 1;
                for (int i = 0; i < numBits; i++)
                {
                    m = (m << 1) + DecodeBit(probs, offset + m);
                }
                return m - (1 << numBits);
            }

            public int ReverseDecode(ushort[] probs, int offset, int numBits)
            {
                int m = 1;
                int symbol = 0;
                for (int i = 0; i < numBits; i++)
                {
                    int bit = DecodeBit(probs, offset + m);
                    m = (m << 1) + bit;
                    symbol |= bit << i;
                }
                return symbol;
            }
        }

        private sealed class LenDecoder
        {
            private readonly ushort[] _choice = InitProbs(2);
            private readonly ushort[][] _lowCoder = new ushort[NumPosStatesMax][];
            private readonly ushort[][] _midCoder = new ushort[NumPosStatesMax][];
            private readonly ushort[] _highCoder = InitProbs(256);

            public LenDecoder()
            {
                for (int i = 0; i < NumPosStatesMax; i++)
                {
                    _lowCoder[i] = InitProbs(8);
                    _midCoder[i] = InitProbs(8);
                }
            }

            public int Decode(RangeDecoder rc, int posState)
            {
                if (rc.DecodeBit(_choice, 0) == 0)
                    return rc.BitTreeDecode(_lowCoder[posState], 0, 3);
                if (rc.DecodeBit(_choice, 1) == 0)
                    return 8 + rc.BitTreeDecode(_midCoder[posState], 0, 3);
                return 16 + rc.BitTreeDecode(_highCoder, 0, 8);
            }
        }

        private static ushort[] InitProbs(int count)
        {
            var arr = new ushort[count];
            Array.Fill(arr, (ushort)(BitModelTotal >> 1));
            return arr;
        }

        public static string DecompressUtf8(byte[] compressed)
        {
            byte[] decoded = Decompress(compressed);
            return Encoding.UTF8.GetString(decoded);
        }

        public static byte[] Decompress(byte[] input)
        {
            if (input == null || input.Length < 18)
                throw new InvalidDataException("Invalid LZMA standalone stream.");

            int prop0 = input[0];
            if (prop0 >= 9 * 5 * 5)
                throw new InvalidDataException("Invalid LZMA properties.");

            int pb = prop0 / 45;
            int rem = prop0 % 45;
            int lp = rem / 9;
            int lc = rem % 9;

            uint dictSize = BitConverter.ToUInt32(input, 1);
            if (dictSize < 4096) dictSize = 4096;

            long outSize = BitConverter.ToInt64(input, 5);
            int posStateMask = (1 << pb) - 1;
            int literalPosMask = (1 << lp) - 1;

            var rc = new RangeDecoder(input, 13);

            var isMatch = InitProbs(NumStates << NumPosStatesBitsMax);
            var isRep = InitProbs(NumStates);
            var isRepG0 = InitProbs(NumStates);
            var isRepG1 = InitProbs(NumStates);
            var isRepG2 = InitProbs(NumStates);
            var isRep0Long = InitProbs(NumStates << NumPosStatesBitsMax);

            var posSlotDecoder = new ushort[NumLenToPosStates][];
            for (int i = 0; i < NumLenToPosStates; i++)
                posSlotDecoder[i] = InitProbs(1 << 6);

            var posDecoders = InitProbs(256);
            var posAlignDecoder = InitProbs(AlignTableSize);
            var lenDecoder = new LenDecoder();
            var repLenDecoder = new LenDecoder();

            int numLitStates = 1 << (lc + lp);
            var litDecoders = new ushort[numLitStates][];
            for (int i = 0; i < numLitStates; i++)
                litDecoders[i] = InitProbs(0x300);

            using var ms = outSize > 0 && outSize < 64 * 1024 * 1024
                ? new MemoryStream((int)outSize)
                : new MemoryStream();

            var ring = new byte[Math.Min(dictSize, 16u * 1024 * 1024)];
            int ringPos = 0;
            long nowPos = 0;

            int state = 0;
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;

            void PutByte(byte b)
            {
                ms.WriteByte(b);
                ring[ringPos++] = b;
                if (ringPos >= ring.Length) ringPos = 0;
                nowPos++;
            }

            byte GetPrevByte(uint dist)
            {
                int idx = ringPos - (int)dist - 1;
                if (idx < 0) idx += ring.Length;
                return ring[idx];
            }

            byte prevByte = 0;

            while (outSize < 0 || nowPos < outSize)
            {
                int posState = (int)nowPos & posStateMask;
                if (rc.DecodeBit(isMatch, (state << NumPosStatesBitsMax) + posState) == 0)
                {
                    int litIdx = (((int)nowPos & literalPosMask) << lc) + (prevByte >> (8 - lc));
                    ushort[] probs = litDecoders[litIdx];
                    int symbol = 1;
                    if (state >= 7)
                    {
                        byte matchByte = GetPrevByte(rep0);
                        while (symbol < 0x100)
                        {
                            int matchBit = (matchByte >> 7) & 1;
                            matchByte <<= 1;
                            int bit = rc.DecodeBit(probs, ((1 + matchBit) << 8) + symbol);
                            symbol = (symbol << 1) | bit;
                            if (matchBit != bit) break;
                        }
                    }
                    while (symbol < 0x100)
                    {
                        symbol = (symbol << 1) | rc.DecodeBit(probs, symbol);
                    }
                    prevByte = (byte)symbol;
                    PutByte(prevByte);
                    state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                }
                else
                {
                    int len;
                    if (rc.DecodeBit(isRep, state) == 1)
                    {
                        len = 0;
                        if (rc.DecodeBit(isRepG0, state) == 0)
                        {
                            if (rc.DecodeBit(isRep0Long, (state << NumPosStatesBitsMax) + posState) == 0)
                            {
                                state = state < 7 ? 9 : 11;
                                prevByte = GetPrevByte(rep0);
                                PutByte(prevByte);
                                continue;
                            }
                        }
                        else
                        {
                            uint distance;
                            if (rc.DecodeBit(isRepG1, state) == 0)
                            {
                                distance = rep1;
                            }
                            else
                            {
                                if (rc.DecodeBit(isRepG2, state) == 0)
                                    distance = rep2;
                                else
                                {
                                    distance = rep3;
                                    rep3 = rep2;
                                }
                                rep2 = rep1;
                            }
                            rep1 = rep0;
                            rep0 = distance;
                        }
                        len = repLenDecoder.Decode(rc, posState) + 2;
                        state = state < 7 ? 8 : 11;
                    }
                    else
                    {
                        rep3 = rep2;
                        rep2 = rep1;
                        rep1 = rep0;
                        len = 2 + lenDecoder.Decode(rc, posState);
                        state = state < 7 ? 7 : 10;

                        int lenToPos = len - 2;
                        if (lenToPos >= NumLenToPosStates) lenToPos = NumLenToPosStates - 1;
                        int posSlot = rc.BitTreeDecode(posSlotDecoder[lenToPos], 0, 6);
                        if (posSlot >= StartPosModelIndex)
                        {
                            int numDirectBits = (posSlot >> 1) - 1;
                            rep0 = (uint)((2 | (posSlot & 1)) << numDirectBits);
                            if (posSlot < EndPosModelIndex)
                            {
                                rep0 += (uint)rc.ReverseDecode(posDecoders, (int)rep0 - posSlot - 1, numDirectBits);
                            }
                            else
                            {
                                rep0 += (uint)(rc.DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits);
                                rep0 += (uint)rc.ReverseDecode(posAlignDecoder, 0, NumAlignBits);
                                if (rep0 == 0xFFFFFFFFu)
                                    break;
                            }
                        }
                        else
                        {
                            rep0 = (uint)posSlot;
                        }
                    }

                    for (int i = 0; i < len && (outSize < 0 || nowPos < outSize); i++)
                    {
                        prevByte = GetPrevByte(rep0);
                        PutByte(prevByte);
                    }
                }
            }

            return ms.ToArray();
        }
    }
}
