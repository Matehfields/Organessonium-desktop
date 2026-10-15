# Organessônium

Este repositório contém a versão desktop C# WPF + WebView2 do Organessônium. O aplicativo usa o Discord como armazenamento de payloads, com um índice hierárquico por pasta para que a abertura da raiz não dependa da quantidade total de arquivos no servidor.

## Arquitetura de dados

- `Bridge/AppController.cs` coordena upload, download, previews, transferências e a ponte WebView2.
- `DesktopEntryPoint.cs` inicia o coletor temporário `Services/Diagnostics` antes de criar `App`; o coletor observa falhas, tempos e responsividade sem controlar os fluxos de negócio.
- `Services/ForumUploader.cs`, `ForumUploader.MultiAttachment.cs` e `ForumUploader.Shared.cs` preservam o transporte dedicado e encaminham arquivos elegíveis à fila compartilhada, com até dez attachments criptografados independentes por mensagem.
- `SharedStorageManager` e seus módulos `Recovery`, `Cleanup`, `Mutations`, `Reads` e `ReferenceScan` mantêm catálogo remoto, reservas, referências e limpeza recuperável; `SharedPayloadBatcher` agrega descriptors por pool sem juntar plaintexts.
- `Services/UploadPartContent.cs` prepara e transmite anexos sob demanda; `UploadBodySlot.cs` separa workers de transmissão das respostas ainda pendentes.
- `Services/ForumDownloader.cs` resolve mensagens e attachments referenciados pelo metadata, autentica as partes em RAM, grava plaintext retomável e usa `DownloadAssembler` para montar o arquivo final na ordem dos índices lógicos.
- `Services/AppRegistry.cs` resolve o mapa `folderId -> threadId`, abre somente o índice da pasta requisitada no fórum `folders` e atualiza os caches afetados por cada operação.
- `Services/MetadataService.cs` publica e lê o mapping no `names` e os metadados de filhos diretos nos threads de índice de `folders`.
- `Services/SerialIndexService.cs` lê, valida, mantém em memória e publica o serial binário de uma única pasta por vez no canal `serial`.
- `Services/FolderSerialCodec.cs` codifica e decodifica o formato binário versionado `OSER` usado pelos seriais.
- `Services/SettingsStore.cs` mantém configurações, os caches locais `app_entries` e `folder_index_map`, previews e estados de transferência no diretório de dados.
- `Services/VideoStreamSession.cs`, `VideoChunkIndex.cs` e `DiscordVideoChunkProvider.cs` expõem o vídeo como arquivo virtual assíncrono, com chunks autenticados em RAM.
- `Services/PreviewResourceRegistry.cs` e `Bridge/WebView2PreviewAdapter.cs` entregam prévias por host HTTPS virtual interceptado pelo WebView2, sem servidor local.
- `Services/Mp4FastStart.cs` prepara MP4 elegível por leitura virtual antes do upload, preservando os payloads de áudio/vídeo.
- `Services/FfmpegMediaService.cs` executa o motor de mídia separado/oculto, extrai miniaturas e entrega MP4 fragmentado por pipe; `FfmpegPreviewUpload.cs` gera a prévia completa em staging oculto ao lado da origem e a envia com buffers limitados. `VideoPreviewWorker.cs` conserva o auxiliar Windows para imagens e fallback, sem saída temporária.
- `Services/VideoPlaybackProgress.cs` salva e restaura a posição local de reprodução por `fileId`.
- `Services/Sync/SyncBackgroundHost.cs` mantém o sincronizador sem depender da janela; `SyncEngine`, `SyncPlanner`, `SyncScanner` e `FolderWatcher` detectam, comparam e aplicam alterações.
- `Services/Sync/SyncStateStore.cs` persiste vínculos, estado-base e decisões; `CloudSyncRepository.cs` reutiliza chunks, criptografia, transferências e índices do Discord.
- `Services/AppRegistry.Sync.cs` integra computadores e árvores sincronizadas à aba Dispositivos; `Bridge/AppController.Sync.cs` e `web/sync.js` apresentam os comandos, estados e conflitos.
- `App.Background.cs`, `MainWindow.Sync.cs`, `TrayManager`, `SingleInstanceHost` e `StartupManager` separam o ciclo de vida gráfico da execução em segundo plano.

Um arquivo é identificado por `fileId`; seu metadata associa esse identificador ao `threadId` de payload, às referências dos chunks, ao nome e MIME originais, ao tamanho original `sizeBytes`, ao `parentFolderId` e às referências de prévias. Uploads novos usam `chunkRefs: [{ index, messageId, attachmentId }]`, com índice zero-based e identificação exata do anexo; arquivos antigos continuam usando `chunks: string[]`, um attachment por mensagem. Pastas usam `folderId`; o `parentFolderId` estabelece a árvore. Os IDs são preservados em rename e move. Novas publicações de metadados são cifradas integralmente com encrypted metadata V1; o desktop autentica e descriptografa antes de localizar os blobs. JSON antigo em claro continua sendo lido, sem migração ao abrir.

### Armazenamento compartilhado V2

Novos conteúdos de até 1 GiB original, inclusive o limite exato, usam pools no fórum `files`; conteúdos maiores continuam dedicados. Cada pool guarda até 256 fileIds distintos, incluindo reservas/históricos ainda retidos. Chunks, versões e previews não ocupam vagas adicionais. `storage: { version: 2, mode: "shared", storageId }` identifica o layout no metadata e nos snapshots; dedicado novo usa `mode: "dedicated"`. Ausência de storage continua sendo legado dedicado, sem migração automática. O documento completo de interoperabilidade é [implementation2.md](implementation2.md).

O controle inicial shared é cifrado e não tem attachment. O catálogo cifrado `storageThread` e o journal operacional V2 ficam em uma thread técnica de índice no fórum `folders`, identificada pelo mapping `folderId: storage-v2`. Somente esse mapping é publicado em `names`; não existe card dessa pasta na biblioteca. Metadados de cada arquivo continuam no índice da respectiva pasta. O catálogo guarda ownerId, thread/control IDs, revision, estado e membros/pendingVersions. SQLite não decide a capacidade: o documento remoto selecionado é relido antes de alterá-la. O owner é UUID persistido por instalação, vinculado ao MachineGuid/SID no desktop. Só esse escritor reserva/transmite/limpa fisicamente o pool. Outra instalação cria pools com identidade diferente; reads, organização e versões continuam compatíveis. Não há CAS/transação distribuída no Discord, nem adoção automática de identidade perdida.

`storageBatchPlan` cifrado registra fileId/versionId/role/index/count/filename/tamanho antes de cada multipart. `storageBatchReceipt` vem depois de validar toda a resposta e antes dos checkpoints participantes. Isso permite reparar ACK/checkpoint perdido sem reenviar chunks confirmados. Uma fila limitada agrega partes de arquivos/roles distintos, fecha batches incompletos pela janela de 250 ms e mantém os gates de rede/memória existentes. Dez arquivos de uma parte podem formar um único multipart, sem criar dez posts. Grandes dedicados mantêm seu transporte anterior.

Thumbnails shared usam `preview.thumbnailRef: { messageId, attachmentId }`, nunca seleção pelo primeiro anexo. 480p conserva `preview.video480p.chunkRefs`. DTOs são resolvidos por `(threadId,messageId)` com cache limitado/deduplicado; cada download usa attachmentId e contexto criptográfico do arquivo/versão. CDN expirada renova o mesmo ID. A UI usa requestId do metadata/arquivo, e o cache da miniatura inclui fileId, versionId e os dois IDs do anexo, evitando colisões entre cards do mesmo pool. OSER continua v3; seus consumidores hidratam detalhes quando necessário.

Exclusão definitiva persiste `storageCleanup` antes de retirar metadata, depois registra released. GC verifica índices remotos, versões, trash, revisões sync, claims, reservas e referências de upload/download/preview. Em shared, faz PATCH somente com IDs dos attachments a conservar, relê/confere o snapshot e valida a resposta. Só uma mensagem vazia é removida, nunca o controle/post. Erro mantém intent para retomada; 404 confirmado do locator permite recuperar a janela entre exclusão de metadata e released. Pools de outro owner, reads/claims externos e POSTs inconclusivos permanecem pendentes. O app avisa a pendência após retirar o item do índice, sem arriscar attachments de outros arquivos.

Claims de metadata, fences de exclusão, planos, recibos e referências de leitura são eventos cifrados no índice técnico `storage-v2`, não em `names`. Alteração concorrente retorna conflito; publicação de versão revalida conteúdo sob claim. Leases de escrita por conta/thread impedem arquivamento durante atividade e só desarquivam quando necessário, respeitando locked/403. Reads não reativam o pool: registram referência no índice técnico, liberada ao concluir/fechar, ou conservada com checkpoint pausado. Reinício reconcilia operações desse owner; não expira referências estrangeiras por relógio. O leitor também aceita registros V2 antigos em `names`, sem criar ou atualizar esses registros nesse canal; um catálogo antigo efetivamente modificado passa a ser publicado no índice técnico. Não há exclusão automática dos registros antigos. A enumeração de todos os índices/pools para prova de GC ocorre somente quando há limpeza pendente, não para renderizar a raiz.

A manutenção explícita `tools/NamesScanner --migrate-storage-journal` transfere o journal V2 antigo de names para o índice técnico, com o app fechado. Copia e autentica todos os destinos antes de iniciar remoções; confere origem/destino novamente por registro e preserva mappings e blobs. O marcador cifrado `legacyNamesMessageId` permite retomada idempotente e preserva a ordem lógica dos eventos. O modo `--audit-metadata-layout` é somente leitura e imprime contagens por tipo. O comando não transfere metadados `file`/`folder` legados para o índice técnico; tais objetos exigem migração por pasta. O protocolo e o procedimento estão em [implementation2.md](implementation2.md).

## Índice serial

Cada pasta, inclusive `root`, possui exatamente um serial `.bin`. O caption `folderSerial` e o anexo são cifrados nas novas gravações. O caption guarda `folderId` e SHA-256 do anexo cifrado; o anexo `serial.bin` contém o envelope JSON V1 em UTF-8, que revela `folderSerialData` com OSER em Base64 após autenticação. Internamente, OSER guarda os filhos diretos, IDs, relações, MIME, datas, previews, cores, favorito e agregados. Captions plaintext e anexos OSER antigos permanecem legíveis.

O serial é o fast-path persistente. Ele é mantido em `OrganessoniumData\Serials` e, depois da primeira leitura, também em memória durante a sessão. O codec lê `OSER` v1 a v4, mas publica v3 para manter o formato binário compartilhado. `sizeBytes` por filho existe somente em v4; o agregado da pasta preserva o total de bytes. Abrir a raiz lê somente o serial da raiz. Abrir `/Fotos` lê somente o serial de `Fotos`; não lê os seriais das subpastas. Se o serial local estiver ausente, o app baixa apenas o anexo indicado pelo mapping daquela pasta, valida o SHA-256 e grava o arquivo localmente por escrita atômica. Antes de reutilizar um serial local durante uma sincronização, o app confirma uma vez por sessão que a mensagem remota indicada pelo mapping ainda existe. Mapping sem `serialMessageId` ou mensagem removida faz o serial ser republicado e o mapping ser atualizado. Os posts do fórum `folders` são arquivados após a leitura e também são reconciliados na inicialização, mantendo os índices antigos fora do limite de posts ativos.

