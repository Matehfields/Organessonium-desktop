# Git, versionamento, testes e publicação

Este documento descreve a infraestrutura de desenvolvimento do Organessônium Desktop.
Ele deve ser atualizado quando o processo de build, versionamento, testes ou publicação
mudar.

> **Regra de autorização**
>
> Compilações completas de distribuição, criação de tags remotas, push e publicação de
> versões estáveis somente podem ocorrer mediante autorização explícita do mantenedor.
> Verificações leves, compilação Debug/Release e testes rápidos podem ser executados
> conforme as regras de CI abaixo.

## 1. Visão geral

O repositório usa Git localmente e o GitHub como repositório remoto central:

- `main`: branch estável, protegida, de onde saem as releases.
- `feature/<descricao>`: novas funcionalidades.
- `fix/<descricao>`: correções.

Não crie branches permanentes adicionais sem necessidade. Contribuições externas
chegam por pull request para `main`.

O remoto esperado é `https://github.com/Matehfields/Organessonium-desktop`.

## 2. Versionamento

### 2.1 Fonte única

`Version.props` é a única fonte da versão pública e do número de build:

```xml
<OrganessoniumVersion>1.0.0</OrganessoniumVersion>
<OrganessoniumBuild>1</OrganessoniumBuild>
```

`Directory.Build.props` importa esse arquivo e deriva os metadados usados pelo .NET e
pelo Windows:

| Metadado | Exemplo | Uso |
|---|---|---|
| Versão pública | `1.0.0` | SemVer, documentação, tag `v1.0.0` |
| Build | `1` | rodada de desenvolvimento/correções |
| `Version` | `1.0.0` | propriedade do SDK .NET |
| `AssemblyVersion` | `1.0.0.0` | identidade gerenciada |
| `FileVersion` | `1.0.0.1` | propriedades do executável Windows |
| `InformationalVersion` | `1.0.0+build.1` | texto legível de versão |
| Versão de MSI/MSIX | `1.0.0.0` | pacotes Windows |

Os formatos de MSI/MSIX exigem quatro campos numéricos e restringem MAJOR/MINOR a 255.
Por isso a versão pública é mantida em três campos e convertida para `A.B.C.0` apenas
nos pacotes. O número de build não entra na versão do pacote para não quebrar upgrades
do Windows Installer.

Não repita números de versão em `.csproj`, scripts, manifests ou configurações. Se
precisar consultar os valores derivados:

```powershell
pwsh ./tools/version.ps1 show
```

### 2.2 Regras da versão pública

- A versão inicial é `1.0.0`.
- Alterar MAJOR, MINOR ou PATCH exige autorização explícita do mantenedor.
- Use `set-version` somente depois da autorização:

  ```powershell
  pwsh ./tools/version.ps1 set-version -Version 1.0.1 -Authorized
  ```

  O comando valida o formato e incrementa a build uma vez, pois a mudança de versão
  também conclui uma rodada.
- A tag de release é `v` + versão pública: `v1.0.0`, `v1.0.1`, `v1.1.0`, `v2.0.0`.

### 2.3 Regras do número de build

- A build começa em `1` e é incrementada exatamente uma vez por rodada concluída de
  desenvolvimento ou correções.
- Uma rodada pode alterar vários arquivos; a build não é incrementada por arquivo.
- O incremento ocorre ao final da rodada, antes do commit correspondente.
- A build nunca é incrementada automaticamente por um workflow do GitHub Actions.
- Use `-ExpectedBuild` com o valor lido no início da rodada:

  ```powershell
  pwsh ./tools/version.ps1 show
  pwsh ./tools/version.ps1 bump-build -ExpectedBuild 1
  ```

  Se outro agente já tiver incrementado a build, o comando falha em vez de produzir um
  incremento duplicado. Revise o estado antes de tentar novamente.

### 2.4 Exemplo de evolução

