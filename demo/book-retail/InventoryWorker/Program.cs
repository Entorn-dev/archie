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
        var bootstrap = Environment.GetEnvironmentVariable("BOOK_RETAIL_KAFKA") ?? "127.0.0.1:4403";
        await RunPostgres("""
            CREATE TABLE IF NOT EXISTS inventory_reservations (
                order_id text PRIMARY KEY,
                book_id text NOT NULL,
                quantity integer NOT NULL,
                status text NOT NULL
            );
            """, Shutdown.Token);

        using var consumer = new ConsumerBuilder<string, InventoryReservationRequested>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "inventory-worker",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = true
        }).SetValueDeserializer(new KafkaJsonDeserializer<InventoryReservationRequested>()).Build();
        using var producer = new ProducerBuilder<string, InventoryReserved>(new ProducerConfig { BootstrapServers = bootstrap })
            .SetValueSerializer(new KafkaJsonSerializer<InventoryReserved>()).Build();
        consumer.Subscribe("inventory.reservation-requested");

        while (!Shutdown.Token.IsCancellationRequested)
        {
            var message = consumer.Consume(Shutdown.Token).Message.Value;
            await RunPostgres(
                "INSERT INTO inventory_reservations (order_id, book_id, quantity, status) VALUES (:'id', :'book', :'quantity'::integer, 'reserved') " +
                "ON CONFLICT (order_id) DO UPDATE SET status = EXCLUDED.status;",
                Shutdown.Token, ("id", message.OrderId), ("book", message.BookId), ("quantity", message.Quantity.ToString()));
            await producer.ProduceAsync("inventory.reserved", new Message<string, InventoryReserved>
            {
                Key = message.OrderId,
                Value = new(message.OrderId, message.BookId, message.Quantity)
            }, Shutdown.Token);
        }
    }

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

public sealed record InventoryReservationRequested(string OrderId, string BookId, int Quantity);
public sealed record InventoryReserved(string OrderId, string BookId, int Quantity);

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
