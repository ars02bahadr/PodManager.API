using Microsoft.AspNetCore.Mvc;
using PodManager.API.Models;
using PodManager.API.Services;

namespace PodManager.API.Controllers;

[ApiController]
[Route("api/pods/{podName}/files")]
public class FilesController : ControllerBase
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;
    private readonly IKubernetesService _kubernetesService;

    public FilesController(IKubernetesService kubernetesService)
    {
        _kubernetesService = kubernetesService;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<UploadResponse>> UploadFile(
        string podName,
        [FromQuery] string? path,
        [FromForm] UploadFileRequest? request,
        CancellationToken cancellationToken)
    {
        var file = request?.File;
        if (file == null || file.Length == 0)
        {
            return BadRequest(new UploadResponse { Success = false, Error = "Dosya seçilmedi." });
        }

        if (file.Length > MaxUploadBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new UploadResponse { Success = false, Error = "Dosya boyutu 10MB sınırını aşıyor." });
        }

        try
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);

            var response = await _kubernetesService.UploadFileAsync(podName, path, file.FileName, stream.ToArray(), cancellationToken);
            if (!response.Success)
            {
                return BadRequest(response);
            }

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new UploadResponse { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("download")]
    public async Task<IActionResult> DownloadFile(
        string podName,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _kubernetesService.DownloadFileAsync(podName, path, cancellationToken);
            return File(result.Content, "application/octet-stream", result.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("list")]
    public async Task<ActionResult<List<Models.FileInfo>>> ListFiles(
        string podName,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = await _kubernetesService.ListFilesAsync(podName, path, cancellationToken);
            return Ok(files);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