Um serial novo só passa a ser o atual depois que o mapping correspondente em `names` é confirmado. Se essa confirmação falhar, o anexo provisório é removido e o cache local é invalidado para que a próxima reconciliação não trate conteúdo não confirmado como publicado. Após atualizar os mappings na inicialização, uma manutenção em segundo plano remove anexos `folderSerial` duplicados do bot somente quando o mapping atual daquela pasta ainda aponta para o serial preservado. O serial não contém a lista de chunks; ao abrir a prévia ou baixar um arquivo sem detalhes no cache, o desktop busca o metadata completo por `namesMessageId` em `names` ou no thread de índice do pai e confere `fileId` e `threadId` antes de usar os payloads.

Quando a mensagem do serial já existe, atualizar seus filhos substitui o anexo `.bin` nessa mesma mensagem e atualiza seu SHA-256 no mapping, preservando o `serialMessageId`. Uma mensagem nova só é criada se não houver referência ou se a mensagem tiver sido removida. A comparação usa os campos efetivamente publicados na versão OSER, incluindo os agregados; valores de tamanho individuais omitidos em v3 não provocam reenvios sem mudança. O cache em memória conserva o mesmo conteúdo codificado do cache em disco.

Depois de renderizar o conteúdo conhecido, o app reconcilia em segundo plano o thread remoto daquela mesma pasta. O resultado atualiza `app_entries`, o serial correspondente e a interface visível quando houver mudança. A sincronização não percorre todos os seriais nem todos os threads do fórum durante a inicialização. Os cards de pastas não exibem contagem de arquivos nem solicitam estatísticas por pasta durante a renderização. Pastas são únicas por pai e nome: uma nova solicitação para a mesma pasta reutiliza o `folderId` existente. Durante a reconciliação, duplicatas legadas são consolidadas na pasta canônica, com os filhos preservados antes da exclusão do metadata, mapping, serial e thread de índice duplicados. Durante essa leitura, a interface mostra carregamento em vez de uma pasta vazia temporária. Uma leitura com mensagens, mas sem nenhum metadata válido, preserva o cache; uma leitura concluída sem mensagens ou com apenas o cabeçalho válido confirma uma pasta vazia e remove os filhos antigos. A tela Arquivos exige associação de `parentFolderId` com a raiz antes de exibir itens nela.

`AppRegistry` deduplica uma reconciliação em andamento por `folderId`. A paginação e o parsing de um thread ou do canal `names` usam `SearchWorkers` com concorrência limitada. `DiscordApiClient` mantém o controle de rota e respeita o `retry_after` de HTTP 429, portanto uma verificação em segundo plano não dispara requisições ilimitadas nem duplica o trabalho de uma navegação.

### Limites e agendamento da API

`Services/DiscordLimits.cs` centraliza os limites de texto (2.000 caracteres), título de thread (100 caracteres), paginação (100 mensagens), anexo (20 MiB por arquivo) e quantidade de anexos (10 por mensagem). Cada chunk desconta os 70 bytes do envelope criptográfico do limite individual; não há bloqueio local de 25 MiB para a soma do multipart. Uploads agrupados em produção mantêm dez anexos por mensagem, até 200 MiB de conteúdo cifrado; somente o lote final incompleto ou uma rejeição específica do servidor reduz essa quantidade. `UploadMessageTargetBytes` acompanha esse total, e `MaxPendingUploadMessages` limita a 64 os lotes em preparação/envio/confirmação, compartilhados entre uploads. Esses 64 lotes são descritores, não 64 buffers de 200 MiB. Metadados maiores que o limite de texto continuam sendo enviados como anexo. Para testar outros valores, altere esse arquivo e recompile. Alterar o orçamento de anexo também altera a divisão dos uploads; transferências já parcialmente confirmadas preservam a divisão registrada no checkpoint.

O limite global documentado pelo Discord é 50 requisições por segundo, não por minuto. `DiscordRequestLimiter` espaça o início das chamadas REST com alvo de 45/s, compartilhado entre clientes do mesmo bot no processo. As cotas por rota são aprendidas pelos headers `X-RateLimit-Bucket`, `Limit`, `Remaining` e `Reset-After`; IDs de mensagens compartilham a rota e canais diferentes mantêm cotas separadas. A cota informada pelo servidor fica separada das reservas em andamento: cada chamada paralela ocupa uma vaga durante o envio, sem descontá-la permanentemente de cada resposta. Headers mais recentes podem recuperar vagas; respostas atrasadas da mesma janela não aumentam a cota. O agendador usa a ordem das requisições, `Date` e os dados de reset quando disponíveis, com tratamento conservador quando faltam timestamps. Enquanto existem chamadas em andamento, a fila reavalia vagas em até 50 ms, sem manter um lock durante a rede. Um HTTP 429 bloqueia novas chamadas desde a leitura dos headers até interpretar `retry_after`, mesmo que outra resposta informe vagas positivas. HTTP 429 global pausa todas as rotas; HTTP 429 local pausa somente o bucket afetado, respeitando o maior `Retry-After`/`retry_after` e uma margem de segurança. Esperas são assíncronas, canceláveis e preservam os callbacks de progresso das transferências. Downloads de anexos pela CDN não consomem essa fila REST. Outros dispositivos usando o mesmo bot não compartilham a fila local, e limites desconhecidos ou alterações do servidor ainda podem produzir 429; nesse caso o app aguarda e tenta novamente.

Para compatibilidade com os seriais existentes, o canal `serial` também aceita os índices globais diretos `favorites` e `synchronized`, sem depender de um mapping desktop. O serial `favorites`, de tipo próprio, mantém a lista plana de itens favoritos. O serial `synchronized` representa a raiz lógica `/@synchronized`; essa raiz técnica não é exibida na tela Arquivos, enquanto seus filhos diretos aparecem em Dispositivos e usam `deviceType` igual a `phone` ou `pc`. Ao abrir Dispositivos, o desktop recompõe os seriais apenas quando encontra filhos diretos de `synchronized` nos metadados de `names`; caso contrário, lê o serial existente e, se ele estiver ausente, os metadados do thread de índice apontado pelo mapping. A presença isolada do metadata da raiz não publica um serial vazio. Os filhos são associados por `folderId` e `parentFolderId`. No formato OSER v3, os bits de favorito, telefone e computador são preservados.

Ao atualizar um serial global existente, o desktop substitui o anexo `.bin` da mesma mensagem do Discord. Para `favorites`, se houver um mapping correspondente em `names`, ele também atualiza o SHA-256 e corrige um ponteiro que tenha ficado apontando para uma mensagem removida.

Os seriais de `favorites` e `synchronized` usam o mesmo cache local persistente dos seriais de pastas. Ao abrir essas abas, o desktop mostra primeiro o serial local válido; uma atualização explícita consulta novamente o servidor. Arquivos, Favoritos e Dispositivos mantêm coleções de interface separadas, de modo que cards de uma aba não entram na contagem ou listagem de outra. Leituras concorrentes das abas especiais só publicam a resposta mais recente.

## Metadados e transporte criptografado

No layout desktop atual, o canal de texto `names` é pequeno e contém mappings `folderId -> threadId`; a mensagem do mapping é um envelope cifrado cujo JSON interno inclui também a referência do serial atual quando ele existir. O ID da própria mensagem de `names` é mantido no cache local para atualização e remoção direta. Para interoperar com metadados publicados diretamente em `names`, o leitor também aceita metadata de arquivo ou pasta nesse canal quando ele é referenciado por um serial global ou de dispositivo. Ao alterar apenas o favorito de um item desse tipo, o JSON remoto original é carregado e atualizado integralmente para preservar chunks, criptografia, previews e todos os demais campos.

O fórum configurado em `foldersForumId` corresponde ao canal `folders` e contém somente posts `index-*` de índice de pasta. Cada post de índice possui uma primeira mensagem `folderIndexHeader` para identificação e os metadados `file` e `folder` dos filhos diretos. O índice da raiz de uma pasta sincronizada também recebe revisões `syncRevision` e confirmações `syncApplied`, cifradas nas novas publicações; o marcador textual antigo `syncApplied:<revisionId>` continua legível. Esses registros não viram cards nem entram nos seriais. O fórum configurado em `appsForumId` corresponde ao canal `files` e contém somente os posts de payload usados por upload. O `threadId` de payload indica um post dedicado ou um pool compartilhado por vários arquivos, conforme `storage`; a identidade lógica continua sendo `fileId`. O `threadId` de índice de uma pasta é resolvido pelo mapping em `names`.

Regra principal: a presenca de um campo significa que o recurso existe. A ausencia significa que o recurso nao existe ou nao se aplica.

O metadata novo nao deve incluir campos nulos, arrays vazios, objetos vazios, `enabled = false`, campos temporarios da UI, campos de video em imagens, nem campos de arquivo em pastas. Arquivos e pastas são publicados no thread de índice do respectivo pai, não no `names`. Os exemplos abaixo representam o JSON interno, que é cifrado antes de publicar; não são o conteúdo visível das mensagens novas.

`Services/MetadataEncryption` centraliza o router, envelope, AES-256-GCM, HKDF-SHA256 e erros. O envelope `organessonium-metadata`, versão 1, expõe somente parâmetros técnicos e ciphertext/tag. Usa salt de 16 bytes, nonce aleatório de 12 bytes, tag de 16 bytes, Base64 padrão com padding, UTF-8 sem BOM e AAD definido pela versão. A chave é derivada da master key existente com domínio separado do conteúdo dos arquivos. O modo padrão reutiliza material embutido no app e não é um segredo exclusivo do usuário; confidencialidade baseada em segredo exige chave personalizada. Nenhuma chave vai no envelope.

Arquivos, pastas, mappings, cabeçalhos, seriais, revisões, confirmações e rótulos de payload são protegidos. Novos posts têm títulos aleatórios opacos. Dados legados e títulos antigos não são regravados automaticamente. O backend ainda observa canais/posts, IDs externos, tamanhos, horários e agrupamentos. O cache SQLite, estados de transferência e previews locais continuam operacionais e podem conter dados em claro; seriais novos locais usam os mesmos bytes cifrados da nuvem. Falhas de versão, chave, tag, Base64 ou JSON não viram pasta vazia nem fazem downgrade para plaintext.

`MetadataService` aplica o codec antes de criar inline ou `metadata.json`, e antes de materializar os objetos. Updates usam PATCH da mesma mensagem, inclusive anexo↔inline; removem attachments anteriores na confirmação, sem apagar o metadata antigo antes do envio. Temporários de metadata contêm somente o envelope cifrado. Leituras não causam migração; uma modificação efetiva publica V1. A especificação criptográfica e o vetor público de teste estão em [implementation old.md](<implementation old.md>). O transporte multi-attachment está em [implementation old 2.md](<implementation old 2.md>); o inventário de compatibilidades está em [migration.md](migration.md).

