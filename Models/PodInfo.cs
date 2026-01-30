namespace PodManager.API.Models;

public class PodInfo
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    public string Status { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
    public string? PodIP { get; set; }
    public Dictionary<string, int> Ports { get; set; } = new();
    public int? NodePort { get; set; }
}