using PodManager.API.Models;

namespace PodManager.API.Services;

public interface IKubernetesService
{
    Task<List<PodInfo>> GetPodsAsync();
    Task<PodInfo?> GetPodAsync(string name);
    Task<PodInfo> CreatePodAsync(CreatePodRequest request);
    Task DeletePodAsync(string name);
    Task<string> GetPodLogsAsync(string name, int tailLines = 100);
}