using System.Threading.Channels;
using k8s;
using Microsoft.AspNetCore.SignalR;
using PodManager.API.Services;

namespace PodManager.API.Hubs;

public class PodHub : Hub
{
    private readonly IKubernetesService _kubernetesService;
    private readonly ILogger<PodHub> _logger;
    private readonly IHubContext<PodHub> _hubContext;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _logStreams = new();

    public PodHub(IKubernetesService kubernetesService, ILogger<PodHub> logger, IHubContext<PodHub> hubContext)
    {
        _kubernetesService = kubernetesService;
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task SubscribeToPod(string podName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"pod-{podName}");
        _logger.LogInformation($"Client {Context.ConnectionId} subscribed to pod {podName}");
    }

    public async Task UnsubscribeFromPod(string podName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"pod-{podName}");
        _logger.LogInformation($"Client {Context.ConnectionId} unsubscribed from pod {podName}");
    }

    public async Task StartLogStream(string podName)
    {
        var key = $"{Context.ConnectionId}:{podName}";
        if (_logStreams.ContainsKey(key))
        {
            return; // Already streaming
        }

        var cts = new CancellationTokenSource();
        if (_logStreams.TryAdd(key, cts))
        {
            // Capture connectionId to avoid closure over mutable Context (though here it's passed by value effectively)
            var connectionId = Context.ConnectionId;
            _ = StreamLogsAsync(podName, connectionId, cts.Token);
            _logger.LogInformation($"Started log stream for {podName} (Client: {connectionId})");
        }
    }

    public Task StopLogStream(string podName)
    {
        var key = $"{Context.ConnectionId}:{podName}";
        if (_logStreams.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _logger.LogInformation($"Stopped log stream for {podName} (Client: {Context.ConnectionId})");
        }
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Cancel all streams for this connection
        var keys = _logStreams.Keys.Where(k => k.StartsWith($"{Context.ConnectionId}:")).ToList();
        foreach (var key in keys)
        {
            if (_logStreams.TryRemove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        return base.OnDisconnectedAsync(exception);
    }

    private async Task StreamLogsAsync(string podName, string connectionId, CancellationToken cancellationToken)
    {
        try
        {
            // Initial logs
            var logs = await _kubernetesService.GetPodLogsAsync(podName, 100);
            if (!string.IsNullOrEmpty(logs))
            {
                foreach (var line in logs.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        await _hubContext.Clients.Client(connectionId).SendAsync("PodLog", podName, new LogEntry
                        {
                            Timestamp = DateTime.UtcNow.ToString("O"),
                            Message = line
                        }, cancellationToken);

                        await Task.Delay(10, cancellationToken); // Throttling
                    }
                }
            }

            // Simulate stream
            while (!cancellationToken.IsCancellationRequested)
            {
                // In real implementation, watch k8s logs here.
                await Task.Delay(2000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error streaming logs for {podName}");
        }
    }
}

public class LogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
