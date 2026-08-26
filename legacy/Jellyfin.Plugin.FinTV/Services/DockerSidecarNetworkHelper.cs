using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Resolves Docker network attachment for ChannelFlow sidecars (Playwright, WeatherStar) when Jellyfin runs in a container.
/// </summary>
public static class DockerSidecarNetworkHelper
{
    private static readonly Regex DockerCgroupIdRegex = new(
        @"(?:docker-|/docker/)([0-9a-f]{64})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShortContainerIdRegex = new(
        @"^[0-9a-f]{12,64}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool RunsInsideDocker() => File.Exists("/.dockerenv");

    public static bool UsesExplicitDockerNetwork()
        => !string.IsNullOrWhiteSpace(Jellyfin.Plugin.FinTV.AppEnvironment.Get("DOCKER_NETWORK"));

    public static async Task<DockerSidecarNetworkResolution> ResolveSidecarNetworkAsync(
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var overrideNetwork = Jellyfin.Plugin.FinTV.AppEnvironment.Get("DOCKER_NETWORK");
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
            logger.LogWarning(
                "Jellyfin is running in Docker but its container name could not be resolved. "
                + "Set CHANNELFLOW_JELLYFIN_CONTAINER (for example Jellyfin on Unraid) so WeatherStar and Playwright can share loopback.");
            return new DockerSidecarNetworkResolution();
        }

        logger.LogInformation(
            "Jellyfin container {ContainerRef} will share network namespace with ChannelFlow sidecar",
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
        var containerRef = Jellyfin.Plugin.FinTV.AppEnvironment.Get("JELLYFIN_CONTAINER");
        if (!string.IsNullOrWhiteSpace(containerRef))
        {
            return containerRef.Trim();
        }

        if (!RunsInsideDocker())
        {
            return null;
        }

        foreach (var candidate in await EnumerateSelfContainerRefsAsync(cancellationToken))
        {
            var name = await InspectContainerNameAsync(candidate, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
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
        if (UsesExplicitDockerNetwork() || !RunsInsideDocker())
        {
            return false;
        }

        var expectedRef = jellyfinContainerRef ?? await ResolveJellyfinContainerRefAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(expectedRef))
        {
            return false;
        }

        // Port-published sidecar on a bridge network cannot be reached on Jellyfin loopback.
        if (string.IsNullOrWhiteSpace(sidecarNetworkParent))
        {
            return true;
        }

        var expectedId = await ResolveContainerIdAsync(expectedRef, cancellationToken);
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

    private static async Task<List<string>> EnumerateSelfContainerRefsAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        if (File.Exists("/etc/hostname"))
        {
            var hostname = (await File.ReadAllTextAsync("/etc/hostname", cancellationToken)).Trim();
            if (!string.IsNullOrWhiteSpace(hostname))
            {
                candidates.Add(hostname);
            }
        }

        var cgroupId = TryReadContainerIdFromCgroup();
        if (!string.IsNullOrWhiteSpace(cgroupId))
        {
            candidates.Add(cgroupId);
            if (cgroupId.Length > 12)
            {
                candidates.Add(cgroupId[..12]);
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryReadContainerIdFromCgroup()
    {
        foreach (var path in new[] { "/proc/self/cgroup", "/proc/1/cgroup", "/proc/self/mountinfo" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path);
                var match = DockerCgroupIdRegex.Match(text);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (IOException)
            {
                // Ignore unreadable proc files.
            }
        }

        return null;
    }

    private static async Task<string?> InspectContainerNameAsync(string containerRef, CancellationToken cancellationToken)
    {
        var nameResult = await Cli.Wrap("docker")
            .WithArguments(["inspect", "-f", "{{.Name}}", containerRef])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (nameResult.ExitCode != 0)
        {
            return ShortContainerIdRegex.IsMatch(containerRef) ? containerRef : null;
        }

        var name = nameResult.StandardOutput.Trim().TrimStart('/');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>
/// Docker network attachment for a ChannelFlow sidecar container.
/// </summary>
public sealed class DockerSidecarNetworkResolution
{
    public string? Network { get; init; }

    public bool SharesJellyfinNetwork { get; init; }

    public string? JellyfinContainerRef { get; init; }
}
