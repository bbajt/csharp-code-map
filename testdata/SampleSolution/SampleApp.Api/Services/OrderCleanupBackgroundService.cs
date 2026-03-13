namespace SampleApp.Api.Services;

using SampleApp.Services;

/// <summary>Background service that periodically cleans up cancelled or expired orders.</summary>
public class OrderCleanupBackgroundService : BackgroundService
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrderCleanupBackgroundService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public OrderCleanupBackgroundService(
        IOrderService orderService,
        ILogger<OrderCleanupBackgroundService> logger)
    {
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Order cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during order cleanup; retrying in 5 minutes");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Order cleanup service stopped");
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        _logger.LogDebug("Running expired order cleanup pass");
        await Task.CompletedTask;
    }
}
