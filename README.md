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

O serial é o fast-path persistente. Ele é mantido em `OrganessoniumData\Serials` e, depois da primeira leitura, também em memória durante a sessão. O codec lê `OSER` v1 a v4, mas publica v3 para manter o formato binário compartilhado. `sizeBytes` por filho existe somente em v4; o agregado da pasta preserva o total de bytes. Abrir a raiz lê somente o serial da raiz. Abrir `/Fotos` lê somente o serial de `Fotos`; não lê os seriais das subpastas. Se o serial local estiver ausente, o app baixa apenas o anexo indicado pelo mapping daquela pasta, valida o SHA-256 e grava o arquivo localmente por escrita atômica. Antes de reutilizar um serial local durante uma sincronização, o app confirma uma vez por sessão que a mensagem remota indicada pelo mapping ainda existe. Mapping sem `serialMessageId` ou mensagem removida faz o serial ser republicado e o mapping ser atualizado. Os posts do fórum `folders` são arquivados após a leitura e também são reconciliados na inicialização, mantendo os índices antigos fora do limite de posts ativos.

Um serial novo só passa a ser o atual depois que o mapping correspondente em `names` é confirmado. Se essa confirmação falhar, o anexo provisório é removido e o cache local é invalidado para que a próxima reconciliação não trate conteúdo não confirmado como publicado. Após atualizar os mappings na inicialização, uma manutenção em segundo plano remove anexos `folderSerial` duplicados do bot somente quando o mapping atual daquela pasta ainda aponta para o serial preservado. O serial não contém a lista de chunks; ao abrir a prévia ou baixar um arquivo sem detalhes no cache, o desktop busca o metadata completo por `namesMessageId` em `names` ou no thread de índice do pai e confere `fileId` e `threadId` antes de usar os payloads.

Depois de renderizar o conteúdo conhecido, o app reconcilia em segundo plano o thread remoto daquela mesma pasta. O resultado atualiza `app_entries`, o serial correspondente e a interface visível quando houver mudança. A sincronização não percorre todos os seriais nem todos os threads do fórum durante a inicialização. Os cards de pastas não exibem contagem de arquivos nem solicitam estatísticas por pasta durante a renderização. Pastas são únicas por pai e nome: uma nova solicitação para a mesma pasta reutiliza o `folderId` existente. Durante a reconciliação, duplicatas legadas são consolidadas na pasta canônica, com os filhos preservados antes da exclusão do metadata, mapping, serial e thread de índice duplicados. Durante essa leitura, a interface mostra carregamento em vez de uma pasta vazia temporária. Uma leitura remota sem nenhum metadata válido não apaga os itens já renderizados; a tela Arquivos exige associação de `parentFolderId` com a raiz antes de exibir itens nela.

`AppRegistry` deduplica uma reconciliação em andamento por `folderId`. A paginação e o parsing de um thread ou do canal `names` usam `SearchWorkers` com concorrência limitada. `DiscordApiClient` mantém o controle de rota e respeita o `retry_after` de HTTP 429, portanto uma verificação em segundo plano não dispara requisições ilimitadas nem duplica o trabalho de uma navegação.

Para compatibilidade com os seriais existentes, o canal `serial` também aceita os índices globais diretos `favorites` e `synchronized`, sem depender de um mapping desktop. O serial `favorites`, de tipo próprio, mantém a lista plana de itens favoritos. O serial `synchronized` representa a raiz lógica `/@synchronized`; essa raiz técnica não é exibida na tela Arquivos, enquanto seus filhos diretos aparecem em Dispositivos e usam `deviceType` igual a `phone` ou `pc`. Ao abrir Dispositivos, o desktop recompõe os seriais apenas quando encontra filhos diretos de `synchronized` nos metadados de `names`; caso contrário, lê o serial existente e, se ele estiver ausente, os metadados do thread de índice apontado pelo mapping. A presença isolada do metadata da raiz não publica um serial vazio. Os filhos são associados por `folderId` e `parentFolderId`. No formato OSER v3, os bits de favorito, telefone e computador são preservados.

