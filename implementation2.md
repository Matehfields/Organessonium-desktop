# Organessônium Shared Storage V2

Esta é a especificação do armazenamento compartilhado implementado no desktop C#/WPF/WebView2. O Flutter ainda não foi alterado nem validado com esse protocolo. A implementação móvel deve reproduzir os contratos remotos, não os workers, buffers ou consumo de memória do desktop.

Documentos complementares preservados: [criptografia dos metadados](<implementation old.md>), [transporte multi-attachment e ORGAESGC](<implementation old 2.md>), [histórico de arquivos](implementation.md), [compatibilidades temporárias](migration.md). V2 é o layout de armazenamento, não uma nova versão de AES, metadata, OSER ou histórico.

## 1. Identidade e versões de protocolo

| Contrato | Versão / identificação |
| --- | --- |
| Envelope de metadata | `format: organessonium-metadata`, `version: 1` |
| Objeto file/folder | `schemaVersion: 2` |
| Layout de armazenamento | `storage.version: 2`, `storage.mode: shared` ou `dedicated` |
| Registros operacionais no índice técnico de folders | `schemaVersion: 1`, `entryType: storage…` |
| Blob criptografado | ORGAESGC V1 |
| Serial de pasta | OSER v3, sem campos novos |
| Checkpoint unitário antigo | `payloadLayoutVersion: 1`; também quando ausente no JSON antigo |
| Checkpoint multi-attachment dedicado | `payloadLayoutVersion: 2` |
| Checkpoint compartilhado | `payloadLayoutVersion: 3` |

`fileId` identifica o arquivo lógico. `versionId` identifica um conteúdo desse arquivo; novos uploads usam o UUID N do checkpoint `transferId`. `batchId` do arquivo continua sendo o identificador existente do upload, sem obrigar que todas as partes estejam na mesma mensagem. O UUID `storageBatchPlan.batchId` identifica uma mensagem de transporte, podendo associar partes de arquivos e versões diferentes. Não confundir esses dois batchIds.

`threadId` localiza um post físico, não identifica um arquivo. `metadataMessageId` localiza o metadata no índice da pasta. Para localizar um attachment são necessários `(threadId, messageId, attachmentId)`. IDs Discord são strings, nunca números JSON ou doubles. UUIDs novos usam 32 caracteres hexadecimais minúsculos, sem hífens. IDs legados continuam sendo strings opacas; preserve seus bytes ao derivar chaves.

## 2. Organização no servidor

- `files`: posts de blobs. Layout compartilhado inicia com controle cifrado sem attachment. Layout dedicado conserva o primeiro chunk na mensagem inicial.
- `folders`: índices por pasta com cabeçalho e metadados cifrados dos filhos. O índice técnico `storage-v2` contém catálogo V2, planos/recibos, referências, intents de limpeza e claims de alteração. Renomear, mover ou enviar à lixeira muda o índice do arquivo, não os blobs.
- `names`: novas publicações somente de mappings `folderIndex`. Não publicar metadados de arquivos nem registros operacionais V2 nesse canal.
- `serial`: OSER encapsulado no formato criptografado existente. É uma visão de navegação, não fonte completa de chunks, thumbnails ou versões.

Todos os registros V2 do índice técnico, controles e captions de payload são cifrados pelo codec existente. Se o envelope couber nos 2.000 caracteres, é inline. Caso contrário, `metadata.json` contém somente o envelope cifrado, em UTF-8, com content técnico `metadata`. A escrita operacional usa RAM, sem arquivo temporário de plaintext. Nomes privados, fileIds, membros, associações de attachments, caminhos e versões nunca vão no título do post.

Resolver o mapping cifrado `folderIndex` com `folderId: storage-v2` em `names`; seu `threadId` é o destino de todos os registros operacionais. Se não existir, estabelecer o índice root, criar um índice interno no fórum `folders` com cabeçalho `folderIndexHeader`, e publicar somente o mapping em `names`. Não criar metadata de pasta/card na raiz nem incluir registros técnicos no serial de navegação. Escritas respeitam archive/locked/permissões usando leases. O contexto de upload mantém o journal aberto junto com os pools até terminar a escrita, inclusive na retomada individual, em vez de alternar archive/unarchive a cada plano, recibo ou referência. O último lease arquiva a thread; cancelamento e falha também liberam os leases. Não exige alteração do formato dos registros.

Compatibilidade temporária: ler também registros `storage…` antigos em `names`, mesclados por snowflake com o journal atual. O canal de origem do catálogo é operacional e não entra no JSON. Ao modificar um catálogo antigo, publicar sua atualização no índice técnico antes de usar o novo ID. Registros antigos não são apagados, regravados em names ou migrados em massa automaticamente. Sua remoção exige uma operação coordenada e verificada, especialmente se outros clientes ainda utilizarem a versão inicial de V2. O fallback de leitura está registrado em `migration.md`.

### Migração explícita do journal que foi publicado em names

`AppRegistry.MigrateNamesStorageJournalAsync` move somente os tipos operacionais V2 conhecidos, schema 1. A operação deve ser executada com o desktop fechado e outros clientes escritores pausados. Não é acionada na inicialização nem ao ler arquivos. O comando de manutenção é `dotnet run --project tools/NamesScanner -c Release -- --migrate-storage-journal`; `--audit-metadata-layout` é somente leitura e mostra contagens por tipo, sem metadata completo ou secrets.

Cada cópia preserva integralmente o JSON e adiciona `legacyNamesMessageId`, string com a snowflake da origem, dentro do conteúdo cifrado. Primeiro copiar/confirmar **todos** os destinos; conferir novamente o snapshot completo de origens; então, para cada remoção, reler origem e destino e comparar o JSON antes do DELETE somente daquela mensagem de names. Não remover mappings, controles/payloads ou registros de tipo/versão desconhecidos. Falha mantém origens ainda não removidas; destinos já confirmados permanecem válidos. Retomada localiza cópias por `legacyNamesMessageId` e não cria outra cópia quando o conteúdo já confere. Nenhum arquivo temporário de plaintext é criado.