| Situação | Versão | Build |
|---|---|---|
| Versão inicial | 1.0.0 | 1 |
| Primeira rodada de correções | 1.0.0 | 2 |
| Segunda rodada de correções | 1.0.0 | 3 |
| Versão estável autorizada | 1.0.1 | 4 |
| Nova funcionalidade estável | 1.1.0 | 5 |

## 3. Commits

Use Conventional Commits, em português ou inglês, com uma mudança lógica por commit:

```text
feat: adiciona suporte a nova funcionalidade
fix: corrige falha no gerenciamento de arquivos
refactor: reorganiza componentes internos
perf: melhora desempenho de transferência
test: adiciona testes de integridade
docs: atualiza documentação
build: ajusta processo de compilação
ci: atualiza workflow de integração
chore: atualiza dependências
```

Antes de commitar:

1. Revise `git status` e `git diff`.
2. Não inclua arquivos gerados, bancos, logs, certificados ou configurações locais.
3. Execute as verificações leves.
4. Incremente a build uma vez, se a rodada ainda não o fez.
5. Atualize o `CHANGELOG.md` quando a mudança for relevante.

Não faça push sem autorização. Não reescreva commits já publicados.

### 3.1 Sincronização com o GitHub

O remoto `origin` aponta para:

```text
https://github.com/Matehfields/Organessonium-desktop.git
```

A branch `main` já está publicada. Em um clone novo, o primeiro envio cria o tracking:

```powershell
git push -u origin main
```

Depois disso, cada rodada concluída é publicada com:

```powershell
git push
```

Antes do push:

1. `git status` e `git diff` para revisar o que será enviado.
2. `pwsh ./tools/check-repository.ps1`.
3. `pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia`, quando houver
   mudança de código.
4. Incrementar a build uma vez, se a rodada ainda não o fez.
5. Fazer o commit em Conventional Commits.

O Git envia apenas arquivos rastreados. `artifacts/`, `releases/`, `bin/`, `obj/`,
`TestResults/`, mídia verificada, bancos, logs e configurações locais ficam fora do
repositório. As builds de publicação permanecem apenas no disco local em
`artifacts/releases`.

A autenticação usa o Git Credential Manager. No primeiro push, conclua o login na janela
do GitHub. Se o Git solicitar senha, use um Personal Access Token com permissão de leitura
e escrita em Contents. Nunca grave o token em arquivo do repositório.

