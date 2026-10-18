using System.Text;
using System.Text.Json;
using SharpView.Core;
using Xunit;

namespace SharpView.Tests;

public sealed class SnapshotJsonTests
{
    // Hand-written schema-3 input ensures reader and writer cannot hide the same wire-format mistake.
    private const string Document = """
        {"schemaVersion":3,"producerVersion":"0.1.0","snapshot":{
          "sourceRoot":"/example",
          "callables":[
            {"id":"entry","compilation":"project","kind":0,"displaySignature":"Entry()","accessibility":"Public",
             "returnType":{"displayName":"void","nullability":0,"components":[]},"parameters":[],"attributes":[],"body":0,"declarations":[]},
            {"id":"leaf","compilation":"project","kind":0,"displaySignature":"Leaf()","accessibility":"Public",
             "returnType":{"displayName":"void","nullability":0,"components":[]},"parameters":[],"attributes":[],"body":0,"declarations":[]}],
          "callSites":[{"id":"call","caller":"entry","boundTarget":"leaf","targetDisplay":"Leaf()","dispatch":0,
            "typeArguments":[],"evidence":{"path":"Example.cs","checksum":"abc","spanStart":10,"spanLength":6,
            "startLine":2,"startColumn":3,"isGenerated":false},"isImplicit":false,"protectedRegions":[]}]
        }}
        """;

    [Fact]
    public async Task Schema_three_document_supports_offline_reachability()
    {
        using var input = Stream(Document);
        var document = await SnapshotJson.ReadAsync(input);
        Assert.Equal("0.1.0", document.ProducerVersion);
        Assert.Equal("/example", document.Snapshot.SourceRoot);
        var index = new ProgramIndex(document.Snapshot);
        var root = Assert.Single(ReachabilityQuery.Analyze(index, [new("entry")], new()).Roots);
        var hit = Assert.Single(root.Reached);
        Assert.Equal(new CallableId("leaf"), hit.Callable);
        Assert.Equal(1, hit.Distance);
        Assert.Equal("Example.cs", index.CallSite(Assert.Single(hit.Witness).Site).Evidence.Path);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Writer_preserves_schema_three_envelope_and_string_ids()
    {
        using var input = Stream(Document);
        var original = await SnapshotJson.ReadAsync(input);
        using var output = new MemoryStream();
        await SnapshotJson.WriteAsync(output, original.Snapshot, "test-producer");
        output.Position = 0;
        using (var json = await JsonDocument.ParseAsync(output))
        {
            Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("entry", json.RootElement.GetProperty("snapshot").GetProperty("callables")[0].GetProperty("id").GetString());
        }
        output.Position = 0;
        var restored = await SnapshotJson.ReadAsync(output);
        Assert.Equal("test-producer", restored.ProducerVersion);
        Assert.Equal(original.Snapshot.CallSites[0].Evidence, restored.Snapshot.CallSites[0].Evidence);
        Assert.Equal(new CallableId("leaf"), restored.Snapshot.CallSites[0].BoundTarget);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Unsupported_schema_is_rejected_before_payload_deserialization(int version)
    {
        using var input = Stream($$"""{"schemaVersion":{{version}},"producerVersion":"other","snapshot":"incompatible"}""");
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => SnapshotJson.ReadAsync(input));
        Assert.Contains(version.ToString(), error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"3\",\"producerVersion\":\"x\",\"snapshot\":{}}")]
    [InlineData("{\"schemaVersion\":3,\"snapshot\":{}}")]
    [InlineData("{\"schemaVersion\":3,\"producerVersion\":\"\",\"snapshot\":{}}")]
    [InlineData("{\"schemaVersion\":3,\"producerVersion\":\"x\"}")]
    [InlineData("{\"schemaVersion\":3,\"producerVersion\":\"x\",\"snapshot\":null}")]
    [InlineData("{\"schemaVersion\":3,\"producerVersion\":\"x\",\"snapshot\":[]}")]
    public async Task Invalid_envelope_is_rejected(string json)
    {
        using var input = Stream(json);
        await Assert.ThrowsAsync<JsonException>(() => SnapshotJson.ReadAsync(input));
    }

    [Fact]
    public async Task Cancellation_is_honored_without_closing_streams()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = Stream(Document);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotJson.ReadAsync(input, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotJson.WriteAsync(output, new(), "test", cancellation.Token));
        Assert.Equal(0, output.Length);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json));
}
