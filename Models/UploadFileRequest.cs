using Microsoft.AspNetCore.Http;

namespace PodManager.API.Models;

public class UploadFileRequest
{
    public IFormFile? File { get; set; }
}
