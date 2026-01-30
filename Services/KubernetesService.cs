using k8s;
using k8s.Models;
using PodManager.API.Models;

namespace PodManager.API.Services;

    public class KubernetesService : IKubernetesService
    {
        private readonly IKubernetes _client;
        private const string Namespace = "default";

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
    }