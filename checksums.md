# Checksums de conteúdo V1

## Escopo e bytes exatos

Campo opcional `checksums` dentro do JSON de cada `file` e de cada `versions[].payload`, protegido pelo envelope criptográfico de metadados existente. Novos uploads do desktop publicam esse campo; não altera schemaVersion 2, storage V2, chunkRefs nem o envelope ORGAESGC v1. Não contém chaves e não deve ser registrado em logs.

O algoritmo é SHA-256 padrão, sem salt, chave, BOM ou prefixo adicional. A entrada é o conteúdo binário **plaintext** efetivamente transportado. O hash completo é SHA256(chunk0 || chunk1 || ...), não o hash da concatenação dos hashes. Nonce, salt, header, tag, nomes e overhead multipart não participam. No MP4 com faststart, calcular sobre o stream virtual remapeado: esses são os bytes reconstruídos no download. O arquivo físico original não é modificado. Na sincronização não há faststart.

O manifesto cobre o conteúdo principal de uma versão, não thumbnails/prévias convertidas, que continuam autenticadas por AES-GCM. SHA-256 complementa essa autenticação e detecta inclusive corrupção local de partes plaintext. Hash sem metadata autenticado não é prova contra um atacante que possa substituir também os hashes.

## Formato

Todos os campos do objeto são obrigatórios. Ordem das propriedades não importa. JSON usa as convenções existentes, UTF-8, números inteiros e arrays ordenados. Hashes são exatamente 64 caracteres hexadecimais minúsculos ASCII, sem separadores, Base64 ou prefixo `0x`.

```json
{
  "checksums": {
    "version": 1,
    "algorithm": "SHA-256",
    "sha256": "bef57ec7f53a6d40beb640a780a639c83bc29ac8a9816f1fc6c5c6dcd93c4721",
    "chunkSizeBytes": 3,
    "chunks": [
      "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
      "cb8379ac2098aa165029e3938a51da0bcecfc008fd6795f401178647f96c5b34"
    ]
  }
}
```

Vetor acima: arquivo de seis bytes ASCII/UTF-8 `abcdef`, chunk 0 = `abc`, chunk 1 = `def`, sem quebra de linha. O tamanho 3 serve apenas como vetor, não muda o tamanho de upload em produção. Em produção, `chunkSizeBytes` é o tamanho plaintext do checkpoint, antes dos 70 bytes ORGAESGC, limitado pelos anexos Discord. Todos os chunks, salvo o último, têm esse tamanho. A quantidade é max(1, ceil(sizeBytes/chunkSizeBytes)), em ordem zero-based, correspondente aos índices de chunkRefs ou à ordem de chunks legado. Os índices ORGAESGC continuam one-based no header.

Arquivo vazio: sizeBytes = 0, um chunk vazio, chunkSizeBytes positivo e ambos os hashes iguais a `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.

## Escrita e retomada

1. Definir representação final e layout antes de transmitir.
2. Ler sequencialmente com buffer limitado. Alimentar um SHA-256 completo e outro por chunk, finalizando o segundo a cada fronteira.
3. Persistir o manifesto no checkpoint antes do primeiro envio.
4. A cada preparação/retry, conferir SHA-256 do plaintext em RAM contra o índice esperado antes de cifrar. Mudança recusa o envio; não atualizar hashes para acomodar uma origem diferente.
5. Manter o transporte, concorrência, limites e agrupamento de até dez anexos existentes. Confirmação do Discord não recalcula o manifesto.
6. Publicar metadata com o manifesto somente após concluir o upload normalmente. Cache e histórico conservam os mesmos valores.

O desktop faz uma leitura sequencial adicional antes do upload para calcular o hash completo convencional sem serializar a transmissão paralela ou reter partes fora de ordem na memória. Até 1 MiB de buffer é usado por cálculo, zerado ao terminar, sem arquivo intermediário. Leitura e cálculo executam fora do Dispatcher. Um checkpoint sem partes confirmadas pode receber o manifesto; um checkpoint antigo parcialmente confirmado continua sem esse campo, pois não há evidência de igualdade entre os bytes já enviados e a origem atual.

## Leitura e erro

1. Descriptografar e validar o metadata. Campo ausente significa compatibilidade antiga, não checksum validado.
2. Se declarado, exigir objeto completo, version = 1, algorithm = SHA-256, tamanhos/quantidade coerentes e hashes válidos. Não fazer downgrade após erro.
3. Autenticar/decifrar cada chunk em RAM. Conferir SHA-256 antes de gravar plaintext ou expor um chunk ao player progressivo.
4. Revalidar por SHA-256 partes plaintext de um checkpoint de download. Parte divergente é removida e baixada novamente.
5. Durante a montagem, recalcular os hashes de cada parte e do arquivo inteiro na ordem lógica. Gravar a saída incompleta no diretório de destino e substituir o nome final somente após todas as verificações passarem. Falha/cancelamento preserva o destino anterior e limpa a montagem incompleta.
6. Falha de parte permite a política limitada de novas tentativas do download existente. Hash completo divergente não oferece abrir mesmo assim. O erro é apresentado pelo tratamento normal de transferências; não encerra o app.

O checksum completo pode ser validado somente quando todos os bytes forem lidos em ordem; o player por ranges valida cada chunk solicitado, sem alegar validação completa por assistir apenas uma faixa.

## Flutter interoperability

Flutter deve ler o campo opcional, validar a versão e reproduzir exatamente os bytes e a ordem descritos acima. O manifesto é independente do número de workers, do lote HTTP e dos limites de RAM de cada plataforma. Não é necessário copiar o paralelismo do desktop. Pode usar hashing incremental e buffers menores, respeitando as fronteiras lógicas dos chunks.

Pseudocódigo: `whole.add(bytes); chunk.add(bytes);` até a fronteira; adicionar `hexLower(chunk.close())` e iniciar outro hash. Ao terminar: `sha256 = hexLower(whole.close())`. A posição do manifesto acompanha a versão do conteúdo e deve ser preservada em rename, move, favorito, lixeira, histórico, compartilhamento de metadata e cache. Seriais OSER não contêm o manifesto: hidratar metadata completo antes de transferir. Clientes antigos que descartam campos desconhecidos precisam preservar `checksums` ao editar metadata existente; este código não pode impor essa regra a versões mobile já distribuídas.

No desktop, as APIs são IncrementalHash/SHA256 do .NET. Os testes cobrem upload real do pipeline com HTTP simulado, download, retomada de parte adulterada, GCM válido com conteúdo divergente, hash completo incorreto, preservação do destino, cache, histórico, legado, vazio, vetor conhecido, faststart e exibição no WebView2 oculto. Não representam um teste de transferência na conta Discord do usuário.
