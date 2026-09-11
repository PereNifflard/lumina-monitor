using System.Buffers.Binary;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The capture file the probe writes, and everything that plays one back reads.
/// </summary>
/// <remarks>
/// Three records, one per datagram, and nothing else — a length, the time it
/// arrived, and the datagram as it came off the wire:
///
/// <code>
/// [u32 little-endian  size of the datagram]
/// [u64 little-endian  microseconds, from the recorder's own clock]
/// [size bytes         the datagram, RTP or RTCP, untouched]
/// </code>
///
/// <para>No header and no index, which is deliberate: a capture is appended to
/// while it is being written and a run that is killed halfway leaves a file whose
/// every complete record is still readable. A truncated last record ends the
/// reading rather than failing it.</para>
///
/// <para>It lives here rather than in each of its readers because there are three
/// of them — the video replay, the offline audio decode and the audio playback —
/// and a format written out three times is a format that will be read three
/// slightly different ways.</para>
/// </remarks>
internal static class RtpCapture
{
    /// <summary>The fixed part of a record: the size, then the timestamp.</summary>
    private const int HeaderBytes = 12;

    /// <summary>Reads a capture, record by record, stopping at the first one that is not whole.</summary>
    public static IEnumerable<(byte[] Datagram, long Microseconds)> Read(string path)
    {
        using var file = File.OpenRead(path);
        byte[] header = new byte[HeaderBytes];
        while (true)
        {
            if (!Fill(file, header, HeaderBytes))
                yield break;
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            long microseconds = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4));
            if (size is <= 0 or > 65535)
                yield break;
            byte[] datagram = new byte[size];
            if (!Fill(file, datagram, size))
                yield break;
            yield return (datagram, microseconds);
        }
    }

    private static bool Fill(Stream stream, byte[] buffer, int count)
    {
        int at = 0;
        while (at < count)
        {
            int read = stream.Read(buffer, at, count - at);
            if (read <= 0)
                return false;
            at += read;
        }
        return true;
    }
}