Na leitura, ordenar eventos pelo valor numérico de `legacyNamesMessageId`, quando presente, ou pelo ID atual da mensagem quando ausente. Comparar inteiros exatos, usando `BigInt.parse` em Dart quando necessário, nunca double. Desempatar pelo ID atual. Usar essa ordem lógica também na prioridade de claims. O frontier de paginação continua físico. Se uma cópia recém-descoberta introduzir evento lógico anterior ao frontier, reconstituir o estado pelo journal completo, em vez de aplicar eventos antigos sobre estado recente. Isso impede um catálogo antigo copiado depois de substituir um catálogo mais novo já existente. O atributo é opcional de compatibilidade; não muda o schema criptográfico. Flutter deve reproduzir essas regras para contas migradas.

Depois de remover os registros de names, a versão mínima do escritor precisa reconhecer o mapping storage-v2 e a ordem lógica dos registros migrados. Voltar ao writer V2 inicial exige migração reversa verificada; retornar somente o código ao commit anterior não restaura as mensagens remotas removidas.

Metadados `file`/`folder` legados e tipos não operacionais não são movidos por esse comando: precisam de planejamento por pasta e atualização dos IDs/seriais, não de cópia para o índice técnico. A auditoria da conta deve identificar esses casos antes de declarar a migração completa.

O título compartilhado é `storage-<storageId>`, opaco. Attachments compartilhados recebem `<UUID N>.bin` e `application/octet-stream`. O Discord ainda vê IDs físicos, quantidades, tamanhos cifrados, horários, títulos técnicos, o header ORGAESGC e movimentação de rede. A criptografia não esconde essa análise de tráfego.

## 3. Regra de tamanho e capacidade

O teste é sobre o tamanho original: `sizeBytes <= 1_073_741_824` usa shared; `>` usa dedicated. Um arquivo exatamente de 1 GiB pode ser compartilhado. Preview ou soma de versões não altera essa classificação.

Cada pool admite até **256 fileIds distintos**. Um fileId ocupa uma vaga enquanto houver conteúdo, histórico, reserva pendente ou referência que impeça concluir sua limpeza naquele pool. Versões, chunks, miniaturas e 480p não ocupam vagas adicionais. O mesmo fileId pode ocupar uma vaga em cada pool que contenha versões suas. A capacidade nunca é inferida pelo número de mensagens/attachments.

O catálogo remoto é autoridade. SQLite fornece configurações, identidade de instalação e checkpoints/cache, não uma autorização para reutilizar vagas ausentes localmente. Antes de alterar um pool selecionado, seu registro remoto é buscado novamente. Pools aparentando lotação são relidos antes de criar outro, para encontrar vagas liberadas por outra instância cooperante da mesma instalação. Um catálogo inválido, duplicação de membros, versão desconhecida ou leitura recusada interrompe a reserva, sem sobrescrever o estado remoto.

## 4. Catálogo e escritor exclusivo

O Discord não oferece CAS/transação para esses documentos. A estratégia é conservadora: cada pool tem **um escritor identificado por ownerId**. Uma instalação não reserva vagas, envia blobs ou executa PATCH destrutivo em pools de outra instalação. Para enviar uma nova versão a partir de outro dispositivo, reserva um pool desse dispositivo; snapshots anteriores permanecem apontando para os pools originais.

No desktop, ownerId é UUID aleatório persistido em SQLite, sob uma chave vinculada ao MachineGuid e SID do Windows. Não é uma chave de criptografia. É compartilhado pelo desktop e sincronizador da instalação; semáforos por owner/account/pool e o host de instância única coordenam os processos cooperantes locais. Não copie/reutilize essa identidade entre instalações simultâneas. Uma imagem clonada do sistema/identidade, bots externos e clientes que não respeitem o protocolo estão fora dessa garantia; não existe promessa de lock global atômico.

Objeto interno do catálogo, antes da criptografia:

```json
{
  "schemaVersion": 1,
  "entryType": "storageThread",
  "layoutVersion": 2,
  "storageId": "11111111111111111111111111111111",
  "threadId": "930000000000000100",
  "controlMessageId": "930000000000000101",
  "ownerId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "state": "open",
  "revision": 7,
  "members": [
    { "fileId": "22222222222222222222222222222222", "pendingVersions": ["33333333333333333333333333333333"] },
    { "fileId": "44444444444444444444444444444444", "pendingVersions": [] }
  ]
}
```

O ID da mensagem desse documento não é um campo persistido no JSON: vem do DTO Discord (`CatalogMessageId` somente local). `revision` cresce a cada PATCH, mas não equivale a CAS. `open` permite novas reservas; `closed` impede novas alocações. Retomadas já reservadas são verificadas pelo owner/membro, sem mover seus chunks.

O primeiro content do post, também cifrado, não contém arquivos:

```json
{
  "schemaVersion": 1,
  "entryType": "storageControl",
  "layoutVersion": 2,
  "storageId": "11111111111111111111111111111111",
  "ownerId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}
```

Criação: persistir intent local por storageId, criar post uma vez, salvar thread/control recebidos, publicar catálogo no índice técnico de folders, depois remover intent local. Um resultado HTTP de criação desconhecido **não é reenviado cegamente**. Na retomada, procurar o título opaco em threads ativas/arquivadas e conferir o controle cifrado antes de publicar o catálogo. HTTP 429 e rejeição explícita por limite de threads podem ser tratados conforme a API existente. Uma instalação nova sem aquela identidade/checkpoint não adota automaticamente um pool órfão de outro escritor.

## 5. Reserva, confirmação e reconciliação

```text
para cada arquivo novo elegível:
    persistir transferId, fileId, encryption e sharedAllocationPending=true
sob gate do escritor:
    ler catálogo/journal remoto
    escolher pool open do owner, preferindo vaga existente daquele fileId
    reler o documento escolhido e validar identidade/capacidade
    adicionar membro somente se o fileId ainda não estiver presente
    adicionar transferId a pendingVersions sem duplicar
    validar <=256, gravar catálogo e conferir resposta
persistir storage/threadId/layout=3/sharedAllocationPending=false
somente então admitir descriptors para transmissão
```

