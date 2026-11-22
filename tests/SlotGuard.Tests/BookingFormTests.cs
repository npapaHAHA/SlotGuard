using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SlotGuard.Tests;

public sealed class BookingFormTests(SlotGuardFixture app) : IClassFixture<SlotGuardFixture>
{
    private static readonly DateTimeOffset Start = SlotGuardFixture.Now.AddHours(2.5);

    [Fact]
    public async Task GuestMustAnswerRequiredQuestionsAndOnlyTheSameOrganizationAdminSeesAnswers()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var foreign = await app.CreateDemo("coworking-admin");
        using var member = await app.CreateGuest();
        using var guest = app.CreateClient();
        var resource = await Create(admin, "Студия с формой", requiresApproval: true);
        var form = await Save(admin, resource, Guid.Empty, new Question(null, "Цель съёмки", true),
            new Question(null, "Дополнительное оборудование", false));
        var question = form.Questions[0];
        var publicPath = await Publish(admin, resource);
        var publicResource = await guest.GetFromJsonAsync<PublicBookingEndpoints.PublicResource>(publicPath);
        Assert.Equal(form.Version, publicResource!.BookingForm!.Version);
        Assert.Equal(form.Questions, publicResource.BookingForm.Questions);
        using var cannotEdit = await member.PutAsJsonAsync(FormEditor(resource), new { version = form.Version, questions = Array.Empty<Question>() });
        Assert.Equal(HttpStatusCode.Forbidden, cannotEdit.StatusCode);
        using var wrongOrganization = await foreign.PutAsJsonAsync(FormEditor(resource), new { version = form.Version, questions = Array.Empty<Question>() });
        Assert.Equal(HttpStatusCode.NotFound, wrongOrganization.StatusCode);

