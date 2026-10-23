"use client";

import { Link2, UploadCloud } from "lucide-react";
import { useState } from "react";
import { PageHeader } from "@/components/layout/page-header";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { LinkTab } from "@/features/new-project/link-tab";
import { UploadTab, type SelectedSource, type ShortSourceAdjustment } from "@/features/new-project/upload-tab";
import { adjustForShortSource, applyPlanLimits, buildDefaultSettings, isShortSource } from "@/features/projects/analysis-settings";
import { useMe } from "@/lib/api/hooks/use-auth";
import { useHydrated } from "@/lib/hooks/use-hydrated";
import type { AnalysisSettings } from "@/lib/api/types";

export function NewProjectPage() {
  const me = useMe();
  // The app shell may have fetched the session before this page hydrates: reading it only after
  // hydration keeps the first render identical to the server HTML (no React #418 on /new).
  const hydrated = useHydrated();
  const session = hydrated ? me.data : undefined;
  const [tab, setTab] = useState<"upload" | "link">("upload");
  const [settings, setSettings] = useState<AnalysisSettings | null>(null);
  // Set while the chosen recording is shorter than the clip minimum: the clip lengths were fitted
  // into it and `previous` is restored when the file is removed or replaced.
  const [shortSource, setShortSource] = useState<ShortSourceAdjustment | null>(null);

  // Defaults come from the profile's editing preferences (format, subtitles, clip lengths and the
  // language spoken in the recordings) once the session is known; after the first edit the form
  // keeps its own state. Either way the plan limits apply (e.g. a subtitle style the plan lacks).
  const limits = session?.limits;
  const planOptions = { shortSourceSeconds: shortSource?.durationSeconds ?? null };
  const normalize = (next: AnalysisSettings) => applyPlanLimits(next, limits, planOptions);
  const effectiveSettings = normalize(settings ?? buildDefaultSettings(session?.profile.editingPreferences, limits));

  /** The upload tab read (or dropped) a recording: fit the clip lengths to a short one, undo it afterwards. */
  const handleSourceChange = (source: SelectedSource | null) => {
    const base = shortSource
      ? applyPlanLimits(
          {
            ...effectiveSettings,
            minClipSeconds: shortSource.previous.minClipSeconds,
            maxClipSeconds: shortSource.previous.maxClipSeconds,
            targetClipSeconds: shortSource.previous.targetClipSeconds,
          },
          limits,
        )
      : effectiveSettings;
    if (source && isShortSource(source.durationSeconds, base.minClipSeconds)) {
      setShortSource({
        durationSeconds: source.durationSeconds,
        previous: { minClipSeconds: base.minClipSeconds, maxClipSeconds: base.maxClipSeconds, targetClipSeconds: base.targetClipSeconds },
      });
      setSettings(adjustForShortSource(base, source.durationSeconds));
      return;
    }
    if (shortSource) {
      setShortSource(null);
      setSettings(base);
    }
  };

  return (
    <>
      <PageHeader
        title="Novo projeto"
        description="Envie a gravação de uma live ou verifique um link. A análise encontra os melhores momentos e entrega os clipes prontos."
      />
      <Tabs value={tab} onValueChange={(value) => setTab(value as "upload" | "link")}>
        <TabsList aria-label="Fonte da gravação">
          <TabsTrigger value="upload">
            <UploadCloud aria-hidden="true" />
            Enviar gravação
          </TabsTrigger>
          <TabsTrigger value="link">
            <Link2 aria-hidden="true" />
            Link
          </TabsTrigger>
        </TabsList>
        <TabsContent value="upload">
          <UploadTab
            settings={effectiveSettings}
            onSettingsChange={setSettings}
            normalize={normalize}
            limits={limits}
            shortSource={shortSource}
            onSourceChange={handleSourceChange}
          />
        </TabsContent>
        <TabsContent value="link">
          <LinkTab
            settings={effectiveSettings}
            onSettingsChange={setSettings}
            limits={limits}
            onSwitchToUpload={() => setTab("upload")}
          />
        </TabsContent>
      </Tabs>
    </>
  );
}