## Metadata publica

No layout desktop atual, o canal de texto `names` é pequeno e contém mappings `folderId -> threadId`; a mensagem do mapping é JSON inline minificado e inclui também a referência do serial atual quando ele existir. O ID da própria mensagem de `names` é mantido no cache local para atualização e remoção direta. Para interoperar com metadados publicados diretamente em `names`, o leitor também aceita metadata de arquivo ou pasta nesse canal quando ele é referenciado por um serial global ou de dispositivo. Ao alterar apenas o favorito de um item desse tipo, o JSON remoto original é carregado e atualizado integralmente para preservar chunks, criptografia, previews e todos os demais campos.

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
  "threadId": "123456789012345678",
  "name": "1745186438334.jpg",
  "mimeType": "image/jpeg",
  "sizeBytes": 317584,
  "uploadedAt": "2026-06-30T20:13:00Z",
  "batchId": "ca433d8f1b",
  "chunks": ["123456789012345678"],
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

Uploads criptografados de arquivos grandes não são mais divididos e criptografados integralmente antes do envio. Para arquivos a partir de 256 MiB, a política interna `UploadBufferStrategy.Auto` consulta a memória física total e disponível pelo `GlobalMemoryStatusEx`, o Working Set do processo e a configuração `UploadThreads`. O plano preserva a maior entre 2 GiB e 10% da RAM física para o sistema, usa no máximo um oitavo da RAM física disponível e limita o processo a um alvo de 800 MiB, sem reservar além do teto de 1 GiB. Cada worker de RAM recebe um orçamento conservador de 64 MiB para plaintext, ciphertext, multipart HTTP e overhead; assim, um arquivo de 1 TiB usa a mesma quantidade limitada de memória que um arquivo de 1 GiB. A estratégia `Disk` continua disponível internamente para diagnóstico; mesmo quando `Memory` é solicitada, o teto físico e do processo nunca é ignorado.

No caminho de RAM, um worker lê apenas a faixa atual de aproximadamente 10 MB, cifra diretamente em um buffer de ciphertext próprio e envia esse mesmo buffer por `MemoryStream`, sem escrever ou reler um arquivo temporário. O plaintext é zerado antes de voltar ao pool local da fila, e o ciphertext só é devolvido depois de sucesso, falha definitiva ou cancelamento. Tentativas HTTP reutilizam o ciphertext em memória com um stream novo, sem recifrar e sem alterar nonces, salts, hashes, metadados ou mensagens já publicados. Ao terminar ou cancelar o lote, o coordenador limpa e descarta seu pool; quando buffers grandes foram usados, ele solicita a compactação do LOH e uma coleta completa para que o próximo upload reutilize a memória liberada em vez de acumular outro conjunto de chunks. Se a memória estiver baixa, a reserva falhar, a pressão aumentar ou não houver vaga, somente o próximo chunk usa o caminho legado: ciphertext em `.organessonium-upload-*` ao lado do original, removido após o envio. Arquivos menores permanecem nesse fallback por padrão.

