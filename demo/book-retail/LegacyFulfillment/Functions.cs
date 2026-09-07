using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Azure.Functions.Worker;

public static class Program
{
    public static async Task Main()
    {
        var bootstrap = Environment.GetEnvironmentVariable("BOOK_RETAIL_KAFKA") ?? "127.0.0.1:4403";
        using var consumer = new ConsumerBuilder<string, FulfilmentRequested>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "legacy-fulfilment",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        }).SetValueDeserializer(new KafkaJsonDeserializer<FulfilmentRequested>()).Build();
        using var producer = new ProducerBuilder<string, DispatchRequested>(new ProducerConfig { BootstrapServers = bootstrap })
            .SetValueSerializer(new KafkaJsonSerializer<DispatchRequested>()).Build();
        var functions = new FulfilmentFunctions();
        consumer.Subscribe("fulfilment.requested");
        while (!Shutdown.Token.IsCancellationRequested)
        {
            var message = consumer.Consume(Shutdown.Token).Message.Value;
            var dispatch = functions.Fulfil(message);
            await producer.ProduceAsync("dispatch.requested", new Message<string, DispatchRequested>
            {
                Key = dispatch.OrderId,
                Value = dispatch
            }, Shutdown.Token);
        }
    }
}

public sealed record FulfilmentRequested(string OrderId);
public sealed record DispatchRequested(string OrderId);

public sealed class KafkaJsonSerializer<T> : ISerializer<T>
{
    public byte[] Serialize(T data, SerializationContext context) => JsonSerializer.SerializeToUtf8Bytes(data);
}

public sealed class KafkaJsonDeserializer<T> : IDeserializer<T>
{
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) =>
        JsonSerializer.Deserialize<T>(data) ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");
}

public sealed class FulfilmentFunctions
{
    [Function("FulfilOrder")]
    [ServiceBusOutput("dispatch.requested")]
    public DispatchRequested Fulfil(
        [ServiceBusTrigger("fulfilment.requested")] FulfilmentRequested request) =>
        new(request.OrderId);
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
