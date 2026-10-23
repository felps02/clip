using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

/// <summary>
/// notificationPreferences do something: in-app notifications are always created, the e-mail copy follows the user's
/// switches (job completed / failed, quota warnings, billing) and only goes to a confirmed address, and delivery never
/// fails the request.
/// </summary>
public class NotificationEmailTests : TestBase
{
    public NotificationEmailTests(ApiFactory factory) : base(factory)
    {
    }

    private const string ProjectLinkBase = "http://localhost:3000/projects/";

    [Fact]
    public async Task Job_completed_email_is_opt_in_and_links_the_project()
    {
        var (client, _, email) = await RegisterAsync(displayName: "Ana Streamer", confirmEmail: true);
        var worker = CreateInternalClient("worker-1");

        // default: emailOnJobCompleted = false → in-app only
        var (_, firstJob) = await CreateProjectAsync(client, "Primeira live");
        await CompleteAnalyzeJobAsync(worker, firstJob, 60);
        (await SentEmailsAsync(email)).ShouldNotContain(m => m.Subject.Contains("análise concluída"));
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.JobCompleted))).ShouldBe(1);

        await UpdateProfileAsync(client, new { notificationPreferences = new { emailOnJobCompleted = true } });
        var (projectId, jobId) = await CreateProjectAsync(client, "Final do campeonato");
        await CompleteAnalyzeJobAsync(worker, jobId, 60);

        var mail = (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("análise concluída")).ShouldHaveSingleItem();
        mail.Subject.ShouldBe("ClipForge: análise concluída – Final do campeonato");
        mail.TextBody.ShouldStartWith("Olá, Ana Streamer!");
        mail.TextBody.ShouldContain("A análise de \"Final do campeonato\" foi concluída");
        mail.TextBody.ShouldContain(ProjectLinkBase + projectId);
        mail.TextBody.ShouldContain("Análise concluída"); // the preference that caused it, in the footer
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.JobCompleted))).ShouldBe(2);
    }

    [Fact]
    public async Task Notification_emails_only_go_to_a_confirmed_address()
    {
        // Registration does not require confirmation, so the address may belong to someone else (a typo, or registered
        // on purpose): until its owner confirms it, the account's own text (project titles, display name) never leaves
        // by e-mail — only the in-app notifications are created.
        var (client, userId, email) = await RegisterAsync(displayName: "Equipe de Suporte");
        await UpdateProfileAsync(client, new { notificationPreferences = new { emailOnJobCompleted = true } });
        var worker = CreateInternalClient("worker-1");

        // an upload without video fails at the probe (nothing charged); another analysis completes
        var (_, failedJob) = await CreateProjectAsync(client, "Sua conta será bloqueada: acesse http://phishing.example");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{failedJob}/probe", new { durationSeconds = 60.0, hasAudio = true, hasVideo = false }, Json), HttpStatusCode.Conflict, "invalid_media");
        var (_, completedJob) = await CreateProjectAsync(client, "Live de ontem");
        await CompleteAnalyzeJobAsync(worker, completedJob, 60);

        (await SentEmailsAsync(email)).ShouldHaveSingleItem().Subject.ShouldContain("confirme seu e-mail"); // the verification e-mail only
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && (n.Type == NotificationType.JobFailed || n.Type == NotificationType.JobCompleted)))).ShouldBe(2);

        // the owner confirms the address: from then on the copies the preferences ask for go out
        await ConfirmEmailAsync(client, userId, email);
        var (_, laterJob) = await CreateProjectAsync(client, "Live de hoje");
        await CompleteAnalyzeJobAsync(worker, laterJob, 60);
        (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("análise concluída")).ShouldHaveSingleItem().Subject.ShouldContain("Live de hoje");
    }

    [Fact]
    public async Task Job_failed_emails_are_on_by_default_and_can_be_turned_off()
    {
        var (client, _, email) = await RegisterAsync(confirmEmail: true);
        var worker = CreateInternalClient("worker-1");

        var (projectId, jobId) = await CreateProjectAsync(client, "Live quebrada");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "invalid_media", message = "O arquivo está corrompido.", retryable = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var mail = (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("falha na análise")).ShouldHaveSingleItem();
        mail.Subject.ShouldContain("Live quebrada");
        mail.TextBody.ShouldContain("O arquivo está corrompido.");
        mail.TextBody.ShouldContain(ProjectLinkBase + projectId);

        // a retryable failure that is re-queued is not a failure yet: no e-mail
        var (_, retryJob) = await CreateProjectAsync(client, "Live com nova tentativa");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{retryJob}/fail", new { code = "worker_error", message = "boom", retryable = true }, Json))).GetProperty("status").GetString().ShouldBe("queued");
        (await SentEmailsAsync(email)).Count(m => m.Subject.Contains("falha na análise")).ShouldBe(1);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{retryJob}/fail", new { code = "invalid_media", message = "x", retryable = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SentEmailsAsync(email)).Count(m => m.Subject.Contains("falha na análise")).ShouldBe(2);

        // turned off: the in-app notification is still created, the e-mail is not sent
        await UpdateProfileAsync(client, new { notificationPreferences = new { emailOnJobFailed = false } });
        var (_, quietJob) = await CreateProjectAsync(client, "Live silenciosa");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        // a probe rejection fails the job server-side through the same path
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{quietJob}/probe", new { durationSeconds = 60.0, hasAudio = true, hasVideo = false }, Json), HttpStatusCode.Conflict, "invalid_media");
        (await SentEmailsAsync(email)).Count(m => m.Subject.Contains("falha na análise")).ShouldBe(2);
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.JobFailed))).ShouldBe(3);
    }

    [Fact]
    public async Task Render_failure_email_links_the_clip()
    {
        var (client, _, email) = await RegisterAsync(confirmEmail: true);
        var worker = CreateInternalClient("worker-1");
        var (projectId, jobId) = await CreateProjectAsync(client, "Live com clipe");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new[] { new { rank = 1, startSeconds = 10.0, endSeconds = 40.0, score = 80, category = "highlight", reasoning = "r", transcript = "t", signals = new { } } },
        }, Json);
        var candidateId = (await ReadJsonAsync(candidates)).GetProperty("ids")[0].GetGuid();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{candidateId}/clips", new { title = "Clipe do gol" }, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(created);
        var clipId = body.GetProperty("clip").GetProperty("id").GetGuid();
        var renderJob = body.GetProperty("job").GetProperty("id").GetGuid();
        (await ClaimAsync(worker, "worker-1", "render_clip")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{renderJob}/fail", new { code = "render_failed", message = "ffmpeg saiu com código 1", retryable = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var mail = (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("falha na renderização")).ShouldHaveSingleItem();
        mail.Subject.ShouldContain("Clipe do gol");
        mail.TextBody.ShouldContain($"http://localhost:3000/clips/{clipId}");
        mail.TextBody.ShouldContain("ffmpeg saiu com código 1");
    }

    [Fact]
    public async Task Quota_warnings_are_raised_once_per_threshold_and_period()
    {
        var (client, userId, email) = await RegisterAsync(confirmEmail: true);
        var worker = CreateInternalClient("worker-1");

        async Task<List<int>> ThresholdsAsync(Guid user)
        {
            var data = await WithDbAsync(db => db.Notifications.Where(n => n.UserId == user && n.Type == NotificationType.QuotaWarning).OrderBy(n => n.CreatedAt).Select(n => n.Data!).ToListAsync());
            return data.Select(d => JsonDocument.Parse(d).RootElement.GetProperty("threshold").GetInt32()).ToList();
        }

        // free plan: 60 minutes. 50 min → 83 % → the 80 % warning
        var (_, job1) = await CreateProjectAsync(client, "P1");
        await CompleteAnalyzeJobAsync(worker, job1, 50 * 60);
        (await ThresholdsAsync(userId)).ShouldBe([80]);
        var warning = await WithDbAsync(db => db.Notifications.SingleAsync(n => n.UserId == userId && n.Type == NotificationType.QuotaWarning));
        warning.Title.ShouldContain("80%");
        warning.Body!.ShouldContain("50 de 60 minutos");
        (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("80%")).ShouldHaveSingleItem().TextBody.ShouldContain("http://localhost:3000/billing");

        // 55 min → 91 %: already warned at 80 %
        var (_, job2) = await CreateProjectAsync(client, "P2");
        await CompleteAnalyzeJobAsync(worker, job2, 5 * 60);
        (await ThresholdsAsync(userId)).ShouldBe([80]);

        // 60 min → 100 %
        var (_, job3) = await CreateProjectAsync(client, "P3");
        await CompleteAnalyzeJobAsync(worker, job3, 5 * 60);
        (await ThresholdsAsync(userId)).ShouldBe([80, 100]);
        var emails = (await SentEmailsAsync(email)).Where(m => m.Subject.Contains("minutos de processamento")).ToList();
        emails.Count.ShouldBe(2);
        emails[1].Subject.ShouldContain("esgotados");

        // emailOnQuotaWarning = false: the notification is created, the e-mail is not; one jump straight to 100 % raises only the 100 % warning
        var (quietClient, quietUser, quietEmail) = await RegisterAsync(confirmEmail: true);
        await UpdateProfileAsync(quietClient, new { notificationPreferences = new { emailOnQuotaWarning = false } });
        var (_, quietJob) = await CreateProjectAsync(quietClient, "Q1");
        await CompleteAnalyzeJobAsync(worker, quietJob, 60 * 60);
        (await ThresholdsAsync(quietUser)).ShouldBe([100]);
        (await SentEmailsAsync(quietEmail)).ShouldNotContain(m => m.Subject.Contains("minutos de processamento"));
    }
}
