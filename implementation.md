# Organessônium: upload multi-attachment

Especificação do transporte implementado no desktop C# WPF + WebView2. Este documento descreve o agrupamento de payloads e as referências necessárias para outro cliente ler, escrever e retomar o mesmo formato.

A especificação criptográfica anterior foi preservada, sem alterações, em [implementation old.md](<implementation old.md>). Encrypted metadata V1, conteúdo ORGAESGC V1, master keys standard/custom, KDF, AES-GCM e OSER não mudaram. Este documento complementa esses contratos, não os substitui.

## Multi-attachment payload transport

### Limites e identidades

- `DiscordLimits.AttachmentsPerMessage = 10`.
- `DiscordLimits.AttachmentBytes = 20 * 1024 * 1024 = 20.971.520` bytes por attachment, incluindo seu envelope criptográfico.
- `EncryptionService.EnvelopeOverheadBytes = 70` bytes.
- `DiscordLimits.PlaintextChunkBytes = 20.971.450` bytes. O último chunk pode ser menor. Um arquivo vazio ainda utiliza um payload criptografado com plaintext vazio.
- Não existe validação local de 25 MiB para a soma do multipart. Dez attachments no limite individual resultam em aproximadamente 200 MiB transmitidos, mais headers, sem criar um chunk lógico de 200 MiB.

Os limites individuais acima são a configuração atual do projeto. O prompt desta implementação forneceu resultados de testes reais de mensagens com 2 e 10 attachments de 20 MiB, e rejeição de 11. A implementação respeita também as respostas de limite do servidor; esses resultados não garantem a mesma capacidade em qualquer conta/rota futura.

| Conceito | Identificação e significado |
| --- | --- |
| Chunk lógico | Índice zero-based dentro do original ou da versão 480p. Mantém criptografia, tamanho e posição independentes. |
| Mensagem Discord | `messageId`, dentro do `threadId` de payload. Pode conter até dez chunks. |
| Attachment Discord | `attachmentId`, retornado pela API. Identifica exatamente um payload na mensagem. |
| Arquivo | `fileId`, identidade estável para metadata, chave derivada e operações de biblioteca. |
| Thread de payload | `threadId`, post no fórum `files`. Não é o thread de índice de pasta. |

O par `(messageId, attachmentId)` é a localização persistente de um chunk dentro do thread. URLs da CDN são temporárias e não substituem esses IDs. IDs Discord são strings no JSON, nunca números de ponto flutuante.

A ordem é sempre:

```text
arquivo -> chunks plaintext atuais -> ORGAESGC individual
        -> attachments independentes -> mensagem multipart
```

Não concatenar plaintext/ciphertext dos dez chunks para formar outro payload. Não alterar salt, chave, nonce, role ou header apenas porque mudou o agrupamento HTTP.

### Metadata exato

O schema dos metadados comuns permanece `schemaVersion: 2`. Um payload novo utiliza:

```json
"chunkRefs": [
  { "index": 0, "messageId": "100", "attachmentId": "200" },
  { "index": 1, "messageId": "101", "attachmentId": "201" },
  { "index": 2, "messageId": "101", "attachmentId": "202" }
]
```

Este fragmento representa os campos internos, antes da cifra do metadata. Propriedades exatas: `index`, `messageId`, `attachmentId`. `index` é inteiro zero-based. Nenhum campo novo de URL, offset, chave ou nonce é necessário na referência. O tamanho real vem do attachment e do envelope ORGAESGC; os demais dados criptográficos continuam no objeto `encryption` existente.

Regras de leitura e escrita:

1. `chunkRefs` presente seleciona o layout novo. `null`, outro tipo JSON, array vazio, referência nula, índice ausente/negativo/duplicado, lacunas ou IDs vazios são inválidos. Não fazer fallback silencioso para `chunks`.
2. Um metadata completo deve possuir índices contíguos `0..N-1`. A ordem física do array não é obrigatória; ordenar pelo índice antes de consumir ou publicar. A ordem das propriedades JSON não importa.
3. `(messageId, attachmentId)` não pode aparecer duas vezes no mesmo payload. Vários índices podem compartilhar `messageId`, mas devem apontar para attachments distintos.
4. `chunkRefs` ausente seleciona `chunks: string[]` legado. Seu índice é a posição da mensagem no array; cada mensagem representa um chunk e o leitor conserva a seleção unitária antiga.
5. Se ambos os campos vierem preenchidos, só aceitar quando `chunks` tiver o mesmo comprimento, sem mensagens duplicadas, e cada posição corresponder ao `messageId` da referência equivalente. `chunkRefs` continua autoritativo. Não escrever ambos em payloads novos.
6. Um array legado vazio ao lado de referências válidas não define outro layout. Ele pode existir como default de um modelo/cache antigo, mas o escritor novo omite `chunks`.
7. Renomear, mover, favoritar, restaurar ou salvar novamente um arquivo antigo preserva seu layout de payload. Cifrar novamente o metadata não transforma mensagens unitárias em agrupadas.

