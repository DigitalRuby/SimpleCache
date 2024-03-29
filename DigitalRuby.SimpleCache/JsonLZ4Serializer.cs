﻿using K4os.Compression.LZ4;

namespace DigitalRuby.SimpleCache;

/// <summary>
/// Compressed json serializer using lz4
/// </summary>
public sealed class JsonLZ4Serializer : ISerializer
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public byte[]? Serialize(object? obj)
    {
        if (obj is null)
        {
            return null;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj);
        return LZ4Pickler.Pickle(bytes);
    }

    /// <inheritdoc />
    public string Description { get; } = "json-lz4";
}
