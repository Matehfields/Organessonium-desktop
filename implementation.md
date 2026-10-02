# Reprodução progressiva de vídeo

Este documento descreve a implementação desktop C# + WebView2, seus contratos de dados e os requisitos para uma implementação equivalente em Flutter. Não representa mudanças já realizadas no aplicativo mobile.

## 1. Objetivo e separação dos fluxos

O player lê um arquivo virtual composto pelos attachments do Discord. Somente os chunks necessários e uma janela pequena de antecipação ficam em RAM. Abrir um vídeo não reconstrói o arquivo inteiro e não cria mídia temporária no SSD. Não existe servidor HTTP, listener, porta local, regra de firewall ou conversão de vídeo durante a reprodução.

O download tradicional continua reconstruindo **todos** os chunks no disco escolhido pelo usuário. Seus temporários, checkpoints e cancelamento são independentes do player. O cache de thumbnails e de prévias de arquivos que não são vídeos continua existindo.

O faststart pertence ao **upload**: um MP4 elegível é preparado antes da divisão e da criptografia. Essa preparação pode criar um único arquivo temporário completo, posteriormente removido. Não é o cache do streaming e não altera vídeos já armazenados no servidor.

## 2. Arquitetura e responsabilidades

```text
Discord REST: mensagens, IDs, tamanhos e URLs dos attachments
    |
ForumDownloader.OpenVideoChunksAsync
    |
DiscordVideoChunkProvider <--- CDN + EncryptionService
    |
VideoChunkIndex: prefixos dos tamanhos plaintext reais
    |
VideoStreamSession: leituras assíncronas, RAM, deduplicação e cancelamento
    |
PreviewResourceRegistry: recurso virtual opaco e faixas limitadas
    |
WebView2PreviewAdapter: WebResourceRequested, sem TCP
    |
web/app.js: player existente, eventos, buffering, seek e retomada

Mp4FastStart -> upload/chunks/encryption -> Discord
VideoPlaybackTracker -> settings do SQLite local
ForumDownloader.DownloadAndAssembleAsync -> download completo no disco
```

Os arquivos C# ficam em `Organessonium/Organessonium`:

| Componente | Responsabilidade |
| --- | --- |
| `Services/VideoChunkIndex.cs` | Prefix sums, busca binária, `IVirtualFile` e contrato do provider |
| `Services/DiscordVideoChunkProvider.cs` | Um attachment por leitura de chunk, renovação da URL, retry e autenticação |
| `Services/VideoStreamSession.cs` | Sessão cancelável por vídeo/qualidade, cache e tarefas compartilhadas |
| `Services/VideoNetworkPriority.cs` | Prioridade cooperativa sobre a cópia do download tradicional |
| `Services/PreviewResourceRegistry.cs` | Registro/revogação de URLs, validação de Range e respostas limitadas |
| `Services/AvailableMemoryStream.cs` | Bytes já disponíveis para o WebView2, liberados após consumo ou fechamento |
| `Services/VirtualFileStream.cs` | Vista limitada dos arquivos de prévia não relacionados ao streaming de vídeo |
| `Bridge/WebView2PreviewAdapter.cs` | Única camada nova dependente de WebView2 |
| `Services/Mp4FastStart.cs` | Parser e realocação conservadora de `moov`, sem FFmpeg |
| `Services/VideoPlaybackProgress.cs` | Registro local por `fileId`, throttle e conclusão |
| `Bridge/AppController.cs` | Integração dos fluxos existentes, sessões, qualidade, logout e upload |
| `Models/AppSettings.cs`, `Services/SettingsStore.cs` | Configuração persistente de antecipação |
| `Models/TransferState.cs` | Checkpoint local `fastStartApplied` para retomar upload |
| `MainWindow.xaml.cs` | Anexa e remove o adaptador junto ao ciclo de vida da WebView |
| `web/app.js`, `web/bridge.js`, `web/index.html`, `web/style.css` | Interface, mensagens de reprodução e slider |

