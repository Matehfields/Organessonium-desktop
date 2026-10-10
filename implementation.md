# Versões de arquivos e ações de arquivo

Este documento especifica o histórico de versões implementado no desktop e o contrato necessário para clientes que leem e escrevem os mesmos dados. Não é uma especificação de desempenho do upload. O transporte continua descrito em [implementation old 2.md](<implementation old 2.md>) e a criptografia em [implementation old.md](<implementation old.md>).

## 1. Objetivo e responsabilidades

Ao enviar um arquivo com nome e pasta iguais aos de um arquivo existente, escolher **Substituir** publica um novo conteúdo sem apagar o anterior. O desktop envia as partes novas para o mesmo post do fórum `files`, com um novo `batchId`, salt e referências de attachments. O `fileId` continua identificando o mesmo arquivo lógico.

A mensagem do metadata permanece no índice da pasta. O histórico faz parte desse mesmo metadata, protegido integralmente pelo envelope criptográfico já existente. Não são publicados nomes ou listas de versões em JSON legível no Discord.

Componentes:

- `Models/MetadataModels.cs`: `FileMetadata`, `FileVersion` e checkpoint `FileReplacement`.
- `Models/AppEntry.cs`: estado operacional e campos enviados à interface.
- `Services/FileVersioning.cs`: snapshot, preservação do conteúdo anterior e cálculo do espaço utilizado.
- `Services/MetadataService.cs`: leitura, validação e escrita cifrada do metadata completo.
- `Bridge/AppController.Files.cs`: informações, cópia, download histórico e publicação de substituição.
- `Bridge/AppController.cs`: conflito de nomes, upload, editor Markdown e retomada.
- `Services/SettingsStore.cs` e `SerialIndexService.cs`: cache persistente e reutilização segura dos detalhes.
- `web/file-details.js`: diálogos de informações e histórico.

## 2. Identidade e localização

| Campo | Significado |
| --- | --- |
| `fileId` | Identidade lógica estável, inclusive na derivação da chave do conteúdo. |
| `threadId` | Post que contém o payload atual. Substituir por upload reutiliza esse post. |
| `metadataMessageId` | ID da mensagem de metadata no índice do pai; pertence ao estado local/serial, não ao JSON interno do arquivo. |
| `parentFolderId` | Pasta atual, incluindo raízes técnicas quando aplicável. |
| `path` | Localização calculada para a interface, como `/` ou `/Fotos`; não substitui `parentFolderId`. |
| `versionId` | Identificador opaco e estável de uma versão de conteúdo. Não é a versão do algoritmo criptográfico. |
| `batchId` | Lote de payloads de uma versão. Cada conteúdo novo recebe outro lote. |

IDs do Discord são strings, nunca números de ponto flutuante. `chunkRefs.index` é inteiro zero-based. A ordem lógica deve ser definida por esse índice, e não pela ordem em que anexos ou mensagens chegam.

Renomear, mover e favoritar não alteram `fileId`, `versionId`, salt ou referências do conteúdo. Mover pode mudar o ID da mensagem de metadata porque o índice de destino é outro thread.

## 3. JSON interno

O schema existente continua `schemaVersion: 2`, `entryType: "file"`. O contrato de versões é uma extensão aditiva desse schema. Todos os campos anteriores continuam válidos: `fileId`, `threadId`, `name`, `mimeType`, `sizeBytes`, `uploadedAt`, `batchId`, `parentFolderId`, `chunks` ou `chunkRefs`, `preview`, `encryption`, `fav`, `sync` e `trash`.

Campos novos:

- `versionId`: versão de conteúdo atual. Uploads desktop usam o UUID/identificador do checkpoint, sem separadores; uma retomada reutiliza esse valor.
- `versionUploadedAt`: instante de upload do conteúdo atual.
- `modifiedAt`: última alteração do arquivo, incluindo substituição, renomeação, movimento ou edição.
- `versions`: array de conteúdos anteriores, do mais antigo para o mais recente. A versão atual não entra nesse array.

`uploadedAt` passa a permanecer como a data inicial do arquivo lógico. Na primeira publicação, `uploadedAt`, `versionUploadedAt` e `modifiedAt` têm o mesmo valor. Ao substituir, somente os dois últimos mudam. Datas remotas são ISO 8601 UTC, normalmente `yyyy-MM-ddTHH:mm:ssZ`; checkpoints e `archivedAt` podem conter frações de segundo. O consumidor deve aceitar ISO 8601 com ou sem frações e apresentar no fuso local.

Cada elemento de `versions` contém:

