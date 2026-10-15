# Organessonium Development Compatibility / Migration

Inventário técnico compartilhado do desktop C# e do cliente Flutter durante desenvolvimento. Não é autorização para remover funcionalidades. Código atual é a referência; nenhum item abaixo foi removido nesta tarefa. Ao adicionar fallback/parser/alias temporário, registrar ID, motivo, comportamento, condição e procedimento de remoção aqui.

## Compatibilidades atuais

### DIAGNOSTICS-TEMP-001

Status: ativo, infraestrutura temporária de desenvolvimento.

Descrição: Coletor local opcional iniciado antes da construção do WPF, com rastros JSONL, observação de exceções, requisições, bridge, WebView2, recursos e períodos sem resposta. Não modifica fluxos de negócio ou seus tratamentos de erros.

Introduzido por: Diagnóstico de travamentos sem correções especulativas.

Motivo: Obter evidências técnicas reproduzíveis durante desenvolvimento. Não representa dependência de protocolo remoto ou compatibilidade Flutter.

Código relacionado: DesktopEntryPoint; StartupObject no Organessonium.csproj; Services/Diagnostics; hooks no AppLogger, AppController.HandleJsonMessageAsync/RunAsync, DiscordApiClient.SendAsync, WebMessageBridge e MainWindow; web/diagnostics.js e seção correspondente do index.html.

Comportamento atual: Ativo por padrão no executável desktop normal/background; desativável por ORGANESSONIUM_DIAGNOSTICS=0; auxiliar de mídia não abre outra sessão. Evidências permanecem locais e limitadas, sem segredos/conteúdo. Não há captura de memória ou serviço externo.

Pode ser removido quando: Os travamentos tiverem evidências suficientes e a instrumentação temporária não for mais necessária.

Impacto da remoção: Deixam de existir os rastros e a vigilância adicionais; os fluxos observados e a política anterior de erros continuam funcionando. Não exige alteração dos metadados, cache ou canais Discord.

Procedimento para remoção:

1. Remover StartupObject do csproj e DesktopEntryPoint.cs, retomando o Main gerado pelo WPF.
2. Remover as chamadas DevelopmentDiagnostics/DiagnosticWebView nos pontos de integração mencionados, inclusive o descarte na recuperação/fechamento e o case developmentDiagnostic do host.
3. Remover Services/Diagnostics, web/diagnostics.js, sua tag script e a seção Diagnóstico temporário.
4. Remover somente os três testes diagnostic* e a opção --diagnostic-probe do runner; manter loggingPersistsOnlyErrors e os testes das funcionalidades existentes.
5. Atualizar README e este registro. Não apagar logs antigos sem decisão deliberada.

### LOGGING-LEGACY-001

Status: ativo.

Descrição: Logger anterior de arquivos .log por erro, com filtro de áreas, preservado como fallback quando o coletor temporário não está ativo ou está indisponível.

Introduzido por: Anterior ao coletor temporário; preservado nesta implementação para permitir sua remoção sem depender dele.

Motivo: Não remover a política existente de logs enquanto o sistema temporário é avaliado.

Código relacionado: AppLogger.Error/Write e loggingPersistsOnlyErrors. Coletor ativo intercepta erros antes do filtro e usa JSONL sanitizado; o fallback conserva formato/política existentes. Não há leitura automática ou migração dos .log antigos.

Pode ser removido quando: Houver decisão explícita sobre o logger definitivo, independente da remoção do coletor temporário.

Impacto da remoção: Sem outra implementação, o app ficaria sem logs de erro quando a coleta estiver desativada ou indisponível.

Procedimento para remoção: Escolher/validar um logger definitivo, substituir somente o fallback, atualizar seu teste e a documentação; não apagar os .log existentes. O formato legado pode conter mensagens/caminhos e não deve ser tratado como automaticamente seguro para publicação.

### METADATA-LEGACY-001

Status: ativo

Descrição: JSON plaintext schemaVersion 2, inline ou metadata.json.

Introduzido por: Encrypted metadata V1.

Motivo: Abrir dados de desenvolvimento anteriores.

Código relacionado: MetadataCodec.Decode; MetadataService.ReadJsonPayloadAsync/ReadEnvelopeAsync.

Flutter: MetadataCodec.decode e MetadataService.readJsonMessage leem JSON legado e escrevem somente envelope V1, sem migração na leitura.

Comportamento atual: Leitura automática; escrita remota nova somente cifrada; migração apenas ao modificar e salvar.

Pode ser removido quando: Todos os registros antigos puderem ser descartados ou tiverem migração confirmada.

Impacto da remoção: Entradas plaintext deixam de abrir.

Procedimento para remoção: Remover branch JSON legado do router; exigir envelope nos leitores; remover somente fixtures legadas; manter validação de versão/autenticação; atualizar a especificação criptográfica em implementation old.md e os testes.

### SERIAL-LEGACY-001

Status: ativo

Descrição: Anexos e caches OSER sem envelope criptografado.

Introduzido por: Encrypted metadata V1.

Motivo: Ler seriais existentes sem republicar ao abrir.

Código relacionado: SerialMetadataTransport.Decode; SerialIndexService.ReadCachedFolderAsync/LoadFolderAsync/LoadStandaloneSerialAsync.

Flutter: SerialMetadataTransport.decode e FolderSerialRepository conservam bytes/hash de OSER legado sem converter ao abrir; SerialIndexService reutiliza bytes quando o conteúdo não mudou.

Comportamento atual: OSER bruto é lido; novas gravações usam folderSerialData cifrado. Conteúdo inalterado conserva transporte/digest anterior.

Pode ser removido quando: Seriais antigos da conta e dos caches tiverem sido substituídos ou descartados deliberadamente.

Impacto da remoção: Abertura de bibliotecas antigas perderá o fast-path; não confundir erro com pasta vazia.

Procedimento para remoção: Remover detecção OSER bruto apenas no transporte; manter codec binário interno; ajustar fixtures/cache e erro explícito; não apagar attachments antes de confirmar novos.

### SERIAL-LEGACY-002

Status: ativo

Descrição: Leitura OSER v1, v2 e v4 além do v3 publicado.

Introduzido por: Anterior à tarefa.

Motivo: V1 não traz dispositivos/encryption; v2 traz dispositivos; v4 experimental traz tamanho individual.

Código relacionado: FolderSerialCodec.DecodeFolder/ReadEncryption; CurrentVersion e PublishedVersion.

