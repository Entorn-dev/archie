using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;

public static class Program
{
    public static async Task Main()
    {
        var cancellation = Shutdown.Token;
        var bootstrap = Environment.GetEnvironmentVariable("BOOK_RETAIL_KAFKA") ?? "127.0.0.1:4403";
        await RunPostgres("""
            CREATE TABLE IF NOT EXISTS orders (
                id text PRIMARY KEY,
                book_id text NOT NULL,
                quantity integer NOT NULL,
                status text NOT NULL
            );
            """, cancellation);
        using var submitted = Consumer<OrderSubmitted>(bootstrap, "ordering-submitted");
        submitted.Subscribe("order.submitted");
        using var reserved = Consumer<InventoryReserved>(bootstrap, "ordering-reserved");
        reserved.Subscribe("inventory.reserved");
        using var inventory = Producer<InventoryReservationRequested>(bootstrap);
        using var fulfilment = Producer<FulfilmentRequested>(bootstrap);

        var submittedTask = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = submitted.Consume(cancellation).Message.Value;
                await RunPostgres(
                    "INSERT INTO orders (id, book_id, quantity, status) VALUES (:'id', :'book', :'quantity'::integer, 'inventory-requested') " +
                    "ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status;",
                    cancellation, ("id", message.Id), ("book", message.BookId), ("quantity", message.Quantity.ToString()));
                await inventory.ProduceAsync("inventory.reservation-requested", new Message<string, InventoryReservationRequested>
                {
                    Key = message.Id,
                    Value = new(message.Id, message.BookId, message.Quantity)
                }, cancellation);
            }
        }, cancellation);

        var reservedTask = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = reserved.Consume(cancellation).Message.Value;
                await RunPostgres("UPDATE orders SET status = 'fulfilment-requested' WHERE id = :'id';",
                    cancellation, ("id", message.OrderId));
                await fulfilment.ProduceAsync("fulfilment.requested", new Message<string, FulfilmentRequested>
                {
                    Key = message.OrderId,
                    Value = new(message.OrderId)
                }, cancellation);
            }
        }, cancellation);

        await Task.WhenAll(submittedTask, reservedTask);
    }

    private static IConsumer<string, T> Consumer<T>(string bootstrap, string group)
    {
        return new ConsumerBuilder<string, T>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        }).SetValueDeserializer(new KafkaJsonDeserializer<T>()).Build();
    }

    private static IProducer<string, T> Producer<T>(string bootstrap) =>
        new ProducerBuilder<string, T>(new ProducerConfig { BootstrapServers = bootstrap })
            .SetValueSerializer(new KafkaJsonSerializer<T>()).Build();

    private static async Task RunPostgres(string sql, CancellationToken cancellation,
        params (string Name, string Value)[] variables)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BOOK_RETAIL_PSQL") ?? "psql")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-X", "-v", "ON_ERROR_STOP=1", "-h", "127.0.0.1", "-p", "4405", "-U", "archie", "-d", "bookretail", "-q" })
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in variables)
        {
            start.ArgumentList.Add("--set");
            start.ArgumentList.Add($"{name}={value}");
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start psql.");
        await process.StandardInput.WriteAsync(sql.AsMemory(), cancellation);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    }
}

public sealed record OrderSubmitted(string Id, string BookId, int Quantity);
public sealed record InventoryReservationRequested(string OrderId, string BookId, int Quantity);
public sealed record InventoryReserved(string OrderId, string BookId, int Quantity);
public sealed record FulfilmentRequested(string OrderId);

public sealed class KafkaJsonSerializer<T> : ISerializer<T>
{
    public byte[] Serialize(T data, SerializationContext context) => JsonSerializer.SerializeToUtf8Bytes(data);
}

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
