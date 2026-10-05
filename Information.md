# Organessônium: contratos de dados e sincronização

Este arquivo documenta os contratos usados pelo desktop C# WPF + WebView2. O panorama da aplicação está em `README.md`; este documento detalha metadados, transferências e sincronização persistente entre computadores.

## Projeto C# atual

O projeto desktop fica em:

```text
Organessonium/
  Organessonium/
    Organessonium.csproj
    MainWindow.xaml
    MainWindow.xaml.cs
    Bridge/
    Models/
    Services/
    web/
```

A UI principal fica em `web/` e roda dentro do WebView2. A lógica de arquivos, Discord, metadados, chunks, cache e transferências fica em C#.

Arquivos importantes:

- `Models/MetadataModels.cs`: models públicos de metadata.
- `Models/AppEntry.cs`: DTO interno usado pela UI e pelo cache local.
- `Models/TransferState.cs`: estado persistente de uploads/downloads.
- `Services/MetadataService.cs`: geração, validação, leitura e envio de metadata.
- `Services/AppRegistry.cs`: mappings em `names`, índices hierárquicos em `folders`, seriais e cache local.
- `Services/ForumUploader.cs`: upload de chunks, previews e threads no Discord.
- `Services/ForumDownloader.cs`: download e montagem de chunks.
- `Services/TransferStateRepository.cs`: leitura/escrita atômica dos estados de transferência.
- `Services/TransferManager.cs`: criação, atualização, conclusão, falha e cancelamento de transferências.
- `Services/SettingsStore.cs`: SQLite, pastas locais e configurações.
- `Bridge/AppController.cs`: ponte entre WebView2 e serviços C#.
- `web/bridge.js`: métodos JS expostos para chamar C#.
- `web/app.js`: interface.

## Pastas locais

No Windows, os dados ficam em:

```text
%AppData%/
  OrganessoniumData/
    organessonium.db
    Logs/
    Previews/
    Serials/
    SyncStaging/
    SyncDownloads/
    Transfers/
      active/
      completed/
      failed/
      tmp/
    WebView/
```

Uso atual:

- `organessonium.db`: configurações e cache leve da listagem.
- `Logs/`: erros inesperados e resumos limitados de eventos importantes da sincronização. Eventos de debug não são persistidos.
- `Previews/`: cache local de thumbnails e previews em tela cheia.
- `Transfers/active/`: estados de upload/download ainda não concluídos.
- `Transfers/completed/`: histórico opcional de transferências concluídas.
- `Transfers/failed/`: transferências canceladas/falhas mantidas para diagnóstico.
- `Transfers/tmp/`: chunks temporários de download e arquivos auxiliares de transferência.
- `Serials/`: índices OSER persistentes das pastas e abas especiais.
- `SyncStaging/`: snapshots estáveis para upload retomável de pastas vinculadas.
- `SyncDownloads/`: chunks e arquivos reconstruídos para download retomável do sincronizador.
- `WebView/`: dados da WebView2.

A identidade da instalação fica fora dessa árvore, em `%LOCALAPPDATA%\Organessonium\installation-id.txt`. Sair do bot apaga configurações, vínculos, checkpoints e caches em `OrganessoniumData`, preservando `WebView`, `Logs` e a identidade da instalação. Não apaga as pastas locais vinculadas nem os objetos remotos.

## Canais do Discord

O app usa:

- `names`: canal de texto para mappings `folderId -> threadId`, incluindo a referência do serial. A leitura ainda aceita metadados legados referenciados por seriais.
- `folders`: fórum de índices. Cada thread contém cabeçalho e metadados dos filhos diretos; a raiz canônica de sincronização também contém o diário de revisões.
- `files`: fórum onde ficam threads de payload, chunks, thumbnails e previews.
- `serial`: canal de texto com envelopes `folderSerial` e anexos OSER `.bin`.

Arquivos originais e previews nunca são enviados ao `names` ou ao `folders`. Novos metadados de arquivo/pasta vão para o índice do pai no `folders`, não para `names`.

## Metadata

Metadata é o registro público que descreve um arquivo ou pasta. Ela não é estado temporário de transferência.

O serviço responsável é:

