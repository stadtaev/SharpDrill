using System.Text.Json;

namespace SharpView.Core;

public sealed record SnapshotDocument(int SchemaVersion, string ProducerVersion, ProgramSnapshot Snapshot);

/// <summary>Reads and writes the versioned envelope emitted by inspect --format json.</summary>
/// <remarks>Supports schema 3 only; no implicit migrations. Streams remain owned by the caller.
/// Envelope validation is not validation of every fact or graph reference in the snapshot.</remarks>
public static class SnapshotJson
{
    public const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static Task WriteAsync(Stream destination, ProgramSnapshot snapshot, string producerVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        return JsonSerializer.SerializeAsync(destination,
            new SnapshotDocument(CurrentSchemaVersion, producerVersion, snapshot), Options, cancellationToken);
    }

    public static async Task<SnapshotDocument> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        using var json = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
            throw new JsonException("Snapshot document requires an integer schemaVersion.");
        // Inspect the version before interpreting a payload whose shape may belong to another schema.
        if (version != CurrentSchemaVersion)
            throw new NotSupportedException($"Snapshot schema version {version} is unsupported; expected {CurrentSchemaVersion}.");
        if (!root.TryGetProperty("producerVersion", out var producer) || producer.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(producer.GetString()))
            throw new JsonException("Snapshot document requires a nonempty producerVersion.");
        if (!root.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
            throw new JsonException("Snapshot document requires a snapshot object.");
        cancellationToken.ThrowIfCancellationRequested();
        var result = snapshot.Deserialize<ProgramSnapshot>(Options)!;
        cancellationToken.ThrowIfCancellationRequested();
        return new(version, producer.GetString()!, result);
    }
}