Múltiplas reservas do mesmo `(fileId, transferId)` não consomem novas vagas. Reservar um lote altera cada catálogo uma vez. Um catalog PATCH confirmado sem a atualização local posterior é retomado usando o mesmo fileId/transferId do checkpoint pendente.

Depois de publicar o metadata completo, publica-se uma referência `file:`. A reconciliação de reservas remove pendingVersions confirmadas em referências live. Em lote, isso ocorre ao final, evitando reler/regravar o catálogo por arquivo pequeno. Não remover membro somente porque sumiu da lista/cache: a liberação exige limpeza confirmada e nenhuma referência restante.

## 6. Batching global e memória

`SharedPayloadBatcher` agrega descriptors por thread, incluindo arquivos e roles diferentes. Cada descriptor contém contexto criptográfico independente, tamanho e fábrica de stream. Não agrega plaintexts em um blob.

- Máximo de dez attachments por mensagem, cada um até 20 MiB cifrados, soma normal até 200 MiB.
- Dez descriptors prontos disparam o envio. A janela de 250 ms reavalia um lote parcial, mas não o fecha enquanto houver produtores previstos naquele pool. O controller registra os originais futuros antes de iniciar a fila. A miniatura registra seu produtor antes da geração e o retira após enfileirar o descriptor, cancelar ou falhar.
- Preparação ativa ou espera pela vaga de original permitem drenar a sobra após cinco segundos, evitando espera circular, inclusive com paralelismo de arquivo igual a um. Uma exceção preserva os lotes cheios: se todos os produtores pendentes ainda aguardam iniciar e há trabalho independente publicando metadados, manter a espera mesmo após cinco segundos. O controller conta somente a fase depois das prévias, antes de publicar metadata, e libera a contagem no finally; não confundir uma miniatura esperando o batch com publicação independente. Um produtor original é marcado ativo antes de esperar sua vaga. Sem produtores restantes, a próxima reavaliação envia a sobra. Quotas de rede, memória e mensagens pendentes podem acrescentar espera.
- Fila por pool: até 640 descriptors (`64 * 10`), com backpressure. Até 64 mensagens aguardando resposta globalmente.
- Corpos HTTP podem sobrepor a espera dos ACKs. Admissão de rede respeita gate configurado/dinâmico e teto de 16 corpos compartilhados.
- O controller admite até 32 trabalhos de arquivo, contando envio, prévias e publicação, por `Parallel.ForEachAsync` limitado. Originais shared de uma parte usam uma faixa de até 20 descritores, inclusive em lotes mistos, permitindo dois multiparts de dez em andamento. Originais multipart/dedicados mantêm o paralelismo configurado de arquivos. A vaga de original é liberada somente após seus ACKs/checkpoints, antes de gerar prévias e publicar metadata; o trabalho permanece pendente e nunca informa conclusão antecipada. Backpressure da publicação limita a fila, sem disparar o fechamento antecipado por 250 ms; o teto de espera preserva a drenagem.
- Buffers criptográficos são alugados no pool global limitado por bytes. O original é lido por faixas, criptografado em RAM e transmitido por streaming. Não há buffer para o multipart inteiro nem cópia do original para TEMP. Miniaturas shared conservam a fonte pequena em RAM, mas materializam buffers criptográficos somente na transmissão. O índice de faststart e sua fábrica de leitura são liberados ao concluir o original, sem retê-los durante a publicação.
- Mantêm-se Job Object de 1.000.000.000 bytes comprometidos para processos geridos, admissão nativa de 800 MiB, gerenciada de 448 MiB e limite existente de buffers residentes. O switch de paralelismo não ignora esses limites.
- Preview 480p mantém staging oculto ao lado do original; não é uma alteração criptográfica do original.

O rate limiter existente continua respeitando headers de buckets, cota global e HTTP 429. V2 não promete saturação física constante da rede quando Discord, disco, codec ou rede estão aguardando. Não inventar quota maior por existir menos threads.

Uma rejeição específica de tamanho/quantidade pode dividir aquele batch, reutilizando os bytes/contextos de replay existentes. Não reduzir permanentemente o limite normal de dez. Cancelar um participante não cancela outros attachments já admitidos naquele multipart. A resposta é validada, os checkpoints de cada participante são salvos, e o cancelado pode então entrar em limpeza. Se todos cancelarem, o HTTP pode ser cancelado; um resultado remoto incerto permanece recuperável/conservador.

## 7. Plano e recibo duráveis de mensagem

Antes do POST do corpo, gravar no índice técnico de folders o plano cifrado:

```json
{
  "schemaVersion": 1,
  "entryType": "storageBatchPlan",
  "batchId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "threadId": "930000000000000100",
  "parts": [
    { "fileId": "22222222222222222222222222222222", "versionId": "33333333333333333333333333333333", "role": "original", "index": 0, "count": 1, "filename": "cccccccccccccccccccccccccccccccc.bin", "sizeBytes": 1094 },
    { "fileId": "44444444444444444444444444444444", "versionId": "55555555555555555555555555555555", "role": "preview", "index": 0, "count": 1, "filename": "dddddddddddddddddddddddddddddddd.bin", "sizeBytes": 326 }
  ]
}
```

`sizeBytes` é o tamanho cifrado do attachment. `index` e `count` são do role daquele arquivo/versão, não do batch. Nomes técnicos únicos associam a resposta, sem depender da posição no array Discord.

Content interno do POST de payload, cifrado como `payloadReference`:

```json
{ "schemaVersion": 1, "entryType": "payloadReference", "content": "shared:v2 batch:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
```

Depois de validar quantidade, IDs únicos não vazios, filenames e tamanhos de **toda** a resposta, gravar recibo cifrado antes de distribuir confirmações locais:

```json
{
  "schemaVersion": 1,
  "entryType": "storageBatchReceipt",
  "batchId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "threadId": "930000000000000100",
  "messageId": "930000000000000200",
  "attachments": [
    { "filename": "cccccccccccccccccccccccccccccccc.bin", "attachmentId": "930000000000000201", "sizeBytes": 1094 },
    { "filename": "dddddddddddddddddddddddddddddddd.bin", "attachmentId": "930000000000000202", "sizeBytes": 326 }
  ]
}
```