Flutter: FolderSerialCodec lê v1-v4 e publica v3. V3 não carrega tamanho individual; mutações incrementais recebem metadados completos conhecidos para ajustar os agregados.

Comportamento atual: Publicação interna v3; leitura v1-v4. Versões futuras desconhecidas falham.

Pode ser removido quando: Nenhum serial dessas versões continuar necessário.

Impacto da remoção: Dados antigos/experimentais serão recusados.

Procedimento para remoção: Restringir versões no codec; remover branches por versão e fixtures correspondentes; coordenar os leitores de todas as plataformas; não mudar silenciosamente v3.

### METADATA-LEGACY-002

Status: ativo

Descrição: parentFolderId ausente/null e pais antigos inconsistentes nos seriais.

Introduzido por: Anterior à tarefa.

Motivo: Dados anteriores não expressavam completamente a hierarquia.

Código relacionado: MetadataService.ToFolderEntries/NormalizeParentId/NormalizeFolderParentId; SerialIndexService.MaterializeEntries; AppRegistry.NormalizeFolderId.

Flutter: MetadataEntry e materialização dos seriais conservam normalização do pai legado; metadados novos mantêm schemaVersion 2.

Comportamento atual: Thread de índice define associação direta; raiz normalizada; favoritos podem preservar pai original.

Pode ser removido quando: Todos os metadados/seriais necessários tiverem relação explícita correta.

Impacto da remoção: Itens podem sumir da árvore ou ir para raiz se remoção for incompleta.

Procedimento para remoção: Migrar relações por leitura autoritativa; exigir pai no schema; retirar normalizações somente após testes de raiz, favoritos, dispositivos e lixeira.

### METADATA-LEGACY-003

Status: ativo

Descrição: Metadados diretos em names e busca de detalhes por localização alternativa.

Introduzido por: Anterior à tarefa.

Motivo: Galerias/dados produzidos antes dos índices hierárquicos atuais.

Código relacionado: AppRegistry.LoadFileDetailsAsync/ReadStandaloneMetadataEntriesAsync/SetFavoriteAsync; MetadataService.ReadEnvelopeAsync.

Flutter: DiscordRestStorageService._readTrashMetadata conserva a localização alternativa names para referências antigas e valida identidade. Alterações preservam extensões do JSON remoto e não fazem fallback em falhas criptográficas.

Comportamento atual: Lê names ou índice do pai e valida identidade; favorito pode editar o registro names existente, agora cifrado.

Pode ser removido quando: Dados dependentes de names estiverem migrados para índices e seriais confirmados.

Impacto da remoção: Galerias/arquivos referenciados por seriais antigos podem deixar de abrir.

Procedimento para remoção: Inventariar referências namesMessageId; mover registros preservando payloads; confirmar destinos/seriais; retirar fallback names sem tocar em falhas criptográficas.

### SERIAL-LEGACY-003

Status: ativo

Descrição: Caption folderSerial plaintext e folderName opcional/truncado.

Introduzido por: Anterior à tarefa; leitura cifrada adicionada agora.

Motivo: Localizar seriais antigos pelos IDs técnicos e digest.

Código relacionado: SerialMessageEnvelope.TryParse/Encode/FolderNameForMessage; SerialIndexService.

Flutter: A REST autentica a legenda antes de SerialMessageEnvelope.tryParse. Publicações novas omitem folderName e cifram a legenda; o checksum cobre o anexo transportado completo.

Comportamento atual: Caption antigo é aceito; caption publicado é cifrado, sem nome redundante fora do anexo.

Pode ser removido quando: Captions plaintext antigos não forem mais necessários.

Impacto da remoção: Favoritos/dispositivos/pastas antigos não terão serial localizado.

Procedimento para remoção: Exigir envelope no parser de captions; retirar helper de truncamento quando todos os chamadores forem atualizados; manter busca por mensagem/digest.

### DEVICES-LEGACY-001

Status: ativo

Descrição: Reconstrução de galerias/seriais globais por metadados names; raiz técnica sem mapping completo.

Introduzido por: Anterior à tarefa.

Motivo: Dispositivos existentes podem depender apenas de synchronized/favorites no canal serial.

Código relacionado: AppRegistry.LoadSynchronizedDevicesAsync/RebuildSynchronizedSerialsAsync/LoadSynchronizedDevicesFromSerialAsync; SerialIndexService.LoadStandaloneSerialAsync.

Flutter: A árvore synchronized e os índices existentes passam pelo mesmo transporte autenticado; não se inventa migração por nome de post.

Comportamento atual: Preserva galerias e índices globais; evita publicar synchronized vazio por ausência de metadados antigos.

Pode ser removido quando: Dispositivos antigos tiverem índices e mappings confirmados.

Impacto da remoção: Galerias podem desaparecer ou ser substituídas por índice vazio.

Procedimento para remoção: Migrar dispositivo por dispositivo, confirmar filhos e agregados; retirar somente reconstrução names legada, NÃO as abas globais que continuam sendo funcionalidades atuais.

### PAYLOAD-LEGACY-001

Status: ativo

Descrição: Arquivo/preview sem encryption e transferência já parcialmente enviada sem cifra.

Introduzido por: Anterior à tarefa.

Motivo: Não recriptografar conteúdo antigo nem misturar chunks no meio de uma retomada.

Código relacionado: ForumDownloader (entry.Encryption ausente); AppController.UploadSingleAsync (transferState.Encryption ausente); UploadMemoryCoordinator.PreparePlainPartAsync; VideoChunkIndex/DiscordVideoChunkProvider.

Flutter: Download e streaming conservam payloads sem encryption e ORGAESGC v1; a mudança do envelope de metadados não recriptografa partes existentes.

Comportamento atual: Leitura mantém caminho plaintext de conteúdo; retomada antiga pode completar conteúdo com mesmo layout. Metadados/rótulos novos são cifrados.

Pode ser removido quando: Não houver payloads plaintext necessários nem checkpoints antigos recuperáveis.

Impacto da remoção: Downloads/previews antigos e retomadas podem falhar.

Procedimento para remoção: Migrar conteúdo explicitamente fora desta tarefa, confirmar hashes/refs, limpar checkpoints obsoletos autorizados; retirar branches plaintext só então.

### PAYLOAD-LEGACY-002

Status: ativo

Descrição: Rótulos textuais file:... role:... chunk:... encryption:v1, batch/Parte e ORG_PREVIEW.

