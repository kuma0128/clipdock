using System.Buffers.Binary;
using System.Text.Json;

namespace ClipDock.Core;

public static class NativeProtocol
{
    public const int MaxMessageBytes = 512 * 1024;

    public static async Task<T> ReadAsync<T>(Stream input, CancellationToken cancellationToken = default)
    {
        var prefix = new byte[4];
        await input.ReadExactlyAsync(prefix, cancellationToken);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is <= 0 or > MaxMessageBytes) throw new InvalidDataException("メッセージのサイズが正しくありません。");
        var body = new byte[size];
        await input.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, Json.Options) ?? throw new InvalidDataException("空のメッセージです。");
    }

    public static async Task WriteAsync(Stream output, object value, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        if (body.Length > MaxMessageBytes) throw new InvalidDataException("メッセージが大きすぎます。");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await output.WriteAsync(prefix, cancellationToken);
        await output.WriteAsync(body, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
