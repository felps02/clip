using ClipForge.Api.Features.Assistant;
using ClipForge.Api.Features.Projects;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

/// <summary>Validation and plan capping of what the assistant was asked to change.</summary>
public class AssistantPatchBuilderTests
{
    private static readonly PlanLimits Free = new()
    {
        MaxCandidatesPerJob = 15, MaxAutoRenderPerJob = 15, AllowCustomSubtitleStyles = false, AllowWebcamLayout = false,
    };

    private static readonly PlanLimits Creator = new()
    {
        MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30, AllowCustomSubtitleStyles = true, AllowWebcamLayout = true,
    };

    private static AnalysisSettings Current(PlanLimits limits, Action<AnalysisSettings>? change = null)
    {
        var settings = new AnalysisSettings { AspectRatio = "16:9", MinClipSeconds = 15, TargetClipSeconds = 45, MaxClipSeconds = 60 };
        change?.Invoke(settings);
        return PlanLimitHelpers.CapSettings(settings, limits);
    }

    private static AssistantOutcome Apply(RequestedChanges requested, PlanLimits? limits = null, Action<AnalysisSettings>? change = null, decimal? sourceSeconds = null)
    {
        limits ??= Creator;
        return AssistantPatchBuilder.Apply(Current(limits, change), requested, limits, sourceSeconds);
    }

    [Fact]
    public void An_explicit_target_becomes_a_range_around_it()
    {
        var outcome = Apply(new RequestedChanges { TargetClipSeconds = 30 });
        outcome.Patch.MinClipSeconds.ShouldBe(20);
        outcome.Patch.TargetClipSeconds.ShouldBe(30);
        outcome.Patch.MaxClipSeconds.ShouldBe(40);
        var change = outcome.Changes.ShouldHaveSingleItem();
        change.Field.ShouldBe("targetClipSeconds");
        change.Label.ShouldBe("Duração: 20–40 s (alvo 30 s)");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(121)]
    [InlineData(300)]
    public void Lengths_outside_5_to_120_seconds_are_refused(int seconds)
    {
        var outcome = Apply(new RequestedChanges { TargetClipSeconds = seconds });
        outcome.Patch.IsEmpty.ShouldBeTrue();
        outcome.Changes.ShouldBeEmpty();
        outcome.Notes.ShouldHaveSingleItem().ShouldContain("entre 5 e 120 segundos");
    }

    [Fact]
    public void Lengths_always_keep_min_le_target_le_max()
    {
        var current = Current(Creator);
        AssistantPatchBuilder.ResolveDurations(current, 70, null, null).ShouldBe((70, 70, 93));   // min above the current max widens it
        AssistantPatchBuilder.ResolveDurations(current, null, null, 10).ShouldBe((7, 10, 10));    // max below the current min narrows it
        AssistantPatchBuilder.ResolveDurations(current, 40, null, 20).ShouldBe((20, 40, 40));     // swapped bounds, target clamped
        AssistantPatchBuilder.ResolveDurations(current, 10, 15, 25).ShouldBe((10, 15, 25));
        AssistantPatchBuilder.ResolveDurations(current, null, 120, null).ShouldBe((80, 120, 120));
    }

    [Fact]
    public void Plan_caps_moments_and_clips_and_says_so()
    {
        var outcome = Apply(new RequestedChanges { MaxCandidates = 40, AutoRenderTop = 40 }, Free);
        outcome.Proposed.MaxCandidates.ShouldBe(15);
        outcome.Proposed.AutoRenderTop.ShouldBe(15);
        outcome.Notes.ShouldContain(n => n.Contains("até 15 momentos"));
        outcome.Notes.ShouldContain(n => n.Contains("até 15 clipes automáticos"));
    }

    [Fact]
    public void More_clips_than_moments_raises_the_moments_within_the_plan()
    {
        var outcome = Apply(new RequestedChanges { AutoRenderTop = 25 }, Creator, s => { s.MaxCandidates = 10; s.AutoRenderTop = 10; });
        outcome.Patch.MaxCandidates.ShouldBe(25);
        outcome.Patch.AutoRenderTop.ShouldBe(25);
    }

    [Fact]
    public void Every_moment_becomes_a_clip()
    {
        var outcome = Apply(new RequestedChanges { AutoRenderAll = true }, Creator, s => { s.MaxCandidates = 20; s.AutoRenderTop = 5; });
        outcome.Patch.AutoRenderTop.ShouldBe(20);
        outcome.Changes.ShouldHaveSingleItem().Label.ShouldBe("Clipes automáticos: todos os momentos encontrados");
    }