O uploader mantém a posição de cada parte pelo índice, mesmo quando partes pendentes são enviadas em paralelo. Ele cria somente a quantidade limitada de workers e cada worker pede o próximo índice após concluir o anterior, portanto não há fila ilimitada de chunks lidos ou criptografados. Em um lote, `UploadThreads` é dividido pelo número de arquivos ativos para limitar os workers locais de cada arquivo; com 16 workers globais e 8 vídeos simultâneos, cada vídeo mantém no máximo 2 partes em andamento. O limite global de rede continua em `UploadThreads`; o coordenador de memória limita separadamente quantos workers podem manter buffers em RAM, e workers excedentes usam o fallback temporário em disco sem reduzir a concorrência de rede. A vaga global cobre preparação e envio: quando a vazão agregada de todos os workers indica que a internet virou gargalo por várias amostras consecutivas, o controlador reduz gradualmente os uploads ativos até dois workers e volta a aumentar após amostras saudáveis. A decisão usa a vazão agregada estimada, não a velocidade individual de cada conexão, evitando interpretar a banda compartilhada entre 16 partes como 16 conexões lentas. A cada poucos segundos, a pressão de memória também reduz novas vagas de RAM sem mover chunks já em voo; eles terminam normalmente e os próximos passam para RAM ou disco conforme o orçamento atual. O índice é salvo no estado da transferência imediatamente após a confirmação da mensagem, permitindo retomar sem reenviar as partes já confirmadas. Depois da reconciliação inicial, uma verificação silenciosa lista posts ativos e arquivados do fórum `files`, compara seus `threadId` com os arquivos indexados e retoma apenas posts órfãos que ainda possuem estado local recuperável e arquivo de origem. Sem esse estado local, o app registra o órfão, mas não inventa nome, pasta, criptografia ou IDs de chunks. Falhas temporárias de rede, HTTP 520, respostas 5xx e indisponibilidade momentânea da internet aguardam com backoff até a conexão voltar, respeitando o cancelamento explícito do usuário. Uma espera isolada de chunk não substitui o progresso visível enquanto outra parte ainda está sendo transmitida; o estado de espera aparece quando todas as requisições de partes estão paradas ou quando a etapa é única, como metadata, miniatura ou prévia unitária. HTTP 429 e o limite de threads ativas do Discord continuam preservando o thread e os chunks já enviados. O app não persiste logs de debug; somente exceções não tratadas produzem arquivos em `OrganessoniumData\Logs`.

O progresso individual cobre preparação, envio dos chunks originais, geração e envio da miniatura, geração e envio da prévia 480p, publicação dos metadados e conclusão. O diálogo mostra o percentual inteiro em cada arquivo e no lote. A velocidade de download é a média dos bytes transferidos na janela móvel dos últimos 60 segundos; a velocidade de upload usa somente os intervalos em que as requisições HTTP estão ativas, excluindo esperas de rate limit e backoff do Discord. Downloads também mostram uma estimativa de conclusão quando há dados suficientes.

### Download, integridade e compatibilidade

Para payloads criptografados, cada tentativa baixa o ciphertext para um arquivo temporário identificado como tal, valida o envelope e a tag GCM, e só então grava o plaintext. Falhas temporárias de rede durante o download aguardam e repetem a parte até a conexão voltar. Falhas de estrutura ou autenticação descartam os temporários suspeitos e fazem no máximo três downloads completos por payload. Após a terceira falha, o diálogo oferece `Tentar novamente`, iniciando uma nova sequência de três buscas. Após reconstruir o arquivo, o tamanho plaintext é comparado com `sizeBytes` do metadata.

Uma falha de autenticação nunca entrega plaintext. Em modo personalizado, a interface informa que a chave configurada pode estar incorreta. Quando a autenticação passa, mas apenas o tamanho do metadata diverge, o app preserva o arquivo validado e oferece `Abrir mesmo assim` em um diálogo nativo.

Entradas legadas sem `encryption` seguem exatamente o caminho anterior de download e continuam acessíveis. O cache de previews armazena somente plaintext: se encontrar um arquivo iniciado por `ORGAESGC`, o app o invalida e refaz o download descriptografado. Ciphertext e plaintext temporários não são reutilizados entre tentativas. A reprodução progressiva existente fica restrita a vídeos legados, pois payloads criptografados precisam ser autenticados antes de expor bytes ao player.

## Persistência local

As configurações e os índices locais ficam em `organessonium.db`, dentro da pasta de dados. A tabela `settings` usa `key`, `value` e `updated_at`; cada gravação atualiza o valor e o horário em UTC pelo SQLite. `folder_index_map` persiste `folderId`, `threadId`, ID da mensagem em `names` e a referência do serial. `app_entries` é cache dos filhos já conhecidos, agrupado por `parent_folder_id`, e mantém também favorito, tipo de dispositivo, versão, modo e salt de criptografia sem armazenar qualquer chave.