### Mapping no `names`

```json
{
  "schemaVersion": 2,
  "entryType": "folderIndex",
  "folderId": "root",
  "threadId": "id-do-thread-de-indice-root",
  "serialMessageId": "id-da-mensagem-do-serial",
  "serialSha256": "sha256-do-anexo-binario"
}
```

`serialMessageId` e `serialSha256` são opcionais até a primeira sincronização do serial. O ID da mensagem de `names` não é colocado dentro do JSON: ele é o ID retornado pelo Discord.

### Cabeçalho do thread de índice

```json
{
  "schemaVersion": 2,
  "entryType": "folderIndexHeader",
  "folderId": "root",
  "parentFolderId": "root",
  "name": "root"
}
```

### Configuração manual do Discord

Antes de usar este protocolo, configure os quatro canais no assistente do app: o canal de texto `names`, o fórum `folders`, o fórum `files` e o canal de texto `serial`. O bot precisa de acesso de leitura ao histórico, envio de mensagens e anexos, criação de posts públicos nos fóruns, edição e exclusão das próprias mensagens e gerenciamento de threads.

Configure os quatro canais e preserve os dados existentes. O desktop conserva os leitores legados registrados em migration.md; não remove/regrava objetos apenas por lê-los. Mappings e índices existentes continuam usados, e pools shared novos coexistem com dedicados antigos. Não remova metadados globais ou payloads para ativar V2. Qualquer migração ou descarte de dados de desenvolvimento deve ser uma decisão explícita e validada, não parte da atualização de leitura.

### Imagem com thumbnail

Os exemplos abaixo descrevem campos compatíveis dos layouts anteriores/dedicados. Escrita shared nova acrescenta `storage` e usa `thumbnailRef` completo, como especificado em implementation2.md; não usar os exemplos legados para emitir metadata compartilhado novo.

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "f2ebaea53f1146bbaf967132e7d031f3",
  "threadId": "123456789012345678",
  "name": "1745186438334.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 317584,
  "uploadedAt": "2026-06-30T20:13:00Z",
  "batchId": "ca433d8f1b",
  "chunkRefs": [
    { "index": 0, "messageId": "123456789012345678", "attachmentId": "123456789012345680" }
  ],
  "preview": {
    "thumbnailMessageId": "123456789012345679"
  }
}
```

### Video sem preview 480p

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "id-estavel",
  "threadId": "id-da-thread",
  "name": "video.mp4",
  "mimeType": "video/mp4",
  "sizeBytes": 40765943,
  "uploadedAt": "2026-06-30T20:13:00Z",
  "batchId": "id-do-lote",
  "chunkRefs": [
    { "index": 0, "messageId": "mensagem-inicial", "attachmentId": "anexo-0" },
    { "index": 1, "messageId": "mensagem-seguinte", "attachmentId": "anexo-1" }
  ],
  "preview": {
    "thumbnailMessageId": "thumbnailMessageId"
  }
}
```

