using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SiscoNet.Core;

public static class DiscordIpcProtocol
{
    public const int HandshakeOpcode = 0;
    public const int FrameOpcode = 1;

    public static byte[] CreateHandshake(string applicationId) =>
        CreateFrame(HandshakeOpcode, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = applicationId }));

    public static byte[] CreateSetActivity(string details, string state, DateTimeOffset startedAt, string nonce)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid = Process.GetCurrentProcess().Id,
                activity = new
                {
                    details = Limit(details),
                    state = Limit(state),
                    timestamps = new { start = startedAt.ToUnixTimeSeconds() },
                    instance = false
                }
            },
            nonce
        });
        return CreateFrame(FrameOpcode, payload);
    }

    public static byte[] CreateFrame(int opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > 1_048_576) throw new InvalidDataException("Discord returned an invalid IPC frame length.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Discord closed the IPC connection.");
            offset += read;
        }
    }

    private static string Limit(string value) => value.Length <= 128 ? value : value[..128];
}