As retomadas de prévia plaintext geradas em RAM preservam `ORG_PREVIEW|video-480|batch:...|Parte n/total`, inclusive para uma única parte. `ForumUploader.UploadResumableGeneratedAsync` recebe `legacyPreviewKind` somente nesse caminho; não permite combiná-lo com criptografia ou transporte agrupado. Isso evita identificar uma prévia como conteúdo original ao reconstruir mensagens legadas.

Introduzido por: Leitores anteriores; payloadReference cifrado nesta tarefa.

Motivo: Ler mensagens antigas sem alterar os bytes ORGAESGC.

Código relacionado: MetadataService.DecodePayloadReference; ForumDownloader.GetPayloadMessagesAsync/GetPayloadMessageAsync/DecodeReference; EncryptedPayloadProtocol.TryParse.

Flutter: A REST autentica payloadReference e entrega o content original aos leitores de partes. Rótulos plaintext anteriores permanecem legíveis.

Comportamento atual: Mensagem nova encapsula o rótulo em JSON cifrado; texto antigo segue para parsers atuais.

Pode ser removido quando: Todas as referências relevantes estiverem no transporte novo.

Impacto da remoção: Resolução de previews/lotes antigos pode parar.

Procedimento para remoção: Retirar passthrough textual nos adaptadores, não AES dos chunks; remover parsers antigos apenas após inventário de mensagens e testes de ordem/papéis.

### PAYLOAD-LEGACY-003

Status: ativo

Descrição: Descoberta de chunks por batch, padrões Parte e attachments sem marca de partes.

Introduzido por: Anterior à tarefa.

Motivo: Metadados antigos não traziam IDs explícitos de todas as mensagens.

Código relacionado: ForumDownloader.CollectPartsAsync/TryReadPartInfo/ResolveMessagePartsAsync e overload DownloadAndAssembleAsync(threadId,...).

Flutter: A descoberta histórica por batch/partes permanece no backend; novas labels são cifradas sem mudar identidade e ordenação das partes.

Comportamento atual: Pode reconstruir lote antigo; metadados cifrados de conteúdo exigem contexto e referências apropriadas.

Pode ser removido quando: Todos os arquivos necessários tiverem chunks explícitos validados.

Impacto da remoção: Arquivos antigos sem lista de chunks deixarão de baixar.

Procedimento para remoção: Recompor listas por identidade/lote completo, validar conteúdo e atualizar metadata; retirar regex/último fallback de attachments somente depois.

### PREVIEW-LEGACY-001

Status: ativo

Descrição: Marcadores PREVIEW| e VIDEO_PREVIEW_480P| e layouts alternativos de preview.

Introduzido por: Anterior à tarefa.

Motivo: Dados de outras versões de desenvolvimento usam marcadores diferentes.

Código relacionado: ForumDownloader: DartPreviewPrefix, DartVideoPreviewPrefix, IsPreviewMessage, CollectPreviewPartsAsync.

Flutter: Os leitores de labels e a hidratação de previews mantêm os aliases existentes após autenticação, sem escrever novos prefixos legados.

Comportamento atual: Lê marcadores anteriores; transportes novos podem cifrar seus rótulos antes da mesma interpretação.

Pode ser removido quando: Nenhum arquivo necessário depender dessas mensagens.

Impacto da remoção: Thumbnails/480p antigos podem sumir, ou preview ser confundido com original.

Procedimento para remoção: Mapear referências para preview.thumbnailMessageId/video480p.chunks, validar papéis/tamanho, remover cada alias e sua fixture; preservar fallback original quando não há 480p.

### PREVIEW-LEGACY-002

Status: ativo

Descrição: videoPreviewMessageId único versus videoPreviewChunkMessageIds.

Introduzido por: Anterior à tarefa.

Motivo: Previews antigos cabiam em uma mensagem ou cache antigo só guardava o primeiro ID.

Código relacionado: MetadataService.CreatePreviewMetadata; SettingsStore.ReadCachedEntries; AppEntry campos de preview; ForumDownloader.

Flutter: MetadataService.fileMetadataFromFile normaliza o vídeo single-message para chunks; metadados existentes e histórico continuam legíveis.

Comportamento atual: Reconstrói lista quando há ID único; novo metadata usa preview.video480p.chunks e tamanho quando conhecidos.

Pode ser removido quando: Caches/metadados necessários tiverem lista completa e tamanho.

Impacto da remoção: Prévia 480p antiga pode parar de resolver.

Procedimento para remoção: Migrar referência única para lista validada, atualizar cache, remover alias singular sem remover thumbnailMessageId ou referências originais.

### CACHE-LEGACY-001

Status: verificar antes da remoção

Descrição: Caminho LegacyThumbnailFile e invalidação de cache com ORGAESGC.

Introduzido por: Anterior à tarefa.

Motivo: Há helpers de thumbnail antigo e proteção contra cache contendo ciphertext em vez de imagem.

Código relacionado: ThumbnailCache.Get/Remove/LegacyThumbnailFile; SettingsStore.GetCachedPreviewFile/IsEncryptedPreviewCache.

Comportamento atual: Get possui fallback por null; conferir existência real antes de afirmar que todo arquivo antigo é recuperado. Remove conhece ambos. Cache ciphertext é invalidado.

Pode ser removido quando: Layout histórico não existir mais e a invalidação não for necessária para integridade.

Impacto da remoção: Miniaturas locais podem não abrir ou ciphertext pode ir para decoder de imagem.

Procedimento para remoção: Reproduzir cache antigo isolado; identificar layout exato; retirar somente caminho legado. Não remover validação de segurança sem justificativa.

### SYNC-LEGACY-001

Status: ativo

Descrição: syncRevision plaintext inline/anexo e syncApplied:<revisionId> textual.

Introduzido por: Diário anterior; transporte V1 agora.

Motivo: Manter pastas sincronizadas e checkpoints existentes.

Código relacionado: CloudSyncRepository.ReadAsync/ReadRevisionAsync/ReadRevisionDocument; MetadataService.ReadJsonAsync.

Flutter: O codec entende JSON autenticado, mas o cliente móvel usa backup de galeria e não implementa o diário de sincronização de diretórios do desktop. Nenhum protocolo de revisão novo foi criado.

Comportamento atual: Revisões antigas são decodificadas como legado; marcador textual é lido; novas revisões/confirmações são cifradas.

Pode ser removido quando: Diários necessários tiverem commits/markers cifrados ou dados de desenvolvimento puderem ser descartados.

Impacto da remoção: Reconciliação e reparo de projeções antigas podem falhar.

