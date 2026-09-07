# Organessonium - documentação técnica do projeto

Este arquivo documenta como a versão C# WPF + WebView2 do Organessonium está organizada hoje e como as outras versões do app devem implementar os mesmos fluxos para permanecerem compatíveis.

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
- `Services/AppRegistry.cs`: listagem do canal `names` e cache local.
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
    Transfers/
      active/
      completed/
      failed/
      tmp/
    WebView/
```

Uso atual:

- `organessonium.db`: configurações e cache leve da listagem.
- `Logs/`: logs de erros inesperados.
- `Previews/`: cache local de thumbnails e previews em tela cheia.
- `Transfers/active/`: estados de upload/download ainda não concluídos.
- `Transfers/completed/`: histórico opcional de transferências concluídas.
- `Transfers/failed/`: transferências canceladas/falhas mantidas para diagnóstico.
- `Transfers/tmp/`: chunks temporários de download e arquivos auxiliares de transferência.
- `WebView/`: dados próprios do WebView2.

## Canais do Discord

O app usa:

- `names`: canal de texto para metadata de arquivos e pastas.
- `files`: fórum/canal onde ficam threads, chunks, thumbnails e previews.

O canal `names` não deve receber arquivo original, preview, thumbnail, blob ou base64. Ele recebe apenas JSON inline ou `metadata.json`.

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
- envia e apaga a mensagem de metadata no canal `names`.

`AppRegistry` usa o `MetadataService`, mas não monta JSON manualmente.

## JSON inline e metadata.json

Ao salvar metadata:

1. O app gera o objeto de metadata.
2. Serializa como JSON minificado.
3. Mede o tamanho da string.
4. Se tiver até `2000` caracteres, envia no conteúdo da mensagem do canal `names`.
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
  "threadId": "1521609754625970269",
  "name": "1745186438334.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 317584,
  "uploadedAt": "2026-06-30T20:13:00Z",
  "batchId": "ca433d8f1b",
  "chunks": [
    "1521609754625970269"
  ],
  "preview": {
    "thumbnailMessageId": "1521609757788602448"
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
2. `ArchiveSplitter` divide o arquivo em partes de até 10 MB.
3. `TransferManager` salva o estado em `Transfers/active/<transferId>.json`.
4. `ForumUploader.UploadResumableAsync` cria a thread ou reaproveita uma thread já salva no estado.
5. Cada chunk enviado gera atualização imediata no JSON de transferência.
6. Thumbnail é enviada separadamente e registrada no estado.
7. Preview 480p é enviado separadamente e registrado no estado.
8. Preview 480p também é dividido em chunks quando passa do limite.
9. No fim, `MetadataService` publica a metadata no `names`.
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
- `maxParallelUploads` e `maxParallelDownloads` estão salvos nas configurações, mas o fluxo atual ainda executa a fila principal sequencialmente.
- Metadata maior que o limite de anexo ainda não tem armazenamento multipart de metadata.
- O cache SQLite local ainda mantém nomes internos antigos em algumas colunas, como `names_message_id` e `chunk_count`; isso é cache interno, não metadata pública.
- O fallback de download por thread antiga ainda existe em alguns pontos para não quebrar operações internas, mas versões novas devem usar `metadata.chunks`.

## Como outras versões devem implementar

Para listar:

1. Ler mensagens do `names`.
2. Tentar JSON inline primeiro.
3. Usar `metadata.json` só se inline não for metadata válido.
4. Validar `schemaVersion` e `entryType`.
5. Ignorar formatos antigos.

Para upload:

1. Criar estado local antes de enviar.
2. Enviar chunks para thread em `files`.
3. Salvar cada chunk enviado.
4. Enviar thumbnail e preview 480p separados.
5. Gerar metadata final.
6. Publicar metadata no `names`.
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