Um checkpoint local é atômico por arquivo; não existe transação de checkpoints de todos os arquivos. Recibo/plano remoto permitem reparar uma distribuição local parcialmente salva. Falha ao salvar um participante não impede salvar os demais. Nunca publicar metadata superior enquanto faltarem confirmações necessárias.

Recuperação desktop relê os planos remotos daquele fileId/transferId e, somente na retomada/limpeza, as mensagens do pool. Decodifica o batchId, seleciona por filename/tamanho e reconstrói `chunkRefs`. Não usa a primeira mensagem/anexo. Ignora o controle sem attachments; um attachment de outro arquivo removido seletivamente não invalida os que ainda existem. O cache de planos é limitado a 1.024 itens; a retomada não depende dele.

## 8. JSON do arquivo e previews

Exemplo completo de conteúdo interno de um file compartilhado, antes do envelope criptográfico:

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "modifiedAt": "2026-10-14T12:00:00Z",
  "storage": { "version": 2, "mode": "shared", "storageId": "11111111111111111111111111111111" },
  "versionId": "33333333333333333333333333333333",
  "versionUploadedAt": "2026-10-14T12:00:00Z",
  "fileId": "22222222222222222222222222222222",
  "threadId": "930000000000000100",
  "name": "Foto com acentuação.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 1024,
  "uploadedAt": "2026-10-14T12:00:00Z",
  "batchId": "3333333333",
  "parentFolderId": "root",
  "chunkRefs": [ { "index": 0, "messageId": "930000000000000200", "attachmentId": "930000000000000201" } ],
  "preview": { "thumbnailRef": { "messageId": "930000000000000210", "attachmentId": "930000000000000211" } },
  "encryption": { "version": 1, "mode": "standard", "salt": "AAECAwQFBgcICQoLDA0ODw" }
}
```

O salt do exemplo é sintético. Produção mantém geração aleatória existente. `versions`, `trash`, `sync`, `fav` e demais campos existentes continuam com os contratos anteriores. Ausências opcionais não autorizam inventar valores nem apagar campos de organização/histórico. `preview` pode ser null quando não houver prévia. `thumbnailRef` e `storage` não aceitam null quando declarados. Não escrever `chunks` para payload novo com chunkRefs.

Thumbnail nova compartilhada exige os dois IDs. O writer omite `thumbnailMessageId` quando existe thumbnailRef. O legado usa thumbnailMessageId somente quando não há referência nova e a mensagem permite seleção unitária inequívoca. Mais de um attachment sem attachmentId é erro explícito, não fallback para o primeiro.

480p continua independente:

```json
{
  "thumbnailRef": { "messageId": "930000000000000210", "attachmentId": "930000000000000211" },
  "video480p": {
    "sizeBytes": 4096,
    "chunkRefs": [
      { "index": 0, "messageId": "930000000000000220", "attachmentId": "930000000000000221" },
      { "index": 1, "messageId": "930000000000000220", "attachmentId": "930000000000000222" }
    ]
  }
}
```

Cada role tem índices contíguos a partir de zero. IDs de mensagem podem repetir; attachmentIds de uma lista não. Arrays vazios/incompletos, lacunas e declaração inválida não viram `chunks` legado. Não inferir compartilhamento somente pela quantidade de attachments: o dedicado também pode usar dez por mensagem.

## 9. Criptografia preservada e representação

Encrypted metadata V1, ORGAESGC V1, AES-256-GCM, HKDF-SHA256, master keys standard/custom, PBKDF2/recovery da chave custom e OSER não foram alterados. Nunca usar threadId, messageId, attachmentId, storageId, ownerId ou batchId de transporte para derivar a chave do arquivo.

Metadata: JSON compacto UTF-8 sem BOM; propriedades com nomes exatos e case-sensitive. Ordem de propriedades e whitespace não importam para leitura após autenticação. C# escreve nulls opcionais conforme os modelos; estruturas não declaradas não mudam o protocolo. Datas são RFC3339 UTC, números inteiros sem separador local, booleanos JSON. Strings privadas ficam somente no plaintext em memória antes do codec.

O envelope V1 possui exatamente `format`, `version`, `encrypted`, `algorithm`, `kdf`, `keyMode`, `salt`, `nonce`, `keyCheck`, `ciphertext`, `tag`. Salt de 16 bytes, nonce de 12, tag de 16, chave de 32. Seus binários são Base64 **padrão com padding**. A chave deriva de HKDF-SHA256(masterKey, salt, UTF8(`organessonium/metadata-key/v1`), 32). KeyCheck é os primeiros 16 bytes de HMAC-SHA256 dessa chave sobre UTF8(`organessonium/metadata-key-check/v1`). AAD é UTF-8 dos campos abaixo unidos por LF, **sem LF final**:

```text
organessonium-metadata
1
true
AES-256-GCM
HKDF-SHA256
<keyMode>
<salt Base64>
<nonce Base64>
<keyCheck Base64>
```

O `encryption.salt` do arquivo, diferente do salt do envelope metadata, usa **Base64URL sem padding**. Chave do arquivo: HKDF-SHA256(masterKey, fileSalt16, info, 32), com `info = UTF8("organessonium/file-key/v1") || 0x00 || UInt32BE(UTF8(trim(fileId)).length) || UTF8(trim(fileId))`.

Blob ORGAESGC:

| Offset | Bytes | Conteúdo |
| --- | --- | --- |
| 0 | 8 | ASCII `ORGAESGC` |
| 8 | 1 | versão 1 |
| 9 | 1 | role: original=1, preview=2, video480p=3 |
| 10 | 4 | índice one-based, UInt32 big-endian |
| 14 | 4 | quantidade global do role, UInt32 big-endian |
| 18 | 8 | tamanho plaintext, UInt64 big-endian |
| 26 | 16 | salt do arquivo |
| 42 | 12 | nonce GCM aleatório |
| 54 | N | ciphertext |
| 54+N | 16 | authentication tag |

O header de 54 bytes é o AAD. Overhead total: 70 bytes. Máximo plaintext por attachment: 20.971.450 bytes. Contexto original usa fileId/encryption daquela versão, index do chunkRefs e count da lista inteira. Thumbnail usa `preview`, index=0/count=1. 480p usa `video480p`, índice/count da lista 480p. Histórico usa a encryption do snapshot, nunca a da versão atual. A associação de transporte não altera nenhum desses bytes. Não concatenar arquivos, não recifrar o conteúdo por ser shared e não reutilizar nonce com contexto/plaintext/chave diferentes.

Vetor determinístico e teste independente existentes em `tests/metadata-v1-vector.json` e `tests/metadata-v1-interop.cjs`, além dos testes ORGAESGC, continuam obrigatórios. Os exemplos JSON deste documento não são instruções para fixar salts/nonces em produção.

## 10. Resolução, cache e streaming

```text
autenticar/descriptografar metadata
selecionar o snapshot atual ou histórico
validar chunkRefs/thumbnailRef/storage
agrupar messageIds distintos dentro daquele thread
GET /channels/{threadId}/messages/{messageId}
selecionar attachment cujo id seja attachmentId
baixar/autenticar somente aquele attachment
```

Leitura normal não varre o post para descobrir chunks. Cache de DTO por `(threadId,messageId)`, 256 entradas, TTL de 30 segundos para respostas completas; GETs simultâneos são deduplicados. Requests em andamento não são descartados por TTL. Se todas as vagas estiverem pendentes, novas resoluções falham/aguardam no fluxo de operação existente, sem criar cache ilimitado. Cancelar um waiter não aborta o GET compartilhado; encerrar a sessão cancela o lifetime do cliente.

PATCH/replace/delete invalidam a entrada da mensagem. CDN 403/404 de miniatura ou vídeo força nova resolução e seleciona **o mesmo attachmentId**, não o primeiro nome/anexo parecido. URLs não são identidade permanente nem metadata persistido. Uma resposta renovada sem o ID esperado é erro. Tag inválida não é tratada como expiração de URL nem como conteúdo vazio.

Miniatura local shared usa `(fileId,versionId,messageId,attachmentId)`; a ponte UI usa requestId do metadata/arquivo, não do pool. O renderer invalida a miniatura quando salt ou thumbnailRef conhecido muda. Sessões/recursos de preview ficam separados por arquivo; o provider de vídeo preserva mapeamento variável de chunks, seek, prefetch e autenticação completa antes de disponibilizar plaintext.

## 11. Histórico, organização e cópia

Substituir mantém fileId, cria versionId/salt novos e conserva snapshot completo anterior em `versions`, sem history recursivo. A regra de tamanho vale por novo conteúdo: pequeno vira shared; grande vira dedicated. Grande pode continuar no dedicado atual quando ele já é dedicado; não reutiliza o pool compartilhado para conteúdo acima do teto. Pequeno usa pool do escritor atual, reaproveitando vaga do fileId quando possível. Não mover chunks históricos para acompanhar a versão atual.

Publicação revalida a versão/batch remoto sob claim antes de atualizar metadata. Mudança concorrente não é sobrescrita silenciosamente. Dados enviados para uma atualização recusada permanecem em checkpoint/limpeza conservadora; não apagar o conteúdo atual para resolver o conflito.

Rename, favorito e move carregam metadata completo, preservam payloads/versões e atualizam referências do índice. Lixeira publica metadata no índice trash antes de remover o anterior; restore preserva refs. Essas operações não removem attachments. Cópia baixa/autentica e reenvia um novo fileId/encryption; pode usar o mesmo pool físico, mas não compartilha attachments do original.

O desktop atual não tem um exportador de compartilhamento JSON ativo ligado ao menu. Não foi inventado outro protocolo de compartilhamento nesta tarefa. Um futuro exportador precisa preservar os snapshots/IDs e cadastrar referência live enquanto tiver autorização para reter blobs; sem referência remota, um JSON externo não impede exclusão definitiva pelo titular.

## 12. Referências e alterações concorrentes

Referência cifrada no índice técnico de folders:

```json
{
  "schemaVersion": 1,
  "entryType": "storageReference",
  "scope": "file:22222222222222222222222222222222",
  "ownerId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "state": "live",
  "fileId": "22222222222222222222222222222222",
  "metadataThreadId": "930000000000000300",
  "metadataMessageId": "930000000000000301",
  "payloads": [
    {
      "fileId": "22222222222222222222222222222222",
      "versionId": "33333333333333333333333333333333",
      "threadId": "930000000000000100",
      "storage": { "version": 2, "mode": "shared", "storageId": "11111111111111111111111111111111" },
      "attachments": [
        { "messageId": "930000000000000200", "attachmentId": "930000000000000201" },
        { "messageId": "930000000000000210", "attachmentId": "930000000000000211" }
      ]
    }
  ]
}
```

`payloads` inclui atual e históricos para scope file. Cada payload agrega refs de original/thumbnail/480p, sem URLs. Legado dedicado pode ter attachments vazios, mas seu threadId continua sendo uma referência de proteção.

Scopes: `file:<fileId>`, `sync:<syncFolderId>:<revisionId>`, `upload:<transferId>`, `download:<transferId>`, `preview:<UUID>`. Estado `released` usa mesmos scope/fileId/ownerId, metadata IDs vazios e payloads vazios. São eventos append-only; a maior snowflake de mensagem por scope define o estado mais recente. PATCH de catálogo é uma exceção e exige leitura fresca do documento conhecido, mesmo que seu ID seja anterior ao frontier.

Downloads e streams registram referências antes de consumir bytes, sem desarquivar o pool. Downloads pausados conservam scope/checkpoint; retomada do scope já autorizado pode terminar após exclusão lógica. Streams liberam ao fechar. Reinício reconcilia somente scopes desse owner sem operação local/checkpoint ativo. Scopes de outro owner não expiram por relógio. Upload de substituição protege o snapshot anterior até concluir/cancelar; reservas pending também protegem upload shared não publicado.

Claim cifrado de metadata:

```json
{ "schemaVersion": 1, "entryType": "storageMutation", "operationId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "fileId": "22222222222222222222222222222222", "ownerId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "state": "pending" }
```

Antes de escrever, publicar pending, reler journal e verificar ausência de fence de exclusão. Entre pending do mesmo fileId, a menor snowflake de criação do claim tem prioridade; o outro retorna conflito. `completed` encerra a operação com o mesmo operationId. Claims locais vivos são registrados antes do POST. Reinício só encerra claims do owner sem operação local ativa. Não encerrar claims de outro owner nem usar timeout para roubar a prioridade.

Isso é um protocolo cooperante com barreiras duráveis, não CAS. As decisões de GC continuam conservadoras e exigem leitura fresca das referências/metadados. A ordem de criação das mensagens não torna o PATCH de Discord uma transação distribuída.

## 13. Exclusão seletiva e GC recuperável

Intent persistido **antes** de remover metadata/reference:

```json
{
  "schemaVersion": 1,
  "entryType": "storageCleanup",
  "operationId": "ffffffffffffffffffffffffffffffff",
  "fileId": "22222222222222222222222222222222",
  "scope": "file:22222222222222222222222222222222",
  "metadataThreadId": "930000000000000300",
  "metadataMessageId": "930000000000000301",
  "payloads": [
    {
      "fileId": "22222222222222222222222222222222",
      "versionId": "33333333333333333333333333333333",
      "threadId": "930000000000000100",
      "storage": { "version": 2, "mode": "shared", "storageId": "11111111111111111111111111111111" },
      "attachments": [ { "messageId": "930000000000000200", "attachmentId": "930000000000000201" } ]
    }
  ]
}
```

Uma intent `file:` é fence contra novos writers/leitores desse arquivo. Não existe apagamento automático por tempo de lixo, leitura, ausência do cache ou contagem local zero.

```text
persistir intent com todas as versões e locator do metadata
remover metadata pelo canal/ID correto e atualizar índices/seriais
publicar released do scope
sob gate do escritor autorizado:
    reler journal e catálogo
    abortar/adicionar pendência se claim ou reserva estiver ativa
    completar referências perdidas usando planos e payloads remotos
    ler nomes/mappings/índices remotos, incluindo trash e syncRevision
    adicionar referências live de transferências/previews e checkpoints locais
    calcular attachments ainda protegidos
    para cada mensagem candidata:
        nunca tocar controlMessageId
        GET fresco, calcular lista keep, preservar também IDs desconhecidos
        se candidato ainda estiver referenciado, manter pendente
        PATCH somente attachments [{id: ID a manter}]
        validar IDs e content da resposta; invalidar cache
        se ficou vazia, DELETE somente dessa mensagem
    remover membro apenas sem referência/reserva restante
    persistir storageCleanupApplied
