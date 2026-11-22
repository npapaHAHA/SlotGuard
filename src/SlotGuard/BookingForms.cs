using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SlotGuard;

internal sealed record BookingQuestion(Guid Id, string Label, bool Required);
internal sealed record BookingAnswer(Guid QuestionId, string Label, string Value);
internal sealed record BookingFormSubmission(Guid Version, Dictionary<Guid, string?>? Answers);
internal sealed record BookingForm(Guid Version, BookingQuestion[] Questions)
{
    internal const int MaxQuestions = 8;
    internal const int MaxLabelLength = 160;
    internal const int MaxAnswerLength = 1000;
    public static readonly BookingForm Empty = new(Guid.Empty, []);

    public static BookingForm Read(Resource resource) => resource.BookingFormDefinition is { } json
        ? JsonSerializer.Deserialize<BookingForm>(json, JsonSerializerOptions.Web)! : Empty;

    public static BookingAnswer[] ReadAnswers(string? json) => json is null
        ? [] : JsonSerializer.Deserialize<BookingAnswer[]>(json, JsonSerializerOptions.Web)!;

    public IResult? Check(BookingFormSubmission? submission, out string? snapshot)
    {
        snapshot = null;
        if (submission is not null && submission.Version != Version)
            return Invalid("Вопросы изменились. Проверьте актуальную форму бронирования.");
        if (submission?.Answers is { } supplied
            && (supplied.Count > Questions.Length || supplied.Keys.Any(id => !Questions.Any(question => question.Id == id))))
            return Invalid("В форме есть ответы на вопросы, которых больше нет.");

        var answers = new List<BookingAnswer>();
        var errors = new Dictionary<Guid, string>();
        foreach (var question in Questions)
        {
            var value = submission?.Answers?.GetValueOrDefault(question.Id)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (question.Required)
                    errors.Add(question.Id, "Ответьте на обязательный вопрос.");
                continue;
            }
            if (value.Length > MaxAnswerLength || value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            {
                errors.Add(question.Id, $"Введите текст до {MaxAnswerLength} символов.");
                continue;
            }
            answers.Add(new BookingAnswer(question.Id, question.Label, value));
        }
        if (errors.Count > 0)
            return Invalid("Проверьте ответы на вопросы.", errors);
        if (answers.Count > 0)
            snapshot = JsonSerializer.Serialize(answers, JsonSerializerOptions.Web);
        return null;
    }

    private IResult Invalid(string title, Dictionary<Guid, string>? errors = null) =>
        Results.Problem(statusCode: 422, title: title, extensions: new Dictionary<string, object?>
        {
            ["bookingForm"] = this, ["questionErrors"] = errors
        });
}

internal static class BookingFormEndpoints
{
    private sealed record QuestionInput(Guid? Id, string? Label, bool Required);
    private sealed record EditInput(Guid Version, QuestionInput?[]? Questions);

    public static void MapBookingForms(this RouteGroupBuilder api)
    {
        api.MapGet("/resources/{id}/booking-form", Read);
        api.MapPut("/workspace/resources/{id}/booking-form", Save);
        api.MapGet("/bookings/{id:guid}/answers", ReadAnswers);
    }

    private static async Task<IResult> Read(string id, BookingDb db, OrganizationScope scope,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var resource = await scope.Resources(db).AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && !item.IsArchived, ct);
        return resource is null ? Results.NotFound() : Results.Ok(BookingForm.Read(resource));
    }

    private static async Task<IResult> Save(string id, EditInput input, BookingDb db, OrganizationScope scope,
        ClaimsPrincipal user, TimeProvider clock, CalendarUpdates updates, CancellationToken ct)
    {
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        if (input.Questions is not { Length: <= BookingForm.MaxQuestions })
            return Results.Problem(statusCode: 400, title: $"Добавьте до {BookingForm.MaxQuestions} вопросов.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resource = await WorkspaceEndpoints.LockEditableResource(db, scope, id, ct);
        if (resource is null || resource.IsArchived)
            return Results.NotFound();
        var previous = BookingForm.Read(resource);
        if (input.Version != previous.Version)
            return Results.Problem(statusCode: 409, title: "Форму уже изменили. Откройте редактор заново.");
        var existingIds = previous.Questions.Select(question => question.Id).ToHashSet();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questions = new List<BookingQuestion>();
        foreach (var question in input.Questions)
        {
            var label = question?.Label?.Trim();
            if (label is null || label.Length is 0 or > BookingForm.MaxLabelLength
                || label.Any(char.IsControl) || !labels.Add(label)
                || question!.Id is { } existing && !existingIds.Remove(existing))
                return Results.Problem(statusCode: 400, title: "Укажите разные непустые вопросы длиной до 160 символов.");
            questions.Add(new BookingQuestion(question.Id ?? Guid.NewGuid(), label, question.Required));
        }
        if (questions.SequenceEqual(previous.Questions))
            return Results.Ok(previous);

        var form = new BookingForm(Guid.NewGuid(), questions.ToArray());
        var json = JsonSerializer.Serialize(form, JsonSerializerOptions.Web);
        await scope.Resources(db).Where(item => item.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.BookingFormDefinition, json), ct);
        Audit.Add(db, resource, WorkspaceEndpoints.Actor(user), "resource.booking-form",
            "Обновлены вопросы для бронирования", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await updates.Publish(resource.OrganizationId);
        return Results.Ok(form);
    }

    private static async Task<IResult> ReadAnswers(Guid id, BookingDb db, OrganizationScope scope,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!scope.IsAdmin)
            return Results.StatusCode(403);
        var json = await (from booking in db.Bookings.AsNoTracking()
            join resource in scope.Resources(db) on booking.ResourceId equals resource.Id
            where booking.Id == id && booking.MaintenanceReason == null
            select booking.AnswersJson).SingleOrDefaultAsync(ct);
        return json is null ? Results.NotFound() : Results.Ok(BookingForm.ReadAnswers(json));
    }
}