Procedimento para remoção: Migrar cada diário com validação de heads/parents e projeções; retirar marcador textual e leitura plaintext sem apagar payloads atuais/bases de conflito.

### CONFIG-LEGACY-001

Status: verificar antes da remoção

Descrição: Migrações aditivas SQLite e colunas/valores históricos de cache.

Introduzido por: Anterior à tarefa.

Motivo: Instalações antigas podem ter tabelas sem campos novos ou listas/propriedades incompletas.

Código relacionado: SettingsStore.EnsureColumn/ReadCachedEntries/DeserializeCachedList/Load; TransferState.Normalize e TransferStateRepository.

Comportamento atual: Adiciona colunas; aceita valores ausentes com defaults; reconstrói IDs de preview; listas inválidas podem virar vazias no cache. Não é fallback de metadata criptografado.

Pode ser removido quando: Uma versão mínima de banco/config/checkpoint for definida e arquivos antigos forem descartáveis/migrados.

Impacto da remoção: Instalações antigas podem não iniciar ou perder estados recuperáveis.

Procedimento para remoção: Definir schema mínimo e exportação/backup de estados; migrar SQLite/checkpoints; substituir defaults temporários por validação explícita. Não retirar settings atuais por serem opcionais.

### MODEL-LEGACY-001

Status: verificar antes da remoção

Descrição: IDs sintéticos folder_, geração de IDs ausentes, MIME inferido e descritor de pasta por caminho.

Introduzido por: Anterior à tarefa.

Motivo: Pode servir tanto DTO antigo quanto construção corrente de objetos da UI.

Código relacionado: AppEntry.Normalize/File/Folder/GuessMimeType; MetadataService.ToAppEntry; AppRegistry.ResolveFolderDescriptor.

Comportamento atual: Defaults/fallbacks permanecem; metadata público novo exige IDs reais dos objetos e mapping de índice.

Pode ser removido quando: Todos os chamadores correntes forem diferenciados de leitores legados.

Impacto da remoção: Criação de pasta/navegação pode quebrar, não apenas dados antigos.

Procedimento para remoção: Identificar origem de cada DTO, não apagar helpers cegamente; separar construção normal de reparo legado; exigir IDs nos loaders de cache depois da migração.

### ENCRYPTION-LEGACY-001

Status: verificar antes da remoção

Descrição: encryption.keyId opcional e slot correspondente em OSER custom.

Introduzido por: Anterior à tarefa.

Motivo: Model/clone/binário ainda preservam esse campo, mas a derivação atual usa M, fileId e salt.

Código relacionado: EncryptionMetadata.KeyId/Clone; FolderSerialCodec.ReadEncryption/WriteEncryption; MetadataService.

Flutter: EncryptionMetadata aceita custom sem keyId e usa a master ativa. Um keyId explícito ainda exige a chave histórica correspondente; não há tentativa silenciosa com outra chave.

Comportamento atual: Campo é preservado quando existe; sem decisão de remover slot binário.

Pode ser removido quando: Semântica histórica e consumidores externos forem conhecidos.

Impacto da remoção: Remover bytes no meio do OSER pode deslocar o restante do serial.

Procedimento para remoção: Verificar fixtures/clientes e autoria histórica; versionar OSER se mudar layout; só então retirar a propriedade e suporte.

### BRIDGE-LEGACY-001

Status: ativo

Descrição: folderId escalar em lugar de JSON array para estatísticas.

Introduzido por: Anterior à tarefa; comentário explícito de WebView antiga.

Motivo: Suportar conteúdo web antigo/cacheado durante atualização.

Código relacionado: AppController.ParseFolderIds e tratamento de JsonException.

Comportamento atual: Aceita array atual e um ID escalar de até 128 caracteres.

Pode ser removido quando: Nenhuma UI/cache antigo enviar formato escalar.

Impacto da remoção: Chamadas antigas deixam de obter resultado; ainda não é motivo para mudar contagem/UI nesta tarefa.

Procedimento para remoção: Atualizar todos os chamadores e política de cache WebView, remover branch escalar, manter testes do contrato array.

### MARKDOWN-COMPAT-001

Status: verificar antes da remoção

Descrição: URI organessonium-image://v1 e validação de salt com API Base64 distinta do codec de conteúdo.

Introduzido por: Anterior à tarefa.

Motivo: Links Markdown precisam preservar referências e material de contexto de imagens existentes.

Código relacionado: MarkdownEncryptedImageReference.ToUrl/TryParse; AppController preview de imagens Markdown.

Comportamento atual: Formato foi mantido. TryParse usa Convert.FromBase64String sobre salt recuperado, enquanto novo conteúdo usa Base64URL sem padding; exige verificação específica de links.

Pode ser removido quando: Todos os links necessários tiverem formato validado e houver decisão de protocolo.

Impacto da remoção: Imagens embutidas podem não resolver; não presumir que remover ou trocar parser é seguro.

Procedimento para remoção: Inventariar URLs de teste com salt URL-safe, validar contra parser/chaves de conteúdo, planejar correção/versionamento separado; registrar nova compatibilidade se adicionar fallback.

### ENCRYPTION-BOOTSTRAP-001

Status: verificar antes da remoção

Descrição: Master key standard embutida e recuperação custom OEK1.

Introduzido por: Anterior à tarefa.

Motivo: Compatibilidade entre instalações e acesso a conteúdo existente.

Código relacionado: DefaultEncryptionKeyProvider.GetMasterKey; CustomEncryptionKeyProvider.ConfigureFromPassphrase/ImportAsync/ExportAsync; MetadataCodec.

Flutter: StandardMasterKeyProvider conserva o bootstrap exato. Novas masters usam PBKDF2 do desktop; mestres antigos derivados por Argon2id permanecem armazenados. OEK1 é escrito/importado; ORGKEY antigo continua somente na importação, sem rederivação.

Comportamento atual: Metadata reutiliza os mesmos provedores com domínio HKDF distinto. Standard não oferece segredo exclusivo; OEK1 contém M.

Pode ser removido quando: Nova política de chave e recuperação estiver definida e dados dependentes tiverem plano de migração.

Impacto da remoção: Trocar/remover M quebra arquivos e metadados standard/custom antigos.

Procedimento para remoção: Não alterar constantes ou KDF implicitamente; versionar seleção/bootstrap/recovery, garantir exportação autorizada e acesso a dados anteriores antes de substituir.

### INDEX-LEGACY-001