```

PATCH não inclui arquivos nem content e não reenvia os attachments preservados:

```json
{ "attachments": [ { "id": "930000000000000202" } ] }
```

O cliente serializa edições localmente, busca novamente a mensagem e confere que os IDs continuam iguais ao snapshot usado para escolher keep. Se mudaram, deixa pendente. Mesmo para retirar o último attachment, usa esse PATCH validado antes de deletar a mensagem vazia. O post shared e o controle nunca são deletados. Attachments não conhecidos pela intent são mantidos.

Depois de confirmação final:

```json
{ "schemaVersion": 1, "entryType": "storageCleanupApplied", "operationId": "ffffffffffffffffffffffffffffffff" }
```

Falha antes/após PATCH mantém intent. Retry de 404 de mensagem já removida é idempotente. Se o app caiu depois de remover metadata e antes de released, a retomada verifica o locator da intent: **404 confirmado**, não timeout/403, permite registrar released. Metadata existente mantém a limpeza bloqueada. Sync usa locator da revisão; upload cancelado usa sua intent explícita. Falha de leitura de índice/tag/versão interrompe GC, sem interpretar o índice como vazio.

A enumeração remota inclui versões no metadata, índices da lixeira e revisões sync antigas sem ledger. O ledger de arquivos não substitui essa conferência. Limpeza física de pool de outro owner fica pendente para esse escritor. Referências órfãs, claims externos inconclusivos e POSTs de resultado desconhecido podem reter attachments/vagas. Não usar TTL para decidir que esses dados desapareceram. A estatística de armazenamento existente soma arquivos/versões referenciados, não bytes órfãos operacionais.

Dedicados/legados só permitem remover a thread inteira quando nenhuma referência atual, histórica, sync ou de leitura a protege. Metadata explícito shared, thread registrada shared ou catálogo ilegível **não** é autorização para cair nessa exclusão antiga.

## 14. Threads arquivadas e permissões

Leases de escrita contam usuários por conta/thread no processo, incluindo GUI e sync. O primeiro writer consulta estado e só desarquiva quando `archived=true`. O último writer rearquiva; o lote manual mantém lease de pool durante toda a fila, evitando alternância por arquivo. Rotinas de arquivamento por limite não escolhem pools em uso. Leitura de mensagens/CDN não adquire lease de escrita.

`locked=true` é recusado, sem modificar o bloqueio. HTTP 403/404 no desarquivamento não cria outra thread silenciosamente nem troca IDs existentes. Erro de arquivamento é registrado e não apaga um upload confirmado. O pool pode ser reaberto depois, independentemente de conservar vagas livres quando arquivado.

## 15. Checkpoints, cache, sync e recuperação

O checkpoint shared conserva os campos existentes e acrescenta `storage`, `thumbnailRef` e layout 3. Exemplo estrutural dos campos de transporte, não um substituto para os demais campos obrigatórios do checkpoint existente:

```json
{
  "transferId": "33333333333333333333333333333333",
  "transferType": "upload",
  "status": "paused",
  "fileId": "22222222222222222222222222222222",
  "threadId": "930000000000000100",
  "batchId": "3333333333",
  "payloadLayoutVersion": 3,
  "storage": { "version": 2, "mode": "shared", "storageId": "11111111111111111111111111111111" },
  "totalChunks": 1,
  "thumbnailRef": { "messageId": "930000000000000210", "attachmentId": "930000000000000211" },
  "uploadedChunks": [ { "index": 0, "messageId": "930000000000000200", "attachmentId": "930000000000000201", "sizeBytes": 1024 } ]
}
```

`sharedAllocationPending` é somente local e explícito nos uploads recém-planejados; permite concluir uma reserva com resultado incerto. Não promovê-lo por tamanho em checkpoints layout 1/2. Layout 3 exige storage shared e IDs persistentes; retomada confirma owner/membro, reaproveita fileId/transferId/salt/contagem e não reencripta chunks confirmados. Contexto de replay nonce/digest continua volátil conforme o transporte anterior. Apenas chunks não confirmados podem receber novo nonce ao reiniciar.

SQLite adiciona `storage_json` e `thumbnail_ref_json`. As demais tabelas/PKs permanecem; nomes/message IDs são a chave de entradas, não pool. OSER não recebe listas completas de refs: o consumidor deve hidratar file metadata antes de download/thumbnail/alteração quando só tiver serial. Cache local e checkpoints continuam contendo metadata operacional descriptografado, sem guardar master keys em plaintext; esta tarefa não introduz um formato cifrado de cache nem migração em massa.

Sync envia novo payload pequeno em shared, grande em dedicated, e grava storage/chunkRefs/thumbnailRef no payload da revisão. O decoder roda antes do planner. `syncRevision`/`syncApplied` e regras de conflito/base/tombstone continuam existentes. GC de revisão persiste intent antes de remover journal e usa proteção compartilhada, em vez de DELETE da thread. Conserva heads, bases imediatas e conflitos. O snapshot local em disco do sincronizador, previews suportados pelo Windows e ausência de geração 480p nesse fluxo não foram alterados.

Não fazer migração de conta ao ler. Uma nova versão pode usar V2 sem alterar os snapshots antigos. Catálogo, fila e GC usam o codec central; AES não entra na lógica de navegação/sync.

## Flutter interoperability - shared storage V2

O mobile precisa implementar:

1. Decodificar os envelopes V1 existentes; resolver em names o mapping `folderId: storage-v2`, ler/escrever o journal na thread indicada no fórum folders e reconhecer tipos `storageThread`, `storageControl`, `storageReference`, `storageMutation`, `storageCleanup`, `storageCleanupApplied`, `storageBatchPlan` e `storageBatchReceipt` pelo schema declarado. Names recebe somente mappings novos; aceitar os registros operacionais antigos ali apenas para leitura.
2. Criar/persistir ownerId aleatório por instalação, diferente do desktop. Não importar o owner desktop para disputar vagas; SQLite/secure storage local não é autoridade remota.
3. Aplicar o teto de 1 GiB original e 256 fileIds distintos. Reservar/validar catálogo remoto antes do body, sem usar revision como CAS.
4. Persistir fileId/versionId/encryption/reserva/checkpoint; criar controle sem attachment e tratar ACK desconhecido por descoberta/validação do post existente.
5. Cifrar cada faixa independentemente com ORGAESGC inalterado. Agregar somente descriptors/attachments, incluindo roles diferentes quando conveniente; publicar planos e recibos cifrados.
6. Resolver `chunkRefs` e `thumbnailRef` por IDs exatos, cache limitado por thread/message e renovação CDN por attachmentId. Download de um attachment desktop não exige baixar os outros nove.
7. Manter snapshots de versões com seus thread/storage/encryption/refs, mesmo quando o conteúdo novo cruza o teto ou muda de owner.
8. Preservar todos os campos ao alterar organização/lixeira; não serializar somente a visão simplificada do serial.
9. Publicar claims/referências de leitura e intents antes das mudanças destrutivas. Não apagar pool de outro owner; publicar limpeza pendente para o escritor autorizado.
10. Fazer PATCH com keep IDs, sem multipart/reupload; leitura fresca, preservação de IDs desconhecidos, validação da resposta, proteção do controle e nenhum fallback para DELETE de shared.
11. Reabrir pool e índice do journal só para escrever, respeitar locked/403 e manter leases durante a transferência/finalização. Rearquivar ao encerrar o último writer, sem alternar o journal por registro.
12. Retomar layout 3 via planos/recibos e refs, preservando layout 1/2 de checkpoints antigos; versão desconhecida é erro explícito, não formato legado.

Pseudocódigo de batching móvel:

```text
budget = capacidade real do dispositivo
while houver descriptors ou produtores:
    recolher até min(10, limite seguro local), respeitando janela de espera
    publicar plano cifrado
    gerar/transmitir cada attachment com buffers limitados
    validar resposta inteira por filename/size/id
    publicar recibo cifrado
    gravar atomicamente cada checkpoint participante
