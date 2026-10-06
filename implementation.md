# Encrypted metadata V1: protocolo do Organessônium

## 1. Visão geral e arquitetura

Este protocolo protege as novas publicações de metadados no desktop C# WPF/WebView2. Não modifica AES, chaves de arquivo, chunks ou bytes de payload já existentes.

`IMetadataCodec` recebe JSON e devolve envelope JSON, ou identifica e decodifica um envelope recebido. `MetadataCodec` roteia leitura legada e versões; `EncryptedMetadataCodecV1` implementa criptografia; `MetadataEncryptedEnvelope`, `MetadataCryptoParameters` e `MetadataCodecException` definem estrutura, constantes e erros. `SerialMetadataTransport` protege OSER sem mudar seu layout interno. Negócio e UI não manipulam AES.

Cobertura das novas escritas:

- Arquivos e pastas: nome, MIME, tamanho, datas, IDs, relações, favorito, previews, chunks, criptografia de conteúdo, lixeira e extensões de sincronização.
- Mappings de `names` e cabeçalhos de `folders`: todo o JSON.
- Seriais: envelope de mensagem e bytes OSER encapsulados.
- Sincronização: revisões e confirmações de projeção.
- Rótulos de payloads: inclusive referências de imagens Markdown e previews.
- Novos títulos de posts: identificadores aleatórios opacos, não derivados de nomes ou IDs internos.

Visíveis: discriminador/versão/algoritmo/KDF, modo de seleção da chave, salt, nonce, verificador de chave, ciphertext/tag e seu comprimento. Discord continua vendo canais, IDs externos de mensagens/posts/attachments, horários de publicação, tamanhos e agrupamentos. Nomes técnicos como `metadata.json`, `serial.bin`, `payload.bin` e sequência/papel codificados no header binário dos chunks não são uma árvore privada legível. Não existe proteção contra análise de tráfego.

Leitura: localizar mensagem ou anexo pelos canais configurados, reconhecer envelope, autenticar/descriptografar, interpretar JSON, validar schema interno, consumir no índice/sincronizador/preview. Escrita: validar objeto, serializar JSON, UTF-8, cifrar, montar envelope, enviar inline/anexo. Dados legados não são regravados por simples leitura. Modificações efetivas usam V1; não existe encoder legado de publicação.

## 2. JSON anterior à criptografia

JSON RFC 8259, objeto na raiz, UTF-8 sem BOM, sem normalização Unicode. Propriedades são case-sensitive. Strings podem conter acentos, emoji, caracteres compostos/decompostos e escapes JSON válidos. Nenhum segredo mestre entra nesse objeto.

A serialização C# usa System.Text.Json, nomes explícitos de propriedades, JSON compacto e UnsafeRelaxedJsonEscaping. Campos opcionais nulos de metadados comuns são omitidos; `fav: false` é omitido. Objetos opcionais inexistentes ficam ausentes. Não inventar preview ou campos de arquivo em pasta. Arrays necessários, como `chunks` e `parents`, seguem o contrato do tipo; o codec não apaga valores, arrays vazios ou null recebidos em JSON genérico.

Datas comuns: `yyyy-MM-ddTHH:mm:ssZ`. Sincronização também aceita frações UTC no formato ISO 8601; escreve datas UTC. OSER usa milissegundos Unix. Inteiros são decimais JSON, não strings; tamanhos/revisões podem exigir 64 bits. Não converter IDs Discord para números: são strings. Booleanos são `true`/`false`. Arrays mantêm ordem, principalmente os IDs dos chunks.

Ordem das propriedades e whitespace não importam ao leitor. O ciphertext, porém, depende dos bytes exatos do JSON de entrada. Não há canonicalização JSON nem reserialização durante autenticação. Para reproduzir o vetor, use literalmente o JSON da seção 11, sem newline final. Um JSON semanticamente igual com outra ordem/escaping pode gerar outro ciphertext e continuar interoperável.