```text
Services/MetadataService.cs
```

Ele faz:

- cria metadata de arquivo e pasta;
- valida `schemaVersion` e `entryType`;
- serializa JSON inline minificado;
- serializa `metadata.json` formatado;
- decide automaticamente entre inline e anexo;
- lê primeiro JSON inline;
- baixa `metadata.json` somente quando o conteúdo da mensagem não é JSON válido;
- publica metadados no thread de índice solicitado e mappings no canal `names`.

`AppRegistry` usa o `MetadataService`, mas não monta JSON manualmente.

## JSON inline e metadata.json

Ao salvar metadata:

1. O app gera o objeto de metadata.
2. Serializa como JSON minificado.
3. Mede o tamanho da string.
4. Se tiver até `DiscordLimits.MessageContentCharacters`, envia no conteúdo da mensagem do índice correspondente.
5. Se passar de `2000` caracteres, envia como anexo `metadata.json`.

Mensagem inline:

- JSON puro.
- Sem `APP|`.
- Sem `FILE_META|`.
- Sem `ORGANESSONIUM_METADATA|`.
- Sem texto antes ou depois.
- Sem quebra de linha.
- Sem indentação.

Anexo:

- Nome obrigatório: `metadata.json`.
- JSON formatado com indentação.
- Conteúdo da mensagem fica vazio quando a API aceita.

Leitura:

1. Tenta parsear `message.content` como JSON.
2. Se tiver `schemaVersion = 2` e `entryType`, usa como metadata.
3. Se não for JSON válido, procura anexo `metadata.json`.
4. Baixa o anexo só nesse caso.
5. Mensagens inválidas são ignoradas sem travar a listagem.

## Limpeza do JSON de metadata

O metadata publico deve conter apenas campos que se aplicam ao tipo de entrada e que tenham informacao real.

Regras atuais:

- Campo presente significa que o recurso existe.
- Campo ausente significa que o recurso nao existe ou nao se aplica.
- Nao salvar `null`, exceto quando uma versao antiga ja tiver sido publicada; metadata novo deve preferir omitir o campo.
- Nao salvar arrays vazios.
- Nao salvar objetos vazios.
- Nao salvar `enabled = false`.
- Nao salvar `video480p` em imagens.
- Nao salvar `video480p` em videos quando a versao 480p nao foi gerada.
- Nao salvar `preview` se nao houver thumbnail nem preview de video.
- Nao salvar campos especificos de arquivo em pastas.
- Nao salvar campos temporarios de UI ou transferencia no metadata publico.

O serializer de metadata ignora campos nulos. Arrays e objetos opcionais devem ser deixados como `null` no model quando nao tiverem conteudo real, porque o `System.Text.Json` nao omite lista vazia automaticamente.
## Unicode no JSON

O C# usa `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` em `JsonOptions`.

Isso mantém nomes legíveis no Discord:

```json
{"name":"Lembranças IF"}
```

Em vez de:

```json
{"name":"Lembran\u00E7as IF"}
```

Outras versões devem configurar o serializador para não escapar acentos comuns quando isso for seguro.

## Schema de arquivo

Schema atual: `schemaVersion = 2`.

Formato logico de imagem com thumbnail:

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
  "chunks": [
    "123456789012345678"
  ],
  "preview": {
    "thumbnailMessageId": "123456789012345679"
  }
}
```

Formato logico de video sem preview 480p:

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
  "chunks": [
    "messageIdChunk1",
    "messageIdChunk2"
  ],
  "preview": {
    "thumbnailMessageId": "thumbnailMessageId"
  }
}
```

