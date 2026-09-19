# Organessônium

Este repositório contém a versão desktop C# WPF + WebView2 do Organessônium. O aplicativo usa o Discord como armazenamento de payloads, com um índice hierárquico por pasta para que a abertura da raiz não dependa da quantidade total de arquivos no servidor.

## Arquitetura de dados

- `Bridge/AppController.cs` coordena upload, download, previews, transferências e a ponte WebView2.
- `Services/ForumUploader.cs` cria posts e mensagens com attachments no fórum `files`.
- `Services/ForumDownloader.cs` resolve as mensagens referenciadas pelo metadata, baixa as partes e monta o arquivo final na ordem dos índices.
- `Services/AppRegistry.cs` resolve o mapa `folderId -> threadId`, abre somente o índice da pasta requisitada no fórum `folders` e atualiza os caches afetados por cada operação.
- `Services/MetadataService.cs` publica e lê o mapping no `names` e os metadados de filhos diretos nos threads de índice de `folders`.
- `Services/SerialIndexService.cs` lê, valida, mantém em memória e publica o serial binário de uma única pasta por vez no canal `serial`.
- `Services/FolderSerialCodec.cs` codifica e decodifica o formato binário versionado `OSER` usado pelos seriais.
- `Services/SettingsStore.cs` mantém configurações, os caches locais `app_entries` e `folder_index_map`, previews e estados de transferência no diretório de dados.

Um arquivo é identificado por `fileId`; seu metadata associa esse identificador ao `threadId` de payload, aos IDs das mensagens dos chunks, ao nome e MIME originais, a `sizeBytes` em plaintext, ao `parentFolderId` e às referências de prévias. Pastas usam `folderId`; o `parentFolderId` estabelece a árvore. Os IDs são preservados em rename e move. O metadata não é criptografado, pois é o índice necessário para localizar os blobs no Discord.

## Índice serial

Cada pasta, inclusive `root`, possui exatamente um serial `.bin` próprio. O canal de texto `serial` contém uma mensagem do bot para esse serial, com o envelope JSON `folderSerial`, `folderId`, nome opcional e SHA-256 do anexo. O anexo usa a extensão `.bin` e o formato binário versionado `OSER`; ele guarda somente os filhos diretos, seus IDs, relações, MIME, timestamps, referências de preview, cores, favorito e totais dessa pasta.

O serial é o fast-path persistente. Ele é mantido em `OrganessoniumData\Serials` e, depois da primeira leitura, também em memória durante a sessão. Abrir a raiz lê somente o serial da raiz. Abrir `/Fotos` lê somente o serial de `Fotos`; não lê os seriais das subpastas. Se o serial local estiver ausente, o app baixa apenas o anexo indicado pelo mapping daquela pasta, valida o SHA-256 e grava o arquivo localmente por escrita atômica. Os posts do fórum `folders` são arquivados após a leitura e também são reconciliados na inicialização, mantendo os índices antigos fora do limite de posts ativos.

Um serial novo só passa a ser o atual depois que o mapping correspondente em `names` é confirmado. Se essa confirmação falhar, o anexo provisório é removido e o cache local é invalidado para que a próxima reconciliação não trate conteúdo não confirmado como publicado. Após atualizar os mappings na inicialização, uma manutenção em segundo plano remove anexos `folderSerial` duplicados do bot somente quando o mapping atual daquela pasta ainda aponta para o serial preservado.

Depois de renderizar o conteúdo conhecido, o app reconcilia em segundo plano o thread remoto daquela mesma pasta. O resultado atualiza `app_entries`, o serial correspondente e a interface visível quando houver mudança. A sincronização não percorre todos os seriais nem todos os threads do fórum durante a inicialização. A contagem exibida nos cards de pastas é obtida sob demanda pelos seriais da hierarquia, de forma recursiva e limitada, sem carregar todos os metadados da biblioteca na memória. Quando índices legados duplicados apontam para o mesmo caminho visível, o app carrega e soma todos os IDs daquele grupo, sem ocultar os arquivos que ficaram distribuídos entre eles. Durante essa leitura, a interface mostra carregamento em vez de uma pasta vazia temporária.

`AppRegistry` deduplica uma reconciliação em andamento por `folderId`. A paginação e o parsing de um thread ou do canal `names` usam `SearchWorkers` com concorrência limitada. `DiscordApiClient` mantém o controle de rota e respeita o `retry_after` de HTTP 429, portanto uma verificação em segundo plano não dispara requisições ilimitadas nem duplica o trabalho de uma navegação.

## Metadata publica

O canal de texto `names` é pequeno e contém somente mappings `folderId -> threadId`; ele não armazena mais metadata global de arquivos ou pastas. A mensagem do mapping é JSON inline minificado e inclui também a referência do serial atual quando ele existir. O ID da própria mensagem de `names` é mantido no cache local para atualização e remoção direta.

O fórum configurado em `foldersForumId` corresponde ao canal `folders` e contém somente posts `index-*` de índice de pasta. Cada post de índice possui uma primeira mensagem `folderIndexHeader` para identificação e, depois, somente os metadados `file` e `folder` dos filhos diretos daquela pasta. O fórum configurado em `appsForumId` corresponde ao canal `files` e contém somente os posts de payload usados por upload. O `threadId` de payload continua pertencendo ao arquivo; o `threadId` de índice de uma pasta é resolvido pelo mapping em `names`.

Regra principal: a presenca de um campo significa que o recurso existe. A ausencia significa que o recurso nao existe ou nao se aplica.