### Video com preview 480p

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "id-estavel",
  "threadId": "id-da-thread",
  "name": "video.mp4",
  "mimeType": "video/mp4",
  "sizeBytes": 40765943,
  "uploadedAt": "2026-06-30T20:13:00Z",
  "batchId": "id-do-lote",
  "chunkRefs": [
    { "index": 0, "messageId": "mensagem-inicial", "attachmentId": "anexo-0" },
    { "index": 1, "messageId": "mensagem-seguinte", "attachmentId": "anexo-1" }
  ],
  "preview": {
    "thumbnailMessageId": "thumbnailMessageId",
    "video480p": {
      "sizeBytes": 1234567,
      "chunkRefs": [
        { "index": 0, "messageId": "mensagem-preview", "attachmentId": "preview-0" },
        { "index": 1, "messageId": "mensagem-preview", "attachmentId": "preview-1" }
      ]
    }
  }
}
```

### Pasta

```json
{
  "schemaVersion": 2,
  "entryType": "folder",
  "folderId": "id-estavel",
  "name": "Fotos",
  "createdAt": "2026-06-30T17:01:00Z",
  "parentFolderId": "root",
  "folderColor": "#C4C7C5"
}
```

## Payloads criptografados

Todo novo attachment de conteúdo do Organessônium passa por `EncryptionService` antes de chegar ao Discord: arquivo original, chunks, miniaturas, prévias de imagem, `video480p` e imagens anexadas em Markdown. O JSON interno do metadata recebe o objeto mínimo `encryption` abaixo; o objeto completo é protegido pelo codec de metadados, sem cifrar novamente os chunks:

```json
"encryption": {
  "version": 1,
  "mode": "standard",
  "salt": "base64url-sem-padding-do-salt-por-arquivo"
}
```

O `salt` usa exclusivamente Base64URL sem padding, com os caracteres `A-Z`, `a-z`, `0-9`, `-` e `_`. Esse é o formato gravado nas mensagens do servidor e usado tanto na validação do índice quanto na derivação da chave.

O blob `ORGAESGC` v1 contém magic, versão, papel do payload, índice 1-based e quantidade total do chunk, tamanho plaintext, salt de 16 bytes, nonce GCM de 96 bits, ciphertext e tag de autenticação. O nonce é gerado por CSPRNG para cada payload. A chave por arquivo é derivada por HKDF-SHA-256 a partir da master key, do `fileId` normalizado e do salt do arquivo. O header completo é o AAD do AES-256-GCM, então trocar, truncar ou reordenar payloads invalida a autenticação.

Os attachments criptografados usam nomes técnicos, como `payload.bin`, `chunk-1.bin`, `preview.bin`, `video-preview-1.bin` e `image.bin`, sempre com `application/octet-stream`. O rótulo interno unitário usa `file:<fileId> role:<role> chunk:<parte>/<total> encryption:v1`; um batch usa `chunks:<partes-separadas-por-vírgula>/<total>`. Os números desses rótulos são one-based, enquanto `chunkRefs.index` é zero-based. Ambos ficam dentro de `payloadReference` cifrado nas novas mensagens. O download decodifica esse transporte antes dos parsers existentes; rótulos antigos continuam sendo lidos. Nome, MIME e IDs internos ficam no metadata cifrado.

### Modos de chave

- **Padrão:** funciona automaticamente em instalações compatíveis. A master key é derivada internamente, nunca é exibida, enviada ao Discord ou colocada no metadata.
- **Chave personalizada:** a frase do usuário passa por PBKDF2-HMAC-SHA256 com salt aleatório e 600.000 iterações para produzir uma master key de 256 bits. O SQLite guarda apenas o material protegido pelo DPAPI do Windows para o usuário atual, nunca a frase ou a chave em plaintext.
- **Recuperação:** Configurações permite exportar e importar um arquivo `.organessonium-key` versionado. Esse arquivo é sensível e deve ser guardado fora do computador, pois permite recuperar arquivos protegidos pela chave personalizada.

Mudar o modo afeta somente uploads novos. Cada arquivo usa o modo registrado no próprio metadata, sem recriptografar conteúdo existente.

## Upload e transferências

Uploads dedicados criam o post com somente o chunk 0 e enviam os demais em batches de até dez. Shared começa com controle sem arquivo e agrega attachments de arquivos/roles diferentes. Cada attachment mantém ORGAESGC e o `ChunkCount` global daquele arquivo/role. A resposta inteira é validada; checkpoints são atômicos por arquivo, com recibo remoto para recuperar distribuição parcialmente salva entre participantes. A retomada usa chunks confirmados; o progresso visual usa bytes lógicos transmitidos. Checkpoints antigos sem `payloadLayoutVersion` continuam unitários; dedicados multi-attachment usam 2 e compartilhados usam 3. O download deduplica mensagens e seleciona pelo attachmentId. `chunkRefs` ou layout declarado inválido não vira legado silenciosamente.

O upload da biblioteca mantém um contexto de índice em `AppRegistry.UploadBatch`: cada índice de pasta usado pelo lote é desarquivado uma vez, recebe os metadados individuais e só é arquivado ao encerrar o último lote ativo. Cancelamento e falha liberam o contexto. A reconciliação não arquiva índices/pools que estejam em uso. Seriais e reservas confirmadas são reconciliados ao fim. Os arquivos continuam independentes por fileId/encryption/refs mesmo quando compartilham o post físico. Rate limits e tratamento de HTTP 429 permanecem obrigatórios.

Cada arquivo concluído e pasta criada é publicado sobre a coleção local atual, sem substituir por uma cópia capturada no começo do lote. Abrir ou atualizar uma pasta durante o upload não faz a conclusão de outro arquivo apagar suas entradas já carregadas. O renderer reconcilia cards e linhas por identidade e conteúdo: itens inalterados mantêm seus elementos, miniaturas e seleção; inclusões, alterações e remoções efetivas afetam somente os itens correspondentes. A visualização inativa é limpa para não duplicar elementos ou seleção entre grade e lista.

Os chunks de upload são lidos por faixa e cifrados somente em RAM, inclusive em arquivos pequenos, retomadas criptografadas, miniaturas, imagens Markdown e uploads do sincronizador. `UploadMemoryCoordinator` usa o pool global de `RamUploadBudget`; não há ciphertext temporário no SSD. O upload da biblioteca lê o original diretamente, sem copiá-lo para o TEMP do Windows. Faststart e miniaturas permanecem sem mídia intermediária. Somente a prévia 480p convertida pode ser gravada temporariamente, em pasta oculta ao lado do original, conforme a seção de vídeo. Retomadas plaintext antigas preparam cada faixa em RAM. Snapshots da sincronização, checkpoints, cache de miniaturas e arquivos de destino/intermediários plaintext de download mantêm suas finalidades operacionais.

`AppMemoryBudget` estabelece um limite Windows Job Object de 1.000.000.000 bytes de memória privada comprometida para a execução normal/background, os processos WebView2 registrados e o auxiliar de geração de mídia. O heap gerenciado tem teto de 512 MiB. Antes de preparar um lote, a admissão considera memória atual de C#/WebView2/auxiliar, buffers existentes e reservas de outros uploads/downloads, com alvos de 800 MiB totais e 448 MiB gerenciados. Há espaço reservado para dois buffers plaintext e aproximadamente 400 MiB de ciphertext, reduzido quando necessário. Até 160 partes pequenas podem ocupar as vagas de chunks, mas a admissão é por bytes, não autorização para alocar 160 chunks de 20 MiB. Sob pressão, novos trabalhos aguardam de forma cancelável. Esse limite controla memória privada comprometida, não memória compartilhada de DLLs/GPU, cache de disco do Windows ou paginação do sistema.

O plaintext é zerado ao voltar ao pool. No upload agrupado sob demanda, o ciphertext é zerado e devolvido logo após transmitir o anexo, sem esperar o ACK da mensagem. Durante a espera ficam somente descritores, nonce e um digest de integridade por chunk. Um retry relê a faixa e regenera os mesmos bytes cifrados apenas após verificar plaintext, chave derivada e contexto autenticado; uma alteração é recusada antes de reutilizar o nonce. O pool limitado é compartilhado e reutilizado entre transferências, sem coleta completa bloqueante ao terminar cada arquivo. Buffers livres podem ser descartados sob pressão. Os caminhos unitários e de partes já preparadas conservam sua retenção anterior até sucesso/falha/cancelamento. Nenhum caminho cria um buffer para o multipart inteiro.

Miniaturas aguardando memória têm prioridade de admissão em `RamUploadBudget`: novas reservas de chunks aguardam enquanto os envios existentes liberam espaço para o gerador. Os auxiliares Windows e FFmpeg não descartam uma miniatura apenas por ultrapassar 30 segundos nessa espera, que continua assíncrona e cancelável. Os alvos de admissão e o limite de 1 GB permanecem iguais; imagens geradas continuam em RAM, sem copiar o original ou criar mídia temporária. A geração de vídeo 480p conserva sua política separada de admissão e staging.

Configurações > Uploads inclui `Forçar 16 workers (RAM até 1 GB)`, desativado por padrão e persistido em `forceUploadParallelism`. Ativo, oferece até 16 workers globais de transmissão sem redução dinâmica por vazão, mantendo arquivos simultâneos, memória, rate limits e limites de attachments. Nos uploads agrupados, `UploadBodySlot` adquire a vaga antes de enviar a requisição e a devolve ao terminar os anexos; uma confirmação lenta não ocupa essa vaga. Até 64 mensagens podem aguardar trabalho ou confirmação, com backpressure global. `UploadConcurrencyRegistry` reconfigura imediatamente os gates ativos da biblioteca, retomadas e sincronização; não é necessário reiniciar a transferência. Requisições em andamento terminam normalmente, e a política nova vale para novas admissões. Desativar restaura o limite adaptativo. A interface destaca o botão, exibe 16 e desativa o controle de partes enquanto ele estiver ativo. Dezesseis workers não significam dezesseis buffers de 200 MiB: cada transmissão usa somente o buffer do anexo atual.

`ForumUploader.MultiAttachment` produz descritores de lotes de dez anexos, com `Channel` de capacidade um e teto global de 64 mensagens pendentes. Cada conteúdo HTTP prepara um anexo por vez em RAM enquanto outros requests transmitem; dois workers de leitura/cifra alimentam os workers de transmissão. A admissão por bytes ocorre por anexo, não por mensagem de 200 MiB. Ao concluir o corpo útil, os buffers e a vaga de transmissão ficam disponíveis para sucessores enquanto a resposta anterior continua pendente. O agrupamento HTTP não altera tamanho, índice, nonce, salt, contexto ou referências lógicas dos chunks. Os caminhos de partes materializadas permanecem disponíveis para compatibilidade e transportes unitários. Disco mais lento que a rede, pressão externa de memória e esperas impostas pela API ainda podem interromper a transmissão: não existe garantia física de rede ocupada 100% do tempo.

No modo adaptativo, `UploadThreads` é dividido entre arquivos ativos, e `AdaptiveUploadGate` pode reduzir/reabrir vagas conforme amostras de vazão agregada. Todos os índices de uma mensagem nova são salvos juntos no checkpoint após validar a resposta, permitindo retomar sem reenviar chunks confirmados. Depois da reconciliação inicial, a recuperação silenciosa de posts órfãos exige estado local e origem disponíveis, sem inventar nome, pasta, criptografia ou referências. Falhas transitórias, HTTP 520/5xx e desconexão mantêm retry cancelável; HTTP 429 respeita o bucket do Discord. Uma espera isolada não substitui o progresso enquanto outra requisição transmite. O thread e os chunks confirmados são preservados. Mensagens de status não são persistidas; o coletor temporário conserva somente sinais técnicos limitados, conforme a seção de diagnóstico.

As vagas de rede limitam transmissões de requisições/batches, não attachments nem respostas pendentes. Duas faixas podem ser lidas/cifradas simultaneamente no processo, independentemente dos workers de rede. O multipart utiliza streams individuais e nunca um buffer de 200 MiB. Rejeições específicas de quantidade/tamanho reduzem o agrupamento e reproduzem os payloads cifrados; outros HTTP 400 não recebem esse retry. Cada tentativa readquire sua vaga de transmissão, e backoff/429 não mantém buffers de anexo. Falha ou cancelamento interrompe também o produtor e limpa os descritores ainda não enviados. Abertura, leitura, cifra e montagem de upload/download executam fora do Dispatcher WPF, evitando bloquear a interface enquanto o armazenamento responde.

O progresso individual avança por bytes lógicos, não por chunks confirmados. Preparação e metadata não somam porcentagem. Imagens usam 0–98% para o original e 98–99% para a miniatura. Vídeos com prévia ativada usam 0–80% para o original, 80–90% para gerar a prévia e 90–99% para enviá-la; sem prévia, o original vai a 99%. Todos chegam a 100% após concluir e publicar o arquivo. ACK pendente e metadata mostram `Concluindo`. MB/GB substituem contagem de partes. A barra do lote pondera as etapas de cada arquivo pelo tamanho do original, sem saltar a 99% porque seus bytes originais terminaram. `UploadWireProgressStream` e `UploadByteProgress` separam bytes transmitidos/retries do avanço lógico. O tamanho conhecido da prévia entra no total e sua transmissão no relógio de atividade e na velocidade. Downloads usam a janela móvel de 60 segundos; montagem não simula novos bytes de rede. O painel apresenta ETA enquanto houver bytes e velocidade utilizável, e retira a estimativa ao terminar ou interromper.

### Download, integridade e compatibilidade

O coletor temporário descrito abaixo é o destino principal de `AppLogger` enquanto estiver ativo. Ele conserva eventos técnicos limitados, sem as mensagens de status, argumentos ou dados dos arquivos. Fora desse modo, o logger anterior continua sem persistir INFO/DEBUG/WARN e conserva somente os erros previstos. Logs antigos não são removidos automaticamente.

Para payloads criptografados, cada tentativa baixa o ciphertext para um arquivo temporário identificado como tal, valida o envelope e a tag GCM, e só então grava o plaintext. Falhas temporárias de rede durante o download aguardam e repetem a parte até a conexão voltar. Falhas de estrutura ou autenticação descartam os temporários suspeitos e fazem no máximo três downloads completos por payload. Após a terceira falha, o diálogo oferece `Tentar novamente`, iniciando uma nova sequência de três buscas. Após reconstruir o arquivo, o tamanho plaintext é comparado com `sizeBytes` do metadata.

Uma falha de autenticação nunca entrega plaintext. Em modo personalizado, a interface informa que a chave configurada pode estar incorreta. Quando a autenticação passa, mas apenas o tamanho do metadata diverge, o app preserva o arquivo validado e oferece `Abrir mesmo assim` em um diálogo nativo.

Entradas legadas sem `encryption` continuam acessíveis por download direto. O cache de previews conserva plaintext; um arquivo iniciado por `ORGAESGC` é invalidado e baixado/autenticado novamente. Novos downloads criptografados não criam cópia ciphertext no disco: cada parte é autenticada em RAM antes de gravar plaintext retomável, e retries descartam a tentativa inválida. A reprodução progressiva também autentica cada chunk em RAM antes de expô-lo ao player.

## Diagnóstico temporário de desenvolvimento

`Organessonium.csproj` seleciona `DesktopEntryPoint` como entrada STA. Ela conecta os observers antes de construir `App`, carregar seus recursos ou iniciar o WPF. A criação da pasta, recuperação de sessões e gravação acontecem em outra thread, sem fazer a janela aguardar o armazenamento. O auxiliar `--media-preview-worker` não inicia outro coletor. Não há serviço Windows, executável adicional, hook global de teclado/mouse, telemetria externa ou inspeção de outros aplicativos.

O módulo está em `Services/Diagnostics`: `DevelopmentDiagnostics` é a fachada opcional; `DiagnosticSession` mantém sessão, fila, persistência e vigilância; `DiagnosticWebView` conecta eventos do navegador; `DiagnosticLabels` limita rótulos a valores técnicos conhecidos. Remover/desativar o módulo não muda resultados, retries, cancelamentos, paralelismo ou tratamento de exceções das operações observadas.

O diretório é fixo e anterior às configurações: `%APPDATA%\OrganessoniumData\Logs\Diagnostics\<sessão>`. Isso também vale quando a pasta operacional do banco foi personalizada. Configurações mostra `Diagnóstico temporário`, seu estado e o caminho da sessão. Os arquivos `trace-0000.jsonl`, `trace-0001.jsonl` etc. usam JSON UTF-8 sem BOM, uma linha por evento. Para análise durante execução, o leitor precisa permitir compartilhamento de leitura/escrita e rotação; uma última linha ainda incompleta deve ser ignorada até a próxima leitura.

Cada evento inclui UTC, tempo monotônico desde o início, sequência local, tipo, área, thread gerenciada, ID local da operação e dados técnicos. A sessão inicial informa protocolo 1, PID, versão do assembly e runtime. Operações têm início/fim, ID local/pai e duração. A instrumentação cobre:

- Exceções gerenciadas lançadas, erros enviados ao logger, falhas não tratadas do domínio, exceções do Dispatcher e tasks não observadas. Registra tipo, HResult e até quatro causas com 16 frames cada, incluindo método, linha disponível e offset IL. Não grava `Exception.Message`, `ToString`, `Data`, nomes/caminhos de arquivos, valores dos argumentos, chaves ou tokens.
- Despacho de comandos do bridge, trabalhos assíncronos e callbacks relevantes da UI. Rótulos desconhecidos viram `unknownCommand`/`application`, nunca cópias de entradas arbitrárias.
- Tentativas REST do `DiscordApiClient`, com rota anonimizada, tempo total incluindo esperas e status HTTP. IDs, host, query, headers, token e corpo não são gravados; CDN e stderr de auxiliares não recebem captura de conteúdo.
- Navegação, tipo/motivo/código de falha de processo WebView2 e erros/rejeições JavaScript. JavaScript conserva somente classe do erro e linha/coluna, não sua mensagem ou stack textual.
- A cada dois segundos: memória privada, working set, heap/commit gerenciado, memória observada dos processos já registrados, tempo acumulado de CPU, threads, fila do ThreadPool, contadores GC, exceções observadas, eventos descartados e até 16 operações em andamento. Não inicializa nem altera o orçamento de memória para obter métricas.

Uma thread de vigilância separada da escrita posta no máximo um pulso pendente no Dispatcher. Cinco segundos sem resposta produzem `ui.stalled`; a resposta posterior produz `ui.recovered`. O JavaScript envia um pulso a cada dois segundos; páginas visíveis sem resposta por 12 segundos produzem o mesmo sinal na área `javascript`. Uma navegação sem primeiro pulso recebe `webview.heartbeatMissing`. Uma página oculta não é classificada como congelada pela ausência do timer. Os observers são liberados tanto na recuperação quanto ao fechar a janela para continuar no tray. Esses eventos indicam tempo sem resposta, não estabelecem a causa nem tratam trabalho demorado como uma falha fatal.

`exception.observed` é uma notificação first-chance, anterior à escolha do `catch`. Ela também ocorre em comportamentos normais e não prova que houve crash. Há amostragem de até 16 dessas notificações por intervalo; erros explícitos continuam indo pela via prioritária. Eventos repetitivos são limitados por rótulo a um por intervalo. A fila admite 512 eventos com reserva de 64 vagas para erros, além de até oito pedidos limitados de flush; sob saturação, erros recentes substituem registros antigos e o descarte é contabilizado. Até 128 descritores de operações são retidos. Não há buffers de mídia, dump de memória ou plaintext de arquivos nesse módulo.

O writer descarrega periodicamente, faz flush limitado na falha terminal/saída e rotaciona quatro arquivos de aproximadamente 4 MiB por sessão. A manutenção conserva a sessão atual e até três sessões inativas anteriores, preservando sessões de processos ainda vivos. Apaga somente traces/marcadores reconhecidos de pastas geradas pelo módulo, sem seguir reparse points nem remover conteúdo inesperado. Os `.log` anteriores permanecem intactos. A retenção comum fica perto de 64 MiB, além de sessões simultâneas ainda ativas; arquivos antigos podem ser removidos pela rotação, portanto copie a sessão relevante antes de muitos reinícios.

`active.json` guarda somente PID e horário de criação do processo. A saída normal, depois de drenar o writer, remove esse marcador. Na próxima abertura, um marcador sem processo correspondente produz `previous.uncleanExit` referenciando a sessão anterior. Isso pode significar crash, encerramento pelo Gerenciador de Tarefas ou desligamento abrupto, não necessariamente bug. Um término nativo, `FailFast`, stack overflow, falta extrema de memória ou queda de energia pode impedir os últimos registros; o coletor não promete capturar um stack nativo nem impedir a falha. Logs descrevem o que foi observado, não autorizam alterações especulativas dos fluxos existentes.

Para desativar sem recompilar, inicie o processo com `ORGANESSONIUM_DIAGNOSTICS=0`. Configurações então informa o estado desativado. Para remover em outra versão, siga `DIAGNOSTICS-TEMP-001` em [migration.md](migration.md); a entrada WPF gerada pode voltar a ser usada e os poucos hooks saem junto do módulo. O logger anterior permanece disponível como fallback quando a coleta estiver desativada/indisponível. Seus arquivos podem conter mensagens/caminhos legados e devem ser revisados antes de compartilhar.

Semântica da notificação: [AppDomain.FirstChanceException](https://learn.microsoft.com/en-us/dotnet/api/system.appdomain.firstchanceexception?view=net-10.0).

## Reprodução de vídeo e faststart

Downloads de arquivos reservam 0–80% para a rede e 80–100% para a montagem. `DownloadAssembler` copia os chunks plaintext por bytes, com I/O assíncrono, e a UI mostra `Montando arquivo final...`; o último percentual só é concluído depois do fechamento/validação. Chunks criptografados são recebidos e autenticados integralmente em RAM antes de gravar apenas plaintext retomável. Não existe etapa `.encrypted` em disco. A admissão compartilha o orçamento global de memória, e cada payload é limitado a 64 MiB para impedir alocações arbitrárias; dados adulterados não são gravados. Cache e destino plaintext continuam exigindo proteção local pelo usuário.

Configurações > Uploads oferece `Gerar prévias 480p dos vídeos`, persistido em `generateVideoPreviews` e ativado por padrão. Desativar não remove miniaturas nem prévias já existentes e não desativa a reprodução progressiva do original. A opção vale para novos lotes e retomadas iniciadas depois da mudança; o lote em andamento mantém a preferência inicial; a preferência de qualidade de reprodução é independente. No painel, borda e barras/círculos de upload são neutros. Passar o mouse sobre o progresso de um arquivo ativo ou na fila revela X, que cancela somente esse arquivo; os demais continuam. Após publicar um arquivo, sua entrada aparece imediatamente na listagem da pasta atual, e encerrar o lote não troca a coleção por apenas a raiz.

O player existente lê chunks sob demanda por `VideoStreamSession`, sem reconstruir o vídeo inteiro, criar mídia temporária ou persistir chunks no SSD. O índice usa os tamanhos plaintext reais dos attachments, com prefix sums e busca binária: arquivos antigos de 10 MiB, novos de 20 MiB, partes finais menores e tamanhos misturados não dependem do limite atual do uploader. O primeiro chunk é pedido imediatamente; MP4 com `moov` no final permite leitura direta da região final e seek sem baixar todas as partes intermediárias. Não há migração ou reenvio dos vídeos já armazenados.

Configurações > Prévias inclui `Chunks de vídeo antecipados`, chave `videoPrefetchChunks`, padrão 1 e intervalo de 0 a 6. Zero desativa antecipação, mantendo as leituras necessárias. O valor vale para sessões novas. Durante reprodução, o app mantém uma janela limitada de chunks, deduplica downloads simultâneos e cancela prefetch obsoleto em seek, pausa ou fechamento. O cache plaintext tem teto de 192 MiB e o player recusa chunks individuais acima de 64 MiB; esses limites não alteram o protocolo de upload de 20 MiB. Falhas temporárias têm retry cancelável; falha definitiva aparece no fallback do player. Download completo, checkpoints e cancelamento continuam independentes da reprodução.

As URLs `https://media.organessonium.local/preview/<GUID>` são recursos internos interceptados por `WebResourceRequested`, não um servidor HTTPS real. Para vídeo, cada resposta fornece até 512 KiB disponíveis em RAM, com `206`, `Content-Range`, `Content-Length`, `Accept-Ranges` e `Cache-Control: no-store`. O Chromium solicita as faixas seguintes quando necessário. Isso evita que o WebView2 drene uma resposta integral antes de reproduzir. Há no máximo quatro respostas vivas por recurso; EOF/fechamento libera seus buffers. Imagens, PDF e áudio preservam seus caches e usam o mesmo host virtual, sem listener localhost.

