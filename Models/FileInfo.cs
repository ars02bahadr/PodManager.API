namespace PodManager.API.Models;

public class FileInfo
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
    public DateTime? ModifiedAt { get; set; }
}
