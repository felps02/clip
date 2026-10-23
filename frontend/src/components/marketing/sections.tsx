import {
  Captions,
  Clock3,
  LayoutTemplate,
  ListChecks,
  ScanSearch,
  SlidersHorizontal,
  UploadCloud,
  Download,
} from "lucide-react";
import Link from "next/link";
import { FlowDemo } from "@/components/marketing/flow-demo";
import { buttonVariants } from "@/components/ui/button";
import { brand } from "@/config/brand";
import { cn } from "@/lib/utils";

export function SectionHeading({ eyebrow, title, description, id }: { eyebrow: string; title: string; description?: string; id?: string }) {
  return (
    <div className="mx-auto mb-10 max-w-2xl text-center">
      <p className="text-xs font-medium uppercase tracking-widest text-accent">{eyebrow}</p>
      <h2 id={id} className="mt-2 text-3xl font-semibold tracking-tight text-balance sm:text-4xl">
        {title}
      </h2>
      {description ? <p className="mt-3 text-base text-fg-muted text-balance">{description}</p> : null}
    </div>
  );
}

export function Hero() {
  return (
    <section className="relative overflow-hidden">
      <div className="pointer-events-none absolute inset-0 surface-grid opacity-40 [mask-image:radial-gradient(70%_60%_at_50%_0%,black,transparent)]" />
      <div className="relative mx-auto grid max-w-7xl gap-12 px-4 pb-16 pt-14 sm:px-6 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)] lg:items-center lg:px-8 lg:pb-24 lg:pt-20">
        <div>
          <p className="inline-flex items-center gap-2 rounded-full border border-line bg-surface px-3 py-1 text-xs text-fg-muted">
            <span className="size-1.5 rounded-full bg-accent" aria-hidden="true" />
            Para streamers e criadores de conteúdo
          </p>
          <h1 className="mt-5 text-4xl font-semibold leading-[1.1] tracking-tight text-balance sm:text-5xl lg:text-6xl">
            {brand.tagline}
          </h1>
          <p className="mt-6 max-w-xl text-lg text-fg-muted">{brand.shortDescription}</p>
          <div className="mt-8 flex flex-wrap items-center gap-3">
            <Link href="/register" className={buttonVariants({ size: "lg" })}>
              Criar conta grátis
            </Link>
            <Link href="#como-funciona" className={buttonVariants({ variant: "secondary", size: "lg" })}>
              Ver como funciona
            </Link>
          </div>
          <p className="mt-4 text-xs text-fg-subtle">
            Sem cartão de crédito no plano gratuito. Você mantém o controle: nada é publicado automaticamente.
          </p>
        </div>
        <FlowDemo />
      </div>
    </section>
  );
}

const BENEFITS = [
  {
    icon: Clock3,
    title: "Economize horas de revisão",
    description:
      "Em vez de assistir à live inteira, você recebe uma lista de momentos candidatos ordenados por relevância estimada.",
  },
  {
    icon: Captions,
    title: "Legendas sincronizadas",
    description: "Transcrição com marcação por palavra e legendas queimadas no vídeo, em até três estilos conforme o plano (destaque, limpo e caixa).",
  },
  {
    icon: LayoutTemplate,
    title: "Formatos para cada rede",
    description: "Renderize em 9:16, 16:9 ou 1:1, com enquadramento centralizado ou webcam + gameplay quando houver webcam e o plano incluir.",
  },
  {
    icon: SlidersHorizontal,
    title: "Você decide o corte final",
    description: "Ajuste início e fim, título, legendas e formato no editor e renderize novamente quantas vezes o plano permitir.",
  },
];