O JSON é serializado em UTF-8, sem BOM, utilizando nomes camelCase explicitados pelo contrato. `null` de propriedades opcionais é omitido nas publicações. Datas continuam ISO-8601 UTC, tamanhos são inteiros de bytes, booleanos são JSON e arrays não recebem representação binária. O metadata completo é cifrado antes da escolha entre conteúdo inline e `metadata.json`. Não há formato novo de envelope de metadata.

### Exemplo completo novo

Exemplo estrutural com IDs fictícios e salt público ilustrativo. Não representa dados existentes nem um vetor criptográfico; o vetor de cifra continua no documento anterior.

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "11111111111111111111111111111111",
  "threadId": "900",
  "parentFolderId": "root",
  "name": "Vídeo de exemplo.mp4",
  "mimeType": "video/mp4",
  "sizeBytes": 41943800,
  "uploadedAt": "2026-10-07T12:00:00Z",
  "batchId": "exemplo",
  "chunkRefs": [
    { "index": 0, "messageId": "100", "attachmentId": "200" },
    { "index": 1, "messageId": "101", "attachmentId": "201" },
    { "index": 2, "messageId": "101", "attachmentId": "202" }
  ],
  "preview": {
    "thumbnailMessageId": "103",
    "video480p": {
      "sizeBytes": 41943000,
      "chunkRefs": [
        { "index": 0, "messageId": "102", "attachmentId": "300" },
        { "index": 1, "messageId": "102", "attachmentId": "301" },
        { "index": 2, "messageId": "102", "attachmentId": "302" }
      ]
    }
  },
  "encryption": {
    "version": 1,
    "mode": "standard",
    "salt": "AAECAwQFBgcICQoLDA0ODw"
  }
}
```

`preview.video480p.chunkRefs` é outro array independente, no mesmo thread de payload. Ele utiliza os mesmos nomes de propriedades e regras, mas índices e `ChunkCount` referentes apenas à prévia. A miniatura continua sendo um único attachment referenciado por `thumbnailMessageId`.

### Exemplo completo legado

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "22222222222222222222222222222222",
  "threadId": "901",
  "parentFolderId": "root",
  "name": "Arquivo antigo.mp4",
  "mimeType": "video/mp4",
  "sizeBytes": 30000000,
  "uploadedAt": "2026-09-01T12:00:00Z",
  "batchId": "antigo",
  "chunks": ["110", "111", "112"],
  "preview": {
    "thumbnailMessageId": "113",
    "video480p": { "sizeBytes": 1000000, "chunks": ["114"] }
  }
}
```

Sem `encryption`, os payloads desse exemplo são plaintext legados. Um metadata legado também pode ter `encryption` V1 e `chunks`; nesse caso, as mensagens continuam unitárias, mas cada chunk é autenticado normalmente. Ambos permanecem legíveis. Não migrar ao ler ou baixar.

### Contexto criptográfico e contagens

`EncryptedPayloadContext.ChunkIndex` recebe `chunkRefs.index`, zero-based. O header ORGAESGC existente grava esse valor mais um. `ChunkCount` deve ser o número total de chunks lógicos daquele payload, obtido do array completo, não o número de mensagens, attachments do batch ou partes pendentes na retomada.

No exemplo novo:

- Original: role `original`, `ChunkCount = 3`, índices de contexto `0, 1, 2`.
- Prévia: role `video480p`, `ChunkCount = 3`, índices de contexto `0, 1, 2`.
- Thumbnail: role `preview`, índice `0`, quantidade `1`.

Os demais roles existentes, inclusive imagens Markdown, conservam sua semântica. Alterar índice, quantidade global, role, ciphertext ou tag invalida a autenticação. Uma retomada que ainda precisa dos índices 1 e 2 continua usando quantidade global 3.

### payloadReference agrupado

Uma mensagem com um attachment conserva o rótulo anterior:

```text
file:11111111111111111111111111111111 role:original chunk:1/26 encryption:v1
```

Com vários attachments, o rótulo interno exato é:

```text
file:11111111111111111111111111111111 role:original chunks:2,3,4,5,6,7,8,9,10,11/26 encryption:v1
```

Números one-based, separados por vírgula sem espaços, seguidos por `/` e a quantidade global. Não há um identificador criptográfico novo de batch. `batchId` permanece o identificador de transferência existente no metadata/checkpoint, não entra no rótulo criptografado novo.

O rótulo vira o JSON interno abaixo, então passa pelo codec encrypted metadata V1:

```json
{
  "schemaVersion": 1,
  "entryType": "payloadReference",
  "content": "file:11111111111111111111111111111111 role:original chunks:2,3,4,5,6,7,8,9,10,11/26 encryption:v1"
}
```

A mensagem Discord não expõe esse JSON em claro. O leitor novo localiza anexos pelos IDs de `chunkRefs`, não extrai localização desse rótulo. Não tentar passar `chunks:` pelo parser unitário antigo. Rótulos antigos plaintext e unitários continuam no caminho legado já existente.

## Upload e multipart

### Primeiro chunk e forum post

