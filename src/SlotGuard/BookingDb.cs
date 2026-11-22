using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SlotGuard;

public sealed class BookingDb(DbContextOptions<BookingDb> options) : IdentityDbContext<Account, IdentityRole<Guid>, Guid>(options)
{
    public const string ActiveSlotIndex = "ux_bookings_active_slot";
    public const string ActiveWindowConstraint = "ex_bookings_active_window";

    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> Memberships => Set<OrganizationMembership>();
    internal DbSet<OrganizationUnit> OrganizationUnits => Set<OrganizationUnit>();
    internal DbSet<OrganizationBranding> Branding => Set<OrganizationBranding>();
    internal DbSet<ResourceGroup> ResourceGroups => Set<ResourceGroup>();
    public DbSet<HoldOutboxEvent> HoldOutbox => Set<HoldOutboxEvent>();
    public DbSet<HoldNotification> HoldNotifications => Set<HoldNotification>();
    public DbSet<WorkspaceActivity> WorkspaceActivities => Set<WorkspaceActivity>();
    internal DbSet<OrganizationInvitation> Invitations => Set<OrganizationInvitation>();
    internal DbSet<BookingEmail> BookingEmails => Set<BookingEmail>();
    internal DbSet<PrivateBookingLink> PrivateBookingLinks => Set<PrivateBookingLink>();
    internal DbSet<GoogleCalendarLink> GoogleCalendars => Set<GoogleCalendarLink>();
    internal DbSet<SeatReservation> SeatReservations => Set<SeatReservation>();
    internal DbSet<PublishedEvent> PublishedEvents => Set<PublishedEvent>();
    internal DbSet<EventMail> EventMails => Set<EventMail>();
    internal DbSet<ResourceTemplate> ResourceTemplates => Set<ResourceTemplate>();
    internal DbSet<BookingRouteRule> BookingRouteRules => Set<BookingRouteRule>();
    internal DbSet<ExternalApiKey> ExternalApiKeys => Set<ExternalApiKey>();
    internal DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    internal DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    internal DbSet<CalendarImportBatch> CalendarImports => Set<CalendarImportBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Account>().Property(account => account.FactorRecoveryHashes).HasDefaultValue(Array.Empty<string>());
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
        memberships.HasOne<OrganizationUnit>().WithMany()
            .HasForeignKey(item => new { item.OrganizationId, item.UnitId })
            .HasPrincipalKey(item => new { item.OrganizationId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        memberships.HasData(
            new OrganizationMembership(DemoAccounts.UniversityAdmin, Organization.University, OrganizationMembership.Admin),
            new OrganizationMembership(DemoAccounts.UniversityAdmin, Organization.Coworking, OrganizationMembership.Member),
            new OrganizationMembership(DemoAccounts.CoworkingAdmin, Organization.Coworking, OrganizationMembership.Admin));

        OrganizationStructure.Configure(modelBuilder);
        var branding = modelBuilder.Entity<OrganizationBranding>();
        branding.HasKey(item => item.OrganizationId);
        branding.HasOne<Organization>().WithOne().HasForeignKey<OrganizationBranding>(item => item.OrganizationId);
        branding.Property(item => item.Name).HasMaxLength(80);
        branding.Property(item => item.LogoUrl).HasMaxLength(512);
        branding.Property(item => item.BannerUrl).HasMaxLength(512);
        branding.Property(item => item.Palette).HasMaxLength(16);
        branding.Property(item => item.CompletionUrl).HasMaxLength(512);
        branding.Property(item => item.CompletionLabel).HasMaxLength(60);
        branding.Property(item => item.SenderName).HasMaxLength(80);
        branding.Property(item => item.ReplyTo).HasMaxLength(254);
        branding.Property(item => item.SmtpProfile).HasMaxLength(64);

        var apiKeys = modelBuilder.Entity<ExternalApiKey>();
        apiKeys.Property(item => item.OrganizationId).HasMaxLength(32);
        apiKeys.Property(item => item.Name).HasMaxLength(100);
        apiKeys.Property(item => item.TokenHash).HasMaxLength(64);
        apiKeys.Property(item => item.Prefix).HasMaxLength(16);
        apiKeys.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId);
        apiKeys.HasIndex(item => item.TokenHash).IsUnique();

        var subscriptions = modelBuilder.Entity<WebhookSubscription>();
        subscriptions.Property(item => item.OrganizationId).HasMaxLength(32);
        subscriptions.Property(item => item.Name).HasMaxLength(100);
        subscriptions.Property(item => item.Url).HasMaxLength(512);
        subscriptions.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId);