O metadata novo nao deve incluir campos nulos, arrays vazios, objetos vazios, `enabled = false`, campos temporarios da UI, campos de video em imagens, nem campos de arquivo em pastas. Arquivos e pastas são publicados no thread de índice do respectivo pai, não no `names`.

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

Não há migração nem fallback automático. Para migrar um servidor antigo, crie o fórum `folders`, remova do `names` todos os metadados globais antigos e recrie nele os posts de índice de cada pasta com o cabeçalho acima. Publique no `names` um mapping para cada thread correspondente, inclusive `root`. O fórum `files` deve manter somente os payloads. Não misture o layout antigo e o novo.

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

## Upload e transferências

Uploads criptografados de arquivos grandes não são mais divididos e criptografados integralmente antes do envio. O app calcula a quantidade de partes usando o limite de plaintext de 10 MB menos o overhead do envelope, lê uma faixa por vez, criptografa essa faixa e grava somente o ciphertext atual em uma pasta oculta `.organessonium-upload-*` ao lado do arquivo original. O ciphertext é removido assim que a mensagem correspondente termina; a pasta também é removida ao fim da transferência. O limite global de `UploadThreads` inclui a preparação e o envio, evitando criar uma cópia temporária para cada chunk do arquivo.

O uploader mantém a posição de cada parte pelo índice, mesmo quando partes pendentes são enviadas em paralelo. O índice é salvo no estado da transferência imediatamente após a confirmação da mensagem, permitindo retomar sem reenviar as partes já confirmadas. Falhas temporárias de rede, HTTP 429, respostas 5xx e o limite de threads ativas do Discord deixam o estado como pausado e recuperável, preservando o thread e os chunks já enviados. Os attachments de payload usam o limite global de `UploadThreads` sem um lock de rota que os serialize; o cliente continua compartilhando o `retry_after` do Discord e serializa as atualizações pequenas de metadata e serial.

O progresso individual cobre preparação, envio dos chunks originais, geração e envio da miniatura, geração e envio da prévia 480p, publicação dos metadados e conclusão. O diálogo mostra o percentual em cada arquivo e no lote. A velocidade de upload e download exibida no diálogo é a média dos bytes transferidos na janela móvel dos últimos 60 segundos; downloads também mostram uma estimativa de conclusão quando há dados suficientes.

### Download, integridade e compatibilidade

Para payloads criptografados, cada tentativa baixa o ciphertext para um arquivo temporário identificado como tal, valida o envelope e a tag GCM, e só então grava o plaintext. Falhas de download, estrutura ou autenticação descartam os temporários suspeitos e fazem no máximo três downloads completos por payload. Após a terceira falha, o diálogo oferece `Tentar novamente`, iniciando uma nova sequência de três buscas. Após reconstruir o arquivo, o tamanho plaintext é comparado com `sizeBytes` do metadata.

Uma falha de autenticação nunca entrega plaintext. Em modo personalizado, a interface informa que a chave configurada pode estar incorreta. Quando a autenticação passa, mas apenas o tamanho do metadata diverge, o app preserva o arquivo validado e oferece `Abrir mesmo assim` em um diálogo nativo.

Entradas legadas sem `encryption` seguem exatamente o caminho anterior de download e continuam acessíveis. O cache de previews armazena somente plaintext: se encontrar um arquivo iniciado por `ORGAESGC`, o app o invalida e refaz o download descriptografado. Ciphertext e plaintext temporários não são reutilizados entre tentativas. A reprodução progressiva existente fica restrita a vídeos legados, pois payloads criptografados precisam ser autenticados antes de expor bytes ao player.

## Persistência local

As configurações e os índices locais ficam em `organessonium.db`, dentro da pasta de dados. A tabela `settings` usa `key`, `value` e `updated_at`; cada gravação atualiza o valor e o horário em UTC pelo SQLite. `folder_index_map` persiste `folderId`, `threadId`, ID da mensagem em `names` e a referência do serial. `app_entries` é cache dos filhos já conhecidos, agrupado por `parent_folder_id`, e mantém também versão, modo e salt de criptografia sem armazenar qualquer chave.

Ao abrir uma pasta, o cache e o serial daquela pasta são mostrados primeiro. Em seguida, somente o thread remoto dessa pasta é consultado. O thread é a fonte de associação dos filhos diretos; `parentFolderId` ausente em metadata legado é normalizado para a pasta que está sendo lida. O botão Atualizar reconcilia o mapping `names` e o índice da pasta aberta, sem voltar a carregar apenas a raiz. A contagem total de arquivos percorre recursivamente os seriais da árvore, enquanto a contagem dos cards é solicitada sob demanda. Falhas temporárias, serial corrompido, thread inacessível ou HTTP 429 não removem o cache local válido: a operação é registrada e a interface continua com o último estado conhecido. A exclusão, criação, rename e move atualizam somente os threads, caches e seriais dos pais envolvidos; a exclusão de uma pasta também remove seu mapping, serial e thread de índice depois de remover seus descendentes.

Ao clicar em `Sair`, o app encerra a sessão, cancela transferências e prévias em andamento e limpa o diretório `OrganessoniumData`. O banco SQLite, caches, seriais e estados de transferência são removidos; somente `WebView` e `Logs` são preservados. O banco é recriado automaticamente quando uma nova sessão for configurada, sem exigir reinicialização do app.

Para detalhes de arquitetura e compatibilidade entre versoes, veja `Information.md`.