O núcleo não conhece DOM, controles WPF nem WebView2. `IVirtualFile` expõe `Length` e `ReadAsync(offset, destination, cancellationToken)`. O provider fornece bytes de um único chunk. A interface JavaScript não recebe token do Discord, chave de criptografia ou URL privada de attachment para reproduzir vídeo.

## 3. Metadados e localização dos payloads

O formato remoto permanece o existente, sem novos campos obrigatórios:

- `fileId`: identidade persistente do arquivo, preservada em rename e move.
- `threadId`: post do fórum `files` com os payloads, não o post do índice da pasta.
- `chunks`: IDs das mensagens que contêm os chunks originais.
- `sizeBytes`: comprimento do arquivo original em plaintext.
- `preview.video480p.chunks` e `preview.video480p.sizeBytes`: mensagens e comprimento da versão 480p, quando existente.
- `encryption.version`, `mode`, `salt`: contexto para autenticar/descriptografar os payloads. Ausência de `encryption` mantém o caminho legado não criptografado.
- `parentFolderId`, `namesMessageId` e os seriais continuam localizando os detalhes na arquitetura de índices existente. O serial não passa a transportar bytes de vídeo.

O controller carrega os detalhes pelo `AppRegistry` antes de abrir a sessão. `ForumDownloader` resolve as mensagens, confere ordem e contexto dos payloads usando o protocolo existente e coleta `messageId`, nome, URL e tamanho de cada attachment. Essa resolução pode exigir várias chamadas REST antes do primeiro chunk, mas não baixa os payloads inteiros.

Para conteúdo legado sem lista explícita de chunks, a coleta existente identifica um lote completo no post. Uma referência criptografada sem chunks explícitos é recusada; não se tenta adivinhar quais blobs pertencem ao arquivo. A versão 480p e o original sempre usam listas separadas.

## 4. Arquivo virtual e tamanhos variáveis

Não use `offset / limiteAtualDoUpload` para ler arquivos existentes. O limite atual de upload não determina o tamanho dos chunks antigos.

Se `L[i]` for o tamanho plaintext real do chunk `i`:

```text
S[0] = 0
S[i + 1] = S[i] + L[i]
Length = soma(L)
chunk(offset) = maior i tal que S[i] <= offset
offsetInterno = offset - S[i]
take = min(bytesAindaSolicitados, L[i] - offsetInterno)
```

`VideoChunkIndex` usa soma verificada de inteiros de 64 bits e busca binária. Os índices são internos, baseados em zero. Um offset exatamente em `S[i]` pertence ao chunk `i`. Uma leitura na fronteira copia o final de uma parte e continua no início da próxima.

Exemplos em MiB para chunks não criptografados:

| Tamanhos reais | Offset | Chunk | Offset interno |
| --- | --- | --- | --- |
| 20, 20, 12 | 0 | 0 | 0 |
| 20, 20, 12 | 25 | 1 | 5 |
| 10, 10, 7 | 20 | 2 | 0 |
| 10, 20, 3 | 25 | 1 | 15 |

Em payloads criptografados v1, `L[i] = attachment.size - 70`. O envelope contém header de 54 bytes e tag GCM de 16 bytes. A autenticação confirma o tamanho plaintext antes de expor os bytes. Quando `sizeBytes > 0`, a soma precisa coincidir com o metadata; referência incompleta ou divergência é apresentada como erro, sem entregar bytes deslocados.

## 5. Criptografia compartilhada

O streaming reutiliza `EncryptionService.DecryptBytes`, sem alterar a criptografia dos arquivos existentes. Cada attachment criptografado é recebido completamente em RAM antes de autenticar sua tag AES-GCM. Só o plaintext autenticado pode entrar no cache e ser copiado para o player. O ciphertext é zerado após essa etapa.

Contrato do envelope v1, com inteiros big-endian:

| Offset | Tamanho | Campo |
| --- | --- | --- |
| 0 | 8 | Magic ASCII `ORGAESGC` |
| 8 | 1 | Versão, `1` |
| 9 | 1 | Papel: `1` original, `2` thumbnail/imagem, `3` vídeo 480p |
| 10 | 4 | Número do chunk baseado em um |
| 14 | 4 | Total de chunks |
| 18 | 8 | Tamanho plaintext |
| 26 | 16 | Salt do arquivo |
| 42 | 12 | Nonce GCM |
| 54 | variável | Ciphertext |
| fim menos 16 | 16 | Tag GCM |

O header inteiro é AAD. A chave de arquivo tem 32 bytes, derivada com HKDF-SHA256 da master key do modo configurado e do salt. O `info` é `UTF8("organessonium/file-key/v1") + byte zero + uint32BE(tamanho UTF8 do fileId.Trim()) + UTF8(fileId.Trim())`. O salt do metadata é Base64URL sem padding. Nenhuma chave real é documentada, publicada ou adicionada ao metadata.

A futura implementação deve reutilizar o mecanismo de master key compatível já existente em cada plataforma. DPAPI é persistência local do Windows, não parte do protocolo remoto. Não substituir o contexto por somente nome de arquivo, tamanho, URL ou índice.

As mensagens criptografadas seguem `file:<fileId> role:<papel> chunk:<n>/<total> encryption:v1`. O papel textual do vídeo reduzido é `video480p`. Validar versão, salt, papel, número, total, comprimento e tag. Falha de autenticação não gera plaintext parcialmente utilizável.

## 6. Sessão, cache e cancelamento

Cada vídeo/qualidade ativa tem `VideoStreamSession`, provider, CTS de vida, cache `index -> byte[]`, mapa de downloads em andamento e uma geração de prefetch cancelável. Aberturas ainda sem URL também são canceladas ao fechar ou sair.

O cache é protegido por lock curto. Nenhum lock é mantido durante rede. Duas leituras do mesmo chunk compartilham a mesma tarefa. O cancelamento de um consumidor não cancela um download necessário a outro consumidor. Um chunk especulativo promovido a leitura necessária deixa de ser cancelado como simples prefetch.

Há no máximo duas transferências CDN e duas leituras de faixa simultâneas por sessão. A janela conserva o chunk atual, o anterior e os próximos `N`, além de chunks fixados por leituras ativas. Antes de reproduzir, o primeiro chunk permanece reutilizável mesmo se o Chromium sondar o final do MP4. A troca de região remove chunks fora da janela e cancela antecipações antigas.

O cache plaintext tem teto de 192 MiB. Chunks individuais acima de 64 MiB são recusados pelo player, com orientação para usar o download completo; esse teto não muda o limite do uploader. Além do cache, podem existir até duas operações com ciphertext/plaintext em trânsito e até 2 MiB de respostas do adaptador. A RAM total do processo também inclui WebView2, decodificação, outras transferências e o coletor .NET; o teto do cache não é um teto do Working Set. Com anexos atuais de até 20 MiB, o conjunto em trânsito é pequeno e independente do tamanho total do vídeo. Mesmo anexos legados maiores aceitos pelo guard não fazem a RAM crescer com a duração do arquivo.

Ao fechar: cancelar abertura, cancelar vida da sessão e prefetch, revogar a URL, descartar respostas e cache, fechar o provider e aguardar tarefas/leituras no `DisposeAsync`. A memória deixa de ter referências de sessão; a devolução física ao sistema depende do GC. O logout suspende gravações tardias de progresso antes de apagar os dados locais. A recuperação/fechamento da WebView remove o adaptador antigo.

## 7. Configuração e buffering

Configurações > Prévias contém **Chunks de vídeo antecipados**, persistido como `videoPrefetchChunks` em `settings`:

- Padrão: `1`.
- Intervalo: `0` a `6`, validado em interface, controller, store e sessão.
- `0`: somente chunks pedidos pelo player; não desativa leitura na fronteira nem download completo.
- O valor vale para sessões novas. Não redimensiona uma sessão já aberta.
- `VideoPreviewQuality` continua escolhendo 480p ou original quando as referências existem.

Abrir a prévia solicita imediatamente o chunk zero. Não faz prefetch de cinco ou dez partes. O prefetch começa quando os eventos do player sinalizam reprodução e avança sequencialmente até `N` à frente. Uma leitura necessária tem precedência sobre o início de nova antecipação. Pausar ou iniciar seek cancela a geração antiga. O Chromium ainda pode pedir bytes para metadados ou buffering mesmo com prefetch zero: isso é demanda do player, não antecipação do serviço.

Estados internos: `Opening`, `Idle`, `Playing`, `Seeking`, `Buffering`, `Error`, `Closed`. Os eventos `waiting`/`stalled` mostram a indicação de carregamento existente; `playing`, `canplay` e `seeked` a retiram. Os controles, o fullscreen, o volume, a qualidade e o layout do player foram preservados. Falha definitiva apresenta o fallback existente, com o download convencional disponível.

## 8. Seek e entrega ao WebView2

O player transforma tempo em byte ranges usando as tabelas do container. C# não aproxima tempo por percentual do tamanho do arquivo. Ao solicitar uma região distante, `VideoChunkIndex` localiza diretamente a parte correspondente. Não se baixam todas as partes intermediárias. O prefetch anterior é cancelado; demanda ainda compartilhada por outro leitor pode concluir normalmente.

A URL `https://media.organessonium.local/preview/<GUID>` é virtual e opaca. `WebResourceRequested` intercepta o host; não é um servidor HTTPS real nem uma URL pública de attachment. O registro não permite resolver caminhos arbitrários, host externo, query, porta alternativa ou token inválido.

O WebView2 requer conteúdo disponível ao concluir o deferral. Um stream que espera rede dentro do `Read` pode ser drenado inteiro antes de o `<video>` começar. Por isso cada GET de vídeo entrega **no máximo 512 KiB já disponíveis em RAM**, com `206` e intervalo real em `Content-Range`. O Chromium solicita a próxima faixa quando precisa. Há no máximo quatro respostas vivas por recurso, e seus buffers são liberados no EOF ou na revogação, pois o runtime não garante fechar o Stream .NET.

| Pedido | Resposta |
| --- | --- |
| `GET bytes=a-b` | `206`, de `a` até `min(b, Length-1, a+512KiB-1)` |
| `GET bytes=a-` | `206`, até 512 KiB a partir de `a` |
| `GET bytes=-n` | `206`, a partir de `max(0, Length-n)`, também limitado |
| GET de vídeo sem Range | Equivalente à primeira faixa a partir de zero, `206` limitado |
| HEAD sem Range | `200`, comprimento integral e nenhum corpo |
| HEAD com Range | `206`, cabeçalhos do intervalo, nenhum corpo |
| Range inválido, múltiplo ou fora do arquivo | `416`, `Content-Range: bytes */Length` |
| Token desconhecido/revogado | `404`; pedido em andamento cancelado pode receber `410` |
| Método diferente de GET/HEAD | `405` |
| Falha definitiva ao obter bytes | `503` e erro do player |

`Content-Length` sempre descreve o corpo entregue; `Accept-Ranges: bytes` e `Cache-Control: no-store` evitam tratar a mídia como cache persistente do app. Intervalos são inclusivos. Este adapter parcial serve o player, não substitui a operação Baixar. As respostas de imagens/PDF/áudio existentes continuam podendo ser integrais, mas agora também usam o host virtual em vez do servidor localhost.

Referências de integração: [CreateWebResourceResponse](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createwebresourceresponse), [contrato da resposta WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2webresourceresponse), [conteúdo local e streams](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content).

## 9. Discord, URLs e falhas de rede