Uploads novos de MP4 elegível passam por `Mp4FastStart` antes da divisão/criptografia. O parser C# expõe uma sequência virtual que coloca `moov` antes do primeiro `mdat` e corrige `stco`/`co64`. Somente o índice `moov`, limitado a 32 MiB e admitido no orçamento global de RAM, é copiado em memória; áudio, vídeo e descritores são lidos por faixa do original. `PreparedVideoUpload.OpenRead()` deve ser usado para consumir essa sequência; `File` identifica a origem, não um arquivo remuxado. O original permanece intacto e não há cópia integral nem saída no TEMP. Estruturas não suportadas mantêm o upload original e registram erro. O FFmpeg é usado na geração de mídia auxiliar, não para alterar o conteúdo original. O checkpoint local `fastStartApplied` permite reconstruir os mesmos bytes ao retomar, sem misturar layouts de chunks; transferências antigas parcialmente enviadas preservam o layout anterior.

Miniaturas de vídeo usam o executável separado `media/ffmpeg.exe`, sem janela, inclusive para MKV/H.264 e MKV/HEVC. Imagens tentam primeiro o auxiliar Windows (`--media-preview-worker`, antes de inicializar configurações/Discord/WebView2) e usam FFmpeg em caso de falha. O auxiliar Windows não aceita um ícone genérico do Shell como miniatura real. Os resultados são PNG para imagens e JPEG para vídeos, quadrados de 200×200, no cache persistente existente e no metadata cifrado. Cada geração de miniatura reserva 96 MiB nativos e 8 MiB de saída, não a reserva de um vídeo inteiro.

`FfmpegPreviewUpload` gera a prévia completa em uma única passagem do FFmpeg, antes de iniciar seu upload. O resultado é gravado por I/O assíncrono em `.organessonium-preview-<GUID>/preview.mp4`, pasta com atributo Hidden na mesma localização do original. Upload de pastas e sincronização ignoram esse diretório interno. Não usa o TEMP do Windows, não copia o original e não grava ciphertext intermediário. O handle usa `DeleteOnClose` e permanece aberto durante o upload: sucesso, erro, cancelamento ou término do processo liberam o arquivo; a pasta vazia é removida no encerramento normal. Essa pasta pode ficar vazia após um encerramento abrupto. Hidden não é criptografia nem proteção contra leitura local: a prévia temporária está em claro. É necessário espaço e permissão de gravação ao lado do original; falhas preservam o original e são notificadas, sem fallback para outro disco. O tamanho final fornece o ChunkCount autenticado de ORGAESGC v1, eliminando a medição por segunda transcodificação.

A prévia é MP4 fragmentado (`empty_moov`, `frag_keyframe`, `default_base_moof`), H.264/YUV420p até 854×480 sem ampliar o original, 30 fps, vídeo alvo/máximo de 1.000 kbit/s e AAC estéreo de 96 kbit/s quando existe áudio. libx264 usa `veryfast`, `zerolatency` e até quatro threads; o filtro reduz a 30 fps antes da escala, evitando redimensionar frames que serão descartados; decodificação admite duas threads e `HardwareAcceleration` permite aceleração automática na decodificação. Duração e tempo codificado fornecem o progresso da geração. Não exige que duas codificações produzam bytes idênticos. A geração ainda depende de duração, resolução, codec, CPU/GPU e velocidade de leitura do original; não é instantânea nem há promessa de velocidade fixa.

A prévia usa o limite central de attachments, atualmente 20 MiB por parte cifrada, descontando os 70 bytes ORGAESGC do plaintext, e até dez anexos por mensagem. Depois de fechar a geração, o uploader existente lê suas faixas, cifra em RAM e usa descritores sob demanda, workers de transmissão, ACKs paralelos, retries e backpressure por bytes. Buffers são liberados ao terminar o corpo, sem esperar uma confirmação lenta. O gate global da biblioteca também controla a prévia e recebe mudanças do botão de 16 workers. Referências só são publicadas após confirmar todos os anexos. Prévias antigas de 2 MiB, MP4 flat, plaintext legado e transporte unitário permanecem legíveis; não há migração nem reenvio automático.

O FFmpeg reserva 192 MiB nativos e continua registrado no limite global de 1 GB junto do C#/WebView2. Só sua saída convertida usa staging no disco; buffers criptográficos continuam limitados em RAM. Admissão aguarda até 30 segundos. Stderr não é salvo; são extraídos apenas duração/progresso, e logs de falha conservam tipo/HResult, sem nomes ou metadata. Windows de 32 bits ou instalações sem o componente conservam o transcoder Windows limitado a 128 MiB e aos codecs instalados. A licença e as fontes do componente estão em `media/NOTICE.md`.