Ao abrir uma pasta, o cache e o serial daquela pasta são mostrados primeiro. Em seguida, somente o thread remoto dessa pasta é consultado. O thread é a fonte de associação dos filhos diretos; `parentFolderId` ausente em metadata legado é normalizado para a pasta que está sendo lida. O botão Atualizar reconcilia o mapping `names` e o índice da pasta aberta, sem voltar a carregar apenas a raiz. `F5` atualiza o mapping e verifica o thread da pasta aberta em busca de novos itens, sem limpar os seriais. `Ctrl+F5` atualiza o mapping, limpa somente os seriais locais e os recria dos metadados do servidor. A publicação remota ocorre antes da troca de referência no mapping, sem exclusão em massa do canal. A contagem total de arquivos percorre recursivamente os seriais da árvore, enquanto a contagem dos cards é solicitada sob demanda. Uma leitura vazia dos mappings não substitui o mapa local existente. Falhas temporárias, serial corrompido, thread inacessível ou HTTP 429 não removem o cache local válido: a operação é registrada e a interface continua com o último estado conhecido. Se o mapping de uma pasta foi perdido, mas o metadata ainda guarda o `threadId` do seu índice, a exclusão lê esse thread diretamente para remover os descendentes e a própria pasta. Em uploads e exclusões em lote, os metadados e caches são atualizados por item, mas cada serial afetado é publicado somente ao fim da ação. A exclusão de uma pasta também remove seu mapping, serial e thread de índice depois de remover seus descendentes.

O comando `--audit-metadata` faz uma leitura do canal `names`, dos threads do fórum `folders` e do canal `serial`, sem alterar o servidor, e grava `metadata-server-audit.txt` no diretório de dados. O relatório lista os metadados reconhecidos e aponta mensagens ilegíveis, seriais inválidos e referências sem metadata. O comando `--repair-serials` exige uma auditoria inicial sem metadados ilegíveis ou referências ausentes, refaz o cache local e publica os seriais a partir dos metadados, depois repete a auditoria. O relatório contém nomes e IDs privados da biblioteca e não deve ser publicado.

Ao confirmar `Sair`, o splash WPF da inicialização cobre Configurações durante a limpeza, sem mostrar a biblioteca anterior. O app encerra a sessão, cancela transferências e prévias em andamento, apaga configurações e entradas em cache do SQLite e limpa o diretório `OrganessoniumData`. Banco, caches, seriais e estados de transferência são removidos; somente `WebView` e `Logs` são preservados. Se algum dado local não puder ser apagado, o splash sai e Configurações volta com a opção de tentar novamente. Após a limpeza, a configuração inicial aparece sem token nem IDs anteriores; o banco é recriado vazio, sem exigir reinicialização do app.

## Interface desktop

