using PodManager.API.Models;

namespace PodManager.API.Services;

public interface IKubernetesService
{
    Task<List<PodInfo>> GetPodsAsync();
    Task<PodInfo?> GetPodAsync(string name);
    Task<PodInfo> CreatePodAsync(CreatePodRequest request);
    Task DeletePodAsync(string name);
    Task<string> GetPodLogsAsync(string name, int tailLines = 100);
    Task<UploadResponse> UploadFileAsync(string podName, string? directoryPath, string fileName, byte[] content, CancellationToken cancellationToken = default);
    Task<(byte[] Content, string FileName)> DownloadFileAsync(string podName, string filePath, CancellationToken cancellationToken = default);
    Task<List<Models.FileInfo>> ListFilesAsync(string podName, string? directoryPath, CancellationToken cancellationToken = default);
}
