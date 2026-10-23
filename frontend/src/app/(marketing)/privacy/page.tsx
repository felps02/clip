import type { Metadata } from "next";
import Link from "next/link";
import { LegalUnavailable } from "@/components/marketing/legal-unavailable";
import { brand, resolveLegal } from "@/config/brand";
import { formatCalendarDate } from "@/lib/format";

export const metadata: Metadata = {
  title: "Política de privacidade",
  description: `Como o ${brand.name} trata os seus dados pessoais, em conformidade com a LGPD.`,
};

export default function PrivacyPage() {
  // Published only with the real legal identity (see src/config/brand.ts); never with placeholders.
  const legal = resolveLegal();
  if (!legal) return <LegalUnavailable title="Política de privacidade" />;

  const dpoLink = <a href={`mailto:${legal.dpoEmail}`}>{legal.dpoEmail}</a>;

  return (
    <article className="legal-prose mx-auto max-w-3xl px-4 py-14 sm:px-6 lg:px-8">
      <p className="text-xs font-medium uppercase tracking-widest text-accent">Documento legal</p>
      <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Política de privacidade</h1>
      <p className="mt-3 text-sm text-fg-subtle">Última atualização: {formatCalendarDate(legal.updatedAt)}.</p>

      <h2>1. Controlador e encarregado</h2>
      <p>
        O controlador dos dados tratados pelo {brand.name} é {legal.companyName}, CNPJ {legal.cnpj}, {legal.address}. O encarregado
        pelo tratamento de dados pessoais (DPO) pode ser contatado em {dpoLink}.
      </p>
      <p>
        Esta Política segue a Lei Geral de Proteção de Dados Pessoais (Lei nº 13.709/2018, “LGPD”) e complementa os{" "}
        <Link href="/terms">Termos de uso</Link>.
      </p>

      <h2>2. Quais dados tratamos</h2>
      <ul>
        <li>
          <strong>Dados de conta:</strong> nome de exibição, e-mail, senha (armazenada apenas como hash), idioma, fuso horário,
          preferências e registro de aceite dos termos.
        </li>
        <li>
          <strong>Conteúdo enviado:</strong> arquivos de vídeo/áudio, transcrições geradas, sinais de análise, candidatos, clipes e
          miniaturas. O conteúdo pode incluir imagem e voz sua e de terceiros que aparecem na gravação.
        </li>
        <li>
          <strong>Dados de uso:</strong> projetos criados, minutos processados, renderizações, armazenamento utilizado, eventos dos
          trabalhos de processamento e notificações.
        </li>
        <li>
          <strong>Dados de pagamento:</strong> identificadores da assinatura e eventos do provedor de pagamentos. Números de cartão
          não passam pelos nossos servidores.
        </li>
        <li>
          <strong>Dados técnicos:</strong> endereço IP, navegador, registros de acesso e identificadores de sessão (cookies).
        </li>
      </ul>

      <h2>3. Para que usamos os dados e bases legais</h2>
      <ul>
        <li>Prestar o Serviço (análise, geração de clipes, armazenamento) — execução de contrato (art. 7º, V, LGPD).</li>
        <li>Autenticação, segurança, prevenção a fraudes e limites de uso — legítimo interesse e cumprimento de obrigação legal.</li>
        <li>Cobrança e emissão de documentos fiscais — execução de contrato e obrigação legal.</li>
        <li>Comunicações sobre o funcionamento do Serviço (confirmação de e-mail, processamento concluído, avisos de cota) — execução de contrato.</li>
        <li>Comunicações de marketing — somente com o seu consentimento, que pode ser revogado a qualquer momento.</li>
      </ul>
      <p>Não usamos o seu conteúdo para treinar modelos de inteligência artificial de uso geral.</p>

      <h2>4. Cookies</h2>
      <p>
        Usamos cookies estritamente necessários para manter a sua sessão (tokens de acesso e de renovação, com atributos HttpOnly e
        SameSite). Não usamos cookies de rastreamento publicitário.
      </p>

      <h2>5. Compartilhamento</h2>
      <p>
        Compartilhamos dados apenas com operadores necessários à prestação do Serviço: provedores de hospedagem e armazenamento de
        arquivos, provedor de e-mail transacional e provedor de pagamentos. Todos atuam sob contrato e instruções da Empresa. Dados
        podem ser transferidos internacionalmente para esses provedores com as salvaguardas previstas na LGPD. Também podemos
        compartilhar dados quando exigido por lei ou ordem de autoridade competente.
      </p>

      <h2>6. Retenção e exclusão</h2>
      <ul>
        <li>Gravações, transcrições e clipes: pelo período de retenção do plano contratado; depois são apagados automaticamente.</li>
        <li>Uploads não concluídos: removidos em até 24 horas.</li>
        <li>
          Conta: ao solicitar a exclusão, as sessões são encerradas imediatamente, os projetos são marcados para remoção e os arquivos
          e dados pessoais são apagados definitivamente. Registros de pagamento e de consumo são mantidos de forma anonimizada apenas
          pelo prazo exigido pela legislação fiscal.
        </li>
      </ul>

      <h2>7. Seus direitos (art. 18 da LGPD)</h2>
      <p>Você pode, a qualquer momento, solicitar:</p>
      <ul>
        <li>confirmação da existência de tratamento e acesso aos dados;</li>
        <li>correção de dados incompletos, inexatos ou desatualizados;</li>
        <li>anonimização, bloqueio ou eliminação de dados desnecessários ou tratados em desconformidade;</li>
        <li>portabilidade dos dados a outro fornecedor;</li>
        <li>eliminação dos dados tratados com base no consentimento;</li>
        <li>informação sobre com quem compartilhamos os dados;</li>
        <li>informação sobre a possibilidade de não fornecer consentimento e suas consequências;</li>
        <li>revogação do consentimento.</li>
      </ul>
      <p>
        As solicitações podem ser feitas nas configurações da conta (alteração de dados e exclusão da conta) ou pelo e-mail {dpoLink}.
        Responderemos nos prazos previstos na LGPD. Você também pode apresentar reclamação à Autoridade Nacional de Proteção de Dados
        (ANPD).
      </p>

      <h2>8. Segurança</h2>
      <p>
        Adotamos medidas técnicas e administrativas como criptografia em trânsito, senhas armazenadas com hash, URLs assinadas e
        temporárias para acesso a arquivos, isolamento de dados por usuário, controle de acesso e registro de eventos. Nenhum sistema
        é totalmente seguro; em caso de incidente relevante, comunicaremos você e a ANPD conforme a lei.
      </p>

      <h2>9. Crianças e adolescentes</h2>
      <p>O Serviço não se destina a menores de 18 anos sem autorização de um responsável legal.</p>

      <h2>10. Alterações</h2>
      <p>
        Esta Política pode ser atualizada. Avisaremos sobre mudanças relevantes por e-mail ou dentro do Serviço, indicando a data da
        última atualização no topo do documento.
      </p>

      <h2>11. Contato</h2>
      <p>
        Dúvidas sobre privacidade: {dpoLink} ou <a href={`mailto:${legal.supportEmail}`}>{legal.supportEmail}</a>.
      </p>
    </article>
  );
}
