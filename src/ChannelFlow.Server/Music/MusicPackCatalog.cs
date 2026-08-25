namespace FinTv.Music;

public sealed class MusicPackCatalogFile
{
    public List<MusicPackDefinition> Packs { get; set; } = [];
}

public sealed class MusicPackDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Season { get; set; } = "anytime";

    public string Version { get; set; } = "0.0.1";

    public string? GoogleDriveFileId { get; set; }
}

public sealed class MusicPackInstallRecord
{
    public string Id { get; set; } = "";

    public string Version { get; set; } = "";
}

public sealed class MusicPackStatus
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Season { get; set; } = "";

    public string PlaysWhen { get; set; } = "";

    public string CatalogVersion { get; set; } = "";

    public string? InstalledVersion { get; set; }

    public int TrackCount { get; set; }

    public string Status { get; set; } = "idle";

    public string? Error { get; set; }

    public bool HasDriveFile { get; set; }

    public bool IsActive { get; set; }
}

public static class MusicPackVersions
{
    public static int Compare(string? left, string? right)
    {
        if (Version.TryParse(Normalize(left), out var a) && Version.TryParse(Normalize(right), out var b))
        {
            return a.CompareTo(b);
        }

        return string.Compare(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        return text;
    }
}
