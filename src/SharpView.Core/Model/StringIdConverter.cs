using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpView.Core;

public interface IStringId<TSelf> where TSelf : struct, IStringId<TSelf>
{
    string Value { get; }
    static abstract TSelf Create(string value);
}

// IDs are opaque strings on the wire; wrapping them in {"value": ...} objects only added noise.
public sealed class StringIdConverter<T> : JsonConverter<T> where T : struct, IStringId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        T.Create(reader.GetString() ?? throw new JsonException("ID must be a string."));
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