Status: ativo no Flutter

Descrição: Manifesto agregado folderIndex schemaVersion 1 publicado por versões móveis anteriores.

Motivo: Preservar a localização de pastas existentes sem migrar ao abrir.

Código relacionado: DiscordRestStorageService._readFolderIndex/_mutateFolderIndex; FolderIndexManifest.fromJson.

Comportamento atual: Leitura legada; uma alteração efetiva publica mappings v2 cifrados antes de retirar o manifesto. Não escreve um novo manifesto v1 e não transforma a navegação em varredura global de arquivos.

Pode ser removido quando: Todos os servidores dependentes tiverem mappings v2 confirmados.

Impacto da remoção: Índices móveis antigos deixam de localizar seus threads.

Procedimento para remoção: Confirmar a conversão dos mappings sem remover payloads/seriais; retirar somente o parser e o ramo de conversão explícita, manter erros criptográficos e testes de mappings v2.

### PAYLOAD-LEGACY-004

Status: ativo no desktop; suporte multi-attachment mobile a implementar

Descrição: single attachment per message / chunks string[]. Metadata original e `preview.video480p` antigos listam apenas IDs de mensagens, um chunk por mensagem. Novos uploads desktop usam `chunkRefs: [{ index, messageId, attachmentId }]`, com índices zero-based e vários attachments por mensagem.

Introduzido por: Multi-attachment payload transport. A leitura e a retomada unitárias já existiam e foram preservadas.

Motivo: Arquivos e transferências anteriores não possuem IDs explícitos de attachment. Reescrever sua lista ou inserir messageIds repetidos não produz compatibilidade real. Clientes sem chunkRefs ainda precisam de atualização coordenada.

Código relacionado:

- Models/ChunkReference, MetadataModels, AppEntry e TransferState.PayloadLayoutVersion.
- MetadataService.Parse/ToAppEntry/CreateFileMetadata/CreatePreviewMetadata.
- ForumUploader.UploadResumableGeneratedAsync/UploadResumableAsync e ForumUploader.MultiAttachment.
- ForumDownloader.ResolveMessagePartsAsync, resolução de original/video480p, download e streaming.
- AppController upload/resume/Markdown, CloudSyncRepository e DiscordVideoChunkProvider.

Comportamento atual:

- Leitura: `chunkRefs` presente é autoritativo e estritamente validado; ausente usa `chunks` unitário legado. Declaração inválida não cai no legado.
- Escrita: uploads novos usam somente `chunkRefs`; arquivos antigos modificados sem reupload preservam sua lista `chunks`.
- Preview: a mesma distinção aplica-se independentemente a `preview.video480p`; thumbnail permanece unitário.
- Payload plaintext: sem `encryption`, conserva o caminho antigo de leitura, registrado em PAYLOAD-LEGACY-001.
- Migração: nenhuma regravação de payload ao ler, baixar, renomear, mover ou favoritar. O envelope cifrado de metadata pode ser atualizado sem converter transporte.

Pode ser removido quando: Dados unitários necessários tiverem referências completas confirmadas ou puderem ser descartados, todos os checkpoints antigos forem concluídos e a versão mínima dos clientes suportar chunkRefs.

Impacto da remoção: Arquivos/previews antigos deixam de localizar chunks; leitores antigos não abrem uploads novos. Metadados cifrados não garantem compatibilidade de transporte.

Procedimento para remoção: Inventariar mensagens unitárias e checkpoints sem expor dados privados; planejar conversão explícita com IDs reais e sem reenviar conteúdo; confirmar acesso antes de remover parsers/resolvers antigos; retirar fixtures unitárias apenas depois dessa decisão; atualizar implementation.md e a versão mínima dos clientes. Não alterar ORGAESGC, encrypted metadata V1 ou OSER para essa remoção.

### TRANSFER-LEGACY-001

Status: ativo

Descrição: Checkpoints anteriores sem `payloadLayoutVersion` ou sem `attachmentId` continuam com layout 1, uma mensagem por chunk. O layout 2 novo confirma messageId e attachmentId juntos.

Introduzido por: Multi-attachment payload transport.

Motivo: Evitar continuar uma transferência parcialmente enviada em outro layout ou recalcular seus índices usando o limite atual.

Código relacionado: TransferState/TransferChunkState.Normalize; TransferManager.CreateUpload/MarkUploadChunkAsync/MarkUploadChunksAsync; TransferStateRepository; AppController e CloudSyncRepository retomada.

Comportamento atual: Propriedade ausente recebe layout 1. Layouts desconhecidos falham. Retomadas antigas preservam thread, chunks confirmados, tamanho lógico e quantidade global registrados; não fabricam attachmentIds. Novas transferências usam layout 2. O estado conserva também videoPreview480pChunks legado e videoPreview480pChunkRefs novo.

Pode ser removido quando: Não houver transferências recuperáveis no layout 1 e a política de upgrades permitir descartar checkpoints anteriores.

Impacto da remoção: Transferências antigas podem precisar reiniciar o upload e perder a identificação dos chunks já confirmados.

Procedimento para remoção: Concluir ou migrar checkpoints com verificação dos anexos remotos; definir schema mínimo; retirar default e ramo unitário; preservar a atomicidade por batch e testes de retomada do layout 2.

### CACHE-LEGACY-002

Status: ativo

Descrição: Bancos app_entries anteriores ao agrupamento não têm `chunk_refs_json` e `video_preview_chunk_refs_json`.

Introduzido por: Multi-attachment payload transport.

Motivo: Acrescentar referências completas sem apagar o cache existente ou impedir uma instalação anterior de iniciar.

Código relacionado: SettingsStore.EnsureColumn/ReadCachedEntries/cache SQL; AppEntry.Normalize; AppRegistry.CopyEntryState; SerialIndexService.CopyEntry.

Comportamento atual: Migração aditiva de duas colunas opcionais. NULL indica referências não disponíveis; listas unitárias existentes continuam válidas. Serial OSER continua sem lista detalhada de chunks, e a hidratação busca o metadata completo quando necessário. Não reconstruir referências novas apenas com messageIds.

Pode ser removido quando: Houver schema mínimo de SQLite definido e migração explícita dos bancos anteriores.

Impacto da remoção: Bancos antigos deixam de abrir ou perdem detalhes necessários ao download de arquivos já armazenados.

Procedimento para remoção: Definir upgrade de schema, conservar registros existentes e testar reabertura; retirar apenas o suporte ao banco antigo, não a ausência válida de detalhes em um card vindo do serial.

