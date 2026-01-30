using k8s;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using PodManager.API.Hubs;

namespace PodManager.API.Services;

public class PodMonitorService : BackgroundService
{
    private readonly IHubContext<PodHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;
    private const string Namespace = "default";

    public PodMonitorService(IHubContext<PodHub> hubContext, IServiceProvider serviceProvider)
    {
        _hubContext = hubContext;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var kubernetesService = scope.ServiceProvider.GetRequiredService<IKubernetesService>();

                // İlk yüklemede ve periyodik olarak tüm listeyi gönder
                var pods = await kubernetesService.GetPodsAsync();
                await _hubContext.Clients.All.SendAsync("PodListUpdate", pods, stoppingToken);

                // Basit polling (Watch yerine daha stabil olması için şimdilik polling)
                // Watch implementasyonu karmaşık olabilir (timeout, disconnects vs.)
                await Task.Delay(2000, stoppingToken);
            }
            catch (Exception ex)
            {
                // Log error
                Console.WriteLine($"Pod monitoring error: {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