        using var empty = await GuestBook(guest, publicPath, Start, Submission(form, question.Id, "  "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        using var problem = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("questionErrors").TryGetProperty(question.Id.ToString(), out _));
        Assert.Empty((await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!);
        using var tooLong = await GuestBook(guest, publicPath, Start, Submission(form, question.Id, new string('x', 1001)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        using var unknown = await GuestBook(guest, publicPath, Start, Submission(form, Guid.NewGuid(), "Не тот вопрос"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        const string response = "Записываем интервью\nНужен тихий зал";
        using var created = await GuestBook(guest, publicPath, Start, Submission(form, question.Id, " " + response + " "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var booking = (await created.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        Assert.DoesNotContain(response, await created.Content.ReadAsStringAsync());
        var request = Assert.Single((await admin.GetFromJsonAsync<ApprovalEndpoints.ApprovalRequest[]>("/api/approvals"))!,
            item => item.Id == booking.Id);
        Assert.Equal(new BookingAnswer(question.Id, question.Label, response), Assert.Single(request.Answers!));
        var address = $"/api/bookings/{booking.Id}/answers";
        using var denied = await member.GetAsync(address);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var crossTenant = await foreign.GetAsync(address);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        using var guestReceipt = await guest.GetAsync($"{publicPath}/bookings/{booking.Id}");
        Assert.DoesNotContain("Записываем интервью", await guestReceipt.Content.ReadAsStringAsync());
        using var adminAnswers = await admin.GetAsync(address);
        Assert.True(adminAnswers.Headers.CacheControl?.NoStore);
        Assert.Equal(response, Assert.Single((await adminAnswers.Content.ReadFromJsonAsync<BookingAnswer[]>())!).Value);
    }

    [Fact]
    public async Task StaleFormCannotBookAndReschedulePreservesTheOriginalQuestionSnapshot()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var guest = app.CreateClient();
        var resource = await Create(admin, "Версии вопросов");
        var initial = await Save(admin, resource, Guid.Empty, new Question(null, "Состав команды", true));
        var question = initial.Questions.Single();
        var path = await Publish(admin, resource);
        using var first = await GuestBook(guest, path, Start, Submission(initial, question.Id, "Три человека"));
        var booking = (await first.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        var updated = await Save(admin, resource, initial.Version, new Question(question.Id, "Какой отдел?", true));
        Assert.NotEqual(initial.Version, updated.Version);
        using var concurrentEdit = await admin.PutAsJsonAsync(FormEditor(resource),
            new { version = initial.Version, questions = Array.Empty<Question>() });
        Assert.Equal(HttpStatusCode.Conflict, concurrentEdit.StatusCode);
        using var stale = await GuestBook(guest, path, Start.AddHours(2), Submission(initial, question.Id, "Старый текст"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        using var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(updated.Version, body.RootElement.GetProperty("bookingForm").GetProperty("version").GetGuid());

        using var moved = await guest.PostAsJsonAsync($"{path}/bookings/{booking.Id}/reschedule", new { startsAt = Start.AddHours(2) });
        Assert.Equal(HttpStatusCode.Created, moved.StatusCode);
        var successor = (await moved.Content.ReadFromJsonAsync<PublicBookingEndpoints.Receipt>())!;
        var answers = await admin.GetFromJsonAsync<BookingAnswer[]>($"/api/bookings/{successor.Id}/answers");
        Assert.Equal(new BookingAnswer(question.Id, "Состав команды", "Три человека"), Assert.Single(answers!));
    }

    [Fact]
    public async Task SeriesAndHoldConfirmationRequireAnswersAndPersistThem()
    {
        using var admin = await app.CreateDemo("university-admin");
        using var member = await app.CreateGuest();
        using var waiter = await app.CreateGuest();
        var id = await Create(admin, "Резерв и серия");
        var form = await Save(admin, id, Guid.Empty, new Question(null, "Номер проекта", true));
        var answer = Submission(form, form.Questions[0].Id, "P-102");
        var seriesAddress = $"/api/resources/{id}/series";
        var series = new WeeklySeriesInput(Start, 2, SeriesId: Guid.NewGuid());
        using var noSeriesAnswers = await member.PostAsJsonAsync(seriesAddress, series);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSeriesAnswers.StatusCode);
        using var booked = await member.PostAsJsonAsync(seriesAddress, series with { BookingForm = answer });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var created = (await booked.Content.ReadFromJsonAsync<CreatedSeries>())!;
        Assert.Equal(2, created.Bookings.Length);
        foreach (var item in created.Bookings)
            Assert.Equal("P-102", Assert.Single((await admin.GetFromJsonAsync<BookingAnswer[]>($"/api/bookings/{item.Id}/answers"))!).Value);

        using var joined = await waiter.PostAsJsonAsync("/api/waitlist", new { resourceId = id, startsAt = Start });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        using var cancelled = await member.DeleteAsync($"/api/bookings/{created.Bookings[0].Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var agenda = await waiter.GetFromJsonAsync<JsonElement>("/api/workspace/me");
        var reserve = Assert.Single(agenda.GetProperty("upcoming").EnumerateArray()).GetProperty("id").GetGuid();
        using var noHoldAnswers = await waiter.PostAsync($"/api/bookings/{reserve}/confirm", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noHoldAnswers.StatusCode);
        using var confirmed = await waiter.PostAsJsonAsync($"/api/bookings/{reserve}/confirm",
            Submission(form, form.Questions[0].Id, "P-103"));
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal("P-103", Assert.Single((await admin.GetFromJsonAsync<BookingAnswer[]>($"/api/bookings/{reserve}/answers"))!).Value);
    }

    private static BookingFormSubmission Submission(BookingForm form, Guid id, string? response) =>
        new(form.Version, new Dictionary<Guid, string?> { [id] = response });

    private static Task<HttpResponseMessage> GuestBook(HttpClient client, string path, DateTimeOffset startsAt, BookingFormSubmission payload) =>
        client.PostAsJsonAsync(path + "/bookings", new { startsAt, bookingForm = payload });

    private static string FormEditor(string id) => $"/api/workspace/resources/{id}/booking-form";

    private static async Task<BookingForm> Save(HttpClient admin, string id, Guid version, params Question[] questions)
    {
        using var response = await admin.PutAsJsonAsync(FormEditor(id), new { version, questions });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BookingForm>())!;
    }

    private static async Task<string> Publish(HttpClient admin, string id)
    {
        using var response = await admin.PutAsJsonAsync($"/api/workspace/resources/{id}/public-link", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<Link>())!;
        return $"/api/public/resources/{link.PublicLinkId}";
    }

    private static async Task<string> Create(HttpClient admin, string name, bool requiresApproval = false)
    {
        using var resource = await admin.PostAsJsonAsync("/api/workspace/resources", new { name, location = "Этаж 2", requiresApproval });
        Assert.Equal(HttpStatusCode.Created, resource.StatusCode);
        return (await resource.Content.ReadFromJsonAsync<Resource>())!.Id;
    }

    private sealed record Question(Guid? Id, string Label, bool Required);
    private sealed record Link(Guid PublicLinkId);
}