```json
{
  "versionId": "identificador-opaco-da-versao-anterior",
  "archivedAt": "2026-10-10T15:00:00.000Z",
  "payload": {
    "schemaVersion": 2,
    "entryType": "file",
    "fileId": "identidade-logica-estavel",
    "threadId": "post-do-conteudo-anterior",
    "name": "Foto.jpg",
    "mimeType": "image/jpeg",
    "sizeBytes": 4096,
    "uploadedAt": "2026-10-01T12:00:00Z",
    "versionId": "identificador-opaco-da-versao-anterior",
    "versionUploadedAt": "2026-10-01T12:00:00Z",
    "modifiedAt": "2026-10-01T12:00:00Z",
    "batchId": "lote-anterior",
    "parentFolderId": "root",
    "chunkRefs": [
      { "index": 0, "messageId": "mensagem-anterior", "attachmentId": "anexo-anterior" }
    ],
    "encryption": { "version": 1, "mode": "standard", "salt": "AAAAAAAAAAAAAAAAAAAAAA" }
  }
}
```

Os identificadores e o salt acima são ilustrativos e não representam dados de uma conta. O salt fixo do exemplo nunca deve ser utilizado para upload real.

O `payload` é um snapshot completo e independente dos dados necessários para baixar aquele conteúdo. Deve conservar o MIME, nome, tamanho, lote, referências, `encryption` e `preview` daquele momento. Não possui `versions` aninhado. `trash`, `sync` e `fav` são retirados do snapshot pelo desktop, pois a organização corrente pertence ao arquivo atual, não a um attachment antigo. O consumidor não deve usar o `parentFolderId` histórico para mover ou listar o arquivo atual.

O `fileId` de cada snapshot deve ser igual ao do arquivo lógico. O desktop verifica isso, valida suas referências e não aceita IDs de versão ausentes/duplicados ou histórico aninhado. `payload.versionId` corresponde ao `versionId` externo.

Um metadata completo após substituição tem a estrutura:

```json
{
  "schemaVersion": 2,
  "entryType": "file",
  "fileId": "identidade-logica-estavel",
  "threadId": "post-existente",
  "name": "Foto.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 8192,
  "uploadedAt": "2026-10-01T12:00:00Z",
  "versionId": "nova-versao",
  "versionUploadedAt": "2026-10-10T15:00:00Z",
  "modifiedAt": "2026-10-10T15:00:00Z",
  "parentFolderId": "root",
  "batchId": "novo-lote",
  "chunkRefs": [
    { "index": 0, "messageId": "nova-mensagem", "attachmentId": "novo-anexo" }
  ],
  "encryption": { "version": 1, "mode": "standard", "salt": "AAECAwQFBgcICQoLDA0ODw" },
  "versions": [
    {
      "versionId": "versao-antiga",
      "archivedAt": "2026-10-10T15:00:00.000Z",
      "payload": {
        "schemaVersion": 2,
        "entryType": "file",
        "fileId": "identidade-logica-estavel",
        "threadId": "post-existente",
        "name": "Foto.jpg",
        "mimeType": "image/jpeg",
        "sizeBytes": 4096,
        "uploadedAt": "2026-10-01T12:00:00Z",
        "versionId": "versao-antiga",
        "versionUploadedAt": "2026-10-01T12:00:00Z",
        "batchId": "lote-antigo",
        "chunkRefs": [
          { "index": 0, "messageId": "mensagem-antiga", "attachmentId": "anexo-antigo" }
        ],
        "encryption": { "version": 1, "mode": "standard", "salt": "AAAAAAAAAAAAAAAAAAAAAA" }
      }
    }
  ]
}
```

JSON UTF-8 segue as regras existentes. A ordem das propriedades não afeta a leitura. Campos opcionais nulos/arrays vazios não são publicados. `versions` ausente significa histórico vazio; não significa falha. Arquivos antigos podem usar `chunks` sem `encryption`, e um snapshot desse arquivo deve preservar exatamente essa ausência.

## 4. Escrita e atomicidade

1. Resolver nome e pasta, e aguardar a escolha Substituir. Manter ambos continua sendo outro arquivo independente.
2. Ler o metadata completo, não apenas o serial. Conferir a identidade e guardar um snapshot no checkpoint local.
3. Reutilizar `fileId` e `threadId`; gerar outro `batchId`, outro salt de arquivo e nonces seguros para o conteúdo novo. Não reutilizar a chave de arquivo derivada com o salt anterior para simplesmente sobrescrever os blobs.
4. Desarquivar o post existente e enviar novas mensagens/attachments nele. Não editar as mensagens antigas de conteúdo.
5. Confirmar e validar todas as referências novas. Concluir as prévias novas antes de trocar o metadata.
6. Reler o metadata atual. Se outra versão já o substituiu, interromper a publicação e informar conflito, mantendo a versão existente. Mudanças de organização devem ser preservadas a partir dessa leitura.
7. Copiar o conteúdo atual para `versions`, sem histórico aninhado. Reutilizar o histórico já existente e evitar duplicação pelo `versionId`.
8. Gerar e validar o novo metadata cifrado completo. Fazer PATCH da mensagem existente, inclusive ao trocar inline por `metadata.json`, sem DELETE prévio da mensagem.
9. Após confirmação, atualizar `app_entries`, seriais local/remoto e interface. Reabrir o arquivo usa o novo conteúdo.
10. Invalidar thumbnails e cache de prévia vinculados ao thread, pois esse ID não mudou, mas o conteúdo mudou. Rearquivar o post ao finalizar.

