using Meridian.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.ModularMonolith;

public static class Billing
{
    public sealed class BillingModule : IModuleStartup
    {
        public string Name => "Billing";

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IBillingLedger, InMemoryBillingLedger>();
        }
    }

    public interface IBillingLedger
    {
        Task AddAsync(Invoice invoice, CancellationToken cancellationToken);
        Task<Invoice[]> GetAllAsync(CancellationToken cancellationToken);
        Task<decimal> GetTotalAsync(CancellationToken cancellationToken);
    }

    public sealed class InMemoryBillingLedger : IBillingLedger
    {
        private readonly List<Invoice> _invoices = [];
        private readonly object _lock = new();

        public Task AddAsync(Invoice invoice, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _invoices.Add(invoice);
            }

            return Task.CompletedTask;
        }

        public Task<Invoice[]> GetAllAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_invoices.ToArray());
            }
        }

        public Task<decimal> GetTotalAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_invoices.Sum(invoice => invoice.TotalAmount));
            }
        }
    }

    public sealed class OrderPlacedToInvoiceHandler(
        IBillingLedger ledger)
        : INotificationHandler<Order.OrderPlacedNotification>
    {
        public async Task Handle(Order.OrderPlacedNotification notification, CancellationToken cancellationToken)
        {
            await ledger.AddAsync(
                new Invoice(
                    Guid.NewGuid(),
                    notification.OrderId,
                    notification.CustomerId,
                    notification.Sku,
                    notification.Total),
                cancellationToken);
        }
    }

    public static class Queries
    {
        public sealed record GetBillingSummaryQuery() : IRequest<BillingSummaryDto>, ICacheableQuery
        {
            public string CacheKey => "modular:billing:summary";
            public TimeSpan? CacheDuration => TimeSpan.FromMinutes(3);
        }

        public sealed class GetBillingSummaryQueryHandler(
            IBillingLedger ledger)
            : IRequestHandler<GetBillingSummaryQuery, BillingSummaryDto>
        {
            public async Task<BillingSummaryDto> Handle(GetBillingSummaryQuery request, CancellationToken cancellationToken)
            {
                var all = await ledger.GetAllAsync(cancellationToken);
                var total = await ledger.GetTotalAsync(cancellationToken);
                return new BillingSummaryDto(all.Length, total);
            }
        }

        public sealed class GetBillingSummaryQueryValidator : IValidator<GetBillingSummaryQuery>
        {
            public Task<ValidationResult> ValidateAsync(GetBillingSummaryQuery instance, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ValidationResult());
            }
        }
    }

    public sealed record Invoice(Guid InvoiceId, string OrderId, string CustomerId, string Sku, decimal TotalAmount);
    public sealed record InvoiceVm(string InvoiceId, string OrderId, string CustomerId, string Sku, decimal TotalAmount);
    public sealed record BillingSummaryDto(int InvoiceCount, decimal TotalBilled);
}
