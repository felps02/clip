using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

/// <summary>
/// Round 3 settings: focusCategories, customKeywords and instructions (validated, stored in project.settings and passed to
/// the worker), autoRenderTop defaulting to every candidate, and free plans that are never sold.
/// </summary>
public class AnalysisSettingsRound3Tests : TestBase
{
    public AnalysisSettingsRound3Tests(ApiFactory factory) : base(factory)
    {
    }

    private static async Task<HttpResponseMessage> CreateWithSettingsAsync(HttpClient client, Guid assetId, object settings)
        => await client.PostAsJsonAsync("/api/projects", new { title = "Live", source = new { kind = "upload", mediaAssetId = assetId }, settings }, Json);

    [Fact]
    public async Task Focus_keywords_and_instructions_are_stored_and_reach_the_worker()
    {
        var (client, _, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client);
        var response = await CreateWithSettingsAsync(client, assetId, new
        {
            focusCategories = new[] { "funny", "Hype", "funny" },
            customKeywords = new[] { "  clutch ", "Clutch", "que   isso" },
            instructions = "  quero os momentos em que eu grito  ",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var settings = body.GetProperty("project").GetProperty("settings");
        settings.GetProperty("focusCategories").EnumerateArray().Select(e => e.GetString()).ShouldBe(["funny", "hype"]);
        settings.GetProperty("customKeywords").EnumerateArray().Select(e => e.GetString()).ShouldBe(["clutch", "que isso"]);
        settings.GetProperty("instructions").GetString().ShouldBe("quero os momentos em que eu grito");

        // project.settings (jsonb) and the job input
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        var stored = await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.Settings).FirstAsync());
        stored.FocusCategories.ShouldBe(["funny", "hype"]);
        stored.CustomKeywords.ShouldBe(["clutch", "que isso"]);

        // the worker payload carries them in camelCase
        var payload = (await ClaimAsync(CreateInternalClient("worker-1"), "worker-1", "analyze_source")).ShouldNotBeNull();
        var payloadSettings = payload.GetProperty("settings");
        payloadSettings.GetProperty("focusCategories").EnumerateArray().Select(e => e.GetString()).ShouldBe(["funny", "hype"]);
        payloadSettings.GetProperty("customKeywords").EnumerateArray().Select(e => e.GetString()).ShouldBe(["clutch", "que isso"]);
        payloadSettings.GetProperty("instructions").GetString().ShouldBe("quero os momentos em que eu grito");
        payloadSettings.GetProperty("autoRenderTop").GetInt32().ShouldBe(15);
    }

    [Fact]
    public async Task Defaults_are_empty_and_every_candidate_becomes_a_clip()
    {
        var (client, _, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client);
        var response = await CreateWithSettingsAsync(client, assetId, new { maxCandidates = 12 });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var settings = (await ReadJsonAsync(response)).GetProperty("project").GetProperty("settings");
        settings.GetProperty("autoRenderTop").GetInt32().ShouldBe(12); // not sent → maxCandidates
        settings.GetProperty("focusCategories").GetArrayLength().ShouldBe(0);
        settings.GetProperty("customKeywords").GetArrayLength().ShouldBe(0);
        settings.GetProperty("instructions").ValueKind.ShouldBe(JsonValueKind.Null);

        // without settings: the profile's preferences, every candidate (free plan: 15) rendered
        var (other, _, _) = await RegisterAsync();
        var (projectId, _) = await CreateProjectAsync(other);
        var project = await ReadJsonAsync(await other.GetAsync($"/api/projects/{projectId}"));
        project.GetProperty("project").GetProperty("settings").GetProperty("autoRenderTop").GetInt32().ShouldBe(15);
    }

    [Fact]
    public async Task Invalid_focus_keywords_or_instructions_are_400()
    {
        var (client, _, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client);

        async Task ShouldFail(object settings, string field)
        {
            var response = await CreateWithSettingsAsync(client, assetId, settings);
            await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            var errors = (await ReadJsonAsync(response)).GetProperty("errors");
            errors.EnumerateObject().Select(p => p.Name).ShouldContain(name => name.StartsWith(field, StringComparison.Ordinal), errors.ToString());
        }

        await ShouldFail(new { focusCategories = new[] { "funny", "dancing" } }, "settings.focusCategories");
        await ShouldFail(new { customKeywords = Enumerable.Range(0, 21).Select(i => $"palavra{i}").ToArray() }, "settings.customKeywords");
        await ShouldFail(new { customKeywords = new[] { new string('x', 41) } }, "settings.customKeywords");
        await ShouldFail(new { customKeywords = new[] { "  " } }, "settings.customKeywords");
        await ShouldFail(new { instructions = new string('a', 501) }, "settings.instructions");
        (await WithDbAsync(db => db.Projects.CountAsync())).ShouldBe(0);

        // 20 distinct keywords (repeated spellings merged) and 500 characters are fine
        var ok = await CreateWithSettingsAsync(client, assetId, new
        {
            customKeywords = Enumerable.Range(0, 20).Select(i => $"palavra{i}").Append("PALAVRA0").ToArray(),
            instructions = new string('a', 500),
        });
        ok.StatusCode.ShouldBe(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reanalysis_validates_and_stores_the_new_fields()
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(CreateInternalClient("worker-1"), jobId);

        var invalid = await client.PostAsJsonAsync($"/api/projects/{projectId}/jobs", new { settings = new { focusCategories = new[] { "nope" } } }, Json);
        await ShouldBeProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");

        var created = await client.PostAsJsonAsync($"/api/projects/{projectId}/jobs", new { settings = new { focusCategories = new[] { "quote" }, customKeywords = new[] { "gg" } } }, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var settings = (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"))).GetProperty("project").GetProperty("settings");
        settings.GetProperty("focusCategories").EnumerateArray().Select(e => e.GetString()).ShouldBe(["quote"]);
        settings.GetProperty("customKeywords").EnumerateArray().Select(e => e.GetString()).ShouldBe(["gg"]);
    }

    [Fact]
    public async Task A_free_plan_is_never_purchasable_nor_sent_to_checkout()
    {
        var (client, _, _) = await RegisterAsync();
        Factory.Payments.Configured = true;
        // even with a provider price id configured by mistake
        await WithDbAsync(db => db.Plans.Where(p => p.Code == "free").ExecuteUpdateAsync(s => s.SetProperty(p => p.ProviderPriceId, "price_free")));
        try
        {
            var plans = await ReadJsonAsync(await client.GetAsync("/api/plans"));
            var free = plans.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "free");
            free.GetProperty("priceMonthlyCents").GetInt32().ShouldBe(0);
            free.GetProperty("purchasable").GetBoolean().ShouldBeFalse();

            await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "free" }, Json), HttpStatusCode.Conflict, "conflict");
            Factory.Payments.CheckoutRequests.ShouldBeEmpty();
        }
        finally
        {
            await WithDbAsync(db => db.Plans.Where(p => p.Code == "free").ExecuteUpdateAsync(s => s.SetProperty(p => p.ProviderPriceId, (string?)null)));
        }
    }
}
