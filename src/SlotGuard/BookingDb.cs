using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

public sealed class BookingDb(DbContextOptions<BookingDb> options) : DbContext(options)
{
    public const string ActiveSlotIndex = "ux_bookings_active_slot";

    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var resources = modelBuilder.Entity<Resource>();
        resources.HasKey(resource => resource.Id);
        resources.Property(resource => resource.Id).HasMaxLength(32);
        resources.HasData(
            new Resource("microscope", "Оптический микроскоп", "Лаборатория А · кабинет 204", 1),
            new Resource("electronics", "Паяльная станция", "Мастерская · кабинет 108", 2),
            new Resource("studio", "Студия записи", "Медиалаб · кабинет 312", 3));

        var bookings = modelBuilder.Entity<Booking>();
        bookings.HasOne<Resource>().WithMany().HasForeignKey(booking => booking.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        bookings.HasIndex(booking => new { booking.ResourceId, booking.StartUtc })
            .HasDatabaseName(ActiveSlotIndex)
            .IsUnique()
            .HasFilter("\"CancelledAt\" IS NULL");
    }
}

public sealed record Resource(string Id, string Name, string Location, int SortOrder);

public sealed class Booking
{
    public Guid Id { get; init; }
    public required string ResourceId { get; init; }
    public Guid OwnerId { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset? CancelledAt { get; private set; }
}
