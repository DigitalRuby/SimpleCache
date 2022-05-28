namespace DigitalRuby.SimpleCache;

/// <summary>
/// Uncompressed json serializer
/// </summary>
public sealed class JsonSerializer : ISerializer
{
    /// <inheritdoc />
    public object? Deserialize(byte[]? bytes, Type type)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }
        var result = System.Text.Json.JsonSerializer.Deserialize(bytes, type);
        return result;
    }

    /// <inheritdoc />
    public byte[]? Serialize(object? obj)
    {
        if (obj is null)
        {
            return null;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj);
        return bytes;
    }

    /// <inheritdoc />
    public string Description { get; } = "json";
}
