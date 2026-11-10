using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application;

/// <summary>
/// Where orders live. Today the synthetic fixtures, loaded into memory: approved simulated writes (L10) change this
/// in-memory copy only, never the fixture files, and the changes are lost on restart until L11.
/// </summary>
public interface IOrderRepository
{
    Order? Find(string orderId);

    void Update(Order order);
}
