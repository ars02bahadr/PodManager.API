using Microsoft.AspNetCore.Mvc;
using PodManager.API.Models;
using PodManager.API.Services;

namespace PodManager.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PodsController : ControllerBase
{
    private readonly IKubernetesService _kubernetesService;

    public PodsController(IKubernetesService kubernetesService)
    {
        _kubernetesService = kubernetesService;
    }

    [HttpGet]
    public async Task<ActionResult<List<PodInfo>>> GetPods()
    {
        var pods = await _kubernetesService.GetPodsAsync();
        return Ok(pods);
    }

    [HttpGet("{name}")]
    public async Task<ActionResult<PodInfo>> GetPod(string name)
    {
        var pod = await _kubernetesService.GetPodAsync(name);
        if (pod == null)
            return NotFound();

        return Ok(pod);
    }

    [HttpPost]
    public async Task<ActionResult<PodInfo>> CreatePod([FromBody] CreatePodRequest request)
    {
        var pod = await _kubernetesService.CreatePodAsync(request);
        return CreatedAtAction(nameof(GetPod), new { name = pod.Name }, pod);
    }


    [HttpDelete("{name}")]
    public async Task<ActionResult> DeletePod(string name)
    {
        await _kubernetesService.DeletePodAsync(name);
        return NoContent();
    }
    [HttpGet("{name}/logs")]
    public async Task<ActionResult<string>> GetPodLogs(string name, [FromQuery] int tailLines = 100)
    {
        var logs = await _kubernetesService.GetPodLogsAsync(name, tailLines);
        return Ok(logs);
    }
}