### PAYLOAD-COMPAT-001

Status: verificar antes da remoção

Descrição: Redução automática do tamanho do batch quando Discord rejeita quantidade/tamanho de attachments. Não é parser legado nem migração de dados.

Introduzido por: Multi-attachment payload transport.

Motivo: A capacidade observada de dez anexos de 20 MiB pode não ser aceita por toda rota/conta futura; a falha de limite não deve obrigar trocar a cifra ou o tamanho lógico dos chunks.

Código relacionado: ForumUploader.MultiAttachment.SendBatchAsync/IsAttachmentLimitRejection/HasLimitCode; DiscordApiClient.SendFilesMessageAsync.

Comportamento atual: HTTP 413, code 40005 ou erros específicos de tamanho/quantidade em HTTP 400/code 50035 reduzem o agrupamento pela metade, até um attachment, reutilizando o ciphertext e mantendo chunkRefs. Outros erros 400 não recebem retry de redução. O teto aprendido dura na instância do uploader. Rate limits/429 continuam independentes.

Pode ser removido quando: Uma decisão explícita de produto exigir falha imediata para qualquer rejeição de agrupamento e a capacidade de todas as rotas suportadas estiver verificada. Não presumir que robustez operacional seja dívida removível.

Impacto da remoção: Uma conta/rota com teto menor pode falhar em vez de continuar o upload.

Procedimento para remoção: Validar capacidades reais e alternativa de recuperação; manter retries de rede/429, tamanho individual, abertura de streams e checkpoints; retirar somente a divisão de batches e o teste correspondente após a decisão.

### TRANSFER-METRICS-COMPAT-001

Status: verificar antes da remoção

Descrição: `_opsTransferMetrics` continua aceitando o contrato anterior de sete argumentos. O oitavo, `rateBytes`, separa transmissão real e retries do progresso lógico único do upload.

Introduzido por: Upload limitado em RAM com progresso durante o corpo HTTP.

Motivo: Preservar chamadas existentes, inclusive downloads, que ainda enviam um único contador de bytes. Essa ausência pode ser comportamento atual válido, não dívida removível automaticamente.

Código relacionado: `web/app.js` (`_opsTransferMetrics`); `Bridge/AppController.cs` (`OpsTransferMetricsAsync`); `Services/UploadByteProgress.cs`, `UploadWireProgressStream.cs`.

Comportamento atual: `rateBytes` ausente/null usa `transferredBytes` para velocidade. Uploads com contador separado mantêm progresso lógico monotônico e contam retransmissões somente na vazão. Nenhum formato de metadata, payload ou checkpoint foi alterado.

Pode ser removido quando: Todos os produtores de métricas enviarem um contador de transmissão explícito, inclusive fluxos de download e retomada, ou o contrato opcional deixar de ser necessário.

Impacto da remoção: Chamadas de sete argumentos podem perder velocidade/estimativa. Não confundir esse fallback com downgrade de dados criptografados.

Procedimento para remoção: Inventariar as chamadas C#/JS, atualizar produtores e testes do painel; depois remover somente o fallback do oitavo argumento e atualizar a documentação.

## Compatibilidades do histórico de arquivos

### FILE-VERSION-LEGACY-001

Status: ativo

Descrição: metadados anteriores à extensão de versões não possuem `versionId`, `versionUploadedAt`, `modifiedAt` ou `versions`. A ausência de histórico é lida como lista vazia; as datas ausentes usam `uploadedAt`. Ao substituir efetivamente um conteúdo antigo, o snapshot recebe um ID opaco `legacy-` derivado de seu JSON e o conteúdo novo recebe o ID do checkpoint. Leitura isolada não publica migração.

Introduzido por: histórico de arquivos e informações no desktop.

Código relacionado: `Models/MetadataModels.cs`, `Services/MetadataService.cs` (`ToAppEntry`), `Services/FileVersioning.cs` (`PreservePrevious`), `Bridge/AppController.Files.cs`.

Comportamento atual: leitura de arquivos sem histórico, `chunks` legados ou sem criptografia é preservada; o snapshot mantém as referências/criptografia daquele payload. Escritores atualizados precisam preservar o histórico ao alterar o arquivo. Leitores antigos podem abrir o conteúdo superior, mas não são escritores seguros de metadata com versões.

Pode ser removido quando: todos os arquivos mantidos tiverem identificação/datas de versão e os clientes escritores preservarem o contrato novo.

Impacto da remoção: arquivos antigos podem deixar de abrir ou de criar a primeira versão; datas de alterações antigas não podem ser reconstruídas.

Procedimento: inventariar dados, migrar deliberadamente sem excluir payloads, atualizar os clientes e somente depois retirar fallbacks e cenários legados dos testes.

### CACHE-LEGACY-003

Status: ativo

Descrição: bancos anteriores continuam válidos por adição de `modified_at`, `version_id`, `version_uploaded_at` e `versions_json`, com defaults vazios. OSER v3 permanece sem campos de histórico; o metadata completo é carregado quando necessário.

Código relacionado: `Services/SettingsStore.cs`, `Services/SerialIndexService.cs`, `Services/AppRegistry.cs`.

Comportamento atual: o cache persiste histórico/datas, mas um serial sem detalhes não vira metadata vazio para gravação. Mudanças de salt/thread/thumbnail invalidam detalhes antigos. Remoção futura exige migração do banco/cache e contrato de serial explícito, não exclusão indiscriminada dos dados locais.

### TRANSFER-DOWNLOAD-LEGACY-001

Status: ativo

Descrição: checkpoints novos contêm `downloadMetadata` para fixar o payload durante retomada após uma substituição. Checkpoints antigos sem esse snapshot mantêm o fluxo de resolução da listagem atual.

Código relacionado: `Models/TransferState.cs`, `Services/TransferManager.cs` (`CreateDownloadAsync`), `Bridge/AppController.cs` (`ResumeInterruptedTransfersAsync`).

Pode ser removido quando: não existirem downloads interrompidos de versões anteriores do desktop. Impacto: estados antigos sem snapshot deixarão de retomar; atualizar testes e documentação junto da remoção.

### TRANSFER-FASTSTART-LEGACY-001

Status: ativo

Descrição: checkpoints anteriores podem registrar `fastStartApplied` para um MP4 que era remuxado em arquivo temporário. A retomada agora reconstrói a mesma sequência por leitura virtual, sem reutilizar ou recriar a cópia em disco. Checkpoints parcialmente enviados sem esse flag preservam os bytes originais, sem mudar de layout no meio do upload.

