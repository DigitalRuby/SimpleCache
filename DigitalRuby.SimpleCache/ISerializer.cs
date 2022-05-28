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
    /// <returns>Deserialized object or null if bytes is null or empty</returns>
    object? Deserialize(byte[]? bytes, Type type);

    /// <summary>
    /// Deserialize using generic type parameter
    /// </summary>
    /// <typeparam name="T">Type of object to deserialize</typeparam>
    /// <param name="bytes">Bytes</param>
    /// <returns>Deserialized object or null if bytes is null or empty</returns>
    T? Deserialize<T>(byte[]? bytes) => (T?)Deserialize(bytes, typeof(T));

    /// <summary>
    /// Serialize an object
    /// </summary>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize(object? obj);

    /// <summary>
    /// Serialize using generic type parameter
    /// </summary>
    /// <typeparam name="T">Type of object</typeparam>
    /// <param name="obj">Object to serialize</param>
    /// <returns>Serialized bytes or null if obj is null</returns>
    byte[]? Serialize<T>(T? obj) => Serialize(obj);

    /// <summary>
    /// Get a short description for the serializer, i.e. json or json-lz4.
    /// </summary>
    string Description { get; }
}