### Metadados comuns: schemaVersion 2

| entryType | Propriedades |
| --- | --- |
| file | schemaVersion, entryType, fileId, threadId, name, mimeType, sizeBytes, uploadedAt, batchId, parentFolderId opcional, chunks, preview opcional, encryption opcional, fav opcional, trash/sync opcionais |
| folder | schemaVersion, entryType, folderId, name, createdAt, parentFolderId, folderColor, deviceType/fav/trash/sync/syncDevice opcionais; threadId somente quando aplicável |
| folderIndex | schemaVersion, entryType, folderId, threadId, serialMessageId/serialSha256 opcionais |
| folderIndexHeader | schemaVersion, entryType, folderId, parentFolderId, name |

`preview`: `thumbnailMessageId` opcional; `video480p: { sizeBytes, chunks }` quando existe. `encryption` de conteúdo: `version: 1, mode: standard|custom, salt` em Base64URL sem padding; `keyId` opcional antigo. Isso é distinto do envelope de metadados.

`trash`: `originalParentFolderId, deletedAt`. `syncDevice`: `version: 1, deviceId, deviceName`. `sync`: `version: 1, syncFolderId, relativePath, hash, modifiedUtc, revision, revisionId, deviceId, deviceName, state, deletedUtc, canonicalFolderId`, somente os campos aplicáveis. Raízes/aliases não precisam dos campos de arquivo. Caminhos relativos usam `/`; SHA-256 é hexadecimal; IDs são strings.

### Outros objetos internos cifrados

- Caption de serial: `{ "schema":1, "entryType":"folderSerial", "folderId":"...", "sha256":"..." }`. O hash cobre o anexo cifrado completo. `folderName` legado é lido, mas novos captions dispensam esse campo.
- Dados de serial: `{ "schemaVersion":1, "entryType":"folderSerialData", "data":"BASE64_PADDED_OSER" }`.
- Rótulo de payload: `{ "schemaVersion":1, "entryType":"payloadReference", "content":"..." }`. content contém o rótulo atual, por exemplo `file:<id> role:original chunk:1/2 encryption:v1`; o rótulo é privado, não o conteúdo externo da mensagem.
- Confirmação: `{ "schemaVersion":1, "entryType":"syncApplied", "revisionId":"..." }`.
- Revisão: schemaVersion 2, entryType syncRevision, syncVersion 1, syncFolderId, fileId, relativePath, kind, sizeBytes, hash, modifiedUtc, revision, revisionId, parents, deviceId, deviceName, state, deletedUtc opcional e payload FileMetadata quando aplicável. MessageId é local e não é serializado.

Schemas internos são separados da versão do envelope. Cifrar um schema não o transforma em outra versão.

## 3. Envelope completo

Todos os 11 campos são obrigatórios. V1 rejeita campos extras, nomes duplicados, tipos errados e valores fora do protocolo. Ordem externa das propriedades não altera a autenticação.

| Campo | Tipo e significado |
| --- | --- |
| format | string exata organessonium-metadata |
| version | inteiro 1 |
| encrypted | boolean true |
| algorithm | string exata AES-256-GCM |
| kdf | string exata HKDF-SHA256 |
| keyMode | standard ou custom, case-sensitive |
| salt | Base64 padrão canônico de 16 bytes aleatórios |
| nonce | Base64 padrão canônico de 12 bytes aleatórios |
| keyCheck | Base64 padrão canônico de 16 bytes, definido na seção 5 |
| ciphertext | Base64 padrão dos bytes cifrados |
| tag | Base64 padrão canônico de 16 bytes |

O envelope válido da seção 11 é também o exemplo completo. Sem fileId, entryType, nome, threadId, caminho, hash plaintext, timestamps de arquivo ou chave mestre fora do ciphertext.

## 4. AES e AAD

