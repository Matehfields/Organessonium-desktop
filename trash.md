# Lixeira: contrato do desktop e implementação Flutter

Este documento descreve o sistema implementado no desktop C#/WebView2 e o contrato que o cliente Flutter deve seguir. O código Flutter de referência está desatualizado e não foi alterado nesta tarefa. A interoperabilidade exige implementar esse contrato no mobile, não apenas adicionar um botão.

## Interface

- Desktop: aba **Lixeira** na barra lateral, usando o mesmo estado recolhido/expandido e os mesmos cards da biblioteca.
- Mobile: adicionar **Lixeira** à barra lateral do aplicativo, com ícone de lixeira e ação para abrir o índice `trash`. Não colocar esse acesso somente na navegação inferior.
- `Delete` envia os itens selecionados à lixeira. O diálogo informa que eles podem ser restaurados. Pastas são movidas com sua árvore intacta.
- `Shift+Delete` pede confirmação e apaga permanentemente os itens selecionados, inclusive seus descendentes quando forem pastas.
- Na Lixeira, o menu do item e a barra de seleção oferecem **Restaurar** e **Apagar permanentemente**. `Delete` sozinho não transforma a ação em exclusão permanente: informa que o item já está na lixeira.
- A restauração opera sobre os filhos diretos da Lixeira. Para recuperar um arquivo dentro de uma pasta apagada, restaurar a pasta antes.
- A lixeira não recebe uploads, criação de pastas nem movimentação comum pela interface. Não há retenção automática, prazo de expiração ou limpeza ao fechar o aplicativo.

## Identidade e armazenamento no Discord

| Identificador | Significado |
| --- | --- |
| `folderId = "trash"` | Identidade fixa da pasta técnica da Lixeira. |
| `/@trash` | Caminho lógico local, independente do idioma. |
| `folderIndex.threadId` | Post de índice da Lixeira no fórum `folders`. |
| `folderIndex.serialMessageId` | Mensagem do canal `serial` com o índice OSER da Lixeira. |
| `file.threadId` | Post de payload existente no fórum `files`; não muda ao enviar para a lixeira ou restaurar. |
| `metadataMessageId` / OSER `namesMessageId` | Mensagem com o metadata completo no índice do pai atual; muda ao mover entre índices. Não confundir com a mensagem do mapping em `names`. |

O índice é criado na primeira abertura da Lixeira ou no primeiro envio para ela. A raiz `root` deve existir antes. Não publicar uma entrada `folder` chamada Lixeira entre os filhos de `root`: a pasta técnica tem mapping independente e é acessada pela barra lateral.

O mapping em `names` é o mesmo contrato usado para pastas normais:

```json
{
  "schemaVersion": 2,
  "entryType": "folderIndex",
  "folderId": "trash",
  "threadId": "<id-do-indice>",
  "serialMessageId": "<id-da-mensagem-do-serial>",
  "serialSha256": "<sha256-do-bin>"
}
```

O post contém um `folderIndexHeader` com `folderId: "trash"`, `parentFolderId: "root"` e `name: "trash"`, seguido dos metadados completos dos filhos diretos. Os payloads continuam nos posts originais de `files`.

## Metadata de recuperação

Arquivos e pastas enviados à Lixeira mantêm `schemaVersion: 2`, sua identidade e os campos existentes. Apenas o pai passa para `trash` e o seguinte objeto é acrescentado ao metadata JSON:

```json
{
  "parentFolderId": "trash",
  "trash": {
    "originalParentFolderId": "<folderId-do-pai-anterior-ou-root>",
    "deletedAt": "2026-10-04T15:00:00Z"
  }
}
```

`originalParentFolderId` é a identidade do pai anterior, não um nome nem um caminho. `deletedAt` é ISO 8601 em UTC. Em itens ativos, omitir `trash`, em vez de gravar `null` ou um objeto vazio.

Preservar `fileId`, `folderId`, `threadId` do payload, `chunks`, tamanho, MIME, timestamps de upload/criação, preview, prévia de vídeo, criptografia, favorito, cor e tipo de dispositivo. Não recriptografar, reenviar os arquivos ou gerar outra identidade.

Ao apagar uma pasta de forma recuperável, mover somente seu metadata para o índice da Lixeira. Seus filhos mantêm os mesmos `parentFolderId`, posts de índice, mappings e payloads. O serial da pasta movida passa a apontar para `parentFolderId: "trash"`; os seriais das pastas descendentes mantêm seus pais.

## Serial e cache

Usar **OSER v3**, com `Kind = Folder`, `folderId: "trash"`, `parentFolderId: "root"` e `folderName: "trash"`. Não criar outra versão binária nem outro tipo de serial. O envelope da mensagem é o `folderSerial` habitual, com ID da pasta e SHA-256 do anexo.