export function Benefits() {
  return (
    <section className="mx-auto max-w-7xl px-4 py-16 sm:px-6 lg:px-8 lg:py-24" aria-labelledby="beneficios">
      <SectionHeading
        id="beneficios"
        eyebrow="Benefícios"
        title="Menos tempo editando, mais tempo criando"
        description="Ferramentas pensadas para quem faz lives longas e precisa de cortes rápidos sem perder o controle do resultado."
      />
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {BENEFITS.map((benefit) => {
          const Icon = benefit.icon;
          return (
            <article key={benefit.title} className="rounded-lg border border-line bg-surface p-6 shadow-card">
              <div className="flex size-10 items-center justify-center rounded-md border border-line bg-elevated text-accent">
                <Icon className="size-5" aria-hidden="true" />
              </div>
              <h3 className="mt-4 text-base font-semibold">{benefit.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-fg-muted">{benefit.description}</p>
            </article>
          );
        })}
      </div>
    </section>
  );
}

const STEPS = [
  {
    icon: UploadCloud,
    title: "Envie a gravação",
    description: "Faça upload do arquivo da live (MP4, MOV, MKV, WebM ou áudio). O envio mostra progresso e pode ser cancelado.",
  },
  {
    icon: ScanSearch,
    title: "A análise encontra os momentos",
    description:
      "Energia do áudio, transcrição da fala e atividade visual são combinadas para apontar os trechos mais intensos.",
  },
  {
    icon: ListChecks,
    title: "Revise os candidatos",
    description: "Cada candidato mostra o motivo da escolha, a transcrição e uma prévia. Gere clipes só do que fizer sentido.",
  },
  {
    icon: Download,
    title: "Ajuste e baixe",
    description: "Refine o corte no editor, escolha o formato e as legendas e baixe o arquivo pronto para publicar onde quiser.",
  },
];

export function HowItWorks() {
  return (
    <section id="como-funciona" className="scroll-mt-28 border-y border-line bg-surface/40 md:scroll-mt-20" aria-labelledby="como-funciona-titulo">
      <div className="mx-auto max-w-7xl px-4 py-16 sm:px-6 lg:px-8 lg:py-24">
        <SectionHeading id="como-funciona-titulo" eyebrow="Como funciona" title="Quatro passos, nenhum deles é assistir tudo de novo" />
        <ol className="grid gap-6 md:grid-cols-2 lg:grid-cols-4">
          {STEPS.map((step, index) => {
            const Icon = step.icon;
            return (
              <li key={step.title} className="relative rounded-lg border border-line bg-canvas p-6">
                <span className="absolute right-5 top-5 font-mono text-xs text-fg-subtle">0{index + 1}</span>
                <div className="flex size-10 items-center justify-center rounded-md bg-accent/10 text-accent">
                  <Icon className="size-5" aria-hidden="true" />
                </div>
                <h3 className="mt-4 text-base font-semibold">{step.title}</h3>
                <p className="mt-2 text-sm leading-relaxed text-fg-muted">{step.description}</p>
              </li>
            );
          })}
        </ol>
      </div>
    </section>
  );
}

const FAQ = [
  {
    question: "Preciso assistir à live inteira para escolher os clipes?",
    answer:
      "Não. A análise percorre a gravação e lista os trechos candidatos com duração, motivo e transcrição. Você revisa só a lista e decide o que vira clipe.",
  },
  {
    question: "Como os momentos são escolhidos?",
    answer:
      "Combinamos sinais de áudio (picos de energia, gritos, risadas), de fala (transcrição, palavras-chave, ritmo) e de imagem (movimento, cortes de cena). Cada candidato mostra quais sinais contribuíram.",
  },
  {
    question: "O que significa a relevância estimada?",
    answer:
      "É uma pontuação interna de 0 a 100 que ordena os candidatos dentro da mesma gravação. Não é uma previsão de visualizações ou de viralização.",
  },
  {
    question: "Posso processar um link da Twitch ou do YouTube?",
    answer:
      "Hoje não. As plataformas não permitem baixar VODs de terceiros e nós não fazemos scraping. Envie o arquivo da gravação ou use o companion para OBS quando estiver disponível.",
  },
  {
    question: "Quais formatos de arquivo são aceitos?",
    answer: "MP4, MOV, MKV e WebM para vídeo; MP3 e WAV para áudio. O tamanho e a duração máximos dependem do plano.",
  },
  {
    question: "Por quanto tempo os arquivos ficam armazenados?",
    answer:
      "Cada plano tem um período de retenção. Depois dele, gravações e clipes são removidos automaticamente. Você também pode excluir projetos e a conta a qualquer momento.",
  },
  {
    question: "Posso cancelar quando quiser?",
    answer: "Sim. O cancelamento vale ao fim do período já pago e sua conta volta para o plano gratuito.",
  },
];

export function Faq() {
  return (
    <section id="faq" className="mx-auto max-w-3xl scroll-mt-28 px-4 py-16 sm:px-6 md:scroll-mt-20 lg:px-8 lg:py-24" aria-labelledby="faq-titulo">
      <SectionHeading id="faq-titulo" eyebrow="Perguntas frequentes" title="Dúvidas comuns" />
      <div className="divide-y divide-line rounded-lg border border-line bg-surface">
        {FAQ.map((item) => (
          <details key={item.question} className="group px-5 py-4 open:bg-elevated/40">
            <summary className="flex cursor-pointer list-none items-center justify-between gap-4 text-sm font-medium text-fg [&::-webkit-details-marker]:hidden">
              {item.question}
              <span className="text-fg-subtle transition-transform duration-200 group-open:rotate-45" aria-hidden="true">
                +
              </span>
            </summary>
            <p className="mt-3 text-sm leading-relaxed text-fg-muted">{item.answer}</p>
          </details>
        ))}
      </div>
    </section>
  );
}

export function CtaSection() {
  return (
    <section className="mx-auto max-w-7xl px-4 pb-20 sm:px-6 lg:px-8">
      <div className="relative overflow-hidden rounded-xl border border-accent/30 bg-[radial-gradient(80%_120%_at_20%_0%,rgba(179,245,59,0.16),transparent),#14171c] px-6 py-14 text-center sm:px-12">
        <h2 className="text-3xl font-semibold tracking-tight text-balance sm:text-4xl">Sua próxima live já pode virar clipes</h2>
        <p className="mx-auto mt-3 max-w-xl text-fg-muted">
          Crie uma conta, envie uma gravação e veja os candidatos aparecerem com os motivos de cada escolha.
        </p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <Link href="/register" className={buttonVariants({ size: "lg" })}>
            Criar conta grátis
          </Link>
          <Link href="/login" className={cn(buttonVariants({ variant: "ghost", size: "lg" }))}>
            Já tenho conta
          </Link>
        </div>
      </div>
    </section>
  );
}
