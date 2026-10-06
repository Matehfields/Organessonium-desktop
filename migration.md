# Organessonium Development Compatibility / Migration

Inventário técnico compartilhado do desktop C# e do cliente Flutter durante desenvolvimento. Não é autorização para remover funcionalidades. Código atual é a referência; nenhum item abaixo foi removido nesta tarefa. Ao adicionar fallback/parser/alias temporário, registrar ID, motivo, comportamento, condição e procedimento de remoção aqui.

## Compatibilidades atuais

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

Procedimento para remoção: Remover branch JSON legado do router; exigir envelope nos leitores; remover somente fixtures legadas; manter validação de versão/autenticação; atualizar implementation.md e testes.

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

Código relacionado: ForumDownloader (entry.Encryption ausente); AppController.UploadSingleAsync preserveLegacyTransfer; VideoChunkIndex/DiscordVideoChunkProvider.

Flutter: Download e streaming conservam payloads sem encryption e ORGAESGC v1; a mudança do envelope de metadados não recriptografa partes existentes.

Comportamento atual: Leitura mantém caminho plaintext de conteúdo; retomada antiga pode completar conteúdo com mesmo layout. Metadados/rótulos novos são cifrados.

Pode ser removido quando: Não houver payloads plaintext necessários nem checkpoints antigos recuperáveis.

Impacto da remoção: Downloads/previews antigos e retomadas podem falhar.

Procedimento para remoção: Migrar conteúdo explicitamente fora desta tarefa, confirmar hashes/refs, limpar checkpoints obsoletos autorizados; retirar branches plaintext só então.

### PAYLOAD-LEGACY-002

Status: ativo

Descrição: Rótulos textuais file:... role:... chunk:... encryption:v1, batch/Parte e ORG_PREVIEW.

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

## Fronteiras e itens que não são dívida automaticamente

- Não foi encontrado protocolo de compartilhamento JSON neste desktop. Não existe evidência para inventar SHARE-LEGACY-001. Exportação OEK1 é recuperação de chave, não share de arquivo.
- Apenas schemaVersion 2 é aceito pelo leitor de metadados comuns. Prefixos APP|/FILE_META| e outros schemas não foram encontrados como leitores suportados; não declarar compatibilidade inexistente.
- favorites, synchronized, trash, sync.canonicalFolderId e árvore hierárquica são funcionalidades atuais. Não removê-los sob o rótulo de legado. Aliases de sincronização representam a mesma pasta lógica em PCs diferentes.
- Tamanhos reais/variáveis de chunks, índices de Range, fallback de MP4 não suportado, limites de memória, retry, falta de codec de mídia e moldura WPF em Windows antigo são comportamentos operacionais, não autorização para simplificar suporte.
- Base64URL sem padding de encryption.salt é contrato atual de conteúdo; Base64 padrão com padding do envelope de metadata é outro contrato. Não adicionar fallback entre eles sem registrar e justificar.
- A ausência de preview/sync/trash/encryption pode ser válida no tipo de entrada. Campos opcionais não são automaticamente legado removível.
- O cache SQLite local continua operacional e pode conter plaintext. Somente seriais novos receberam transporte cifrado. Não houve mudança de schema do cache nesta tarefa.
- Não existe downgrade de envelope declarado: version futura, Base64 inválido, chave/tag incorretas ou JSON interno inválido não seguem METADATA-LEGACY-001.

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
