# Ativos de mídia para compilação de distribuição

## Por que não ficam no Git

O Organessônium embute o runtime do FFmpeg no executável e distribui as fontes
correspondentes para cumprir a licença GPL. Esses arquivos são grandes e não devem entrar
no histórico Git:

| Arquivo | Destino local | Tamanho aproximado |
|---|---|---|
| `runtime-package.zip` | `Organessonium/Organessonium/media/runtime-package.zip` | 72 MB |
| `ffmpeg-corresponding-source.zip` | `Organessonium/Organessonium/media/sources/ffmpeg-corresponding-source.zip` | 1,81 GB |

O manifesto `Organessonium/Organessonium/media/source-manifest.json` registra os SHA-256
esperados. O script `media/Verify-Sources.ps1` confere hashes, inventário e condições de
redistribuição.

## Release de mídia

Os ativos são publicados uma vez em uma release separada, com a tag `media-assets-v1`.
Essa tag não é uma versão do aplicativo e não deve ser usada para releases estáveis.

### Opção A: GitHub CLI

Com `gh` instalado e autenticado:

```powershell
gh release create media-assets-v1 `
  --repo Matehfields/Organessonium-desktop `
  --title "Ativos de mídia v1" `
  --notes "Runtime FFmpeg verificado e fontes correspondentes para a compilação de distribuição." `
  Organessonium/Organessonium/media/runtime-package.zip `
  Organessonium/Organessonium/media/sources/ffmpeg-corresponding-source.zip
```

### Opção B: interface web

1. Abra **Releases → Draft a new release** no GitHub.
2. Crie a tag `media-assets-v1` no commit atual da `main`.
3. Título: `Ativos de mídia v1`.
4. Anexe os dois arquivos.
5. Publique a release.

Cada arquivo deve ficar abaixo do limite de 2 GiB por asset do GitHub Releases. Não
anexe esses arquivos à release do aplicativo.

## Verificação antes de publicar

```powershell
pwsh ./Organessonium/Organessonium/media/Verify-Sources.ps1 -ForRedistribution
```

O script deve terminar com sucesso. Se ele falhar, não publique a release de mídia nem a
distribuição do aplicativo.

## Download no desenvolvimento e na CI

```powershell
pwsh ./release/Get-MediaAssets.ps1 -Repository Matehfields/Organessonium-desktop
```

O script:

1. Lê os SHA-256 do manifesto.
2. Reutiliza arquivos locais que já correspondem.
3. Baixa os ausentes da release `media-assets-v1`.
4. Confere o hash antes de mover para o diretório de mídia.
5. Executa `Verify-Sources.ps1 -ForRedistribution`.

Na CI, o workflow usa `GH_TOKEN` com permissão de leitura. Nenhum segredo de assinatura é
necessário para baixar mídia pública.

## Quando a mídia mudar

1. Gere e verifique os novos arquivos localmente.
2. Atualize `source-manifest.json` e os avisos aplicáveis.
3. Publique uma nova release, por exemplo `media-assets-v2`.
4. Atualize o padrão de `Get-MediaAssets.ps1` e os workflows, ou passe `-Tag` explicitamente.
5. Registre a mudança no `CHANGELOG.md`.
6. Nunca reutilize a tag `media-assets-v1` com conteúdo diferente.

## Limitações

- O runtime controlado não inclui `ffprobe.exe`; alguns testes manuais de mídia exigem
  uma distribuição completa do FFmpeg.
- A release de mídia precisa existir antes de executar `build.yml` ou `release.yml`.
- Um clone limpo compila o código, mas não publica a distribuição sem esses ativos.