O provider baixa um attachment completo por chunk, usando `ResponseHeadersRead`, cliente com proxy configurado e timeout de 60 segundos. Comprimento HTTP, leitura incompleta e bytes excedentes são conferidos. Não monta um arquivo local para descriptografar.

Em `403` ou `404`, reler a mensagem por `threadId` e `messageId`, localizar o attachment pelo nome e exigir o mesmo tamanho. Atualizar somente a URL em memória. Mensagem removida, attachment trocado ou índice incompleto não podem ser corrigidos inventando bytes.

Cada chunk tem no máximo quatro tentativas, com backoff cancelável da `NetworkRetryPolicy`; renovação também consome tentativa. Uma falha transitória mantém o player em buffering. Falha definitiva de demanda notifica o controller. Falha de prefetch não encerra a sessão: quando o player precisar da parte, a leitura pode tentar novamente. Não há logs de cada tentativa normal.

As chamadas REST usam `DiscordApiClient` e o `DiscordRequestLimiter` existente: alvo global de 45 requisições por segundo, headers por bucket/rota, pausas de HTTP 429 e `retry_after`. Downloads CDN não passam pela cota REST. Limites de uploads continuam centralizados em `DiscordLimits.cs`, inclusive os 20 MiB por attachment e o desconto do envelope. Não foi criada uma constante alternativa para dividir uploads.

## 10. Vídeos antigos e containers

Vídeos já armazenados não são reprocessados, migrados ou reenviados. Chunks de tamanhos antigos, atuais ou misturados usam o mesmo índice por tamanho real.

MP4 com `moov` no final continua utilizável: o player pede o final por Range, lê os metadados e busca somente as partes relacionadas à posição escolhida. Não é necessário reconstruir tudo para acessar `moov`. A abertura pode esperar chunks do início e do final; não há garantia de latência idêntica à de um MP4 faststart.

Reprodução depende dos containers/codecs suportados pelo Chromium/WebView2 instalado. O arquivo virtual não adiciona codecs nem transcodifica MKV/MOV/WebM. Um formato não reproduzível conserva o fallback e o download completo. Arquivos com metadados incompletos ou maiores que o orçamento por chunk produzem erro explícito, não uma falsa reprodução bem-sucedida.

## 11. Faststart nos uploads novos

`Mp4FastStart.PrepareAsync` inspeciona `.mp4` antes de chunking/encryption. O original do usuário nunca é editado. Se `moov` já precede o primeiro `mdat`, não há cópia nem remux.

Para MP4 convencional elegível:

1. Ler somente headers das boxes de nível superior, com inteiros de 32/64 bits e limites verificados.
2. Exigir `ftyp` inicial, um `moov` e ao menos um `mdat`.
3. Ler `moov` em RAM, limitado a 32 MiB.
4. Colocar `moov` antes do primeiro `mdat`, preservando a ordem das demais boxes.
5. Percorrer containers conhecidos e corrigir cada `stco`/`co64` de acordo com a realocação do `mdat` que contém a amostra.
6. Copiar as demais boxes literalmente, em blocos de 128 KiB, para um temporário de upload.
7. Conferir tamanho final, dividir usando os limites existentes, criptografar e publicar pelo uploader atual.
8. Remover o temporário no fim, na falha ou no cancelamento.

Para cada offset de amostra `O` dentro de um `mdat` antigo `B`:

```text
O' = O + novoOffset(B) - antigoOffset(B)
```

Isso também cobre múltiplos `mdat` com deslocamentos diferentes. Bytes de `mdat`, descritores dos codecs, áudio, vídeo, resolução e duração não são recodificados. O arquivo baixado de um upload preparado contém o container faststart; não terá o mesmo hash de arquivo completo que a origem, embora os payloads de mídia permaneçam iguais.

