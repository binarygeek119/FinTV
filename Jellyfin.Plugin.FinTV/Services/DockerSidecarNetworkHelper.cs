using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves Docker network attachment for FinTV sidecars (Playwright, WeatherStar) when Jellyfin runs in a container.
/// </summary>
public static class DockerSidecarNetworkHelper
{
    public static bool RunsInsideDocker() => File.Exists("/.dockerenv");

    public static async Task<DockerSidecarNetworkResolution> ResolveSidecarNetworkAsync(
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var overrideNetwork = Environment.GetEnvironmentVariable("FINTV_DOCKER_NETWORK");
        if (!string.IsNullOrWhiteSpace(overrideNetwork))
        {
            return new DockerSidecarNetworkResolution
            {
                Network = overrideNetwork.Trim(),
                SharesJellyfinNetwork = false
            };
        }

        if (!RunsInsideDocker())
        {
            return new DockerSidecarNetworkResolution();
        }

        var containerRef = await ResolveJellyfinContainerRefAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(containerRef))
        {
            return new DockerSidecarNetworkResolution();
        }

        logger.LogInformation(
            "Jellyfin container {ContainerRef} will share network namespace with FinTV sidecar",
            containerRef);

        return new DockerSidecarNetworkResolution
        {
            Network = $"container:{containerRef}",
            SharesJellyfinNetwork = true,
            JellyfinContainerRef = containerRef
        };
    }

    public static async Task<string?> ResolveJellyfinContainerRefAsync(CancellationToken cancellationToken = default)
    {
        var containerRef = Environment.GetEnvironmentVariable("FINTV_JELLYFIN_CONTAINER");
        if (!string.IsNullOrWhiteSpace(containerRef))
        {
            return containerRef.Trim();
        }

        if (!File.Exists("/etc/hostname"))
        {
            return null;
        }

        var hostname = (await File.ReadAllTextAsync("/etc/hostname", cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var nameResult = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.Name}}", hostname])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (nameResult.ExitCode == 0)
        {
            var name = nameResult.StandardOutput.Trim().TrimStart('/');
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return hostname;
    }

    public static async Task<string?> GetSidecarNetworkParentRefAsync(
        string containerName,
        CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.HostConfig.NetworkMode}}", containerName])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var mode = result.StandardOutput.Trim();
        const string prefix = "container:";
        if (!mode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = mode[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(parent) ? null : parent;
    }

    public static async Task<bool> IsStaleNetworkAttachmentAsync(
        string? jellyfinContainerRef,
        string? sidecarNetworkParent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jellyfinContainerRef)
            || string.IsNullOrWhiteSpace(sidecarNetworkParent))
        {
            return false;
        }

        var expectedId = await ResolveContainerIdAsync(jellyfinContainerRef, cancellationToken);
        var parentId = await ResolveContainerIdAsync(sidecarNetworkParent, cancellationToken);
        if (string.IsNullOrWhiteSpace(expectedId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parentId))
        {
            return true;
        }

        return !string.Equals(expectedId, parentId, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string?> ResolveContainerIdAsync(
        string containerRef,
        CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.Id}}", containerRef])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var id = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}

/// <summary>
/// Docker network attachment for a FinTV sidecar container.
/// </summary>
public sealed class DockerSidecarNetworkResolution
{
    public string? Network { get; init; }

    public bool SharesJellyfinNetwork { get; init; }

    public string? JellyfinContainerRef { get; init; }
}
