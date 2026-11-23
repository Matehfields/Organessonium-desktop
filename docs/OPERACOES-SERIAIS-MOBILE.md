# Operações da biblioteca pelos seriais no Mobile

O Desktop usa dois estados complementares. A fila local permite iniciar a ação sem rede e mantém o andamento. O canal de seriais recebe uma mensagem por operação retomável. O estado confirmado da biblioteca continua nos seriais OSER v3 e nos metadados de pasta.

## Contrato para o Android

| Campo | Formato |
| --- | --- |
| Canal | O canal configurado dos seriais da biblioteca. |
| Conteúdo | `library-operation-v1:<operationId>`, com ID de 32 dígitos hexadecimais. |
| Anexo | `metadata.json`, em UTF-8, com o envelope cifrado de metadados v1. |
| Documento decifrado | `schemaVersion: 1`, `entryType: "libraryOperation"` e `operation`. |
| Tipo da operação | Numeração do Desktop `0` mover, `1` enviar à Lixeira, `2` restaurar, `5` renomear. |
| Identidade e andamento | `operationId`, `timestamp`, `changes` e, para movimentações, `relocations`, incluindo origem, destino, canal de origem e `filePayload` completo quando se tratar de arquivo. |

O leitor identifica o autor pelo ID retornado por `GET /users/@me` para o bot configurado. Confere a versão, autentica o documento, valida o ID do marcador e deduplica pela identidade da operação. Clientes antigos podem continuar lendo os seriais de pasta, pois o formato e os localizadores OSER continuam iguais. Eles não executam a nova fila.

Para executar uma operação, o Android deve:

1. Importar o documento para a fila local, preservando o ID e o instante original. Ações criadas sem rede só ficam visíveis a outros dispositivos depois da publicação.
2. Validar o item, a thread e o `parentFolderId` esperados no Discord. Para um arquivo compartilhado, adotar a reserva `library-<nonce>` da operação. O nonce são os 24 primeiros caracteres hexadecimais minúsculos do SHA-256 UTF-8 de `operationId + ":" + source.metadataMessageId`. Reservas dessa identidade sobrevivem à interrupção e são liberadas na conclusão da operação. Uma reserva de outro trabalho para o mesmo arquivo é conflito.
3. Para mover, enviar à Lixeira ou restaurar, conferir a publicação anterior no destino antes de repetir um POST. Usar o mesmo nonce com `enforce_nonce` para a janela recente de respostas incertas. Após validar identidade, versão e localização do destino, publicar os seriais afetados e só então remover o metadado de origem. Para renomear, editar a mesma mensagem e repetir a atualização do título da thread se o item for uma pasta.
4. Remover a mensagem da operação no canal de seriais depois da confirmação da última etapa. Um 404 nessa remoção pode representar conclusão em outro dispositivo. O arquivo e seu payload continuam acessíveis através da biblioteca.

## Estado de armazenamento e retirada da thread antiga

O Desktop recupera lotes de upload com `storageBatchPlan` e os anexos efetivos, sem emitir novos `storageBatchReceipt`. O Android atual mantém sua escrita e leitura de recibos nas transferências que ele inicia. Antes de compartilhar a retomada de uploads entre plataformas, o Android precisa aceitar o rótulo cifrado do lote Desktop ao reconstruir um recibo ausente. Checkpoints de upload, caminhos de origem e dados parciais de download continuam vinculados à instalação.

`storage-v2` ainda é lida e escrita pelos dois clientes. Ela contém catálogos de pools, reservas, referências ativas, planos de recuperação e limpezas pendentes. Para retirar a thread, os dois clientes precisam primeiro ler o estado migrado. Com os escritores pausados, é necessário exportar e autenticar o estado necessário, validar a retomada de uploads e a proteção da limpeza em duas instalações e conferir que a thread antiga deixou de receber escritas. A exclusão do post e do mapeamento vem depois dessas verificações.

Os testes Desktop simulam duas instalações e a retomada de uma operação interrompida. A execução pelo Android e a retirada da thread do Discord ainda precisam ser validadas.