quando partes necessárias estiverem confirmadas:
    publicar file metadata e storageReference
```

É válido o mobile enviar um, dois ou três attachments, usar chunks menores ou manter somente o ciphertext do pequeno batch atual. Não copiar dez buffers de 20 MiB, 16 workers, 64 mensagens pendentes, 32 trabalhos de arquivo, 20 originais shared ou a política desktop de processos. Esses valores são agendamento local, não contrato de interoperabilidade. O mobile pode separar envio e publicação com filas menores e prazos adequados ao dispositivo; deve evitar que um batch espere recursos que ele mesmo retém. O número global de chunks no header e nas listas precisa corresponder ao tamanho/chunk layout escolhido pelo mobile. Menor paralelismo não altera capacidade, ownership, independência criptográfica ou capacidade de ler mensagens desktop de dez anexos.

Implementação de reads deve registrar lease antes da CDN, conservar download pausado no checkpoint e não liberar scopes externos por TTL. Implementação de GC precisa tratar falta de prova como pendência. Uma instalação mobile que perder owner/checkpoints não deve tomar a identidade/autoridade do desktop para limpar seus pools.

Antes de habilitar esse formato no mobile, testar desktop upload -> Dart download/thumbnail/seek e Dart upload -> desktop download/versões/GC, além do vetor metadata e ORGAESGC anteriores. Não basta testar JSON sem autenticação nem apenas a chamada de um helper.

## 16. Validação e limites operacionais

Os testes HTTP simulados exercitam serviços reais, codec, multipart, SQLite, checkpoints, índices/seriais e controller. `SharedStorageTests`, `SharedStorageLifecycleTests`, `SharedStorageControllerTests` e `SharedStorageSafetyTests` agrupam os cenários de 256/257 arquivos, dez pequenos, roles mistos, >1 GiB, IDs de mensagens repetidos, thumbnails autenticadas, histórico entre threads, lixeira, archive/locked/403, retomada/ACK perdido, conflito, limpeza recuperável, legado, proteção de leitura e vagas. A UI shared é exercitada em WebView2 com HWND sem estilo visível. Os testes de sync/crypto/multi-attachment existentes afetados também precisam permanecer verdes.

Correspondência com os 28 cenários de aceitação do pedido:

| Cenários | Testes de comportamento |
| --- | --- |
| 1, 2, 3, 27: capacidade, novo pool, agrupamento e reconciliação | `sharedStorageCapacityAndPacking`, `sharedStorageSelectiveCleanupAndRecovery`, `sharedStorageConcurrentOwnershipAndDeletion` |
| 4, 6, 7, 28: mensagens compartilhadas, thumbnails e cache por attachment | `sharedStorageReferencesDownloadsAndThumbnails`, `sharedStorageMixedRolesAndReadProtection`, `sharedStorageCardsHiddenBrowser` |
| 5: arquivo acima do teto | `sharedStorageControllerBatchAndLargeRouting`, com arquivo esparso de 1 GiB + 1 byte transmitido e cifrado integralmente para receptor simulado |
| 8: streaming e seek | `sharedStorageReferencesDownloadsAndThumbnails`, `multiAttachmentVideoPreviewSeek`, `videoEncryptedChunksAndRenewal` |
| 9, 10, 21, 22: exclusão seletiva, último attachment, concorrência e recuperação | `sharedStorageSelectiveCleanupAndRecovery`, `sharedStorageConcurrentOwnershipAndDeletion`, `sharedStorageIndividualCancellation` |
| 11, 12, 13, 14, 15, 24, 26: versões, organização, lixeira, legado e histórico entre threads | `sharedStorageVersionsTrashAndLegacy`, `fileVersionsLifecycle` |
| 16, 17, 18, 19, 23: arquivo interrompido, archive/permissões e publicação | `sharedStorageResumePermissionsAndPublication`, `sharedStorageSelectiveCleanupAndRecovery`, `sharedStorageCreationAckAndMetadataFences` |
| 20: uploads simultâneos no mesmo pool | `sharedStorageConcurrentOwnershipAndDeletion`, `sharedStorageControllerBatchAndLargeRouting` |
| 25: sincronização | `syncDiscordPublicationAndRecovery`, `twoComputerSyncLifecycle`, `syncFailuresPreserveFiles` |

Os casos adicionais de `sharedStorageMixedRolesAndReadProtection` e `sharedStorageCreationAckAndMetadataFences` verificam leitura pausada protegida contra GC, resposta de criação perdida e bloqueio de alterações concorrentes. `sharedStorageIndividualCancellation` confirma que cancelar um participante não cancela o outro nem remove seus anexos. `uploadCardsHiddenBrowser` verifica continuidade dos cards, imagem e seleção durante o upload.

`sharedStoragePublicationPipeline` usa o controller com 46 arquivos, um deles multipart, e bloqueia a publicação real do primeiro metadata no servidor HTTP simulado por mais de cinco segundos. Confere lotes completos mesmo acima dos 32 trabalhos, quatro mensagens de dez anexos e uma sobra de sete, sem conclusão antecipada nem perda dos 46 cards; o journal só é arquivado ao terminar o contexto, sem ciclos por registro. Em seguida envia somente dois originais multipart com paralelismo de arquivo igual a um, verificando drenagem e preservação dos cards anteriores. `sharedStorageDelayedProducers` confirma dez miniaturas com preparação além dos 250 ms no mesmo multipart, liberação por cancelamento, drenagem após bloqueio de preparação e encerramento da sessão sem lote pendurado. `mediaUploadTypes` envia e relê metadata cifrado de treze formatos mistos, sem supor o codec pelo nome da extensão. Esses casos verificam o comportamento remoto do agendamento, não apenas a chamada de helpers.

Validação local em 15/10/2026: solução Release compilada sem erros ou avisos; o projeto de testes compila sem erros, com dois avisos CS0436 existentes do Windows App SDK. A rodada final passou nos 13 cenários relacionados: publicação bloqueada por mais de cinco segundos e lease do journal, classificação/metadata de formatos mistos, produtores atrasados, arquivos grandes, dois casos de cancelamento individual, pequenos arquivos com atualização da pasta, miniaturas/downloads, versões/legado, continuidade dos cards/resumo, ACKs sobrepostos, replay com integridade e geração real de miniaturas. O teste nativo usa FFmpeg de hash idêntico ao distribuído, cobre PNG/JPEG/WebP/AVIF/MOV/MKV e MKV/HEVC, verifica ausência de intermediários e o limite de memória; também gera/envia/autentica as prévias MKV com receptor simulado. HEIC/HEIF são cobertos por classificação e metadata, não por um vetor nativo de decodificação. Nenhuma janela do app foi exibida. Os testes de transferência usam HTTP simulado e não representam nova medição de throughput/quota da conta real nem validação de uma implementação Dart.

Limitações deliberadas: sem CAS; identidade exclusiva de instalação; remoção física estrangeira pendente; refs/claims inconclusivos podem reter órfãos/vagas; catálogo/journal crescem remotamente e não são compactados automaticamente; operações de recuperação/GC podem enumerar names/índices/pools; navegação/download normal não faz essa varredura. HTTP simulado não comprova quota/throughput do Discord real. Nenhuma conta real deve ser usada sem autorização.

Referências da API: [editar mensagem e keep attachments](https://docs.discord.com/developers/resources/message#edit-message), [criar thread de fórum](https://docs.discord.com/developers/resources/channel#start-thread-in-forum-or-media-channel), [threads e permissões](https://docs.discord.com/developers/topics/threads).
