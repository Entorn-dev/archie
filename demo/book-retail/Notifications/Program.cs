using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using System.Text.Json;

public static class Program
{
    public static async Task Main()
    {
        var bootstrap = Environment.GetEnvironmentVariable("BOOK_RETAIL_KAFKA") ?? "127.0.0.1:4403";
        var output = Environment.GetEnvironmentVariable("BOOK_RETAIL_NOTIFICATION_FILE") ?? ".amp/runtime/book-retail/notification.json";
        using var consumer = new ConsumerBuilder<string, DispatchRequested>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "notifications",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        }).SetValueDeserializer(new KafkaJsonDeserializer<DispatchRequested>()).Build();
        consumer.Subscribe("dispatch.requested");
        while (!Shutdown.Token.IsCancellationRequested)
        {
            var message = consumer.Consume(Shutdown.Token).Message.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                orderId = message.OrderId,
                status = "dispatch-notified"
            }), Shutdown.Token);
        }
    }
}

public sealed record DispatchRequested(string OrderId);

public sealed class KafkaJsonDeserializer<T> : IDeserializer<T>
{
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) =>
        JsonSerializer.Deserialize<T>(data) ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");
}

internal static class Shutdown
{
    public static CancellationToken Token { get; } = Create();

    private static CancellationToken Create()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; source.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => source.Cancel();
        return source.Token;
    }
}