O serial guarda os filhos diretos e seus agregados, mas não contém a lista completa de chunks nem o objeto `trash`. Para mover ou restaurar, o cliente precisa ler e validar o metadata JSON completo indicado pelo `namesMessageId` do filho. Essa mensagem fica no thread de índice do pai atual, não necessariamente no canal `names`.

O desktop aceita a origem em `names` quando um item de um serial existente aponta para metadata publicado diretamente nesse canal. O destino continua sendo o índice mapeado da Lixeira. Uma origem sem índice mapeado exige o serial existente da pasta para preservar seus demais filhos.

Ao alterar o conteúdo, substituir apenas o anexo `.bin` da mensagem existente, mantendo `serialMessageId`. Atualizar o SHA-256 do mapping depois da publicação. Criar uma mensagem somente quando não houver uma referência válida. Uma Lixeira vazia continua com mapping, post de índice e serial vazio; não excluir a infraestrutura técnica.

Persistir o serial entre execuções e mostrar o cache válido antes da leitura remota. `F5`/Atualizar reconciliam também a árvore da Lixeira. `Ctrl+F5` pode reconstruir os seriais locais a partir dos metadados remotos sem apagar arquivos. O SQLite desktop guarda os filhos por `parent_folder_id`; os dados de restauração são lidos do JSON remoto, não deduzidos do cache.

## Enviar para a Lixeira

1. Ler o metadata completo na origem, conferir identidade e associação ao pai. Se ele estiver ausente ou um arquivo não vazio não tiver chunks, interromper sem excluir nada.
2. Garantir o índice `trash` e ler os filhos atuais da origem e do destino.
3. Criar o metadata completo no destino, com o novo pai e o objeto `trash`.
4. Depois da confirmação do destino, remover somente a mensagem de metadata da origem. Nunca excluir o post de payload nesta operação.
5. Se a remoção da origem falhar, remover a cópia recém-publicada no destino e manter o original. Uma falha adicional de rede pode exigir reconciliação com Atualizar; não apagar payloads para resolver duplicatas.
6. Atualizar caches, listas renderizadas, seriais da origem/destino e, quando for pasta, o serial dessa pasta. Recalcular Favoritos excluindo toda a árvore da Lixeira.

No lote, se um ancestral e seus filhos estiverem selecionados, mover somente o ancestral. A identidade é a chave da operação, não o nome.

## Restaurar

Ler novamente o metadata completo da Lixeira. Usar `trash.originalParentFolderId` quando esse pai continuar mapeado e fora da árvore da Lixeira. Caso contrário, restaurar em `root`.

Resolver o caminho atual pelo encadeamento de IDs dos seriais, não por um caminho antigo salvo. Remover o objeto `trash`, publicar no índice de destino antes de remover a mensagem da Lixeira e atualizar os caches/seriais. O favorito é preservado e volta à lista ativa após a restauração.

Se outra pasta com o mesmo nome já existir no destino, o desktop bloqueia a restauração sem alterar o item. Renomear essa pasta ativa antes de restaurar evita o mecanismo de consolidação de duplicatas do índice normal. Dentro da Lixeira, pastas apagadas com nomes iguais são itens independentes: nunca consolidá-las. Abrir uma delas usando seu `folderId`, não juntando todos os itens com o mesmo caminho textual.

## Exclusão permanente

Depois de confirmação explícita, excluir o post de payload do arquivo, sua mensagem de metadata, referências no cache e os registros locais de preview/progresso. Para pastas, remover os arquivos descendentes antes de excluir índices, seriais, mappings e metadados das pastas. Falhas em arquivos preservam as pastas para nova tentativa. Reconciliar os seriais dos pais afetados.

Esta operação é irreversível. Enviar para a Lixeira nunca deve chamar esse fluxo.

## Isolamento entre telas

Arquivos, Favoritos, Dispositivos e Lixeira não devem compartilhar uma lista filtrada sem identificar a origem. Excluir a árvore `trash` das telas ativas, busca normal e seleção normal, inclusive quando descendentes ainda tiverem um caminho antigo no cache. Verificar a cadeia de `parentFolderId` até `trash`, além do caminho `/@trash`.

A pasta técnica não aparece como card em Arquivos. Favoritos retém a marca no metadata, mas não lista itens dentro da árvore da Lixeira. Respostas assíncronas devem ser aplicadas ao índice/tela correspondente. Navegar para outra aba não pode inserir seus itens no índice atual.

## Validação necessária no Flutter

Validar com o servidor de teste e depois com os dois clientes: envio de arquivo, pasta com descendentes, último favorito, restauração, pai original removido, duas pastas apagadas com nome igual, leitura após reiniciar e exclusão permanente. Conferir IDs, chunks, preview, criptografia e SHA-256 antes/depois, além do resultado visível nas telas. Testar falha na publicação do destino sem perda do original.

O teste desktop `webViewTrashLifecycle` usa a interface WebView2, o controller, SQLite e o codec reais, com transporte Discord simulado. Isso não certifica o cliente Flutter desatualizado nem substitui a validação de interoperabilidade após implementar o contrato no mobile.
