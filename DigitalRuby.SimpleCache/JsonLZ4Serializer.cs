namespace DigitalRuby.SimpleCache;

/// <summary>
/// Interface for serializing cache objects to/from bytes
/// </summary>
internal interface ISerializer
{
    /// <summary>
    /// Deserialize
    /// </summary>
    /// <param name="bytes">Bytes to deserialize</param>
    /// <param name="type">Type of object to deserialize to</param>
    /// <returns>Deserialized object or null if failure</returns>
    object? Deserialize(byte[]? bytes, Type type);

    /// <summary>
    /// Serialize an object
    /// </summary>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize(object? obj);

    /// <summary>
    /// Get a description for the serializer
    /// </summary>
    string Description { get; }
}

/// <summary>
/// Compressed json serializer using lz4
/// </summary>
internal sealed class JsonLZ4Serializer : ISerializer
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
        using var stream = LZ4Stream.Decode(new MemoryStream(bytes));
        var result = System.Text.Json.JsonSerializer.Deserialize(stream, type);
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
        MemoryStream ms = new();
        {
            using var stream = LZ4Stream.Encode(ms, leaveOpen: true);
            System.Text.Json.JsonSerializer.Serialize(stream, obj);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Description
    /// </summary>
    public string Description { get; } = "json-lz4";

    /// <summary>
    /// Singleton instance (thread safe)
    /// </summary>
    public static JsonLZ4Serializer Instance { get; } = new();
}