O progresso fica no SQLite local, em `settings` sob `videoProgress:<fileId>`, com posição/duração em segundos, UTC da última reprodução e conclusão. O app grava a cada 10 segundos durante fluxo normal e força o último valor em pausa, fechamento, troca ou encerramento normal. Ao reabrir, restaura automaticamente a posição. A partir de 95% da duração, a próxima abertura começa no início; mudança de duração maior que o maior entre 5 segundos e 1% impede restaurar uma posição incompatível. Exclusão permanente remove o registro; registros sem atualização são limpos após 180 dias. Logout apaga o progresso junto aos dados da configuração anterior. Nada disso entra no metadata remoto.

O suporte de codecs permanece dependente do WebView2 instalado; não há transcodificação durante reprodução. Os contratos de reprodução acima não foram alterados pela cifra dos metadados.

## Lixeira

A Lixeira é uma pasta técnica independente, com `folderId = trash`, caminho `/@trash`, post de índice no fórum `folders`, mapping em `names` e serial OSER v3 do tipo Folder no canal `serial`. Seu índice é criado na primeira abertura ou exclusão comum; ela não aparece como uma pasta da raiz. O anexo `.bin` e seu cache persistente seguem o mesmo fluxo das pastas normais, mantendo o ID da mensagem quando o conteúdo muda e preservando um serial vazio quando não há itens.

`Delete` e a ação comum de exclusão enviam os itens para a Lixeira após confirmação. `Shift+Delete` confirma a exclusão permanente e remove os payloads. O envio recuperável lê o metadata completo, publica no destino antes de remover a mensagem da origem e preserva identidade, chunks, criptografia, previews e favorito. O metadata do item recebe `parentFolderId: trash` e `trash: { originalParentFolderId, deletedAt }`, com data UTC. Pastas levam seus descendentes sem excluir os índices internos; apenas o pai da pasta movida muda. Pastas apagadas com nomes iguais permanecem separadas na Lixeira.

A barra lateral abre essa árvore isolada de Arquivos, Favoritos, Dispositivos e busca normal. A seleção e o menu da Lixeira oferecem Restaurar e Apagar permanentemente. `Delete` dentro dela não implica uma exclusão definitiva. Restaurar usa o pai original se ainda estiver ativo, ou `root` se ele não existir ou também estiver na Lixeira. Uma pasta com nome conflitante no destino bloqueia a restauração sem alterar o item. Não há limpeza automática nem expiração. `AppRegistry.Trash.cs` concentra as operações recuperáveis; `AppController` separa esse fluxo da exclusão permanente e publica os resultados na interface.

O contrato completo para implementar esse recurso no Flutter, incluindo o acesso pela barra lateral mobile, está em [trash.md](trash.md).

## Persistência local

As configurações e os índices locais ficam em `organessonium.db`, dentro da pasta de dados. A tabela `settings` usa `key`, `value` e `updated_at`; cada gravação atualiza o valor e o horário em UTC pelo SQLite. `folder_index_map` persiste `folderId`, `threadId`, ID da mensagem em `names` e a referência do serial. `app_entries` é cache dos filhos já conhecidos, agrupado por `parent_folder_id`, e mantém também favorito, tipo de dispositivo, versão, modo e salt de criptografia sem armazenar qualquer chave.

As tabelas `sync_folders`, `sync_state` e `sync_issues` guardam, respectivamente, vínculos por conjunto de canais, a última revisão aceita por caminho relativo e decisões pendentes. O cache `app_entries` conserva as extensões opcionais `sync` e `syncDevice`. O identificador da instalação fica separado em `%LOCALAPPDATA%\Organessonium\installation-id.txt`, sem token ou caminho das pastas, e não é apagado ao sair do bot. `SyncStaging` contém snapshots de upload e `SyncDownloads` conserva chunks retomáveis; ambos ficam dentro de `OrganessoniumData`.

Ao abrir uma pasta, o cache e o serial daquela pasta são mostrados primeiro. Em seguida, somente o thread remoto dessa pasta é consultado. O thread é a fonte de associação dos filhos diretos; `parentFolderId` ausente em metadata legado é normalizado para a pasta que está sendo lida. O botão Atualizar e `F5` usam a mesma verificação incremental: leem os mappings em `names` e reconciliam os threads da árvore mapeada, uma pasta por vez, incluindo a árvore de dispositivos quando mapeada. Adições e remoções atualizam `app_entries`, o serial local e o serial do servidor; a lista renderizada recebe o resultado de cada pasta sem esperar o término da árvore inteira. Os seriais locais não são apagados nessa operação e conteúdo inalterado não reenvia o `.bin` nem regrava o mapping. `Ctrl+F5` atualiza o mapping, limpa somente os seriais locais e os recria dos metadados do servidor. A publicação remota ocorre antes da troca de referência no mapping, sem exclusão em massa do canal. A contagem total de arquivos percorre recursivamente os seriais da árvore; os cards de pastas não exibem contagem. Uma leitura vazia dos mappings não substitui o mapa local existente. Falhas temporárias, serial corrompido, thread inacessível ou HTTP 429 não removem o cache local válido: a operação é registrada e a interface continua com o último estado conhecido. Se o mapping de uma pasta foi perdido, mas o metadata ainda guarda o `threadId` do seu índice, a exclusão lê esse thread diretamente para remover os descendentes e a própria pasta. Em uploads e exclusões permanentes em lote, os metadados e caches são atualizados por item, mas cada serial afetado é publicado somente ao fim da ação. A exclusão permanente de uma pasta também remove seu mapping, serial e thread de índice depois de remover seus descendentes. A exclusão comum envia o item para a Lixeira, preservando payloads e índices descendentes.

O comando `--audit-metadata` faz uma leitura do canal `names`, dos threads do fórum `folders` e do canal `serial`, sem alterar o servidor, e grava `metadata-server-audit.txt` no diretório de dados. O relatório lista os metadados reconhecidos e aponta mensagens ilegíveis, seriais inválidos e referências sem metadata. O comando `--repair-serials` exige uma auditoria inicial sem metadados ilegíveis ou referências ausentes, refaz o cache local e publica os seriais a partir dos metadados, depois repete a auditoria. O relatório contém nomes e IDs privados da biblioteca e não deve ser publicado.

Ao confirmar `Sair`, o splash WPF da inicialização cobre Configurações durante a limpeza, sem mostrar a biblioteca anterior. O app encerra a sessão, cancela transferências e prévias em andamento, apaga configurações e entradas em cache do SQLite e limpa o diretório `OrganessoniumData`. Banco, caches, seriais e estados de transferência são removidos; somente `WebView` e `Logs` são preservados. Se algum dado local não puder ser apagado, o splash sai e Configurações volta com a opção de tentar novamente. Após a limpeza, a configuração inicial aparece sem token nem IDs anteriores; o banco é recriado vazio, sem exigir reinicialização do app.

## Interface desktop

A janela principal usa a WebView2 dentro de uma shell WPF. O primeiro frame exibe um splash WPF leve, com o ômega centralizado e pulsação suave, antes de abrir o SQLite e inicializar a WebView2; esse trabalho persistente começa somente depois que a janela já foi renderizada. No Windows 11, o `AppWindowTitleBar` do Windows App SDK estende a shell para a faixa superior, mas o sistema continua desenhando e operando minimizar, maximizar/restaurar e fechar. A WebView recebe os insets reais dos botões para não colocar ações sob eles, e as regiões vazias da barra e da marca lateral são marcadas como arrastáveis. Essas regiões são reaplicadas após alterações de layout; ao abrir Prévia ou Configurações, a região acompanha o cabeçalho visível e exclui seus controles clicáveis. Em Windows sem suporte ou se a inicialização falhar, o app preserva a moldura WPF padrão. A barra lateral tem a marca do Organessônium e um botão de painel para recolher sua largura. Recolhida, ela mostra o ômega e revela o botão de expansão ao passar o mouse sobre a marca, ocultando a marca sob o botão durante a transição. Os ícones dos comandos laterais usam slots fixos nos dois estados, então permanecem ancorados enquanto a largura anima; os rótulos aguardam o fim da expansão e entram ou saem por opacidade, sem quebra de linha. O botão destacado `Enviar` abre um menu para criar pasta, criar e abrir um novo Markdown, enviar arquivos ou enviar uma pasta; ele permanece no mesmo lugar, mas fica desativado em Favoritos, Dispositivos e Lixeira para não deslocar a navegação. As mesmas ações de arquivos continuam disponíveis no menu de contexto da área de arquivos. A área de arquivos exibe o caminho da pasta na mesma linha do nome atual e, logo abaixo, filtros de tipo e data, ordenação, visualização e atualização do índice. A seleção por arraste começa e permanece somente na região dos cards, abaixo do cabeçalho. A barra lateral mantém o acesso a Arquivos, Favoritos, Dispositivos sincronizados, Lixeira e Configurações; as transferências continuam visíveis no painel próprio de operações. O menu de cada arquivo ou pasta permite adicionar e remover o item dos favoritos, e a alteração aparece imediatamente tanto na grade atual quanto na aba Favoritos. A aba Dispositivos mostra as raízes publicadas sob `synchronized` e permite navegar progressivamente pela árvore compartilhada. Não há área de compartilhamento nem perfil de usuário.

A barra lateral e a barra superior usam o fundo contínuo da janela. A grade e a lista de arquivos ficam em uma única área de cards levemente mais escura, com cantos arredondados. Na grade, o tamanho Médio apresenta seis cards por linha em 1920×1080 com a barra lateral aberta; a quantidade de colunas aumenta ou diminui com a largura disponível. O card inteiro é quadrado, incluindo o cabeçalho de 44 px: a miniatura é mais baixa que larga, com margem interna de 8 px, e não aumenta a altura do card. Os cards têm cantos de 12 px e espaçamento de 16 px; o tamanho Médio usa base de 240 px e limita a expansão individual a 300 px. Pequeno e Grande preservam a mesma proporção, e os chips de pasta continuam com 48 px de altura. Os thumbnails visíveis são solicitados na ordem dos cards, carregados com concorrência limitada pela configuração `Prévias simultâneas` e entregues à interface de cima para baixo. O rodapé de status anterior não é exibido; mensagens continuam acessíveis pela região `aria-live`, enquanto atividades em segundo plano e transferências usam painéis separados. O resumo lateral fica no rodapé, separa o armazenamento por uma linha e mostra bytes e a quantidade total em formato compacto, como `8,43 GB 312 itens`, depois de receber o agregado completo dos seriais: `AppRegistry.GetLibraryStatisticsAsync` soma os agregados diretos de cada pasta da árvore, e `AppController` entrega o resultado à WebView2. Uma biblioteca vazia é mostrada como `0 B` e `0 itens`. Um ícone animado junto desse resumo permanece visível enquanto alguma leitura ou reconciliação com o servidor está em andamento, inclusive com operações sobrepostas, e desaparece somente depois que todas terminam. Assim, o resumo não trata apenas os filhos da pasta atualmente aberta como a biblioteca inteira. Configurações abre uma tela inteira dentro da janela, com as seções distribuídas em colunas no espaço amplo e uma coluna única em janelas menores. O editor Markdown abre em suas guias locais; sua barra de formatação acompanha a largura útil da área de trabalho e mantém os ícones visíveis no modo compacto.

