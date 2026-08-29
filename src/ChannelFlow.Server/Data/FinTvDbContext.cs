using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Data;

public class FinTvDbContext : DbContext
{
    public FinTvDbContext(DbContextOptions<FinTvDbContext> options)
        : base(options)
    {
    }

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<LogoSet> LogoSets => Set<LogoSet>();

    public DbSet<LogoSetEntry> LogoSetEntries => Set<LogoSetEntry>();

    public DbSet<Lineup> Lineups => Set<Lineup>();

    public DbSet<LineupOverride> LineupOverrides => Set<LineupOverride>();

    public DbSet<LineupSlot> LineupSlots => Set<LineupSlot>();

    public DbSet<SlotCandidate> SlotCandidates => Set<SlotCandidate>();

    public DbSet<PlayoutItem> PlayoutItems => Set<PlayoutItem>();

    public DbSet<PlayoutHistoryEntry> PlayoutHistory => Set<PlayoutHistoryEntry>();

    public DbSet<CommercialPreset> CommercialPresets => Set<CommercialPreset>();

    public DbSet<Commercial> Commercials => Set<Commercial>();

    public DbSet<CommercialChapter> CommercialChapters => Set<CommercialChapter>();

    public DbSet<FinTvList> FinTvLists => Set<FinTvList>();

    public DbSet<SpecialPresentation> SpecialPresentations => Set<SpecialPresentation>();

    public DbSet<SpecialPresentationCandidate> SpecialPresentationCandidates => Set<SpecialPresentationCandidate>();

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    public DbSet<MediaChapter> MediaChapters => Set<MediaChapter>();

    public DbSet<TvShowRow> TvShows => Set<TvShowRow>();

    public DbSet<EpisodeRow> Episodes => Set<EpisodeRow>();

    public DbSet<MovieRow> Movies => Set<MovieRow>();

    public DbSet<MusicRow> Music => Set<MusicRow>();

    public DbSet<MusicVideoRow> MusicVideos => Set<MusicVideoRow>();

    public DbSet<PastTenseNewsRow> PastTenseNews => Set<PastTenseNewsRow>();

    public DbSet<ChannelCatalogPoolItem> ChannelCatalogPool => Set<ChannelCatalogPoolItem>();

    public DbSet<ChannelPrimetimeSlot> ChannelPrimetimeSlots => Set<ChannelPrimetimeSlot>();

    public DbSet<ChannelPrimetimeCandidate> ChannelPrimetimeCandidates => Set<ChannelPrimetimeCandidate>();

    public DbSet<MusicVideoChannelArtist> MusicVideoChannelArtists => Set<MusicVideoChannelArtist>();

    public DbSet<MusicVideoYoutubeSource> MusicVideoYoutubeSources => Set<MusicVideoYoutubeSource>();

    public DbSet<PathMapping> PathMappings => Set<PathMapping>();

    public DbSet<MediaServerConnection> MediaServerConnections => Set<MediaServerConnection>();

    public DbSet<MediaServerLibrary> MediaServerLibraries => Set<MediaServerLibrary>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<AppSettingsRow> AppSettings => Set<AppSettingsRow>();

    public DbSet<NewsFeed> NewsFeeds => Set<NewsFeed>();