AES-256-GCM, chave de 32 bytes, nonce de 12 bytes, tag de 16 bytes. C#: `AesGcm(key, 16)`, Encrypt/Decrypt, com RandomNumberGenerator para salt e nonce em cada Encode.

AAD é UTF-8 desta sequência, com LF (byte 0A) entre linhas e SEM LF final:

```text
organessonium-metadata
1
true
AES-256-GCM
HKDF-SHA256
<keyMode>
<salt Base64 exatamente como no envelope>
<nonce Base64 exatamente como no envelope>
<keyCheck Base64 exatamente como no envelope>
```

Não é o JSON do envelope. Não usar CRLF, BOM, espaços adicionais, ordem de objeto, bytes decodificados do salt/nonce ou nomes de propriedades nesse AAD. Valores fixos também são validados antes do AES. Cabeçalho modificado, nonce, ciphertext ou tag alterados nunca entregam plaintext.

## 5. Chaves e KDF

Entrada secreta `M`: exatamente 32 bytes da master key do modo indicado. Seleção independe do modo atualmente usado para escrever: ler envelope standard seleciona M standard; ler custom seleciona M custom. Não usar token, IDs do Discord, nome de arquivo ou fileId como chave de metadados.

`standard` reutiliza o provedor padrão existente. Seu bootstrap é embutido no app; não é um segredo exclusivo do usuário. Pode ser recuperado por alguém que tenha o aplicativo/código. AES não corrige essa limitação. Para confidencialidade baseada em segredo do usuário, usar custom.

`custom` reutiliza a master key existente, persistida no Windows com DPAPI CurrentUser. DPAPI NÃO faz parte do protocolo remoto. Os 32 bytes recuperados/exportados são a entrada M que outra plataforma precisa importar para seu armazenamento seguro. Não publicar M nesta especificação nem no envelope.

Quando a configuração inicial deriva M de senha: PBKDF2-HMAC-SHA256, senha em UTF-8 sem BOM/normalização/trim, salt aleatório de 16 bytes, 600000 iterações, saída de 32 bytes. A validação rejeita senha inteiramente em branco; os demais caracteres são preservados. Esse salt e as iterações ficam no armazenamento de chave local, não são o salt do envelope. Cada configuração gera outro salt: digitar a mesma senha em dois dispositivos NÃO garante a mesma M. Para sincronizar, importar a mesma chave de recuperação. Uma chave importada já é M, sem repetir PBKDF2; o armazenamento registra iterações 0 nesse caso.

Recuperação existente OEK1: ASCII OEK1 (4 bytes), versão 1 (byte), comprimento 32 (byte), M (32 bytes), HMAC-SHA256(M, primeiros 38 bytes) (32 bytes), total 70 bytes. Arquivo sensível. Sua verificação é integridade, não confidencialidade; ele contém M e deve permanecer fora do backend e de logs.

Chave por operação:

```text
K = HKDF-SHA256(ikm=M, salt=envelopeSaltRaw16,
                info=UTF8("organessonium/metadata-key/v1"), length=32)
```

RFC 5869 completo, Extract e Expand. Para estes comprimentos:
PRK = HMAC-SHA256(key=salt, message=M).
K = HMAC-SHA256(key=PRK, message=UTF8(info) || byte 01).
O info não tem NUL, prefixo de comprimento ou newline. Não usar o info da chave de conteúdo dos arquivos.

```text
keyCheckRaw = primeiros 16 bytes de
  HMAC-SHA256(key=K, message=UTF8("organessonium/metadata-key-check/v1"))
```

Comparar keyCheck em tempo constante antes do AES; incluí-lo também no AAD. Ele não contém M/K nem substitui a tag. Divergência sugere chave diferente ou cabeçalho alterado; não permite provar qual das duas causas ocorreu. Não é um identificador persistente de usuário: muda com o salt.

