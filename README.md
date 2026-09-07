# Organessônium

Este repositório contém a versão desktop C# WPF + WebView2 do Organessônium. O aplicativo usa o Discord como armazenamento de payloads, o canal `names` como índice estrutural autoritativo e o canal `serial` como índice rápido por pasta.

## Arquitetura de dados

- `Bridge/AppController.cs` coordena upload, download, previews, transferências e a ponte WebView2.
- `Services/ForumUploader.cs` cria posts e mensagens com attachments no fórum de arquivos.
- `Services/ForumDownloader.cs` resolve as mensagens referenciadas pelo metadata, baixa as partes e monta o arquivo final na ordem dos índices.
- `Services/MetadataService.cs` publica e lê o metadata estrutural no canal `names`.
- `Services/SerialIndexService.cs` lê, valida, mantém em cache e publica o índice binário rápido por pasta no canal `serial`.
- `Services/FolderSerialCodec.cs` codifica e decodifica o formato binário versionado `OSER` usado pelos seriais.
- `Services/SettingsStore.cs` mantém configurações, o índice local `app_entries`, cache de previews e estados de transferência no diretório de dados.

Um arquivo é identificado por `fileId`; o metadata associa esse identificador ao `threadId` do fórum, aos IDs das mensagens dos chunks, ao nome e MIME originais, a `sizeBytes` em plaintext, ao `parentFolderId` e às referências de prévias. Pastas usam `folderId`; o `parentFolderId` estabelece a árvore. O metadata não é criptografado, pois é o índice necessário para localizar os blobs no Discord.

## Índice serial

O canal de texto `serial` contém uma mensagem do bot para cada pasta materializada. O conteúdo da mensagem é um envelope JSON compacto com `schema`, `entryType: "folderSerial"`, `folderId`, `folderName` opcional e o `sha256` do anexo. O anexo usa a extensão `.bin` e o formato binário `OSER` versionado; ele guarda os filhos diretos da pasta, seus nomes, IDs, relações, MIME, timestamps, referências de preview, cores e os totais agregados da pasta. O serial não substitui o metadata completo do arquivo: tamanho, chunks e demais dados necessários para transferir o arquivo continuam sendo lidos do canal `names` e preservados no cache local.

Na inicialização, o app lê primeiro os seriais válidos já armazenados em `OrganessoniumData\Serials` e tenta atualizar somente os hashes ausentes ou divergentes no canal `serial`. Esses resumos são enviados à interface imediatamente para acelerar a primeira renderização. Em paralelo limitado pelo número de `SearchWorkers`, os anexos novos são baixados, verificados por SHA-256 e decodificados; entradas completas previamente armazenadas no SQLite são usadas para reidratar os campos de transferência. Instalações antigas sem `serialChannelId` continuam usando o caminho autoritativo do canal `names` até que o canal seja configurado novamente.

Depois da varredura dos seriais, o canal `names` é lido por páginas em paralelo. O metadata de cada mensagem é desserializado em lotes usando os mesmos `SearchWorkers`, e os resultados são enviados progressivamente à interface. Ao final, essa leitura completa do `names` permanece autoritativa, atualiza `app_entries` e sincroniza apenas os seriais cujo conteúdo mudou. Mensagens de serial duplicadas ou hashes antigos são removidos durante a sincronização.

O progresso da leitura começa com o cache local ou com os seriais disponíveis e é atualizado conforme cada página do Discord chega, sem esperar o fim da varredura. A paginação usa os workers configurados sem um limitador global artificial; o tratamento de rate limit do cliente continua ativo por rota. Ao criar uma pasta ou excluir itens, o app trabalha sobre a lista local já exibida, atualiza SQLite e interface imediatamente e sincroniza o canal `serial` sem iniciar uma nova varredura completa do `names`. Enquanto essa operação termina, a interface exibe o aviso `Em andamento...`, que pode ser ocultado sem cancelar a operação.

## Metadata publica

A metadata publica fica no canal `names` do Discord. O app envia JSON inline minificado quando couber em uma mensagem; quando passar do limite de caracteres do Discord, envia um anexo `metadata.json` formatado.

Regra principal: a presenca de um campo significa que o recurso existe. A ausencia significa que o recurso nao existe ou nao se aplica.

O metadata novo nao deve incluir campos nulos, arrays vazios, objetos vazios, `enabled = false`, campos temporarios da UI, campos de video em imagens, nem campos de arquivo em pastas.