Se `pwsh` não estiver instalado, os mesmos scripts podem ser executados com:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\check-repository.ps1
```

Não use `git push --force`, não reescreva commits publicados e não crie tags sem
autorização.

## 4. Branches e pull requests

```powershell
git switch main
git pull --ff-only origin main
git switch -c feature/minha-funcionalidade
```

Ao concluir:

```powershell
git switch main
git pull --ff-only origin main
git switch feature/minha-funcionalidade
git rebase main
```

Se o rebase já publicado for necessário, ele exige autorização e coordenação. Depois,
abra um pull request para `main` usando o template do repositório. A CI precisa passar
antes do merge.

## 5. Testes

### 5.1 Suíte rápida

Não exige FFmpeg nem ativos de mídia:

```powershell
pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia
```

Ela compila o projeto de testes, executa os testes sem interface e ignora os testes
dependentes de mídia. É a suíte usada pela CI em pushes e pull requests.

### 5.2 Testes de interface

Alguns testes abrem janelas WebView2 ocultas ou visíveis:

```powershell
pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia -IncludeUi
```

Execute apenas quando puder abrir janelas. Não abra a interface durante uso real do
aplicativo.

### 5.3 Testes de mídia

Exigem `Organessonium/Organessonium/media/runtime-package.zip` e, para os testes que
sondam mídia, um `ffprobe.exe` da mesma distribuição do FFmpeg:

```powershell
pwsh ./tools/run-tests.ps1 -Configuration Release -FfmpegDirectory <pasta-com-ffmpeg-e-ffprobe>
```

O pacote de runtime controlado não inclui `ffprobe.exe`; por isso a suíte completa de
mídia é uma validação manual separada. A compilação de distribuição valida o runtime
embutido com `Organessonium.exe --verify-media-runtime`.

### 5.4 Limitações

- Os testes usam servidores HTTP simulados e diretórios temporários; não usam Discord
  real nem credenciais reais.
- Testes de WebView2 dependem do runtime WebView2 instalado no Windows.
- Testes de interface e de mídia podem consumir muita RAM e tempo.
- A CI não substitui validação em dois computadores reais, Discord real, MSIX instalado
  ou uso prolongado.

## 6. Compilação local

### 6.1 Requisitos

- Windows 10 versão 2004 (build 19041) ou posterior, x64.
- .NET SDK `10.0.401` (fixado em `global.json`).
- Git.
- WebView2 Runtime para executar a interface.
- Windows 10/11 SDK instalado pelo Visual Studio Installer para gerar MSI/MSIX.
- Ativos de mídia verificados para publicar a distribuição completa.

A restauração usa apenas o feed oficial do NuGet definido em `NuGet.config`. Não há
arquivos de lock porque o projeto alterna entre publicação self-contained e
framework-dependent, com referências condicionais; um único lock ficaria inconsistente.
As versões dos pacotes são fixadas nos `.csproj`, e o SDK é fixado em `global.json`.

### 6.2 Restaurar, compilar e executar

```powershell
dotnet restore Organessonium/Organessonium.slnx
dotnet build Organessonium/Organessonium.slnx -c Debug
dotnet build Organessonium/Organessonium.slnx -c Release
dotnet run --project Organessonium/Organessonium/Organessonium.csproj
```

O projeto de testes:

```powershell
dotnet restore tests/Organessonium.NetworkTests/Organessonium.NetworkTests.csproj
dotnet build tests/Organessonium.NetworkTests/Organessonium.NetworkTests.csproj -c Release
```

O projeto `tools/NamesScanner` é uma ferramenta auxiliar e também referencia o app.

### 6.3 Mídia

Um clone limpo consegue compilar o código, mas `dotnet publish` e a geração de
distribuição exigem os ativos verificados:

- `Organessonium/Organessonium/media/runtime-package.zip`
- `Organessonium/Organessonium/media/sources/ffmpeg-corresponding-source.zip`

Baixe-os da release de mídia documentada em `docs/MEDIA-ASSETS.md`. Nunca os adicione
ao Git. O script `release/Get-MediaAssets.ps1` automatiza o download e confere os
SHA-256 do manifesto antes de instalar os arquivos.

### 6.4 Compilação completa

Somente com autorização:

```powershell
./release/Get-MediaAssets.ps1 -Repository Matehfields/Organessonium-desktop
./release/Build-Releases.ps1 -SkipStorePackages
```

O script valida fontes de mídia, gera publicações self-contained e framework-dependent,
MSIs, ZIP de código-fonte, fontes FFmpeg, `build-info.json` e `SHA256SUMS.txt` em
`artifacts/releases/<versao>-build.<n>`. Sem `-SkipStorePackages`, também gera os três
MSIX e o MSIXBundle. O WACK só se aplica quando o MSIXBundle é gerado.

### 6.5 Limpeza do workspace

O workspace pode acumular builds de teste e revisão em `artifacts`. A política de
retenção é:

- preservar `artifacts/releases`, que contém as builds de publicação;
- preservar `Organessonium/Organessonium/media/runtime-package.zip` e `media/sources`,
  necessários para gerar a distribuição;
- remover `artifacts/release-work`, `artifacts/media-build`, `artifacts/test-media` e
  pastas de revisão;
- remover `bin`, `obj`, `TestResults`, `.vs`, `Logs` e caches locais quando não estiverem
  em uso.

Exemplo de limpeza no Windows PowerShell, sem tocar nas builds de publicação:

```powershell
Get-ChildItem artifacts -Directory | Where-Object { $_.Name -ne 'releases' } |
    Remove-Item -Recurse -Force
