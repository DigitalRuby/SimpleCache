namespace DigitalRuby.SimpleCache.Sandbox;

/// <summary>
/// Uncompressed json serializer
/// </summary>
internal sealed class JsonSerializer : ISerializer
{
    /// <summary>
    /// Deserialize an object from compressed json bytes
    /// </summary>
    /// <param name="bytes">Compressed json bytes</param>
    /// <param name="type">Type of object</param>
    /// <returns>Object</returns>
    public object? Deserialize(byte[]? bytes, Type type)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }
        var result = System.Text.Json.JsonSerializer.Deserialize(bytes, type);
        return result;
    }

    /// <summary>
    /// Serialize an object to compressed json bytes
    /// </summary>
    /// <param name="obj">Object</param>
    /// <returns>Compressed json bytes</returns>
    public byte[]? Serialize(object? obj)
    {
        if (obj is null)
        {
            return null;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj);
        return bytes;
    }

    /// <summary>
    /// Description
    /// </summary>
    public string Description { get; } = "json";
}