HKDF não possui iterações PBKDF2. Todos os parâmetros novos ficam em MetadataCryptoParameters; o PBKDF2 existente permanece centralizado no provedor de chave personalizada.

## 6. Representação, transporte e OSER

Salt, nonce, keyCheck, ciphertext e tag usam Base64 padrão RFC 4648, alfabeto A-Z/a-z/0-9/+/, padding = obrigatório quando aplicável. Rejeitar espaços, quebras de linha, caracteres URL-safe, padding ausente/excedente e bits não canônicos. Isso não altera o Base64URL do salt de conteúdo, que está DENTRO do plaintext cifrado.

Sem concatenação ciphertext+tag no envelope: são campos separados. AES recebe arrays de bytes. O envelope é JSON UTF-8 sem BOM. Metadados comuns até 2000 caracteres vão inline; maiores usam metadata.json. Revisões grandes usam sync-revision.json e caption técnico metadata. Seriais usam serial.bin com JSON UTF-8 cifrado, não OSER visível. Não inferir formato pelo sufixo do arquivo.

Orçamento máximo do envelope: DiscordLimits.AttachmentBytes, atualmente 20 MiB. O limite conservador de plaintext é (MaxEnvelopeBytes - 1024) * 3 / 4; o tamanho final também é validado. O envelope cresce por Base64. Não há multipart de metadados acima disso; rejeitar antes de substituir dados. O tamanho dos arquivos/chunks de conteúdo não mudou.

Dentro de folderSerialData, data é Base64 padrão de OSER v3 existente. Tudo abaixo continua privado:

- Inteiros little-endian; strings = uint32 comprimento dos bytes + UTF-8 sem BOM.
- Cabeçalho: ASCII OSER, uint16 versão 3, uint8 kind (0 Folder, 1 Favorites), int64 generatedAt Unix-ms, strings folderId, parentFolderId, folderName.
- Agregado: seis int64 na ordem totalItems, fileCount, folderCount, totalBytes, imageCount, videoCount; uint32 typeCount; para cada tipo em ordem ordinal, string extensão + int64 quantidade.
- uint32 childCount; filhos na ordem publicada.
- Filho: uint8 flags; strings id, namesMessageId, parentFolderId, name, threadId, mimeType; int64 uploadedAt Unix-ms; string previewMessageId; contexto encryption; string folderColor.
- flags: bit0 pasta, bit1 favorito, bit2 phone, bit3 pc.
- encryption: uint16 0 se ausente; caso contrário uint16 1, uint8 modo (0 standard, 1 custom), string salt de conteúdo, e string keyId somente para custom.
- V3 não contém sizeBytes por filho; usa agregado. V4 acrescenta int64 sizeBytes depois de folderColor, somente para leitura de dados experimentais existentes. V1 não contém bits de dispositivo nem encryption; V2 contém dispositivo mas não encryption.
- Rejeitar truncamento, versão desconhecida, kind inválido e bytes restantes ao fim.

SHA-256 do caption/mapping cobre o anexo serial CIFRADO completo, em hexadecimal lowercase. O cache novo conserva exatamente os bytes desse anexo. Para decidir se houve mudança, comparar o conteúdo OSER decodificado, ignorando generatedAt, e conferir o digest do transporte conhecido; não comparar um ciphertext novo de nonce aleatório com o anterior. Assim, leitura/reconciliação sem mudança não republica um serial nem converte um serial legado.

## 7. Processo de escrita

1. Validar os campos do objeto de negócio.
2. Serializar uma vez para JSON interno.
3. Converter para UTF-8 estrito e validar orçamento.
4. Resolver uma cópia temporária da master key pelo modo selecionado.
5. Gerar salt16 e nonce12 com CSPRNG.
6. Derivar K com HKDF; calcular keyCheck.
7. Construir AAD como especificado.
8. Cifrar JSON UTF-8 com AES-GCM e obter tag.
9. Converter componentes para Base64 canônico, montar e serializar envelope.
10. Validar tamanho final; publicar inline ou anexo.
11. Liberar AES e zerar arrays temporários de chave/plaintext quando possível.

