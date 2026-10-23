using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

/// <summary>GET /api/projects/{id}/clips/archive: every ready clip of the project in one streamed zip.</summary>
public class ClipsArchiveTests : TestBase
{
    public ClipsArchiveTests(ApiFactory factory) : base(factory)
    {
    }

    private static byte[] ClipBytes(int marker, int size)
    {
        var bytes = FakeMp4(size);
        bytes[100] = (byte)marker;
        bytes[^1] = (byte)(255 - marker);
        return bytes;
    }

    /// <summary>Analysis with one rendered clip per (title, score); returns the project, the candidates and the uploaded bytes per clip.</summary>
    private async Task<(Guid ProjectId, List<Guid> CandidateIds, Dictionary<Guid, byte[]> BytesByClip)> AnalyzeWithClipsAsync(
        HttpClient client, HttpClient worker, string projectTitle, params (string Title, int Score)[] clips)
    {
        var (projectId, jobId) = await CreateProjectAsync(client, projectTitle);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = clips.Select((c, i) => new
            {
                rank = i + 1, startSeconds = 10.0 + i * 60, endSeconds = 40.0 + i * 60, score = c.Score, category = "hype", reasoning = "r", transcript = "t", signals = new { },
            }).ToArray(),
        }, Json));
        var candidateIds = candidates.GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();

        var bytesByClip = new Dictionary<Guid, byte[]>();
        if (clips.Length > 0)
        {
            var files = (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new
            {
                files = clips.Select((_, i) => new { kind = "clip_render", fileName = $"clip-{i}.mp4", contentType = "video/mp4", sizeBytes = 8192 }).ToArray(),
            }, Json))).GetProperty("files");
            var payloads = clips.Select((_, i) => ClipBytes(i + 1, 3000 + i * 700)).ToList();
            for (var i = 0; i < clips.Length; i++)
            {
                (await worker.PutAsync(files[i].GetProperty("url").GetString(), new ByteArrayContent(payloads[i]))).StatusCode.ShouldBe(HttpStatusCode.OK);
            }
            var created = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
            {
                clips = clips.Select((c, i) => new
                {
                    candidateId = candidateIds[i], title = c.Title, aspectRatio = "9:16", startSeconds = 10.0 + i * 60, endSeconds = 40.0 + i * 60,
                    render = new { storageKey = files[i].GetProperty("key").GetString(), width = 720, height = 1280, durationSeconds = 30.0, sizeBytes = payloads[i].Length, hasSubtitles = true },
                }).ToArray(),
            }, Json));
            var createdClips = created.GetProperty("clips").EnumerateArray().ToList();
            for (var i = 0; i < createdClips.Count; i++) bytesByClip[createdClips[i].GetProperty("clipId").GetGuid()] = payloads[i];
        }
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return (projectId, candidateIds, bytesByClip);
    }

    private static Dictionary<string, (byte[] Bytes, long Compressed)> ReadZip(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var result = new Dictionary<string, (byte[], long)>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            result[entry.FullName] = (copy.ToArray(), entry.CompressedLength);
        }
        return result;
    }

    [Fact]
    public async Task The_archive_streams_every_ready_clip_with_its_bytes()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, candidateIds, bytesByClip) = await AnalyzeWithClipsAsync(client, worker, "Live de Sábado!",
            ("Frase marcante", 70), ("Hype!", 90), ("Clipe apagado", 80));
        var clipIds = bytesByClip.Keys.ToList();

        // a deleted clip and a clip whose render is still pending are not part of the archive
        (await client.DeleteAsync($"/api/clips/{clipIds[2]}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var pending = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{candidateIds[0]}/clips", new { title = "Ainda renderizando" }, Json);
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted, await pending.Content.ReadAsStringAsync());

        var response = await client.GetAsync($"/api/projects/{projectId}/clips/archive");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        response.Content.Headers.ContentDisposition!.ToString().ShouldBe("attachment; filename=\"live-de-sabado-clipes.zip\"");

        var entries = ReadZip(await response.Content.ReadAsByteArrayAsync());
        entries.Keys.ShouldBe(["01-hype.mp4", "02-frase-marcante.mp4"]); // best first (score)
        entries["01-hype.mp4"].Bytes.ShouldBe(bytesByClip[clipIds[1]]);
        entries["02-frase-marcante.mp4"].Bytes.ShouldBe(bytesByClip[clipIds[0]]);
        foreach (var (_, (bytes, compressed)) in entries) compressed.ShouldBe(bytes.LongLength); // stored, not compressed

        // downloading the archive is not a render
        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.Kind == UsageKind.Render))).ShouldBe(1); // only the pending clip above
    }

    [Fact]
    public async Task A_project_without_ready_clips_is_not_found()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, _) = await AnalyzeWithClipsAsync(client, worker, "Sem clipes");
        await ShouldBeProblemAsync(await client.GetAsync($"/api/projects/{projectId}/clips/archive"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Only_the_owner_can_download_the_archive()
    {
        var (owner, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, _) = await AnalyzeWithClipsAsync(owner, worker, "Minha live", ("Hype!", 90));

        var (other, _, _) = await RegisterAsync();
        await ShouldBeProblemAsync(await other.GetAsync($"/api/projects/{projectId}/clips/archive"), HttpStatusCode.NotFound, "not_found");
        (await CreateClient().GetAsync($"/api/projects/{projectId}/clips/archive")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.GetAsync($"/api/projects/{projectId}/clips/archive")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_render_whose_file_is_gone_is_left_out()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, bytesByClip) = await AnalyzeWithClipsAsync(client, worker, "Live", ("Primeiro", 90), ("Segundo", 80));
        var firstClip = bytesByClip.Keys.First();
        var key = await WithDbAsync(db => db.Clips.Where(c => c.Id == firstClip)
            .Join(db.ClipRenders, c => c.CurrentRenderId, r => r.Id, (c, r) => r.StorageKey).FirstAsync());
        File.Delete(Path.Combine(Factory.StorageRoot, key!.Replace('/', Path.DirectorySeparatorChar)));

        var entries = ReadZip(await (await client.GetAsync($"/api/projects/{projectId}/clips/archive")).Content.ReadAsByteArrayAsync());
        entries.Keys.ShouldBe(["01-segundo.mp4"]);
    }
}