O Discord não fornece uma transação envolvendo payload, metadata e serial, nem compare-and-swap do JSON de metadata. A releitura reduz conflitos, mas não elimina uma corrida entre clientes que publicam simultaneamente. Os chunks antigos nunca são apagados na substituição. Se o envio falhar, o metadata anterior permanece; chunks novos confirmados podem ficar sem publicação até retomada/recuperação. Cancelar uma substituição não pode excluir o thread compartilhado.

Se o PATCH foi confirmado e uma etapa posterior falhou, a retomada identifica o `versionId` já publicado e não adiciona o mesmo conteúdo duas vezes ao histórico. O checkpoint guarda `replacement: { metadataMessageId, path, metadata }`; o novo `versionId` é o `transferId`. Um cliente mobile pode escolher outro identificador, desde que ele seja opaco, único e persistente entre tentativas.

Quando dados de desenvolvimento já possuem vários arquivos com o mesmo nome/pasta, a substituição do desktop seleciona uma entrada existente e preserva as demais. Arquivos ainda não enviados do mesmo lote continuam obedecendo à escolha de conflito, sem criar versões de conteúdo que nunca chegou ao servidor.

## 5. Leitura e download

O arquivo atual continua nos campos superiores do metadata. Leitores que ignoram `versions` conseguem localizar o payload atual se já suportam o codec criptográfico e o transporte de attachments. Porém, esses leitores não são escritores seguros: recriar o JSON omitindo campos de versões destruiria o histórico.

Para baixar uma versão anterior, selecionar o elemento por `versionId` e usar exclusivamente seu `payload`: `threadId`, `fileId`, `batchId`, tamanho, referências e criptografia. Não usar o salt, contagem ou previews atuais para o conteúdo antigo. O download autentica cada payload com o contexto e total de chunks daquela versão.

Downloads novos guardam `downloadMetadata` no checkpoint. A retomada usa esse snapshot, mesmo que a versão atual mude enquanto o download está interrompido. Checkpoints anteriores sem esse campo continuam seguindo a recuperação antiga.

O array é exibido em ordem inversa na interface, com a versão atual separada. Há download do conteúdo histórico, sem ações de restauração/exclusão de versões individuais nesta implementação.

## 6. Datas, espaço e informações

O diálogo mostra categoria/MIME, localização calculada, tamanho atual, espaço utilizado, upload inicial, última modificação, thread, mensagem de metadata e quantidade lógica de chunks. `messageId` não é o ID de uma mensagem arbitrária de chunk.

`tamanho utilizado = sizeBytes atual + soma(versions[].payload.sizeBytes)`.

Essa medida é a soma dos conteúdos plaintext registrados, não uma medição dos bytes totais de attachments no Discord. Não inclui thumbnails, versões de vídeo de prévia, headers/tags, metadata, mensagens órfãs ou retries não referenciados. A quantidade de chunks também é lógica: uma mensagem com dez anexos pode representar dez chunks.

Metadados antigos sem `modifiedAt` ou `versionUploadedAt` usam `uploadedAt` como fallback operacional. Não é possível reconstruir datas de modificações que versões antigas nunca registraram. O editor Markdown registra conteúdo novo e preserva histórico ao salvar um documento existente.

A recuperação já existente do editor Markdown em HTTP 403/404 pode criar outro post quando o antigo não pode ser reutilizado. O snapshot conserva o `threadId` anterior e o desktop não apaga aquele post. Isso não é o fluxo de Substituir por upload, que exige o post existente. Conteúdo removido externamente do Discord não pode ser recuperado apenas pelo snapshot.

## 7. Cópia

Fazer cópia cria outro arquivo, na mesma pasta, com nome `Cópia de <nome>` e sufixo numérico em caso de conflito. Copia somente o conteúdo atual, não o histórico ou estado de favorito. O desktop baixa o conteúdo autenticado para um diretório operacional temporário e o envia como upload independente: outro `fileId`, post, salt e metadata. A criptografia das partes de upload continua em RAM; o diretório de reconstrução é removido ao finalizar.

