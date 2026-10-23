using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipForge.Api.Features.Live;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClipForge.Api.Tests.Integration;

/// <summary>Live capture API (docs/LIVE_CAPTURE.md §2–3): device tokens, sessions, segments, live_segment jobs.</summary>
public class LiveCaptureTests : TestBase
{
    private const string WorkerId = "live-worker";

    public LiveCaptureTests(ApiFactory factory) : base(factory)
    {
    }

    // ---------------------------------------------------------------- devices and authentication

    [Fact]
    public async Task Device_tokens_are_created_listed_and_revoked_and_revoked_tokens_get_401()
    {
        var (client, userId, _) = await RegisterAsync(displayName: "Ana Streamer");

        var create = await client.PostAsJsonAsync("/api/live/companion/tokens", new { name = "  PC do estúdio  " }, Json);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await ReadJsonAsync(create);
        var token = created.GetProperty("token").GetString()!;
        token.ShouldStartWith("cfd_");
        token.Length.ShouldBe(44);
        token[4..].All(char.IsAsciiLetterOrDigit).ShouldBeTrue();
        var device = created.GetProperty("device");
        var deviceId = device.GetProperty("id").GetGuid();
        device.GetProperty("name").GetString().ShouldBe("PC do estúdio");
        device.GetProperty("tokenPrefix").GetString().ShouldBe(token[4..12]);
        device.GetProperty("online").GetBoolean().ShouldBeFalse();
        device.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        // Only the SHA-256 hex and the display prefix are stored.
        var stored = await WithDbAsync(db => db.CompanionDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId));
        stored.TokenHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        stored.TokenHash.ShouldNotContain(token[4..]);
        stored.UserId.ShouldBe(userId);

        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/live/companion/tokens", new { name = " " }, Json), HttpStatusCode.BadRequest, "validation_failed");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/live/companion/tokens", new { name = new string('x', 61) }, Json), HttpStatusCode.BadRequest, "validation_failed");

        var companion = DeviceClient(token);
        var me = await companion.GetAsync("/api/live/companion/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync());
        var meBody = await ReadJsonAsync(me);
        meBody.GetProperty("device").GetProperty("id").GetGuid().ShouldBe(deviceId);
        meBody.GetProperty("device").GetProperty("name").GetString().ShouldBe("PC do estúdio");
        meBody.GetProperty("user").GetProperty("displayName").GetString().ShouldBe("Ana Streamer");
        meBody.GetProperty("plan").GetProperty("code").GetString().ShouldBe("free");
        meBody.GetProperty("plan").GetProperty("name").GetString().ShouldBe("Gratuito");

        // The request was seen: lastSeenAt is set and the device is online.
        var second = await CreateDeviceAsync(client, "Notebook");
        var devices = (await ReadJsonAsync(await client.GetAsync("/api/live/companion/devices"))).GetProperty("items").EnumerateArray().ToList();
        devices.Count.ShouldBe(2);
        var first = devices.Single(d => d.GetProperty("id").GetGuid() == deviceId);
        first.GetProperty("lastSeenAt").ValueKind.ShouldBe(JsonValueKind.String);
        first.GetProperty("online").GetBoolean().ShouldBeTrue();

        // Revoking: 204, idempotent, the token stops working, revoked devices are listed last.
        (await client.DeleteAsync($"/api/live/companion/devices/{second.DeviceId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/live/companion/devices/{second.DeviceId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/live/companion/devices/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await DeviceClient(second.Token).GetAsync("/api/live/companion/me"), HttpStatusCode.Unauthorized, "unauthorized");
        devices = (await ReadJsonAsync(await client.GetAsync("/api/live/companion/devices"))).GetProperty("items").EnumerateArray().ToList();
        devices.Select(d => d.GetProperty("id").GetGuid()).ShouldBe([deviceId, second.DeviceId]);
        devices[1].GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.String);
        devices[1].GetProperty("online").GetBoolean().ShouldBeFalse();

        // Unknown or malformed device tokens.
        await ShouldBeProblemAsync(await DeviceClient("cfd_" + new string('a', 40)).GetAsync("/api/live/companion/me"), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await DeviceClient("cfd_short").GetAsync("/api/live/companion/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Companion_endpoints_accept_only_device_tokens_and_user_endpoints_only_user_auth()
    {
        var email = NextEmail();
        var (client, _, _) = await RegisterAsync(email);
        var (_, token) = await CreateDeviceAsync(client);
        var companion = DeviceClient(token);

        // The user's cookie and the user's JWT are refused on companion endpoints.
        await ShouldBeProblemAsync(await client.GetAsync("/api/live/companion/me"), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/live/sessions", new { title = "x" }, Json), HttpStatusCode.Unauthorized, "unauthorized");
        var login = await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jwt = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("cf_access=")).Split(';')[0]["cf_access=".Length..];
        var bearer = CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        (await bearer.GetAsync("/api/live/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeProblemAsync(await bearer.GetAsync("/api/live/companion/me"), HttpStatusCode.Unauthorized, "unauthorized");

        // The device token is refused on user endpoints (live and elsewhere).
        await ShouldBeProblemAsync(await companion.GetAsync("/api/live/sessions"), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await companion.GetAsync("/api/live/companion/devices"), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await companion.PostAsJsonAsync("/api/live/companion/tokens", new { name = "outro" }, Json), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await companion.GetAsync("/api/projects"), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await companion.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "unauthorized");
        (await companion.GetAsync("/api/live/companion/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sessions_of_another_user_are_not_found()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var session = await CreateSessionAsync(a.Device);
        var sessionId = session.GetProperty("id").GetGuid();

        await ShouldBeProblemAsync(await b.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/heartbeat", new { state = State(recording: true) }, Json), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await UploadSegmentAsync(b.Device, sessionId, 0, 0, 60), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await b.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/end", new { }, Json), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await b.User.GetAsync($"/api/live/sessions/{sessionId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await b.User.PostAsync($"/api/live/sessions/{sessionId}/stop", null), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await b.User.DeleteAsync($"/api/live/companion/devices/{a.DeviceId}"), HttpStatusCode.NotFound, "not_found");
        (await ReadJsonAsync(await b.User.GetAsync("/api/live/sessions"))).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await ReadJsonAsync(await b.User.GetAsync("/api/live/companion/devices"))).GetProperty("items").GetArrayLength().ShouldBe(1);

        // Nothing changed for the owner.
        var owner = await ReadJsonAsync(await a.User.GetAsync($"/api/live/sessions/{sessionId}"));
        owner.GetProperty("session").GetProperty("status").GetString().ShouldBe("waiting");
        owner.GetProperty("session").GetProperty("stopRequested").GetBoolean().ShouldBeFalse();
    }

    // ---------------------------------------------------------------- session lifecycle

    [Fact]
    public async Task Session_goes_from_waiting_to_live_to_ending_to_ended()
    {
        var s = await SetupAsync();
        await ShouldBeProblemAsync(await s.Device.PostAsJsonAsync("/api/live/sessions", new { segmentSeconds = 10 }, Json), HttpStatusCode.BadRequest, "validation_failed");
        await ShouldBeProblemAsync(await s.Device.PostAsJsonAsync("/api/live/sessions", new { settings = new { sensitivity = 500 } }, Json), HttpStatusCode.BadRequest, "validation_failed");

        var create = await s.Device.PostAsJsonAsync("/api/live/sessions", new
        {
            title = "Live de sexta",
            segmentSeconds = 30,
            settings = new { maxClipSeconds = 40, subtitles = new { position = "top" } },
            obs = new { version = "31.0.2", recordingPath = "D:/OBS/live.mkv" },
        }, Json);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await ReadJsonAsync(create);
        var session = created.GetProperty("session");
        var sessionId = session.GetProperty("id").GetGuid();
        var projectId = session.GetProperty("projectId").GetGuid();
        session.GetProperty("status").GetString().ShouldBe("waiting");
        session.GetProperty("title").GetString().ShouldBe("Live de sexta");
        session.GetProperty("segmentSeconds").GetInt32().ShouldBe(30);
        session.GetProperty("deviceId").GetGuid().ShouldBe(s.DeviceId);
        session.GetProperty("deviceName").GetString().ShouldBe("PC do estúdio");
        session.GetProperty("online").GetBoolean().ShouldBeFalse();
        session.GetProperty("stats").GetProperty("segmentsReceived").GetInt32().ShouldBe(0);
        // Partial settings over the profile defaults, capped by the plan (free: subtitles at the bottom).
        created.GetProperty("settings").GetProperty("maxClipSeconds").GetInt32().ShouldBe(40);
        created.GetProperty("settings").GetProperty("subtitles").GetProperty("position").GetString().ShouldBe("bottom");
        created.GetProperty("settings").GetProperty("subtitles").GetProperty("enabled").GetBoolean().ShouldBeTrue();

        var project = (await ReadJsonAsync(await s.User.GetAsync($"/api/projects/{projectId}"))).GetProperty("project");
        project.GetProperty("sourceKind").GetString().ShouldBe("live");
        project.GetProperty("sourcePlatform").GetString().ShouldBe("obs");
        project.GetProperty("title").GetString().ShouldBe("Live de sexta");
        project.GetProperty("status").GetString().ShouldBe("processing");

        // A second session for the same device is refused while this one is not terminal.
        await ShouldBeProblemAsync(await s.Device.PostAsJsonAsync("/api/live/sessions", new { }, Json), HttpStatusCode.Conflict, "plan_limit");

        var heartbeat = await ReadJsonAsync(await Heartbeat(s.Device, sessionId, State(recording: true)));
        heartbeat.GetProperty("status").GetString().ShouldBe("live");
        heartbeat.GetProperty("stopRequested").GetBoolean().ShouldBeFalse();
        heartbeat.GetProperty("nextSequence").GetInt32().ShouldBe(0);

        var upload = await UploadSegmentAsync(s.Device, sessionId, 0, 0, 30.033);
        upload.StatusCode.ShouldBe(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var uploaded = await ReadJsonAsync(upload);
        uploaded.GetProperty("duplicate").GetBoolean().ShouldBeFalse();
        uploaded.GetProperty("segment").GetProperty("sequence").GetInt32().ShouldBe(0);
        uploaded.GetProperty("segment").GetProperty("status").GetString().ShouldBe("queued");
        uploaded.GetProperty("segment").GetProperty("durationSeconds").GetDecimal().ShouldBe(30.033m);
        (await ReadJsonAsync(await Heartbeat(s.Device, sessionId, State(recording: true, queueLength: 2, lastError: "falha temporária"))))
            .GetProperty("nextSequence").GetInt32().ShouldBe(1);

        var detail = await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"));
        var state = detail.GetProperty("session").GetProperty("companionState");
        state.GetProperty("recording").GetBoolean().ShouldBeTrue();
        state.GetProperty("queueLength").GetInt32().ShouldBe(2);
        state.GetProperty("lastError").GetString().ShouldBe("falha temporária");
        detail.GetProperty("session").GetProperty("online").GetBoolean().ShouldBeTrue();
        detail.GetProperty("settings").GetProperty("maxClipSeconds").GetInt32().ShouldBe(40);

        var end = await s.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/end", new { lastSequence = 0 }, Json);
        end.StatusCode.ShouldBe(HttpStatusCode.OK, await end.Content.ReadAsStringAsync());
        (await ReadJsonAsync(end)).GetProperty("session").GetProperty("status").GetString().ShouldBe("ending");
        // Ending is not terminal: heartbeats still answer (the companion waits for it), a new session is still refused.
        (await ReadJsonAsync(await Heartbeat(s.Device, sessionId, State()))).GetProperty("status").GetString().ShouldBe("ending");
        await ShouldBeProblemAsync(await s.Device.PostAsJsonAsync("/api/live/sessions", new { }, Json), HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(await s.User.GetAsync("/api/live/sessions?status=ending"))).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The last segment job finishes → ended.
        var worker = CreateInternalClient(WorkerId);
        var payload = await ClaimLiveAsync(worker);
        payload.GetProperty("live").GetProperty("sessionId").GetGuid().ShouldBe(sessionId);
        await RunSegmentJobAsync(worker, payload, liveContext: new { compositeSample = new[] { 1.0 }, language = "pt" });

        detail = await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"));
        var ended = detail.GetProperty("session");
        ended.GetProperty("status").GetString().ShouldBe("ended");
        ended.GetProperty("endedAt").ValueKind.ShouldBe(JsonValueKind.String);
        ended.GetProperty("endReason").GetString().ShouldBe(LiveService.EndedByCompanionReason);
        ended.GetProperty("stats").GetProperty("segmentsReceived").GetInt32().ShouldBe(1);
        ended.GetProperty("stats").GetProperty("segmentsAnalyzed").GetInt32().ShouldBe(1);
        ended.GetProperty("stats").GetProperty("segmentsFailed").GetInt32().ShouldBe(0);
        ended.GetProperty("stats").GetProperty("recordedSeconds").GetDecimal().ShouldBe(30.033m);
        detail.GetProperty("segments").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("analyzed");
        (await ReadJsonAsync(await s.User.GetAsync($"/api/projects/{projectId}"))).GetProperty("project").GetProperty("status").GetString().ShouldBe("ready");

        // Terminal: heartbeats and uploads answer 409, /end is harmless, a new session can start.
        await ShouldBeProblemAsync(await Heartbeat(s.Device, sessionId, State()), HttpStatusCode.Conflict, "conflict");
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 1, 30.033, 30), HttpStatusCode.Conflict, "conflict");
        (await s.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/end", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await ReadJsonAsync(await s.User.GetAsync("/api/live/sessions?status=ended"));
        list.GetProperty("total").GetInt32().ShouldBe(1);
        (await ReadJsonAsync(await s.User.GetAsync("/api/live/sessions?status=live"))).GetProperty("items").GetArrayLength().ShouldBe(0);
        var next = await CreateSessionAsync(s.Device, new { });
        next.GetProperty("title").GetString()!.ShouldStartWith("Live de ");
    }

    [Fact]
    public async Task Stop_requested_on_the_lives_page_reaches_the_companion_heartbeat()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        (await ReadJsonAsync(await Heartbeat(s.Device, sessionId, State(recording: true)))).GetProperty("stopRequested").GetBoolean().ShouldBeFalse();

        var stop = await s.User.PostAsync($"/api/live/sessions/{sessionId}/stop", null);
        stop.StatusCode.ShouldBe(HttpStatusCode.Accepted, await stop.Content.ReadAsStringAsync());
        (await ReadJsonAsync(stop)).GetProperty("session").GetProperty("stopRequested").GetBoolean().ShouldBeTrue();

        var heartbeat = await ReadJsonAsync(await Heartbeat(s.Device, sessionId, State(recording: true)));
        heartbeat.GetProperty("stopRequested").GetBoolean().ShouldBeTrue();
        heartbeat.GetProperty("status").GetString().ShouldBe("live");

        // The companion ends it; nothing was uploaded, so it is ended at once.
        var end = await ReadJsonAsync(await s.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/end", new { }, Json));
        end.GetProperty("session").GetProperty("status").GetString().ShouldBe("ended");
        end.GetProperty("session").GetProperty("endReason").GetString().ShouldBe(LiveService.StoppedByUserReason);
        await ShouldBeProblemAsync(await s.User.PostAsync($"/api/live/sessions/{sessionId}/stop", null), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Revoking_a_device_sets_its_open_sessions_to_error()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        (await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await s.User.DeleteAsync($"/api/live/companion/devices/{s.DeviceId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var session = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("session");
        session.GetProperty("status").GetString().ShouldBe("error");
        session.GetProperty("endReason").GetString().ShouldBe("dispositivo revogado");
        await ShouldBeProblemAsync(await Heartbeat(s.Device, sessionId, State()), HttpStatusCode.Unauthorized, "unauthorized");
        // The segment already received is still analysed.
        (await ClaimLiveAsync(CreateInternalClient(WorkerId))).GetProperty("live").GetProperty("sequence").GetInt32().ShouldBe(0);
    }

    // ---------------------------------------------------------------- segments

    [Fact]
    public async Task Uploading_the_same_sequence_twice_answers_200_duplicate_with_one_asset_and_one_job()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        var bytes = FakeMkv(5000);

        var first = await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60, bytes);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var second = await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60, FakeMkv(7000));
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        var duplicate = await ReadJsonAsync(second);
        duplicate.GetProperty("duplicate").GetBoolean().ShouldBeTrue();
        duplicate.GetProperty("segment").GetProperty("id").GetGuid().ShouldBe((await ReadJsonAsync(first)).GetProperty("segment").GetProperty("id").GetGuid());

        await WithDbAsync(async db =>
        {
            (await db.LiveSegments.CountAsync(x => x.SessionId == sessionId)).ShouldBe(1);
            var assets = await db.MediaAssets.Where(a => a.UserId == s.UserId).ToListAsync();
            var asset = assets.ShouldHaveSingleItem();
            asset.Kind.ShouldBe(MediaAssetKind.LiveSegment);
            asset.StorageKey.ShouldBe($"users/{s.UserId:D}/live/{sessionId:D}/seg_00000.mkv");
            asset.SizeBytes.ShouldBe(5000);
            asset.Status.ShouldBe(MediaAssetStatus.Uploaded);
            asset.ContentType.ShouldBe("video/x-matroska");
            (await db.ProcessingJobs.CountAsync(j => j.UserId == s.UserId && j.Kind == JobKind.LiveSegment)).ShouldBe(1);
        });
        var path = Path.Combine(Factory.StorageRoot, "users", s.UserId.ToString("D"), "live", sessionId.ToString("D"), "seg_00000.mkv");
        File.ReadAllBytes(path).ShouldBe(bytes);

        // An MP4 segment is accepted too (by its magic bytes).
        var mp4 = await UploadSegmentAsync(s.Device, sessionId, 1, 60, 60, FakeMp4(3000), fileName: "seg_00001.mp4", contentType: "video/mp4");
        mp4.StatusCode.ShouldBe(HttpStatusCode.Created, await mp4.Content.ReadAsStringAsync());
        (await WithDbAsync(db => db.MediaAssets.AnyAsync(a => a.StorageKey.EndsWith("/seg_00001.mp4") && a.ContentType == "video/mp4"))).ShouldBeTrue();
    }

    [Fact]
    public async Task Segments_that_are_not_matroska_webm_or_mp4_are_rejected_by_magic_bytes()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();

        var text = Encoding.ASCII.GetBytes(new string('x', 2048));
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60, text), (HttpStatusCode)422, "invalid_media");
        // An audio-only container is not a segment either.
        var wav = new byte[1024];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60, wav), (HttpStatusCode)422, "invalid_media");
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 0, 0, 301), HttpStatusCode.BadRequest, "validation_failed");
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, -1, 0, 60), HttpStatusCode.BadRequest, "validation_failed");

        using var noFile = new MultipartFormDataContent { { new StringContent("0"), "sequence" }, { new StringContent("0"), "startOffsetSeconds" }, { new StringContent("60"), "durationSeconds" } };
        await ShouldBeProblemAsync(await s.Device.PostAsync($"/api/live/sessions/{sessionId}/segments", noFile), HttpStatusCode.BadRequest, "validation_failed");

        await WithDbAsync(async db =>
        {
            (await db.LiveSegments.CountAsync()).ShouldBe(0);
            (await db.MediaAssets.CountAsync(a => a.UserId == s.UserId)).ShouldBe(0);
            (await db.ProcessingJobs.CountAsync(j => j.UserId == s.UserId)).ShouldBe(0);
        });
        // The session is still usable.
        (await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- live_segment jobs

    [Fact]
    public async Task Segment_jobs_are_claimed_in_order_with_the_previous_segment_and_its_live_context()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        var seg0 = await UploadOkAsync(s.Device, sessionId, 0, 0, 60);
        var seg1 = await UploadOkAsync(s.Device, sessionId, 1, 60, 60);
        var worker = CreateInternalClient(WorkerId);

        var first = await ClaimLiveAsync(worker);
        first.GetProperty("kind").GetString().ShouldBe("live_segment");
        first.GetProperty("userId").GetGuid().ShouldBe(s.UserId);
        var live = first.GetProperty("live");
        live.GetProperty("sessionId").GetGuid().ShouldBe(sessionId);
        live.GetProperty("segmentId").GetGuid().ShouldBe(seg0);
        live.GetProperty("sequence").GetInt32().ShouldBe(0);
        live.GetProperty("startOffsetSeconds").GetDecimal().ShouldBe(0m);
        live.GetProperty("durationSeconds").GetDecimal().ShouldBe(60m);
        live.GetProperty("final").GetBoolean().ShouldBeFalse();
        live.GetProperty("segmentSeconds").GetInt32().ShouldBe(60);
        var source = first.GetProperty("source");
        source.GetProperty("fileName").GetString().ShouldBe("seg_00000.mkv");
        source.GetProperty("contentType").GetString().ShouldBe("video/x-matroska");
        source.GetProperty("sizeBytes").GetInt64().ShouldBe(2048);
        source.GetProperty("durationSeconds").GetDecimal().ShouldBe(60m);
        source.GetProperty("downloadUrl").GetString()!.ShouldContain("/api/storage/download?key=");
        first.GetProperty("previous").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("settings").GetProperty("autoRenderTop").ValueKind.ShouldBe(JsonValueKind.Number);
        first.GetProperty("limits").GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3600);
        first.GetProperty("limits").GetProperty("transcriptionModel").GetString().ShouldBe("base");

        // Segment 1 waits until segment 0's job is terminal (also for another worker).
        (await ClaimAsync(CreateInternalClient("other-worker"), "other-worker", "live_segment")).ShouldBeNull();
        var segment0 = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("segments").EnumerateArray().Last();
        segment0.GetProperty("status").GetString().ShouldBe("analyzing");

        var context = new { compositeSample = new[] { 1.2, 0.8 }, language = "pt", sequence = 0, committedUntil = 50.0 };
        await RunSegmentJobAsync(worker, first, liveContext: context);

        var second = await ClaimLiveAsync(worker);
        second.GetProperty("live").GetProperty("sequence").GetInt32().ShouldBe(1);
        second.GetProperty("live").GetProperty("segmentId").GetGuid().ShouldBe(seg1);
        second.GetProperty("live").GetProperty("startOffsetSeconds").GetDecimal().ShouldBe(60m);
        second.GetProperty("source").GetProperty("fileName").GetString().ShouldBe("seg_00001.mkv");
        var previous = second.GetProperty("previous");
        previous.GetProperty("segmentId").GetGuid().ShouldBe(seg0);
        previous.GetProperty("sequence").GetInt32().ShouldBe(0);
        previous.GetProperty("startOffsetSeconds").GetDecimal().ShouldBe(0m);
        previous.GetProperty("durationSeconds").GetDecimal().ShouldBe(60m);
        previous.GetProperty("downloadUrl").GetString()!.ShouldContain("seg_00000.mkv");
        var carried = second.GetProperty("context");
        carried.GetProperty("compositeSample").EnumerateArray().Select(v => v.GetDouble()).ShouldBe([1.2, 0.8]);
        carried.GetProperty("language").GetString().ShouldBe("pt");
        carried.GetProperty("committedUntil").GetDouble().ShouldBe(50.0);
        await WithDbAsync(async db =>
            (await db.LiveSegments.SingleAsync(x => x.Id == seg0)).LiveContext.ShouldNotBeNull());

        // Segment 1 fails for good: segment 2 gets no previous segment, and the newest context there is (segment 0's).
        var seg2 = await UploadOkAsync(s.Device, sessionId, 2, 120, 45.5, final: true);
        (await ClaimAsync(worker, WorkerId, "live_segment")).ShouldBeNull();
        var fail = await worker.PostAsJsonAsync($"/internal/jobs/{second.GetProperty("jobId").GetGuid()}/fail", new { code = "worker_error", message = "falhou", retryable = false }, Json);
        fail.StatusCode.ShouldBe(HttpStatusCode.OK, await fail.Content.ReadAsStringAsync());
        var third = await ClaimLiveAsync(worker);
        third.GetProperty("live").GetProperty("segmentId").GetGuid().ShouldBe(seg2);
        third.GetProperty("live").GetProperty("final").GetBoolean().ShouldBeTrue();
        third.GetProperty("live").GetProperty("durationSeconds").GetDecimal().ShouldBe(45.5m);
        third.GetProperty("previous").ValueKind.ShouldBe(JsonValueKind.Null);
        third.GetProperty("context").GetProperty("sequence").GetInt32().ShouldBe(0);

        var detail = await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"));
        var segments = detail.GetProperty("segments").EnumerateArray().ToList();
        segments.Select(x => x.GetProperty("sequence").GetInt32()).ShouldBe([2, 1, 0]);
        segments[1].GetProperty("status").GetString().ShouldBe("failed");
        segments[1].GetProperty("error").GetString().ShouldBe("falhou");
        detail.GetProperty("session").GetProperty("stats").GetProperty("segmentsFailed").GetInt32().ShouldBe(1);
        // Failed segments do not notify one by one.
        (await ReadJsonAsync(await s.User.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray()
            .ShouldNotContain(n => n.GetProperty("type").GetString() == "job_failed");
    }

    [Fact]
    public async Task Render_of_a_live_clip_carries_the_overlapping_segments_and_highlights_notify()
    {
        var s = await SetupAsync();
        var session = await CreateSessionAsync(s.Device);
        var sessionId = session.GetProperty("id").GetGuid();
        var projectId = session.GetProperty("projectId").GetGuid();
        await UploadOkAsync(s.Device, sessionId, 0, 0, 60);
        await UploadOkAsync(s.Device, sessionId, 1, 60, 60);
        await UploadOkAsync(s.Device, sessionId, 2, 120, 60);
        var worker = CreateInternalClient(WorkerId);

        // Segment 0 detects a moment that runs into segment 1 and auto-renders it.
        var first = await ClaimLiveAsync(worker);
        var jobId = first.GetProperty("jobId").GetGuid();
        await ProbeAsync(worker, jobId, 60);
        var candidates = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new[] { new { rank = 1, startSeconds = 50.0, endSeconds = 70.0, score = 88, category = "hype", reasoning = "r", transcript = "t", signals = new { live = new { sequence = 0 } } } },
        }, Json);
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK, await candidates.Content.ReadAsStringAsync());
        var candidateId = (await ReadJsonAsync(candidates)).GetProperty("ids")[0].GetGuid();
        var clips = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
        {
            clips = new[]
            {
                new
                {
                    candidateId, title = "Virada no fim", aspectRatio = "9:16", startSeconds = 50.0, endSeconds = 70.0,
                    render = new { storageKey = $"users/{s.UserId:D}/jobs/{jobId:D}/clip_render/clip1.mp4", width = 720, height = 1280, durationSeconds = 20.0, sizeBytes = 1000, hasSubtitles = true },
                    thumbnailKey = $"users/{s.UserId:D}/jobs/{jobId:D}/thumbnail/clip1.jpg",
                },
            },
        }, Json);
        clips.StatusCode.ShouldBe(HttpStatusCode.OK, await clips.Content.ReadAsStringAsync());
        var clipId = (await ReadJsonAsync(clips)).GetProperty("clips")[0].GetProperty("clipId").GetGuid();
        var transcriptKey = $"users/{s.UserId:D}/jobs/{jobId:D}/analysis/transcript.json";
        await PostAnalysisAndCompleteAsync(worker, jobId, 60, new { compositeSample = new[] { 2.0 }, language = "pt" }, transcriptKey);
        await RunSegmentJobAsync(worker, await ClaimLiveAsync(worker));
        await RunSegmentJobAsync(worker, await ClaimLiveAsync(worker));

        // One clip_ready per highlight (besides the welcome notification of the registration); none per segment job.
        var notifications = (await ReadJsonAsync(await s.User.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() != "system").ToList();
        var highlight = notifications.ShouldHaveSingleItem();
        highlight.GetProperty("type").GetString().ShouldBe("clip_ready");
        highlight.GetProperty("title").GetString().ShouldBe("Novo destaque na live");
        highlight.GetProperty("data").GetProperty("clipId").GetGuid().ShouldBe(clipId);
        highlight.GetProperty("data").GetProperty("sessionId").GetGuid().ShouldBe(sessionId);

        var detail = await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"));
        detail.GetProperty("candidateCount").GetInt32().ShouldBe(1);
        detail.GetProperty("highlights").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(clipId);
        detail.GetProperty("session").GetProperty("stats").GetProperty("highlights").GetInt32().ShouldBe(1);
        detail.GetProperty("session").GetProperty("stats").GetProperty("segmentsAnalyzed").GetInt32().ShouldBe(3);
        (await ReadJsonAsync(await s.User.GetAsync($"/api/projects/{projectId}/candidates"))).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The open session holds the free plan's only concurrency slot: end it, then re-render the clip.
        await ShouldBeProblemAsync(await s.User.PostAsync($"/api/clips/{clipId}/render", null), HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(await s.Device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/end", new { }, Json)))
            .GetProperty("session").GetProperty("status").GetString().ShouldBe("ended");
        var render = await s.User.PostAsync($"/api/clips/{clipId}/render", null);
        render.StatusCode.ShouldBe(HttpStatusCode.Accepted, await render.Content.ReadAsStringAsync());

        var payload = (await ClaimAsync(worker, WorkerId, "render_clip")).ShouldNotBeNull();
        payload.GetProperty("kind").GetString().ShouldBe("render_clip");
        payload.TryGetProperty("source", out _).ShouldBeFalse();
        var segments = payload.GetProperty("segments").EnumerateArray().ToList();
        segments.Select(x => x.GetProperty("startOffsetSeconds").GetDecimal()).ShouldBe([0m, 60m]);
        segments.Select(x => x.GetProperty("fileName").GetString()).ShouldBe(["seg_00000.mkv", "seg_00001.mkv"]);
        segments.ShouldAllBe(x => x.GetProperty("durationSeconds").GetDecimal() == 60m && x.GetProperty("downloadUrl").GetString()!.Contains("/api/storage/download?key="));
        payload.GetProperty("clip").GetProperty("clipId").GetGuid().ShouldBe(clipId);
        payload.GetProperty("clip").GetProperty("startSeconds").GetDecimal().ShouldBe(50m);
        payload.GetProperty("clip").GetProperty("endSeconds").GetDecimal().ShouldBe(70m);
        payload.GetProperty("analysis").GetProperty("transcriptUrl").GetString()!.ShouldContain("transcript.json");
        payload.GetProperty("limits").GetProperty("maxRenderHeight").GetInt32().ShouldBe(720);
    }

    [Fact]
    public async Task Exhausted_monthly_minutes_refuse_segments_and_set_the_session_to_error()
    {
        // No minutes left at upload time.
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        await UseProcessingSecondsAsync(s.UserId, 3600);
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 0, 0, 60), HttpStatusCode.Conflict, "quota_exceeded");
        var session = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("session");
        session.GetProperty("status").GetString().ShouldBe("error");
        session.GetProperty("endReason").GetString().ShouldBe(LiveService.QuotaExhaustedReason);
        await ShouldBeProblemAsync(await Heartbeat(s.Device, sessionId, State(recording: true)), HttpStatusCode.Conflict, "quota_exceeded");
        (await WithDbAsync(db => db.LiveSegments.CountAsync())).ShouldBe(0);

        // The declared duration fits (60 s of the 60 left) but the file is longer: the worker's /probe refuses it and
        // the session stops.
        var t = await SetupAsync();
        var otherId = (await CreateSessionAsync(t.Device)).GetProperty("id").GetGuid();
        await UseProcessingSecondsAsync(t.UserId, 3540);
        await UploadOkAsync(t.Device, otherId, 0, 0, 60);
        var worker = CreateInternalClient(WorkerId);
        var payload = await ClaimLiveAsync(worker);
        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{payload.GetProperty("jobId").GetGuid()}/probe", ProbeOf(60.5), Json);
        await ShouldBeProblemAsync(probe, HttpStatusCode.Conflict, "quota_exceeded");
        var detail = await ReadJsonAsync(await t.User.GetAsync($"/api/live/sessions/{otherId}"));
        detail.GetProperty("session").GetProperty("status").GetString().ShouldBe("error");
        detail.GetProperty("session").GetProperty("endReason").GetString().ShouldBe(LiveService.QuotaExhaustedReason);
        detail.GetProperty("segments")[0].GetProperty("status").GetString().ShouldBe("failed");
        await ShouldBeProblemAsync(await UploadSegmentAsync(t.Device, otherId, 1, 60, 60), HttpStatusCode.Conflict, "quota_exceeded");
    }

    [Fact]
    public async Task Probe_reserves_each_segment_and_completion_charges_it()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        await UploadOkAsync(s.Device, sessionId, 0, 0, 60);
        var worker = CreateInternalClient(WorkerId);
        var payload = await ClaimLiveAsync(worker);
        var jobId = payload.GetProperty("jobId").GetGuid();

        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeOf(59.5), Json);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK, await probe.Content.ReadAsStringAsync());
        (await ReadJsonAsync(probe)).GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3600 - 60);
        await WithDbAsync(async db =>
        {
            (await db.ProcessingJobs.SingleAsync(j => j.Id == jobId)).ReservedSeconds.ShouldBe(60);
            var asset = await db.LiveSegments.Where(x => x.JobId == jobId).Select(x => x.MediaAsset!).SingleAsync();
            asset.DurationSeconds.ShouldBe(59.5m);
            asset.Status.ShouldBe(MediaAssetStatus.Verified);
        });

        var complete = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 59.5 }, Json);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        await WithDbAsync(async db =>
        {
            var usage = await db.UsageRecords.Where(u => u.UserId == s.UserId && u.Kind == UsageKind.ProcessingSeconds).ToListAsync();
            usage.ShouldHaveSingleItem().Quantity.ShouldBe(60m);
            usage[0].JobId.ShouldBe(jobId);
        });
    }

    [Fact]
    public async Task A_silent_session_goes_to_error_and_its_pending_segments_are_still_processed()
    {
        var s = await SetupAsync();
        var silentId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        await UploadOkAsync(s.Device, silentId, 0, 0, 60);
        var other = await SetupAsync();
        var activeId = (await CreateSessionAsync(other.Device)).GetProperty("id").GetGuid();
        (await Heartbeat(other.Device, activeId, State(recording: true))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var old = DateTimeOffset.UtcNow.AddMinutes(-11);
        await WithDbAsync(db => db.LiveSessions.Where(x => x.Id == silentId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.StartedAt, old).SetProperty(x => x.LastSegmentAt, old).SetProperty(x => x.LastHeartbeatAt, old)));

        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LiveService>().ReapAsync(CancellationToken.None)).ShouldBe(1);
        }

        var silent = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{silentId}"))).GetProperty("session");
        silent.GetProperty("status").GetString().ShouldBe("error");
        silent.GetProperty("endReason").GetString().ShouldBe("companion offline");
        silent.GetProperty("online").GetBoolean().ShouldBeFalse();
        (await ReadJsonAsync(await other.User.GetAsync($"/api/live/sessions/{activeId}"))).GetProperty("session").GetProperty("status").GetString().ShouldBe("live");
        await ShouldBeProblemAsync(await Heartbeat(s.Device, silentId, State(recording: true)), HttpStatusCode.Conflict, "conflict");

        // The segment it had sent is still analysed.
        var worker = CreateInternalClient(WorkerId);
        var payload = await ClaimLiveAsync(worker);
        payload.GetProperty("live").GetProperty("sessionId").GetGuid().ShouldBe(silentId);
        await RunSegmentJobAsync(worker, payload);
        var detail = await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{silentId}"));
        detail.GetProperty("session").GetProperty("status").GetString().ShouldBe("error");
        detail.GetProperty("segments")[0].GetProperty("status").GetString().ShouldBe("analyzed");
        // The device can start a new session (the silent one is terminal).
        (await s.Device.PostAsJsonAsync("/api/live/sessions", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- quotas and backlog of segment uploads

    [Fact]
    public async Task Minutes_promised_to_segments_not_probed_yet_refuse_a_segment_that_could_never_be_analysed()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        await UseProcessingSecondsAsync(s.UserId, 3420); // 180 s left of the 3600 of the plan
        await UploadOkAsync(s.Device, sessionId, 0, 0, 60);
        await UploadOkAsync(s.Device, sessionId, 1, 60, 60);
        // The worker probes segment 0: its minute is reserved now and must not be counted twice.
        var worker = CreateInternalClient(WorkerId);
        var payload = await ClaimLiveAsync(worker);
        await ProbeAsync(worker, payload.GetProperty("jobId").GetGuid(), 60);
        await UploadOkAsync(s.Device, sessionId, 2, 120, 60); // 3420 used + 60 reserved + 60 promised + 60 = 3600

        var refused = await UploadSegmentAsync(s.Device, sessionId, 3, 180, 60);
        await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "quota_exceeded");
        var session = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("session");
        session.GetProperty("status").GetString().ShouldBe("error");
        session.GetProperty("endReason").GetString().ShouldBe(LiveService.QuotaExhaustedReason);
        (await WithDbAsync(db => db.LiveSegments.CountAsync(x => x.SessionId == sessionId))).ShouldBe(3);
        File.Exists(SegmentPath(s.UserId, sessionId, 3)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_segment_over_the_storage_quota_is_refused_before_it_is_stored_and_ends_the_session()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        await UploadOkAsync(s.Device, sessionId, 0, 0, 60);
        // The free plan stores 5 GB; the user already has all of it but 1 KB.
        await AddStoredBytesAsync(s.UserId, 5L * 1024 * 1024 * 1024 - 1024);

        var refused = await UploadSegmentAsync(s.Device, sessionId, 1, 60, 60, FakeMkv(4096));
        await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "quota_exceeded");
        var session = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("session");
        session.GetProperty("status").GetString().ShouldBe("error");
        session.GetProperty("endReason").GetString().ShouldBe(LiveService.StorageExhaustedReason);
        File.Exists(SegmentPath(s.UserId, sessionId, 1)).ShouldBeFalse();
        await WithDbAsync(async db =>
        {
            (await db.LiveSegments.CountAsync(x => x.SessionId == sessionId)).ShouldBe(1);
            (await db.MediaAssets.CountAsync(a => a.UserId == s.UserId && a.Kind == MediaAssetKind.LiveSegment)).ShouldBe(1);
        });
        await ShouldBeProblemAsync(await Heartbeat(s.Device, sessionId, State(recording: true)), HttpStatusCode.Conflict, "quota_exceeded");
        // The segment already stored is still analysed.
        var worker = CreateInternalClient(WorkerId);
        (await ClaimLiveAsync(worker)).GetProperty("live").GetProperty("sequence").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_full_backlog_answers_429_with_retry_after_until_the_worker_takes_a_segment()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();
        for (var sequence = 0; sequence < 5; sequence++)
        {
            await UploadOkAsync(s.Device, sessionId, sequence, sequence * 60, 60);
        }

        var full = await UploadSegmentAsync(s.Device, sessionId, 5, 300, 60);
        await ShouldBeProblemAsync(full, HttpStatusCode.TooManyRequests, "rate_limited");
        full.Headers.RetryAfter.ShouldNotBeNull();
        full.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(30));
        File.Exists(SegmentPath(s.UserId, sessionId, 5)).ShouldBeFalse();
        var session = (await ReadJsonAsync(await s.User.GetAsync($"/api/live/sessions/{sessionId}"))).GetProperty("session");
        session.GetProperty("status").GetString().ShouldBe("live");
        session.GetProperty("stats").GetProperty("segmentsReceived").GetInt32().ShouldBe(5);
        // A sequence already stored is still answered as a duplicate.
        (await UploadSegmentAsync(s.Device, sessionId, 4, 240, 60)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The worker takes segment 0: there is room for one more.
        await ClaimLiveAsync(CreateInternalClient(WorkerId));
        (await UploadSegmentAsync(s.Device, sessionId, 5, 300, 60)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldBeProblemAsync(await UploadSegmentAsync(s.Device, sessionId, 6, 360, 60), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task Only_the_known_fields_are_read_and_too_many_parts_before_the_file_are_refused()
    {
        var s = await SetupAsync();
        var sessionId = (await CreateSessionAsync(s.Device)).GetProperty("id").GetGuid();

        // Other parts (even one over the 1 KB field limit, or another file) are skipped unread.
        using var extra = SegmentForm(0, 0, 60);
        extra.Add(new StringContent(new string('x', 5000)), "comment");
        extra.Add(new ByteArrayContent(FakeMkv(100)), "thumbnail", "thumb.mkv");
        extra.Add(new ByteArrayContent(FakeMkv(3000)), "file", "seg_00000.mkv");
        var accepted = await s.Device.PostAsync($"/api/live/sessions/{sessionId}/segments", extra);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
        File.ReadAllBytes(SegmentPath(s.UserId, sessionId, 0)).ShouldBe(FakeMkv(3000));

        // A flood of tiny parts before the file: 400 after the 8th, nothing stored.
        using var flood = new MultipartFormDataContent();
        for (var i = 0; i < 50; i++) flood.Add(new StringContent("1"), $"f{i}");
        AddSegmentFields(flood, 1, 60, 60);
        flood.Add(new ByteArrayContent(FakeMkv()), "file", "seg_00001.mkv");
        var refused = await s.Device.PostAsync($"/api/live/sessions/{sessionId}/segments", flood);
        await ShouldBeProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        (await ReadJsonAsync(refused)).GetProperty("errors").GetProperty("file")[0].GetString()!.ShouldContain($"{LiveService.MaxPartsBeforeFile} campos");
        (await WithDbAsync(db => db.LiveSegments.CountAsync(x => x.SessionId == sessionId))).ShouldBe(1);
    }

    [Fact]
    public async Task Segments_reach_s3_storage_although_the_request_stream_has_no_length()
    {
        // The API with S3 storage (the real SDK over an in-memory endpoint, 16 KB parts so a 50 KB segment goes up in parts).
        var s3 = new FakeS3Handler();
        await using var s3Api = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage>(s3.CreateStorage(partSizeBytes: 16 * 1024));
        }));
        var user = s3Api.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        var register = await user.PostAsJsonAsync("/api/auth/register", new { email = NextEmail("s3"), password = "Password123", displayName = "S3", acceptedTerms = true }, Json);
        register.StatusCode.ShouldBe(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());
        var userId = (await ReadJsonAsync(register)).GetProperty("user").GetProperty("id").GetGuid();
        var (_, token) = await CreateDeviceAsync(user);
        var device = s3Api.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sessionId = (await CreateSessionAsync(device)).GetProperty("id").GetGuid();

        var small = FakeMkv(5000);
        var large = FakeMkv(50_000);
        await UploadOkAsync(device, sessionId, 0, 0, 60, bytes: small);
        await UploadOkAsync(device, sessionId, 1, 60, 60, bytes: large);

        var prefix = $"users/{userId:D}/live/{sessionId:D}/";
        s3.Objects[prefix + "seg_00000.mkv"].Content.ShouldBe(small);
        s3.Objects[prefix + "seg_00001.mkv"].Content.ShouldBe(large);
        s3.Objects[prefix + "seg_00001.mkv"].ContentType.ShouldBe("video/x-matroska");
        s3.Calls.ShouldContain($"PutObject {prefix}seg_00000.mkv");
        s3.Calls.ShouldContain($"CompleteMultipartUpload {prefix}seg_00001.mkv 4");
        s3.OpenUploads.ShouldBe(0);
        var sizes = await WithDbAsync(db => db.MediaAssets.Where(a => a.UserId == userId && a.Kind == MediaAssetKind.LiveSegment)
            .OrderBy(a => a.StorageKey).Select(a => a.SizeBytes).ToListAsync());
        sizes.ShouldBe(new List<long> { 5000, 50_000 });
    }

    // ---------------------------------------------------------------- helpers

    private string SegmentPath(Guid userId, Guid sessionId, int sequence)
        => Path.Combine(Factory.StorageRoot, "users", userId.ToString("D"), "live", sessionId.ToString("D"), $"seg_{sequence:00000}.mkv");

    /// <summary>A stored upload of <paramref name="bytes"/> (row only) counting against the storage quota of the user.</summary>
    private async Task AddStoredBytesAsync(Guid userId, long bytes)
    {
        await WithDbAsync(async db =>
        {
            db.MediaAssets.Add(new MediaAsset
            {
                UserId = userId, Kind = MediaAssetKind.SourceUpload, StorageKey = $"users/{userId:D}/uploads/{Guid.NewGuid():N}.mp4",
                OriginalFileName = "old.mp4", ContentType = "video/mp4", SizeBytes = bytes, Status = MediaAssetStatus.Uploaded,
                UploadedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
    }

    private static MultipartFormDataContent SegmentForm(int sequence, double start, double duration, bool final = false)
    {
        var form = new MultipartFormDataContent();
        AddSegmentFields(form, sequence, start, duration, final);
        return form;
    }

    private static void AddSegmentFields(MultipartFormDataContent form, int sequence, double start, double duration, bool final = false)
    {
        form.Add(new StringContent(sequence.ToString(CultureInfo.InvariantCulture)), "sequence");
        form.Add(new StringContent(start.ToString("F6", CultureInfo.InvariantCulture)), "startOffsetSeconds");
        form.Add(new StringContent(duration.ToString("F6", CultureInfo.InvariantCulture)), "durationSeconds");
        form.Add(new StringContent(final ? "true" : "false"), "final");
    }


    private sealed record LiveUser(HttpClient User, Guid UserId, HttpClient Device, Guid DeviceId, string Token);

    private async Task<LiveUser> SetupAsync()
    {
        var (client, userId, _) = await RegisterAsync();
        var (deviceId, token) = await CreateDeviceAsync(client);
        return new LiveUser(client, userId, DeviceClient(token), deviceId, token);
    }

    private static async Task<(Guid DeviceId, string Token)> CreateDeviceAsync(HttpClient user, string name = "PC do estúdio")
    {
        var response = await user.PostAsJsonAsync("/api/live/companion/tokens", new { name }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        return (body.GetProperty("device").GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    private HttpClient DeviceClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> CreateSessionAsync(HttpClient device, object? body = null)
    {
        var response = await device.PostAsJsonAsync("/api/live/sessions", body ?? new { title = "Live de teste", segmentSeconds = 60 }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("session");
    }

    private static object State(bool recording = false, int queueLength = 0, string? lastError = null)
        => new { obsConnected = true, streaming = true, recording, uploading = false, queueLength, lastError };

    private static Task<HttpResponseMessage> Heartbeat(HttpClient device, Guid sessionId, object state)
        => device.PostAsJsonAsync($"/api/live/sessions/{sessionId}/heartbeat", new { state }, Json);

    /// <summary>A Matroska header (EBML magic + "matroska" doc type) followed by filler bytes.</summary>
    private static byte[] FakeMkv(int size = 2048)
    {
        byte[] header = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81, 0x04, 0x42, 0xF3, 0x81, 0x08, 0x42, 0x82, 0x88,
            (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a'];
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < bytes.Length; i++) bytes[i] = (byte)(i % 239);
        return bytes;
    }

    private static Task<HttpResponseMessage> UploadSegmentAsync(HttpClient device, Guid sessionId, int sequence, double start, double duration,
        byte[]? bytes = null, bool final = false, string? fileName = null, string contentType = "video/x-matroska")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(sequence.ToString(CultureInfo.InvariantCulture)), "sequence" },
            { new StringContent(start.ToString("F6", CultureInfo.InvariantCulture)), "startOffsetSeconds" },
            { new StringContent(duration.ToString("F6", CultureInfo.InvariantCulture)), "durationSeconds" },
            { new StringContent(final ? "true" : "false"), "final" },
        };
        var file = new ByteArrayContent(bytes ?? FakeMkv());
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName ?? $"seg_{sequence:00000}.mkv");
        return device.PostAsync($"/api/live/sessions/{sessionId}/segments", content);
    }

    private static async Task<Guid> UploadOkAsync(HttpClient device, Guid sessionId, int sequence, double start, double duration, bool final = false, byte[]? bytes = null)
    {
        var response = await UploadSegmentAsync(device, sessionId, sequence, start, duration, bytes, final: final);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("segment").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ClaimLiveAsync(HttpClient worker)
        => (await ClaimAsync(worker, WorkerId, "live_segment")).ShouldNotBeNull("no live_segment job was claimable");

    private static object ProbeOf(double duration) => new
    {
        durationSeconds = duration, width = 1920, height = 1080, fps = 60.0, hasAudio = true, hasVideo = true,
        container = "matroska", videoCodec = "h264", audioCodec = "aac", sizeBytes = 2048,
    };

    private static async Task ProbeAsync(HttpClient worker, Guid jobId, double duration)
    {
        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeOf(duration), Json);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK, await probe.Content.ReadAsStringAsync());
    }

    private static async Task PostAnalysisAndCompleteAsync(HttpClient worker, Guid jobId, double duration, object? liveContext, string? transcriptKey = null)
    {
        var summary = new { signals = new { audio = new { available = true } }, processedSeconds = duration, candidatesFound = 0, liveContext };
        var analysis = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/analysis", new { transcriptKey, summary }, Json);
        analysis.StatusCode.ShouldBe(HttpStatusCode.OK, await analysis.Content.ReadAsStringAsync());
        var complete = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = duration, summary }, Json);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
    }

    /// <summary>Probe → /analysis (with summary.liveContext) → /complete, as the worker does for a segment.</summary>
    private static async Task RunSegmentJobAsync(HttpClient worker, JsonElement payload, object? liveContext = null)
    {
        var jobId = payload.GetProperty("jobId").GetGuid();
        var duration = payload.GetProperty("live").GetProperty("durationSeconds").GetDouble();
        await ProbeAsync(worker, jobId, duration);
        await PostAnalysisAndCompleteAsync(worker, jobId, duration, liveContext);
    }

    private async Task UseProcessingSecondsAsync(Guid userId, decimal seconds)
    {
        await WithDbAsync(async db =>
        {
            var subscription = await db.Subscriptions.FirstAsync(x => x.UserId == userId);
            db.UsageRecords.Add(new UsageRecord
            {
                UserId = userId, SubscriptionId = subscription.Id, Kind = UsageKind.ProcessingSeconds, Quantity = seconds,
                PeriodStart = subscription.CurrentPeriodStart, PeriodEnd = subscription.CurrentPeriodEnd,
            });
            await db.SaveChangesAsync();
        });
    }
}