Quando não existe thread, o desktop cria o post com apenas o chunk 0 pelo endpoint de forum post. Ele valida e salva o `messageId` e o `attachmentId` inicial. Não assume que esse endpoint aceite dez arquivos.

Os índices restantes são agrupados em dez por chamada a `POST /channels/{threadId}/messages`, exceto o lote final incompleto. Para 26 chunks, o caso sem falhas é: 1 no post inicial, depois 10, 10 e 5. A pressão de RAM faz a preparação de anexos aguardar, não reduz a quantidade por mensagem. Uma rejeição específica de tamanho/quantidade do servidor ainda utiliza a redução já existente. Mensagens podem ser enviadas em paralelo; a ordem de conclusão não determina a ordem do arquivo. Descritores podem ser preparados durante o post inicial; o envio das mensagens exige o thread confirmado.

Uma prévia 480p ou atualização Markdown dentro de um thread existente não precisa criar outro post, então pode agrupar desde seu índice 0. Um checkpoint já com thread continua nesse mesmo thread.

### Corpo HTTP

O cliente envia `multipart/form-data` em streaming:

```json
{
  "content": "<envelope cifrado de payloadReference>",
  "attachments": [
    { "id": "0", "filename": "chunk-2.bin" },
    { "id": "1", "filename": "chunk-3.bin" }
  ]
}
```

Esse JSON ocupa `payload_json`. As partes binárias têm nomes `files[0]`, `files[1]`, etc., correspondentes aos IDs locais do multipart. Esses IDs locais não são `attachmentId` remoto, nem o índice lógico do chunk. Os nomes técnicos são únicos dentro do batch e usam a nomenclatura criptografada anterior, com número lógico mais um.

Na resposta, mapear cada arquivo enviado pelo filename técnico exato; conferir quantidade, unicidade, tamanho em bytes e ID remoto não vazio. Não confiar na ordem de `attachments` da resposta. Depois dessa validação, construir `chunkRefs` com os índices lógicos originais e os IDs reais retornados. Uma resposta incompleta não confirma parcialmente os chunks daquele request.

### Algoritmo de upload

```text
carregar ou criar contexto de cifra e layout do checkpoint
validar referências já confirmadas
pendentes = índices globais menos confirmados e o chunk inicial se falta thread
selecionar agrupamento de 10 (último lote pode ser incompleto)
iniciar produtor independente das vagas HTTP:
    adquirir uma das 64 vagas globais de mensagens pendentes
    criar descritores de faixa/filename/tamanho/contexto, sem ler o arquivo
    publicar descritores na fila (capacidade: um lote por arquivo)
se não existe thread:
    criar forum post com o descriptor do chunk 0, usando a mesma transmissão sob demanda
    validar resposta e persistir referência inicial
iniciar consumidores de mensagens limitados globalmente a 64:
    criar multipart com até dez conteúdos de comprimento conhecido
    adquirir worker de transmissão; respeitar rate limiter antes do request
    para cada attachment que o HTTP está prestes a consumir:
        reservar os bytes desse anexo no orçamento global
        ler somente sua faixa; cifrar em RAM com contexto global
        transmitir e reportar bytes; zerar/devolver buffers e reserva
    ao terminar o último attachment, devolver worker de transmissão
    aguardar resposta sem ocupar worker nem buffers dos anexos
    validar todos os attachments retornados
    salvar um checkpoint contendo todos os chunks do request
    publicar número de chunks confirmados, sem contar mensagens
    descartar nonce/digest/descritores e liberar vaga de mensagem pendente
em falha ou cancelamento:
    cancelar produtor e consumidores; liberar vagas e dados também do lote da fila
validar referências completas e ordená-las por índice
publicar metadata cifrado com chunkRefs, sem chunks
```

A nova API de resultado utiliza referências completas. Não gerar `chunks` com messageIds repetidos para fingir compatibilidade com leitores antigos. Referências do payload só são publicadas no metadata após o upload completo e válido.

## Download, preview e streaming

```text
decodificar e autenticar metadata antes da lógica de negócio
se chunkRefs presente:
    validar array completo e ordenar por index
    obter mensagens de distinct(messageId), no thread de payload
    para cada referência:
        selecionar exatamente um attachment com id == attachmentId
        falhar se mensagem/anexo estiver ausente ou duplicado
        registrar URL/tamanho real no índice do chunk
    N = quantidade global das referências
    baixar chunks com concorrência limitada
    autenticar cada um com fileId, role, index e ChunkCount=N
senão:
    executar resolver chunks unitário legado
concatenar plaintexts por índice, sem renumerar pendentes
conferir tamanho final com sizeBytes
```

`GetMessagesByIdsAsync` recebe IDs de mensagens deduplicados. Assim, dez chunks na mesma mensagem não exigem dez GETs para montar uma resolução. A resposta pode chegar em ordem diferente da solicitada. A tabela de lookup é por ID remoto.

Falhas do layout novo não seguem recuperação por batch/nome ou seleção do primeiro attachment. O antigo caminho `chunks` conserva seus parsers, recuperação por batch e payloads plaintext quando não existe `encryption`.