Motivo: preservar índices, tamanho, thread e chunks confirmados em transferências interrompidas antes da eliminação do staging de mídia.

Código relacionado: `Services/Mp4FastStart.cs` (`PreparedVideoUpload.OpenRead`), `Models/TransferState.cs`, `Bridge/AppController.cs` (`UploadSingleAsync`). Nenhum formato remoto ou algoritmo de criptografia foi alterado.

Comportamento atual: o flag true exige que o faststart possa ser reconstruído; se não for possível, a retomada falha explicitamente e mantém os chunks confirmados. Leituras não regravam objetos remotos. O arquivo de origem deve permanecer disponível e válido.

Pode ser removido quando: não houver checkpoints anteriores recuperáveis e existir política explícita para descartar estados interrompidos.

Impacto da remoção: retomar um upload antigo pode misturar bytes remuxados/originais se o layout não for validado.

Procedimento: concluir ou descartar estados antigos com autorização, verificar os payloads remotos e somente então retirar o branch de compatibilidade, seus testes e esta documentação. O fallback operacional de MP4 não suportado e a falha segura de prévias acima do limite de RAM não são dívida legada removível.

### MEDIA-ENCODER-LEGACY-001

Status: verificar antes da remoção

Descrição: `VideoPreviewWorker` mantém o gerador Windows para miniaturas de imagens e para prévias em instalações sem FFmpeg ou Windows de 32 bits. O caminho 480p legado depende de codecs Windows e mantém a saída seekable em até 128 MiB. Miniaturas usam reserva separada de 96 MiB/8 MiB e não aceitam um ícone genérico como imagem. FFmpeg gera vídeo/MKV em uma passagem, com staging oculto ao lado do original e upload posterior, sem esse teto de saída.

Introduzido por: correção de miniaturas e prévias 480p longas via motor separado.

Código relacionado: `Services/VideoPreviewGenerator.cs`, `VideoPreviewWorker.cs`, `FfmpegMediaService.cs`, `FfmpegPreviewUpload.cs`, `Bridge/AppController.cs` (`TryUploadGeneratedPreviewAsync`).

Comportamento atual: previews remotos existentes, MP4 flat, `chunks` unitários, plaintext antigo e `chunkRefs` permanecem legíveis. Nenhuma entrada é regravada para trocar o encoder. Retomadas antigas podem enviar prévias pelo layout unitário; uploads novos agrupam até dez anexos. ORGAESGC v1 e o metadata não mudam. Novas partes acompanham o limite de 20 MiB; prévias antigas de 2 MiB continuam válidas porque leitores usam tamanhos reais, não o limite atual de upload.

Pode ser removido quando: houver motor de mídia disponível e validado para todas as instalações/arquiteturas suportadas, e a política de distribuição/licenciamento estiver concluída. O gerador Windows de imagens é um caminho operacional útil, não dívida descartável automaticamente.

Impacto/procedimento: remover fallback sem essa decisão impede gerar mídia em instalações antigas/32 bits. Inventariar plataformas, confirmar pacote e codecs, validar miniaturas/prévias e somente então remover os branches/tests correspondentes. Não excluir previews ou conteúdo original remoto.

## Fronteiras e itens que não são dívida automaticamente

- Não foi encontrado protocolo de compartilhamento JSON neste desktop. Não existe evidência para inventar SHARE-LEGACY-001. Exportação OEK1 é recuperação de chave, não share de arquivo.
- Apenas schemaVersion 2 é aceito pelo leitor de metadados comuns. Prefixos APP|/FILE_META| e outros schemas não foram encontrados como leitores suportados; não declarar compatibilidade inexistente.
- favorites, synchronized, trash, sync.canonicalFolderId e árvore hierárquica são funcionalidades atuais. Não removê-los sob o rótulo de legado. Aliases de sincronização representam a mesma pasta lógica em PCs diferentes.
- Tamanhos reais/variáveis de chunks, índices de Range, fallback de MP4 não suportado, limites de memória, retry, falta de codec de mídia e moldura WPF em Windows antigo são comportamentos operacionais, não autorização para simplificar suporte.
- Base64URL sem padding de encryption.salt é contrato atual de conteúdo; Base64 padrão com padding do envelope de metadata é outro contrato. Não adicionar fallback entre eles sem registrar e justificar.
- A ausência de preview/sync/trash/encryption pode ser válida no tipo de entrada. Campos opcionais não são automaticamente legado removível.
- O cache SQLite local continua operacional e pode conter plaintext. Seriais novos usam transporte cifrado; as colunas aditivas para chunkRefs não cifram o restante do banco. CACHE-LEGACY-002 registra essa evolução sem apagar dados.
- Não existe downgrade de envelope declarado: version futura, Base64 inválido, chave/tag incorretas ou JSON interno inválido não seguem METADATA-LEGACY-001.

### STORAGE-V2-LEGACY-001

Status: ativo

Descrição: metadata sem `storage` continua representando dedicado antigo. `chunks` unitário e `chunkRefs` dedicado anterior permanecem legíveis. Mais de um attachment não identifica shared por inferência.

Introduzido por: Shared Storage V2.

Motivo: conservar arquivos de desenvolvimento, sem migrar posts/conteúdos ao ler.

Código relacionado: `StorageLayout`, `MetadataService`, `ForumDownloader`, `SharedStorageManager.Cleanup`, `DiscordApiClient.Payload`.

Comportamento atual: novos conteúdos elegíveis usam marcador V2; snapshots antigos mantêm formato/threads. Dedicado maior que 1 GiB é comportamento permanente, não fallback removível.

Pode ser removido quando: nenhum metadata sem marcador precisar ser lido e migração/descarte tiver autorização.

Impacto: arquivos sem marcador deixam de ser localizados como dedicado.

Procedimento: inventariar e migrar explicitamente os objetos sem storage; remover apenas a inferência de ausência, manter modo dedicated; atualizar decoder/testes/implementation2.md.

### STORAGE-V2-THUMB-001

Status: ativo

Descrição: leitura de `preview.thumbnailMessageId`/`AppEntry.previewMessageId` dos thumbnails unitários antigos. Shared novo exige `thumbnailRef: { messageId, attachmentId }`.

Introduzido por: coexistência de thumbnails anteriores com Shared Storage V2.