Get-ChildItem Organessonium, tests, tools -Directory -Recurse |
    Where-Object { $_.Name -in @('bin', 'obj', 'TestResults') } |
    Remove-Item -Recurse -Force
```

Não remova `artifacts/releases`, `runtime-package.zip` nem `media/sources`. Em
2026-11-18 o workspace foi reduzido de cerca de 47 GB para 5,8 GB com essa política.

## 7. Workflows do GitHub Actions

As ações externas são fixadas por SHA de commit e mantêm a versão legível em comentário.
O Dependabot acompanha atualizações dessas referências para revisão antes da adoção.

### 7.1 `ci.yml`

- Executa em push e pull request para `main`.
- Permissão: `contents: read`.
- Valida estrutura, versão e segredos.
- Restaura dependências, compila em Release e executa a suíte rápida.
- Não usa segredos e não executa código de fork com credenciais.
- Concorrência cancela execuções antigas do mesmo ref.

### 7.2 `build.yml`

- Somente manual (`workflow_dispatch`).
- Permissão: `contents: read`.
- Permite escolher branch, tag ou commit.
- Baixa os ativos de mídia da release `media-assets-v1`.
- Executa validações e a compilação.
- Compila portáteis e MSIs por padrão, sem MSIX nem MSIXBundle. Marque
  `include_store_packages` para incluir os pacotes da Store.
- Valida metadados, artefatos e checksums.
- Armazena os artefatos por 14 dias para análise.
- Executa os testes rápidos depois do upload. Uma falha de teste deixa o job vermelho,
  mas os artefatos já enviados permanecem disponíveis.
- **Não cria tag, release ou publicação.**

### 7.3 `release.yml`

- Somente manual, a partir da `main`.
- Exige tag anotada existente e contida na `main`.
- Confere se a tag corresponde a `Version.props`.
- Exige seção revisada do `CHANGELOG.md`.
- Usa o ambiente protegido `release`, com aprovação manual quando configurado.
- Compila, testa, valida, cria rascunho, envia artefatos, confere tamanhos e publica.
- Compila portáteis e MSIs por padrão; `include_store_packages` inclui MSIX e MSIXBundle.
- Se o envio falhar, o rascunho permanece para recuperação.
- Não sobrescreve release já publicada.

## 8. Publicação de uma versão estável

Pré-requisitos:

1. Ativos de mídia publicados em `media-assets-v1`.
2. Ambiente `release` configurado no GitHub com revisores obrigatórios.
3. Branch `main` protegida e CI obrigatória.
4. Versão pública autorizada e registrada em `Version.props`.
5. Seção da versão revisada no `CHANGELOG.md`, sem marcadores de rascunho.
6. Autorização explícita para push, tag e publicação.

Procedimento:

1. Atualize a versão, se autorizado:

   ```powershell
   pwsh ./tools/version.ps1 set-version -Version 1.0.1 -Authorized
   ```

2. Renomeie a seção `## [Não lançado]` do `CHANGELOG.md` para
   `## [1.0.1] - AAAA-MM-DD` e revise o conteúdo.

3. Incremente a build se ela ainda não foi incrementada nesta rodada:

   ```powershell
   pwsh ./tools/version.ps1 bump-build -ExpectedBuild <build-atual>
   ```

4. Execute as verificações:

   ```powershell
   pwsh ./tools/check-repository.ps1
   pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia
   ```

5. Faça commit e push da `main` **com autorização**:

   ```powershell
   git add <arquivos>
   git commit -m "build: prepara versão 1.0.1"
   git push origin main
   ```

6. Crie e envie a tag anotada **com autorização**:

   ```powershell
   git tag -a v1.0.1 -m "Organessônium v1.0.1"
   git push origin v1.0.1
   ```

7. No GitHub, abra **Actions → Release → Run workflow**, selecione a branch `main` e
   informe a tag `v1.0.1`.

8. Aprove o ambiente `release` quando solicitado.