O player virtual continua usando tamanhos plaintext reais, prefix sums, seek e prefetch limitado. Cada chunk é autenticado integralmente antes de disponibilizar bytes. O cache de vídeo não baixa o arquivo inteiro. Quando uma URL expira, a consulta de renovação seleciona o mesmo `attachmentId`; filename não substitui identidade. A deduplicação de mensagens ocorre na resolução inicial; renovações posteriores ainda podem consultar a mensagem para chunks distintos.

O download 480p utiliza `preview.video480p.chunkRefs` ou o array legado daquela prévia, com o contexto `video480p`. Não reutilizar a quantidade de chunks do original para uma prévia de quantidade diferente. Os previews de imagem e thumbnails unitários não foram convertidos em batches misturados com o original.

## Checkpoint e retomada

O JSON local `TransferState` inclui `payloadLayoutVersion`:

- `1`: layout unitário antigo. Também é o default quando a propriedade está ausente em um checkpoint já existente.
- `2`: layout novo. Cada elemento confirmado inclui `index`, `messageId` e `attachmentId`.
- Outra versão: erro explícito de layout não suportado.

`uploadedChunks` mantém também `sizeBytes` do ciphertext enviado e `logicalSizeBytes` do plaintext, além dos campos locais já existentes. Índices são zero-based. `totalChunks` e `chunkSizeBytes` pertencem ao arquivo inteiro. Exemplo de fragmento local confirmado:

```json
{
  "payloadLayoutVersion": 2,
  "threadId": "900",
  "batchId": "exemplo",
  "chunkSizeBytes": 20971450,
  "totalChunks": 3,
  "uploadedChunks": [
    { "index": 0, "messageId": "100", "attachmentId": "200", "sizeBytes": 20971520, "logicalSizeBytes": 20971450 },
    { "index": 1, "messageId": "101", "attachmentId": "201", "sizeBytes": 20971520, "logicalSizeBytes": 20971450 },
    { "index": 2, "messageId": "101", "attachmentId": "202", "sizeBytes": 970, "logicalSizeBytes": 900 }
  ],
  "pendingChunks": []
}
```

Após validar toda a resposta, `MarkUploadChunksAsync` serializa a alteração em memória e salva todos os elementos de uma mensagem de uma vez. O repository grava um arquivo temporário de checkpoint e o move sobre o anterior, sem apagar primeiro o estado antigo. Conclusões concorrentes são serializadas para não sobrescrever chunks de outro batch. O batch inicial de um attachment segue a mesma regra.

Ao reabrir:

```text
ler checkpoint e seu layout
preservar fileId, salt, threadId, batchId, tamanho de chunk e quantidade global
validar os índices confirmados; referências parciais podem ter lacunas
pendentes = 0..totalChunks-1 menos confirmados
layout 1: continuar unitário, sem fabricar attachmentId
layout 2: agrupar apenas pendentes e conservar IDs confirmados
```

Uploads antigos parcialmente enviados não são repartidos usando o limite atual. Downloads retomados reaproveitam partes locais somente quando mensagem e attachment correspondem à referência esperada. O contexto de autenticação continua usando a quantidade global, mesmo quando resta apenas um chunk.

O estado mantém `videoPreview480pChunkRefs` para uma prévia agrupada concluída; `videoPreview480pChunks` continua como representação legada. Uma geração/upload parcial de prévia ainda não constitui um checkpoint completo por batch da prévia: a retomada pode gerar e enviar novamente essa etapa se ela não tiver sido concluída. Não há alegação de exatamente uma publicação remota em uma falha após o Discord confirmar mas antes da persistência local.

## Memória, concorrência, retries e limites

O agendador de produção mantém dez attachments por mensagem. `DiscordLimits.UploadMessageTargetBytes = AttachmentsPerMessage * AttachmentBytes` corresponde a 200 MiB com a configuração atual. Não há buffer de 200 MiB por mensagem: os conteúdos HTTP são descritores de anexos preparados sob demanda. ORGAESGC, offsets, tamanho dos chunks, `chunkRefs`, checkpoint e limites da API não mudaram.

Em `DiscordRequestLimiter`, uploads multipart não serializados têm reservas com sequência. Headers novos marcam a sequência da requisição respondida como limite do snapshot; reservas posteriores ainda ocupam vagas, e respostas antigas não devolvem cotas. Expirar uma janela renova a cota de uploads sem descontar reservas que pertencem à janela anterior. As demais chamadas conservam o desconto de todas as requisições em andamento. O mecanismo não ignora `Remaining = 0`, backoff global/local, `retry_after`, cancelamento nem os 45 requests/s globais configurados.

`UploadMemoryCoordinator.PreparePartAsync(..., deferBuffers: true)` devolve um `PreparedUploadPart` com filename técnico, comprimento cifrado, tamanho lógico, contexto/fábrica de preparação e estado de replay pequeno. `UploadPartContent` declara o comprimento antes de transmitir, mas só lê/cifra quando `SerializeToStreamAsync` consome aquele anexo. `PreparedUploadPart.CopyToAsync` reserva seus bytes, prepara o chunk, transmite em blocos de até 64 KiB e libera/zera o buffer imediatamente após a cópia. Não espera a resposta da mensagem. Não há fallback de ciphertext temporário no SSD.

