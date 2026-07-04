using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Data;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.Property(e => e.Number).HasColumnType("REAL");
            entity.HasIndex(e => e.Number).IsUnique();
            entity.HasOne(e => e.LogoSet).WithMany().HasForeignKey(e => e.LogoSetId);
            entity.HasOne(e => e.CommercialPreset).WithMany(e => e.Channels).HasForeignKey(e => e.CommercialPresetId);
            entity.HasOne(e => e.DefaultLineup).WithOne(e => e.Channel!).HasForeignKey<Lineup>(e => e.ChannelId);
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
    }
}