O parser é conservador: aceita somente as boxes superiores suportadas, aninhamento até 32 níveis e até 100.000 boxes superiores. Recusa moov comprimido, estrutura fragmentada/auxiliar identificada, tabelas truncadas, offsets fora de `mdat`, tamanho inválido, moov acima do orçamento ou estendido até EOF. `stco` que exigiria promoção para 64 bits também faz fallback, em vez de alterar o tamanho da árvore de boxes sem segurança. Nenhum FFmpeg ou novo pacote .NET foi adicionado para esse recurso.

Estrutura não suportada, arquivo corrompido ou falha de preparação registra `Video.FastStart` e mantém o upload do original. Faststart é uma otimização, não requisito para armazenar vídeo. Cancelamento explícito é propagado. O transcoder Windows existente continua gerando a prévia 480p quando aplicável; seu MP4 gerado também passa pelo preparador antes do upload.

`TransferState.fastStartApplied` é somente local. O `sourcePath` continua apontando para a origem, não para o temporário. Na retomada, o preparador refaz a mesma realocação antes de continuar os chunks. Se um checkpoint faststart não puder ser reproduzido, a retomada falha sem combinar chunks de dois layouts. Transferências antigas que já enviaram partes sem faststart continuam naquele layout.

## 12. Progresso local e retomada

O registro usa `fileId`, nunca somente o nome. A chave SQLite é `videoProgress:<fileId>` na tabela `settings`; não existe tabela nova nem campo remoto. Exemplo do JSON persistido pelo serializer do registro:

```json
{
  "FileId": "identificador-estavel-do-video",
  "PositionSeconds": 120.0,
  "DurationSeconds": 130.0,
  "LastPlayedUtc": "2026-10-02T15:00:00+00:00",
  "Completed": false
}
```

A unidade é segundos com precisão decimal. JavaScript reporta no máximo uma vez por segundo durante `timeupdate`; C# grava no máximo uma vez a cada 10 segundos por arquivo durante fluxo normal. Pausa, fim, seek, fechar, trocar de vídeo e `pagehide` forçam atualização; fechar a sessão/app faz flush do último valor conhecido. Um encerramento abrupto pode perder o intervalo ainda não salvo.

Ao abrir, `loadedmetadata` restaura automaticamente a posição, sem redesenhar o player. Se a posição atingir 95% da duração, o registro fica `Completed=true` e `PositionSeconds=0`; a próxima abertura começa no início. Se a duração mudar mais que o maior entre 5 segundos e 1%, a posição anterior não é restaurada. Posições não finitas, negativas ou além da faixa segura são ignoradas/limitadas.

Excluir um arquivo pelo desktop remove seu registro. Exclusão feita por outro cliente pode deixar um registro local sem referência, eliminado na limpeza de 180 dias sem atualização. Rename/move preservam o progresso enquanto `fileId` permanece estável. Entradas sem identidade persistente não ganham identidade pelo nome; devem ser corrigidas no índice antes de prometer retomada entre sessões.

Logout faz flush, suspende novas atualizações e apaga os dados pela rotina existente; conectar outra configuração reativa o tracker. Falha de SQLite registra `Video.Progress`, mas não impede a reprodução. O progresso não é sincronizado com Discord e não compartilha CTS com download.

## 13. Download completo em paralelo

`DownloadAndAssembleAsync` continua baixando todos os chunks, validando integridade e montando o arquivo, inclusive com `videoPrefetchChunks=0`. O fluxo de download usa seus checkpoints/temporários e seu token de cancelamento. Fechar o player não cancela Baixar; cancelar Baixar não fecha o player. Não há compartilhamento de cache entre esses fluxos, evitando vincular suas vidas e autenticações.

`VideoNetworkPriority` marca as tentativas CDN de reprodução/prefetch como prioritárias. A cópia do download tradicional cede de forma assíncrona antes de ler seu próximo bloco enquanto houver uma tentativa de vídeo ativa. O lease é liberado antes do backoff. Isso reduz competição, mas não é QoS de rede: uma requisição já em voo e buffers do sistema ainda podem consumir banda. Não altera os controles de upload nem a concorrência global REST.

