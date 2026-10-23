# Companion do OBS (detecção de destaques ao vivo)

Aplicativo local, em Python, que roda **no seu computador**, ao lado do OBS Studio. Enquanto você
grava a sua live, ele acompanha o arquivo de gravação que o OBS está escrevendo, corta esse
arquivo em segmentos (sem recodificar, com FFmpeg) e envia os segmentos para a plataforma. Lá os
segmentos são analisados em ordem e os destaques aparecem na página **Lives** sem esperar a live
acabar.

## O que ele faz e o que ele não faz

Faz:

- conversa com o **seu** OBS pelo obs-websocket v5 (o mesmo computador ou a sua rede local) para
  saber quando a gravação começa e termina;
- lê **a sua própria gravação** do disco, enquanto ela é escrita, e envia cópias em segmentos de
  20 a 120 s (padrão 60 s);
- mostra no console e na página Lives o estado real: OBS conectado, gravando, enviando, fila,
  offline.

Não faz:

- **não captura nada da Twitch, do YouTube, da Kick ou de qualquer plataforma**: não lê a
  transmissão de ninguém, não baixa vídeos e não lê o chat;
- não muda as configurações do OBS. A única ação no OBS é **Iniciar gravação**, e só se você
  usar `--auto-record`;
- não grava a tela nem o microfone por conta própria: sem uma gravação do OBS (ou um arquivo
  indicado com `--file`), nada é enviado.

## Requisitos

- Windows 10/11 (também funciona em Linux e macOS), com **Python 3.11 ou mais recente**.
- **OBS Studio 28 ou mais recente**, que já vem com o obs-websocket v5.
- **obs-websocket ativado e com senha**: no OBS, abra *Ferramentas > Configurações do servidor
  WebSocket*, marque *Ativar servidor WebSocket*, mantenha a porta 4455 e defina uma senha
  (*Mostrar informações de conexão* mostra a senha).
- **Gravação em MKV** (recomendado, é o padrão do OBS) ou **MP4 híbrido/fragmentado**: em
  *Configurações > Saída > Gravação > Formato da gravação*. MP4 comum **não serve**: ele só pode
  ser lido depois que a gravação termina, e o companion recusa esse formato com uma mensagem
  clara. Se precisar de um MP4 no final, use *Arquivo > Remux de gravações* depois da live.
- **FFmpeg** (o mesmo usado pelo worker). Em desenvolvimento no Windows, `scripts/dev-setup.ps1`
  instala o FFmpeg em `%LOCALAPPDATA%\ClipForge\tools\ffmpeg\bin`, e o companion o usa
  automaticamente. Em outro lugar, informe `--ffmpeg CAMINHO` ou a variável `FFMPEG_PATH`;
  sem nada disso ele procura `ffmpeg` no PATH.
- Espaço em disco para alguns segmentos (cada segmento de 60 s ocupa o mesmo que 60 s da sua
  gravação; eles são apagados depois do envio).

## Instalação (a partir deste repositório)

Ainda não há instalador: o companion roda com o Python deste repositório.

```powershell
cd companion
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Linux/macOS: `python3 -m venv .venv && .venv/bin/python -m pip install -r requirements.txt`.

## Token do dispositivo

1. Entre na plataforma e abra a página **Lives**, seção **Dispositivos**.
2. Crie um token com um nome que identifique o computador (por exemplo "PC do estúdio").
3. Copie o token (`cfd_...`): ele aparece **uma única vez**. Se perder, crie outro e revogue o
   antigo na mesma página.

Prefira passar o token pela variável de ambiente `CLIPFORGE_DEVICE_TOKEN` em vez de `--token`
(argumentos ficam no histórico do terminal). O companion nunca mostra nem registra o token nos
logs.

```powershell
$env:CLIPFORGE_API_URL = "https://api.exemplo.com"
$env:CLIPFORGE_DEVICE_TOKEN = "cfd_..."          # o token criado na página Lives
$env:OBS_WEBSOCKET_PASSWORD = "senha-do-obs-websocket"
```

Para validar o token e o acesso à API:

```powershell
.venv\Scripts\python.exe -m clipforge_companion check
# Token válido. Dispositivo: PC do estúdio
# Plano: Pro (pro)
# FFmpeg: ffmpeg version ...
```

`check --obs ws://127.0.0.1:4455` também testa a conexão com o OBS.