Não há temporário de metadata plaintext. Arquivos temporários de metadata/serial/revisão contêm só ciphertext/envelope. São removidos após envio/falha. Cache operacional local é uma decisão separada.

## 8. Processo de leitura

1. Ler conteúdo da mensagem; quando não houver metadata inline reconhecível, buscar o anexo técnico.
2. Limitar tamanho antes e durante leitura.
3. Interpretar objeto JSON e identificar declaração de envelope.
4. Validar format e version; versão desconhecida é erro, não legado.
5. Validar campos, tipos, algoritmo/KDF/modo e Base64 canônico.
6. Resolver M pelo keyMode, derivar K, comparar keyCheck.
7. Reconstruir AAD e executar AES-GCM Decrypt.
8. Somente após autenticação, decodificar UTF-8 estrito e interpretar JSON.
9. Validar schema e campos do negócio; então materializar arquivo/pasta/serial/revisão.
10. Liberar AES e arrays temporários.

Não aceitar plaintext parcialmente autenticado. JSON interno inválido também falha. Falha de decodificação nunca significa pasta vazia.

## 9. Detecção de versões e erros

Envelope se declara pelo format exato, ou por campos encrypted/ciphertext que exigem validação de envelope. A decisão não depende apenas de encrypted=true. Faltar/mudar format não permite downgrade. Só JSON sem declaração de criptografia pode seguir leitura legada; o negócio ainda valida seu schema. Metadados comuns legados suportados continuam sendo schemaVersion 2, não qualquer formato histórico.

Binary serial começando OSER entra no leitor legado v1-v4. Serial novo é JSON do envelope V1, que depois revela folderSerialData. Rótulos textuais e marcadores syncApplied antigos são lidos somente pelos componentes correspondentes.

MetadataError diferencia InvalidFormat, UnsupportedVersion, InvalidBase64, KeyUnavailable, KeyMismatch, AuthenticationFailed, InvalidPlaintextJson, MissingField e TooLarge. GCM não distingue matematicamente toda adulteração de toda chave errada; keyCheck fornece a classificação preliminar indicada acima. Mensagens ao usuário não exibem stack trace ou plaintext. Nenhum erro de envelope reconhecido faz fallback para outro anexo plaintext.

## 10. Flutter interoperability

Implementar o mesmo router, envelope, bytes e AAD antes de a lógica de negócio consumir metadados. Usar AES-GCM interoperável com nonce96 e tag128; algumas bibliotecas retornam um SecretBox contendo ciphertext, nonce e MAC separados. Não colocar MAC no ciphertext do envelope.

HKDF deve incluir Extract. Entrada M/salt/info são bytes, não suas strings hex/Base64. PBKDF2 só cria M na configuração de chave; não rodar PBKDF2 com o salt de cada envelope. Guardar M no armazenamento seguro da plataforma, não copiar DPAPI. Chaves de recuperação devem transportar os mesmos 32 bytes.

Pseudocódigo:

```text
encode(jsonUtf8, M, mode):
    salt = secureRandom(16); nonce = secureRandom(12)
    K = HKDF_SHA256(M, salt, UTF8("organessonium/metadata-key/v1"), 32)
    check = HMAC_SHA256(K, UTF8("organessonium/metadata-key-check/v1"))[0:16]
    header = {fixedFields, keyMode:mode, salt:B64(salt), nonce:B64(nonce), keyCheck:B64(check)}
    aad = UTF8(joinLF(fixedAADFields, mode, header.salt, header.nonce, header.keyCheck))
    ciphertext, tag = AES_GCM_encrypt(K, nonce, jsonUtf8, aad, tagBytes=16)
    return JSON({header, ciphertext:B64(ciphertext), tag:B64(tag)})

decode(envelope, keyResolver):
    validateVersionAndFields(envelope)
    salt, nonce, check, ciphertext, tag = strictBase64Fields(envelope)
    M = keyResolver(envelope.keyMode)
    K = HKDF_SHA256(M, salt, UTF8("organessonium/metadata-key/v1"), 32)
    constantTimeCheck(keyCheck(K), check)
    bytes = AES_GCM_decrypt(K, nonce, ciphertext, tag, reconstructedAAD)
    return parseJSON(strictUTF8(bytes))
```