## 14. Implementação equivalente em Flutter

Nenhuma alteração no aplicativo Flutter é necessária para ler o container faststart ou manter o formato remoto existente. Para reproduzir o comportamento novo, implementar um núcleo Dart independente do widget/player:

| Desktop | Equivalente necessário em Dart/plataforma |
| --- | --- |
| `VideoChunkIndex` | Prefixos `List<int>` com tamanhos reais e busca binária |
| `DiscordVideoChunkProvider` | HTTP/cliente Discord existente, renovação e autenticação GCM |
| `IVirtualFile.ReadAsync` | `Future<Uint8List> read(offset, length, cancel)` |
| `VideoStreamSession` | Cache por índice, Future compartilhado e geração cancelável de prefetch |
| WebView2 adapter | Data source nativo do player com leitura por offset; não presumir que aceita Stream Dart |
| `VideoPlaybackTracker` | Banco/configuração local por `fileId`, segundos, throttle e conclusão |
| `Mp4FastStart` | Parser/remux anterior a chunking, mantendo bytes e criptografia |

Pseudocódigo do núcleo, não uma API pronta de plugin:

```dart
class VirtualVideo {
  final List<int> lengths;
  final List<int> starts; // soma dos tamanhos plaintext reais
  final Map<int, Uint8List> cache = {};
  final Map<int, Future<Uint8List>> pending = {};
  final int prefetchChunks; // 0..6, default 1

  Future<Uint8List> read(int offset, int count, CancelSignal cancel) async {
    final output = Uint8List(min(count, totalLength - offset));
    var copied = 0;
    while (copied < output.length) {
      cancel.throwIfCancelled();
      final i = findChunkByPrefix(offset + copied);
      cancelObsoletePrefetch(i);
      final bytes = cache[i] ?? await sharedAuthenticatedDownload(i, cancel);
      final inside = offset + copied - starts[i];
      final take = min(output.length - copied, bytes.length - inside);
      output.setRange(copied, copied + take, bytes, inside);
      copied += take;
      evictOutsideBoundedWindow(i);
    }
    return output;
  }
}
```

No desenvolvimento real, adicionar validação de offsets, fixação durante cópia, teto de RAM, semáforos e cancelamento cooperativo do cliente HTTP. Um Future compartilhado não deve ser abortado enquanto outro leitor ainda depende dele. Criar geração nova em seek e descartar bytes/resultados obsoletos. Não gravar chunks em `.part`, `.cache` ou `.mp4` durante reprodução.

O adaptador do player Android/iOS depende da API disponível e precisa ser validado naquela plataforma. Uma API que só aceite caminho de arquivo e obrigue materialização completa não implementa este requisito; usar data source nativo por faixa/plataforma. Não copiar o host virtual do WebView2 para um servidor HTTP mobile como solução automática.

Preservar `fileId`, listas de message IDs, índices, tamanhos plaintext, papéis, envelope, derivação e autenticação. Não calcular índices pela constante de 20 MiB nem tratar URL CDN como identificador permanente. O limite de anexos continua pertencendo ao upload. Progresso pode seguir o mesmo schema lógico local sem mudar os dados do Discord.

## 15. Logs finais

`AppLogger.Info` e `Debug` continuam sem persistência. Nenhum evento normal de abrir, reproduzir, Range, seek, cache hit/miss, prefetch, fechamento ou coleta de RAM gera arquivo de log. As novas áreas persistidas são `Video.Stream`, `Video.FastStart` e `Video.Progress`, além das exceções não tratadas já existentes.

Os erros adicionados usam mensagens saneadas: não registram tokens, chaves, URLs assinadas, paths locais ou nomes privados de mídia. Falha especulativa recuperável não gera log por chunk. Não foi adicionado framework de logging nem uma opção de debug de produção.