### Imagem com thumbnail

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
  "chunks": ["1521609754625970269"],
  "preview": {
    "thumbnailMessageId": "1521609757788602448"
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
  "chunks": ["chunk1", "chunk2"],
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
  "chunks": ["chunk1", "chunk2"],
  "preview": {
    "thumbnailMessageId": "thumbnailMessageId",
    "video480p": {
      "sizeBytes": 1234567,
      "chunks": ["previewChunk1", "previewChunk2"]
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

Todo novo attachment binário do Organessônium passa por `EncryptionService` antes de chegar ao Discord: arquivo original, chunks, miniaturas, prévias de imagem, `video480p` e imagens anexadas em Markdown. O metadata continua público e recebe somente o objeto mínimo `encryption`:

```json
"encryption": {
  "version": 1,
  "mode": "standard",
  "salt": "base64url-sem-padding-do-salt-por-arquivo"
}
```

O `salt` usa exclusivamente Base64URL sem padding, com os caracteres `A-Z`, `a-z`, `0-9`, `-` e `_`. Esse é o formato gravado nas mensagens do servidor e usado tanto na validação do índice quanto na derivação da chave.

O blob `ORGAESGC` v1 contém magic, versão, papel do payload, índice 1-based e quantidade total do chunk, tamanho plaintext, salt de 16 bytes, nonce GCM de 96 bits, ciphertext e tag de autenticação. O nonce é gerado por CSPRNG para cada payload. A chave por arquivo é derivada por HKDF-SHA-256 a partir da master key, do `fileId` normalizado e do salt do arquivo. O header completo é o AAD do AES-256-GCM, então trocar, truncar ou reordenar payloads invalida a autenticação.

Os attachments criptografados usam nomes técnicos, como `payload.bin`, `chunk-1.bin`, `preview.bin`, `video-preview-1.bin` e `image.bin`, sempre com `application/octet-stream`. A mensagem do Discord usa o formato `file:<fileId> role:<role> chunk:<parte>/<total> encryption:v1`. O nome e o MIME originais permanecem somente no metadata do Organessônium.

### Modos de chave

- **Padrão:** funciona automaticamente em instalações compatíveis. A master key é derivada internamente, nunca é exibida, enviada ao Discord ou colocada no metadata.
- **Chave personalizada:** a frase do usuário passa por PBKDF2-HMAC-SHA256 com salt aleatório e 600.000 iterações para produzir uma master key de 256 bits. O SQLite guarda apenas o material protegido pelo DPAPI do Windows para o usuário atual, nunca a frase ou a chave em plaintext.
- **Recuperação:** Configurações permite exportar e importar um arquivo `.organessonium-key` versionado. Esse arquivo é sensível e deve ser guardado fora do computador, pois permite recuperar arquivos protegidos pela chave personalizada.

Mudar o modo afeta somente uploads novos. Cada arquivo usa o modo registrado no próprio metadata, sem recriptografar conteúdo existente.

### Download, integridade e compatibilidade

Para payloads criptografados, cada tentativa baixa o ciphertext para um arquivo temporário identificado como tal, valida o envelope e a tag GCM, e só então grava o plaintext. Falhas de download, estrutura ou autenticação descartam os temporários suspeitos e fazem no máximo três downloads completos por payload. Após a terceira falha, o diálogo oferece `Tentar novamente`, iniciando uma nova sequência de três buscas. Após reconstruir o arquivo, o tamanho plaintext é comparado com `sizeBytes` do metadata.

Uma falha de autenticação nunca entrega plaintext. Em modo personalizado, a interface informa que a chave configurada pode estar incorreta. Quando a autenticação passa, mas apenas o tamanho do metadata diverge, o app preserva o arquivo validado e oferece `Abrir mesmo assim` em um diálogo nativo.

Entradas legadas sem `encryption` seguem exatamente o caminho anterior de download e continuam acessíveis. O cache de previews armazena somente plaintext: se encontrar um arquivo iniciado por `ORGAESGC`, o app o invalida e refaz o download descriptografado. Ciphertext e plaintext temporários não são reutilizados entre tentativas. A reprodução progressiva existente fica restrita a vídeos legados, pois payloads criptografados precisam ser autenticados antes de expor bytes ao player.

## Persistência local

As configurações e os índices locais ficam em `organessonium.db`, dentro da pasta de dados. A tabela `settings` usa `key`, `value` e `updated_at`; cada gravação atualiza o valor e o horário em UTC pelo SQLite. A tabela `app_entries` mantém também versão, modo e salt de criptografia para o índice local, sem armazenar qualquer chave. Os anexos seriais são mantidos em `OrganessoniumData\Serials`, nomeados por hash para evitar colisões e reutilização de conteúdo inválido. Na inicialização, o app cria a coluna `updated_at` quando encontra um banco legado e preenche os registros antigos antes de carregar as preferências.

Para detalhes de arquitetura e compatibilidade entre versoes, veja `Information.md`.
