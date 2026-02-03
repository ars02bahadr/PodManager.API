namespace PodManager.API.Models;

public class UploadResponse
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
}