A janela principal usa a WebView2 dentro de uma shell WPF. O primeiro frame exibe um splash WPF leve, com o ômega centralizado e pulsação suave, antes de abrir o SQLite e inicializar a WebView2; esse trabalho persistente começa somente depois que a janela já foi renderizada. No Windows 11, o `AppWindowTitleBar` do Windows App SDK estende a shell para a faixa superior, mas o sistema continua desenhando e operando minimizar, maximizar/restaurar e fechar. A WebView recebe os insets reais dos botões para não colocar ações sob eles, e as regiões vazias da barra e da marca lateral são marcadas como arrastáveis. Essas regiões são reaplicadas após alterações de layout; ao abrir Prévia ou Configurações, a região acompanha o cabeçalho visível e exclui seus controles clicáveis. Em Windows sem suporte ou se a inicialização falhar, o app preserva a moldura WPF padrão. A barra lateral tem a marca do Organessônium e um botão de painel para recolher sua largura. Recolhida, ela mostra o ômega e revela o botão de expansão ao passar o mouse sobre a marca, ocultando a marca sob o botão durante a transição. Os ícones dos comandos laterais usam slots fixos nos dois estados, então permanecem ancorados enquanto a largura anima; os rótulos aguardam o fim da expansão e entram ou saem por opacidade, sem quebra de linha. O botão destacado `Enviar` abre um menu para criar pasta, criar e abrir um novo Markdown, enviar arquivos ou enviar uma pasta; ele permanece no mesmo lugar, mas fica desativado em Favoritos e Dispositivos para não deslocar a navegação. As mesmas ações de arquivos continuam disponíveis no menu de contexto da área de arquivos. A área de arquivos exibe o caminho da pasta na mesma linha do nome atual e, logo abaixo, filtros de tipo e data, ordenação, visualização e atualização do índice. A seleção por arraste começa e permanece somente na região dos cards, abaixo do cabeçalho. A barra lateral mantém o acesso a Arquivos, Favoritos, Dispositivos sincronizados e Configurações; as transferências continuam visíveis no painel próprio de operações. O menu de cada arquivo ou pasta permite adicionar e remover o item dos favoritos, e a alteração aparece imediatamente tanto na grade atual quanto na aba Favoritos. A aba Dispositivos mostra as raízes publicadas sob `synchronized` e permite navegar progressivamente pela árvore compartilhada. Não há área de compartilhamento nem perfil de usuário.

A barra lateral e a barra superior usam o fundo contínuo da janela. A grade e a lista de arquivos ficam em uma única área de cards levemente mais escura, com cantos arredondados e contraste próprio. Na grade, o tamanho Médio apresenta aproximadamente seis cards por linha em 1920×1080 com a barra lateral aberta; a quantidade de colunas aumenta ou diminui com a largura disponível. As miniaturas quadradas têm margem interna em relação às bordas dos cards, e os tamanhos Pequeno e Grande preservam essa proporção. Os ícones da WebView2 usam a fonte local Material Symbols Rounded do Google, sem depender de uma chamada de rede durante a inicialização. Os thumbnails visíveis são solicitados na ordem dos cards, carregados com concorrência limitada pela configuração `Prévias simultâneas` e entregues à interface de cima para baixo. O rodapé de status anterior não é exibido; mensagens continuam acessíveis pela região `aria-live`, enquanto atividades em segundo plano e transferências usam seus próprios painéis. O resumo lateral fica no rodapé, separa o armazenamento por uma linha e mostra bytes e a quantidade total em formato compacto, como `8,43 GB 312 itens`, depois de receber o agregado completo dos seriais: `AppRegistry.GetLibraryStatisticsAsync` soma os agregados diretos de cada pasta da árvore, e `AppController` entrega o resultado à WebView2. Uma biblioteca vazia é mostrada como `0 B` e `0 itens`. Um ícone animado junto desse resumo permanece visível enquanto alguma leitura ou reconciliação com o servidor está em andamento, inclusive com operações sobrepostas, e desaparece somente depois que todas terminam. Assim, o resumo não trata apenas os filhos da pasta atualmente aberta como a biblioteca inteira. Configurações abre uma tela inteira dentro da janela, com as seções distribuídas em colunas no espaço amplo e uma coluna única em janelas menores. O editor Markdown abre em suas guias locais; sua barra de formatação acompanha a largura útil da área de trabalho e mantém os ícones visíveis no modo compacto.

Para detalhes de arquitetura e compatibilidade entre versoes, veja `Information.md`.

## Geração de releases para Windows

`build-releases.bat` gera os artefatos separadamente em `artifacts/releases/<versão>` sem alterar o pacote portátil existente. A versão pode vir de `release/release-settings.json` ou ser informada no comando, por exemplo `build-releases.bat 1.2.0.0`. Use sempre `A.B.C.0`: os três MSIX e o MSIXBundle recebem exatamente essa versão, enquanto o Windows Installer usa os três primeiros campos para controlar upgrades do MSI.

