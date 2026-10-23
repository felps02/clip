using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

public class PlanCappingTests
{
    [Fact]
    public void Settings_are_capped_by_plan_limits()
    {
        var limits = new PlanLimits { MaxCandidatesPerJob = 15, MaxAutoRenderPerJob = 3, AllowWebcamLayout = false, AllowCustomSubtitleStyles = false };
        var settings = new AnalysisSettings
        {
            MaxCandidates = 50,
            AutoRenderTop = 20,
            Subtitles = new SubtitleSettings { Style = "boxed", Position = "top" },
            Framing = new FramingSettings { Mode = "webcam-split" },
            MinClipSeconds = 1,
            MaxClipSeconds = 999,
            TargetClipSeconds = 500,
            Sensitivity = 150,
            AspectRatio = "4:3",
        };

        var capped = PlanLimitHelpers.CapSettings(settings, limits);

        capped.MaxCandidates.ShouldBe(15);
        capped.AutoRenderTop.ShouldBe(3);
        capped.Subtitles.Style.ShouldBe("bold");
        capped.Subtitles.Position.ShouldBe("bottom");
        capped.Framing.Mode.ShouldBe("auto");
        capped.MinClipSeconds.ShouldBe(5);
        capped.MaxClipSeconds.ShouldBe(300);
        capped.TargetClipSeconds.ShouldBe(300);
        capped.Sensitivity.ShouldBe(100);
        capped.AspectRatio.ShouldBe("9:16");

        // original untouched
        settings.MaxCandidates.ShouldBe(50);
        settings.Framing.Mode.ShouldBe("webcam-split");
    }

    [Fact]
    public void Settings_within_limits_are_preserved()
    {
        var limits = new PlanLimits { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 10, AllowWebcamLayout = true, AllowCustomSubtitleStyles = true };
        var settings = new AnalysisSettings
        {
            MaxCandidates = 20,
            AutoRenderTop = 5,
            Subtitles = new SubtitleSettings { Style = "clean", Position = "center" },
            Framing = new FramingSettings { Mode = "webcam-split" },
            AspectRatio = "16:9",
        };
        var capped = PlanLimitHelpers.CapSettings(settings, limits);
        capped.MaxCandidates.ShouldBe(20);
        capped.AutoRenderTop.ShouldBe(5);
        capped.Subtitles.Style.ShouldBe("clean");
        capped.Subtitles.Position.ShouldBe("center");
        capped.Framing.Mode.ShouldBe("webcam-split");
        capped.AspectRatio.ShouldBe("16:9");
    }

    [Fact]
    public void AutoRenderTop_never_exceeds_MaxCandidates()
    {
        var limits = new PlanLimits { MaxCandidatesPerJob = 5, MaxAutoRenderPerJob = 10 };
        var capped = PlanLimitHelpers.CapSettings(new AnalysisSettings { MaxCandidates = 3, AutoRenderTop = 8 }, limits);
        capped.AutoRenderTop.ShouldBe(3);
    }

    [Fact]
    public void AutoRenderTop_not_sent_means_every_candidate_capped_by_the_plan()
    {
        // "Todos os momentos encontrados": the default follows maxCandidates, then the plan's maxAutoRenderPerJob.
        PlanLimitHelpers.CapSettings(new AnalysisSettings { MaxCandidates = 12 }, new PlanLimits { MaxCandidatesPerJob = 15, MaxAutoRenderPerJob = 15 })
            .AutoRenderTop.ShouldBe(12);
        PlanLimitHelpers.CapSettings(new AnalysisSettings { MaxCandidates = 99 }, new PlanLimits { MaxCandidatesPerJob = 50, MaxAutoRenderPerJob = 30 })
            .AutoRenderTop.ShouldBe(30);
        // an explicit value is kept (and still capped as before)
        PlanLimitHelpers.CapSettings(new AnalysisSettings { MaxCandidates = 20, AutoRenderTop = 3 }, new PlanLimits { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30 })
            .AutoRenderTop.ShouldBe(3);
    }

    [Fact]
    public void Focus_keywords_and_instructions_are_normalised()
    {
        var capped = PlanLimitHelpers.CapSettings(new AnalysisSettings
        {
            FocusCategories = ["Funny", "hype", "funny", "unknown"],
            CustomKeywords = ["  Clutch ", "clutch", "que   isso", "", new string('x', 41)],
            Instructions = "   " + new string('a', 600) + "   ",
        }, new PlanLimits());
        capped.FocusCategories.ShouldBe(["funny", "hype"]);
        capped.CustomKeywords.ShouldBe(["Clutch", "que isso"]);
        capped.Instructions!.Length.ShouldBe(500);
        PlanLimitHelpers.CapSettings(new AnalysisSettings { Instructions = "  " }, new PlanLimits()).Instructions.ShouldBeNull();
        PlanLimitHelpers.CapSettings(new AnalysisSettings { FocusCategories = null!, CustomKeywords = null! }, new PlanLimits()).FocusCategories.ShouldBeEmpty();
    }

    [Fact]
    public void Framing_focus_is_always_explicit_in_capped_settings()
    {
        var capped = PlanLimitHelpers.CapSettings(new AnalysisSettings(), new PlanLimits());
        capped.Framing.FocusX.ShouldBe(0.5);
        capped.Framing.FocusY.ShouldBe(0.5);

        var custom = PlanLimitHelpers.CapSettings(new AnalysisSettings { Framing = new FramingSettings { Mode = "center", FocusX = 1.7, FocusY = -2 } }, new PlanLimits());
        custom.Framing.Mode.ShouldBe("center");
        custom.Framing.FocusX.ShouldBe(1);
        custom.Framing.FocusY.ShouldBe(0);
    }

    [Fact]
    public void Clip_framing_and_subtitles_are_capped()
    {
        var limits = new PlanLimits { AllowWebcamLayout = false, AllowCustomSubtitleStyles = false };
        var framing = PlanLimitHelpers.CapFraming(new FramingSettings { Mode = "webcam-split", FocusX = 2, WebcamRegion = new WebcamRegion { X = 0.9, Y = 0.9, W = 0.5, H = 0.5 } }, limits);
        framing.Mode.ShouldBe("auto");
        framing.FocusX.ShouldBe(1);
        framing.WebcamRegion.ShouldBeNull();

        var subs = PlanLimitHelpers.CapSubtitles(new SubtitleSettings { Enabled = false, Style = "boxed" }, limits);
        subs.Enabled.ShouldBeFalse();
        subs.Style.ShouldBe("bold");
    }
}
