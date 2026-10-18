# Downloads diretos do desktop

## Escopo e fluxo

Somente C#/WPF/WebView2. Não altera Android, metadados remotos, locators Discord, ORGAESGC, AES-GCM, KDF, limites de RAM, workers, rede, retries, uploads ou streaming. A API `DownloadAndAssembleAsync` conserva o nome para compatibilidade dos chamadores, mas não monta arquivos. `DownloadAssembler` foi retirado.

`ForumDownloader` resolve os mesmos anexos anteriores. `DownloadFileLayout` ordena os índices e calcula soma acumulada `long` com overflow verificado. O tamanho lógico é o tamanho do anexo, menos `EncryptionService.EnvelopeOverheadBytes` somente quando o metadata declara criptografia. Índices descontínuos, comprimentos negativos e divergências do manifesto são recusados.

`PartialDownloadFile` abre um handle assíncrono. Cada worker escreve apenas sua região usando `RandomAccess.WriteAsync`, sem alterar um cursor compartilhado. A região impede exceder seu limite. A ordem de conclusão não importa. Não há fila de ordenação nem cache adicional de chunks. Não se cria `.organessonium_parts_*`, `.preview_parts_*`, arquivo ciphertext ou segunda saída de montagem.

Payloads cifrados mantêm a reserva global anterior, de `2 * tamanho cifrado + 1 MiB`, e o limite existente de 64 MiB. GCM e checksum são conferidos antes de gravar plaintext; buffers sensíveis são zerados ao sair, inclusive em erro. Plaintext legado flui da rede para a região com buffer de 1 MiB e SHA-256 incremental, sem carregar o chunk inteiro.

## Identidade e caminhos

O nome final passa pelo saneamento de nomes existente. O parcial fica ao lado dele: `<nome limitado a 120 caracteres>.<primeiros 32 hex do SHA-256 do transferId>.part`. Não há uma pasta intermediária. O diretório operacional dos checkpoints permanece o mesmo.

A identidade local SHA-256 considera fileId, threadId, batchId, nome, versionId, tamanho, criptografia, referências e checksums. Destino igual sem identidade igual não permite reaproveitar dados. Um gate por criação evita checkpoints duplicados no manager, e a posse do destino/handle impede escritores simultâneos. O arquivo parcial não pode ser um reparse point. Limpeza valida caminhos derivados da transferência; não apaga o arquivo definitivo.

## Checkpoint local V1

Os campos aditivos são serializados com o JSON camelCase já usado pelo `TransferStateRepository`:

| Campo | Significado |
| --- | --- |
| `downloadCheckpointVersion` | `1`; ausente/zero identifica o modelo anterior |
| `partialFilePath`, `finalFilePath` | Caminhos exatos, conferidos contra destino, nome e transferId |
| `downloadIdentity` | Identidade local do conteúdo/versão |
| `downloadReadyToFinalize` | Conteúdo validado e sincronizado antes da renomeação |
| `downloadSha256` | SHA-256 local do arquivo validado, para recuperar a finalização |
| `chunks[].offset` | Offset lógico de 64 bits |
| `chunks[].logicalSizeBytes` | Tamanho plaintext da região |
| `chunks[].sha256` | Digest local calculado dos bytes recebidos/validados |

`chunks[].downloaded`, `downloadedChunks` e `pendingChunks` mantêm a API existente, mas não dependem de `tempChunkPath`. Referências de anexos, snapshot de metadata e campos de upload permanecem compatíveis. Versões desconhecidas de checkpoint não são interpretadas como legadas.

Grupos de até quatro conclusões e o encerramento dos workers fazem `RandomAccess.FlushToDisk` antes do checkpoint. O JSON temporário também é fisicamente sincronizado antes da troca atômica. Não há checkpoint por bloco HTTP. Se a sincronização do arquivo falhar, conclusões ainda não duráveis são invalidadas antes de o erro ser entregue ao chamador. Uma queda entre escrita e checkpoint pode exigir redownload, sem aceitar bytes não confirmados.

## Retomada e compatibilidade

Cada região marcada como concluída é relida e comparada com o checksum remoto ou o digest local. Região ausente, alterada, truncada ou sem hash confiável fica pendente. Não se trunca o parcial ao abrir, preservando partes corretas fora de ordem. A extensão lógica do parcial não é usada como prova de conclusão ou de alocação física.

Checkpoints antigos mantêm seus arquivos até haver substituição durável. Havendo hash verificável, a migração copia e calcula o hash de um chunk por vez para a região correta, com flush/checkpoint agrupado. Sem hash, baixa novamente. Só arquivos com caminho e nome reconhecidos na pasta legada daquela transferência podem ser removidos automaticamente após a substituição; arquivos inesperados ou externos são preservados. Não há exclusão recursiva. Uma migração pode precisar de espaço para um pequeno grupo adicional de chunks, mas não para uma segunda cópia integral.

