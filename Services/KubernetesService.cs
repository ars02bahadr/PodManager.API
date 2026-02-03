using k8s;
using k8s.Models;
using PodManager.API.Models;
using System.Text;

namespace PodManager.API.Services;

    public class KubernetesService : IKubernetesService
    {
        private readonly IKubernetes _client;
        private const string Namespace = "default";
        private const string DefaultWorkdir = "/";
        private const string ContainerName = "main";

        public KubernetesService()
        {
            var config = KubernetesClientConfiguration.BuildDefaultConfig();
            _client = new Kubernetes(config);
        }

        public async Task<List<PodInfo>> GetPodsAsync()
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: "app=user-pod");
            var services = await _client.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: "app=user-pod-service");

            var podInfos = new List<PodInfo>();
            foreach (var pod in pods.Items)
            {
                var service = services.Items.FirstOrDefault(s => s.Metadata.Name == GetServiceName(pod.Metadata.Name));
                podInfos.Add(MapToPodInfo(pod, service));
            }

            return podInfos;
        }

        public async Task<PodInfo?> GetPodAsync(string name)
        {
            try
            {
                var pod = await _client.CoreV1.ReadNamespacedPodAsync(name, Namespace);
                V1Service? service = null;
                try
                {
                    service = await _client.CoreV1.ReadNamespacedServiceAsync(GetServiceName(name), Namespace);
                }
                catch
                {
                    // Service might not exist
                }
                return MapToPodInfo(pod, service);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<string> GetPodLogsAsync(string name, int tailLines = 100)
        {
            try
            {
                var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(name, Namespace, tailLines: tailLines);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                return $"Error fetching logs: {ex.Message}";
            }
        }

        public async Task<UploadResponse> UploadFileAsync(string podName, string? directoryPath, string fileName, byte[] content, CancellationToken cancellationToken = default)
        {
            var safeDir = NormalizeDirectoryPath(directoryPath, DefaultWorkdir);
            var safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return new UploadResponse { Success = false, Error = "Geçersiz dosya adı." };
            }

            var targetPath = CombineUnixPath(safeDir, safeFileName);
            var base64 = Convert.ToBase64String(content);
            var command = new[] { "/bin/sh", "-c", $"base64 -d > {EscapeShellArg(targetPath)}" };

            var result = await ExecWithStdinAsync(podName, command, base64, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.stderr))
            {
                return new UploadResponse { Success = false, Error = result.stderr.Trim() };
            }

            return new UploadResponse { Success = true, FilePath = targetPath };
        }

        public async Task<(byte[] Content, string FileName)> DownloadFileAsync(string podName, string filePath, CancellationToken cancellationToken = default)
        {
            var safePath = NormalizeFilePath(filePath);
            var command = new[] { "/bin/sh", "-c", $"base64 {EscapeShellArg(safePath)}" };
            var result = await ExecAsync(podName, command, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.stderr))
            {
                throw new InvalidOperationException(result.stderr.Trim());
            }

            var content = Convert.FromBase64String(result.stdout);
            var fileName = Path.GetFileName(safePath);
            return (content, fileName);
        }

        public async Task<List<Models.FileInfo>> ListFilesAsync(string podName, string? directoryPath, CancellationToken cancellationToken = default)
        {
            var safeDir = NormalizeDirectoryPath(directoryPath, DefaultWorkdir);
            var command = new[] { "/bin/sh", "-c", $"LC_ALL=C ls -la --time-style=long-iso {EscapeShellArg(safeDir)}" };
            var result = await ExecAsync(podName, command, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.stderr))
            {
                throw new InvalidOperationException(result.stderr.Trim());
            }

            return ParseLsOutput(result.stdout);
        }

        public async Task<PodInfo> CreatePodAsync(CreatePodRequest request)
        {
            var sanitizedName = SanitizePodName(request.Name);
            var (image, command, args, port) = GetTemplate(request.Image);

            // 0. Check if exists and delete if so (Idempotent Create / Update)
            var existingPod = await GetPodAsync(sanitizedName);
            if (existingPod != null)
            {
                await DeletePodAsync(sanitizedName);
                // Simple wait for deletion to propagate
                for (int i = 0; i < 20; i++)
                {
                    if (await GetPodAsync(sanitizedName) == null) break;
                    await Task.Delay(500);
                }
            }

            // 1. Create Pod
            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = sanitizedName,
                    NamespaceProperty = Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        { "app", "user-pod" },
                        { "user-pod-name", sanitizedName }
                    }
                },
                Spec = new V1PodSpec
                {
                    Containers = new List<V1Container>
                    {
                        new V1Container
                        {
                            Name = "main",
                            Image = image,
                            Command = command,
                            Args = args,
                            Ports = new List<V1ContainerPort>
                            {
                                new V1ContainerPort { ContainerPort = port, Name = "main-port" }
                            }
                        }
                    },
                    RestartPolicy = "Never"
                }
            };

            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(pod, Namespace);

            // 2. Create Service
            var serviceName = GetServiceName(sanitizedName);
            // Ensure service is also cleaned up (DeletePodAsync handles this, but just in case of race)
            try
            {
                await _client.CoreV1.DeleteNamespacedServiceAsync(serviceName, Namespace);
            }
            catch { /* ignore */ }

            var service = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = serviceName,
                    NamespaceProperty = Namespace,
                    Labels = new Dictionary<string, string>
                    {
                         { "app", "user-pod-service" },
                         { "user-pod-name", sanitizedName }
                    }
                },
                Spec = new V1ServiceSpec
                {
                    Selector = new Dictionary<string, string>
                    {
                        { "user-pod-name", sanitizedName }
                    },
                    Ports = new List<V1ServicePort>
                    {
                        new V1ServicePort
                        {
                            Port = port,
                            TargetPort = port,
                            NodePort = null // Auto-assign
                        }
                    },
                    Type = "NodePort"
                }
            };

            var createdService = await _client.CoreV1.CreateNamespacedServiceAsync(service, Namespace);

            return MapToPodInfo(createdPod, createdService);
        }



        public async Task DeletePodAsync(string name)
        {
            try
            {
                await _client.CoreV1.DeleteNamespacedPodAsync(name, Namespace);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }

            try
            {
                await _client.CoreV1.DeleteNamespacedServiceAsync(GetServiceName(name), Namespace);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }

        private PodInfo MapToPodInfo(V1Pod pod, V1Service? service = null)
        {
            var info = new PodInfo
            {
                Name = pod.Metadata.Name,
                Namespace = pod.Metadata.NamespaceProperty,
                Status = pod.Status.Phase,
                Image = pod.Spec.Containers.FirstOrDefault()?.Image ?? "",
                CreatedAt = pod.Metadata.CreationTimestamp,
                PodIP = pod.Status.PodIP,
                Ports = pod.Spec.Containers
                    .SelectMany(c => c.Ports ?? new List<V1ContainerPort>())
                    .ToDictionary(p => p.Name ?? p.ContainerPort.ToString(), p => p.ContainerPort)
            };

            if (service != null && service.Spec.Ports.Count > 0)
            {
                info.NodePort = service.Spec.Ports[0].NodePort;
            }

            return info;
        }

        private string GetServiceName(string podName) => $"service-{podName}";

        private string SanitizePodName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "pod-" + Guid.NewGuid().ToString("N")[..8];

            // 1. Convert to lowercase
            name = name.ToLowerInvariant();

            // 2. Replace invalid characters (whitespace etc) with dash
            var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-z0-9]", "-");

            // 3. Remove consecutive dashes
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"-+", "-");

            // 4. Trim dashes
            sanitized = sanitized.Trim('-');

            if (string.IsNullOrWhiteSpace(sanitized)) return "pod-" + Guid.NewGuid().ToString("N")[..8];

            if (!char.IsLetterOrDigit(sanitized[0]))
                sanitized = "p" + sanitized;

            return sanitized;
        }

        private (string image, List<string>? command, List<string>? args, int port) GetTemplate(string imageKey)
{
    var jupyterArgs = new List<string>
    {
        "start-notebook.sh",
        "--NotebookApp.token=''",
        "--NotebookApp.password=''",
        "--NotebookApp.allow_origin='*'"
    };

    // Jupyter image kontrolü - contains ile
    if (imageKey.Contains("jupyter"))
    {
        var image = imageKey.Contains(":") ? imageKey : imageKey + ":latest";
        return (image, null, jupyterArgs, 8888);
    }

    // Ubuntu
    if (imageKey.Contains("ubuntu"))
    {
        var image = imageKey.Contains(":") ? imageKey : "ubuntu:22.04";
        return (image, new List<string> { "/bin/bash", "-c", "sleep infinity" }, null, 80);
    }

    // Default - bilinmeyen image
    return (imageKey, new List<string> { "/bin/bash", "-c", "sleep infinity" }, null, 8888);
}

        private async Task<(string stdout, string stderr)> ExecAsync(string podName, string[] command, CancellationToken cancellationToken)
        {
            var webSocket = await _client.WebSocketNamespacedPodExecAsync(
                podName,
                Namespace,
                container: ContainerName,
                command: command,
                tty: false,
                stdin: false,
                stdout: true,
                stderr: true,
                cancellationToken: cancellationToken
            );

            using var demux = new StreamDemuxer(webSocket);
            demux.Start();

            using var stdout = demux.GetStream(ChannelIndex.StdOut, ChannelIndex.StdOut);
            using var stderr = demux.GetStream(ChannelIndex.StdErr, ChannelIndex.StdErr);

            var stdoutText = await ReadStreamAsync(stdout, cancellationToken);
            var stderrText = await ReadStreamAsync(stderr, cancellationToken);
            return (stdoutText, stderrText);
        }

        private async Task<(string stdout, string stderr)> ExecWithStdinAsync(string podName, string[] command, string stdinPayload, CancellationToken cancellationToken)
        {
            var webSocket = await _client.WebSocketNamespacedPodExecAsync(
                podName,
                Namespace,
                container: ContainerName,
                command: command,
                tty: false,
                stdin: true,
                stdout: true,
                stderr: true,
                cancellationToken: cancellationToken
            );

            using var demux = new StreamDemuxer(webSocket);
            demux.Start();

            using var stdin = demux.GetStream(ChannelIndex.StdIn, ChannelIndex.StdIn);
            using var stdout = demux.GetStream(ChannelIndex.StdOut, ChannelIndex.StdOut);
            using var stderr = demux.GetStream(ChannelIndex.StdErr, ChannelIndex.StdErr);

            await WriteStreamAsync(stdin, stdinPayload, cancellationToken);

            var stdoutText = await ReadStreamWithTimeoutAsync(stdout, TimeSpan.FromSeconds(2), cancellationToken);
            var stderrText = await ReadStreamWithTimeoutAsync(stderr, TimeSpan.FromSeconds(2), cancellationToken);
            return (stdoutText, stderrText);
        }

        private static async Task<string> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        private static async Task<string> ReadStreamWithTimeoutAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var readTask = reader.ReadToEndAsync(cancellationToken);
            var delayTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(readTask, delayTask);
            if (completed == readTask)
            {
                return await readTask;
            }
            return string.Empty;
        }

        private static async Task WriteStreamAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Close();
        }

        private static string NormalizeDirectoryPath(string? path, string defaultPath)
        {
            var trimmed = string.IsNullOrWhiteSpace(path) ? defaultPath : path.Trim();
            if (!trimmed.StartsWith("/"))
            {
                throw new ArgumentException("Klasör yolu mutlak olmalı.");
            }
            if (trimmed.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException("Geçersiz klasör yolu.");
            }
            if (!trimmed.EndsWith("/"))
            {
                trimmed += "/";
            }
            return trimmed;
        }

        private static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Dosya yolu zorunludur.");
            }
            var trimmed = path.Trim();
            if (!trimmed.StartsWith("/"))
            {
                throw new ArgumentException("Dosya yolu mutlak olmalı.");
            }
            if (trimmed.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException("Geçersiz dosya yolu.");
            }
            return trimmed;
        }

        private static string CombineUnixPath(string directoryPath, string fileName)
        {
            var dir = directoryPath.EndsWith("/") ? directoryPath : directoryPath + "/";
            return $"{dir}{fileName}";
        }

        private static string EscapeShellArg(string value)
        {
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        private static List<Models.FileInfo> ParseLsOutput(string output)
        {
            var results = new List<Models.FileInfo>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("total ", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8)
                {
                    continue;
                }

                var mode = parts[0];
                var sizePart = parts[4];
                var datePart = parts[5];
                var timePart = parts[6];
                var name = string.Join(' ', parts.Skip(7));
                if (name == "." || name == "..")
                {
                    continue;
                }

                if (name.Contains(" -> ", StringComparison.Ordinal))
                {
                    name = name.Split(" -> ", 2, StringSplitOptions.None)[0];
                }

                var isDirectory = mode.StartsWith("d", StringComparison.Ordinal);
                var size = long.TryParse(sizePart, out var parsedSize) ? parsedSize : 0;
                var timestamp = $"{datePart} {timePart}";
                DateTime? modifiedAt = null;
                if (DateTime.TryParse(timestamp, out var parsedDate))
                {
                    modifiedAt = parsedDate;
                }

                results.Add(new Models.FileInfo
                {
                    Name = name,
                    Size = size,
                    IsDirectory = isDirectory,
                    ModifiedAt = modifiedAt
                });
            }

            return results;
        }
    }