    [Fact]
    public void Custom_subtitle_placement_needs_the_plan()
    {
        var free = Apply(new RequestedChanges { SubtitlePosition = "center", SubtitleStyle = "clean" }, Free);
        free.Patch.Subtitles.ShouldBeNull();
        free.Changes.ShouldBeEmpty();
        free.Notes.ShouldHaveSingleItem().ShouldContain("não estão no seu plano");

        var creator = Apply(new RequestedChanges { SubtitlePosition = "center", SubtitleStyle = "clean" }, Creator);
        creator.Patch.Subtitles!.Position.ShouldBe("center");
        creator.Patch.Subtitles.Style.ShouldBe("clean");
        creator.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void Placing_subtitles_turns_them_on()
    {
        var outcome = Apply(new RequestedChanges { SubtitlePosition = "top" }, Creator, s => s.Subtitles.Enabled = false);
        outcome.Patch.Subtitles!.Enabled.ShouldBe(true);
        outcome.Patch.Subtitles.Position.ShouldBe("top");
    }

    [Fact]
    public void Webcam_layout_needs_the_plan()
    {
        var free = Apply(new RequestedChanges { FramingMode = "webcam-split" }, Free);
        free.Patch.Framing.ShouldBeNull();
        free.Notes.ShouldHaveSingleItem().ShouldContain("webcam");

        Apply(new RequestedChanges { FramingMode = "webcam-split" }, Creator).Patch.Framing!.Mode.ShouldBe("webcam-split");
    }

    [Fact]
    public void Values_already_set_are_not_changes()
    {
        var outcome = Apply(new RequestedChanges { AspectRatio = "16:9", FocusCategories = ["funny"] });
        outcome.Patch.AspectRatio.ShouldBeNull();
        outcome.Patch.FocusCategories.ShouldBe(["funny"]);
        outcome.Changes.Select(c => c.Field).ShouldBe(["focusCategories"]);
        outcome.Already.ShouldBe(["formato 16:9 (horizontal)"]);
    }

    [Fact]
    public void Keywords_are_added_trimmed_and_deduplicated()
    {
        var requested = new RequestedChanges();
        requested.AddKeyword("  Clutch ");
        requested.AddKeyword("que   isso");
        var outcome = Apply(requested, Creator, s => s.CustomKeywords = ["clutch"]);
        outcome.Patch.CustomKeywords.ShouldBe(["clutch", "que isso"]);
    }

    [Fact]
    public void Keywords_over_the_limits_are_explained()
    {
        var requested = new RequestedChanges();
        requested.AddKeyword(new string('x', 41));
        for (var i = 0; i < 25; i++) requested.AddKeyword("palavra" + i);
        var outcome = Apply(requested);
        outcome.Patch.CustomKeywords!.Count.ShouldBe(20);
        outcome.Notes.ShouldContain(n => n.Contains("40 caracteres"));
        outcome.Notes.ShouldContain(n => n.Contains("20 palavras-chave"));
    }

    [Fact]
    public void The_result_always_passes_the_project_settings_validator()
    {
        var requests = new[]
        {
            new RequestedChanges { MinClipSeconds = 5, MaxClipSeconds = 120 },
            new RequestedChanges { TargetClipSeconds = 5 },
            new RequestedChanges { MaxCandidates = 100, AutoRenderTop = 100, Sensitivity = 100 },
            new RequestedChanges { Language = "es", FocusCategories = ["quote", "hype", "quote"], SubtitlesEnabled = false },
        };
        foreach (var limits in new[] { Free, Creator })
        {
            foreach (var requested in requests)
            {
                var outcome = AssistantPatchBuilder.Apply(Current(limits), requested, limits, null);
                new AnalysisSettingsValidator().Validate(outcome.Proposed).IsValid.ShouldBeTrue();
                outcome.Proposed.MinClipSeconds.ShouldBeLessThanOrEqualTo(outcome.Proposed.TargetClipSeconds);
                outcome.Proposed.TargetClipSeconds.ShouldBeLessThanOrEqualTo(outcome.Proposed.MaxClipSeconds);
                outcome.Proposed.MaxCandidates.ShouldBeLessThanOrEqualTo(limits.MaxCandidatesPerJob);
                outcome.Proposed.AutoRenderTop!.Value.ShouldBeLessThanOrEqualTo(Math.Min(limits.MaxAutoRenderPerJob, outcome.Proposed.MaxCandidates));
            }
        }
    }

    [Fact]
    public void A_source_shorter_than_the_minimum_is_mentioned()
    {
        var outcome = Apply(new RequestedChanges { TargetClipSeconds = 60 }, Creator, sourceSeconds: 12.7m);
        outcome.Notes.ShouldContain("O vídeo tem 12,7 s, menos que a duração mínima dos clipes (40 s).");
    }
}
