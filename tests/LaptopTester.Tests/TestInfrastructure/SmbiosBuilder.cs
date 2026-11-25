using System.IO;
using System.Text;

namespace LaptopTester.Tests.TestInfrastructure;

/// <summary>Builds synthetic raw SMBIOS RSMB buffers for deterministic parser tests.</summary>
internal sealed class SmbiosBuilder
{
    private readonly MemoryStream _table = new();
    private ushort _nextHandle = 1;

    public SmbiosBuilder Add(byte type, byte length, Action<byte[]> fill, params string[] strings)
    {
        if (length < 4)
            throw new ArgumentOutOfRangeException(nameof(length), "SMBIOS structure length must be at least 4.");

        byte[] body = new byte[length];
        body[0] = type;
        body[1] = length;
        ushort handle = _nextHandle++;
        body[2] = (byte)(handle & 0xFF);
        body[3] = (byte)(handle >> 8);
        fill(body);
        _table.Write(body, 0, body.Length);

        if (strings.Length == 0)
        {
            _table.WriteByte(0);
            _table.WriteByte(0);
        }
        else
        {
            foreach (string s in strings)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                _table.Write(utf8, 0, utf8.Length);
                _table.WriteByte(0);
            }
            _table.WriteByte(0);
        }

        return this;
    }

    public SmbiosBuilder End() => Add(127, 4, _ => { });

    public byte[] Build()
    {
        byte[] tableBytes = _table.ToArray();
        byte[] result = new byte[8 + tableBytes.Length];
        result[0] = 0; // Used20CallingMethod
        result[1] = 3; // SMBIOSMajorVersion
        result[2] = 6; // SMBIOSMinorVersion
        result[3] = 0; // DmiRevision
        BitConverter.GetBytes(tableBytes.Length).CopyTo(result, 4);
        Buffer.BlockCopy(tableBytes, 0, result, 8, tableBytes.Length);
        return result;
    }
}
