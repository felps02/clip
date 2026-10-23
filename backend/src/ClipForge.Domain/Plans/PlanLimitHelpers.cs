using System.Text.RegularExpressions;
using ClipForge.Domain.Common;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Domain.Plans;

/// <summary>Pure plan-limit helpers (no I/O) so they can be unit tested.</summary>
public static partial class PlanLimitHelpers
{
    public static bool IsUnlimited(int limit) => limit <= 0;

    /// <summary>A language code the worker can use: "auto" or letters/dashes (e.g. "pt", "pt-BR"), at most 10 characters.</summary>
    [GeneratedRegex("^[A-Za-z]{1,8}(-[A-Za-z]{1,8})?$")]
    private static partial Regex LanguageCode();

    public static bool IsLanguageCode(string? value) => value is { Length: <= 10 } && LanguageCode().IsMatch(value);

    /// <summary>Returns a copy of <paramref name="settings"/> capped by the plan limits.</summary>
    public static AnalysisSettings CapSettings(AnalysisSettings settings, PlanLimits limits)
    {
        var capped = settings.Clone();

        capped.MinClipSeconds = Math.Clamp(capped.MinClipSeconds, AnalysisSettings.ClipSecondsMin, AnalysisSettings.ClipSecondsMax);
        capped.MaxClipSeconds = Math.Clamp(capped.MaxClipSeconds, capped.MinClipSeconds, AnalysisSettings.ClipSecondsMax);
        capped.TargetClipSeconds = Math.Clamp(capped.TargetClipSeconds, capped.MinClipSeconds, capped.MaxClipSeconds);
        capped.Sensitivity = Math.Clamp(capped.Sensitivity, 0, 100);

        capped.MaxCandidates = Math.Max(1, Math.Min(capped.MaxCandidates, Math.Max(1, limits.MaxCandidatesPerJob)));
        // Not sent = every candidate found becomes a clip ("todos os momentos encontrados"), still capped by the plan.
        var autoRender = capped.AutoRenderTop ?? capped.MaxCandidates;
        autoRender = Math.Max(0, Math.Min(autoRender, Math.Max(0, limits.MaxAutoRenderPerJob)));
        capped.AutoRenderTop = Math.Min(autoRender, capped.MaxCandidates);

        if (!AspectRatios.IsValid(capped.AspectRatio)) capped.AspectRatio = AspectRatios.Vertical;

        capped.Subtitles ??= new SubtitleSettings();
        if (!limits.AllowCustomSubtitleStyles)
        {
            capped.Subtitles.Style = SubtitleStyles.Bold;
            capped.Subtitles.Position = "bottom";
        }
        else
        {
            if (Array.IndexOf(SubtitleStyles.All, capped.Subtitles.Style) < 0) capped.Subtitles.Style = SubtitleStyles.Bold;
            if (Array.IndexOf(SubtitleStyles.Positions, capped.Subtitles.Position) < 0) capped.Subtitles.Position = "bottom";
        }

        capped.Framing ??= new FramingSettings();
        if (Array.IndexOf(FramingModes.All, capped.Framing.Mode) < 0) capped.Framing.Mode = FramingModes.Auto;
        if (!limits.AllowWebcamLayout && capped.Framing.Mode == FramingModes.WebcamSplit)
        {
            capped.Framing.Mode = FramingModes.Auto;
        }
        // The focus point is always explicit (the worker's payload model expects numbers, never null).
        capped.Framing.FocusX = Math.Clamp(capped.Framing.FocusX ?? 0.5, 0, 1);
        capped.Framing.FocusY = Math.Clamp(capped.Framing.FocusY ?? 0.5, 0, 1);
        if (capped.Framing.WebcamRegion is { } region && !region.IsValid()) capped.Framing.WebcamRegion = null;

        // Whatever reaches the worker payload is a plain language code (defence in depth: the request validators and
        // the profile preferences already restrict it).
        capped.Language = IsLanguageCode(capped.Language?.Trim()) ? capped.Language!.Trim() : "auto";

        // Focus, keywords and instructions reach the worker normalised (the request validators reject invalid values;
        // this keeps older or internal documents inside the contract too).
        capped.FocusCategories = AnalysisSettings.NormalizeFocusCategories(capped.FocusCategories);
        capped.CustomKeywords = AnalysisSettings.NormalizeCustomKeywords(capped.CustomKeywords);
        capped.Instructions = AnalysisSettings.NormalizeInstructions(capped.Instructions);

        return capped;
    }

    /// <summary>Caps clip-level framing by the plan (webcam layout).</summary>
    public static FramingSettings CapFraming(FramingSettings framing, PlanLimits limits)
    {
        var capped = framing.Clone();
        if (Array.IndexOf(FramingModes.All, capped.Mode) < 0) capped.Mode = FramingModes.Auto;
        if (!limits.AllowWebcamLayout && capped.Mode == FramingModes.WebcamSplit) capped.Mode = FramingModes.Auto;
        if (capped.FocusX is { } fx) capped.FocusX = Math.Clamp(fx, 0, 1);
        if (capped.FocusY is { } fy) capped.FocusY = Math.Clamp(fy, 0, 1);
        if (capped.WebcamRegion is { } region && !region.IsValid()) capped.WebcamRegion = null;
        return capped;
    }

