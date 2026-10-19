using System.Buffers.Binary;
using System.Text;

namespace Skypod.Core;

public static class XwmValidator
{
    private const ushort WmaVersion2FormatTag = 0x0161;

    public static void Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (stream.Length < 20 || ReadTag(reader) != "RIFF")
        {
            throw new InvalidDataException($"'{path}' is not a RIFF file.");
        }

        _ = reader.ReadUInt32();
        if (ReadTag(reader) != "XWMA")
        {
            throw new InvalidDataException($"'{path}' is not an XWMA file. Renaming another audio format to .xwm does not convert it.");
        }

        var hasFormat = false;
        var hasPacketTable = false;
        var hasAudioData = false;
        var formatHeader = new byte[4];

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadTag(reader);
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;
            var chunkEnd = checked(chunkStart + chunkSize);
            if (chunkEnd > stream.Length)
            {
                throw new InvalidDataException($"'{path}' contains a truncated {chunkId} chunk.");
            }

            switch (chunkId)
            {
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw new InvalidDataException($"'{path}' has an invalid XWMA format chunk.");
                    }

                    stream.ReadExactly(formatHeader);
                    var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(formatHeader);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(formatHeader[2..]);
                    if (formatTag != WmaVersion2FormatTag)
                    {
                        throw new InvalidDataException($"'{path}' uses unsupported codec tag 0x{formatTag:X4}; Skyrim music expects WMA v2 XWMA.");
                    }
                    if (channels is < 1 or > 2)
                    {
                        throw new InvalidDataException($"'{path}' has {channels} channels; Skyrim XWMA music must be mono or stereo.");
                    }
                    hasFormat = true;
                    break;
                case "dpds":
                    hasPacketTable = chunkSize >= 4;
                    break;
                case "data":
                    hasAudioData = chunkSize > 0;
                    break;
            }

            stream.Position = chunkEnd + (chunkSize & 1u);
        }

        if (!hasFormat || !hasPacketTable || !hasAudioData)
        {
            throw new InvalidDataException(
                $"'{path}' is missing one or more required XWMA chunks: fmt, dpds, and data.");
        }
    }

    private static string ReadTag(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(bytes);
    }
}