Formato logico de video com preview 480p:

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
  "chunks": [
    "messageIdChunk1",
    "messageIdChunk2"
  ],
  "preview": {
    "thumbnailMessageId": "thumbnailMessageId",
    "video480p": {
      "sizeBytes": 1234567,
      "chunks": [
        "previewChunk1",
        "previewChunk2"
      ]
    }
  }
}
```

Regras:

- `entryType` identifica arquivo ou pasta.
- `fileId` e o identificador estavel do arquivo.
- `threadId` aponta para a thread do Discord onde estao os dados.
- `chunks` contem somente chunks do arquivo original e nunca deve ser vazio.
- `preview.thumbnailMessageId` aponta para a thumbnail, quando ela existe.
- `preview.video480p` so existe quando a versao 480p foi gerada de verdade.
- `preview.video480p.sizeBytes` deve ser maior que zero.
- `preview.video480p.chunks` contem somente chunks reais da versao 480p e nunca deve ser vazio.
- Arquivo original e preview 480p nunca compartilham a mesma lista de chunks.
- `enabled` nao existe no schema publico.
- `chunkCount` nao e salvo no metadata publico; e derivado de `chunks.length`.
- `folderColor` nao pertence a arquivo.
- `path` nao e identificador principal; a hierarquia vem de `parentFolderId`.
- `parentFolderId` so deve aparecer quando o arquivo estiver dentro de uma pasta. A ausencia representa raiz em metadata novo; a leitura ainda aceita `null` antigo como raiz.
## Schema de pasta

Formato logico:

```json
{
  "schemaVersion": 2,
  "entryType": "folder",
  "folderId": "id-estavel",
  "name": "Fotos",
  "createdAt": "2026-06-30T17:01:00Z",
  "parentFolderId": "root",
  "folderColor": "#8AB4F8"
}
```

Regras:

- `entryType` deve ser `folder`.
- `folderId` e o identificador estavel da pasta.
- `parentFolderId = "root"` representa raiz para metadata nova de pasta.
- A leitura ainda aceita `null` ou campo ausente como raiz para dados experimentais ja gerados.
- `threadId` so aparece se a pasta realmente possuir um ID remoto proprio; pastas normais nao precisam dele.
- `folderColor` pertence so a pasta.
- Pasta nao tem chunks, preview, mimeType ou sizeBytes.
## Upload

Fluxo implementado:

1. `AppController.UploadSingleAsync` cria o estado persistente antes do envio.
2. O orçamento de cada parte vem de `DiscordLimits.PlaintextChunkBytes`: 20 MiB por anexo menos o envelope criptográfico. Partes são preparadas sob demanda com buffers limitados ou temporários em disco.
3. `TransferManager` salva o estado em `Transfers/active/<transferId>.json`.
4. `ForumUploader.UploadResumableAsync` cria a thread ou reaproveita uma thread já salva no estado.
5. Cada chunk enviado gera atualização imediata no JSON de transferência.
6. Thumbnail é enviada separadamente e registrada no estado.
7. Preview 480p é enviado separadamente e registrado no estado.
8. Preview 480p também é dividido em chunks quando passa do limite.
9. No fim, `MetadataService` publica os metadados no thread de índice da pasta e o serial afetado é atualizado.
10. A thread é arquivada para evitar limite de threads ativas.
11. A transferência vira `completed` ou sai de `active`, conforme configuração de histórico.

O upload retomável usa:

- `threadId` salvo;
- `batchId` salvo;
- `uploadedChunks[index].messageId`;
- `pendingChunks`.

Ao retomar, o app não reenvia chunks já registrados no estado.

## Download

Fluxo implementado:

1. `AppController.StartDownloadAsync` cria estado persistente antes de baixar.
2. `ForumDownloader.DownloadAndAssembleAsync(AppEntry, TransferState, TransferManager, ...)` baixa chunks por `metadata.chunks`.
3. Cada chunk vai para `Transfers/tmp/<transferId>/chunk_000000.part`.
4. Cada chunk concluído atualiza o JSON da transferência.
5. Se o app fechar, chunks já baixados continuam marcados.
6. Ao retomar, o app pula chunks temporários já baixados.
7. O arquivo final só é montado quando todos os chunks existem.
9. Ao concluir, temporários são removidos.

Downloads de pasta criam um estado por arquivo, não um estado gigante para a pasta inteira.

## Estados de transferência

Arquivo de estado:

```text
OrganessoniumData/Transfers/active/<transferId>.json
```

Estados possíveis:

- `queued`
- `preparing`
- `uploading`
- `downloading`
- `paused`
- `completed`
- `failed`
- `cancelled`
- `recovering`

O JSON de transferência contém somente estado local e leve:

- IDs;
- caminhos locais;
- progresso;
- chunks já enviados/baixados;
- erro;
- diagnóstico.

Ele não contém blobs, base64, arquivo original, preview ou thumbnail.

## Escrita segura do estado

`TransferStateRepository` escreve estado de forma atômica:

1. Serializa para `<arquivo>.tmp`.
2. Faz flush.
3. Remove o arquivo antigo.
4. Move o `.tmp` para `.json`.
5. Usa lock por `transferId`.

Isso evita `nomedoarquivo.json` parcialmente escrito e evita colisão por nome repetido. O nome físico usa `transferId`, não o nome do arquivo.

## Recuperação ao abrir

Depois de conectar, `AppController.NotifyInterruptedTransfersAsync` verifica `Transfers/active`.

Se houver estados recuperáveis:

- se `autoResumeTransfers = true`, o app chama a retomada automaticamente;
- caso contrário, a UI mostra um aviso para retomar.

O bridge expõe:

- `resumeTransfers`
- `failInterruptedTransfers`

O callback JS atual é:

- `_onInterruptedTransfers(count)`

Hoje a UI mostra confirmação para retomar todas. A ação de marcar como falha já existe no backend, mas ainda não tem botão dedicado na interface.

Checkpoints com `syncFolderId` pertencem ao `SyncBackgroundHost`, não à fila manual. O controller não oferece esses uploads para retomada manual nem os trata como posts órfãos comuns.

## Cancelamento

Transferências ativas continuam canceláveis pelo painel.

Ao cancelar:

- o token de cancelamento é acionado;
- upload cancela e remove a thread parcial quando possível;
- download marca o estado como `cancelled`;
- se `cleanupTempOnCancel = true`, temporários locais são removidos;
- o estado vai para histórico ou é removido conforme `keepTransferHistory`.

## Configurações de transferência

O modelo `AppSettings` possui:

- `autoResumeTransfers`: desligado por padrão.
- `keepTransferHistory`: desligado por padrão.
- `cleanupTempOnCancel`: ligado por padrão.
- `maxParallelUploads`: padrão 1.
- `maxParallelDownloads`: padrão 4.

Essas configurações já são salvas no SQLite. A tela de configurações ainda não expõe todos esses controles.

## Limitações conhecidas

- A tela de recuperação ainda é simples: permite retomar todas pelo aviso, mas não escolher individualmente.
- `failInterruptedTransfers` existe no bridge/backend, mas ainda não tem botão dedicado.
- O upload normal divide `UploadThreads` entre os arquivos concorrentes do lote, respeitando `MaxParallelUploads`. O sincronizador processa um arquivo por vez e paraleliza seus chunks; não cria uma tarefa por arquivo da árvore.
- Metadata maior que o limite de anexo ainda não tem armazenamento multipart de metadata.
- O cache SQLite local ainda mantém nomes internos antigos em algumas colunas, como `names_message_id` e `chunk_count`; isso é cache interno, não metadata pública.
- O fallback de download por thread antiga ainda existe em alguns pontos para não quebrar operações internas, mas versões novas devem usar `metadata.chunks`.

## Como outras versões devem implementar

Para listar:

1. Ler mappings de `names` ou usar o serial válido da pasta.
2. Abrir o índice do pai em `folders` quando for necessário reconciliar ou obter os detalhes completos.
3. Tentar JSON inline primeiro; usar `metadata.json` quando inline não for válido.
4. Validar `schemaVersion` e `entryType`, preservando campos opcionais conhecidos.
5. Não interpretar `syncRevision` ou `syncApplied` como arquivos da listagem.

Para upload:

1. Criar estado local antes de enviar.
2. Enviar chunks para thread em `files`.
3. Salvar cada chunk enviado.
4. Enviar thumbnail e preview 480p separados.
5. Gerar metadata final.
6. Publicar metadados no índice do pai e atualizar o serial dessa pasta.
7. Marcar estado como concluído.

Para download:

1. Criar estado local antes de baixar.
2. Baixar `metadata.chunks`.
3. Salvar cada chunk temporário.
4. Retomar pulando chunks já baixados.
5. Montar arquivo final.

Para compatibilidade:

- Use `fileId` e `folderId` como IDs estáveis.
- Use `parentFolderId` para hierarquia.
- Use `threadId` como localização Discord.
- Use `metadataMessageId` somente como informação local para editar/apagar a mensagem de metadata.
- Não misture estado local de transferência com metadata pública.

## Sincronização persistente entre computadores

### Componentes e fluxo

| Arquivo ou grupo | Responsabilidade |
| --- | --- |
| `Models/SyncModels.cs` | Extensões `sync`/`syncDevice`, vínculo local, base conhecida, revisões e decisões. |
| `Services/Sync/SyncStateStore.cs` | Identidade da instalação, SQLite incremental e isolamento por canais. |
| `Services/Sync/SyncScanner.cs` (inclui `SyncPaths`) | Contenção de caminhos, exclusões, scan, assinatura local e SHA-256. |
| `Services/Sync/FolderWatcher.cs` | Eventos de filesystem, caminhos afetados e debounce. |
| `Services/Sync/SyncPlanner.cs`, `SyncEngine.cs` | Comparação local/base/remoto, decisões, renomeações, tombstones e troca segura. |
| `Services/Sync/CloudSyncRepository.cs` | Diário remoto e reutilização de upload, download, criptografia e checkpoints. |
| `Services/Sync/SyncBackgroundHost.cs` | Worker, fila limitada, reconciliação periódica, retry, pausa e vínculo. |
| `Services/AppRegistry.Sync.cs` | Árvores de computadores, aliases, publicação de metadados comuns e seriais. |
| `Bridge/AppController.Sync.cs`, `web/sync.js` | Seletor, formulários, decisões, estados e progresso. Não executam a comparação em JavaScript. |
| `App.Background.cs`, `MainWindow.Sync.cs` | Janela descartável, execução sem interface, cancelamento e encerramento. |
| `TrayManager.cs`, `SingleInstanceHost.cs`, `StartupManager.cs` em `Services/Sync` | Área de notificação, instância única e inicialização opt-in. |
| `release/Build-Releases.ps1` | Declaração da StartupTask no manifesto MSIX. |

Fluxo: watcher ou timer enfileira a pasta; o scanner calcula as mudanças físicas; o repositório lê as revisões; o planner compara com a base persistida; o engine transfere ou solicita decisão. A publicação atualiza o índice comum e seus seriais, e o evento `TreeChanged` entrega os filhos atualizados ao controller quando existe interface. Não há DOM/WebView2 no worker nem um segundo backend de blobs.

### Identificadores e estado local

`deviceId` é um UUID persistido por instalação, separado de `deviceName = Environment.MachineName`. O `syncFolderId` é um UUID da pasta lógica, independente do caminho físico e do computador que a criou. IDs técnicos publicados usam UUIDs sem hífens.

| Identificador | Significado |
| --- | --- |
| `pc-<deviceId>` | Raiz do computador sob `synchronized`, com `deviceType: "pc"` e `syncDevice`. |
| `sync-<syncFolderId>` | `folderId` canônico da pasta lógica. |
| `sync-link-<deviceId>-<syncFolderId>` | Entrada no computador que vinculou uma pasta existente; `sync.canonicalFolderId` aponta para a árvore original. |
| `sd-<hash>` | ID estável de subpasta, derivado de `syncFolderId` e caminho relativo normalizado. |
| `fileId` | Identidade do arquivo, preservada quando o conteúdo é movido/renomeado de forma reconhecida. |
| `threadId` no payload | Post em `files` que contém a versão binária. Não é o thread do índice. |
| `revisionId`, `parents` | Identidade e ancestralidade do commit. A revisão numérica não resolve concorrência. |

`sync_folders` guarda nome, caminho local, raiz remota e preferências por `AccountKey`, um SHA-256 dos quatro canais configurados, sem incluir token. `sync_state` tem chave composta `(folder,path)` sem distinção de caixa, base/hash aceitos, assinatura local e revisão ignorada. `sync_issues` guarda conteúdo necessário para exibir/revalidar decisões por caminho. Gravações são por registro, não por reserialização de toda a árvore. As tabelas são criadas com `IF NOT EXISTS`; `app_entries` acrescenta `sync_json` e `sync_device_json` sem exigir os novos campos nos arquivos antigos.

Não copie `installation-id.txt` para outro computador. Backup do estado de um único dispositivo é diferente de criar uma nova instalação. Remover vínculo cancela a execução da pasta e apaga apenas suas linhas de estado local. Sair do bot suspende o host antes de limpar a conta.

### Metadados comuns e diário remoto

Arquivos comuns continuam com `schemaVersion: 2`, `entryType: "file"`, `fileId`, nome, MIME, `sizeBytes`, `threadId`, chunks, criptografia e preview. A extensão opcional é:

```json
{
  "sync": {
    "version": 1,
    "syncFolderId": "11111111111141118111111111111111",
    "relativePath": "src/App.cs",
    "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "modifiedUtc": "2026-10-04T15:00:00.0000000Z",
    "revision": 2,
    "revisionId": "22222222222242228222222222222222",
    "deviceId": "33333333333343338333333333333333",
    "deviceName": "PC-CASA",
    "state": "active"
  }
}
```

Os IDs e hash acima são exemplos fictícios. Raízes usam apenas `version`, `syncFolderId` e estado; aliases acrescentam `canonicalFolderId`. O computador tem `syncDevice: { version, deviceId, deviceName }`. Não preencher campos de arquivo com strings vazias na raiz. `sizeBytes` continua no metadata comum. Nada disso altera OSER v3 ou exige esses campos em galerias existentes.

O diário fica no thread de índice da raiz **canônica**, junto dos metadados de filhos diretos. Cada `syncRevision` contém:

- `schemaVersion: 2`, `entryType: "syncRevision"`, `syncVersion: 1`;
- `syncFolderId`, `fileId`, `relativePath`, `kind: "file" | "directory"`;
- `sizeBytes`, `hash`, `modifiedUtc`, `revision`, `revisionId`, `parents`;
- `deviceId`, `deviceName`, `state: "active" | "deleted"` e `deletedUtc` quando excluído;
- `payload` com `FileMetadata` completo para acessar os chunks da versão binária.

Datas são UTC, caminhos usam `/`, hashes são SHA-256 do arquivo em plaintext. Diretórios não têm payload. Tombstones podem conservar a referência da última versão válida; não entram no serial nem como cards na Lixeira.

JSON até o limite de texto é inline. Acima disso, a mensagem usa `syncRevision:<revisionId>` e anexo UTF-8 `sync-revision.json`, dentro do orçamento de metadata. Uma confirmação textual `syncApplied:<revisionId>` registra que a revisão também foi projetada nos índices comuns. Leitores de listagem ignoram esses dois tipos de mensagem. Campos desconhecidos não autorizam interpretar uma revisão como arquivo.

### Publicação e retomada

1. Capturar snapshot de upload com compartilhamento somente para leitura; confirmar tamanho, timestamp e SHA-256.
2. Enviar chunks criptografados e persistir as mensagens confirmadas no checkpoint existente, identificado por `syncFolderId`, `fileId` e `syncHash`.
3. Reconsultar as revisões esperadas, publicar nova revisão sem remover a anterior e verificar sua identidade/hash.
4. Publicar o `FileMetadata`/`FolderMetadata` navegável, atualizar caches/seriais e confirmar com `syncApplied`.
5. Somente depois concluir a transferência e limpar versões ancestrais obsoletas, preservando os heads e as bases imediatas.

Uma interrupção entre diário e índice é reparada na leitura seguinte se houver um único head; não ocorre novo upload do payload já confirmado. Dois commits concorrentes permanecem acessíveis como ramos até resolução. O Discord não oferece CAS transacional entre mensagens: a checagem dos heads reduz corridas, mas a preservação da ancestralidade é necessária mesmo depois dela.

Downloads retomáveis usam uma chave derivada dos IDs/hash em `SyncDownloads`. A montagem valida hash e tamanho antes de copiar para um temporário dentro da raiz local, evitando troca entre volumes. `File.Replace` preserva a versão anterior em `.organessonium-recovery`; o engine revalida o conteúdo local antes/depois da troca e restaura alterações detectadas durante a operação. Cancelamento ou download inválido não publica o temporário como arquivo final. Cache parcial de download abandonado e cópias de recuperação não expiram automaticamente.

Arquivos grandes usam streams e o orçamento existente de chunks, não `ReadAllBytes`. Vídeos mantêm seus bytes originais nesse fluxo, sem faststart/480p; thumbnails são uma tentativa limitada via Windows Shell, sem abrir WebView2 ou depender de FFmpeg.

### Conflitos, remoções e renomeações

A base aceita permite distinguir alterações de cada lado. Timestamp recente não vence um conflito. Uma atualização somente remota apresenta arquivo, dispositivo e datas local/remota; substituir pode memorizar automação apenas para aquela pasta. Manter local ignora a revisão oferecida sem apagar a versão remota. Uma revisão futura pode gerar nova pergunta.

Conflitos oferecem local, nuvem e ambas. Ambas conserva uma cópia com dispositivo/data no nome. Uma decisão pendente é persistida, mas só vale se hash local e IDs remotos continuam iguais aos apresentados. A opção de memorizar substituição remota não transforma conflitos em sobrescritas automáticas; a política alternativa de conflito é manter ambas.

As exclusões são revisões explícitas com `deletedUtc`. Diretório indisponível ou scan incompleto não produz exclusão. Arquivo remoto desaparecido sem tombstone gera indisponibilidade, não autorização para apagar. As políticas de exclusão são perguntar, propagar e ignorar; uma exclusão concorrente com edição local exige decisão. Remoções locais aceitas conservam a versão anterior; diretórios com conteúdo não confirmado não são apagados recursivamente.

Renomeação local é reconhecida quando um arquivo conhecido desaparece e aparece um único arquivo com o mesmo hash; publica nova localização reutilizando chunks e identidade. Renomeação remota preserva o `fileId` e respeita a configuração/decisão local. Identificações ambíguas não são adivinhadas. A Lixeira comum continua independente dos tombstones do sincronizador.

### Watcher, performance e funcionamento offline

O watcher observa nomes, diretórios, tamanho e última gravação, reúne caminhos e espera 2,5 segundos. Um evento força hash do caminho afetado mesmo quando sua assinatura parece igual. Ele não percorre toda a árvore por evento. Overflow e fila de eventos excessiva pedem scan completo. Timer remoto de um minuto e scan completo a cada cinco minutos compensam eventos perdidos; início, retomada, falha e ação manual também reconciliam.

Uma fila limitada coordena pastas, sem dezenas de tarefas por arquivo. O scan/hash ocorre fora da thread da janela; o transferidor reutiliza o limitador REST do bot e os limites de chunks. Arquivos em gravação há menos de dois segundos, bloqueados ou alterados durante a captura aguardam outra tentativa. Falhas usam backoff de até cinco minutos; notificações de tray são agregadas/deduplicadas, não emitidas por chunk. Pausas global e por pasta impedem novas transferências e cancelam com segurança as operações possíveis.

Exclusões internas: `Thumbs.db`, `desktop.ini`, `~$*`, `*.tmp` e arquivos `.organessonium-*`. `.git`, `bin`, `obj` e `node_modules` são incluídos. Links/junctions/reparse points são ignorados, inclusive em validações de destino. Caminhos absolutos, UNC, letras de drive, `..`, nomes reservados e saída da raiz são rejeitados.

Logs `Sync.*` registram somente tipo de decisão, contagens, estado e classe de erro/código HTTP, sem caminhos, nomes, tokens, conteúdo de arquivo ou corpo de exceção. Scans sem mudança e eventos individuais do watcher não geram logs de debug.

### Background, tray e Windows startup

`App` cria um host independente da janela. Modo normal abre o splash e a interface; `--background` cria apenas host e tray, sem `MainWindow`, DOM ou WebView2. Mutex/pipe por usuário coordenam abertura de uma janela já existente; não há segundo worker concorrente. Fechar para tray descarta controller/WebView2 e conserva o host; abrir recria a interface. Sair cancela/aguarda workers, descarta recursos e encerra. O cancelamento de saída não altera a preferência persistida de pausa.

Configurações possui opções separadas para inicialização e comportamento de fechar. Ambas são opt-in. MSI/execução sem identidade de pacote usa `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` com o caminho atual entre aspas e `--background`, sem alterar `StartupApproved`. MSIX declara `windows.startupTask`, `OrganessoniumSync`, `Enabled="false"` e `uap10:Parameters="--background"`; o app solicita habilitação pela API e respeita bloqueio do Windows.

Referências: [StartupTask desktop](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-startuptask), [extensão desktop e parâmetros](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-extension), [Run/RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys). A inscrição Run é limitada a 260 caracteres; uma instalação com caminho maior informa o impedimento. O app não altera o registro de startup apenas por iniciar o host.

### Validar entre dois computadores

1. Configurar os mesmos quatro canais e modo/chave de criptografia nos dois PCs. Não duplicar a identidade da instalação.
2. No PC A, abrir Dispositivos, adicionar uma pasta local de teste e aguardar seu estado sincronizado. Verificar a árvore preservada na nuvem.
3. No PC B, abrir o computador A, entrar na pasta e usar `Sincronizar esta pasta neste computador` com uma pasta vazia. Conferir conteúdo/hash local.
4. Editar no A. No B, verificar a pergunta com datas e dispositivo antes de aceitar. Memorizar a atualização e repetir uma edição para testar automação.
5. Editar o mesmo arquivo em ambos antes de sincronizar. Verificar conflito e testar manter ambas; nenhuma versão deve desaparecer sem decisão.
6. Remover um arquivo no A e verificar a política de exclusão no B. Repetir com alteração local no B para provocar conflito.
7. Renomear um arquivo grande no A e conferir a alteração de caminho no B sem novo conjunto de chunks.
8. Interromper a conexão durante transferência e retomar; o arquivo local anterior e a última versão remota devem continuar válidos.
9. Pausar/retomar, reiniciar o app e conferir persistência de vínculo/base/decisões. Arquivos criados durante pausa devem ser reconciliados.
10. Somente quando o computador estiver livre e com autorização para abrir a interface, testar tray, recriação/descarte do WebView2 e login do Windows em instalações MSI/MSIX. Não executar esses testes durante o uso normal do usuário.

Testes sem interface, com dados e HTTP isolados:

```powershell
dotnet build Organessonium/Organessonium.slnx -c Release --no-restore
dotnet run --project tests/Organessonium.NetworkTests -c Release -- twoComputerSyncLifecycle syncFailuresPreserveFiles syncScannerPathsAndPersistence syncDiscordPublicationAndRecovery
```

O executor não abre testes gráficos por padrão. Testes `webView*` e o cenário de tray/janela exigem `--include-ui`, que pode abrir janelas; não usar essa opção sem autorização do usuário. Compilação e os testes sem interface não comprovam login real do Windows nem a experiência visual final.

### Limitações e fronteira de compatibilidade

- O diário `syncRevision` é a fonte de mudanças entre PCs. Ações comuns da biblioteca e clientes que alteram somente metadados de arquivo/pasta não publicam automaticamente commits nesse diário. Faça edições com propagação no caminho local vinculado; não presumir que alterações por outro fluxo foram sincronizadas.
- Galerias e arquivos sem `sync` conservam seu formato e fluxo anteriores. Novos clientes podem navegar/baixar pelos metadados comuns sem implementar o worker; precisam preservar extensões opcionais ao editar e ignorar registros do diário como cards.
- Não há merge de arquivos arbitrários, eleição de dispositivo, serviço Windows, regras configuráveis de exclusão ou histórico ilimitado. A identidade perdida não é recuperada automaticamente.
- NTFS sem distinção de caixa é a referência de comparação. Árvores remotas com colisões de nomes/caminhos não são resolvidas por sobrescrita silenciosa. Reparse points não são suportados.
- O estado da opção de startup na tela é a preferência salva; o Windows pode bloqueá-la externamente e a próxima tentativa de habilitação deve respeitar essa decisão.
- Testes com duas instalações e Discord simulado não substituem dois PCs reais, backend real, pacote publicado, reboot/logon e medição de RAM/CPU durante horas. Nenhuma validação desse tipo deve ser declarada como realizada apenas porque o projeto compilou.