9. Acompanhe a compilação. Se ela falhar antes da criação do rascunho, corrija e
   execute novamente. Se falhar depois, o rascunho permanece e uma nova execução o
   retoma.

10. Confira a release publicada: notas, arquivos, tamanhos e `SHA256SUMS.txt`.

Nunca mova ou reutilize uma tag já publicada. Para corrigir uma release, publique uma
nova versão PATCH.

## 9. Recuperação de versões e releases

### Código

```powershell
git log --oneline --decorate
git show v1.0.0
git switch --detach v1.0.0
git switch -c fix/correcao-a-partir-da-tag
```

Para desfazer uma mudança já publicada, prefira `git revert <commit>` a reescrever o
histórico. Tags são imutáveis.

### Release

- Rascunho interrompido: execute o workflow novamente com a mesma tag. O script reutiliza
  o rascunho e substitui apenas arquivos do rascunho.
- Release publicada com problema: não apague nem sobrescreva silenciosamente. Publique
  uma versão PATCH com a correção e notas claras.
- Tag errada ainda não publicada: delete a tag local e remota somente com autorização,
  recrie no commit correto e execute o workflow. Se a release já existe, não reutilize a
  tag.

## 10. Como interpretar falhas do GitHub Actions

1. Abra a execução em **Actions** e identifique o primeiro step que falhou.
2. Leia a mensagem e o log do step; os scripts falham com mensagens em português.
3. Verifique a versão em `Version.props` e a tag informada.
4. Verifique se a release de mídia existe e se os SHA-256 conferem.
5. Verifique permissões e o ambiente `release`.
6. Verifique se o `CHANGELOG.md` tem a seção exata da versão.
7. Verifique se a tag é anotada e está contida na `main`.
8. Se a assinatura estiver configurada, confira os secrets e a validade do certificado.
9. Não declare a CI aprovada se um step obrigatório foi ignorado ou falhou.

## 11. Contribuições

Leia `CONTRIBUTING.md`. Contribuições não devem incluir credenciais, dados pessoais,
arquivos de mídia grandes, certificados ou mudanças de versão não autorizadas.

## 12. Alterações de infraestrutura realizadas

- `Version.props` e `Directory.Build.props`: versionamento central.
- `global.json` e `NuGet.config`: SDK e feed previsíveis.
- `tools/Versioning.psm1` e `tools/version.ps1`: leitura, validação, incremento de build
  e alteração autorizada de versão.
- `tools/check-repository.ps1`, `tools/check-secrets.ps1` e
  `tools/check-version-metadata.ps1`: verificações locais e de CI.
- `tools/run-tests.ps1`: suíte rápida, interface e mídia.
- Limpeza do workspace: apenas `artifacts/releases` é preservado; saídas de teste,
  revisão e `media-build` são descartáveis.
- `release/Build-Releases.ps1`: versão central, metadados, assinatura de portáteis,
  `build-info.json`, ZIP de fonte atualizado e `-SkipStorePackages` para compilar sem
  MSIX/MSIXBundle.
- `release/Get-MediaAssets.ps1`, `release/Get-ReleaseNotes.ps1`,
  `release/Test-ReleaseArtifacts.ps1` e `release/Publish-Release.ps1`: automação segura.
- `.github/workflows/ci.yml`, `build.yml` e `release.yml`: CI, build manual e release.
- `.github/dependabot.yml` e template de pull request.
- `.gitignore` e `.gitattributes` ampliados.
- `CHANGELOG.md`, `AGENTS.md`, `CONTRIBUTING.md`, `SECURITY.md`, `git.md` e documentos em
  `docs/`.
- Suporte a `--skip-media` no runner de testes, sem alterar o comportamento padrão.

## 13. Licença

O projeto não possui licença de uso e redistribuição definida. O código pode ser público
para consulta, mas isso não concede automaticamente direitos de uso, modificação ou
redistribuição. A licença somente será adicionada após decisão explícita do mantenedor.
