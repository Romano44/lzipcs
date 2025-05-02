/* Lzip - LZMA lossless data compressor
   Copyright (C) 2008-2025 Antonio Diaz Diaz.

   This program is free software: you can redistribute it and/or modify
   it under the terms of the GNU General Public License as published by
   the Free Software Foundation, either version 2 of the License, or
   (at your option) any later version.

   This program is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU General Public License for more details.

   You should have received a copy of the GNU General Public License
   along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
/* Arg_parser - POSIX/GNU command-line argument parser. (C++ version)
   Copyright (C) 2006-2025 Antonio Diaz Diaz.

   This library is free software. Redistribution and use in source and
   binary forms, with or without modification, are permitted provided
   that the following conditions are met:

   1. Redistributions of source code must retain the above copyright
   notice, this list of conditions, and the following disclaimer.

   2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions, and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   This library is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

/* lzipcs is a translation of lzip to C# NET.
   This program is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU General Public License for more details.

   You should have received a copy of the GNU General Public License
   along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Diagnostics;

public static class Constants
{
    public const int min_dictionary_bits = 12;
    public const int min_dictionary_size = 1 << min_dictionary_bits;
    public const int max_dictionary_bits = 29;
    public const int max_dictionary_size = 1 << max_dictionary_bits;
    public const int min_member_size = 36;
    public const int literal_context_bits = 3;
    public const int literal_pos_state_bits = 0;
    public const int pos_state_bits = 2;
    public const int pos_states = 1 << pos_state_bits;
    public const int pos_state_mask = pos_states - 1;

    public const int len_states = 4;
    public const int dis_slot_bits = 6;
    public const int start_dis_model = 4;
    public const int end_dis_model = 14;
    public const int modeled_distances = 1 << (end_dis_model / 2);
    public const int dis_align_bits = 4;
    public const int dis_align_size = 1 << dis_align_bits;

    public const int len_low_bits = 3;
    public const int len_mid_bits = 3;
    public const int len_high_bits = 8;
    public const int len_low_symbols = 1 << len_low_bits;
    public const int len_mid_symbols = 1 << len_mid_bits;
    public const int len_high_symbols = 1 << len_high_bits;
    public const int max_len_symbols = len_low_symbols + len_mid_symbols + len_high_symbols;

    public const int min_match_len = 2;
    public const int max_match_len = min_match_len + max_len_symbols - 1;
    public const int min_match_len_limit = 5;

    public const int bit_model_move_bits = 5;
    public const int bit_model_total_bits = 11;
    public const int bit_model_total = 1 << bit_model_total_bits;

    public const int price_shift_bits = 6;
    public const int price_step_bits = 2;
    public const int price_step = 1 << price_step_bits;

    public static readonly byte[] lzip_magic = { 0x4C, 0x5A, 0x49, 0x50 }; // "LZIP"

    public const int max_marker_size = 16;
    public const int num_rep_distances = 4;
    public const int infinite_price = 0x0FFFFFFF;
    public const int max_num_trials = 1 << 13;
    public const int single_step_trial = -2;
    public const int dual_step_trial = -1;

    public const int before_size = max_num_trials;
    public const int after_size = (2 * max_match_len) + 1;
    public const int dict_factor = 2;
    public const int num_prev_positions3 = 1 << 16;
    public const int num_prev_positions2 = 1 << 10;
    public const int num_prev_positions23 = num_prev_positions2 + num_prev_positions3;
    public const int pos_array_factor = 2;
}

public static partial class Program          //forward declarations
{
    public static partial void ShowCProgress(ulong cfile_size = 0, ulong partial_size = 0, 
                    ulong out_partial_size = 0, in LZEncoderBase? eb = null, bool init = false);
    public static partial void ShowDProgress(ulong cfile_size = 0, ulong partial_size = 0, 
                    ulong out_partial_size = 0, in LzDecoder? d = null, bool init = false);
}

public struct State
{
    private int st = 0;      //must use new() on struct to init to 0!
    private static readonly int[] next = { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 4, 5 };
    public const int states = 12;
    
    public State() {}

    public int Get() => st;
    public bool IsChar() => st < 7; 
    public void SetChar() { st = next[st]; }
    public bool IsCharSetChar() 
    { 
        SetChar(); 
        return st < 4; 
    }
    public void SetCharRep() { st = 8; }
    public void SetMatch() { st = (st < 7) ? 7 : 10; }
    public void SetRep() { st = (st < 7) ? 8 : 11; }
    public void SetShortRep() { st = (st < 7) ? 9 : 11; }
}

public static class Utils
{
    public static int GetLenState(int len)
        => Math.Min(len - Constants.min_match_len, Constants.len_states - 1); 

    public static int GetLitState(byte prev_byte)
        => prev_byte >> (8 - Constants.literal_context_bits); 

    public static bool IsValidDs(uint dictionary_size)
        => (dictionary_size >= Constants.min_dictionary_size &&
            dictionary_size <= Constants.max_dictionary_size);

    public static int RealBits(uint value)
    {
        int bits = 0;
        while (value > 0) { value >>= 1; ++bits; }
        return bits;
    }
}

public struct BitModel
{
    public int probability;        //must use new() on each struct in array to invoke constructor!!!

    public void Reset() { probability = Constants.bit_model_total / 2; }
    public static void Reset(Span<BitModel> models)
    {
        foreach (ref BitModel model in models) 
            model.Reset();
    }

    public BitModel() { Reset(); }
}

public sealed class LenModel
{
    public BitModel choice1 = new();
    public BitModel choice2 = new();
    private readonly BitModel[] _bm_low = new BitModel[Constants.pos_states * Constants.len_low_symbols];     //was 2D array
    private readonly BitModel[] _bm_mid = new BitModel[Constants.pos_states * Constants.len_mid_symbols];     //was 2D array
    public readonly BitModel[] bm_high = new BitModel[Constants.len_high_symbols];

    public LenModel()
    {
        foreach (ref BitModel b in _bm_low.AsSpan()) b = new();
        foreach (ref BitModel b in _bm_mid.AsSpan()) b = new();
        foreach (ref BitModel b in bm_high.AsSpan()) b = new();
    }

    public void Reset()
    {
        choice1.Reset();
        choice2.Reset();

        BitModel.Reset(_bm_low.AsSpan());
        BitModel.Reset(_bm_mid.AsSpan());
        BitModel.Reset(bm_high.AsSpan());
    }

    public Span<BitModel> bm_low_row(int row)
        => _bm_low.AsSpan(row * Constants.len_low_symbols, Constants.len_low_symbols);

    public Span<BitModel> bm_mid_row(int row)
        => _bm_mid.AsSpan(row * Constants.len_mid_symbols, Constants.len_mid_symbols);
}

public static class CRC32
{
    private static readonly uint[] data = new uint[256];

    static CRC32()
    {
        for (uint n = 0; n < 256; ++n)
        {
            uint c = n;
            for (int k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
            data[n] = c;
        }
    }

    public static uint Get(byte byteVal) => data.AsSpan()[byteVal];

    public static void UpdateByte(ref uint crc, byte byteVal)
    {
        crc = data[(crc ^ byteVal) & 0xFF] ^ (crc >> 8);
    }

    public static void UpdateBuf(ref uint crc, ReadOnlySpan<byte> buffer)
    {
        uint c = crc;
        int len = buffer.Length;
        int i = 0;

        // Process 4 bytes at a time
        for (; i <= len - 4; i += 4)
        {
            c = data[(c ^ buffer[i]) & 0xFFU] ^ (c >> 8);
            c = data[(c ^ buffer[i+1]) & 0xFFU] ^ (c >> 8);
            c = data[(c ^ buffer[i+2]) & 0xFFU] ^ (c >> 8);
            c = data[(c ^ buffer[i+3]) & 0xFFU] ^ (c >> 8);
        }

        // Process remaining bytes
        for (; i < len; i++)
            c = data[(c ^ buffer[i]) & 0xFFU] ^ (c >> 8);

        crc = c;
    }
}

public sealed class LzipHeader
{
    public const int Size = 6;
    public readonly byte[] data = new byte[Size];

    public static LzipHeader FromBuffer(byte[] buffer, int offset)
    {
        LzipHeader header = new();
        Buffer.BlockCopy(buffer, offset, header.data, 0, Size); 
        return header;
    }

    public void SetMagic()
    {
        Constants.lzip_magic.AsSpan().CopyTo(data.AsSpan(0, 4));
        data[4] = 1;
    }

    public bool CheckMagic()
        => data.AsSpan(0, 4).SequenceEqual(Constants.lzip_magic.AsSpan());

    public bool CheckPrefix(int sz)
    {
        for (int i = 0; i < sz && i < 4; ++i)
            if (data[i] != Constants.lzip_magic[i]) 
                return false;
        return sz > 0;
    }

    public bool CheckCorrupt()
    {
        int matches = 0;
        for (int i = 0; i < 4; ++i)
            if (data[i] == Constants.lzip_magic[i]) 
                ++matches;
        return matches > 1 && matches < 4;
    }

    public byte Version() => data[4];
    public bool CheckVersion() => data[4] == 1; 

    public uint DictionarySize()
    {
        uint sz = 1U << (data[5] & 0x1F);
        if (sz > Constants.min_dictionary_size)
            sz -= (uint)((sz / 16) * ((data[5] >> 5) & 7U));
        return sz;
    }

    public bool SetDictionarySize(uint sz)
    {
        if (!Utils.IsValidDs(sz)) 
            return false;
        
        data[5] = (byte)Utils.RealBits(sz - 1);
        if (sz > Constants.min_dictionary_size)
        {
            uint base_size = 1U << data[5];
            uint fraction = base_size / 16;
            for (uint i = 7; i >= 1; --i)
            {
                if (base_size - (i * fraction) >= sz)
                {
                    data[5] |= (byte)(i << 5);
                    break;
                }
            }
        }
        return true;
    }

    public bool Check()
        => CheckMagic() && CheckVersion() && Utils.IsValidDs(DictionarySize());
}

public sealed class LzipTrailer
{
    public const int Size = 20;
    public readonly byte[] data = new byte[Size];

    public static LzipTrailer FromBuffer(byte[] buffer, int offset)
    {
        LzipTrailer trailer = new();
        Buffer.BlockCopy(buffer, offset, trailer.data, 0, Size); 
        return trailer;
    }

    public uint DataCrc()
    {
        uint tmp = 0;
        for (int i = 3; i >= 0; --i) 
        { 
            tmp <<= 8; 
            tmp += data[i]; 
        }
        return tmp;
    }

    public void DataCrc(uint crc)
    {
        for (int i = 0; i <= 3; ++i) 
        { 
            data[i] = (byte)crc; 
            crc >>= 8; 
        }
    }

    public ulong DataSize()
    {
        ulong tmp = 0;
        for (int i = 11; i >= 4; --i) 
        { 
            tmp <<= 8; 
            tmp += data[i]; 
        }
        return tmp;
    }

    public void DataSize(ulong sz)
    {
        for (int i = 4; i <= 11; ++i) 
        { 
            data[i] = (byte)sz; 
            sz >>= 8; 
        }
    }

    public ulong MemberSize()
    {
        ulong tmp = 0;
        for (int i = 19; i >= 12; --i) 
        { 
            tmp <<= 8; 
            tmp += data[i]; 
        }
        return tmp;
    }

    public void MemberSize(ulong sz)
    {
        for (int i = 12; i <= 19; ++i) 
        { 
            data[i] = (byte)sz; 
            sz >>= 8; 
        }
    }

    public bool CheckConsistency()
    {
        uint crc = DataCrc();
        ulong dsize = DataSize();
        if ((crc == 0) != (dsize == 0)) 
            return false;
        
        ulong msize = MemberSize();
        if (msize < Constants.min_member_size) 
            return false;
        
        ulong mlimit = (9 * dsize + 7) / 8 + Constants.min_member_size;
        if (mlimit > dsize && msize > mlimit) 
            return false;
        
        ulong dlimit = 7090 * (msize - 26) - 1;
        if (dlimit > msize && dsize > dlimit) 
            return false;
        
        return true;
    }
}

public struct ClOptions
{
    public bool ignore_trailing = true;
    public bool loose_trailing = false;
    
    public ClOptions() {}
}

public static class IOUtils
{
    public const string bad_magic_msg = "Bad magic number (file not in lzip format).";
    public const string bad_dict_msg = "Invalid dictionary size in member header.";
    public const string corrupt_mm_msg = "Corrupt header in multimember file.";
    public const string empty_msg = "Empty member not allowed.";
    public const string nonzero_msg = "Nonzero first LZMA byte.";
    public const string trailing_msg = "Trailing data not allowed.";

    public static int ReadBlock(Stream fd, byte[] buf, int buff_pos, int size)
    {
        if (buf == null) 
            throw new ArgumentNullException($"ReadBlock: {nameof(buf)}");
        if (buff_pos < 0) 
            throw new ArgumentOutOfRangeException($"ReadBlock: {nameof(buff_pos)}");
        if (size < 0 || buff_pos + size > buf.Length)
            throw new ArgumentOutOfRangeException($"ReadBlock: {nameof(size)}");
                
        int sz = 0;

        while (sz < size)
        {
            try
            {
                int rd = fd.Read(buf, buff_pos + sz, size - sz); 
                if (rd == 0) break;
                sz += rd;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Read failed at ReadBlock: {ex.Message}");
                fd?.Dispose();
                throw;
            }
        }
        return sz;
    }

    public static int WriteBlock(Stream fd, byte[] buf, int buff_pos, int size)
    {
        if (buf == null) 
            throw new ArgumentNullException($"WriteBlock: {nameof(buf)}");
        if (buff_pos < 0) 
            throw new ArgumentOutOfRangeException($"WriteBlock: {nameof(buff_pos)}");
        if (size < 0 || buff_pos + size > buf.Length)
            throw new ArgumentOutOfRangeException($"WriteBlock: {nameof(size)}");

        try
        {
            fd.Write(buf, buff_pos, size);
            return size;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Write failed at WriteBlock: {ex.Message}");
            fd?.Dispose();
            throw;
        }
    }
}

public static class DisSlots
{
    private static readonly byte[] data = new byte[1U << 10];

    public static void Init()
    {
        for (int slot = 0; slot < 4; ++slot) 
            data[slot] = (byte)slot;
        
        for (int i = 4, size = 2, slot = 4; slot < 20; slot += 2)
        {
            Array.Fill(data, (byte)slot, i, size);
            Array.Fill(data, (byte)(slot + 1), i + size, size);
            size <<= 1;
            i += size;
        }
    }

    public static byte GetSlot(uint dis)
    {
        if (dis < (1U << 10)) return data[dis];
        if (dis < (1U << 19)) return (byte)(data[dis >> 9] + 18);
        if (dis < (1U << 28)) return (byte)(data[dis >> 18] + 36);
        return (byte)(data[dis >> 27] + 54);
    }
}

public static class ProbPrices
{
    private static readonly int[] data = new int[Constants.bit_model_total >> Constants.price_step_bits];

    public static void Init()
    {
        for (int i = 0; i < data.Length; ++i)
        {
            uint val = (uint)((i * Constants.price_step) + (Constants.price_step / 2));
            int bits = 0;
            for (int j = 0; j < Constants.price_shift_bits; ++j)
            {
                val *= val;
                bits <<= 1;
                while (val >= 1U << 16) 
                { 
                    val >>= 1; 
                    ++bits; 
                }
            }
            bits += 15;
            data[i] = (Constants.bit_model_total_bits << Constants.price_shift_bits) - bits;
        }
    }

    public static int Get(int probability) 
        => data.AsSpan()[probability >> Constants.price_step_bits];
}

public static class PriceUtils                                                  
{
    public static int Price0(in BitModel bm)
        => ProbPrices.Get(bm.probability);

    public static int Price1(in BitModel bm)
        => ProbPrices.Get(Constants.bit_model_total - bm.probability);

    public static int PriceBit(in BitModel bm, bool bit)
        => bit ? Price1(bm) : Price0(bm);

    public static int PriceSymbol3(ReadOnlySpan<BitModel> bm, int symbol)       
    {
        bool bit = (symbol & 1) != 0;
        symbol |= 8; 
        symbol >>= 1;
        int price = PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        return price + PriceBit(bm[1], (symbol & 1) != 0);
    }

    public static int PriceSymbol6(ReadOnlySpan<BitModel> bm, int symbol)      
    {
        bool bit = (symbol & 1) != 0;
        symbol |= 64; 
        symbol >>= 1;
        int price = PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        return price + PriceBit(bm[1], (symbol & 1) != 0);
    }

    public static int PriceSymbol8(ReadOnlySpan<BitModel> bm, int symbol)       
    {
        bool bit = (symbol & 1) != 0;
        symbol |= 0x100; 
        symbol >>= 1;
        int price = PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        bit = (symbol & 1) != 0; 
        symbol >>= 1; 
        price += PriceBit(bm[symbol], bit);
        return price + PriceBit(bm[1], (symbol & 1) != 0);
    }

    public static int PriceSymbolReversed(ReadOnlySpan<BitModel> bm, int symbol, int num_bits)
    {
        int price = 0;
        int model = 1;
        for (int i = num_bits; i > 0; --i)
        {
            bool bit = (symbol & 1) != 0;
            symbol >>= 1;
            price += PriceBit(bm[model], bit);
            model <<= 1; 
            model |= bit ? 1 : 0;
        }
        return price;
    }
                                                                                
    public static int PriceMatched(ReadOnlySpan<BitModel> bm, uint symbol, uint match_byte)     
    {
        int price = 0;
        uint mask = 0x100U;
        symbol |= mask;
        while (true)
        {
            uint match_bit = (match_byte <<= 1) & mask;
            bool bit = ((symbol <<= 1) & 0x100U) != 0U;
            price += PriceBit(bm[(int)((symbol >> 9) + match_bit + mask)], bit);
            if (symbol >= 0x10000U) 
                return price;
            mask &= ~(match_bit ^ symbol);
        }
    }
}


















































public class MatchfinderBase
{
    protected ulong partial_data_pos;
    protected readonly byte[] buffer;
    protected readonly int[] prev_positions;
    protected Memory<int> pos_array;
    protected readonly int before_size;
    protected int buffer_size;
    protected int dictionary_size;
    protected int pos;
    protected int cyclic_pos;
    protected int stream_pos;
    protected int pos_limit;
    protected int key4_mask;
    protected readonly int num_prev_positions23;
    protected int num_prev_positions;
    protected int pos_array_size;
    protected readonly Stream infd;
    protected bool at_stream_end;

    private bool ReadBlock()
    {
        if (!at_stream_end && stream_pos < buffer_size)
        {
            int size = buffer_size - stream_pos;
            int rd = IOUtils.ReadBlock(infd, buffer, stream_pos, size);
            stream_pos += rd;
            if (rd < size)
            {
                at_stream_end = true;
                pos_limit = buffer_size;
            }
        }
        return pos < stream_pos;
    }

    private void NormalizePos()
    {
        if (pos > stream_pos)
            throw new Exception("Internal error: pos > stream_pos in normalize_pos.");
        
        if (!at_stream_end)
        {
            int offset = pos - before_size - dictionary_size;
            int size = stream_pos - offset;
            buffer.AsSpan(offset, size).CopyTo(buffer.AsSpan());
            partial_data_pos += (ulong)offset;
            pos -= offset;
            stream_pos -= offset;
            
            for (int i = 0; i < num_prev_positions; ++i)
                prev_positions[i] -= Math.Min(prev_positions[i], offset);
            
            Span<int> pos_span = pos_array.Span; 
            for (int i = 0; i < pos_array_size; ++i)
                pos_span[i] -= Math.Min(pos_span[i], offset);
            
            ReadBlock();
        }
    }

    protected MatchfinderBase(int before_size_, int dict_size, int after_size, 
                            int dict_factor, int num_prev_positions23_,
                            int pos_array_factor, Stream ifd)
    {
        partial_data_pos = 0;
        before_size = before_size_;
        pos = 0;
        cyclic_pos = 0;
        stream_pos = 0;
        num_prev_positions23 = num_prev_positions23_;
        infd = ifd;
        at_stream_end = false;

        int buffer_size_limit = (dict_factor * dict_size) + before_size + after_size;
        buffer_size = Math.Max(65536, dict_size);
        buffer = new byte[buffer_size];
        
        if (ReadBlock() && !at_stream_end && buffer_size < buffer_size_limit)
        {
            byte[] tmparr = new byte[buffer_size_limit];
            buffer.CopyTo(tmparr, 0);
            buffer = tmparr;
            buffer_size = buffer_size_limit;
            ReadBlock();  
        }

        if (at_stream_end && stream_pos < dict_size)
            dictionary_size = Math.Max(Constants.min_dictionary_size, stream_pos);
        else
            dictionary_size = dict_size;

        pos_limit = buffer_size;
        if (!at_stream_end) 
            pos_limit -= after_size;
        
        uint size = 1U << Math.Max(16, Utils.RealBits((uint)(dictionary_size - 1)) - 2);
        if (dictionary_size > 1 << 26) 
            size >>= 1;
        
        key4_mask = (int)size - 1;
        size += (uint)num_prev_positions23;
        num_prev_positions = (int)size;

        pos_array_size = pos_array_factor * (dictionary_size + 1);
        size += (uint)pos_array_size;
      
        if(size > int.MaxValue / sizeof(int)) 
            throw new Exception("size > int.MaxValue / sizeof(int)!");
        
        prev_positions = new int[size]; 
        
        pos_array = prev_positions.AsMemory(num_prev_positions);
        Array.Clear(prev_positions, 0, num_prev_positions);
    }

    public byte Peek(int distance) => buffer[pos - distance]; 
    public int AvailableBytes() => stream_pos - pos; 
    public ulong DataPosition() => partial_data_pos + (ulong)pos; 
    public bool DataFinished() => at_stream_end && pos >= stream_pos; 
    public ReadOnlySpan<byte> BufOffsetSizeToCurrentPos(int offset, int size) => buffer.AsSpan(pos + offset, size); 

    public int TrueMatchLen(int index, int distance)
    {
        int i = index;
        int len_limit = Math.Min(AvailableBytes(), Constants.max_match_len);
        while (i < len_limit && buffer[pos + i - distance] == buffer[pos + i]) 
            ++i;
        return i;
    }

    public void MovePos()
    {
        if (++cyclic_pos > dictionary_size) 
            cyclic_pos = 0;
        if (++pos >= pos_limit) 
            NormalizePos();
    }

    public void Reset()
    {
        if (stream_pos > pos)
            buffer.AsSpan(pos, stream_pos - pos).CopyTo(buffer.AsSpan());
        
        partial_data_pos = 0;
        stream_pos -= pos;
        pos = 0;
        cyclic_pos = 0;
        ReadBlock();
        
        if (at_stream_end && stream_pos < dictionary_size)
        {
            dictionary_size = Math.Max(Constants.min_dictionary_size, stream_pos);
            int size = 1 << Math.Max(16, Utils.RealBits((uint)(dictionary_size - 1)) - 2);
            if (dictionary_size > 1 << 26) 
                size >>= 1;
            
            key4_mask = size - 1;
            size += num_prev_positions23;
            num_prev_positions = size;
            pos_array = prev_positions.AsMemory(num_prev_positions);
        }
        
        Array.Clear(prev_positions, 0, num_prev_positions);    
    }
}

public sealed class RangeEncoder
{
    private const int buffer_size = 65536;
    private ulong low;
    private ulong partial_member_pos;
    private readonly byte[] buffer;
    private int pos;
    private uint range;
    private uint ff_count;
    private readonly Stream outfd;
    private byte cache;
    
    private readonly LzipHeader header = new();

    
    private void ShiftLow()
    {
        if ((low >> 24) != 0xFFUL)
        {
            bool carry = low > 0xFFFFFFFFUL;
            PutByte((byte)(cache + (carry ? 1 : 0)));
            for (; ff_count > 0; --ff_count) 
                PutByte((byte)(0xFF + (carry ? 1 : 0)));
            cache = (byte)(low >> 24);
        }
        else 
            ++ff_count;
        
        low = (low & 0x00FFFFFFUL) << 8;
    }

    public void Reset(uint dictionary_size)
    {
        low = 0;
        partial_member_pos = 0;
        pos = 0;
        range = 0xFFFFFFFFU;
        ff_count = 0;
        cache = 0;
        header.SetDictionarySize(dictionary_size);
        for (int i = 0; i < LzipHeader.Size; ++i) 
            PutByte(header.data[i]);
    }

    public RangeEncoder(uint dictionary_size, Stream ofd)
    {
        buffer = new byte[buffer_size];
        outfd = ofd;
        header.SetMagic();
        Reset(dictionary_size);
    }

    public ulong MemberPosition() => partial_member_pos + (ulong)pos + ff_count; 

    public void Flush() { for (int i = 0; i < 5; ++i) ShiftLow(); }
    
    public void FlushData()
    {
        if (pos > 0)
        {
            if (outfd != null) 
                IOUtils.WriteBlock(outfd, buffer, 0, pos);
            
            partial_member_pos += (ulong)pos;
            pos = 0;
            Program.ShowCProgress();
        }
    }

    public void PutByte(byte b)
    {
        buffer[pos] = b;
        if (++pos >= buffer_size) 
            FlushData();
    }

    public void Encode(int symbol, int num_bits)
    {
        for (uint mask = 1U << (num_bits - 1); mask > 0U; mask >>= 1)
        {
            range >>= 1;
            if ((symbol & mask) != 0) 
                low += range;
            
            if (range <= 0x00FFFFFFU) 
            { 
                range <<= 8; 
                ShiftLow(); 
            }
        }
    }

    public void EncodeBit(ref BitModel bm, bool bit)
    {
        uint bound = (range >> Constants.bit_model_total_bits) * (uint)bm.probability;
        if (!bit)
        {
            range = bound;
            bm.probability += (Constants.bit_model_total - bm.probability) >> Constants.bit_model_move_bits;
        }
        else
        {
            low += bound;
            range -= bound;
            bm.probability -= bm.probability >> Constants.bit_model_move_bits;
        }
        if (range <= 0x00FFFFFFU) 
        { 
            range <<= 8; 
            ShiftLow(); 
        }
    }

    public void EncodeTree3(Span<BitModel> bm, int symbol)                      
    {
        bool bit = (((symbol >> 2) & 1) != 0);
        EncodeBit(ref bm[1], bit);
        int model = 2 | (bit ? 1 : 0);
        bit = (((symbol >> 1) & 1) != 0);
        EncodeBit(ref bm[model], bit); 
        model <<= 1; 
        model |= bit ? 1 : 0;
        EncodeBit(ref bm[model], (symbol & 1) != 0);
    }

    public void EncodeTree6(Span<BitModel> bm, uint symbol)                     
    {
        bool bit = (((symbol >> 5) & 1) != 0);
        EncodeBit(ref bm[1], bit);
        int model = 2 | (bit ? 1 : 0);
        bit = (((symbol >> 4) & 1) != 0);
        EncodeBit(ref bm[model], bit); 
        model <<= 1; 
        model |= bit ? 1 : 0;
        bit = (((symbol >> 3) & 1) != 0);
        EncodeBit(ref bm[model], bit); 
        model <<= 1; 
        model |= bit ? 1 : 0;
        bit = (((symbol >> 2) & 1) != 0);
        EncodeBit(ref bm[model], bit); 
        model <<= 1; 
        model |= bit ? 1 : 0;
        bit = (((symbol >> 1) & 1) != 0);
        EncodeBit(ref bm[model], bit); 
        model <<= 1; 
        model |= bit ? 1 : 0;
        EncodeBit(ref bm[model], (symbol & 1) != 0);
    }

    public void EncodeTree8(Span<BitModel> bm, int symbol)                      
    {
        int model = 1;
        for (int i = 7; i >= 0; --i)
        {
            bool bit = ((symbol >> i) & 1) != 0;
            EncodeBit(ref bm[model], bit);
            model <<= 1; 
            model |= bit ? 1 : 0;
        }
    }

    public void EncodeTreeReversed(Span<BitModel> bm, int symbol, int num_bits)
    {
        int model = 1;
        for (int i = num_bits; i > 0; --i)
        {
            bool bit = (symbol & 1) != 0;
            symbol >>= 1;
            EncodeBit(ref bm[model], bit);
            model <<= 1; 
            model |= bit ? 1 : 0;
        }
    }

    public void EncodeMatched(Span<BitModel> bm, uint symbol, uint match_byte)  
    {
        uint mask = 0x100U;
        symbol |= mask;
        while (true)
        {
            uint match_bit = (match_byte <<= 1) & mask;
            bool bit = ((symbol <<= 1) & 0x100U) != 0U;
            EncodeBit(ref bm[(int)((symbol >> 9) + match_bit + mask)], bit);
            if (symbol >= 0x10000U) 
                break;
            mask &= ~(match_bit ^ symbol);
        }
    }

    public void EncodeLen(LenModel lm, int symbol, int pos_state)
    {
        bool bit = (symbol -= Constants.min_match_len) >= Constants.len_low_symbols;
        EncodeBit(ref lm.choice1, bit);
        if (!bit)
            EncodeTree3(lm.bm_low_row(pos_state), symbol);
        else
        {
            bit = (symbol -= Constants.len_low_symbols) >= Constants.len_mid_symbols;
            EncodeBit(ref lm.choice2, bit);
            if (!bit)
                EncodeTree3(lm.bm_mid_row(pos_state), symbol);
            else
                EncodeTree8(lm.bm_high, symbol - Constants.len_mid_symbols);
        }
    }
}

public abstract class LZEncoderBase : MatchfinderBase
{
    protected const int bm_dis_slot_y_sz = 1 << Constants.dis_slot_bits;

    protected uint crc;
    protected readonly BitModel[] _bm_literal = new BitModel[(1 << Constants.literal_context_bits) * 0x300];           //was 2D array
    protected readonly BitModel[] _bm_match = new BitModel[State.states * Constants.pos_states];                       //was 2D array
    protected readonly BitModel[] bm_rep = new BitModel[State.states];
    protected readonly BitModel[] bm_rep0 = new BitModel[State.states];
    protected readonly BitModel[] bm_rep1 = new BitModel[State.states];
    protected readonly BitModel[] bm_rep2 = new BitModel[State.states];
    protected readonly BitModel[] _bm_len = new BitModel[State.states * Constants.pos_states];                         //was 2D array
    protected readonly BitModel[] _bm_dis_slot = new BitModel[Constants.len_states * bm_dis_slot_y_sz];                //was 2D array
    protected readonly BitModel[] bm_dis = new BitModel[Constants.modeled_distances - Constants.end_dis_model + 1];
    protected readonly BitModel[] bm_align = new BitModel[Constants.dis_align_size];

    protected readonly LenModel match_len_model = new();
    protected readonly LenModel rep_len_model = new();
    protected readonly RangeEncoder renc;                         //init later in constructor

    
    protected LZEncoderBase(int before_size, int dict_size, int after_size, 
                          int dict_factor, int num_prev_positions23,
                          int pos_array_factor, Stream ifd, Stream outfd)
        : base(before_size, dict_size, after_size, dict_factor,
              num_prev_positions23, pos_array_factor, ifd)
    {
        crc = 0xFFFFFFFFU;
        renc = new RangeEncoder((uint)dictionary_size, outfd);
    
        foreach (ref BitModel b in _bm_literal.AsSpan())  b = new();
        foreach (ref BitModel b in _bm_match.AsSpan())    b = new();
        foreach (ref BitModel b in bm_rep.AsSpan())       b = new();
        foreach (ref BitModel b in bm_rep0.AsSpan())      b = new();
        foreach (ref BitModel b in bm_rep1.AsSpan())      b = new();
        foreach (ref BitModel b in bm_rep2.AsSpan())      b = new();
        foreach (ref BitModel b in _bm_len.AsSpan())      b = new();
        foreach (ref BitModel b in _bm_dis_slot.AsSpan()) b = new();
        foreach (ref BitModel b in bm_dis.AsSpan())       b = new();
        foreach (ref BitModel b in bm_align.AsSpan())     b = new();
    }
    
    protected Span<BitModel> bm_literal_row(int row)
        => _bm_literal.AsSpan(row * 0x300, 0x300);

    protected Span<BitModel> bm_match_row(int row)
        => _bm_match.AsSpan(row * Constants.pos_states, Constants.pos_states);

    protected Span<BitModel> bm_len_row(int row)
        => _bm_len.AsSpan(row * Constants.pos_states, Constants.pos_states);

    protected Span<BitModel> bm_dis_slot_row(int row)
        => _bm_dis_slot.AsSpan(row * bm_dis_slot_y_sz, bm_dis_slot_y_sz);
    
    
    protected uint Crc() => crc ^ 0xFFFFFFFFU; 

    protected int PriceLiteral(byte prev_byte, byte symbol)
        => PriceUtils.PriceSymbol8(bm_literal_row(Utils.GetLitState(prev_byte)), symbol);

    protected int PriceMatched(byte prev_byte, byte symbol, byte match_byte)
        => PriceUtils.PriceMatched(bm_literal_row(Utils.GetLitState(prev_byte)), symbol, match_byte);

    protected void EncodeLiteral(byte prev_byte, byte symbol)
        => renc.EncodeTree8(bm_literal_row(Utils.GetLitState(prev_byte)), symbol);

    protected void EncodeMatched(byte prev_byte, byte symbol, byte match_byte)
        => renc.EncodeMatched(bm_literal_row(Utils.GetLitState(prev_byte)), symbol, match_byte);

    protected void EncodePair(uint dis, int len, int pos_state)
    {
        renc.EncodeLen(match_len_model, len, pos_state);
        uint dis_slot = DisSlots.GetSlot(dis);
        renc.EncodeTree6(bm_dis_slot_row(Utils.GetLenState(len)), dis_slot);

        if (dis_slot >= Constants.start_dis_model)
        {
            int direct_bits = (int)(dis_slot >> 1) - 1;
            uint base_val = (2U | (dis_slot & 1U)) << direct_bits;
            uint direct_dis = dis - base_val;

            if (dis_slot < Constants.end_dis_model)
                renc.EncodeTreeReversed(bm_dis.AsSpan((int)(base_val - dis_slot)),
                                      (int)direct_dis, direct_bits);
            else
            {
                renc.Encode((int)(direct_dis >> Constants.dis_align_bits), 
                            direct_bits - Constants.dis_align_bits);
                renc.EncodeTreeReversed(bm_align, (int)direct_dis, 
                            Constants.dis_align_bits);
            }
        }
    }
    
    protected void FullFlush(in State state)
    {
        int pos_state = (int)(DataPosition() & Constants.pos_state_mask);
        renc.EncodeBit(ref bm_match_row(state.Get())[pos_state], true);
        renc.EncodeBit(ref bm_rep[state.Get()], false);
        EncodePair(0xFFFFFFFFU, Constants.min_match_len, pos_state);
        renc.Flush();
        
        LzipTrailer trailer = new();
        
        trailer.DataCrc(Crc());
        trailer.DataSize(DataPosition());
        trailer.MemberSize(renc.MemberPosition() + LzipTrailer.Size);
        for (int i = 0; i < LzipTrailer.Size; ++i) 
            renc.PutByte(trailer.data[i]);
        
        renc.FlushData();
    }

    public ulong MemberPosition() => renc.MemberPosition(); 

    public new virtual void Reset()
    {
        base.Reset();
        crc = 0xFFFFFFFFU;
        
        BitModel.Reset(_bm_literal.AsSpan());
        BitModel.Reset(_bm_match.AsSpan());
        BitModel.Reset(bm_rep.AsSpan());
        BitModel.Reset(bm_rep0.AsSpan());
        BitModel.Reset(bm_rep1.AsSpan());
        BitModel.Reset(bm_rep2.AsSpan());
        BitModel.Reset(_bm_len.AsSpan());
        BitModel.Reset(_bm_dis_slot.AsSpan());
        BitModel.Reset(bm_dis.AsSpan());
        BitModel.Reset(bm_align.AsSpan());

        match_len_model.Reset();
        rep_len_model.Reset();
        renc.Reset((uint)dictionary_size);
    }

    public abstract bool EncodeMember(ulong member_size);
}

public sealed class LenPrices
{
    private readonly LenModel lm;                    //assignment later in constructor
    
    private readonly int len_symbols;
    private readonly int count;
    private readonly int[] _prices = new int[Constants.pos_states * Constants.max_len_symbols];   //was 2D array
    private readonly int[] counters = new int[Constants.pos_states];

    private Span<int> prices_row(int row)
        => _prices.AsSpan(row * Constants.max_len_symbols, Constants.max_len_symbols);
    
    
    private void UpdateLowMidPrices(int pos_state)
    {
        Span<int> pps = prices_row(pos_state);
        int tmp = PriceUtils.Price0(lm.choice1);
        int len = 0;
    
        for (; len < Constants.len_low_symbols && len < len_symbols; ++len)
            pps[len] = tmp + PriceUtils.PriceSymbol3(lm.bm_low_row(pos_state), len);
        if (len >= len_symbols) 
            return;
        tmp = PriceUtils.Price1(lm.choice1) + PriceUtils.Price0(lm.choice2);
        for (; len < Constants.len_low_symbols + Constants.len_mid_symbols && len < len_symbols; ++len)
            pps[len] = tmp + PriceUtils.PriceSymbol3(lm.bm_mid_row(pos_state), len - Constants.len_low_symbols);
    }

    private void UpdateHighPrices()
    {
        int tmp = PriceUtils.Price1(lm.choice1) + PriceUtils.Price1(lm.choice2);
        for (int len = Constants.len_low_symbols + Constants.len_mid_symbols; 
             len < len_symbols; len++)
        {
            int price = tmp + PriceUtils.PriceSymbol8(lm.bm_high, 
                len - Constants.len_low_symbols - Constants.len_mid_symbols);
            
            // Using 4 slots per value makes "price" faster
            prices_row(0)[len] = price;
            prices_row(1)[len] = price;
            prices_row(2)[len] = price;
            prices_row(3)[len] = price;
        }
    }

    public void Reset() 
    { 
        Array.Fill(counters, 0); 
    }

    public LenPrices(LenModel m, int match_len_limit)
    {
        lm = m;
        len_symbols = match_len_limit + 1 - Constants.min_match_len;
        count = (match_len_limit > 12) ? 1 : len_symbols;
        Reset();
    }

    public void DecrementCounter(int pos_state) 
    { 
        --counters[pos_state]; 
    }

    public void UpdatePrices()
    {
        bool high_pending = false;
        for (int pos_state = 0; pos_state < Constants.pos_states; ++pos_state)
        {
            if (counters[pos_state] <= 0)
            {
                counters[pos_state] = count;
                UpdateLowMidPrices(pos_state); 
                high_pending = true;
            }
        }
        
        if (high_pending && len_symbols > Constants.len_low_symbols + Constants.len_mid_symbols)
            UpdateHighPrices();
    }

    public int Price(int len, int pos_state) 
        => prices_row(pos_state)[len - Constants.min_match_len]; 
}

public sealed class LZEncoder : LZEncoderBase
{
    private const int dis_slot_prices_y_sz = 2 * Constants.max_dictionary_bits;
 
    private struct Pair
    {
        public int dis;
        public int len;
    }

    private struct Trial
    {
        [InlineArray(Constants.num_rep_distances)]
        public struct _Reps_array                   //helper struct for array
        {
            private int _reps_array;
        }

        public State state = new();
        
        public int price;
        public int dis4;
        public int prev_index;
        public int prev_index2;

        public _Reps_array reps;   //array packed in Trial for fast access

        
        public Trial() {}

        public void Update(int pr, int distance4, int p_i)
        {
            if (pr < price)
            {
                price = pr; 
                dis4 = distance4; 
                prev_index = p_i;
                prev_index2 = Constants.single_step_trial;
            }
        }

        public void Update2(int pr, int p_i)
        {
            if (pr < price)
            {
                price = pr; 
                dis4 = 0; 
                prev_index = p_i;
                prev_index2 = Constants.dual_step_trial;
            }
        }

        public void Update3(int pr, int distance4, int p_i, int p_i2)
        {
            if (pr < price)
            {
                price = pr; 
                dis4 = distance4; 
                prev_index = p_i;
                prev_index2 = p_i2;
            }
        }
    }

    private readonly int cycles;
    private readonly int match_len_limit;
    
    private readonly LenPrices match_len_prices;                                //later in constructor
    private readonly LenPrices rep_len_prices;                                  //later in constructor  
    
    private int pending_num_pairs;
    
    private readonly Pair[] pairs = new Pair[Constants.max_match_len + 1];
    private readonly Trial[] trials = new Trial[Constants.max_num_trials];

    private readonly int[] _dis_slot_prices = new int[Constants.len_states * dis_slot_prices_y_sz];        //was 2D array
    private readonly int[] _dis_prices = new int[Constants.len_states * Constants.modeled_distances];      //was 2D array
    private readonly int[] align_prices = new int[Constants.dis_align_size];
    private readonly int num_dis_slots;


    private Span<int> dis_slot_prices_row(int row)
        => _dis_slot_prices.AsSpan(row * dis_slot_prices_y_sz, dis_slot_prices_y_sz);

    private Span<int> dis_prices_row(int row)
        => _dis_prices.AsSpan(row * Constants.modeled_distances, Constants.modeled_distances);            
    
    
    private bool DecPos(int ahead)
    {
        if (ahead < 0 || pos < ahead) 
            return false;
        
        pos -= ahead;
        if (cyclic_pos < ahead) 
            cyclic_pos += dictionary_size + 1;
        
        cyclic_pos -= ahead;
        return true;
    }

    private int GetMatchPairs(Pair[]? pairs = null)
    {
        int len_limit = match_len_limit;
        if (len_limit > AvailableBytes())
        {
            len_limit = AvailableBytes();
            if (len_limit < 4) 
                return 0;
        }

        int maxlen = 3;
        int num_pairs = 0;
        int min_pos = (pos > dictionary_size) ? pos - dictionary_size : 0;

        Span<byte> buf = buffer.AsSpan();
        Span<Pair> spairs = pairs.AsSpan(); 
        ref byte currentPos = ref buf[pos];
        uint tmp = CRC32.Get(currentPos) ^ buf[pos+1];
        int key2 = (int)(tmp & (Constants.num_prev_positions2 - 1));
        tmp ^= (uint)(buf[pos+2] << 8);
        int key3 = Constants.num_prev_positions2 + (int)(tmp & (Constants.num_prev_positions3 - 1));
        int key4 = Constants.num_prev_positions23 + 
                  (int)((tmp ^ (CRC32.Get(buf[pos+3]) << 5)) & key4_mask);

        if (pairs != null)
        {
            int np2 = prev_positions[key2];
            int np3 = prev_positions[key3];
            if (np2 > min_pos && buf[np2 - 1] == currentPos)
            {
                spairs[0].dis = pos - np2;
                spairs[0].len = maxlen = 2 + (np2 == np3 ? 1 : 0);
                num_pairs = 1;
            }
            if (np2 != np3 && np3 > min_pos && buf[np3 - 1] == currentPos)
            {
                maxlen = 3;
                spairs[num_pairs++].dis = pos - np3;
            }
            if (num_pairs > 0)
            {
                int delta = spairs[num_pairs-1].dis + 1;
                while (maxlen < len_limit && buf[pos + maxlen - delta] == buf[pos + maxlen])
                    ++maxlen;
              
                spairs[num_pairs-1].len = maxlen;
                if (maxlen < 3) 
                    maxlen = 3;
                if (maxlen >= len_limit) 
                    pairs = null;
            }
        }

        int pos1 = pos + 1;
        prev_positions[key2] = pos1;
        prev_positions[key3] = pos1;
        int newpos1 = prev_positions[key4];
        prev_positions[key4] = pos1;

        Span<int> sposArray = pos_array.Span;
        ref int ptr0 = ref sposArray[cyclic_pos << 1];
        ref int ptr1 = ref Unsafe.Add(ref ptr0, 1); 
        int len = 0, len0 = 0, len1 = 0;

        for (int count = cycles; ; )
        {
            if (newpos1 <= min_pos || --count < 0)
            {
                ptr0 = 0; // Direct assignment (like *ptr0 = 0 in C++)
                ptr1 = 0;
                break;
            }

            int delta = pos1 - newpos1;
            Span<int> snewptr = sposArray.Slice((cyclic_pos - delta + 
                                 (cyclic_pos >= delta ? 0 : dictionary_size + 1)) << 1);

            if (Unsafe.Add(ref currentPos, len - delta) == Unsafe.Add(ref currentPos, len))
            {
                while (++len < len_limit && Unsafe.Add(ref currentPos, len - delta) == 
                                            Unsafe.Add(ref currentPos, len)) {}

                if (pairs != null && maxlen < len)
                {
                    spairs[num_pairs].dis = delta - 1;
                    spairs[num_pairs].len = maxlen = len;
                    ++num_pairs;
                }
                if (len >= len_limit)
                {
                    ptr0 = snewptr[0];              // *ptr0 = newptr[0] in C++
                    ptr1 = snewptr[1];    
                    break;
                }
            }

            if (Unsafe.Add(ref currentPos, len - delta) < Unsafe.Add(ref currentPos, len))
            {
                ptr0 = newpos1;
                ptr0 = ref snewptr[1];        
                newpos1 = ptr0;
                len0 = len; if (len1 < len) len = len1;
            }
            else
            {
                ptr1 = newpos1;
                ptr1 = ref snewptr[0]; 
                newpos1 = ptr1;
                len1 = len; if (len0 < len) len = len0;
            }
        }

        return num_pairs;
    }

    private void UpdateDistancePrices()
    {
        for (int dis = Constants.start_dis_model; dis < Constants.modeled_distances; ++dis)
        {
            int dis_slot = DisSlots.GetSlot((uint)dis);
            int direct_bits = (dis_slot >> 1) - 1;
            int base_val = (2 | (dis_slot & 1)) << direct_bits;
            int price = PriceUtils.PriceSymbolReversed(bm_dis.AsSpan(base_val - dis_slot),
                                                     dis - base_val, direct_bits);
            
            for (int len_state = 0; len_state < Constants.len_states; ++len_state)
            {    
                dis_prices_row(len_state)[dis] = price;
            }
        }

        for (int len_state = 0; len_state < Constants.len_states; ++len_state)
        {
            Span<int> dsp = dis_slot_prices_row(len_state); 
            ReadOnlySpan<BitModel> bmds = bm_dis_slot_row(len_state);
            int slot = 0;
        
            for ( ; slot < Constants.end_dis_model; ++slot)
                dsp[slot] = PriceUtils.PriceSymbol6(bmds, slot);
            
            for ( ; slot < num_dis_slots; ++slot)
                dsp[slot] = 
                    PriceUtils.PriceSymbol6(bmds, slot) +
                    (int)((((slot >> 1) - 1) - Constants.dis_align_bits) << Constants.price_shift_bits);
            
            Span<int> dp = dis_prices_row(len_state);
            int dis = 0;
            for ( ; dis < Constants.start_dis_model; ++dis)
                dp[dis] = dsp[dis];
            for ( ; dis < Constants.modeled_distances; ++dis)
                dp[dis] += dsp[DisSlots.GetSlot((uint)dis)];
        }
    }

    private static void MtfReps(int dis4, Span<int> reps)
    {
        if (dis4 >= Constants.num_rep_distances)
        {
            reps[3] = reps[2]; 
            reps[2] = reps[1]; 
            reps[1] = reps[0];
            reps[0] = dis4 - Constants.num_rep_distances;
        }
        else if (dis4 > 0)
        {
            int distance = reps[dis4];
            for (int i = dis4; i > 0; --i) 
                reps[i] = reps[i - 1];
            reps[0] = distance;
        }
    }

    private int PriceShortrep(in State state, int pos_state)
        => PriceUtils.Price0(bm_rep0[state.Get()]) + 
            PriceUtils.Price0(bm_len_row(state.Get())[pos_state]);

    private int PriceRep(int rep, in State state, int pos_state)
    {
        if (rep == 0)
            return PriceUtils.Price0(bm_rep0[state.Get()]) + 
                   PriceUtils.Price1(bm_len_row(state.Get())[pos_state]);
        
        int price = PriceUtils.Price1(bm_rep0[state.Get()]);
        if (rep == 1)
            price += PriceUtils.Price0(bm_rep1[state.Get()]);
        else
        {
            price += PriceUtils.Price1(bm_rep1[state.Get()]);
            price += PriceUtils.PriceBit(bm_rep2[state.Get()], (rep - 2) != 0);
        }
        return price;
    }

    private int PriceRep0Len(int len, in State state, int pos_state)
        => PriceRep(0, state, pos_state) + rep_len_prices.Price(len, pos_state);

    private int PricePair(int dis, int len, int pos_state)
    {
        int price = match_len_prices.Price(len, pos_state);
        int len_state = Utils.GetLenState(len);
        
        if (dis < Constants.modeled_distances)
            return price + dis_prices_row(len_state)[dis];
        else
            return price + dis_slot_prices_row(len_state)[DisSlots.GetSlot((uint)dis)] +
                   align_prices[dis & (Constants.dis_align_size - 1)];
    }

    private int ReadMatchDistances()
    {
        int num_pairs = GetMatchPairs(pairs);
        if (num_pairs > 0)
        {
            ref Pair pair = ref pairs[num_pairs - 1];
            int len = pair.len;
            if (len == match_len_limit && len < Constants.max_match_len) 
                pair.len = TrueMatchLen(len, pair.dis + 1);
        }
        return num_pairs;
    }

    private void MoveAndUpdate(int n)
    {
        while (true)
        {
            MovePos();
            if (--n <= 0) 
                break;
            GetMatchPairs();
        }
    }

    private void Backward(int cur)
    {
        int dis4 = trials[cur].dis4;
        while (cur > 0)
        {
            ref Trial cur_trial = ref trials[cur];
            int prev_index = cur_trial.prev_index;
            ref Trial prev_trial = ref trials[prev_index];

            if (cur_trial.prev_index2 != Constants.single_step_trial)
            {
                prev_trial.dis4 = -1;
                prev_trial.prev_index = prev_index - 1;
                prev_trial.prev_index2 = Constants.single_step_trial;
                
                if (cur_trial.prev_index2 >= 0)
                {
                    ref Trial prev_trial2 = ref trials[prev_index - 1];
                    prev_trial2.dis4 = dis4; 
                    dis4 = 0;
                    prev_trial2.prev_index = cur_trial.prev_index2;
                    prev_trial2.prev_index2 = Constants.single_step_trial;
                }
            }
            
            prev_trial.price = cur - prev_index;
            cur = dis4; 
            dis4 = prev_trial.dis4; 
            prev_trial.dis4 = cur;
            cur = prev_index;
        }
    }

    private int SequenceOptimizer(ReadOnlySpan<int> reps, in State state)
    {
        int num_pairs, num_trials;

        if (pending_num_pairs > 0)
        {
            num_pairs = pending_num_pairs;
            pending_num_pairs = 0;
        }
        else
            num_pairs = ReadMatchDistances();
        
        ReadOnlySpan<byte> buf = buffer.AsSpan();                         
        ReadOnlySpan<Pair> spairs = pairs.AsSpan();  
        Span<Trial> strials = trials.AsSpan();
        Span<int> replens = new int[Constants.num_rep_distances];

        int main_len = (num_pairs > 0) ? spairs[num_pairs-1].len : 0;

        int rep_index = 0;
        for (int i = 0; i < Constants.num_rep_distances; ++i)
        {
            replens[i] = TrueMatchLen(0, reps[i] + 1);
            if (replens[i] > replens[rep_index])
                rep_index = i;
        }
        
        if (replens[rep_index] >= match_len_limit)
        {
            strials[0].price = replens[rep_index];
            strials[0].dis4 = rep_index;
            MoveAndUpdate(replens[rep_index]);
            return replens[rep_index];
        }

        if (main_len >= match_len_limit)
        {
            strials[0].price = main_len;
            strials[0].dis4 = spairs[num_pairs-1].dis + Constants.num_rep_distances;
            MoveAndUpdate(main_len);
            return main_len;
        }

        int pos_state = (int)(DataPosition() & Constants.pos_state_mask);
        byte prev_byte = Peek(1);
        byte cur_byte = Peek(0);
        byte match_byte = Peek(reps[0] + 1);

        strials[1].price = PriceUtils.Price0(bm_match_row(state.Get())[pos_state]);
        if (state.IsChar())
            strials[1].price += PriceLiteral(prev_byte, cur_byte);
        else
            strials[1].price += PriceMatched(prev_byte, cur_byte, match_byte);
        
        strials[1].dis4 = -1;

        int match_price = PriceUtils.Price1(bm_match_row(state.Get())[pos_state]);
        int rep_match_price = match_price + PriceUtils.Price1(bm_rep[state.Get()]);

        if (match_byte == cur_byte)
            strials[1].Update(rep_match_price + PriceShortrep(state, pos_state), 0, 0);

        num_trials = Math.Max(main_len, replens[rep_index]);

        if (num_trials < Constants.min_match_len)
        {
            strials[0].price = 1;
            strials[0].dis4 = strials[1].dis4;
            MovePos();
            return 1;
        }

        strials[0].state = state;
        for (int i = 0; i < Constants.num_rep_distances; ++i)
            strials[0].reps[i] = reps[i];

        for (int len = Constants.min_match_len; len <= num_trials; len++)
            strials[len].price = Constants.infinite_price;


        for (int rep = 0; rep < Constants.num_rep_distances; ++rep)
        {
            if (replens[rep] < Constants.min_match_len) continue;
            int price = rep_match_price + PriceRep(rep, state, pos_state);
            for (int len = Constants.min_match_len; len <= replens[rep]; len++)
                strials[len].Update(price + rep_len_prices.Price(len, pos_state), rep, 0);
        }

        if (main_len > replens[0])
        {
            int normal_match_price = match_price + PriceUtils.Price0(bm_rep[state.Get()]);
            int i = 0, len = Math.Max(replens[0] + 1, Constants.min_match_len);
            while (len > spairs[i].len) 
                i++;
            
            while (true)
            {
                int dis = spairs[i].dis;
                strials[len].Update(normal_match_price + PricePair(dis, len, pos_state),
                                  dis + Constants.num_rep_distances, 0);
                if (++len > spairs[i].len && ++i >= num_pairs) 
                    break;
            }
        }

        int current = 0;
        while (true)                                                            
        {
            MovePos();
            if (++current >= num_trials)
            {
                Backward(current);
                return current;
            }

            num_pairs = ReadMatchDistances();      
            int newlen = (num_pairs > 0) ? spairs[num_pairs - 1].len : 0;
            if (newlen >= match_len_limit)
            {
                pending_num_pairs = num_pairs;
                Backward(current);
                return current;
            }

            ref Trial cur_trial = ref strials[current];                          
            State cur_state = new();                                           

            int dis4 = cur_trial.dis4;
            int prev_index = cur_trial.prev_index;
            int prev_index2 = cur_trial.prev_index2;
            if (prev_index2 == Constants.single_step_trial)
            {
                cur_state = strials[prev_index].state;
                if (prev_index + 1 == current)
                {
                    if (dis4 == 0) 
                        cur_state.SetShortRep();
                    else 
                        cur_state.SetChar();
                }
                else if (dis4 < Constants.num_rep_distances) 
                    cur_state.SetRep();
                else 
                    cur_state.SetMatch();
            }
            else
            {
                if (prev_index2 == Constants.dual_step_trial)
                    --prev_index;
                else
                    prev_index = prev_index2;
                cur_state.SetCharRep();
            }
            
            cur_trial.state = cur_state;
            ref Trial trial = ref strials[prev_index];             //helper
            for (int i = 0; i < Constants.num_rep_distances; ++i)
                cur_trial.reps[i] = trial.reps[i];
            
            MtfReps(dis4, cur_trial.reps);

            pos_state = (int)(DataPosition() & Constants.pos_state_mask);     
            prev_byte = Peek(1);                                              
            cur_byte = Peek(0);                                               
            match_byte = Peek(cur_trial.reps[0] + 1);                         

            int next_price = cur_trial.price +                                  
                             PriceUtils.Price0(bm_match_row(cur_state.Get())[pos_state]);
            if (cur_state.IsChar())
                next_price += PriceLiteral(prev_byte, cur_byte);
            else
                next_price += PriceMatched(prev_byte, cur_byte, match_byte);

            ref Trial next_trial = ref strials[current + 1];
            next_trial.Update(next_price, -1, current);

            match_price = cur_trial.price + PriceUtils.Price1(bm_match_row(cur_state.Get())[pos_state]);    
            rep_match_price = match_price + PriceUtils.Price1(bm_rep[cur_state.Get()]);                     

            if (match_byte == cur_byte && next_trial.dis4 != 0 &&
                next_trial.prev_index2 == Constants.single_step_trial)
            {
                int price = rep_match_price + PriceShortrep(cur_state, pos_state);
                if (price <= next_trial.price)
                {
                    next_trial.price = price;
                    next_trial.dis4 = 0;
                    next_trial.prev_index = current;
                }
            }

            int triable_bytes = Math.Min(AvailableBytes(), 
                        Constants.max_num_trials - 1 - current);
            if (triable_bytes < Constants.min_match_len) 
                continue;
            
            int len_limit = Math.Min(match_len_limit, triable_bytes);

            if (match_byte != cur_byte && next_trial.prev_index != current)
            {
                int dis = cur_trial.reps[0] + 1;
                int limit = Math.Min(match_len_limit + 1, triable_bytes);
                int len = 1;
                while (len < limit && buf[pos + len - dis] == buf[pos + len])    
                    len++;
                
                if (--len >= Constants.min_match_len)
                {
                    int pos_state2 = (pos_state + 1) & Constants.pos_state_mask;
                    State state2 = cur_state; 
                    state2.SetChar();
                    int price = next_price +
                                PriceUtils.Price1(bm_match_row(state2.Get())[pos_state2]) +
                                PriceUtils.Price1(bm_rep[state2.Get()]) +
                                PriceRep0Len(len, state2, pos_state2);
                    
                    while (num_trials < current + 1 + len)
                        strials[++num_trials].price = Constants.infinite_price;

                    strials[current + 1 + len].Update2(price, current + 1);
                }
            }

            int start_len = Constants.min_match_len;

            for (int rep = 0; rep < Constants.num_rep_distances; ++rep)
            {
                int dis = cur_trial.reps[rep] + 1;
                int len;

                if (buf[pos - dis] != buf[pos] || buf[pos + 1 - dis] != buf[pos + 1]) 
                    continue;
                
                for (len = Constants.min_match_len; len < len_limit; len++) 
                    if(buf[pos + len - dis] != buf[pos + len]) break;     
                
                while (num_trials < current + len)
                    strials[++num_trials].price = Constants.infinite_price;

                int price = rep_match_price + PriceRep(rep, cur_state, pos_state);
                
                for (int i = Constants.min_match_len; i <= len; i++)
                    strials[current + i].Update(price + rep_len_prices.Price(i, pos_state), rep, current);

                if (rep == 0) 
                    start_len = len + 1;

                int len2 = len + 1;
                int limit2 = Math.Min(match_len_limit + len2, triable_bytes);
                while (len2 < limit2 && buf[pos + len2 - dis] == buf[pos + len2])    
                    len2++;
                
                len2 -= len + 1;
                if (len2 < Constants.min_match_len) 
                    continue;
                
                int pos_state2 = (pos_state + len) & Constants.pos_state_mask;
                State state2 = cur_state; 
                state2.SetRep();
                price += rep_len_prices.Price(len, pos_state) +
                         PriceUtils.Price0(bm_match_row(state2.Get())[pos_state2]) +
                         PriceMatched(buf[pos + len - 1], buf[pos + len], buf[pos + len - dis]);
                
                pos_state2 = (pos_state2 + 1) & Constants.pos_state_mask;
                state2.SetChar();
                price += PriceUtils.Price1(bm_match_row(state2.Get())[pos_state2]) +
                         PriceUtils.Price1(bm_rep[state2.Get()]) +
                         PriceRep0Len(len2, state2, pos_state2);
                
                while (num_trials < current + len + 1 + len2)
                    strials[++num_trials].price = Constants.infinite_price;

                strials[current + len + 1 + len2].Update3(price, rep, current + len + 1, current);
            }

            if (newlen >= start_len && newlen <= len_limit)
            {
                int normal_match_price = match_price +
                                        PriceUtils.Price0(bm_rep[cur_state.Get()]);

                while (num_trials < current + newlen)
                    strials[++num_trials].price = Constants.infinite_price;

                int i = 0;
                while (spairs[i].len < start_len) 
                    i++;
                
                int dis = spairs[i].dis;
                for (int len = start_len; ; len++)
                {
                    int price = normal_match_price + PricePair(dis, len, pos_state);
                    strials[current + len].Update(price, dis + Constants.num_rep_distances, current);

                    if (len == spairs[i].len)
                    {
                        int dis2 = dis + 1;
                        int len2 = len + 1;
                        int limit = Math.Min(match_len_limit + len2, triable_bytes);
                        while (len2 < limit && buf[pos + len2 - dis2] == buf[pos + len2])     
                            len2++;
                        
                        len2 -= len + 1;
                        if (len2 >= Constants.min_match_len)
                        {
                            int pos_state2 = (pos_state + len) & Constants.pos_state_mask;
                            State state2 = cur_state; 
                            state2.SetMatch();
                            price += PriceUtils.Price0(bm_match_row(state2.Get())[pos_state2]) +
                                     PriceMatched(buf[pos + len - 1], buf[pos + len], buf[pos + len - dis2]);
                            
                            pos_state2 = (pos_state2 + 1) & Constants.pos_state_mask;
                            state2.SetChar();
                            price += PriceUtils.Price1(bm_match_row(state2.Get())[pos_state2]) +
                                     PriceUtils.Price1(bm_rep[state2.Get()]) +
                                     PriceRep0Len(len2, state2, pos_state2);

                            while (num_trials < current + len + 1 + len2)
                                strials[++num_trials].price = Constants.infinite_price;

                            strials[current + len + 1 + len2].Update3(price, dis + Constants.num_rep_distances,
                                                                    current + len + 1, current);
                        }
                        if (++i >= num_pairs) break;
                        dis = spairs[i].dis;
                    }
                }
            }
        }
    }

    public LZEncoder(int dict_size, int len_limit, Stream ifd, Stream outfd)
        : base(Constants.before_size, dict_size, Constants.after_size, Constants.dict_factor,
              Constants.num_prev_positions23, Constants.pos_array_factor, ifd, outfd)
    {
        cycles = (len_limit < Constants.max_match_len) ? 16 + (len_limit / 2) : 256;
        match_len_limit = len_limit;
        match_len_prices = new LenPrices(match_len_model, match_len_limit);
        rep_len_prices = new LenPrices(rep_len_model, match_len_limit);
        pending_num_pairs = 0;
        num_dis_slots = 2 * Utils.RealBits((uint)(dictionary_size - 1));

        foreach (ref Trial t in trials.AsSpan()) t = new();

        trials[1].prev_index = 0;
        trials[1].prev_index2 = Constants.single_step_trial;
    }

    public new void Reset()
    {
        base.Reset();
        match_len_prices.Reset();
        rep_len_prices.Reset();
        pending_num_pairs = 0;
    }

    public override bool EncodeMember(ulong member_size)
    {
        ulong member_size_limit = member_size - (ulong)LzipTrailer.Size - Constants.max_marker_size;
        bool best = match_len_limit > 12;
        int dis_price_count = best ? 1 : 512;
        int align_price_count = best ? 1 : Constants.dis_align_size;
        int price_count = (match_len_limit > 36) ? 1013 : 4093;
        int price_counter = 0;
        int dis_price_counter = 0;
        int align_price_counter = 0;
        Span<int> reps = new int[Constants.num_rep_distances];
        Span<int> salign_prices = align_prices.AsSpan();         //helper
        State state = new();
        
        reps.Fill(0);

        if (DataPosition() != 0 || renc.MemberPosition() != LzipHeader.Size)
            return false;

        if (!DataFinished())
        {
            byte prev_byte = 0;
            byte cur_byte = Peek(0);
            renc.EncodeBit(ref bm_match_row(state.Get())[0], false);
            EncodeLiteral(prev_byte, cur_byte);
            CRC32.UpdateByte(ref crc, cur_byte);
            GetMatchPairs();
            MovePos();
        }

        while (!DataFinished())
        {
            if (price_counter <= 0 && pending_num_pairs == 0)
            {
                price_counter = price_count;
                if (dis_price_counter <= 0)
                {
                    dis_price_counter = dis_price_count;
                    UpdateDistancePrices();
                }
                if (align_price_counter <= 0)
                {
                    align_price_counter = align_price_count;
                    for (int i = 0; i < Constants.dis_align_size; i++)
                        salign_prices[i] = PriceUtils.PriceSymbolReversed(bm_align, i, Constants.dis_align_bits);
                }
                match_len_prices.UpdatePrices();
                rep_len_prices.UpdatePrices();
            }
            
            int ahead = SequenceOptimizer(reps, state);
            price_counter -= ahead;

            for (int i = 0; ahead > 0;)
            {
                int pos_state = (int)((DataPosition() - (ulong)ahead) & Constants.pos_state_mask);
                ref Trial trial = ref trials[i];
                int len = trial.price;
                int dis = trial.dis4;

                bool bit = dis < 0;
                renc.EncodeBit(ref bm_match_row(state.Get())[pos_state], !bit);
                if (bit)
                {
                    byte prev_byte = Peek(ahead + 1);
                    byte cur_byte = Peek(ahead);
                    CRC32.UpdateByte(ref crc, cur_byte);
                    if (state.IsCharSetChar())
                        EncodeLiteral(prev_byte, cur_byte);
                    else
                    {
                        byte match_byte = Peek(ahead + reps[0] + 1);
                        EncodeMatched(prev_byte, cur_byte, match_byte);
                    }
                }
                else
                {
                    CRC32.UpdateBuf(ref crc, BufOffsetSizeToCurrentPos(-ahead, len));
                    MtfReps(dis, reps);
                    bit = dis < Constants.num_rep_distances;
                    renc.EncodeBit(ref bm_rep[state.Get()], bit);
                    if (bit)
                    {
                        bit = dis == 0;
                        renc.EncodeBit(ref bm_rep0[state.Get()], !bit);
                        if (bit)
                            renc.EncodeBit(ref bm_len_row(state.Get())[pos_state], len > 1);
                        else
                        {
                            renc.EncodeBit(ref bm_rep1[state.Get()], dis > 1);
                            if (dis > 1)
                                renc.EncodeBit(ref bm_rep2[state.Get()], dis > 2);
                        }
                        if (len == 1) 
                            state.SetShortRep();
                        else
                        {
                            renc.EncodeLen(rep_len_model, len, pos_state);
                            rep_len_prices.DecrementCounter(pos_state);
                            state.SetRep();
                        }
                    }
                    else
                    {
                        dis -= Constants.num_rep_distances;
                        EncodePair((uint)dis, len, pos_state);
                        if (dis >= Constants.modeled_distances) 
                            --align_price_counter;
                        --dis_price_counter;
                        match_len_prices.DecrementCounter(pos_state);
                        state.SetMatch();
                    }
                }
                ahead -= len; 
                i += len;
                if (renc.MemberPosition() >= member_size_limit)
                {
                    if (!DecPos(ahead)) 
                        return false;
                    FullFlush(state);
                    return true;
                }
            }
        }
        FullFlush(state);
        return true;
    }
}














































public sealed class RangeDecoder
{
    private const int buffer_size = 16384;
    private ulong partial_member_pos;
    private readonly byte[] buffer;      // input buffer
    private int pos;            // current pos in buffer
    private int stream_pos;     // when reached, a new block must be read
    private uint code;
    private uint range;
    private readonly Stream infd;
    private bool at_stream_end;

    public RangeDecoder(Stream ifd)
    {
        partial_member_pos = 0;
        buffer = new byte[buffer_size];
        pos = 0;
        stream_pos = 0;
        code = 0;
        range = 0xFFFFFFFFU;
        infd = ifd;
        at_stream_end = false;
    }

    public bool ReadBlock()
    {
        if(!at_stream_end)
        {
            stream_pos = IOUtils.ReadBlock(infd, buffer, 0, buffer_size);
            at_stream_end = stream_pos < buffer_size;
            partial_member_pos += (ulong)pos;
            pos = 0;
            Program.ShowDProgress();
        }
        return pos < stream_pos;
    }

    public bool Finished() 
        => pos >= stream_pos && !ReadBlock(); 

    public ulong MemberPosition() 
        => partial_member_pos + (ulong)pos; 

    public void ResetMemberPosition()
    {
        partial_member_pos = 0;
        partial_member_pos -= (ulong)pos;
    }

    public byte GetByte()
    {
        // 0xFF avoids decoder error if member is truncated at EOS marker
        if (Finished()) return 0xFF;
        return buffer.AsSpan()[pos++];
    }

    public int ReadData(Span<byte> outbuf, int size)
    {
        int sz = 0;
        while (sz < size && !Finished())
        {
            int rd = Math.Min(size - sz, stream_pos - pos);
            buffer.AsSpan(pos, rd).CopyTo(outbuf.Slice(sz));
            pos += rd;
            sz += rd;
        }
        return sz;
    }

    public bool Load()
    {
        code = 0;
        range = 0xFFFFFFFFU;
        // check first byte of the LZMA stream
        if (GetByte() != 0) return false;
        for (int i = 0; i < 4; ++i) code = (code << 8) | GetByte();
        return true;
    }

    public void Normalize()
    {
        if (range <= 0x00FFFFFFU)
        {
            range <<= 8;
            code = (code << 8) | GetByte();
        }
    }

    public uint Decode(int num_bits)
    {
        uint symbol = 0;
        for (int i = num_bits; i > 0; --i)
        {
            Normalize();
            range >>= 1;
            bool bit = code >= range;
            symbol <<= 1;
            symbol += bit ? 1U : 0U;
            code -= range & (0U - (bit ? 1U : 0U));
        }
        return symbol;
    }

    public bool DecodeBit(ref BitModel bm)
    {
        Normalize();
        uint bound = (range >> Constants.bit_model_total_bits) * (uint)bm.probability;
        if (code < bound)
        {
            range = bound;
            bm.probability += 
                (Constants.bit_model_total - bm.probability) >> Constants.bit_model_move_bits;
            return false;
        }
        else
        {
            code -= bound;
            range -= bound;
            bm.probability -= bm.probability >> Constants.bit_model_move_bits;
            return true;
        }
    }

    public void DecodeSymbolBit(ref BitModel bm, ref uint symbol)
    {
        Normalize();
        symbol <<= 1;
        uint bound = (range >> Constants.bit_model_total_bits) * (uint)bm.probability;
        if (code < bound)
        {
            range = bound;
            bm.probability += 
                (Constants.bit_model_total - bm.probability) >> Constants.bit_model_move_bits;
        }
        else
        {
            code -= bound;
            range -= bound;
            bm.probability -= bm.probability >> Constants.bit_model_move_bits;
            symbol |= 1U;
        }
    }

    public void DecodeSymbolBitReversed(ref BitModel bm, ref uint model, ref uint symbol, int i)
    {
        Normalize();
        model <<= 1;
        uint bound = (range >> Constants.bit_model_total_bits) * (uint)bm.probability;
        if (code < bound)
        {
            range = bound;
            bm.probability += (Constants.bit_model_total - bm.probability) >> Constants.bit_model_move_bits;
        }
        else
        {
            code -= bound;
            range -= bound;
            bm.probability -= bm.probability >> Constants.bit_model_move_bits;
            model |= 1U;
            symbol |= 1U << i;
        }
    }

    public uint DecodeTree6(Span<BitModel> bm)
    {
        uint symbol = 1;
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        return symbol & 0x3FU;
    }

    public uint DecodeTree8(Span<BitModel> bm)
    {
        uint symbol = 1;
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        return symbol & 0xFFU;
    }

    public uint DecodeTreeReversed(Span<BitModel> bm, int num_bits)
    {
        uint model = 1;
        uint symbol = 0;
        for (int i = 0; i < num_bits; ++i)
            DecodeSymbolBitReversed(ref bm[(int)model], ref model, ref symbol, i);
        return symbol;
    }

    public uint DecodeTreeReversed4(Span<BitModel> bm)
    {
        uint model = 1;
        uint symbol = 0;
        DecodeSymbolBitReversed(ref bm[(int)model], ref model, ref symbol, 0);
        DecodeSymbolBitReversed(ref bm[(int)model], ref model, ref symbol, 1);
        DecodeSymbolBitReversed(ref bm[(int)model], ref model, ref symbol, 2);
        DecodeSymbolBitReversed(ref bm[(int)model], ref model, ref symbol, 3);
        return symbol;
    }

    public uint DecodeMatched(Span<BitModel> bm, uint match_byte)
    {
        Span<BitModel> bm1 = bm.Slice(0x100);
        uint symbol = 1;
        while (symbol < 0x100U)
        {
            uint match_bit = (match_byte <<= 1) & 0x100U;
            bool bit = DecodeBit(ref bm1[(int)(symbol + match_bit)]);
            symbol <<= 1;
            symbol |= bit ? 1U : 0U;
            if ((match_bit >> 8 != 0) != bit)
            {
                while (symbol < 0x100U)
                    DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
                break;
            }
        }
        return symbol & 0xFFU;
    }

    public uint DecodeLen(LenModel lm, int pos_state)
    {
        Span<BitModel> bm;
        uint mask, offset;
        uint symbol = 1;

        if (!DecodeBit(ref lm.choice1))
        {
            bm = lm.bm_low_row(pos_state);
            mask = 7;
            offset = 0;
            goto len3;
        }
        if (!DecodeBit(ref lm.choice2))
        {
            bm = lm.bm_mid_row(pos_state);
            mask = 7;
            offset = Constants.len_low_symbols;
            goto len3;
        }
        bm = lm.bm_high.AsSpan();
        mask = 0xFFU;
        offset = Constants.len_low_symbols + Constants.len_mid_symbols;
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
    len3:
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        DecodeSymbolBit(ref bm[(int)symbol], ref symbol);
        return (symbol & mask) + Constants.min_match_len + offset;
    }
}

public sealed class LzDecoder
{
    private ulong partial_data_pos;
    public readonly RangeDecoder rdec;
    private readonly uint dictionary_size;
    private readonly byte[] buffer;      // output buffer
    private uint pos;           // current pos in buffer
    private uint stream_pos;    // first byte not yet written to file
    private uint crc;
    private readonly Stream? outfd;
    private bool pos_wrapped;
    private ulong dec_size;

    private void FlushData()
    {
        if (pos > stream_pos)
        {
            uint size = pos - stream_pos;
            dec_size += size;
            CRC32.UpdateBuf(ref crc, buffer.AsSpan((int)stream_pos,(int)size));
            if (outfd != null)
            {
                IOUtils.WriteBlock(outfd, buffer, (int)stream_pos, (int)size);
            }
            if (pos >= dictionary_size)
            {
                partial_data_pos += pos;
                pos = 0;
                pos_wrapped = true;
            }
            stream_pos = pos;
        }
    }

    private byte PeekPrev()
        => buffer[((pos > 0) ? pos : dictionary_size) - 1];

    private byte Peek(uint distance)
    {
        uint i = ((pos > distance) ? 0 : dictionary_size) + pos - distance - 1;
        return buffer[i];
    }

    private void PutByte(byte b)
    {
        buffer[pos] = b;
        if (++pos >= dictionary_size) FlushData();
    }

    private void CopyBlock(uint distance, uint len)
    {
        uint lpos = pos, i = lpos - distance - 1;
        bool fast, fast2;

        if (lpos > distance)
        {
            fast = len < dictionary_size - lpos;
            fast2 = fast && len <= lpos - i;
        }
        else
        {
            i += dictionary_size;
            fast = len < dictionary_size - i;     // (i == pos) may happen
            fast2 = fast && len <= i - lpos;
        }
        if (fast)                   // no wrap
        {
            pos += len;
            if (fast2)              // no wrap, no overlap
                buffer.AsSpan((int)i, (int)len).CopyTo(buffer.AsSpan((int)lpos));
            else
                for (; len > 0; --len)
                    buffer[lpos++] = buffer[i++]; 
        }
        else
        {
            for (; len > 0; --len)
            {
                buffer[pos] = buffer[i];
                if (++pos >= dictionary_size) FlushData();
                if (++i >= dictionary_size) i = 0;
            }
        }
    }

    private bool CheckTrailer()
    {
        LzipTrailer trailer = new LzipTrailer();
        int size = rdec.ReadData(trailer.data, LzipTrailer.Size);
        bool error = false;

        if (size < LzipTrailer.Size)
        {
            error = true;
            Console.Error.WriteLine($"Trailer truncated at trailer position {size}; some checks may fail.");
            while (size < LzipTrailer.Size) trailer.data[size++] = 0;
        }

        uint td_crc = trailer.DataCrc();
        if (td_crc != Crc())
        {
            error = true;
            Console.Error.WriteLine($"CRC mismatch; stored {td_crc:X8}, computed {Crc():X8}");
        }

        ulong data_size = DataPosition();
        ulong td_size = trailer.DataSize();
        if (td_size != data_size)
        {
            error = true;
            Console.Error.WriteLine($"Data size mismatch; stored {td_size} (0x{td_size:X}), computed {data_size} (0x{data_size:X})");
        }

        ulong member_size = rdec.MemberPosition();
        ulong tm_size = trailer.MemberSize();
        if (tm_size != member_size)
        {
            error = true;
            Console.Error.WriteLine($"Member size mismatch; stored {tm_size} (0x{tm_size:X}), computed {member_size} (0x{member_size:X})");
        }

        if (error) return false;

        Console.Error.Write($"dict {Program.FormatNum3(dictionary_size)}, ");
        if (data_size == 0 || member_size == 0)
            Console.Error.Write("no data compressed. ");
        else
            Console.Error.Write("{0,6:F2}:1, {1,5:F2}% ratio, {2,5:F2}% saved, ",
                            (double)data_size / member_size,
                            (100.0 * member_size) / data_size,
                            100.0 - ((100.0 * member_size) / data_size));
        Console.Error.Write($"CRC {td_crc:X8}, ");
        Console.Error.Write($"{Program.FormatNum3(member_size, true)} in, {Program.FormatNum3(data_size, true)} out. ");
             
        return true;
    }

    public LzDecoder(RangeDecoder rde, uint dict_size, Stream? ofd)
    {
        partial_data_pos = 0;
        rdec = rde;
        dictionary_size = dict_size;
        buffer = new byte[dictionary_size];
        pos = 0;
        stream_pos = 0;
        crc = 0xFFFFFFFFU;
        outfd = ofd;
        pos_wrapped = false;
        // prev_byte of first byte; also for peek(0) on corrupt file
        buffer[dictionary_size - 1] = 0;
        dec_size = 0;
    }

    public uint Crc() 
        => crc ^ 0xFFFFFFFFU; 

    public ulong DataPosition() 
        => partial_data_pos + pos; 

    public ulong DecSize() 
        => dec_size; 


    public int DecodeMember()
    {
        const int bm_dis_slot_y_sz = 1 << Constants.dis_slot_bits;

        BitModel[] _bm_literal = new BitModel[(1 << Constants.literal_context_bits) * 0x300];           //was 2D array
        BitModel[] _bm_match = new BitModel[State.states * Constants.pos_states];                       //was 2D array
        Span<BitModel> bm_rep = new BitModel[State.states];
        Span<BitModel> bm_rep0 = new BitModel[State.states];
        Span<BitModel> bm_rep1 = new BitModel[State.states];
        Span<BitModel> bm_rep2 = new BitModel[State.states];
        BitModel[] _bm_len = new BitModel[State.states * Constants.pos_states];                         //was 2D array
        BitModel[] _bm_dis_slot = new BitModel[Constants.len_states * bm_dis_slot_y_sz];                //was 2D array
        Span<BitModel> bm_dis = new BitModel[Constants.modeled_distances - Constants.end_dis_model + 1];
        Span<BitModel> bm_align = new BitModel[Constants.dis_align_size];

        LenModel match_len_model = new();
        LenModel rep_len_model = new();

        uint rep0 = 0;      // rep[0-3] latest four distances
        uint rep1 = 0;      // used for efficient coding of
        uint rep2 = 0;      // repeated distances
        uint rep3 = 0;
        State state = new();

        foreach (ref BitModel b in _bm_literal.AsSpan())  b = new();
        foreach (ref BitModel b in _bm_match.AsSpan())    b = new();
        foreach (ref BitModel b in bm_rep)                b = new();
        foreach (ref BitModel b in bm_rep0)               b = new();
        foreach (ref BitModel b in bm_rep1)               b = new();
        foreach (ref BitModel b in bm_rep2)               b = new();
        foreach (ref BitModel b in _bm_len.AsSpan())      b = new();
        foreach (ref BitModel b in _bm_dis_slot.AsSpan()) b = new();
        foreach (ref BitModel b in bm_dis)                b = new();
        foreach (ref BitModel b in bm_align)              b = new();

        Span<BitModel> bm_literal_row(int row)
            => _bm_literal.AsSpan(row * 0x300, 0x300);

        Span<BitModel> bm_match_row(int row)
            => _bm_match.AsSpan(row * Constants.pos_states, Constants.pos_states);

        Span<BitModel> bm_len_row(int row)
            => _bm_len.AsSpan(row * Constants.pos_states, Constants.pos_states);

        Span<BitModel> bm_dis_slot_row(int row)
            => _bm_dis_slot.AsSpan(row * bm_dis_slot_y_sz, bm_dis_slot_y_sz);


        if (!rdec.Load()) return 5;
        while (!rdec.Finished())
        {
            int pos_state = (int)DataPosition() & Constants.pos_state_mask;
            if (!rdec.DecodeBit(ref bm_match_row(state.Get())[pos_state]))
            {
                // literal byte
                Span<BitModel> bm = bm_literal_row(Utils.GetLitState(PeekPrev()));
                if (state.IsCharSetChar())
                    PutByte((byte)rdec.DecodeTree8(bm));
                else
                    PutByte((byte)rdec.DecodeMatched(bm, Peek(rep0)));
                continue;
            }
            // match or repeated match
            int len;
            if (rdec.DecodeBit(ref bm_rep[state.Get()]))
            {
                if (!rdec.DecodeBit(ref bm_rep0[state.Get()]))
                {
                    if (!rdec.DecodeBit(ref bm_len_row(state.Get())[pos_state]))
                    {
                        state.SetShortRep();
                        PutByte(Peek(rep0));
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (!rdec.DecodeBit(ref bm_rep1[state.Get()]))
                        distance = rep1;
                    else
                    {
                        if (!rdec.DecodeBit(ref bm_rep2[state.Get()]))
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
                state.SetRep();
                len = (int)rdec.DecodeLen(rep_len_model, pos_state);
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = (int)rdec.DecodeLen(match_len_model, pos_state);
                rep0 = rdec.DecodeTree6(bm_dis_slot_row(Utils.GetLenState(len)));
                if (rep0 >= Constants.start_dis_model)
                {
                    uint dis_slot = rep0;
                    int direct_bits = (int)((dis_slot >> 1) - 1);
                    rep0 = (2U | (dis_slot & 1U)) << direct_bits;
                    if (dis_slot < Constants.end_dis_model)
                        rep0 += rdec.DecodeTreeReversed(bm_dis.Slice((int)(rep0 - dis_slot)), direct_bits);
                    else
                    {
                        rep0 += rdec.Decode(direct_bits - Constants.dis_align_bits) << Constants.dis_align_bits;
                        rep0 += rdec.DecodeTreeReversed4(bm_align);
                        if (rep0 == 0xFFFFFFFFU)
                        {
                            rdec.Normalize();
                            FlushData();
                            if (len == Constants.min_match_len)          // End Of Stream marker
                                { if(CheckTrailer()) return 0; else return 3; }
                            Console.Error.WriteLine($"Unsupported marker code '{len}'"); 
                            return 4;
                        }
                    }
                }
                state.SetMatch();
                if (rep0 >= dictionary_size || (rep0 >= pos && !pos_wrapped))
                {
                    FlushData();
                    return 1;
                }
            }
            CopyBlock(rep0, (uint)len);
        }            
        FlushData(); 
        return 2;    
    }
}














































public sealed class ArgParser
{
    public enum HasArg { No, Yes } 

    public struct Option
    {
        public int code;
        public string? long_name = null;
        public HasArg has_arg;

        public Option() {}
    }

    private struct Record
    {
        public int code;
        public string parsed_name;
        public string argument;

        public Record(char c)
        {
            code = c;
            parsed_name = "-" + c;
            argument = string.Empty;
        }

        public Record(int c, string? long_name)
        {
            code = c;
            parsed_name = "--" + (long_name ?? string.Empty);
            argument = string.Empty;
        }

        public Record(string arg)
        {
            code = 0;
            parsed_name = string.Empty;
            argument = arg;
        }
    }

    private readonly List<Record> data = new();


    private bool ParseLongOption(string opt, string? arg, Option[] options, ref int argind)
    {
        int index = -1;

        bool opt_has_arg = opt.Contains("=", StringComparison.Ordinal);
        var fullopt = opt_has_arg ? Regex.Match(opt, @"^--(\w+[-]?\w*)=(\S+)\s*$", RegexOptions.IgnoreCase) :
                                    Regex.Match(opt, @"^--(\w+[-]?\w*)\s*$", RegexOptions.IgnoreCase);

        if(!fullopt.Success)
            throw new Exception($"Wrong long option: '{opt}'!");

        for (int i = 0; options[i].code != 0; ++i)
        {
            if (options[i].long_name?.Equals(fullopt.Groups[1].Value, 
                                    StringComparison.OrdinalIgnoreCase) ?? false)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new Exception($"Unrecognized option: '{opt}'!");

        Record rec = new Record(options[index].code, options[index].long_name);

        if (options[index].has_arg == HasArg.Yes)
        {
            if(!opt_has_arg)
                throw new Exception($"Option '--{options[index].long_name}' requires an argument!");

            rec.argument = fullopt.Groups[2].Value; 
        }

        argind++;
        data.Add(rec);

        return true;
    }

    private bool ParseShortOption(string opt, string? arg, Option[] options, ref int argind)
    {
        if (opt.Length > 2)
            throw new Exception("Short options stacking is not supported!");

        int index = -1;
        char c = opt[1];

        for (int i = 0; options[i].code != 0; ++i)
        {
            if (c == options[i].code)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new Exception($"Invalid option: '{c}'!");

        Record rec = new Record(c);
        
        if (options[index].has_arg == HasArg.Yes)
        {
            if (string.IsNullOrEmpty(arg))
                throw new Exception($"Option requires an argument: '{c}'!");

            argind++;
            rec.argument = arg;
        }
    
        argind++;
        data.Add(rec);

        return true;
    }

    public ArgParser(string[] args, Option[] options)
    {
        List<string> non_options = new();
        int argind = 0;

        try
        {
            while (argind < args.Length)
            {
                var shortopt = Regex.Match(args[argind], @"^-\w+");
                var longopt = Regex.Match(args[argind], @"^--\w+");
                var endopt = Regex.Match(args[argind], @"^\s*--\s*$");
                string? arg = (argind + 1 < args.Length) ? args[argind+1] : null;

                if(shortopt.Success)
                    ParseShortOption(args[argind], arg, options, ref argind); 
                else if(longopt.Success)
                    ParseLongOption(args[argind], arg, options, ref argind); 
                else if(endopt.Success) 
                {   
                    argind++; 
                    break; 
                }
                else if(argind < args.Length)
                    non_options.Add(args[argind++]);
            }
        }
        catch(Exception)
        {
            data.Clear();
            throw;
        }

        foreach (string non_opt in non_options)
            data.Add(new Record(non_opt));
        while (argind < args.Length)
            data.Add(new Record(args[argind++]));
    }

    public int Arguments() => data.Count;

    public int Code(int i)
    {
        if (i >= 0 && i < data.Count) 
            return data[i].code;
        return 0;
    }

    public string ParsedName(int i)
    {
        if (i >= 0 && i < data.Count) 
            return data[i].parsed_name;
        return string.Empty;
    }

    public string Argument(int i)
    {
        if (i >= 0 && i < data.Count) 
            return data[i].argument;
        return string.Empty;
    }
}

public sealed class Block
{
    private long pos_, size_;

    public Block(long p, long s)
    {
        pos_ = p;
        size_ = s;
    }

    public long Pos() => pos_;
    public long Size() => size_;
    public long End() => pos_ + size_;

    public void SetPos(long p) { pos_ = p; }
    public void SetSize(long s) { size_ = s; }
}

public sealed class LzipIndex
{
    private struct Member
    {
        public Block dblock, mblock;
        public uint dictionary_size;

        public Member(long dpos, long dsize, long mpos, long msize, uint dict_size)
        {
            dblock = new Block(dpos, dsize);
            mblock = new Block(mpos, msize);
            dictionary_size = dict_size;
        }
    }

    private readonly List<Member> member_vector = new();
    private string error_ = string.Empty;
    private readonly long insize;
    private uint dictionary_size_;

    private void CheckHeader(LzipHeader header)
    {
        if (!header.CheckMagic())
            throw new Exception(IOUtils.bad_magic_msg);

        if (!header.CheckVersion())
            throw new Exception($"Version {header.Version()} member format not supported.");

        if (!Utils.IsValidDs(header.DictionarySize()))
            throw new Exception(IOUtils.bad_dict_msg);
    }

    private int SeekRead(Stream fd, byte[] buf, int size, long pos)
    {   
        try
        {
            if (fd.CanSeek && pos < fd.Length)
            {
                fd.Seek(pos, SeekOrigin.Begin);
                return IOUtils.ReadBlock(fd, buf, 0, size);
            }
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Error in SeekRead(): {e.Message}");
            fd?.Dispose();
            throw;
        }

        return 0;
    }

    private void ReadHeader(Stream fd, LzipHeader header, long pos)
    {   try
        {
            if (SeekRead(fd, header.data, LzipHeader.Size, pos) != LzipHeader.Size)
                throw new IOException();
        }
        catch(IOException)
        {
            throw new IOException("Error reading member header");
        }
    }

    private bool SkipTrailingData(Stream fd, ref ulong pos, in ClOptions cl_opts)
    {
        if (pos < Constants.min_member_size) 
            return false;
        
        const int block_size = 16384;
        const int buffer_size = block_size + LzipTrailer.Size - 1 + LzipHeader.Size;
        byte[] buffer = new byte[buffer_size];
        int bsize = (int)(pos % block_size);
        if (bsize <= buffer_size - block_size) 
            bsize += block_size;
        
        int search_size = bsize;
        int rd_size = bsize;
        long ipos = (long)(pos - (ulong)rd_size);

        while (true)
        {
            if (SeekRead(fd, buffer, rd_size, ipos) != rd_size)
                throw new IOException("Error seeking member trailer");

            byte max_msb = (byte)((ipos + search_size) >> 56);
            for (int i = search_size; i >= LzipTrailer.Size; --i)
            {
                if (buffer[i - 1] <= max_msb)
                {
                    LzipTrailer trailer = LzipTrailer.FromBuffer(buffer, i - LzipTrailer.Size);
                    ulong member_size = trailer.MemberSize();
                    if (member_size == 0)
                    {
                        while (i > LzipTrailer.Size && buffer[i - 9] == 0) 
                            --i;
                        continue;
                    }
                    if (member_size > (ulong)ipos + (ulong)i || !trailer.CheckConsistency()) 
                        continue;
                    
                    LzipHeader header = new();
                    ReadHeader(fd, header, ipos + i - (long)member_size); 
                    if (!header.Check()) 
                        continue;
                    
                    LzipHeader header2 = LzipHeader.FromBuffer(buffer, 1);
                    bool full_h2 = bsize - i >= LzipHeader.Size;
                    
                    if (header2.CheckPrefix(bsize - i))
                    {
                        try
                        {
                            if (!full_h2) throw new Exception(); 
                            CheckHeader(header2);
                        }
                        catch(Exception)
                        {
                            throw new Exception("Last member in input file is truncated or corrupt.");
                        }
                    }
                    
                    if (!cl_opts.loose_trailing && full_h2 && header2.CheckCorrupt())
                        throw new Exception($"{IOUtils.corrupt_mm_msg}");
                    
                    if (!cl_opts.ignore_trailing)
                        throw new Exception($"{IOUtils.trailing_msg}");
                    
                    pos = (ulong)(ipos + i - (long)member_size);
                    uint dictionary_size = header.DictionarySize();
                    if (dictionary_size_ < dictionary_size)
                        dictionary_size_ = dictionary_size;
                    
                    member_vector.Add(new Member(0, (long)trailer.DataSize(), 
                                               (long)pos, (long)member_size, 
                                               dictionary_size));
                    return true;
                }
            }
            
            if (ipos == 0)
                throw new Exception($"Bad trailer at pos {pos - (ulong)LzipTrailer.Size}");
            
            bsize = buffer_size;
            search_size = bsize - LzipHeader.Size;
            rd_size = block_size;
            ipos -= rd_size;
            Array.Copy(buffer, rd_size, buffer, 0, buffer_size - rd_size);
        }
    }

    public LzipIndex(Stream infd, in ClOptions cl_opts)
    {
        insize = infd.CanSeek ? infd.Length: -1;         
        dictionary_size_ = 0;

        if (insize < 0)
            throw new Exception("Input file is not seekable");

        LzipHeader header = new();
        if (insize >= LzipHeader.Size)
        { 
            ReadHeader(infd, header, 0);
            CheckHeader(header);
        }

        if (insize < Constants.min_member_size)
            throw new Exception("Input file is truncated.");

        if (insize > long.MaxValue)
            throw new Exception("Input file is too long (2^63 bytes or more).");

        ulong pos = (ulong)insize;
        try
        {
            while (pos >= Constants.min_member_size)
            {
                LzipTrailer trailer = new();
                if (SeekRead(infd, trailer.data, LzipTrailer.Size, (long)pos - LzipTrailer.Size) != LzipTrailer.Size)
                    throw new IOException("Error reading member trailer");

                ulong member_size = trailer.MemberSize();
                if (member_size > pos || !trailer.CheckConsistency())
                {
                    if (member_vector.Count == 0)
                    {
                        if (SkipTrailingData(infd, ref pos, cl_opts))
                            continue;
                        return;
                    }
                    throw new Exception($"Bad trailer at pos {pos - (ulong)LzipTrailer.Size}");
                }

                ReadHeader(infd, header, (long)(pos - member_size));

                if (!header.Check())
                {
                    if (member_vector.Count == 0)
                    {
                        if (SkipTrailingData(infd, ref pos, cl_opts))
                            continue;
                        return;
                    }
                    throw new Exception($"Bad header at pos {pos - member_size}");
                }

                pos -= member_size;
                uint dictionary_size = header.DictionarySize();
                if (dictionary_size_ < dictionary_size)
                    dictionary_size_ = dictionary_size;

                member_vector.Add(new Member(0, (long)trailer.DataSize(), 
                                           (long)pos, (long)member_size, 
                                           dictionary_size));
            }

            if (pos != 0 || member_vector.Count == 0)
                throw new Exception($"Can't create file index.");
        }
        catch(Exception)
        {
            member_vector.Clear();
            throw;
        }

        member_vector.Reverse();
        for (uint i = 0; ; ++i)
        {
            long end = member_vector[(int)i].dblock.End();
            if (end < 0 || end > long.MaxValue)
            {
                member_vector.Clear();
                throw new Exception($"Data in input file is too long (2^63 bytes or more).");
            }
            if (i + 1 >= member_vector.Count) 
                break;

            Member member = member_vector[(int)(i + 1)];
            member.dblock.SetPos(end);
            member_vector[(int)(i + 1)] = member;
        }
    }

    public long Members() => member_vector.Count;
    public uint DictionarySize() => dictionary_size_;

    public bool MultiEmpty()
    {
        if (member_vector.Count > 1)
            foreach (Member m in member_vector)
                if (m.dblock.Size() == 0)
                    return true;
        return false;
    }

    public long UdataSize() => member_vector.Count == 0 ? 0 : member_vector[^1].dblock.End();
    public long CdataSize() => member_vector.Count == 0 ? 0 : member_vector[^1].mblock.End();
    public long FileSize() => insize >= 0 ? insize : 0;

    public Block Dblock(long i) => member_vector[(int)i].dblock;
    public Block Mblock(long i) => member_vector[(int)i].mblock;  
    public uint DictionarySize(long i) => member_vector[(int)i].dictionary_size;
}































































public static partial class Program
{
    private const string program_name = "lzipcs";
    private const string program_year = "2025";
    private static string invocation_name = program_name;

    private readonly struct Extensions
    { 
        public readonly string from; 
        public readonly string to;
    
        public Extensions(string From, string To) 
        {
            from = From;
            to = To;
        }
    } 
    private static readonly Extensions[] known_extensions = 
    {
        new Extensions(".lz", ""), 
        new Extensions(".tlz", ".tar"),
        new Extensions("", "")
    };

    private struct LzmaOptions
    {
        public int dictionary_size;
        public int match_len_limit;
    }

    private enum Mode { Compress, Decompress, List, Test }

    private static string output_filename = string.Empty;
    private static Stream? outfd = null;
    private static bool delete_output_on_interrupt = false;

    private static void ShowHelp()
    {
        Console.WriteLine(@$"{program_name} is a LZMA compressor based on lzip, translated to NET C#.
Usage: {invocation_name} [options] [files]
Options:
  -h, --help                     display this help and exit
  -V, --version                  output version information and exit
  -a, --trailing-error           exit with error status if trailing data
  -b, --member-size=<bytes>      set member size limit of multimember files
  -c, --stdout                   write to standard output, keep input files
  -d, --decompress               decompress, test compressed file integrity
  -f, --force                    overwrite existing output files
  -l, --list                     print (un)compressed file sizes
  -m, --match-length=<bytes>     set match length limit in bytes [36]
  -o, --output=<file>            write to <file>, keep input files
  -s, --dictionary-size=<bytes>  set dictionary size limit in bytes [8 MiB]
  -S, --volume-size=<bytes>      set volume size limit in bytes
  -t, --test                     test compressed file integrity
  -1 .. -9                       set compression level [default 6]
      --best                     alias for -9
      --loose-trailing           allow trailing data seeming corrupt header

NOTE: Short options need space between argument and cannot be stacked together!");
    }

    private static void ShowVersion()
    {
        Console.WriteLine(@$"{program_name} is a translation of lzip to C# NET.
Mainly as an educational benchmark experiment of NET. Original author is
neither affiliated nor responsible for this project in any way. All the
credit for original lzip goes to Antonio Diaz Diaz. Do NOT contact original
author of lzip if you have issues with {program_name}!
------------------------------------------------------------------------------
Note that translation from lzip is not 1:1 in everything. Several differences
already exist, such as with arguments, missing fast encoder(-0 | fast),
quiet/verbose mode, altered realtime output during (de)compression, not
deleting original file automatically after operation and more. Future changes
(if any) may widen gap even more, to the point of incompatibility. This is not
meant to be regularly updated 1:1 translation, but a one time fork with own
purpose.


Original code: lzip v1.25,
Copyright (C) {program_year} Antonio Diaz Diaz. 
Original lzip home page: http://www.nongnu.org/lzip/lzip.html

License GPLv2+: GNU GPL version 2 or later <http://gnu.org/licenses/gpl.html>
This is free software: you are free to change and redistribute it.
There is NO WARRANTY, to the extent permitted by law.

The ideas embodied in lzip/{program_name} are due to (at least) the following
people:
Abraham Lempel and Jacob Ziv (for the LZ algorithm), Andrei Markov (for the
definition of Markov chains), G.N.N. Martin (for the definition of range
encoding), Igor Pavlov (for putting all the above together in LZMA), and
Julian Seward (for bzip2's CLI).");
    }

    public static string FormatNum3(ulong num, bool force = false)
    {
        if (num < 1024) return num.ToString();

        string[] suffixes = { "KB", "MB", "GB", "TB", "PB", "EB" };
    
        for (int i = suffixes.Length; i > 0; i--)
        {
            ulong divisor = 1UL << (10 * i);

            if (num >= divisor && (force || (num % divisor) == 0))
                return $"{num / divisor}{suffixes[i-1]}";
        }
        return num.ToString();
    }

    private static ulong GetNum(string arg, string option_name, ulong llimit, ulong ulimit)
    {
        var match = Regex.Match(arg, @"^(\d+)([a-zA-Z]?)");

        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out ulong result))
        {
            Console.Error.WriteLine($"Arg: '{arg}', bad or missing numerical argument in option: '{option_name}'!");
            Environment.Exit(1);
            return 0;                 //to satisfy compiler
        }

        if (match.Groups.Count > 2 && !string.IsNullOrEmpty(match.Groups[2].Value))
        {
            char suffix = match.Groups[2].Value[0];
            int exponent = suffix switch
            {
                'E' or 'e' => 6,
                'P' or 'p' => 5,
                'T' or 't' => 4,
                'G' or 'g' => 3,
                'M' or 'm' => 2,
                'K' or 'k' => 1,
                _ => 0
            };

            if( exponent <= 0 )
            { 
                Console.Error.WriteLine($"Arg: '{arg}', bad multiplier in numerical argument of: '{option_name}'!");
                Environment.Exit(1);
            }
            for (int i = 0; i < exponent; ++i)
            {
                if (ulimit / 1024 >= result)
                    result *= 1024;
                else
                {
                    Console.Error.WriteLine($"{program_name}: '{arg}': Value out of limits [{FormatNum3(llimit)},{FormatNum3(ulimit)}] in option '{option_name}'.");
                    Environment.Exit(1);
                }
            }
        }

        if (result < llimit || result > ulimit)
        {
            Console.Error.WriteLine($"{program_name}: '{arg}': Value out of limits [{FormatNum3(llimit)},{FormatNum3(ulimit)}] in option '{option_name}'.");
            Environment.Exit(1);
        }

        return result;
    }

    private static void SetMode(ref Mode program_mode, in Mode new_mode)
    {
        if (program_mode != Mode.Compress && program_mode != new_mode)
        {
            Console.Error.WriteLine($"Only one operation can be specified.");
            Environment.Exit(1);
        }
        program_mode = new_mode;
    }

    private static int ExtensionIndex(string name)
    {
        for (int eindex = 0; known_extensions[eindex].from != ""; ++eindex)
        {
            string ext = known_extensions[eindex].from;
            if (name.EndsWith(ext, StringComparison.Ordinal))
                return eindex;
        }
        return -1;
    }

    private static void SetCOutname(string name, bool filenames_given, bool force_ext, bool multifile)
    {
        output_filename = name;
        if (multifile) output_filename += "00001";
        if (force_ext || multifile || (!filenames_given && ExtensionIndex(output_filename) < 0))
            output_filename += known_extensions[0].from;
    }

    private static void SetDOutname(string name, int eindex)
    {
        if (eindex >= 0)
        {
            string from = known_extensions[eindex].from;
            if (name.EndsWith(from, StringComparison.Ordinal))
            {
                output_filename = name[..^from.Length] + known_extensions[eindex].to;
                return;
            }
        }
        output_filename = name + ".out";
        Console.Error.WriteLine($"Can't guess original filename from: {name}, using: '{output_filename}'");
    }

    private static Stream? OpenInstream(string name, out FileInfo? in_stats)
    {
        Stream? infd = null;
        in_stats = null;

        try
        {
            infd = File.OpenRead(name);
            in_stats = new FileInfo(name);
            
            if (!infd.CanSeek || Console.IsInputRedirected)
                throw new IOException();
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Cannot open input file '{name}', or is not a regular file: {e.Message}");
            infd?.Dispose();
            throw;
        }

        return infd;
    }

    private static void OpenOutstream(bool force)
    {
        FileMode mode = force ? FileMode.Create : FileMode.CreateNew;
        FileAccess access = FileAccess.Write;
        FileShare share = FileShare.None;

        try
        {
            if (output_filename.EndsWith('/') || output_filename.EndsWith('\\'))
                throw new IOException("Output filename is a directory", 0x15); // EISDIR
            
            string? dirPath = Path.GetDirectoryName(output_filename);
            if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            outfd = File.Open(output_filename, mode, access, share);
            delete_output_on_interrupt = true;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Can't create output file '{output_filename}': {e.Message}");
            outfd?.Dispose();
            throw;
        }
    }

    private static void SetSignals()
    {
        Console.CancelKeyPress += (_, e) => 
        {
            e.Cancel = true;
            Console.Error.WriteLine("Control-C or similar caught, quitting.");
            CleanupAndFail();
        };
       
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => 
        {
            Console.Error.WriteLine("Session termination detected, quitting.");
            CleanupAndFail();
        });

        PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => 
        {
            Console.Error.WriteLine("Session disconnected, quitting.");
            CleanupAndFail();
        });
    }

    private static void CleanupAndFail()
    {
        outfd?.Dispose();

        if (delete_output_on_interrupt)
        {
            delete_output_on_interrupt = false;
            Console.Error.WriteLine($"Deleting output file if exist: {output_filename}");
            try
            {
                File.Delete(output_filename);
            }
            catch (IOException e)
            { 
                Console.Error.WriteLine($"Cannot delete file: '{output_filename}': {e.Message}");
            }
        }
        Environment.Exit(1);
    }

    private static bool CheckTtyIn(string inputFilename, Stream? inputStream, Mode programMode)
    {
        if ((programMode == Mode.Decompress || programMode == Mode.Test) &&
            !Console.IsInputRedirected && (!inputStream?.CanSeek ?? true)) 
        {
            Console.Error.WriteLine($"I won't read compressed data from terminal: '{inputFilename}'");
            inputStream?.Dispose();
            if (programMode != Mode.Test) 
                CleanupAndFail();
            return false;
        }
        return true;
    }

    private static bool CheckTtyOut(Mode programMode)
    {
        if (programMode == Mode.Compress && (!outfd?.CanSeek ?? true) && !Console.IsOutputRedirected) 
        {
            Console.Error.WriteLine($"I won't write compressed data to terminal: '{output_filename}'");
            return false;
        }
        return true;
    }

    private static bool NextFilename()
    {
        string ext = known_extensions[0].from;
        int ext_len = ext.Length;

        if (output_filename.EndsWith(ext, StringComparison.Ordinal) && 
            output_filename.Length >= ext_len + 5)        //"*00001.lz"
        {
            try
            {
                string basestr = output_filename[..^(ext_len+5)];
                string numstr1 = output_filename[^(ext_len+5)..^ext_len];
                string numstr2 = $"{int.Parse(numstr1) + 1:D5}";

                output_filename = basestr + numstr2 + ext; 
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error in NextFilename(): {e.Message}");
            }
        }
        return false;
    }

    private static void Compress(ulong cfile_size, ulong member_size, ulong volume_size, Stream? infd,
                                in LzmaOptions encoder_options)
    {
        ArgumentNullException.ThrowIfNull(infd);
        ArgumentNullException.ThrowIfNull(outfd);

        LZEncoderBase encoder;
        LzipHeader header = new();
        Stopwatch timer = Stopwatch.StartNew();

        if (header.SetDictionarySize((uint)encoder_options.dictionary_size) &&
            encoder_options.match_len_limit >= Constants.min_match_len_limit &&
            encoder_options.match_len_limit <= Constants.max_match_len)
        {
            encoder = new LZEncoder((int)header.DictionarySize(),
                                  encoder_options.match_len_limit, infd, outfd);
        }
        else
            throw new Exception("invalid argument to encoder.");

        ulong in_size = 0, out_size = 0, partial_volume_size = 0;
        while (true)
        {
            ulong size = (volume_size > 0) ?
                Math.Min(member_size, volume_size - partial_volume_size) : member_size;
            ShowCProgress(cfile_size, in_size, out_size, encoder, true);
            if (!encoder.EncodeMember(size))
                throw new Exception("Encoder error.");
            
            in_size += encoder.DataPosition();
            out_size += encoder.MemberPosition();
            if (encoder.DataFinished()) 
                break;
            
            if (volume_size > 0)
            {
                partial_volume_size += encoder.MemberPosition();
                if (partial_volume_size >= volume_size - Constants.min_dictionary_size)
                {
                    partial_volume_size = 0;
                    if (delete_output_on_interrupt)
                    {
                        outfd?.Dispose();
                        delete_output_on_interrupt = false;

                        if (!NextFilename())
                            throw new Exception("Too many volume files.");
                        OpenOutstream(true);
                    }
                }
            }
            encoder.Reset();
        }

        if (in_size == 0 || out_size == 0)
            Console.Error.WriteLine(" no data compressed.");
        else
            Console.Error.WriteLine($"{in_size / (double)out_size,6:F3}:1, {(100.0 * out_size) / in_size,5:F2}% ratio, {100.0 - ((100.0 * out_size) / in_size),5:F2}% saved, {FormatNum3(in_size, true)} in, {FormatNum3(out_size, true)} out.");

        TimeSpan elapsed = timer.Elapsed;
        Console.Error.WriteLine($"done in {elapsed.Hours:D2}h:{elapsed.Minutes:D2}m:{elapsed.Seconds:D2}s:{elapsed.Milliseconds:D3}ms");
    }

    private static void Decompress(ulong cfile_size, Stream? infd, in ClOptions cl_opts, 
                        bool from_stdin, bool testing)
    {
        ArgumentNullException.ThrowIfNull(infd);

        ulong partial_file_pos = 0, out_size = 0;
        RangeDecoder rdec = new RangeDecoder(infd);
        bool empty = false, multi = false;
        Stopwatch timer = Stopwatch.StartNew();

        for (bool first_member = true; ; first_member = false)
        {
            LzipHeader header = new();
            rdec.ResetMemberPosition();
            int size = rdec.ReadData(header.data, LzipHeader.Size);
            
            if (rdec.Finished())  // End Of File
            {
                if (first_member)
                    throw new Exception("File ends unexpectedly at member header.");

                if (header.CheckPrefix(size))
                    throw new Exception("Truncated header in multimember file.");

                if (size > 0 && !cl_opts.ignore_trailing)
                    throw new Exception("Found trailing data after 'rdec.Finished()'!");

                break;
            }

            if (!header.CheckMagic())
            {
                if (first_member)
                    throw new Exception(IOUtils.bad_magic_msg);

                if (!cl_opts.loose_trailing && header.CheckCorrupt())
                    throw new Exception(IOUtils.corrupt_mm_msg);

                if (!cl_opts.ignore_trailing)
                    throw new Exception("Found trailing data in '!header.CheckMagic()' condition!");

                break;
            }

            if (!header.CheckVersion())
                throw new Exception($"Version {header.Version()} member format not supported.");

            uint dictionarySize = header.DictionarySize();
            if (!Utils.IsValidDs(dictionarySize))
                throw new Exception(IOUtils.bad_dict_msg);

            LzDecoder decoder = new LzDecoder(rdec, dictionarySize, outfd);
            ShowDProgress(cfile_size, partial_file_pos, out_size, decoder, true);  // init
            int result = decoder.DecodeMember();
            partial_file_pos += rdec.MemberPosition();
            out_size += decoder.DecSize();

            if (result != 0)
            {
                if (result <= 2)
                    throw new Exception($"{((result == 2) ? "File ends unexpectedly" : "Decoder error")} at pos {partial_file_pos}");
                else if (result == 5)
                    throw new Exception(IOUtils.nonzero_msg);
                else
                    throw new Exception("Unexpected error from 'decoder.DecodeMember()'!");
            }

            if (!from_stdin)
            {
                multi = !first_member;
                if (decoder.DataPosition() == 0) 
                    empty = true;
            }

            Console.Error.WriteLine(testing ? "ok" : "done");
        }

        TimeSpan elapsed = timer.Elapsed;
        Console.Error.WriteLine($"{(testing ? "ok" : "done")} in {elapsed.Hours:D2}h:{elapsed.Minutes:D2}m:{elapsed.Seconds:D2}s:{elapsed.Milliseconds:D3}ms");

        if (empty && multi)
            throw new Exception(IOUtils.empty_msg);
    }

    private static class ProgressVars              //helper class for functions below
    {
        public static ulong csize = 0;  // file_size / 100
        public static ulong psize = 0;
        public static ulong out_psize = 0;
        public static LZEncoderBase? encb = null;
        public static bool initialized = false;
        public static bool enabled = true;
        public static LzDecoder? dec = null;
        public static Stopwatch timer = Stopwatch.StartNew();
        public static Stopwatch time = Stopwatch.StartNew();
        public static bool oneTimeStart = true;
    }

    public static partial void ShowCProgress(ulong cfile_size, ulong partial_size, ulong out_partial_size, 
                                    in LZEncoderBase? eb, bool init)
    {
        if (!ProgressVars.enabled) return;
        
        if (init)
        {
            if (Console.IsErrorRedirected) 
            { 
                ProgressVars.enabled = false; 
                return; 
            }
            ProgressVars.csize = cfile_size; 
            ProgressVars.psize = partial_size; 
            ProgressVars.out_psize = out_partial_size; 
            ProgressVars.encb = eb;
            ProgressVars.initialized = true;
            if (ProgressVars.oneTimeStart)
                ProgressVars.time.Restart();
            ProgressVars.oneTimeStart = false;
        }
    
        if (ProgressVars.encb != null && ProgressVars.initialized && ProgressVars.timer.ElapsedMilliseconds >= 250)
        {
            ulong pos = ProgressVars.psize + ProgressVars.encb.DataPosition();
            ulong out_pos = ProgressVars.out_psize + ProgressVars.encb.MemberPosition();
            ProgressVars.timer.Restart();
   
            TimeSpan ts = ProgressVars.time.Elapsed;
            if (ProgressVars.csize > 0)
                Console.Error.Write($"{pos / ProgressVars.csize,4}%  {pos / 1048576.0:F1} MB => {out_pos / 1048576.0:F1} MB  {ts.Hours:D2}h:{ts.Minutes:D2}m:{ts.Seconds:D2}s\r");
            else
                Console.Error.Write($"  {pos / 1048576.0:F1} MB => {out_pos / 1048576.0:F1} MB  {ts.Hours:D2}h:{ts.Minutes:D2}m:{ts.Seconds:D2}s\r");
        }
    }
    
    public static partial void ShowDProgress(ulong cfile_size, ulong partial_size, ulong out_partial_size,
                                    in LzDecoder? d, bool init)
    {
        if (!ProgressVars.enabled) return;
        
        if (init)
        {
            if (Console.IsErrorRedirected) 
            { 
                ProgressVars.enabled = false; 
                return; 
            }
            ProgressVars.csize = cfile_size; 
            ProgressVars.psize = partial_size; 
            ProgressVars.out_psize = out_partial_size; 
            ProgressVars.dec = d; 
            ProgressVars.initialized = true; 
            if (ProgressVars.oneTimeStart)
                ProgressVars.time.Restart();
            ProgressVars.oneTimeStart = false;
        }
    
        if (ProgressVars.dec != null && ProgressVars.initialized && ProgressVars.timer.ElapsedMilliseconds >= 250)
        {
            ulong pos = ProgressVars.psize + ProgressVars.dec.rdec.MemberPosition();
            ulong out_pos = ProgressVars.out_psize + ProgressVars.dec.DecSize();
            ProgressVars.timer.Restart();
            
            TimeSpan ts = ProgressVars.time.Elapsed;
            if (ProgressVars.csize > 0)
                Console.Error.Write($"{pos / ProgressVars.csize,4}%  {pos / 1048576.0:F1} MB => {out_pos / 1048576.0:F1} MB  {ts.Hours:D2}h:{ts.Minutes:D2}m:{ts.Seconds:D2}s\r");
            else
                Console.Error.Write($"  {pos / 1048576.0:F1} MB => {out_pos / 1048576.0:F1} MB  {ts.Hours:D2}h:{ts.Minutes:D2}m:{ts.Seconds:D2}s\r");
        }
    }
    
    public static void Main(string[] args)
    {
        LzmaOptions[] option_mapping = {
            new LzmaOptions { dictionary_size = 1 << 20, match_len_limit = 5 },
            new LzmaOptions { dictionary_size = 3 << 19, match_len_limit = 6 },
            new LzmaOptions { dictionary_size = 1 << 21, match_len_limit = 8 },
            new LzmaOptions { dictionary_size = 3 << 20, match_len_limit = 12 },
            new LzmaOptions { dictionary_size = 1 << 22, match_len_limit = 20 },
            new LzmaOptions { dictionary_size = 1 << 23, match_len_limit = 36 },
            new LzmaOptions { dictionary_size = 1 << 24, match_len_limit = 68 },
            new LzmaOptions { dictionary_size = 3 << 23, match_len_limit = 132 },
            new LzmaOptions { dictionary_size = 1 << 25, match_len_limit = 273 } };

        LzmaOptions encoder_options = option_mapping[5];
        const ulong max_member_size = 0x0008000000000000UL;
        const ulong max_volume_size = 0x4000000000000000UL;
        ulong member_size = max_member_size;
        ulong volume_size = 0;
        string default_output_filename = string.Empty;
        Mode program_mode = Mode.Compress;
        ClOptions cl_opts = new();
        bool force = false;
        bool to_stdout = false;
        invocation_name = (System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName.Split('/','\\').Last() ?? invocation_name);
        
        const int opt_lt = 256;
        ArgParser.Option[] options = {
            new ArgParser.Option { code = '1', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '2', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '3', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '4', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '5', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '6', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '7', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '8', long_name = null, has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = '9', long_name = "best", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'a', long_name = "trailing-error", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'b', long_name = "member-size", has_arg = ArgParser.HasArg.Yes },
            new ArgParser.Option { code = 'c', long_name = "stdout", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'd', long_name = "decompress", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'f', long_name = "force", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'h', long_name = "help", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'l', long_name = "list", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'm', long_name = "match-length", has_arg = ArgParser.HasArg.Yes },
            new ArgParser.Option { code = 'o', long_name = "output", has_arg = ArgParser.HasArg.Yes },
            new ArgParser.Option { code = 's', long_name = "dictionary-size", has_arg = ArgParser.HasArg.Yes },
            new ArgParser.Option { code = 'S', long_name = "volume-size", has_arg = ArgParser.HasArg.Yes },
            new ArgParser.Option { code = 't', long_name = "test", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 'V', long_name = "version", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = opt_lt, long_name = "loose-trailing", has_arg = ArgParser.HasArg.No },
            new ArgParser.Option { code = 0, long_name = null, has_arg = ArgParser.HasArg.No } };

        
        ArgParser parser;
        try
        {
            parser = new ArgParser(args, options);
        }
        catch(Exception e)        
        {
            Console.Error.WriteLine($"Error in ArgParser: {e.Message}");
            Environment.Exit(1);
            return;                //to satisfy compiler
        }

        int argind = 0;
        for (; argind < parser.Arguments(); ++argind)
        {
            int code = parser.Code(argind);
            if (code == 0) break;
            
            string pn = parser.ParsedName(argind);
            string arg = parser.Argument(argind);
            
            switch (code)
            {
                case '1': case '2': case '3': case '4':    
                case '5': case '6': case '7': case '8': case '9':
                    encoder_options = option_mapping[code - '1'];  
                    break;
                case 'a': cl_opts.ignore_trailing = false; break;
                case 'b': member_size = GetNum(arg, pn, 102400, max_member_size); break;
                case 'c': to_stdout = true; break;
                case 'd': SetMode(ref program_mode, Mode.Decompress); break;
                case 'f': force = true; break;
                case 'h': ShowHelp(); return;
                case 'l': SetMode(ref program_mode, Mode.List); break;
                case 'm': 
                    encoder_options.match_len_limit = (int)GetNum(arg, pn, 
                                Constants.min_match_len_limit, Constants.max_match_len);
                    break;
                case 'o': 
                    if (arg == "-") to_stdout = true;
                    else default_output_filename = arg;
                    break;
                case 's': 
                    encoder_options.dictionary_size = (int)GetNum(arg, pn,
                                Constants.min_dictionary_size, Constants.max_dictionary_size);
                    break;
                case 'S': volume_size = GetNum(arg, pn, 102400, max_volume_size); break;
                case 't': SetMode(ref program_mode, Mode.Test); break;
                case 'V': ShowVersion(); return;
                case opt_lt: cl_opts.loose_trailing = true; break;
                default: Console.Error.WriteLine("Internal error: uncaught option!"); 
                         Environment.Exit(1); 
                         return;
            }
        }

        List<string> filenames = new();
        bool filenames_given = false;
        for (; argind < parser.Arguments(); ++argind)
        {
            filenames.Add(parser.Argument(argind));
            if (filenames[^1] != "-") filenames_given = true;
        }
        if (filenames.Count == 0) filenames.Add("-");

        if (program_mode == Mode.List)
        {
            try
            {
                ListFiles(filenames, cl_opts);
            }
            catch(Exception e)
            {
                Console.Error.WriteLine(e.Message);
                Environment.Exit(1);
            }
            return;
        }   

        if (program_mode == Mode.Compress)
        {
            if (volume_size > 0 && !to_stdout && !string.IsNullOrEmpty(default_output_filename) &&
                filenames.Count > 1)
            {
                Console.Error.WriteLine("Only can compress one file when using '-o' and '-S'.");
                Environment.Exit(1);
            }
            DisSlots.Init();
            ProbPrices.Init();
        }
        else
            volume_size = 0;
        
        if (program_mode == Mode.Test) to_stdout = false;
        if (program_mode == Mode.Test || to_stdout) default_output_filename = string.Empty;

        if (to_stdout && program_mode != Mode.Test)
        {
            outfd = Console.OpenStandardOutput();
            if (!CheckTtyOut(program_mode))
                Environment.Exit(1);
        }
        else
            outfd = null;

        bool to_file = !to_stdout && program_mode != Mode.Test && !string.IsNullOrEmpty(default_output_filename);
        if (!to_stdout && program_mode != Mode.Test && (filenames_given || to_file))
        {
            SetSignals();
        }

        bool one_to_one = !to_stdout && program_mode != Mode.Test && !to_file;
        bool stdin_used = false;

        foreach (string filename in filenames)
        {
            string input_filename = string.Empty;
            Stream? infd = null;
            FileInfo? in_stats = null;
            bool from_stdin = filename == "-";

            if (from_stdin)                     
            {
                if (stdin_used) continue;
                stdin_used = true;
                infd = Console.OpenStandardInput();
                if (!CheckTtyIn(filename, infd, program_mode)) continue;
                if (one_to_one)
                {
                    outfd = Console.OpenStandardOutput();
                    output_filename = string.Empty;
                }
            }
            else
            {
                int eindex = ExtensionIndex(input_filename = filename);
                try
                {
                    infd = OpenInstream(input_filename, out in_stats);
                }
                catch (IOException e)
                {
                    Console.Error.WriteLine($"Error: {e.Message}");
                    infd?.Dispose();
                    continue;
                }
                if(!CheckTtyIn(filename, infd, program_mode)) continue;
                if (one_to_one)
                {
                    if (program_mode == Mode.Compress)
                        SetCOutname(input_filename, true, true, volume_size > 0);
                    else
                        SetDOutname(input_filename, eindex);
                    
                    try
                    {
                        OpenOutstream(force);
                    }
                    catch (IOException)
                    {                    
                        infd?.Dispose();
                        continue;
                    }
                }
            }

            if (one_to_one && !CheckTtyOut(program_mode))
                Environment.Exit(1);

            if (to_file && outfd == null)
            {
                if (program_mode == Mode.Compress)
                    SetCOutname(default_output_filename, filenames_given, false, volume_size > 0);
                else
                    output_filename = default_output_filename;
                
                OpenOutstream(force);
                if(!CheckTtyOut(program_mode))
                    Environment.Exit(1);
            }

            ulong cfile_size = (!string.IsNullOrEmpty(input_filename) &&
                        in_stats != null) ? (ulong)(in_stats.Length + 99) / 100  : 0; 

            try
            {
                if (program_mode == Mode.Compress)
                {
                    Compress(cfile_size, member_size, volume_size, infd, encoder_options);
                }
                else
                {
                    Decompress(cfile_size, infd, cl_opts, from_stdin, program_mode == Mode.Test);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);

                if (program_mode != Mode.Test)
                    CleanupAndFail();
            }
            finally
            {
                infd?.Dispose();
            }
  
            if (delete_output_on_interrupt && one_to_one)
            {
                outfd?.Dispose();
                delete_output_on_interrupt = false;
            }
        }

        outfd?.Dispose();
        delete_output_on_interrupt = false;
    }

    private static void ListLine(ulong uncomp_size, ulong comp_size, string input_filename)
    {
        if (uncomp_size > 0)
        {
            double savedPercent = 100.0 - ((100.0 * comp_size) / uncomp_size);
            Console.Error.WriteLine("{0,14} {1,14} {2,6:F2}%  {3}",
                            uncomp_size,
                            comp_size,
                            savedPercent,
                            input_filename);
        }
        else
        {
            Console.Error.WriteLine("{0,14} {1,14}   -INF%  {2}",
                            uncomp_size,
                            comp_size,
                            input_filename);
        }
    }

    private static void ListFiles(List<string> filenames, in ClOptions cl_opts)
    {
        ulong total_comp = 0, total_uncomp = 0;
        int files = 0;
        bool first_post = true;
        bool stdin_used = false;

        for (uint i = 0; i < filenames.Count; ++i)
        {
            bool from_stdin = filenames[(int)i] == "-";
            if (from_stdin) 
            { 
                if (stdin_used) continue; 
                else stdin_used = true; 
            }

            string input_filename = from_stdin ? "(stdin)" : filenames[(int)i];
            FileInfo? in_stats = null; // not used
            Stream? infd = null;
            LzipIndex lzip_index;

            try
            {
                infd = from_stdin ? Console.OpenStandardInput() : 
                    OpenInstream(input_filename, out in_stats);
                
                if (infd == null) 
                    throw new IOException($"Error: cannot open '{input_filename}'!");

                lzip_index = new LzipIndex(infd, cl_opts);
            }
            catch(Exception e)
            {
                Console.Error.WriteLine($"{input_filename}: {e.Message}");
                continue;
            }
            finally
            {
                infd?.Dispose();
            }

            bool multi_empty = !from_stdin && lzip_index.MultiEmpty();

            ulong udata_size = (ulong)lzip_index.UdataSize();
            ulong cdata_size = (ulong)lzip_index.CdataSize();
            total_comp += cdata_size; 
            total_uncomp += udata_size; 
            files++;

            long members = lzip_index.Members();

            if (first_post)
            {
                first_post = false;
                Console.Error.Write("   dict   memb  trail ");
                Console.Error.Write("  uncompressed     compressed   saved  name\n");
            }

            Console.Error.Write("   {0,4} {1,6} {2,6} ", 
            FormatNum3(lzip_index.DictionarySize()), members, 
                    (ulong)lzip_index.FileSize() - cdata_size);

            ListLine(udata_size, cdata_size, input_filename);

            if (multi_empty)
                Console.Error.WriteLine($"{input_filename}: {IOUtils.empty_msg}"); 
        }

        if (files > 1)
        {
            Console.Error.Write("                      ");
            ListLine(total_uncomp, total_comp, "(totals)");
        }
    }
}