        var deliveries = modelBuilder.Entity<WebhookDelivery>();
        deliveries.Property(item => item.Type).HasMaxLength(32);
        deliveries.Property(item => item.LastError).HasMaxLength(32);
        deliveries.HasOne<WebhookSubscription>().WithMany().HasForeignKey(item => item.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);
        deliveries.HasIndex(item => item.NextAttemptAt).HasFilter("\"DeliveredAt\" IS NULL AND \"FailedAt\" IS NULL");
        deliveries.HasIndex(item => new { item.SubscriptionId, item.CreatedAt });

        var routing = modelBuilder.Entity<BookingRouteRule>();
        routing.HasKey(item => new { item.OrganizationId, item.Purpose });
        routing.Property(item => item.OrganizationId).HasMaxLength(32);
        routing.Property(item => item.Purpose).HasMaxLength(16);
        routing.Property(item => item.Type).HasMaxLength(16);
        routing.Property(item => item.Revision).IsConcurrencyToken();
        routing.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId);
        routing.HasIndex(item => item.ReviewerId);

        var templates = modelBuilder.Entity<ResourceTemplate>();
        templates.Property(item => item.OrganizationId).HasMaxLength(32);
        templates.Property(item => item.Name).HasMaxLength(100);
        templates.Property(item => item.NormalizedName).HasMaxLength(100);
        templates.Property(item => item.Definition).HasColumnType("jsonb");
        templates.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        templates.HasIndex(item => new { item.OrganizationId, item.NormalizedName }).IsUnique()
            .HasDatabaseName(ResourceTemplateEndpoints.NameIndex);

        var resources = modelBuilder.Entity<Resource>();
        resources.HasKey(resource => resource.Id);
        resources.Property(resource => resource.Id).HasMaxLength(32);
        resources.Property(resource => resource.PublicPhotoUrl).HasMaxLength(512);
        resources.Property(resource => resource.DiscoverCity).HasMaxLength(70);
        resources.Property(resource => resource.DiscoverFeatured).HasDefaultValue(false);
        resources.ToTable(table => table.HasCheckConstraint("ck_resources_price", "\"PriceMinor\" BETWEEN 100 AND 100000000"));
        resources.Property(resource => resource.ScheduleDefinition).HasColumnType("jsonb");
        resources.Property(resource => resource.BookingFormDefinition).HasColumnType("jsonb");
        resources.HasIndex(resource => resource.PublicLinkId).IsUnique();
        resources.Property(resource => resource.Type).HasMaxLength(16).HasDefaultValue(ResourceTypes.Other);
        resources.Property(resource => resource.Equipment).HasDefaultValue(Array.Empty<string>());
        resources.Property(resource => resource.EmbedOrigins).HasDefaultValue(Array.Empty<string>());
        resources.HasOne<Organization>().WithMany().HasForeignKey(resource => resource.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        resources.HasOne<ResourceTemplate>().WithMany()
            .HasForeignKey(resource => new { resource.OrganizationId, resource.TemplateId })
            .HasPrincipalKey(template => new { template.OrganizationId, template.Id })
            .OnDelete(DeleteBehavior.Restrict);
        resources.HasOne<ResourceGroup>().WithMany()
            .HasForeignKey(item => new { item.OrganizationId, item.GroupId })
            .HasPrincipalKey(item => new { item.OrganizationId, item.Id }).OnDelete(DeleteBehavior.Restrict);
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

        var links = modelBuilder.Entity<PrivateBookingLink>();
        links.Property(link => link.EmailDomain).HasMaxLength(253);
        links.ToTable(table => table.HasCheckConstraint("ck_private_booking_links_usage",
            "\"MaxUses\" BETWEEN 1 AND 1000 AND \"UsedCount\" BETWEEN 0 AND \"MaxUses\""));
        links.HasOne<Resource>().WithMany().HasForeignKey(link => link.ResourceId).OnDelete(DeleteBehavior.Restrict);

        var google = modelBuilder.Entity<GoogleCalendarLink>();
        google.HasKey(link => link.ResourceId);
        google.Property(link => link.BusyJson).HasColumnType("jsonb");
        google.HasIndex(link => link.CalendarId).IsUnique().HasFilter("\"ProtectedRefreshToken\" IS NOT NULL");
        google.HasOne<Resource>().WithOne().HasForeignKey<GoogleCalendarLink>(link => link.ResourceId);

        var bookings = modelBuilder.Entity<Booking>();
        var imports = modelBuilder.Entity<CalendarImportBatch>();
        imports.Property(item => item.Revision).HasMaxLength(97);
        imports.HasOne<Resource>().WithMany().HasForeignKey(item => item.ResourceId).OnDelete(DeleteBehavior.Restrict);
        imports.HasIndex(item => new { item.ResourceId, item.Revision }).IsUnique();
        bookings.HasOne<CalendarImportBatch>().WithMany().HasForeignKey(item => item.CalendarImportId).OnDelete(DeleteBehavior.Restrict);
        bookings.ToTable(table => table.HasCheckConstraint("ck_bookings_payment",
            """
            ("PriceMinor" IS NULL AND "PaidAt" IS NULL AND "PaymentRefundedAt" IS NULL)
            OR ("PriceMinor" IS NOT NULL AND "PriceMinor" BETWEEN 100 AND 100000000
                AND ("PaidAt" IS NOT NULL OR "HoldExpiresAt" IS NOT NULL)
                AND ("PaymentRefundedAt" IS NULL OR ("PaidAt" IS NOT NULL AND "CancelledAt" IS NOT NULL)))
            """));
        bookings.Property(booking => booking.MaintenanceReason).HasMaxLength(160);
        bookings.Property(booking => booking.SessionTitle).HasMaxLength(100);
        bookings.Property(booking => booking.SeatApprovalRequired).HasDefaultValue(false);
        bookings.Property(booking => booking.ApprovalStatus).HasMaxLength(16);
        bookings.Property(booking => booking.ApprovalReason).HasMaxLength(240);
        bookings.Property(booking => booking.AnswersJson).HasColumnType("jsonb");
        bookings.Property(booking => booking.RoutingContext).HasColumnType("jsonb");
        bookings.HasIndex(booking => booking.RoutingReviewerId)
            .HasFilter("\"RoutingReviewerId\" IS NOT NULL AND \"CancelledAt\" IS NULL AND \"ApprovalStatus\" = 'pending'");
        bookings.Property(booking => booking.ContactEmail).HasMaxLength(254);
        bookings.Property(booking => booking.CalendarSequence).HasDefaultValue(0);
        bookings.Property(booking => booking.GuestManagement).HasDefaultValue(false);
        bookings.HasIndex(booking => booking.CalendarRootId);
        bookings.HasIndex(booking => booking.BundleId).HasFilter("\"BundleId\" IS NOT NULL");
        bookings.HasOne<PrivateBookingLink>().WithMany().HasForeignKey(booking => booking.PrivateLinkId)
            .OnDelete(DeleteBehavior.Restrict);
        bookings.HasIndex(booking => new { booking.ResourceId, booking.OwnerId, booking.SeriesId })
            .HasFilter("\"SeriesId\" IS NOT NULL");
        bookings.HasOne<Resource>().WithMany().HasForeignKey(booking => booking.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        bookings.HasOne<Booking>().WithOne().HasForeignKey<Booking>(booking => booking.RescheduledFromId)
            .OnDelete(DeleteBehavior.Restrict);
        bookings.HasIndex(booking => booking.RescheduledFromId).IsUnique().HasDatabaseName("ux_bookings_rescheduled_from");
        bookings.HasIndex(booking => new { booking.ResourceId, booking.StartUtc })
            .HasDatabaseName(ActiveSlotIndex)
            .IsUnique()
            .HasFilter("\"CancelledAt\" IS NULL");
        bookings.HasIndex(booking => booking.HoldExpiresAt)
            .HasFilter("\"CancelledAt\" IS NULL AND \"HoldExpiresAt\" IS NOT NULL");
        bookings.HasIndex(booking => booking.StartUtc)
            .HasFilter("\"ApprovalStatus\" = 'pending' AND \"CancelledAt\" IS NULL");

        bookings.HasIndex(booking => booking.EndUtc).HasFilter("\"SeatCapacity\" IS NOT NULL");
        SeatReservation.Configure(modelBuilder.Entity<SeatReservation>());
        var events = modelBuilder.Entity<PublishedEvent>();
        events.HasKey(item => item.SessionId);
        events.Property(item => item.Description).HasMaxLength(1200);
        events.Property(item => item.City).HasMaxLength(70);
        events.HasOne<Booking>().WithOne().HasForeignKey<PublishedEvent>(item => item.SessionId);
        var eventMail = modelBuilder.Entity<EventMail>();
        eventMail.HasOne<SeatReservation>().WithMany().HasForeignKey(item => item.SeatId);
        eventMail.Property(item => item.Message).HasMaxLength(600);
        eventMail.HasIndex(item => item.NextAttemptAt).HasFilter("\"CompletedAt\" IS NULL");

        var bookingEmails = modelBuilder.Entity<BookingEmail>();
        bookingEmails.HasKey(email => new { email.BookingId, email.Kind });
        bookingEmails.HasOne<Booking>().WithMany().HasForeignKey(email => email.BookingId);
        bookingEmails.HasIndex(email => email.NextAttemptAt).HasFilter("\"CompletedAt\" IS NULL");

        var waitlist = modelBuilder.Entity<WaitlistEntry>();
        waitlist.Property(entry => entry.DurationMinutes).HasDefaultValue(Schedule.DefaultDuration);
        waitlist.HasOne<Resource>().WithMany().HasForeignKey(entry => entry.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        waitlist.HasIndex(entry => new { entry.ResourceId, entry.StartUtc, entry.Id })
            .HasFilter("\"RemovedAt\" IS NULL");
        waitlist.HasIndex(entry => new { entry.ResourceId, entry.StartUtc, entry.OwnerId, entry.DurationMinutes })
            .HasDatabaseName("ux_waitlist_active_request")
            .IsUnique()
            .HasFilter("\"RemovedAt\" IS NULL");

        waitlist.HasOne<Booking>().WithOne().HasForeignKey<WaitlistEntry>(entry => entry.OfferedBookingId)
            .OnDelete(DeleteBehavior.Restrict);
        waitlist.HasIndex(entry => entry.OfferedBookingId).IsUnique();

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
    public const string Reviewer = "reviewer";
    public Guid? UnitId { get; set; }
}

public sealed record Resource(string Id, string Name, string Location, int SortOrder, string OrganizationId,
    bool IsArchived = false)
{
    public string Type { get; init; } = ResourceTypes.Other;
    public int? Capacity { get; init; }
    public string[] Equipment { get; init; } = [];
    public bool RequiresApproval { get; init; }
    public Guid? TemplateId { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? PublicLinkId { get; init; }
    [JsonIgnore]
    public string[] EmbedOrigins { get; init; } = [];
    public string? PublicPhotoUrl { get; init; }
    public string? DiscoverCity { get; init; }
    public bool DiscoverFeatured { get; init; }
    public int? PriceMinor { get; init; }
    [JsonIgnore]
    public string? ScheduleDefinition { get; init; }
    [JsonIgnore]
    public string? BookingFormDefinition { get; init; }
    [NotMapped]
    public string TimeZone => Schedule.Read(this).ZoneId;
    [NotMapped]
    public bool FlexibleBooking => Schedule.Read(this).Rules.Flexible;
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
    public DateTimeOffset EndUtc { get; init; }
    public DateTimeOffset OccupiedFromUtc { get; init; }
    public DateTimeOffset OccupiedUntilUtc { get; init; }
    public Guid? RescheduledFromId { get; init; }
    public Guid? CalendarRootId { get; init; }
    public int CalendarSequence { get; init; }
    public string? ContactEmail { get; set; }
    public bool GuestManagement { get; init; }
    public Guid? SeriesId { get; set; }
    public Guid? BundleId { get; init; }
    [JsonIgnore]
    public Guid? PrivateLinkId { get; set; }
    internal BookingWindow Window => new(StartUtc, EndUtc, OccupiedFromUtc, OccupiedUntilUtc);
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? HoldExpiresAt { get; set; }
    public int? PriceMinor { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? PaymentRefundedAt { get; set; }
    public string? MaintenanceReason { get; set; }
    public Guid? CalendarImportId { get; set; }
    public string? SessionTitle { get; set; }
    public int? SeatCapacity { get; set; }
    public bool SeatApprovalRequired { get; set; }
    public string? ApprovalStatus { get; set; }
    public string? ApprovalReason { get; set; }
    [JsonIgnore]
    public string? AnswersJson { get; set; }
    [JsonIgnore]
    public string? RoutingContext { get; set; }
    [JsonIgnore]
    public Guid? RoutingReviewerId { get; set; }
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
    public int DurationMinutes { get; init; } = Schedule.DefaultDuration;
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? OfferedBookingId { get; set; }
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
