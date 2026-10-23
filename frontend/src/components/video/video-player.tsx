"use client";

import { VideoOff } from "lucide-react";
import { forwardRef, useImperativeHandle, useRef, type SyntheticEvent } from "react";
import type { AspectRatio } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export interface VideoPlayerHandle {
  getCurrentTime: () => number;
  seek: (seconds: number) => void;
  play: () => void;
  pause: () => void;
}

export interface VideoPlayerProps {
  src: string | null | undefined;
  poster?: string | null;
  /**
   * Media fragment appended to the URL (`#t=start,end`). Kept stable so the element does not
   * reload while the user drags trim controls; use `clampEnd` for the live boundary.
   */
  fragment?: { start: number; end?: number } | null;
  /** Pause automatically when playback reaches this absolute time. */
  clampEnd?: number | null;
  aspectRatio?: AspectRatio | "auto";
  autoPlay?: boolean;
  muted?: boolean;
  onTimeUpdate?: (seconds: number) => void;
  className?: string;
  emptyMessage?: string;
}

export function withMediaFragment(src: string, fragment?: { start: number; end?: number } | null): string {
  if (!fragment) return src;
  const start = Math.max(0, Math.round(fragment.start * 1000) / 1000);
  const end = fragment.end !== undefined ? Math.round(fragment.end * 1000) / 1000 : undefined;
  const value = end !== undefined && end > start ? `${start},${end}` : `${start}`;
  return `${src.split("#")[0]}#t=${value}`;
}

/**
 * The frame follows the clip: 16:9 fills the width; 9:16 and 1:1 are capped in width so that the
 * height stays within 70% of the viewport (tall and narrow, or square), with no black bars around.
 */
const frameClass: Record<AspectRatio | "auto", string> = {
  "16:9": "w-full",
  auto: "w-full",
  "9:16": "mx-auto w-full max-w-[calc(70dvh*9/16)]",
  "1:1": "mx-auto w-full max-w-[70dvh]",
};

const ratioClass: Record<AspectRatio | "auto", string> = {
  "16:9": "aspect-video",
  auto: "aspect-video",
  "9:16": "aspect-[9/16]",
  "1:1": "aspect-square",
};

export const VideoPlayer = forwardRef<VideoPlayerHandle, VideoPlayerProps>(function VideoPlayer(
  { src, poster, fragment, clampEnd, aspectRatio = "auto", autoPlay, muted, onTimeUpdate, className, emptyMessage },
  ref,
) {
  const videoRef = useRef<HTMLVideoElement | null>(null);

  useImperativeHandle(
    ref,
    () => ({
      getCurrentTime: () => videoRef.current?.currentTime ?? 0,
      seek: (seconds: number) => {
        const video = videoRef.current;
        if (!video) return;
        video.currentTime = Math.max(0, seconds);
      },
      play: () => {
        void videoRef.current?.play().catch(() => undefined);
      },
      pause: () => videoRef.current?.pause(),
    }),
    [],
  );

  const handleTimeUpdate = (event: SyntheticEvent<HTMLVideoElement>) => {
    const video = event.currentTarget;
    onTimeUpdate?.(video.currentTime);
    if (clampEnd !== null && clampEnd !== undefined && video.currentTime >= clampEnd - 0.05 && !video.paused) {
      video.pause();
    }
  };

  const resolvedSrc = src ? withMediaFragment(src, fragment) : null;

  return (
    <div
      className={cn("overflow-hidden rounded-lg border border-line bg-black", frameClass[aspectRatio], className)}
      data-aspect-ratio={aspectRatio}
    >
      <div className={cn("relative w-full", ratioClass[aspectRatio])}>
        {resolvedSrc ? (
          <video
            key={resolvedSrc}
            ref={videoRef}
            className="absolute inset-0 h-full w-full object-contain"
            src={resolvedSrc}
            poster={poster ?? undefined}
            controls
            playsInline
            preload="metadata"
            autoPlay={autoPlay}
            muted={muted}
            onTimeUpdate={handleTimeUpdate}
          >
            Seu navegador não suporta a reprodução deste vídeo.
          </video>
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-elevated text-center text-sm text-fg-muted">
            <VideoOff className="size-6 text-fg-subtle" aria-hidden="true" />
            <p className="max-w-xs px-4">{emptyMessage ?? "Pré-visualização indisponível."}</p>
          </div>
        )}
      </div>
    </div>
  );
});
