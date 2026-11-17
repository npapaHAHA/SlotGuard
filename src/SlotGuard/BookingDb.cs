using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

public sealed class BookingDb(DbContextOptions<BookingDb> options) : IdentityDbContext<Account, IdentityRole<Guid>, Guid>(options)
{
    public const string ActiveSlotIndex = "ux_bookings_active_slot";

    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> Memberships => Set<OrganizationMembership>();
    public DbSet<HoldOutboxEvent> HoldOutbox => Set<HoldOutboxEvent>();
    public DbSet<HoldNotification> HoldNotifications => Set<HoldNotification>();
    public DbSet<WorkspaceActivity> WorkspaceActivities => Set<WorkspaceActivity>();
    internal DbSet<OrganizationInvitation> Invitations => Set<OrganizationInvitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        var organizations = modelBuilder.Entity<Organization>();
        organizations.HasKey(organization => organization.Id);
        organizations.Property(organization => organization.Id).HasMaxLength(32);
        organizations.HasData(
            new Organization(Organization.University, "Университет"),
            new Organization(Organization.Coworking, "Коворкинг"));

        var memberships = modelBuilder.Entity<OrganizationMembership>();
        memberships.HasKey(membership => new { membership.OwnerId, membership.OrganizationId });
        memberships.Property(membership => membership.Role).HasMaxLength(16);
        memberships.HasOne<Organization>().WithMany().HasForeignKey(membership => membership.OrganizationId);
        memberships.HasData(
            new OrganizationMembership(DemoAccounts.UniversityAdmin, Organization.University, OrganizationMembership.Admin),
            new OrganizationMembership(DemoAccounts.UniversityAdmin, Organization.Coworking, OrganizationMembership.Member),
            new OrganizationMembership(DemoAccounts.CoworkingAdmin, Organization.Coworking, OrganizationMembership.Admin));

        var resources = modelBuilder.Entity<Resource>();
        resources.HasKey(resource => resource.Id);
        resources.Property(resource => resource.Id).HasMaxLength(32);
        resources.Property(resource => resource.Type).HasMaxLength(16).HasDefaultValue(ResourceTypes.Other);
        resources.Property(resource => resource.Equipment).HasDefaultValue(Array.Empty<string>());
        resources.HasOne<Organization>().WithMany().HasForeignKey(resource => resource.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        resources.HasData(
            new Resource("microscope", "Оптический микроскоп", "Лаборатория А · кабинет 204", 1, Organization.University)
            {
                Type = ResourceTypes.Equipment, Capacity = 2, Equipment = ["камера"]
            },
            new Resource("electronics", "Паяльная станция", "Мастерская · кабинет 108", 2, Organization.University)
            {
                Type = ResourceTypes.Equipment, Capacity = 1, Equipment = ["вытяжка", "термофен"]
            },
            new Resource("studio", "Студия записи", "Медиалаб · кабинет 312", 3, Organization.University)
            {
                Type = ResourceTypes.Studio, Capacity = 4, Equipment = ["аудиоинтерфейс", "микрофон"]
            },
            new Resource("meeting-room", "Переговорная", "Коворкинг · кабинет 403", 1, Organization.Coworking)
            {
                Type = ResourceTypes.Room, Capacity = 8, Equipment = ["видеосвязь", "доска", "экран"]
            },
            new Resource("podcast-room", "Подкаст-студия", "Коворкинг · кабинет 405", 2, Organization.Coworking)
            {
                Type = ResourceTypes.Studio, Capacity = 4, Equipment = ["аудиоинтерфейс", "микрофон"]
            });

        var bookings = modelBuilder.Entity<Booking>();
        bookings.Property(booking => booking.MaintenanceReason).HasMaxLength(160);
        bookings.Property(booking => booking.ApprovalStatus).HasMaxLength(16);
        bookings.Property(booking => booking.ApprovalReason).HasMaxLength(240);
        bookings.HasOne<Resource>().WithMany().HasForeignKey(booking => booking.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        bookings.HasIndex(booking => new { booking.ResourceId, booking.StartUtc })
            .HasDatabaseName(ActiveSlotIndex)
            .IsUnique()
            .HasFilter("\"CancelledAt\" IS NULL");
        bookings.HasIndex(booking => booking.HoldExpiresAt)
            .HasFilter("\"CancelledAt\" IS NULL AND \"HoldExpiresAt\" IS NOT NULL");
        bookings.HasIndex(booking => booking.StartUtc)
            .HasFilter("\"ApprovalStatus\" = 'pending' AND \"CancelledAt\" IS NULL");

        var waitlist = modelBuilder.Entity<WaitlistEntry>();
        waitlist.HasOne<Resource>().WithMany().HasForeignKey(entry => entry.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        waitlist.HasIndex(entry => new { entry.ResourceId, entry.StartUtc, entry.Id })
            .HasFilter("\"RemovedAt\" IS NULL");
        waitlist.HasIndex(entry => new { entry.ResourceId, entry.StartUtc, entry.OwnerId })
            .IsUnique()
            .HasFilter("\"RemovedAt\" IS NULL");

        var outbox = modelBuilder.Entity<HoldOutboxEvent>();
        outbox.HasOne<Booking>().WithOne().HasForeignKey<HoldOutboxEvent>(message => message.BookingId)
            .OnDelete(DeleteBehavior.Restrict);
        outbox.HasIndex(message => message.NextAttemptAt)
            .HasFilter("\"PublishedAt\" IS NULL");

        var notifications = modelBuilder.Entity<HoldNotification>();
        notifications.HasKey(notification => notification.EventId);
        notifications.HasOne<Booking>().WithOne().HasForeignKey<HoldNotification>(notification => notification.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        var activities = modelBuilder.Entity<WorkspaceActivity>();
        activities.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId);
        activities.HasIndex(item => new { item.OrganizationId, item.Id });

        var invitations = modelBuilder.Entity<OrganizationInvitation>();
        invitations.Property(item => item.Email).HasMaxLength(254);
        invitations.Property(item => item.TokenHash).HasMaxLength(64);
        invitations.HasIndex(item => item.TokenHash).IsUnique();
        invitations.HasIndex(item => new { item.OrganizationId, item.ExpiresAt });
        invitations.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId);
    }
}

public sealed record Organization(string Id, string Name)
{
    public const string University = "university";
    public const string Coworking = "coworking";
}

public sealed record OrganizationMembership(Guid OwnerId, string OrganizationId, string Role)
{
    public const string Member = "member";
    public const string Admin = "admin";
}

public sealed record Resource(string Id, string Name, string Location, int SortOrder, string OrganizationId,
    bool IsArchived = false)
{
    public string Type { get; init; } = ResourceTypes.Other;
    public int? Capacity { get; init; }
    public string[] Equipment { get; init; } = [];
    public bool RequiresApproval { get; init; }
}

public sealed class WorkspaceActivity
{
    public long Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceName { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed class Booking
{
    public Guid Id { get; init; }
    public required string ResourceId { get; init; }
    public Guid OwnerId { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? HoldExpiresAt { get; set; }
    public string? MaintenanceReason { get; set; }
    public string? ApprovalStatus { get; set; }
    public string? ApprovalReason { get; set; }
}

internal static class BookingApproval
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public sealed class WaitlistEntry
{
    public long Id { get; init; }
    public required string ResourceId { get; init; }
    public Guid OwnerId { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class HoldOutboxEvent
{
    public Guid Id { get; init; }
    public Guid BookingId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class HoldNotification
{
    public Guid EventId { get; init; }
    public Guid BookingId { get; init; }
    public DateTimeOffset DeliveredAt { get; init; }
}
