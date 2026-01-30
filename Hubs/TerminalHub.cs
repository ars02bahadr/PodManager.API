using k8s;
using Microsoft.AspNetCore.SignalR;

namespace PodManager.API.Hubs;

public class TerminalHub : Hub
{
    public override Task OnConnectedAsync()
    {
        Console.WriteLine("Bağlandı: " + Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public async Task SendCommand(string podName, string command)
    {
        Console.WriteLine($"SendCommand - Pod: {podName}, Cmd: {command}");
        
        var namespaceName = "default";

        try
        {
            var config = KubernetesClientConfiguration.BuildDefaultConfig();
            var client = new Kubernetes(config);

            using var stdOut = new MemoryStream();
            using var stdErr = new MemoryStream();

            await client.NamespacedPodExecAsync(
                name: podName,
                @namespace: namespaceName,
                container: "main",
                command: new[] { "/bin/sh", "-c", command },
                tty: true,  // TTY açık
                action: async (stdin, stdout, stderr) =>
                {
                    await stdout.CopyToAsync(stdOut);
                    await stderr.CopyToAsync(stdErr);
                },
                CancellationToken.None
            );

            stdOut.Position = 0;
            stdErr.Position = 0;

            using var outReader = new StreamReader(stdOut);
            using var errReader = new StreamReader(stdErr);

            var output = await outReader.ReadToEndAsync();
            var error = await errReader.ReadToEndAsync();

            var result = output + error;
            if (string.IsNullOrEmpty(result))
            {
                result = "(komut çalıştı, çıktı yok)";
            }

            result = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
            
            await Clients.Caller.SendAsync("Output", result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hata: {ex.Message}");
            await Clients.Caller.SendAsync("Output", $"Hata: {ex.Message}\r\n");
        }
    }
}