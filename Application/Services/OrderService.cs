using Application.Abstractions.DTOs.Entities;
using Application.Abstractions.OrderAggregate;
using Application.Abstractions.OrderAggregate.Models;
using Application.Mappers;
using Application.Services.Internal.OrderAudits;
using Application.Services.Internal.ProductQuantities;
using Domain.Abstractions;
using Domain.Entities;
using Domain.Exceptions;
using Domain.ValueModels;
using Shared.ValueModels;

namespace Application.Services;

public sealed class OrderService(IOrderRepository orderRepository,
    IProductSizeRepository productSizeRepository,
    IUserRepository userRepository,
    IInternalOrderAuditService internalOrderAuditService,
    IOrderTransactionsRepository orderTransactionsRepository,
    ITransactionRepository transactionRepository,
    IInternalProductQuantityService productQuantityService,
    IDatabaseTransactionManager transactionManager) : IOrderService
{
    public async Task<OrderDto> CreateOrderAsync(CreateOrderModel model, CancellationToken cancellationToken = default)
    {
        await EnsureUserIsValidAsync(model.UserId, cancellationToken);
        
        var price = await GetProductPriceAsync(model.ProductSizeId, cancellationToken);

        var usersAmount = await userRepository.GetCurrentScoresAmountAsync(model.UserId, cancellationToken);
        if (usersAmount < price) throw new InsufficientFundsException(model.UserId, price, usersAmount);

        var availableProductQuantity = await productQuantityService.GetAvailableQuantityAsync(model.ProductSizeId, cancellationToken);
        if (availableProductQuantity == 0) throw new InsufficientFundsException(model.UserId, price, usersAmount);
        
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = model.UserId,
            Price = price,
            Status = OrderStatus.Created
        };
        
        await using var transaction = await transactionManager.BeginTransactionAsync(cancellationToken);

        await orderRepository.CreateAsync(order, cancellationToken);
        await AddTransactionToOrderAsync(order.Id, model.UserId, price, TransactionType.Pay, cancellationToken);
        await internalOrderAuditService.AddChangeOrderStatusAsync(model.UserId, order, cancellationToken);
        await productQuantityService.ReduceQuantityForOrderAsync(order.Id, model.ProductSizeId, model.UserId, 1, cancellationToken);
        
        await transaction.CommitAsync(cancellationToken);

        return order.ToDto();
    }

    public async Task<OrderDto> CancelOrderAsync(
        CancelOrderModel cancelOrderModel,
        CancellationToken cancellationToken = default)
    {
        var orderId = cancelOrderModel.OrderId;
        var revertedByUserId = cancelOrderModel.RevertedByUserId;

        var order = await orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            throw new EntityNotFoundException<Order>(orderId);
        }

        var isAlreadyClosed =
            order.Status == OrderStatus.Canceled ||
            order.Status == OrderStatus.Completed;

        if (isAlreadyClosed)
        {
            throw new InvalidOperationException(
                $"Order {order.Id} is already {order.Status} and cannot be canceled.");
        }

        order.Status = OrderStatus.Canceled;

        await using var transaction = await transactionManager.BeginTransactionAsync(cancellationToken);

        await orderRepository.UpdateAsync(order, cancellationToken);
        await internalOrderAuditService.AddChangeOrderStatusAsync(revertedByUserId, order, cancellationToken);
        await RefundOrderTransactionAsync(order, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var orderDto = order.ToDto();
        return orderDto;
    }

    public async Task<OrderDto> GetOrderByIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            throw new EntityNotFoundException<Order>(orderId);
        }

        var orderDto = order.ToDto();
        return orderDto;
    }

    public async Task<IEnumerable<OrderDto>> GetOrdersByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureUserIsValidAsync(userId, cancellationToken);
        var orders = await orderRepository.GetByUserIdAsync(userId, cancellationToken);
        return orders.Select(order => order.ToDto());
    }

    private async Task EnsureUserIsValidAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var isExistedUser = await userRepository.AnyAsync(userId, cancellationToken);
        if (!isExistedUser) throw new EntityNotFoundException<User>(userId);
    }
    
    private async Task<Transaction> AddTransactionToOrderAsync(Guid orderId,
        Guid userId,
        decimal amount,
        TransactionType transactionType,
        CancellationToken cancellationToken = default)
    {
        var transaction = new Transaction()
        {
            UserId = userId,
            Amount = -Math.Abs(amount),
            Type = transactionType
        };
        
        await transactionRepository.AddAsync(transaction, cancellationToken);
        
        var orderTransaction = new OrderTransaction()
        {
            OrderId = orderId,
            TransactionId = transaction.Id,
        };
        
        await orderTransactionsRepository.AddAsync(orderTransaction, cancellationToken);
        
        return transaction;
    }

    private async Task<Transaction> RefundOrderTransactionAsync(Order order, CancellationToken cancellationToken = default)
    {
        var existingTransactions = await orderTransactionsRepository.GetByOrderId(order.Id, cancellationToken);

        decimal totalAmount = 0;
        var items = existingTransactions
            .Select(item => item.Transaction!);
        
        foreach (var transaction in items)
        {
            if (transaction.Type == TransactionType.Refund) totalAmount -= Math.Abs(transaction.Amount);
            else if (transaction.Type == TransactionType.Pay)  totalAmount += Math.Abs(transaction.Amount);
        }

        if (totalAmount <= 0)
        {
            throw new InvalidOperationException($"Order {order.Id} has no funds to revert.");
        }

        var refundTransaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = order.UserId,
            Amount = totalAmount,
            Type = TransactionType.Refund
        };

        await transactionRepository.AddAsync(refundTransaction, cancellationToken);
        //

        var orderTransactionLink = new OrderTransaction
        {
            OrderId = order.Id,
            TransactionId = refundTransaction.Id
        };

        await orderTransactionsRepository.AddAsync(orderTransactionLink, cancellationToken);

        return refundTransaction;
    }

    private async Task<decimal> GetProductPriceAsync(Guid productSizeId, CancellationToken cancellationToken = default)
    {
        var productSize =
            await productSizeRepository.GetProductSizeByIdAsync(productSizeId, cancellationToken) ??
            throw new EntityNotFoundException<ProductSize>(productSizeId);

        var price =
            productSize.Product?.Price ??
            throw new EntityNotFoundException<Product>(productSize.ProductId);
         
        return price;
    }
}