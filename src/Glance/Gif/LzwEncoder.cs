using System.IO;

namespace Glance.Gif;

/// <summary>GIF-flavoured variable-width LZW, written as length-prefixed sub-blocks.</summary>
internal sealed class LzwEncoder
{
    private const int MaxCode = 4095;
    private const int HashSize = 5003; // prime, > 4096 * 1.2

    private readonly int[] _hashKeys = new int[HashSize];
    private readonly short[] _hashCodes = new short[HashSize];
    private readonly byte[] _block = new byte[255];

    private Stream _output = Stream.Null;
    private int _blockLength;
    private uint _bitBuffer;
    private int _bitCount;

    public void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        _output = output;
        _blockLength = 0;
        _bitBuffer = 0;
        _bitCount = 0;

        output.WriteByte((byte)minCodeSize);
        var clearCode = 1 << minCodeSize;
        var codeSize = minCodeSize + 1;
        var nextCode = clearCode + 2;
        ResetTable();
        WriteCode(clearCode, codeSize);

        var current = -1;
        foreach (var value in indices)
        {
            if (current < 0)
            {
                current = value;
                continue;
            }

            var key = (current << 8 | value) + 1; // +1 so 0 means "empty slot"
            var slot = Find(key);
            if (_hashKeys[slot] == key)
            {
                current = _hashCodes[slot];
                continue;
            }

            WriteCode(current, codeSize);
            var added = nextCode++;
            _hashKeys[slot] = key;
            _hashCodes[slot] = (short)added;
            // The decoder adds each entry one code later than we do, so widen only once the
            // code just added no longer fits in the current width.
            if (added >= 1 << codeSize)
            {
                codeSize++;
            }

            if (added == MaxCode)
            {
                WriteCode(clearCode, codeSize);
                ResetTable();
                codeSize = minCodeSize + 1;
                nextCode = clearCode + 2;
            }

            current = value;
        }

        WriteCode(current, codeSize);

        // The decoder adds an entry for that last code; if that fills the current width it reads
        // the next code one bit wider, so match it for the end-of-information code.
        if (nextCode == 1 << codeSize && codeSize < 12)
        {
            codeSize++;
        }

        WriteCode(clearCode + 1, codeSize); // end of information
        if (_bitCount > 0)
        {
            PushByte((byte)_bitBuffer);
        }

        FlushBlock();
        output.WriteByte(0); // block terminator
    }

    private int Find(int key)
    {
        var slot = (int)((uint)key * 2654435761u % HashSize);
        while (_hashKeys[slot] != 0 && _hashKeys[slot] != key)
        {
            slot = slot == 0 ? HashSize - 1 : slot - 1;
        }

        return slot;
    }

    private void ResetTable() => Array.Clear(_hashKeys);

    private void WriteCode(int code, int size)
    {
        _bitBuffer |= (uint)code << _bitCount;
        _bitCount += size;
        while (_bitCount >= 8)
        {
            PushByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void PushByte(byte b)
    {
        _block[_blockLength++] = b;
        if (_blockLength == _block.Length)
        {
            FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_blockLength == 0)
        {
            return;
        }

        _output.WriteByte((byte)_blockLength);
        _output.Write(_block, 0, _blockLength);
        _blockLength = 0;
    }
}
