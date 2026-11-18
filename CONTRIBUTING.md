# Contribuindo com o Organessônium Desktop

Obrigado pelo interesse. O projeto é um aplicativo desktop Windows em C#/WPF com
WebView2, que usa o Discord como armazenamento de payloads. Antes de contribuir, leia o
`AGENTS.md` e o `git.md`.

## Licença

O projeto ainda não possui uma licença de uso e redistribuição definida. Não presuma
direitos de uso, modificação ou redistribuição além dos concedidos pelo mantenedor.
Não adicione uma licença sem autorização explícita.

## Ambiente

Requisitos:

- Windows 10 versão 2004 (build 19041) ou posterior, x64.
- .NET SDK `10.0.401` (fixado em `global.json`).
- Git.
- WebView2 Runtime.
- Windows 10/11 SDK para gerar MSI/MSIX (opcional para desenvolvimento).
- Os ativos de mídia verificados para testar publicação completa.

Passos:

```powershell
git clone https://github.com/Matehfields/Organessonium-desktop.git
cd Organessonium-desktop
dotnet restore Organessonium/Organessonium.slnx
dotnet build Organessonium/Organessonium.slnx -c Debug
dotnet run --project Organessonium/Organessonium/Organessonium.csproj
```

Os ativos `runtime-package.zip` e `ffmpeg-corresponding-source.zip` não são versionados.
Para compilar a distribuição completa, siga `docs/MEDIA-ASSETS.md`.

## Branches

- `main`: branch estável.
- `feature/<descricao>`: funcionalidades.
- `fix/<descricao>`: correções.

Crie a branch a partir da `main` atualizada:

```powershell
git switch main
git pull --ff-only origin main
git switch -c fix/descricao-curta
```

## Commits

Use Conventional Commits:

```text
feat: adiciona ...
fix: corrige ...
perf: melhora ...
refactor: reorganiza ...
test: adiciona ...
docs: atualiza ...
build: ajusta ...
ci: atualiza ...
```

Mantenha commits pequenos e coerentes. Não inclua segredos, certificados, bancos,
logs, caminhos absolutos ou arquivos gerados.

## Versionamento

A versão pública e a build ficam em `Version.props`. Não altere a versão pública sem
autorização do mantenedor. Ao concluir uma rodada, incremente a build uma única vez:

```powershell
pwsh ./tools/version.ps1 bump-build -ExpectedBuild <build-atual>
```

Não incremente a build por arquivo alterado nem dentro do GitHub Actions.

## Testes

Suíte rápida:

```powershell
pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia
```

Testes de interface:

```powershell
pwsh ./tools/run-tests.ps1 -Configuration Release -SkipMedia -IncludeUi
```

Testes de mídia exigem FFmpeg e `ffprobe.exe`; veja `git.md`. Não use credenciais reais
do Discord. Os testes usam servidores simulados e diretórios temporários.

## Pull request

Antes de abrir:

- [ ] A branch está atualizada com a `main`.
- [ ] A solução compila em Debug e Release.
- [ ] A suíte rápida passa.
- [ ] A build foi incrementada uma vez, quando aplicável.
- [ ] O `CHANGELOG.md` foi atualizado para mudanças relevantes.
- [ ] Não há segredos, dados pessoais ou arquivos grandes.
- [ ] A mudança preserva o comportamento e a arquitetura existentes.

Descreva o problema, a solução, os riscos e o que não pôde ser testado. A CI precisa
passar. O mantenedor decide sobre merge, versão e publicação.

## Segurança

Não abra issues públicas com tokens, senhas, chaves ou dados pessoais. Siga
`SECURITY.md` para relatar vulnerabilidades.

## Publicação

Contribuições não publicam releases. Compilações completas, tags e releases exigem
autorização explícita do mantenedor. Veja `git.md` e `docs/GITHUB-SETUP.md`.