Reutilizar apenas referências de attachments entre arquivos não é equivalente. A derivação criptográfica usa `fileId`, e excluir um post compartilhado destruiria outra entrada. O cliente mobile pode limitar concorrência e usar seu diretório temporário normal de download para implementar essa ação.

## 8. Cache e seriais

O SQLite recebe colunas aditivas `modified_at`, `version_id`, `version_uploaded_at` e `versions_json`, sem limpar o cache anterior. Esse cache permanece operacional/local e pode conter metadata descriptografado, como os demais campos já existentes.

O formato binário OSER v3 não foi alterado. Um serial não é um catálogo completo de versões. O menu lê o metadata completo para descobrir se Versões deve aparecer; o diálogo consulta os detalhes atuais. Ao materializar um serial, detalhes em cache são preservados quando o payload continua compatível. Mudança de salt, thread ou referência de thumbnail invalida detalhes antigos em vez de combinar chunks velhos com a chave nova.

Renomear, mover, favoritar e enviar/restaurar pela lixeira devem carregar/preservar o metadata inteiro. Não fazer uma gravação de negócios usando somente um card do serial.

## 9. Flutter interoperability

Compatibilidade significa produzir/consumir o mesmo protocolo e preservar o histórico; não significa reproduzir workers, buffers de RAM, Job Object ou filas do desktop.

Requisitos para a implementação mobile:

1. Acrescentar os campos novos aos modelos e persistência. Preservar `versions` integralmente em qualquer alteração do metadata, inclusive favorito, rename, move, lixeira e sincronização.
2. Tratar `versionId` como string opaca. Para dados antigos sem ID, gerar um ID estável ao arquivar e persistir antes de publicar; não é necessário reproduzir o prefixo/hash `legacy-` usado pelo desktop.
3. Criar snapshots sem `versions` aninhado, com o mesmo `fileId`, os valores de criptografia originais e todas as referências de conteúdo/prévia.
4. Substituir por upload no mesmo post, com salt/lote novos. Atualizar somente a mensagem de metadata após completar e validar o payload novo.
5. Continuar usando encrypted metadata V1 e ORGAESGC V1 conforme as especificações existentes. O envelope criptográfico não mudou por causa do histórico.
6. Suportar `chunkRefs` e `chunks` legados por versão. Somente `index` define a ordem dos chunks; IDs são strings.
7. Ler/validar a versão escolhida antes de iniciar download e persistir esse snapshot na retomada.
8. Atualizar o serial e invalidar caches de thumbnail/prévia depois da publicação. Não apagar o serial ao abrir uma aba.
9. Exibir as mesmas datas, localização e soma de espaço. Mostrar Versões apenas se houver histórico.
10. Usar concorrência e tamanho de lote adequados à memória mobile. Batches menores são compatíveis: o leitor resolve attachments por ID, não exige dez por mensagem. Nenhum snapshot exige baixar todas as versões para listar o histórico.

Pseudocódigo de substituição:

```text
base = readFullMetadata(file)
checkpoint = persist(base, stableNewVersionId, freshSalt)
newPayload = uploadInto(base.threadId, base.fileId, freshSalt, freshBatchId)
validateAllReferences(newPayload)
latest = readFullMetadata(file)
if latest.versionId == checkpoint.newVersionId: return latest
if contentIdentity(latest) != contentIdentity(base): reportConflict
history = copy(latest.versions)
history.append(snapshotWithoutNestedHistory(latest))
next = newPayload + currentOrganization(latest) + history
next.uploadedAt = latest.uploadedAt
next.versionUploadedAt = nowUtc
next.modifiedAt = nowUtc
patchEncryptedMetadata(existingMessageId, next)
updateCacheAndSerialAndUI(next)
```

O código Flutter não foi alterado nesta tarefa. Uma versão mobile que não preserva os campos novos deve ser atualizada antes de editar arquivos com histórico. O diário de sincronização entre PCs é outro contrato, com revisões e retenção independentes; não tratar seus IDs de revisão como IDs de versão de UI nem apagar posts referenciados por histórico ao realizar manutenção.

## 10. Verificação implementada

- `fileVersionsLifecycle`: usa controller, uploader, registry, metadata criptografado, cache SQLite e downloader reais, com somente HTTP simulado. Publica três conteúdos no mesmo post, baixa os três, verifica cópia independente, preservação após rename/favorito/move/reabertura e falha de commit sem apagar o conteúdo anterior.
- `fileActionsHiddenBrowser`: carrega a interface real em WebView2 hospedado em HWND invisível. Verifica ações/textos, informações, download histórico, glyphs Google, cinza por tema e limites do diálogo em viewport menor. Não abre janela em tela.

Esses testes não publicam dados na conta e não substituem validação posterior com dispositivos e clientes mobile reais. O teste de cópia valida o fluxo independente de payload; não automatiza o seletor nativo de destino do download.