## Uso

### Modo OBS (padrão)

```powershell
.venv\Scripts\python.exe -m clipforge_companion run --title "Live de sexta"
```

Deixe o companion aberto e use o OBS normalmente:

- ao **iniciar a gravação**, o companion cria uma sessão na página Lives e começa a enviar
  segmentos;
- ao **parar a gravação**, ele finaliza o último segmento, envia o que estiver na fila e encerra a
  sessão; depois volta a aguardar a próxima gravação. Uma gravação nova vira sempre uma sessão
  nova, mesmo iniciada logo depois de parar a anterior. Enquanto o servidor ainda analisa os
  últimos segmentos da sessão anterior, a nova sessão espera ("Aguardando a análise da sessão
  anterior"), também quando a sessão anterior foi encerrada por uma execução anterior do
  companion; nada se perde, a gravação continua no disco e é lida desde o início;
- se o OBS estiver **transmitindo sem gravar**, o companion avisa (e o aviso aparece na página
  Lives). Com `--auto-record` ele clica em *Iniciar gravação* por você;
- se o OBS fechar ou a conexão cair, ele tenta reconectar (1 s, 2 s, 4 s ... até 30 s) e a
  captura continua enquanto o arquivo continuar crescendo. Se o arquivo parar de crescer com o
  OBS desconectado, ele espera o OBS voltar (até 60 s) antes de encerrar a sessão. Ao
  reconectar, ele confere na pasta de gravação qual arquivo o OBS está gravando: se o OBS foi
  reaberto e começou uma gravação nova nesse meio-tempo, a sessão atual termina com o que já foi
  gravado e a gravação nova vira uma sessão nova (também quando a sessão da gravação anterior
  ainda estava sendo criada, esperando a análise da sessão anterior: essa gravação é enviada até
  onde o OBS gravou); se o OBS continua gravando um arquivo cuja sessão já foi encerrada, uma
  sessão nova continua a partir do último segmento enviado;
- gravação pausada: a captura espera o arquivo voltar a crescer; divisão automática de arquivos
  do OBS: a captura continua no arquivo novo, na mesma sessão.

OBS em outro computador da rede: `--obs ws://192.168.0.10:4455` (o arquivo de gravação precisa
estar acessível neste computador pelo mesmo caminho).

### Modo arquivo (sem OBS)

Para testes ou para quem grava com outro programa: o companion acompanha um arquivo que está
sendo gravado (ou já pronto) e termina quando o arquivo para de crescer.

```powershell
.venv\Scripts\python.exe -m clipforge_companion run --file "D:\OBS\2026-10-23 20-00-00.mkv"
```

### Opções

| Opção | Variável | Descrição |
|---|---|---|
| `--api URL` | `CLIPFORGE_API_URL` | endereço da API |
| `--token TOKEN` | `CLIPFORGE_DEVICE_TOKEN` | token do dispositivo (prefira a variável) |
| `--obs URL` | `OBS_WEBSOCKET_URL` | obs-websocket (padrão `ws://127.0.0.1:4455`) |
| `--obs-password SENHA` | `OBS_WEBSOCKET_PASSWORD` | senha do obs-websocket (prefira a variável) |
| `--auto-record` | | inicia a gravação se o OBS transmitir sem gravar |
| `--file ARQUIVO` | | modo arquivo, sem OBS |
| `--segment-seconds N` | | duração alvo dos segmentos, 20 a 120 (padrão 60); uma sessão retomada continua com a duração com que foi criada |
| `--title TÍTULO` | | título da sessão (padrão "Live de dd/MM HH:mm") |
| `--work-dir DIR` | | pasta dos segmentos e da fila (padrão `%LOCALAPPDATA%\ClipForge\companion`) |
| `--ffmpeg CAMINHO` | `FFMPEG_PATH` | executável do FFmpeg |
| `--max-upload-mbps N` | | limite de banda do envio, em megabits por segundo |
| `--keep-segments` | | não apaga os segmentos depois de enviados |
| `-v` | | logs de diagnóstico (sem segredos) |

### Encerrar

- **Ctrl+C uma vez**: finaliza o segmento atual, envia a fila e encerra a sessão.
- **Ctrl+C duas vezes**: sai imediatamente. Os segmentos que faltavam ficam salvos em
  `<work-dir>/<sessão>/queue.json` e são enviados na próxima execução: se for a mesma gravação,
  a mesma sessão é retomada de onde parou (com a duração de segmento com que ela foi criada,
  mesmo que `--segment-seconds` tenha mudado: cortar a gravação de outro jeito pularia ou
  repetiria trechos); se não, a sessão antiga é encerrada antes de criar uma nova. Uma sessão
  que já está em análise no servidor não é retomada: ela é dada como encerrada e uma nova é
  criada. No modo OBS isso acontece logo ao iniciar, sem esperar uma nova gravação: se a
  gravação daquela sessão terminou enquanto o companion estava fechado e o arquivo ainda está
  no disco, o restante dele é enviado e a sessão é encerrada; se o arquivo não existe mais, a
  fila é enviada e a sessão é encerrada. Uma sessão cujo último segmento já estava na fila
  marcado como final (Ctrl+C uma vez e, antes de a fila ser enviada, Ctrl+C de novo) nunca é
  retomada: ela é encerrada com esse segmento e, se a gravação continuar, uma sessão nova
  continua a partir dele, sem enviar de novo o que já foi enviado.
- **Encerrar sessão** na página Lives: o companion recebe o pedido no próximo heartbeat (até
  10 s), finaliza o segmento atual, envia a fila, encerra a sessão e termina.

Códigos de saída: `0` sucesso, `1` erro, `2` uso incorreto, `3` token inválido ou revogado,
`130` saída imediata.

Só um companion roda por token e pasta de trabalho (`--work-dir`): um segundo é recusado com uma
mensagem, para não encerrar a sessão do primeiro como se ela fosse de uma execução interrompida.

## Console

Cada linha tem a hora; o resumo do estado só aparece quando muda:

```
[20:00:01] API conectada. Dispositivo: PC do estúdio · plano Pro
[20:00:01] OBS conectado (OBS 31.0.2, obs-websocket 5.5.4); aguardando gravação.
[20:00:05] OBS começou a gravar: D:\OBS\2026-10-23 20-00-05.mkv
[20:00:05] Sessão criada: Live de sexta (3f2a9c1e)
[20:01:07] Segmento 0 pronto (0:00-1:00, 60,0 s)
[20:01:07] estado: OBS conectado · gravando · enviando 1/1 · fila 1
[20:01:12] Segmento 0 enviado (45,2 MB em 4,8 s)
```

## Como funciona

1. O FFmpeg lê a gravação enquanto ela cresce (`-follow 1`) e o *segment muxer* corta em
   quadros-chave, sem recodificar (`-c copy`), no vídeo principal e na primeira faixa de áudio.
   Os cortes caem em quadros-chave, então a duração dos segmentos varia um pouco (no OBS, use
   intervalo de quadros-chave de 2 s, o recomendado para transmissão).
2. Cada segmento pronto entra numa **fila persistente** (`<work-dir>/<sessão>/queue.json`) e é
   enviado em ordem. Falhas de rede (inclusive erros de TLS, como os causados por antivírus ou
   proxy que inspecionam HTTPS), 5xx e 429 são repetidas com espera crescente (2 s, 4 s ... até
   60 s); a API ignora reenvios do mesmo segmento. Token revogado (401/403) ou sessão
   encerrada/cota esgotada (409) param o envio com uma mensagem clara. Um segmento só é apagado
   depois de aceito (ou recusado de vez) pela API; um arquivo bloqueado por um instante (antivírus)
   é tentado de novo.
3. A cada 10 s um **heartbeat** informa o estado (OBS conectado, transmitindo, gravando,
   enviando, tamanho da fila, último erro) e recebe pedidos de encerramento feitos no site.
4. O último segmento vai marcado como final e a sessão é encerrada depois que ele é enviado.

## Solução de problemas

| Sintoma | O que fazer |
|---|---|
| `OBS indisponível: conexão recusada` | Abra o OBS e ative o servidor WebSocket (Ferramentas > Configurações do servidor WebSocket). Confira a porta. |
| `Senha do obs-websocket incorreta` | Copie a senha em *Mostrar informações de conexão* e defina `OBS_WEBSOCKET_PASSWORD`. |
| `A gravação ... está em MP4 comum` | Troque o formato da gravação para MKV (ou MP4 híbrido) e inicie uma nova gravação. |
| `Token inválido ou revogado` | Crie um novo token na página Lives e atualize `CLIPFORGE_DEVICE_TOKEN`. |
| `Não foi possível criar a sessão: ... sessão ativa` | Já existe uma sessão aberta para este dispositivo: encerre-a na página Lives. |
| `Aguardando a análise da sessão anterior...` | A sessão anterior (mesmo encerrada por uma execução anterior do companion) ainda está sendo analisada no servidor; a nova sessão é criada assim que ela terminar. |
| `O FFmpeg falhou 3 vezes ao ler ...` | O FFmpeg não consegue passar de um ponto da gravação (arquivo danificado, disco cheio): depois de 3 falhas sem avanço (uma leitura que chega a dados novos zera a contagem) a captura desse arquivo para, o que já foi capturado é enviado e a sessão é encerrada. Confira o arquivo e o espaço livre em `--work-dir`. |
| `Outro companion já está rodando com este token...` | Já há um companion aberto com o mesmo token e a mesma pasta de trabalho: use aquele (ou feche-o antes). |
| `FFmpeg não encontrado` | Rode `scripts/dev-setup.ps1` ou informe `--ffmpeg` / `FFMPEG_PATH`. |
| `offline: ...` | Sem acesso à API: os segmentos ficam na fila e são enviados quando a conexão voltar. |
| O último segmento demora a aparecer | Quando a gravação para, o FFmpeg espera o arquivo parar de crescer (até ~45 s) antes de fechar o último segmento; isso é esperado. |
| Upload atrapalha a live | Use `--max-upload-mbps` com um valor bem abaixo da sua banda de upload. |
| Disco cheio | Os segmentos são apagados depois do envio; confira se a API está acessível (fila parada) e o espaço em `--work-dir`. |

Para diagnóstico, rode com `-v`: os logs nunca incluem o token nem a senha do OBS.

## Privacidade e segurança

- Só a **sua** gravação é enviada, e só enquanto o companion está rodando. Nada é capturado de
  plataformas de streaming, e o chat não é lido.
- O token do dispositivo vale só para este computador: você pode revogá-lo a qualquer momento na
  página Lives (as sessões abertas desse dispositivo são encerradas).
- O token e a senha do OBS nunca aparecem no console nem nos logs. Use HTTPS para a API: o
  companion avisa quando a URL é `http://` fora desta máquina.
- Os segmentos ficam no disco apenas até serem enviados (a menos que você use
  `--keep-segments`). O arquivo da fila (`queue.json`) guarda o id da sessão, caminhos, tempos e
  números de sequência, nunca o token; ele é apagado quando a sessão termina.
- Na plataforma, os segmentos são processados como qualquer upload seu: contam na cota de minutos
  do plano e pertencem só à sua conta.

## Limitações conhecidas

- Sem instalador por enquanto: é preciso Python e este repositório.
- Latência de detecção de ~1,5 a 3 min com segmentos de 60 s (um segmento + pós-roll +
  processamento).
- Ao parar a gravação, o último segmento leva até ~3 x 15 s para ser fechado pelo FFmpeg.
- O OBS precisa gravar em um disco que este computador consiga ler pelo mesmo caminho.
- Se o companion for iniciado com uma gravação já longa em andamento, ele processa a gravação
  desde o início: os segmentos desse trecho ficam no disco até serem enviados (no pior caso, o
  mesmo espaço da gravação).

## Desenvolvimento

```powershell
.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.venv\Scripts\python.exe -m ruff check clipforge_companion tests
.venv\Scripts\python.exe -m pytest -m "not integration" -q
```

A suíte rápida usa um servidor obs-websocket falso, uma API falsa (`httpx.MockTransport`) e o
FFmpeg real com o vídeo de teste de `%LOCALAPPDATA%\ClipForge\fixtures\e2e-fixture.mp4` (ou
`COMPANION_TEST_FIXTURE`); testes que precisam do FFmpeg ou do vídeo são pulados se eles não
existirem. O teste de integração roda o modo arquivo contra uma API real:

```powershell
$env:CLIPFORGE_API_URL = "http://localhost:5080"; $env:CLIPFORGE_DEVICE_TOKEN = "cfd_..."
.venv\Scripts\python.exe -m pytest -m integration -q
```
