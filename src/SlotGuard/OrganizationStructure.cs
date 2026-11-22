using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed class OrganizationUnit
{
    public Guid Id { get; init; }
    public required string OrganizationId { get; init; }
    public Guid? BranchId { get; init; }
    public required string Name { get; set; }
}

internal sealed class ResourceGroup
{
    public Guid Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Name { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? ReviewerId { get; set; }
}

internal static class OrganizationStructure
{
    public static void Configure(ModelBuilder model)
    {
        var units = model.Entity<OrganizationUnit>();
        units.Property(item => item.Name).HasMaxLength(100);
        units.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        units.HasOne<OrganizationUnit>().WithMany().HasForeignKey(item => new { item.OrganizationId, item.BranchId })
            .HasPrincipalKey(item => new { item.OrganizationId, item.Id }).OnDelete(DeleteBehavior.Restrict);

        var groups = model.Entity<ResourceGroup>();
        groups.Property(item => item.Name).HasMaxLength(100);
        groups.HasOne<Organization>().WithMany().HasForeignKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        groups.HasOne<OrganizationUnit>().WithMany().HasForeignKey(item => new { item.OrganizationId, item.UnitId })
            .HasPrincipalKey(item => new { item.OrganizationId, item.Id }).OnDelete(DeleteBehavior.Restrict);
        groups.HasOne<OrganizationMembership>().WithMany().HasForeignKey(item => new { item.ReviewerId, item.OrganizationId })
            .HasPrincipalKey(item => new { item.OwnerId, item.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }

    // Branch grants include direct departments. A group reviewer also needs an active reviewer role in this organization.
    private static IQueryable<ResourceGroup> GroupsVisibleTo(BookingDb db, Guid owner) =>
        db.ResourceGroups.Where(group => group.UnitId == null
            || db.Memberships.Any(member => member.OwnerId == owner && member.OrganizationId == group.OrganizationId
                && (member.UnitId == group.UnitId
                    || member.UnitId != null && db.OrganizationUnits.Any(unit => unit.Id == member.UnitId
                        && unit.OrganizationId == member.OrganizationId && unit.BranchId == group.UnitId)
                    || member.Role == OrganizationMembership.Reviewer && group.ReviewerId == owner
                    || member.Role == OrganizationMembership.Admin)));

    public static IQueryable<Resource> Visible(BookingDb db, IQueryable<Resource> resources, Guid owner) =>
        resources.Where(resource => resource.GroupId == null || GroupsVisibleTo(db, owner)
            .Any(group => group.Id == resource.GroupId && group.OrganizationId == resource.OrganizationId));

    public static IQueryable<Resource> Shareable(BookingDb db) =>
        db.Resources.Where(resource => resource.GroupId == null || db.ResourceGroups
            .Any(group => group.Id == resource.GroupId && group.OrganizationId == resource.OrganizationId && group.UnitId == null));

    public static Task<bool> AllowsLinks(BookingDb db, string id, CancellationToken ct) =>
        Shareable(db).AnyAsync(item => item.Id == id, ct);

    public static IResult LinksDisabled() =>
        Results.Problem(statusCode: 409, title: "У ресурса доступ по подразделению. Ссылки на бронирование недоступны.");

    public static IQueryable<Booking> Reviewable(BookingDb db, OrganizationScope scope)
    {
        var visible = scope.Resources(db);
        if (scope.IsAdmin)
            return db.Bookings.Where(booking => visible.Any(resource => resource.Id == booking.ResourceId));

        var owner = scope.OwnerId;
        var groups = db.ResourceGroups.Where(group => group.OrganizationId == scope.Current.Id && group.ReviewerId == owner
            && db.Memberships.Any(member => member.OwnerId == owner && member.OrganizationId == group.OrganizationId
                && member.Role == OrganizationMembership.Reviewer));
        return db.Bookings.Where(booking => booking.OwnerId != owner && booking.BundleId == null
            && visible.Any(resource => resource.Id == booking.ResourceId && (booking.RoutingReviewerId == owner
                || groups.Any(group => group.Id == resource.GroupId))));
    }

    public static Task<bool> CanApprove(BookingDb db, OrganizationScope scope, DateTimeOffset now, CancellationToken ct) =>
        scope.IsAdmin || scope.Current.Role == OrganizationMembership.Reviewer ? Task.FromResult(true)
            : BookingRouting.ReviewingOrganizations(db, scope.OwnerId, now).AnyAsync(id => id == scope.Current.Id, ct);
}
