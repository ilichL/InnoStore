using Domain.Entities;
using Domain.Exceptions;
using FluentAssertions;
using Moq;
using Shared.ValueModels;
using UnitTests.Builders;

namespace UnitTests.Services.OrderServiceTests;

public sealed class OrderService_GetOrdersByUserId_Tests
{
    [Fact]
    public async Task GetOrdersByUserId_ShouldThrowEntityNotFoundException_WhenUserNotExist()
    {
        // Arrange
        var userId = Guid.NewGuid();

        var builder = new OrderServiceBuilder();

        builder.UserRepository
            .Setup(x => x.AnyAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var orderService = builder.Build();

        // Act
        Func<Task> act = () => orderService.GetOrdersByUserIdAsync(userId, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException<User>>();
    }

    [Fact]
    public async Task GetOrdersByUserId_ShouldReturnOrderDtos_WhenUserExists()
    {
        // Arrange
        var userId = Guid.NewGuid();

        var orders = new List<Order>
    {
        new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = OrderStatus.None,
            Price = 100
        },
        new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = OrderStatus.Canceled,
            Price = 250
        }
    };

        var builder = new OrderServiceBuilder();

        builder.UserRepository
            .Setup(x => x.AnyAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        builder.OrderRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orders);

        var sut = builder.Build();

        // Act
        var result = await sut.GetOrdersByUserIdAsync(userId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);

        result.Should().Contain(x => x.Id == orders[0].Id && x.Status == orders[0].Status);
        result.Should().Contain(x => x.Id == orders[1].Id && x.Status == orders[1].Status);
    }

    [Fact]
    public async Task GetOrdersByUserId_ShouldReturnEmptyCollection_WhenUserExistsAndHasNoOrders()
    {
        // Arrange
        var userId = Guid.NewGuid();

        var builder = new OrderServiceBuilder();

        builder.UserRepository
            .Setup(x => x.AnyAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        builder.OrderRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<Order>());

        var sut = builder.Build();

        // Act
        var result = await sut.GetOrdersByUserIdAsync(userId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }
}
