using K4os.Compression.LZ4;

namespace DigitalRuby.SimpleCache;

/// <summary>
/// Interface for serializing cache objects to/from bytes
/// </summary>
public interface ISerializer
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
        byte[] decompressedBytes = LZ4Pickler.Unpickle(bytes);
        var result = System.Text.Json.JsonSerializer.Deserialize(decompressedBytes, type);
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
        return LZ4Pickler.Pickle(bytes);
    }

    /// <summary>
    /// Description
    /// </summary>
    public string Description { get; } = "json-lz4";
}