    public DbSet<NewsSettings> NewsSettings => Set<NewsSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.Property(e => e.Number).HasColumnType("numeric(8,1)");
            entity.HasIndex(e => e.Number).IsUnique();
            entity.HasOne(e => e.LogoSet).WithMany().HasForeignKey(e => e.LogoSetId);
            entity.HasOne(e => e.CommercialPreset).WithMany(e => e.Channels).HasForeignKey(e => e.CommercialPresetId);
            entity.HasOne(e => e.DefaultLineup).WithOne(e => e.Channel!).HasForeignKey<Lineup>(e => e.ChannelId);
            entity.Ignore(e => e.CommercialSearchPlaylistIds);
            entity.Ignore(e => e.IsContinuousLive);
        });

        modelBuilder.Entity<Lineup>(entity =>
        {
            entity.HasMany(e => e.Slots).WithOne(e => e.Lineup).HasForeignKey(e => e.LineupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LineupOverride>(entity =>
        {
            entity.HasMany(e => e.Slots).WithOne(e => e.LineupOverride).HasForeignKey(e => e.LineupOverrideId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LineupSlot>(entity =>
        {
            entity.HasIndex(e => new { e.LineupId, e.SlotIndex });
            entity.HasIndex(e => new { e.LineupOverrideId, e.SlotIndex });
            entity.HasMany(e => e.Candidates).WithOne(e => e.LineupSlot).HasForeignKey(e => e.LineupSlotId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Commercial>(entity =>
        {
            entity.HasIndex(e => e.JellyfinItemId);
            entity.HasIndex(e => e.CommercialBrainzVideoSbid).IsUnique();
            entity.HasMany(e => e.Chapters).WithOne(e => e.Commercial).HasForeignKey(e => e.CommercialId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlayoutItem>(entity =>
        {
            entity.HasIndex(e => new { e.ChannelId, e.Start, e.Finish });
            entity.HasIndex(e => e.CommercialId);
        });

        modelBuilder.Entity<PlayoutHistoryEntry>(entity =>
        {
            entity.HasIndex(e => new { e.ChannelId, e.AiredAt });
        });

        modelBuilder.Entity<LogoSetEntry>(entity =>
        {
            entity.HasIndex(e => new { e.LogoSetId, e.RelativePath });
        });

        modelBuilder.Entity<FinTvList>(entity =>
        {
            entity.HasIndex(e => e.JellyfinPlaylistId).IsUnique();
        });

        modelBuilder.Entity<SpecialPresentation>(entity =>
        {
            entity.HasIndex(e => new { e.ChannelId, e.DayOfWeek, e.SlotIndex });
            entity.HasMany(e => e.Candidates)
                .WithOne(e => e.SpecialPresentation)
                .HasForeignKey(e => e.SpecialPresentationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.HasIndex(e => e.Kind);
            entity.HasIndex(e => e.ParentId);
            entity.HasIndex(e => e.SeriesId);
            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => e.SourceConnectionId);
            entity.HasIndex(e => e.IsMissing);
            entity.HasIndex(e => e.AspectRatio);
            entity.HasIndex(e => e.TrueAspectRatio);
            entity.HasIndex(e => e.TrueAspectProbedAt);
            entity.HasIndex(e => e.FfprobeChaptersAt);
            entity.HasIndex(e => e.CommercialBreaksProbedAt);
            entity.HasMany(e => e.Chapters).WithOne(e => e.MediaItem).HasForeignKey(e => e.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PathMapping>(entity =>
        {
            entity.HasIndex(e => e.SortOrder);
            entity.HasIndex(e => e.ConnectionId);
        });

        modelBuilder.Entity<MediaServerConnection>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => e.Kind);
            entity.HasIndex(e => e.SortOrder);
            entity.HasMany(e => e.Libraries)
                .WithOne(e => e.Connection)
                .HasForeignKey(e => e.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MediaServerLibrary>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => new { e.ConnectionId, e.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasIndex(e => e.UserName).IsUnique();
        });

        modelBuilder.Entity<AppSettingsRow>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<NewsFeed>(entity =>
        {
            entity.HasIndex(e => e.SortOrder);
        });

        ConfigureCatalogTable<TvShowRow>(modelBuilder, "TvShows");
        ConfigureCatalogTable<EpisodeRow>(modelBuilder, "Episodes");
        modelBuilder.Entity<EpisodeRow>(entity =>
        {
            entity.HasIndex(e => e.SeriesId);
        });
        ConfigureCatalogTable<MovieRow>(modelBuilder, "Movies");
        ConfigureCatalogTable<MusicRow>(modelBuilder, "Music");
        ConfigureCatalogTable<MusicVideoRow>(modelBuilder, "MusicVideos");
        ConfigureCatalogTable<PastTenseNewsRow>(modelBuilder, "PastTenseNews");

        modelBuilder.Entity<ChannelCatalogPoolItem>(entity =>
        {
            entity.ToTable("ChannelCatalogPool");
            entity.HasIndex(e => new { e.ChannelId, e.JellyfinItemId }).IsUnique();
            entity.HasIndex(e => e.JellyfinItemId);
        });

        modelBuilder.Entity<ChannelPrimetimeSlot>(entity =>
        {
            entity.HasIndex(e => new { e.ChannelId, e.SlotIndex }).IsUnique();
            entity.HasMany(e => e.Candidates)
                .WithOne(e => e.Slot)
                .HasForeignKey(e => e.SlotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelPrimetimeCandidate>(entity =>
        {
            entity.HasIndex(e => e.SlotId);
            entity.HasIndex(e => e.SeriesId);
        });

        modelBuilder.Entity<MusicVideoChannelArtist>(entity =>
        {
            entity.HasIndex(e => new { e.ChannelId, e.ArtistName }).IsUnique();
        });

        modelBuilder.Entity<MusicVideoYoutubeSource>(entity =>
        {
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.ParentSourceId);
        });
    }

    private static void ConfigureCatalogTable<TEntity>(ModelBuilder modelBuilder, string tableName)
        where TEntity : CatalogMediaRow
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.LibraryId);
            entity.HasIndex(e => e.SourceConnectionId);
            entity.HasIndex(e => e.JellyfinItemId);
            entity.HasIndex(e => e.Path);
            entity.HasIndex(e => e.IsMissing);
        });
    }
}