A futura implementação deve ler inline e os anexos técnicos, decodificar seriais antes do OSER, manter caption/digest cifrados, decodificar payloadReference antes dos parsers de partes e preservar os schemas de sincronização. Títulos novos são opacos. Clientes sem esse decoder não conseguem ler publicações V1; não há duplicata pública para mantê-los funcionando.

C# ↔ Node/OpenSSL valida uma segunda implementação agora; NÃO é prova de execução Flutter. O teste futuro obrigatório deve usar o vetor e também nonces aleatórios nos dois sentidos C# ↔ Dart. Não modificar o checkout Flutter para esta implementação desktop.

## 11. Vetor reproduzível

Chave e valores desta seção são exclusivamente de TESTE, públicos e sem relação com dados/chaves reais. Nonce fixo somente neste vetor. Produção nunca oferece parâmetro de nonce fixo.

JSON original, sem newline final:

```json
{"schemaVersion":2,"entryType":"folder","folderId":"test-folder","name":"Fotos café 📷","createdAt":"2026-10-06T12:00:00Z","parentFolderId":"root","fav":true}
```

- M hexadecimal: `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`
- salt hexadecimal: `202122232425262728292a2b2c2d2e2f`
- nonce hexadecimal: `303132333435363738393a3b`
- K derivada hexadecimal: `5a806c768419e6d565fe129fe960ff0def7b9ff144722054ff7e87d1992a4fbf`
- keyCheck Base64: `YBrO6AjFjVYl4RTTKXLRFQ==`
- tag Base64: `p0rSazS/tPCYuiGeQskzTQ==`

AAD exato:

```text
organessonium-metadata
1
true
AES-256-GCM
HKDF-SHA256
standard
ICEiIyQlJicoKSorLC0uLw==
MDEyMzQ1Njc4OTo7
YBrO6AjFjVYl4RTTKXLRFQ==
```

Envelope esperado, incluindo ciphertext:

```json
{
  "format": "organessonium-metadata",
  "version": 1,
  "encrypted": true,
  "algorithm": "AES-256-GCM",
  "kdf": "HKDF-SHA256",
  "keyMode": "standard",
  "salt": "ICEiIyQlJicoKSorLC0uLw==",
  "nonce": "MDEyMzQ1Njc4OTo7",
  "keyCheck": "YBrO6AjFjVYl4RTTKXLRFQ==",
  "ciphertext": "yYbZ5T6WPbBjFMC1mh+21Ng5eirPrT4xEfwF1dcRoJpPe7b+AdEOYsLkFKwQ35wkjjI9YwWHvKLvoeDBHMkcOZLakrwErZZjV1s4PWj0U+wYvGmco427uW7PTL6TAax83PS2X8GJ2RvrP+5O52MDk4Fbh+bKe4qoGctc0gFoB8x32ia6GN1EO5HmMbSWKQtgpkqGL54H9+rwjRplmc00saU=",
  "tag": "p0rSazS/tPCYuiGeQskzTQ=="
}
```

O arquivo tests/metadata-v1-vector.json contém o mesmo vetor; tests/metadata-v1-interop.cjs é uma implementação independente Node/OpenSSL. Testes comparam os campos esperados e ambos os sentidos com nonces aleatórios, sem abrir janelas.

## 12. Segurança, cache, sincronização e compartilhamento

