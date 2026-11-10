using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application;

/// <summary>Where orders are read from. Today the synthetic fixtures; the application never knows which.</summary>
public interface IOrderRepository
{
    Order? Find(string orderId);
}