## 16. Validação e reprodução dos testes

O projeto de testes executável é `tests/Organessonium.NetworkTests`. `VideoStreamingTests.cs` cobre leitura em fronteira, último chunk menor, 10/20 MiB e tamanhos misturados, seek distante sem partes intermediárias, cache hit, deduplicação, prefetch 0/3, cancelamento, RAM liberada, limite de respostas, persistência/conclusão/duração/logout, renovação de URL, GCM adulterado e independência do download normal/criptografado.

O teste de faststart cobre `stco`, `co64`, header estendido, bytes de `mdat` preservados, arquivo já preparado e fallback de arquivo inválido. `WebViewVideoTests.cs` abre **o player JavaScript embarcado e o adaptador real** em uma janela WPF, com provider de fixture em RAM e sem servidor. Verifica que a abertura não baixa todas as partes, reprodução com frames decodificados, seek até 120 segundos quando a fixture permite, mesma duração antes/depois de faststart, retomada e configuração na interface. O teste confere SHA-256 dos payloads `mdat`, incluindo áudio e vídeo. A fixture contém partes variáveis e a última menor.

```powershell
dotnet build Organessonium/Organessonium/Organessonium.csproj -c Release
$env:ORGANESSONIUM_VIDEO_TEST_FILE = '<caminho de um MP4 de teste com moov no final>'
dotnet run --project tests/Organessonium.NetworkTests/Organessonium.NetworkTests.csproj -c Release
node --check Organessonium/Organessonium/web/app.js
node --check Organessonium/Organessonium/web/bridge.js
node --test tests/library-view-isolation.test.js
```

Sem fixture local, o teste WebView obtém o trailer público de teste da W3C. Isso é infraestrutura de teste, não caminho do app. `ORGANESSONIUM_VIDEO_TEST_OBSERVE_SECONDS` permite até 45 segundos de observação da janela. `ORGANESSONIUM_VIDEO_TEST_EXPORT_DIR`, quando aponta para diretório de QA existente, exporta uma cópia preparada para inspeção externa. Esses arquivos de QA não são criados pelo streaming de produção.

Também verificar manualmente na biblioteca autorizada: abrir vídeo grande; observar o primeiro frame sem reconstrução inteira; pausar/reabrir/app restart; seek para início/meio/final; valores 0, 1 e 6; troca 480p/original; internet lenta/interrompida; CDN expirada; fechar durante prefetch; iniciar/cancelar download em paralelo; formato sem codec; chave personalizada incorreta. Para auditoria externa opcional, comparar tracks/duração/resolução com `ffprobe` ou outro inspetor já disponível. FFmpeg usado para gerar fixture de QA não é dependência nem distribuição do aplicativo.

## 17. Limites da evidência e casos não prometidos

- Testes usam fixtures e HTTP simulado para Discord/criptografia; não certificam todos os arquivos de um servidor real ou uma instalação mobile.
- O contrato virtual foi validado no WebView2 real instalado. Outros runtimes precisam dos mesmos testes de Range/seek e memória.
- Codecs não suportados continuam não reproduzíveis. Não há transcodificação automática de streaming.
- Faststart conservador pode manter o original sem otimização; fragmentado/comprimido, offsets auxiliares e promoção `stco -> co64` não são remuxados.
- REST para localizar mensagens pode limitar a velocidade de abertura antes da primeira leitura CDN.
- O guard de 64 MiB por chunk, o cache de 192 MiB e respostas de 512 KiB são limites do player, não mudança do protocolo/upload de 20 MiB.
- `ProgressiveDownload.cs` e `ResolveProgressivePartsAsync` permanecem como código legado sem chamadas do fluxo atual de prévia de vídeo. Não são o mecanismo ativo descrito aqui.
- O cache de mídia do app não recebe vídeo durante reprodução. O perfil WebView2 ainda contém os dados normais do runtime; não se promete impedir todos os writes internos do navegador/sistema operacional.