Protege confidencialidade condicionada ao segredo de M e integridade dos campos cifrados/autenticados. Não protege máquina comprometida, observador com M, key recovery exposta, logs antigos, capturas de UI ou análise de tráfego. Padrão embutido não equivale a um segredo pessoal. Perder a chave custom pode impedir inclusive localizar os chunks; guardar a recuperação com segurança. Não existe reset que restaure dados cifrados sem M.

Nonce não pode repetir com a mesma K. Novas operações usam salt e nonce aleatórios; o salt também separa a chave por operação. Não reduzir entropia, gerar nonce por timestamp ou reutilizar ciphertext/tag com header alterado. Tag inválida nunca entrega uma parte recuperável do metadata.

Não é anti-rollback: ciphertext válido antigo continua autenticado. Os mecanismos de revisão/mapping do app resolvem versões, e não o GCM isoladamente.

Cache SQLite, sync_state/sync_issues, estados de transferência e previews mantêm o formato operacional existente, que pode conter nomes/caminhos em claro no disco local. Não há criptografia at-rest completa nesta tarefa; proteger conta/disco Windows. Seriais novos em disco conservam o mesmo envelope cifrado da nuvem; seriais OSER locais antigos são lidos sem migração ao abrir. Memória, objetos de UI e strings gerenciadas contêm dados descriptografados durante uso; limpeza de arrays não garante apagar todas as cópias do runtime.

Sincronização recebe JSON autenticado antes de comparar revisões/caminhos/hashes. O diário e os marcadores são cifrados; a lógica de conflitos/tombstones não mudou. As limitações existentes de mudanças fora do diário não foram removidas por esta tarefa.

Não foi encontrado fluxo de compartilhamento por JSON, exportação de biblioteca compartilhada ou tela de compartilhamento neste desktop. Não foi inventado um novo protocolo. A exportação de chave é recuperação, não compartilhamento de arquivo. Uma futura função de compartilhar deve definir entrega autorizada de chave/referências; publicar só ciphertext sem mecanismo de chave não torna um link utilizável. Não publicar M dentro de metadata ou links.

Auditoria manual --audit-metadata continua sendo um relatório local explícito e sensível, não um log automático nem temporário de upload. Ele pode conter metadados autenticados; não publicar o relatório. Diagnóstico --debug-names omite nomes, mapeamentos internos e trechos de conteúdo em seu relatório. Exceções do codec contêm somente categoria/mensagem segura, nunca plaintext ou chave.

Atomicidade: metadados são preparados/cifrados antes de PATCH. Anexo↔inline preserva messageId e remove attachments anteriores quando o servidor confirma a substituição. Falha de PATCH conserva o registro anterior. O fluxo de serial/mapping conserva suas confirmações e invalidação de cache em falhas; não existe transação multi-mensagem no Discord. Não apaga milhares de objetos nem recriptografa arquivos.

Compatibilidades e condições de remoção estão em migration.md. A alteração não reescreve toda a conta: registros e títulos legados continuam legíveis até mudança efetiva ou operação de manutenção explicitamente solicitada.

### Referências e arquivos

Algoritmo e derivação: [HKDF RFC 5869](https://www.rfc-editor.org/rfc/rfc5869), [AES-GCM no .NET](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm), [Base64 RFC 4648](https://www.rfc-editor.org/rfc/rfc4648.html).

Criados: Services/MetadataEncryption/*, Bridge/AppController.Metadata.cs, tests/Organessonium.NetworkTests/MetadataEncryptionTests.cs, tests/metadata-v1-vector.json, tests/metadata-v1-interop.cjs, implementation.md e migration.md.

Integrações: MetadataService, SerialIndexService, AppRegistry, CloudSyncRepository, ForumUploader/ForumDownloader, DiscordApiClient, ServerMetadataAudit, DebugNamesScanner, EncryptionService (somente acesso à master para metadados), AppController, App.xaml.cs, README e projetos/testes relacionados.