Todos os ícones de ações, menus e tipos de arquivos da WebView2 usam a fonte local Google Material Symbols Rounded, inclusive na configuração inicial e no editor, sem dependência de rede. Ações e tipos comuns são cinza `#c4c7c5` no tema escuro e `#5f6368` no claro. Imagens e vídeos usam vermelho `#ea4335`; estrelas de favoritos usam amarelo `#fbbc04`. Pastas usam o glyph preenchido e mantêm as cores de pasta configuradas pelo usuário. Na navegação lateral, Arquivos usa âmbar, Favoritos amarelo, Dispositivos azul e Lixeira vermelho suave; a engrenagem de Configurações permanece cinza nos dois temas, independente da cor de destaque. O ômega e o ícone de marca do aplicativo continuam sendo identidade visual, não ações. Abrir/pré-visualizar continua disponível por duplo clique, mas não aparece no menu dos três pontos.

A fonte `web/fonts/MaterialSymbolsRounded.woff2` inclui o eixo variável `FILL` de 0 a 1, com tamanho óptico 24 e peso 400. Isso permite preencher pastas/estrelas/mídia e manter as demais ações contornadas no mesmo recurso local. Uma fonte estática contornada ignora esse eixo mesmo quando o CSS declara `FILL: 1`. A licença Apache 2.0 acompanha o recurso em `web/fonts/LICENSE`; consulte o [guia oficial de Material Symbols](https://developers.google.com/fonts/docs/material_symbols) para o contrato dos eixos.

`Aparência > Cor de destaque` permite seguir a cor do Windows (padrão), escolher uma paleta de cores do Windows ou informar uma cor personalizada pelo seletor/hexadecimal. `WindowsAccentService` lê `UISettings.GetColorValue(UIColorType.Accent)`; `MainWindow` acompanha `ColorValuesChanged` e encaminha mudanças pelo bridge, sem modificar as preferências do Windows. `accentColorMode` (`system`, `preset`, `custom`) e `accentColor` (`#RRGGBB`) são persistidos na tabela `settings` e preservados no clone da configuração. `web/appearance.js` aplica os tokens CSS, ajusta tons extremos para contraste de pelo menos 3:1 nos controles e seleciona texto claro/escuro para botões preenchidos. Uma escolha explícita de paleta ou cor personalizada não é substituída por mudanças no Windows. Referência da API: [GetColorValue](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.getcolorvalue?view=winrt-26100) e [ColorValuesChanged](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.colorvalueschanged?view=winrt-26100).

O botão lateral selecionado mantém texto e preenchimento cinza, com uma linha compartilhada na cor de destaque que se desloca entre os botões, incluindo Configurações. Contorno/preenchimento dos cards selecionados, seleção na lista e retângulo de seleção por arraste usam tons neutros nos dois temas, sem acompanhar a cor de destaque. Os estados ativos dos botões de visualização, tema, tamanho dos cards e formatação também são neutros. Trocas entre Arquivos, Favoritos, Dispositivos, Lixeira e guias do editor usam entrada de conteúdo por opacidade e deslocamento vertical de 14 px durante 220 ms, sem animar ou reiniciar as coleções/requisições. Na biblioteca, a animação atua no cabeçalho e nas listas internas; `main-area`, seu fundo escuro, margens e cantos permanecem fixos. O menu de contexto entra com opacidade, deslocamento de 4 px e escala de 98% a 100% durante 140 ms. Uma atualização assíncrona dos dados do menu não reinicia a entrada. As transições respeitam `Animações` desativadas. A cor de destaque continua disponível na linha lateral, controles e indicadores de progresso, independente da escolha entre tema claro, escuro e do sistema.

## Versões, cópia e informações de arquivos

O menu de arquivos inclui `Fazer cópia`, `Ver informações` e, quando o metadata completo contém histórico, `Versões`. Os textos são `Adicionar à pasta "Com estrela"`, `Fazer o download`, `Renomear`, `Mover` e `Mover para a lixeira`. Remover o favorito usa `Remover da pasta "Com estrela"`. Não foram adicionadas ações de compartilhamento ou disponibilidade off-line. A restauração/exclusão permanente da Lixeira e os controles de cor das pastas já existentes continuam disponíveis.

Escolher `Substituir` envia novo lote/salt preservando fileId e o conteúdo anterior. Novo conteúdo de até 1 GiB usa pool do escritor; maior usa dedicado, reaproveitando o dedicado atual quando aplicável. A versão pode mudar de thread/layout, mas os snapshots históricos não são movidos. `versions: [{ versionId, archivedAt, payload }]` conserva os metadados completos de cada conteúdo, sem histórico aninhado. A publicação revalida a versão/batch remoto sob claim e só altera metadata após confirmação das partes/prévias; conflito não sobrescreve outra versão silenciosamente. Histórico continua cifrado sem alterar OSER/envelope. Markdown usa o mesmo pipeline de upload/versionamento.

`uploadedAt` conserva o upload inicial; `versionUploadedAt` registra o conteúdo atual e `modifiedAt` acompanha substituição, rename, move e edição. Metadados antigos sem essas datas adicionais usam a data de upload como fallback. `AppController.Files` e `FileVersioning` cuidam da publicação e das ações, enquanto `MetadataService` valida e cifra o JSON. O SQLite persiste as versões e datas em colunas aditivas; materializar seriais preserva detalhes compatíveis e descarta referências antigas quando o salt/thread/thumbnail indicar outro payload. As operações que alteram a organização preservam os detalhes completos, não apenas os cards do serial.

O diálogo de informações mostra categoria/MIME, localização (`/`, `/Fotos` etc.), tamanho atual, tamanho utilizado, upload inicial, última modificação, `threadId`, ID da mensagem de metadata e número lógico de chunks. Tamanho utilizado é a soma de `sizeBytes` atual com os `sizeBytes` dos snapshots, sem contar previews, overhead criptográfico ou uploads órfãos. O resumo lateral de armazenamento usa a mesma soma nos agregados diretos dos seriais e no fallback do cache: três versões de 20 GB ocupam 60 GB, mas continuam sendo um arquivo lógico de 20 GB. Seriais antigos recebem o total corrigido na reconciliação normal da pasta ou ao atualizar o índice; não há alteração do formato OSER. O histórico mostra o conteúdo atual e os anteriores, com download de cada versão. Downloads guardam um snapshot do metadata no checkpoint para não trocar o conteúdo após uma substituição durante a retomada.

O diálogo de informações abre imediatamente com os nove campos na estrutura final, cada valor mostrando `Carregando...` até a leitura terminar. Um texto `Carregando informações...` e um indicador giratório neutro acompanham a espera; o espaço desse status é reservado para evitar mudança de altura quando os valores chegam. A resposta substitui os valores e encerra o indicador, sem abrir outro diálogo. Erro ou fechamento também encerram o indicador, e respostas antigas não atualizam outro item aberto. O estado de espera expõe `aria-busy` e respeita animações desativadas.

`Fazer cópia` baixa o conteúdo atual autenticado para um diretório temporário operacional e o envia como arquivo independente na mesma pasta, com nome `Cópia de <nome>` e sufixo quando necessário. Recebe outro fileId, salt, metadata e attachments; pode usar o mesmo pool físico, sem copiar favorito/histórico nem reutilizar os attachments do original. A criptografia de upload permanece em RAM; o reconstruído temporário é removido ao final. A ação é para arquivos, não árvores de pastas.

A especificação para clientes interoperáveis está em [implementation.md](implementation.md). A criptografia continua em [implementation old.md](<implementation old.md>), o transporte em [implementation old 2.md](<implementation old 2.md>) e as compatibilidades transitórias em [migration.md](migration.md).

## Sincronização de pastas entre computadores

### Identidade, árvore e cadastro

Dispositivos preserva as galerias já publicadas e inclui computadores com `deviceType: "pc"`. O nome exibido vem de `Environment.MachineName`; a identidade é um `deviceId` persistente, não esse nome. Cada instalação tem uma raiz `pc-<deviceId>`, por isso computadores homônimos não são consolidados.

`Adicionar pasta sincronizada` usa o seletor nativo do Windows e permite nome, caminho local, automação, bidirecionalidade, políticas de conflito/exclusão e inicialização com Windows. O `syncFolderId` identifica a pasta lógica, independentemente do caminho físico. A raiz canônica usa `folderId = sync-<syncFolderId>`. Em outro computador, `Sincronizar esta pasta neste computador` associa o mesmo conteúdo a um novo caminho e cria uma entrada de navegação com `sync.canonicalFolderId`; não duplica os payloads. Subpastas mantêm caminhos relativos com `/` e índices hierárquicos separados. Remover o vínculo desativa somente a associação local, sem apagar arquivos locais ou remotos.

Pastas vazias no primeiro vínculo recebem o conteúdo remoto. Conteúdo local idêntico é reconhecido pelo hash; diferenças são tratadas como conflito antes de substituir qualquer arquivo. Pastas locais sobrepostas, a pasta de dados do app e raízes com reparse point são rejeitadas.

### Comparação, automação e transferências

O sincronizador compara estado-base persistido, estado local e revisões remotas. Datas UTC e tamanho evitam hashes desnecessários, mas a igualdade de conteúdo é confirmada por SHA-256 quando necessário. Eventos do watcher invalidam a assinatura daquele caminho mesmo quando tamanho e timestamp coincidem. O watcher aplica debounce de 2,5 segundos e reúne caminhos afetados; overflow solicita reconciliação completa. A consulta remota ocorre a cada minuto nas pastas automáticas e uma varredura local completa ocorre a cada cinco minutos. A primeira execução, a retomada, uma falha e `Sincronizar agora` também fazem varredura completa.

O worker executa fora da thread gráfica, com fila limitada e exclusão mútua por pasta. Arquivos são processados sequencialmente; seus chunks usam o paralelismo configurado e o limitador REST existente. Arquivos instáveis, bloqueados ou inacessíveis são adiados. Varreduras incompletas nunca autorizam exclusões. Falhas de conexão preservam estado e cópias, com espera progressiva de até cinco minutos antes de nova tentativa. Pausa global e pausa por pasta persistem; retomar reconcilia as mudanças. Encerrar o processo cancela o trabalho sem gravar uma nova preferência de pausa.

Uploads capturam uma cópia estável em disco, validam o hash e reutilizam `ForumUploader`, `TransferManager` e `EncryptionService`. Só depois de confirmar chunks e revisão, os metadados navegáveis, caches e seriais são atualizados. Uma confirmação `syncApplied` permite reparar publicação interrompida sem reenviar o arquivo. Downloads reutilizam `ForumDownloader` e os checkpoints; o arquivo anterior não é tocado até validar tamanho/hash e preparar a troca atômica no mesmo volume. Há uma cópia local de recuperação em `.organessonium-recovery` quando uma versão existente é substituída ou removida. Não há leitura integral de arquivos grandes para RAM.

As transferências do worker aparecem no painel quando a janela existe. Progresso e notificações não abrem a interface. As pastas mostram sincronização, pausa, espera por conexão ou decisões pendentes; os modais permitem resolver arquivos individualmente ou confirmar atualizações remotas em lote.

### Revisões, conflitos e exclusões

Os metadados comuns continuam em `schemaVersion: 2`. Arquivos sincronizados recebem `sync.version: 1`, `syncFolderId`, `relativePath`, SHA-256, `modifiedUtc`, revisão numérica, `revisionId`, dispositivo e estado. Raízes e aliases incluem apenas os campos aplicáveis. O OSER publicado continua v3; detalhes da sincronização são obtidos dos metadados, não de uma alteração do formato binário.

O índice da raiz canônica conserva um diário versionado de revisões com `parents` e referências ao payload já existente. A revisão numérica e o relógio não arbitram concorrência: duas revisões sem relação de ancestralidade são conflito. A publicação confirma a revisão remota antes de trocar o índice navegável. A limpeza retém as revisões atuais, suas bases imediatas e todos os ramos em conflito; não é um histórico ilimitado nem um serviço de backup.

Por padrão, uma alteração somente remota pede confirmação. `Substituir arquivo local` pode memorizar atualizações automáticas para aquela pasta; isso não autoriza resolver conflitos reais por timestamp. Alterações dos dois lados permitem manter a versão local, usar a nuvem ou manter ambas com nome contendo dispositivo e data. A decisão é revalidada antes de executar, para não aplicar uma escolha sobre conteúdo que mudou depois.

Exclusões usam `state: "deleted"`, `deletedUtc`, origem e uma nova revisão, não a ausência de um arquivo ou thread. As políticas são perguntar, propagar ou ignorar. Exclusão remota com edição local é conflito. Diretórios não vazios não são removidos recursivamente pelo worker. Renomeações locais com hash único reaproveitam `fileId` e chunks; renomeações remotas preservam a mesma identidade e respeitam a confirmação/configuração da pasta. Tombstones não são itens da aba Lixeira.

### Execução sem interface e inicialização

O evento `Closed` libera imediatamente a referência à janela principal. A liberação é idempotente e não altera uma janela substituta; abrir novamente cria outra instância em vez de chamar `Show()` em uma janela já fechada.

`App.xaml` não usa mais `StartupUri`. No modo normal, o primeiro frame continua sendo o splash WPF. O `App` mantém um `SyncBackgroundHost` independente do `AppController`; fechar a janela com `Minimizar para a área de notificação` descarta controller e WebView2, mas conserva somente worker e ícone nativo. Abrir pelo ícone recria a janela. O menu oferece abrir, sincronizar agora, pausar/retomar e sair; sair cancela workers, libera recursos e encerra o processo.

`Organessonium.exe --background` não cria `MainWindow` nem inicializa WebView2. Um mutex e pipe limitados ao usuário impedem workers duplicados e permitem que uma segunda abertura solicite a janela à instância existente. Não existe executável adicional ou serviço Windows instalado.

A inicialização é opt-in em Configurações. MSI/execução sem pacote registram o executável atual, entre aspas e com `--background`, em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. MSIX usa a extensão `windows.startupTask`, tarefa `OrganessoniumSync`, declarada desativada e habilitada por `StartupTask.RequestEnableAsync`; o manifesto inclui `uap10:Parameters="--background"`. O app não reativa uma tarefa bloqueada pelo Gerenciador de Tarefas. A preferência de fechar no tray é separada e não muda silenciosamente.

### Limites e validação

- Junctions, symlinks e reparse points não são sincronizados; caminhos absolutos, `..`, UNC, nomes inválidos e saídas da raiz são bloqueados. Temporários internos, `Thumbs.db`, `desktop.ini`, `~$*` e `*.tmp` são excluídos. `.git`, `bin`, `obj` e `node_modules` continuam incluídos.
- Não há merge de arquivos arbitrários, lock distribuído/CAS no Discord, regras personalizadas de exclusão nem recuperação automática de uma identidade de instalação perdida.
- O sincronizador exige revisões `syncRevision` para mudanças remotas. Edições por clientes/fluxos que alteram apenas os metadados comuns, incluindo ações normais da biblioteca, não são convertidas automaticamente em revisões. Para editar uma pasta sincronizada com propagação, use o caminho local vinculado. Navegação, previews e downloads continuam usando os objetos comuns.
- Vídeos sincronizados mantêm os bytes originais para preservar o hash; esse upload não aplica faststart nem gera preview 480p. Miniaturas dependem do suporte do Windows Shell. Chunks parciais de downloads abandonados e cópias `.organessonium-recovery` não têm expiração automática.
- Os testes sem interface validam duas instalações isoladas, conflitos, exclusões, falhas, caminhos, persistência e o protocolo remoto com HTTP simulado. Não substituem validação em dois computadores reais, Discord real, login do Windows, pacote MSIX instalado ou consumo durante horas. A interface não deve ser aberta para testar enquanto o usuário estiver usando o computador.

O diário e as confirmações de sincronização usam o mesmo codec antes de serem consumidos pelo worker. Payloads pequenos novos usam shared com chunkRefs/thumbnailRef/storage no snapshot; grandes e retomadas legadas mantêm seu layout. A limpeza de revisão persiste intent antes de retirar journal e usa GC seletivo, protegendo heads/bases/conflitos e referências externas. Transporte dedicado está em [implementation old 2.md](<implementation old 2.md>), shared em [implementation2.md](implementation2.md), criptografia em [implementation old.md](<implementation old.md>) e fallbacks em [migration.md](migration.md). Não muda os bytes originais nem as regras de conflito/tombstone.

## Geração de releases para Windows

`dotnet publish` e `build-releases.bat` executam `media/Verify-Sources.ps1` antes da publicação. O runtime de mídia é separado e precisa corresponder ao SHA-256 e às licenças/avisos registrados em `media/source-manifest.json`. A publicação local é permitida, com aviso enquanto a conferência de todas as fontes/dependências estiver pendente. O gerador inclui `media` no MSI/MSIX, os avisos GPL/MIT no ZIP de código-fonte e `ffmpeg-source-reference.zip` separado, com snapshots FFmpeg/BtbN baixados por HTTPS público e conferidos por SHA-256. Não exige conta/integrador GitHub. Esse pacote de referência não equivale ao Corresponding Source completo; as obrigações para distribuição pública e o estado da conferência estão em [media/NOTICE.md](Organessonium/Organessonium/media/NOTICE.md). Quando um pacote completo for registrado e conferido, o script passa a exigir e entregar `ffmpeg-corresponding-source.zip`, sem instalar as fontes junto do aplicativo.

`build-releases.bat` gera os artefatos separadamente em `artifacts/releases/<versão>` sem alterar o pacote portátil existente. A versão pode vir de `release/release-settings.json` ou ser informada no comando, por exemplo `build-releases.bat 1.2.0.0`. Use sempre `A.B.C.0`: os três MSIX e o MSIXBundle recebem exatamente essa versão, enquanto o Windows Installer usa os três primeiros campos para controlar upgrades do MSI.

- `Organessonium-windows-source.zip` contém somente a solução, o projeto WPF, código C#/XAML/web, assets e documentação necessários para abrir `Organessonium.slnx`, restaurar os pacotes NuGet e compilar o aplicativo. As entradas usam `/`; `bin`, `obj`, artefatos, caches, logs, bancos locais, certificados e configurações locais são excluídos. Antes de criar o ZIP, o script também bloqueia tokens, webhooks, chaves privadas e caminhos de usuário detectados no conteúdo público.
- `Organessonium-installer-x86.msi`, `Organessonium-installer-x64.msi` e `Organessonium-installer-arm64.msi` são produzidos pelas APIs nativas do Microsoft Windows Installer e pelo `MakeCab` do Windows. O Windows Installer exige um pacote distinto por arquitetura, portanto não existe um único MSI misto. Cada instalador contém a publicação multi-file framework-dependent correspondente, em cabinet sem compressão, e instala somente para o usuário atual em `%APPDATA%\Organessonium`, ao lado de `%APPDATA%\OrganessoniumData`, sem exigir elevação. Os atalhos do Menu Iniciar e da Área de Trabalho também pertencem apenas ao usuário atual. O instalador impede downgrade, preserva o `UpgradeCode` x64 existente e usa códigos fixos próprios para x86 e ARM64.
- `msix/Organessonium_<versão>_x86.msix`, `msix/Organessonium_<versão>_x64.msix` e `msix/Organessonium_<versão>_arm64.msix` contêm, cada um, o executável self-contained e single-file da própria arquitetura.
- `Organessonium_<versão>.msixbundle` reúne exatamente os três MSIX. O `MakeAppx` recebe a versão explicitamente por `/bv`, permitindo que o Windows selecione x86, x64 ou ARM64 conforme a máquina.
- `SHA256SUMS.txt` registra os hashes dos três MSIs, dos três MSIX, do MSIXBundle, do ZIP de código-fonte e do pacote de fontes/referências FFmpeg.

Para envio à Microsoft Store, use os valores de `storeIdentityName`, `storePublisher`, `storePublisherDisplayName` e `storeDisplayName` da reserva no Partner Center. O último campo define tanto `Package/Properties/DisplayName` quanto o nome visível do aplicativo no manifesto. Se for necessário usar valores diferentes dos padrões de `release/release-settings.json`, copie `release/release-settings.local.example.json` para `release/release-settings.local.json` e preencha os campos exatos. O arquivo local é ignorado pelo Git, não entra no ZIP e não deve ser publicado. O fluxo normal da Store não exige PFX: o bundle pode permanecer sem assinatura local e será processado pela Microsoft. O pacote MSIX continua instalado pela Store no contexto do usuário atual; `runFullTrust` permite executar o aplicativo desktop, mas não transforma a implantação em uma instalação para todos os usuários.

O script exige o .NET SDK e o Windows 10/11 SDK instalados pelo Visual Studio Installer. Os MSIs não são self-contained: a máquina de destino precisa ter o .NET Desktop Runtime 10 e o Windows App SDK Runtime 1.8 da arquitetura correspondente. Se `ORGANESSONIUM_PFX_PATH` estiver definido, o script usa o `SignTool` oficial para assinar os pacotes; a senha pode ser passada apenas no ambiente por `ORGANESSONIUM_PFX_PASSWORD`, sem ser gravada no repositório. `ORGANESSONIUM_TIMESTAMP_URL` é opcional. O MSI destinado a distribuição pública deve ser assinado com um certificado confiável.

Ao final, o script valida conteúdo, arquitetura, versão, identidade e encoding de cada artefato, descompacta administrativamente os três MSIs e recompila uma extração limpa do ZIP. Para solicitar o Windows App Certification Kit, execute `build-releases.bat 1.2.0.0 -RunWack` em um terminal elevado. A ausência ou falha do WACK é reportada sem apagar os demais artefatos; a certificação final e a associação da identidade ainda dependem do Partner Center.
