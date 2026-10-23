import type { Metadata } from "next";
import Link from "next/link";
import { LegalUnavailable } from "@/components/marketing/legal-unavailable";
import { brand, resolveLegal } from "@/config/brand";
import { formatCalendarDate } from "@/lib/format";

export const metadata: Metadata = {
  title: "Termos de uso",
  description: `Termos de uso do ${brand.name}.`,
};

export default function TermsPage() {
  // Published only with the real legal identity (see src/config/brand.ts); never with placeholders.
  const legal = resolveLegal();
  if (!legal) return <LegalUnavailable title="Termos de uso" />;

  return (
    <article className="legal-prose mx-auto max-w-3xl px-4 py-14 sm:px-6 lg:px-8">
      <p className="text-xs font-medium uppercase tracking-widest text-accent">Documento legal</p>
      <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Termos de uso</h1>
      <p className="mt-3 text-sm text-fg-subtle">Última atualização: {formatCalendarDate(legal.updatedAt)}.</p>

      <h2>1. Quem somos</h2>
      <p>
        O serviço {brand.name} (“Serviço”) é operado por {legal.companyName}, inscrita no CNPJ sob o nº {legal.cnpj}, com sede em{" "}
        {legal.address} (“Empresa”, “nós”). Estes Termos regulam o uso do site, do aplicativo web e das ferramentas associadas.
      </p>

      <h2>2. Aceitação</h2>
      <p>
        Ao criar uma conta ou usar o Serviço você declara ter lido e aceitado estes Termos e a nossa{" "}
        <Link href="/privacy">Política de Privacidade</Link>. Se você usa o Serviço em nome de uma empresa, declara ter poderes para
        vinculá-la a estes Termos.
      </p>

      <h2>3. O que o Serviço faz</h2>
      <p>
        O Serviço recebe gravações de vídeo ou áudio enviadas por você, analisa o conteúdo (energia do áudio, transcrição da fala,
        atividade visual) para sugerir trechos candidatos e renderiza clipes curtos com legendas e formatos escolhidos por você.
      </p>
      <p>
        As sugestões e a “relevância estimada” são resultados automáticos e aproximados. Elas não garantem desempenho, alcance,
        visualizações ou qualquer resultado comercial. A revisão e a decisão de publicação são sempre suas.
      </p>

      <h2>4. Conta e segurança</h2>
      <ul>
        <li>Você precisa ter pelo menos 18 anos ou estar autorizado por um responsável legal.</li>
        <li>Mantenha sua senha em sigilo. Atividades realizadas na sua conta são de sua responsabilidade.</li>
        <li>
          Avise-nos imediatamente pelo e-mail <a href={`mailto:${legal.supportEmail}`}>{legal.supportEmail}</a> em caso de uso não
          autorizado.
        </li>
      </ul>

      <h2>5. Conteúdo enviado e direitos</h2>
      <p>
        Você continua sendo o titular de todo o conteúdo que envia. Ao usar o Serviço, você nos concede uma licença limitada,
        não exclusiva e revogável para armazenar, processar, transcrever e transformar o conteúdo exclusivamente para prestar o
        Serviço a você.
      </p>
      <p>
        <strong>Você declara e garante que possui os direitos necessários</strong> (ou autorização do titular) para enviar e processar
        o conteúdo, incluindo imagem e voz de pessoas que apareçam nele, e que o conteúdo não viola lei, direitos de terceiros ou
        políticas das plataformas onde foi originalmente transmitido. Não oferecemos download ou captura de transmissões de terceiros.
      </p>

      <h2>6. Usos proibidos</h2>
      <ul>
        <li>Enviar conteúdo que você não tem direito de processar.</li>
        <li>Enviar conteúdo ilegal, que viole direitos autorais, de imagem ou de privacidade, ou que contenha abuso ou exploração.</li>
        <li>Tentar contornar limites do plano, acessar contas de terceiros ou interferir na infraestrutura do Serviço.</li>
        <li>Usar o Serviço para produzir desinformação deliberada ou deepfakes sem consentimento das pessoas retratadas.</li>
      </ul>

      <h2>7. Planos, pagamentos e cancelamento</h2>
      <p>
        O Serviço oferece um plano gratuito e planos pagos com limites de processamento, armazenamento e renderizações descritos na
        página de planos. Os valores são cobrados mensalmente por meio de um provedor de pagamentos. O cancelamento pode ser feito a
        qualquer momento e tem efeito ao fim do período já pago, quando a conta retorna ao plano gratuito.
      </p>
      <p>
        Os limites de cada plano podem ser ajustados; mudanças que reduzam limites de planos pagos serão comunicadas com antecedência
        mínima de 30 dias.
      </p>

      <h2>8. Armazenamento e retenção</h2>
      <p>
        Gravações, transcrições e clipes ficam armazenados pelo período de retenção do seu plano e são removidos automaticamente
        após esse prazo. Você pode excluir projetos, clipes ou a conta inteira a qualquer momento; a exclusão da conta apaga os
        arquivos e dados associados conforme a Política de Privacidade.
      </p>

      <h2>9. Disponibilidade e suporte</h2>
      <p>
        Trabalhamos para manter o Serviço disponível, mas ele é fornecido “no estado em que se encontra”. Pode haver interrupções para
        manutenção, falhas de processamento ou limitações técnicas (por exemplo, arquivos corrompidos ou sem faixa de vídeo). O
        suporte é feito pelo e-mail <a href={`mailto:${legal.supportEmail}`}>{legal.supportEmail}</a>.
      </p>

      <h2>10. Limitação de responsabilidade</h2>
      <p>
        Na extensão permitida pela lei, a Empresa não responde por lucros cessantes, perda de audiência ou danos indiretos decorrentes
        do uso do Serviço, e sua responsabilidade total fica limitada ao valor pago por você nos 12 meses anteriores ao evento. Nada
        nestes Termos exclui direitos garantidos pelo Código de Defesa do Consumidor.
      </p>

      <h2>11. Propriedade intelectual do Serviço</h2>
      <p>
        O software, a marca, o design e a documentação do Serviço pertencem à Empresa ou a seus licenciadores. Estes Termos não
        transferem qualquer direito sobre eles, exceto o uso previsto aqui.
      </p>

      <h2>12. Alterações destes Termos</h2>
      <p>
        Podemos atualizar estes Termos. Mudanças relevantes serão avisadas por e-mail ou dentro do Serviço com antecedência razoável.
        O uso continuado após a vigência da nova versão representa aceitação.
      </p>

      <h2>13. Lei aplicável e foro</h2>
      <p>
        Estes Termos são regidos pelas leis da República Federativa do Brasil. Fica eleito o foro de {legal.forum}, salvo quando a lei
        garantir ao consumidor o foro do seu domicílio.
      </p>

      <h2>14. Contato</h2>
      <p>
        Dúvidas sobre estes Termos: <a href={`mailto:${legal.supportEmail}`}>{legal.supportEmail}</a>.
      </p>
    </article>
  );
}