`RamUploadBudget` continua compartilhado entre biblioteca, retomadas, sincronização e descriptografia de downloads. Dois workers de leitura/cifra limitam o plaintext transitório. Reservas por bytes incluem headroom para esses dois buffers e ajustam-se ao consumo de C#/WebView2; o orçamento de ciphertext permanece em torno de 400 MiB, não aumenta com a quantidade de mensagens pendentes. Os caminhos unitários/materializados conservam as reservas anteriores de batch; o upload agrupado usa `streamPreparedParts: true` e não faz essa reserva de 200 MiB. `maxPreparedPartBytes` continua validando o limite superior do anexo cifrado, incluindo os 70 bytes ORGAESGC.

Há três limites independentes: até 16 workers de transmissão configurados/adaptativos, dois preparadores de chunks e `DiscordLimits.MaxPendingUploadMessages = 64` vagas globais para mensagens em preparação/envio/confirmação. A fila por arquivo contém um batch de descritores. `UploadBodySlot` adquire a vaga de transmissão antes do rate limiter/request e devolve-a ao terminar o último anexo; a resposta pode continuar pendente. Uma mensagem ocupa sua vaga global até confirmar/persistir ou falhar/cancelar. Esses limites impedem filas e conexões pendentes ilimitadas, sem manter centenas de MiB por ACK. A quantidade de workers regula corpos em transmissão, não o número de respostas pendentes. Cada retry recebe outro slot de transmissão e passa novamente pela fila REST.

Na execução normal/background, `AppMemoryBudget` configura um Windows Job Object com `JOB_OBJECT_LIMIT_JOB_MEMORY = 1.000.000.000` bytes, incluindo processos WebView2 registrados. Esse limite controla memória privada comprometida, conforme [a definição do Windows](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information), não páginas compartilhadas/GPU ou cache de disco. O runtime configura `System.GC.HeapHardLimit = 536870912` bytes. A admissão utiliza alvos inferiores: 800 MiB totais e 448 MiB gerenciados. Sob pressão, aguarda de maneira assíncrona e cancelável em vez de criar ciphertext no disco ou aumentar a reserva sem limite. O sistema operacional ainda pode paginar RAM, independentemente do app.

`Forçar 16 workers (RAM até 1 GB)` ignora somente a redução dinâmica de workers por vazão. `UploadConcurrencyRegistry` reconfigura os gates registrados por biblioteca, retomadas e sincronizador ao salvar a opção. Vagas adicionais passam a estar disponíveis no upload atual, sem reinício. Desativar afeta admissões seguintes, sem abortar requests em andamento. Memória, limites de attachments, rate limits e HTTP 429 nunca são ignorados. Abertura, leitura, cifra e montagem executam fora do Dispatcher WPF; armazenamento lento não bloqueia a thread da interface.

`MultipartFormDataContent` usa `UploadPartContent` nos anexos sob demanda e `StreamContent` nos caminhos já materializados, sem byte array para o multipart inteiro. Durante um ACK lento, o primeiro caminho mantém apenas estado técnico pequeno por chunk. Faststart, geração de prévias, snapshot estável da sincronização, checkpoints e arquivos de destino do download conservam o I/O existente; não são temporários de criptografia do upload.

HTTP 429, headers de buckets, alvo global de 45 requests/s, backoff e cancelamento continuam no cliente Discord. Um batch é uma chamada REST, não uma chamada por attachment. Cada retry cria outro multipart. Para anexos sob demanda, relê a origem e regenera o ciphertext autenticado após validar sua identidade; caminhos materializados reabrem os bytes anteriores. Backoff não mantém o buffer do anexo. Reproduzir os bytes de uma tentativa não autoriza utilizar o mesmo nonce com outro conteúdo.

### Regeneração segura em retry

O estado de replay é local, volátil e não pertence ao metadata/checkpoint. Para cada chunk, a primeira tentativa guarda nonce aleatório de 12 bytes e digest de 32 bytes. A definição exata é `D = SHA256(H || K || P)`: `H` são os primeiros 42 bytes do header ORGAESGC (todos os campos anteriores ao nonce); `K` é a chave derivada do arquivo, 32 bytes; `P` são os bytes plaintext exatos da faixa. Os comprimentos fixos de H/K tornam a concatenação inequívoca. A primeira tentativa cifra com nonce CSPRNG. Uma tentativa posterior calcula D novamente e usa comparação em tempo constante; se o digest diferir, falha antes de AES-GCM, sem enviar bytes desse anexo. Assim conteúdo, chave, salt, papel, posição, contagem e tamanho precisam permanecer iguais para reproduzir nonce/ciphertext/tag. Não persistir D, K ou P; zerar D/nonce ao descartar o descriptor. A chave é derivada e zerada na rotina existente, sem ser retida no estado de replay. Após reiniciar o app, chunks não confirmados podem receber novos nonces; os chunks confirmados e seus IDs continuam no checkpoint.

