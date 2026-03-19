using Application.Abstractions.OrderAggregate.Models;
using Domain.Entities;
using Domain.Exceptions;
using Domain.ValueModels;
using FluentAssertions;
using Moq;
using Shared.ValueModels;
using UnitTests.Builders;

namespace UnitTests.Services.OrderServiceTests;

public sealed class OrderService_CancelOrder_Tests
{
    [Fact]
    public async Task CancelOrder_ShouldThrowInvalidOperationException_WhenOrderAlreadyCanceled()
    {
        // Arrange
        var orderId = Guid.NewGuid();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid(),
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(model.OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Order()
            {
                Id = orderId,
                UserId = Guid.NewGuid(),
                Status = OrderStatus.Canceled,
                Price = 100
            });

        var orderService = builder.Build();

        //Act 
        Func<Task> act = () => orderService.CancelOrderAsync(model, CancellationToken.None);

        //Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CancelOrder_ShouldThrowInvalidOperationException_WhenOrderAlreadyCompleted()
    {
        // Arrange
        var orderId = Guid.NewGuid();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid(),
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(model.OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Order()
            {
                Id = orderId,
                UserId = Guid.NewGuid(),
                Status = OrderStatus.Completed,
                Price = 100
            });

        var orderService = builder.Build();

        //Act 
        Func<Task> act = () => orderService.CancelOrderAsync(model, CancellationToken.None);

        //Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CancelOrder_ShouldThrowEntityNotFoundException_WhenOrderNotFound()
    {
        // Arrange
        var orderId = Guid.NewGuid();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid(),
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(model.OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        var orderService = builder.Build();

        //Act 
        Func<Task> act = () => orderService.CancelOrderAsync(model, CancellationToken.None);

        //Assert
        var ex = await act.Should().ThrowAsync<EntityNotFoundException<Order>>();

        ex.Which.Message.Should().Contain(orderId.ToString());

        builder.OrderRepository.Verify(
            x => x.GetByIdAsync(model.OrderId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelOrder_ShouldUpdateOrderStatusAndWriteAudit_WhenModelIsValid()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var revertedByUserId = Guid.NewGuid();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = revertedByUserId,
        };

        var order = new Order()
        {
            Id = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            Price = 10,
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        builder.OrderTransactionsRepository
            .Setup(x => x.GetByOrderId(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrderTransaction>
            {
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction()
                {
                    Id = Guid.NewGuid(),
                    Amount = 10m,
                    Type = TransactionType.Pay,
                    UserId = userId
                }
            }
            });

        var sut = builder.Build();

        // Act
        var result = await sut.CancelOrderAsync(model, CancellationToken.None);

        // Assert
        result.Status.Should().Be(OrderStatus.Canceled);
        order.Status.Should().Be(OrderStatus.Canceled);

        builder.OrderRepository.Verify(
            x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()),
            Times.Once);

        builder.OrderRepository.Verify(
            x => x.UpdateAsync(
                It.Is<Order>(o => o.Id == orderId && o.Status == OrderStatus.Canceled),
                It.IsAny<CancellationToken>()),
            Times.Once);

        builder.InternalOrderAuditService.Verify(
            x => x.AddChangeOrderStatusAsync(
                revertedByUserId,
                It.Is<Order>(o => o.Id == orderId && o.Status == OrderStatus.Canceled),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelOrderAsync_Should_CreateRefundTransaction_WithFullPaidAmount()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = new Order()
        {
            Id = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            Price = 200,
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        builder.OrderTransactionsRepository
            .Setup(x => x.GetByOrderId(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrderTransaction>
            {
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction
                {
                    Id = Guid.NewGuid(),
                    Amount = 100m,
                    Type = TransactionType.Pay,
                    UserId = userId
                }
            }
            });

        Transaction? refundTransaction = null;

        builder.TransactionRepository
            .Setup(x => x.AddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, CancellationToken>((t, _) => refundTransaction = t)
            .Returns(Task.CompletedTask);

        var sut = builder.Build();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid()
        };

        // Act
        await sut.CancelOrderAsync(model, CancellationToken.None);

        // Assert
        refundTransaction.Should().NotBeNull();
        refundTransaction!.UserId.Should().Be(userId);
        refundTransaction.Amount.Should().Be(100m);
        refundTransaction.Type.Should().Be(TransactionType.Refund);
    }

    [Fact]
    public async Task CancelOrderAsync_Should_CreateRefundTransaction_WithNetAmount()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = new Order()
        {
            Id = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            Price = 200
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        builder.OrderTransactionsRepository
            .Setup(x => x.GetByOrderId(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrderTransaction>
            {
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction
                {
                    Id = Guid.NewGuid(),
                    Amount = 100m,
                    Type = TransactionType.Pay,
                    UserId = userId
                }
            },
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction
                {
                    Id = Guid.NewGuid(),
                    Amount = 30m,
                    Type = TransactionType.Refund,
                    UserId = userId
                }
            },
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction
                {
                    Id = Guid.NewGuid(),
                    Amount = 20m,
                    Type = TransactionType.Pay,
                    UserId = userId
                }
            }
            });

        Transaction? refundTransaction = null;

        builder.TransactionRepository
            .Setup(x => x.AddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, CancellationToken>((t, _) => refundTransaction = t)
            .Returns(Task.CompletedTask);

        var sut = builder.Build();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid()
        };

        // Act
        await sut.CancelOrderAsync(model, CancellationToken.None);

        // Assert
        refundTransaction.Should().NotBeNull();
        refundTransaction!.Amount.Should().Be(90m); // 100 - 30 + 20
        refundTransaction.Type.Should().Be(TransactionType.Refund);
    }

    [Fact]
    public async Task CancelOrder_ShouldThrowInvalidOperationException_WhenOrderHasNoRefundableFunds()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = new Order()
        {
            Id = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            Price = 100m
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        builder.OrderTransactionsRepository
            .Setup(x => x.GetByOrderId(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrderTransaction>
            {
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction()
                {
                    Id = Guid.NewGuid(),
                    Amount = 100m,
                    Type = TransactionType.Refund,
                    UserId = userId
                }
            }
            });

        var sut = builder.Build();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid()
        };

        // Act
        Func<Task> act = () => sut.CancelOrderAsync(model, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        builder.TransactionRepository.Verify(
            x => x.AddAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()),
            Times.Never);

        builder.OrderTransactionsRepository.Verify(
            x => x.AddAsync(It.IsAny<OrderTransaction>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelOrder_ShouldSetCanceledStatus_BeforeUpdatingOrder()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = new Order()
        {
            Id = orderId,
            UserId = userId,
            Status = OrderStatus.Created,
            Price = 100m
        };

        var builder = new OrderServiceBuilder();

        builder.OrderRepository
            .Setup(x => x.GetByIdAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);

        builder.OrderTransactionsRepository
            .Setup(x => x.GetByOrderId(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OrderTransaction>
            {
            new OrderTransaction()
            {
                OrderId = orderId,
                TransactionId = Guid.NewGuid(),
                Transaction = new Transaction()
                {
                    Id = Guid.NewGuid(),
                    Amount = 100m,
                    Type = TransactionType.Pay,
                    UserId = userId
                }
            }
            });

        var sut = builder.Build();

        var model = new CancelOrderModel()
        {
            OrderId = orderId,
            RevertedByUserId = Guid.NewGuid()
        };

        // Act
        await sut.CancelOrderAsync(model, CancellationToken.None);

        // Assert
        builder.OrderRepository.Verify(
            x => x.UpdateAsync(
                It.Is<Order>(o => o.Id == orderId && o.Status == OrderStatus.Canceled),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}