using System;
using System.Net.Http;
using System.Text.Json;
using Confluent.Kafka;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("payments", client =>
    client.BaseAddress = new Uri(builder.Configuration["Payment:BaseUrl"] ?? "http://payment.local:4402"));
builder.Services.AddSingleton<IProducer<string, OrderSubmitted>>(_ =>
    new ProducerBuilder<string, OrderSubmitted>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "127.0.0.1:4403"
    }).SetValueSerializer(new KafkaJsonSerializer<OrderSubmitted>()).Build());
var app = builder.Build();
var api = app.MapGroup("/api");

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
api.MapPost("/orders", async (PlaceOrder order, IHttpClientFactory clients, IProducer<string, OrderSubmitted> producer) =>
{
    var payment = await clients.CreateClient("payments").PostAsJsonAsync("/payments", new
    {
        orderId = order.Id,
        token = order.PaymentToken,
        amount = order.Quantity * 1299
    });
    if (!payment.IsSuccessStatusCode) return Results.Problem("Payment was declined.", statusCode: 402);

    await producer.ProduceAsync("order.submitted", new Message<string, OrderSubmitted>
    {
        Key = order.Id,
        Value = new(order.Id, order.BookId, order.Quantity)
    });
    return Results.Accepted($"/api/orders/{order.Id}", new { order.Id, status = "submitted" });
});

app.Run();

public sealed record PlaceOrder(string Id, string BookId, int Quantity, string PaymentToken);
public sealed record OrderSubmitted(string Id, string BookId, int Quantity);

public sealed class KafkaJsonSerializer<T> : ISerializer<T>
{
    public byte[] Serialize(T data, SerializationContext context) => JsonSerializer.SerializeToUtf8Bytes(data);
}