HTTP 413, code 40005 ou HTTP 400/code 50035 com erro específico de quantidade/tamanho em `attachments`/`files` reduzem o batch pela metade, até um attachment, mantendo ciphertext e `chunkRefs`. Outros HTTP 400 não acionam essa redução. O teto aprendido é respeitado também por lotes já preparados.

`UploadWireProgressStream` conta bytes após escrita no stream de destino HTTP. `UploadByteProgress` separa bytes de transmissão (incluindo retries) de progresso lógico único, com callbacks limitados e publicação final na confirmação. O contador `Partes confirmadas X/Y` só avança após validar/persistir a resposta. A UI recebe um oitavo argumento opcional em `_opsTransferMetrics` para o contador de transmissão, sem confundir bytes repetidos com conclusão lógica. A velocidade e a estimativa aparecem durante o corpo do request, não somente no ACK; atualizações atrasadas não substituem uma conclusão por estado ativo.

A preparação de anexos em diferentes requests se sobrepõe à rede e as confirmações ficam desacopladas dos workers. Isso não garante ocupação de rede de 100%: storage mais lento que a rede, pressão de memória, saturação das 64 confirmações pendentes, rate limits e falhas podem exigir esperas. A criação do thread e a confirmação final continuam sendo etapas reais. A estimativa de bytes restantes não inclui futuras esperas nem fases posteriores de prévias/metadados.

## Integração com biblioteca, cache e sincronização

- `AppEntry.ChunkReferences` e `VideoPreviewChunkReferences` carregam as novas referências internas; `ChunkMessageIds` fica reservado ao legado.
- SQLite `app_entries` recebeu colunas opcionais `chunk_refs_json` e `video_preview_chunk_refs_json`. Instalações antigas recebem colunas por migração aditiva, sem apagar registros.
- O cache local continua operacional e pode conter metadata em claro. A cifra remota não protege os arquivos locais de um usuário com acesso ao perfil.
- OSER não foi alterado: continua guardando o resumo do card, não a lista completa de chunks. Quando faltam detalhes, o desktop carrega o metadata completo antes do download/preview.
- Copies e hidratação preservam referências de original e 480p, inclusive entre cache, serial e telas especiais.
- Favorito, move, rename, Lixeira/restauração preservam os IDs de attachment. Não reuploadam payloads para mudar organização.
- `CloudSyncRepository` usa o mesmo transporte novo e preserva `chunkRefs` no payload das revisões. Comparação de hashes, ancestralidade, conflitos, tombstones, atomicidade de download e bytes do original permanecem inalterados.
- Imagens Markdown unitárias continuam com seu transporte de preview existente. A atualização do documento Markdown utiliza o novo resultado com referências.

## Erros e integridade

Falhar explicitamente quando a referência é inválida, a versão do checkpoint é desconhecida, falta mensagem/attachment, a resposta não confirma exatamente os arquivos enviados ou a autenticação do payload falha. Não devolver arquivo vazio, omitir chunks inválidos, escolher outro anexo pelo nome ou degradar `chunkRefs` para legado.

A resposta HTTP confirma transporte; a autenticação GCM confirma conteúdo e contexto ao ler. IDs corretos não dispensam GCM. A publicação de metadata preserva a cifra V1 e a estratégia existente de atualização da mensagem. Falha de tag não expõe plaintext; campos inválidos usam as mensagens de erro existentes, sem redesenho da interface.

Não adicionados logs de INFO/DEBUG ou logs contendo metadados completos, chaves ou plaintext. IDs dos exemplos são fictícios.

## Flutter interoperability - multi-attachment

O transporte descrito aqui está implementado e testado no desktop. Esta tarefa não alterou o código Flutter. Clientes móveis sem suporte a `chunkRefs` ainda precisam implementar este contrato para abrir os novos payloads agrupados; o fallback desktop não transforma o formato novo em algo que um leitor antigo entenda.

A implementação Dart deve reproduzir exatamente:

1. Ler encrypted metadata V1 usando a especificação de [implementation old.md](<implementation old.md>), antes de interpretar arquivo/preview.
2. Testar presença real da chave JSON `chunkRefs`; não confundir ausente com `null`, nem tentar `chunks` após detectar referências inválidas.
3. Manter IDs como strings, validar índices e pares únicos, ordenar por `index`, deduplicar consultas por `messageId` e selecionar o anexo por `attachmentId`.
4. Usar `ChunkIndex = index` zero-based no contexto e o número global do array completo como `ChunkCount`. O header ORGAESGC continua one-based internamente.
5. Preservar os protocolos de cifra existentes. Ciphertext de cada attachment deve ser byte-compatible com o leitor C#; não cifrar o conjunto como um único payload.
6. Criar forum post novo com um attachment inicial, depois multipart `payload_json` e `files[n]` com dez anexos, exceto o lote final incompleto/rejeição específica do servidor, com IDs locais correspondentes.
7. Validar toda a resposta por filename/tamanho/IDs, sem depender da ordem retornada. Persistir checkpoints inteiros de batch com layout versionado e referências completas.
8. Preservar checkpoints e metadata unitários antigos. Não produzir `chunks` com IDs repetidos, nem migrar ao ler.
9. Aplicar as mesmas regras à lista independente `preview.video480p.chunkRefs`; conservar thumbnails unitários, seek/prefetch e renovação por ID.
10. Usar conteúdos de comprimento conhecido e streams assíncronos sob demanda, em vez de preparar dez byte arrays antes de criar cada request. A política local de memória pode ser diferente, sem alterar o protocolo.