- `Organessonium-windows-source.zip` contém somente a solução, o projeto WPF, código C#/XAML/web, assets e documentação necessários para abrir `Organessonium.slnx`, restaurar os pacotes NuGet e compilar o aplicativo. As entradas usam `/`; `bin`, `obj`, artefatos, caches, logs, bancos locais, certificados e configurações locais são excluídos. Antes de criar o ZIP, o script também bloqueia tokens, webhooks, chaves privadas e caminhos de usuário detectados no conteúdo público.
- `Organessonium-installer-x86.msi`, `Organessonium-installer-x64.msi` e `Organessonium-installer-arm64.msi` são produzidos pelas APIs nativas do Microsoft Windows Installer e pelo `MakeCab` do Windows. O Windows Installer exige um pacote distinto por arquitetura, portanto não existe um único MSI misto. Cada instalador contém a publicação multi-file framework-dependent correspondente, em cabinet sem compressão, e instala somente para o usuário atual em `%APPDATA%\Organessonium`, ao lado de `%APPDATA%\OrganessoniumData`, sem exigir elevação. Os atalhos do Menu Iniciar e da Área de Trabalho também pertencem apenas ao usuário atual. O instalador impede downgrade, preserva o `UpgradeCode` x64 existente e usa códigos fixos próprios para x86 e ARM64.
- `msix/Organessonium_<versão>_x86.msix`, `msix/Organessonium_<versão>_x64.msix` e `msix/Organessonium_<versão>_arm64.msix` contêm, cada um, o executável self-contained e single-file da própria arquitetura.
- `Organessonium_<versão>.msixbundle` reúne exatamente os três MSIX. O `MakeAppx` recebe a versão explicitamente por `/bv`, permitindo que o Windows selecione x86, x64 ou ARM64 conforme a máquina.
- `SHA256SUMS.txt` registra os hashes dos três MSIs, dos três MSIX, do MSIXBundle e do ZIP de código-fonte.

Para envio à Microsoft Store, use os valores de `storeIdentityName`, `storePublisher`, `storePublisherDisplayName` e `storeDisplayName` da reserva no Partner Center. O último campo define tanto `Package/Properties/DisplayName` quanto o nome visível do aplicativo no manifesto. Se for necessário usar valores diferentes dos padrões de `release/release-settings.json`, copie `release/release-settings.local.example.json` para `release/release-settings.local.json` e preencha os campos exatos. O arquivo local é ignorado pelo Git, não entra no ZIP e não deve ser publicado. O fluxo normal da Store não exige PFX: o bundle pode permanecer sem assinatura local e será processado pela Microsoft. O pacote MSIX continua instalado pela Store no contexto do usuário atual; `runFullTrust` permite executar o aplicativo desktop, mas não transforma a implantação em uma instalação para todos os usuários.

O script exige o .NET SDK e o Windows 10/11 SDK instalados pelo Visual Studio Installer. Os MSIs não são self-contained: a máquina de destino precisa ter o .NET Desktop Runtime 10 e o Windows App SDK Runtime 1.8 da arquitetura correspondente. Se `ORGANESSONIUM_PFX_PATH` estiver definido, o script usa o `SignTool` oficial para assinar os pacotes; a senha pode ser passada apenas no ambiente por `ORGANESSONIUM_PFX_PASSWORD`, sem ser gravada no repositório. `ORGANESSONIUM_TIMESTAMP_URL` é opcional. O MSI destinado a distribuição pública deve ser assinado com um certificado confiável.

Ao final, o script valida conteúdo, arquitetura, versão, identidade e encoding de cada artefato, descompacta administrativamente os três MSIs e recompila uma extração limpa do ZIP. Para solicitar o Windows App Certification Kit, execute `build-releases.bat 1.2.0.0 -RunWack` em um terminal elevado. A ausência ou falha do WACK é reportada sem apagar os demais artefatos; a certificação final e a associação da identidade ainda dependem do Partner Center.