    public static SubtitleSettings CapSubtitles(SubtitleSettings subtitles, PlanLimits limits)
    {
        var capped = subtitles.Clone();
        if (!limits.AllowCustomSubtitleStyles)
        {
            capped.Style = SubtitleStyles.Bold;
            capped.Position = "bottom";
        }
        else
        {
            if (Array.IndexOf(SubtitleStyles.All, capped.Style) < 0) capped.Style = SubtitleStyles.Bold;
            if (Array.IndexOf(SubtitleStyles.Positions, capped.Position) < 0) capped.Position = "bottom";
        }
        return capped;
    }
}

/// <summary>Result of a quota / plan-limit evaluation.</summary>
public sealed record QuotaDecision(bool Allowed, string? Code, string? Message, int Status = 409)
{
    public static readonly QuotaDecision Ok = new(true, null, null);

    public static QuotaDecision PlanLimit(string message, int status = 409) => new(false, ErrorCodes.PlanLimit, message, status);
    public static QuotaDecision Exceeded(string message) => new(false, ErrorCodes.QuotaExceeded, message);

    public void ThrowIfDenied()
    {
        if (!Allowed) throw new AppException(Code!, Status, Message!);
    }
}

/// <summary>Pure quota rules (docs/PLANS_AND_BILLING.md). The QuotaService in the API feeds these with database counts.</summary>
public static class QuotaCalculator
{
    public static QuotaDecision CheckActiveProjects(int activeProjects, PlanLimits limits)
    {
        if (PlanLimitHelpers.IsUnlimited(limits.MaxActiveProjects)) return QuotaDecision.Ok;
        return activeProjects >= limits.MaxActiveProjects
            ? QuotaDecision.PlanLimit($"Limite de projetos ativos do plano atingido ({activeProjects} de {limits.MaxActiveProjects}). Exclua um projeto ou faça upgrade do plano.")
            : QuotaDecision.Ok;
    }

    public static QuotaDecision CheckConcurrentJobs(int activeJobs, PlanLimits limits)
    {
        var max = Math.Max(1, limits.MaxConcurrentJobs);
        return activeJobs >= max
            ? QuotaDecision.PlanLimit($"Limite de processamentos simultâneos do plano atingido ({activeJobs} de {max}). Aguarde a conclusão do processamento atual.")
            : QuotaDecision.Ok;
    }

    public static QuotaDecision CheckSourceDuration(decimal durationSeconds, PlanLimits limits)
    {
        var maxSeconds = limits.MaxSourceDurationSeconds;
        if (maxSeconds <= 0) return QuotaDecision.Ok;
        if (durationSeconds > maxSeconds)
        {
            var minutes = Math.Round(durationSeconds / 60m, 1);
            return QuotaLimitExceeded(
                $"A duração do arquivo ({minutes} min) excede o máximo do plano ({limits.MaxSourceDurationMinutes} min por arquivo).");
        }
        return QuotaDecision.Ok;
    }

    private static QuotaDecision QuotaLimitExceeded(string message) => QuotaDecision.PlanLimit(message);

    /// <summary>used + reserved + requested ≤ monthly limit.</summary>
    public static QuotaDecision CheckProcessingSeconds(decimal usedSeconds, decimal reservedSeconds, decimal requestedSeconds, PlanLimits limits)
    {
        var limit = limits.MonthlyProcessingSeconds;
        if (limit <= 0) return QuotaDecision.Ok;
        var total = usedSeconds + reservedSeconds + requestedSeconds;
        if (total > limit)
        {
            var remaining = Math.Max(0, limit - usedSeconds - reservedSeconds);
            return QuotaDecision.Exceeded(
                $"Minutos de processamento insuficientes no período: restam {Math.Floor(remaining / 60m)} min de {limits.MonthlyProcessingMinutes} e este arquivo precisa de {Math.Ceiling(requestedSeconds / 60m)} min.");
        }
        return QuotaDecision.Ok;
    }

    public static long RemainingProcessingSeconds(decimal usedSeconds, decimal reservedSeconds, PlanLimits limits)
    {
        var limit = limits.MonthlyProcessingSeconds;
        if (limit <= 0) return long.MaxValue;
        return (long)Math.Max(0, Math.Floor(limit - usedSeconds - reservedSeconds));
    }

    public static QuotaDecision CheckRenders(int rendersUsed, PlanLimits limits)
    {
        if (PlanLimitHelpers.IsUnlimited(limits.MaxRendersPerMonth)) return QuotaDecision.Ok;
        return rendersUsed >= limits.MaxRendersPerMonth
            ? QuotaDecision.PlanLimit($"Limite de renderizações do plano atingido no período ({rendersUsed} de {limits.MaxRendersPerMonth}).")
            : QuotaDecision.Ok;
    }

    public static QuotaDecision CheckUpload(long sizeBytes, long storageUsedBytes, PlanLimits limits)
    {
        if (sizeBytes <= 0) return new QuotaDecision(false, ErrorCodes.ValidationFailed, "Tamanho do arquivo inválido.", 400);
        if (limits.MaxFileSizeBytes > 0 && sizeBytes > limits.MaxFileSizeBytes)
        {
            return QuotaDecision.PlanLimit(
                $"O arquivo ({Math.Round(sizeBytes / 1048576.0)} MB) excede o tamanho máximo do plano ({limits.MaxFileSizeMb} MB).", 413);
        }
        if (limits.StorageQuotaBytes > 0 && storageUsedBytes + sizeBytes > limits.StorageQuotaBytes)
        {
            return QuotaDecision.Exceeded(
                $"Cota de armazenamento do plano excedida ({Math.Round(storageUsedBytes / 1073741824.0, 2)} GB usados de {limits.StorageQuotaGb} GB).");
        }
        return QuotaDecision.Ok;
    }
}