### Pipeline a reproduzir no mobile

Separar o número de corpos em transmissão do número de mensagens aguardando ACK. A vaga de transmissão deve ser adquirida antes de iniciar o request e liberada após consumir o último arquivo do multipart, inclusive em falha/cancelamento. A vaga de mensagem pendente permanece até validar/persistir a resposta. Usar filas limitadas e teto explícito de mensagens pendentes; o desktop usa 64, mas o mobile pode ter um orçamento menor de conexões e RAM. A contagem de anexos não depende desse orçamento: um request continua com dez descritores mesmo quando só existe RAM para um chunk em cada corpo ativo.

Cada stream de anexo deve aguardar admissão de memória, ler somente sua faixa, produzir ORGAESGC e alimentar o HTTP com backpressure. Só devolver/zerar o buffer depois que o consumidor HTTP terminar de utilizar seus bytes; não zerar um `Uint8List` ainda referenciado por eventos pendentes. Não montar o multipart inteiro nem gravar ciphertext temporário. Preparação lenta deve executar sem bloquear o isolate responsável pela UI.

No retry, recriar os conteúdos HTTP e readquirir worker/cota. Para reproduzir o comportamento desktop, guardar somente D/nonce por chunk e aplicar a regra da seção de regeneração segura. H/K/P precisam ser idênticos antes de reaplicar o nonce. Esses dados de replay não são enviados ao Discord nem adicionados a `chunkRefs`; a interoperabilidade remota continua baseada no envelope ORGAESGC e nos IDs de attachment. Não registrar digest dependente de chave, nonce acompanhado de contexto privado, chave ou plaintext em logs.

Exemplo de agendamento independente de biblioteca HTTP:

```text
mensagem = adquirir vaga limitada de ACK pendente
descritores = próximos dez índices não confirmados
para cada tentativa:
    transmissão = adquirir worker e respeitar cotas Discord
    criar multipart reabrível de comprimentos conhecidos
    para cada anexo solicitado pelo HTTP:
        aguardar orçamento de RAM
        ler faixa; validar estado de replay; cifrar ORGAESGC
        emitir bytes com backpressure e progresso
        zerar/devolver buffers e orçamento
    devolver worker de transmissão, antes de esperar ACK
    em 429/falha transitória: aguardar política existente e repetir tentativa
    em sucesso: validar todos os IDs/tamanhos; persistir batch atomicamente
descartar replay e devolver vaga de mensagem
```

Aplicar também em arquivos retomados layout 2, prévias 480p, salvamento de Markdown agrupado e uploads sincronizados. Layout unitário antigo e downloads permanecem como descritos nas seções de compatibilidade. Um ACK pendente não deve impedir o envio de outros batches se existir worker, memória e cota disponíveis. Testar ativação/desativação do modo forçado durante uma transferência, mantendo os limites reais mesmo no modo forçado.

Testes futuros entre plataformas: desktop upload -> mobile download/preview/seek; mobile upload -> desktop download/preview/seek; retomada com chunks confirmados; metadados antigos nas duas versões; corrupção e attachment ausente sem fallback inseguro. Usar o vetor criptográfico anterior para verificar cifra, e os exemplos/fixtures de referências desta especificação para verificar o transporte.

## Validação e arquivos relacionados

`UploadCadenceDiagnostics.cs` acrescenta um cenário local de resposta fora de ordem que valida o cliente HTTP real, vagas renovadas com um upload anterior pendente, reserva única da vaga e proteção contra headers atrasados. O comando manual `--live-upload-cadence` fica fora da lista automática: lê somente as configurações necessárias pelo SQLite em modo read-only, cria uma origem sintética e um post temporário, utiliza os services de upload/criptografia, amostra o avanço durante a transmissão e remove somente o post criado. Requer autorização humana para cada sessão de teste. Não imprime token, proxy secreto, IDs privados, conteúdo de resposta ou metadata. Não abre a janela do app.

O diagnóstico distingue `bodyEnd` de `reply` e separa espera inicial de criação do post de lacunas durante o envio. `mainIdle` mede o maior intervalo entre escritas HTTP após iniciar o upload das mensagens; a espera final depois de transferir todos os bytes não entra nesse intervalo. Esses valores são medidos no stream HTTP do cliente, não equivalem a garantia de vazão contínua da placa de rede.

