using Support.Api.Modules.Orders.Domain;

namespace Support.Api.Modules.Orders.Application;

/// <summary>
/// Where orders live: the synthetic fixtures, plus the changes approved simulated writes made (stored durably since
/// L11; the fixture files are never changed). Inside an atomic unit of work, an update joins that transaction.
/// </summary>
public interface IOrderRepository
{
    Order? Find(string orderId);

    void Update(Order order);
}
