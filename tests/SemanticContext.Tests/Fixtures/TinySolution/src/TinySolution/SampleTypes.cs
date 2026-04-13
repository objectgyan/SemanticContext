using Microsoft.AspNetCore.Mvc;

namespace TinySolution;

public interface IOrderService
{
    Task<Order> GetOrderAsync(int id, CancellationToken cancellationToken);
}

public record Order(int Id, string Number);

public class OrderService : IOrderService
{
    public string SourceName { get; } = "OrderService";

    public Task<Order> GetOrderAsync(int id, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Order(id, $"ORD-{id:0000}"));
    }
}

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Order>> GetOrderAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderAsync(id, cancellationToken).ConfigureAwait(false);
        return Ok(order);
    }
}