Os testes de comportamento estão em `tests/Organessonium.NetworkTests/MultiAttachmentTests.cs` e na integração Discord de `SyncIntegrationTests.cs`. Exercitam o uploader, cliente HTTP, criptografia real, metadata cifrado, checkpoints em disco, SQLite, download e vídeo com respostas HTTP simuladas. Não abrem interface nem acessam a conta do usuário.

Cobertura relevante: multipart 1/2/10 e rejeição local de 0/11; soma acima de 25 MiB; 26 chunks interrompidos/retomados; resposta fora de ordem; reconstrução byte a byte; array novo/legado; preview 480p com 13 chunks; mensagens deduplicadas; attachment ausente; cancelamento/limpeza; retries de rede/429; redução de batch; checkpoint unitário antigo; streaming, seek e renovação com filenames iguais; sincronização com três chunks, sendo dois na mesma mensagem.

O teste manual do pipeline de dez anexos utilizou origem sintética de 2 GiB e até onze mensagens concorrentes após o post inicial. A transmissão terminou em torno de 92,62 s; a última confirmação chegou em 127,31 s. A maior lacuna entre escritas HTTP durante o envio foi 0,06 s; pico de memória privada do Job Object: 283,5 MiB; nenhum HTTP 429. Uma amostragem da placa física durante a transmissão mediu aproximadamente 200 a 212 Mbps; esses contadores incluem todo o tráfego do adaptador. Dois posts temporários da comparação foram removidos, sem alterar a biblioteca e sem abrir janelas. Há espera inicial de cerca de quatro segundos para obter o thread e uma cauda de confirmação depois de transmitir todos os bytes. Os números são observados nessa conexão, não garantia para outra rede/horário nem teste de internet real de 1 Gbps.

`UploadBudgetTests.cs` cobre upload real de faixas de uma origem de 1 GiB com criptografia e transporte HTTP simulado, bytes durante o request, chunks confirmados, teto do Job Object e ausência de ciphertext em disco. Outro cenário mantém uma requisição ativa e verifica que o sucessor já está pronto, que a fila não lê além do limite e que o cancelamento zera ambos os conjuntos de buffers. A alteração de workers é exercitada no upload em andamento com storage lento simulado e heartbeat do Dispatcher. A WebView2 real carrega os assets do app em um host nunca visível, validando botão, velocidade, estimativa e descarte de updates atrasados, sem abrir a janela principal.

Na verificação atual, as 12 famílias selecionadas de upload, retomada/download, streaming, limites e publicação/recuperação do upload sincronizado passaram. `StreamingUploadTests.cs` transmite três mensagens completas de 200 MiB com apenas um worker de corpo enquanto todas as respostas ficam retidas, validando a separação das confirmações. Também verifica retries byte a byte e rejeição de uma origem alterada, sem ciphertext em disco. O teste local de preparação/cifra/cópia para receptor simulado mediu 567,8 MiB/s; não representa vazão da internet. A fixture de origem de 1 GiB mediu pico de 117,0 MiB. A solução desktop compilou sem warnings/erros; a compilação limpa do projeto de testes conserva dois CS0436 conhecidos do Windows App SDK, sem erros. Somente o diagnóstico manual autorizado acessa a conta; os testes automáticos usam HTTP simulado.

Arquivos da implementação:

- `Models/ChunkReference.cs`, `AppEntry.cs`, `MetadataModels.cs`, `TransferState.cs`.
- `Services/ForumUploader.cs`, `ForumUploader.MultiAttachment.cs`, `DiscordApiClient.cs`, `DiscordLimits.cs`, `UploadMemoryPipeline.cs`.
- `Services/ForumDownloader.cs`, `DiscordVideoChunkProvider.cs`.
- `Services/MetadataService.cs`, `SettingsStore.cs`, `AppRegistry.cs`, `AppRegistry.Trash.cs`, `SerialIndexService.cs`, `DebugNamesScanner.cs`.
- `Services/AppMemoryBudget.cs`, `RamUploadBudget.cs`, `UploadByteProgress.cs`, `UploadWireProgressStream.cs`, `UploadConcurrencyRegistry.cs`.
- `Services/UploadPartContent.cs`, `UploadBodySlot.cs` e a regeneração validada em `EncryptionService.cs`.
- `Services/TransferManager.cs`, `TransferStateRepository.cs`, `Sync/CloudSyncRepository.cs`, `Bridge/AppController.cs`.
- `tests/Organessonium.NetworkTests/Program.cs`, `UploadBudgetTests.cs`, `MultiAttachmentTests.cs`, `SyncIntegrationTests.cs`, `TrashTests.cs`.
- `tests/Organessonium.NetworkTests/StreamingUploadTests.cs` e `UploadCadenceDiagnostics.cs`.
- `README.md`, `migration.md`, este `implementation.md` e `release/Build-Releases.ps1`.

Os caminhos `Models`, `Services` e `Bridge` são relativos a `Organessonium/Organessonium`. O arquivo arquivado da criptografia não recebeu edição; o ZIP de fontes passa a incluir as duas especificações.