Código relacionado: `PreviewMetadata`, `MetadataService.CreatePreviewMetadata`/`ToAppEntry`, `AppController.LoadVisibleThumbnailsAsync`, `ForumDownloader.DownloadMessageAttachmentBytesAsync`.

Comportamento atual: escrever referência exata em shared; conservar formato antigo nos checkpoints/snapshots dedicados. Mais de um attachment sem ID explícito retorna erro, nunca escolhe o primeiro.

Pode ser removido quando: todos os thumbnails antigos tiverem referência exata validada ou os dados forem descartados deliberadamente.

Impacto: previews antigos só com messageId deixam de aparecer.

Procedimento: resolver e validar attachmentIds dos antigos, atualizar metadados por operação confirmada, remover branches unitários de thumbnail; preservar decoder ORGAESGC e role preview.

### STORAGE-V2-RESUME-001

Status: ativo

Descrição: checkpoints unitários layout 1 e multi-attachment dedicado layout 2, incluindo default 1 quando a propriedade está ausente. Shared novo usa layout 3 com storage/reserva explícitos.

Código relacionado: `TransferState.Normalize`, `TransferManager`, `ForumUploader.UploadResumableGeneratedAsync`, `AppController.SharedStorage`, `CloudSyncRepository.UploadAsync`.

Comportamento atual: não promover upload antigo para shared pelo tamanho. `sharedAllocationPending` só é gravado no planejamento novo; reserva pendente pode ser retomada com fileId/transferId originais. Chunks/salts confirmados não são alterados.

Pode ser removido quando: não houver transferência antiga pendente a preservar.

Impacto: uploads unitários/dedicados em andamento não retomam.

Procedimento: concluir/cancelar pendências com autorização, remover somente leitores de checkpoint antigos, manter upload dedicado atual e testes de leitura de arquivos antigos enquanto necessários.

### STORAGE-V2-CACHE-001

Status: ativo

Descrição: bancos anteriores sem `storage_json`/`thumbnail_ref_json` recebem colunas TEXT nullable aditivas. OSER v3 continua uma visão sem storage/refs completos e precisa de hidratação do metadata.

Código relacionado: `SettingsStore.EnsureColumn`/`ReadCachedEntry`, `SerialIndexService.MaterializeEntries`/`CopyEntry`, `AppRegistry.LoadFileDetailsAsync`, `AppEntry.RequestId`/`ThumbnailCacheKey`.

Comportamento atual: ausência de colunas/detalhes não apaga cache válido; novos detalhes preservam layout/IDs. Ponte WebView2 aceita threadId legado somente quando não é ambíguo; novos DTOs fornecem requestId por arquivo. Thumbnails shared usam identidade incluindo attachmentId.

Pode ser removido quando: bancos/cache antigos puderem ser descartados ou migrados explicitamente e nenhuma ponte antiga precisar ser aceita.

Impacto: cache antigo pode requerer reconstrução. Hidratação de OSER é permanente, não remover como se fosse migração temporária.

Procedimento: coordenar versão mínima, migrar bancos, remover branches de colunas/IDs antigos mantendo a PK por mensagem e o contrato OSER publicado.

### STORAGE-V2-VERSION-001

Status: ativo

Descrição: versões antigas sem versionId/storage conservam os fallbacks de `FILE-VERSION-LEGACY-001`; snapshots podem ter layouts e threads diferentes do conteúdo atual.

Código relacionado: `FileVersioning.PrepareReplacement`/`PreservePrevious`, `AppController.Files.PublishFileVersionAsync`, `MetadataService.Validate`, `SharedStorageManager.Payloads`.

Comportamento atual: ID opaco legado só é materializado ao modificar o arquivo; leitura não regrava conta. Nova versão pode cruzar 1 GiB ou mudar de owner/pool, sem mover histórico. GC considera todos os snapshots.

Pode ser removido quando: não restarem snapshots sem identidade explícita.

Impacto: histórico antigo pode ficar inacessível; comparar somente threadId voltaria a confundir versões/arquivos.

Procedimento: migrar identidade dos snapshots autorizados, retirar somente geração de legacy-id, conservar suporte permanente a snapshots entre layouts/threads e contexto criptográfico de cada versão.

### STORAGE-V2-DELETE-001

Status: ativo

Descrição: exclusão por thread inteira continua somente para dedicado/legado sem referências restantes. Shared usa intent/GC/PATCH seletivo. Metadata antigo pode estar diretamente em names, em vez do índice do pai.

Código relacionado: `AppController.DeleteStoredEntryAsync`, `AppRegistry.DeleteEntryAsync`, `SharedStorageManager.LocateMetadataAsync`/`ReadRemoteReferencesAsync`/`CleanAsync`, `DiscordApiClient.DeleteChannelAsync`.

Comportamento atual: encontrar o canal real do metadata e guardar locator antes de removê-lo; conferir índices remotos, inclusive versões/trash/sync antigos sem ledger. Nunca tratar marcador shared/catálogo ilegível como permissão para DELETE de thread. Leituras/claims externos inconclusivos deixam limpeza pendente.

Pode ser removido quando: não houver metadata dedicado legado/standalone sem catálogo/reference a preservar.

Impacto: exclusão dos objetos antigos passa a exigir migração explícita. Remover proteção de threads compartilhadas causaria perda de outros arquivos.

Procedimento: migrar locators/referências autorizados, remover somente a exclusão legada por thread e lookup antigo em names quando desnecessários, mantendo GC/leases/fences/validação V2.

## Verificações antes de uma versão definitiva

1. Inventariar objetos/remessas/checkpoints da conta sem imprimir dados privados em logs.
2. Decidir explicitamente quais dados de desenvolvimento podem ser descartados e quais precisam de migração.
3. Testar leitura velha, atualização confirmada e falha de substituição antes de desativar cada leitor.
4. Coordenar a versão mínima dos clientes; clientes sem encrypted metadata V1 não abrem publicações novas.
5. Revisar os itens marcados verificar antes da remoção com reproduções pequenas e histórico, sem adivinhar a intenção antiga.
6. Remover cada camada em mudança isolada, atualizar implementation.md/README e testes relacionados.
7. Não apagar dados antigos apenas porque um decoder novo compilou.

## Escopo do inventário

Foram examinados Models, Services, Bridge, fluxos de serial, arquivo/preview/vídeo, sincronização, lixeira, cache/configurações, recuperação e mensagens. O checkout de referência mobile não foi modificado. Este inventário registra as compatibilidades observáveis e as incertezas encontradas; não afirma que todo fallback de robustez é dívida temporária.
