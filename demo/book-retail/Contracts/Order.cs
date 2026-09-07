namespace BookRetail.Contracts;

public sealed record PlaceOrder(string Id, string BookId, int Quantity, string PaymentToken);

public sealed record OrderSubmitted(string Id, string BookId, int Quantity);

public sealed record InventoryReservationRequested(string OrderId, string BookId, int Quantity);

public sealed record InventoryReserved(string OrderId, string BookId, int Quantity);

public sealed record FulfilmentRequested(string OrderId);

public sealed record DispatchRequested(string OrderId);
