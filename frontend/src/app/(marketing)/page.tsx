import { Suspense } from "react";
import { PlansSection, PlansSkeletonGrid } from "@/components/marketing/plans-section";
import { Benefits, CtaSection, Faq, Hero, HowItWorks, SectionHeading } from "@/components/marketing/sections";

export default function LandingPage() {
  return (
    <>
      <Hero />
      <Benefits />
      <HowItWorks />
      <section id="planos" className="mx-auto max-w-7xl scroll-mt-28 px-4 py-16 sm:px-6 md:scroll-mt-20 lg:px-8 lg:py-24" aria-labelledby="planos-titulo">
        <SectionHeading
          id="planos-titulo"
          eyebrow="Planos"
          title="Comece grátis e cresça conforme as lives"
          description="Minutos de processamento por mês, limites de duração e renderizações por plano. Sem surpresas."
        />
        <Suspense fallback={<PlansSkeletonGrid />}>
          <PlansSection />
        </Suspense>
      </section>
      <Faq />
      <CtaSection />
    </>
  );
}