Falhas da biblioteca ficam pausadas e com erro visível, sem mudar a política dos uploads ou os retries. Cancelamento com limpeza aguarda drenar/fechar e remove somente o parcial identificado; sem limpeza conserva checkpoint pausado e parcial. A ação explícita de marcar transferências como falha continua disponível.

A biblioteca e o sincronizador fornecem `TransferState` para persistir retomada. Sobrecargas internas sem estado, usadas por prévias e chamadores legados, compartilham o gravador direto, mas não criam uma persistência paralela: descartam o parcial não validado em erro/cancelamento. Um parcial integralmente validado é preservado se a renomeação falhar, sem retomada automática nesse chamador sem estado.

## Verificação e finalização

Uma leitura sequencial verifica hashes de regiões e do arquivo completo. Regiões divergentes voltam à fila, usando o limite de tentativas existente. Se as regiões conferem, mas o hash completo declarado não confere, o erro é apresentado sem um loop infinito. O destino anterior fica intacto.

Depois da validação: flush, marca de finalização persistida, fechamento do handle e `File.Move` no mesmo volume. Não há cópia. Falha de renomeação mantém o parcial validado. Se o processo parar após renomear e antes de concluir o checkpoint, a retomada verifica caminho, identidade, tamanho e SHA-256 do definitivo e pode concluir sem rede.

`VerifiedPayloadSizeMismatchException` continua entregando ao diálogo existente um arquivo integralmente autenticado quando apenas o tamanho do metadata diverge. Não há padding ou truncamento para forçar esse tamanho.

A verificação de disco usa tamanho lógico recuperado e desconta armazenamento efetivamente alocado via `GetCompressedFileSizeW`, não apenas `FileInfo.Length`. FAT mantém a limitação de 4 GiB; exFAT não é confundido com FAT32. Não se pré-aloca todo o arquivo nem se exige espaço para outra montagem. A alocação/zeragem de lacunas ainda depende do sistema de arquivos.

Na sincronização, o parcial e o arquivo técnico validado ficam na pasta local vinculada, com prefixo já excluído pelo scanner. A entrega ao staging existente é uma renomeação no mesmo volume. Revisões, conflitos, validações e backup de substituição permanecem no motor de sincronização.

## Progresso e testes

Rede/gravação: 0–99%; verificação: `Verificando integridade do arquivo...`; término após renomeação: 100%. O booleano legado `Assembling` sinaliza somente a fase final aos consumidores existentes. `RateBytes` separa tráfego novo de bytes recuperados e não inventa tráfego durante verificação.

As regressões exercitam os serviços, HTTP simulado, arquivos reais e checkpoints JSON: ordem 3/1/4/0/2, cifrado e legado, retomada, corrupção, truncamento, ausência de parcial, checkpoint atrasado, interrupção durante gravação, autenticação inválida, migração, cancelamento, duplicação, destino bloqueado, renomeação já concluída, admissão de RAM e endereçamento lógico de 150 GB. Há regressões existentes para multi-attachment, batch, checksum, sincronização e painel de retomada em WebView2 oculto.

Não equivalem a uma transferência real de 150 GB, a desligamento elétrico real, disco fisicamente cheio/defeituoso, benchmark de desgaste do SSD ou ensaio em todo sistema de arquivos. Flush reduz a janela de perda, mas hardware e sistema operacional precisam honrar a persistência.

APIs: [RandomAccess](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess?view=net-10.0) e [GetCompressedFileSizeW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew).

## Arquivos envolvidos

- Adicionados em `Organessonium/Organessonium/Services/`: `DownloadFileLayout.cs`, `PartialDownloadFile.cs`, `ForumDownloader.Direct.cs` e `TransferManager.Downloads.cs`.
- Adaptados: `Services/ForumDownloader.cs`, `TransferManager.cs`, `TransferStateRepository.cs`, `Services/Sync/CloudSyncRepository.cs`, `Models/TransferState.cs` e somente os callbacks/erros de download em `Bridge/AppController.cs`.
- Removido: `Services/DownloadAssembler.cs`, sem consumidores remanescentes.
- Testes: novo `tests/Organessonium.NetworkTests/DirectDownloadTests.cs`; ajustes de cenários reais em `ChecksumTests.cs`, `MultiAttachmentTests.cs`, `DesktopRegressionTests.cs` e registro em `Program.cs`.
- Documentação: este documento, `README.md` e a compatibilidade `DOWNLOAD-DIRECT-001` em `migration.md`.

Validação desta implementação: 15 testes relacionados aprovados, incluindo o painel oculto com retomada, métricas e erro recuperável visível. A solução Release compilou sem erros ou avisos. O projeto de testes reutiliza a inicialização do Windows App SDK presente no assembly do aplicativo, sem gerar inicializadores duplicados nem suprimir CS0436; compila sem avisos.
