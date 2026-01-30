namespace PodManager.API.Models;

public class CreatePodRequest
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = "ubuntu:22.04";
    public int JupyterPort { get; set; } = 8888;
}