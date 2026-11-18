# Changelog

Todas as mudanças notáveis do Organessônium Desktop são registradas neste arquivo.

O formato segue os princípios de [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
A versão pública segue [Semantic Versioning](https://semver.org/lang/pt-BR/), com um
número de build independente. A fonte única dos dois valores é `Version.props`.

## [Não lançado]

### Adicionado

- Infraestrutura de versionamento centralizada em `Version.props` e `Directory.Build.props`,
  com versão pública SemVer e número de build independente.
- Validação automatizada da estrutura do repositório, da versão e de possíveis segredos.
- Workflow de integração contínua para compilação Release e testes rápidos em pushes e
  pull requests para `main`.
- Workflow manual de compilação completa, sem publicação automática, com download
  verificado dos ativos de mídia, validação de artefatos, checksums SHA-256 e retenção
  dos resultados para análise.
- Workflow manual de release com ambiente protegido, validação de tag anotada, notas
  revisadas do changelog, publicação em rascunho e recuperação segura.
- Scripts auxiliares para incremento de build, execução de testes, verificação de
  metadados de versão, download de mídia, validação de artefatos e publicação.
- Documentação de Git, versionamento, contribuição, segurança, assinatura digital e
  configuração manual do GitHub.
- Regras de `.gitignore` para artefatos de build, testes, pacotes, certificados,
  configurações locais e arquivos de sistema.
- Dependabot para dependências NuGet e GitHub Actions.

### Alterado

- `release/Build-Releases.ps1` passou a usar exclusivamente a versão de `Version.props`.
- `release/release-settings.json` não contém mais a versão pública.
- O ZIP de código-fonte passou a incluir `Version.props`, `Directory.Build.props`,
  `global.json` e `NuGet.config`, permitindo compilação em uma extração limpa.
- Executáveis portáteis também podem ser assinados quando um certificado é configurado.
- Workspace de desenvolvimento limpo: builds de teste, revisões, `media-build` e caches
  foram removidos; apenas `artifacts/releases` permanece como destino local de
  publicações.
- Regras de higiene do workspace documentadas em `AGENTS.md` e `git.md`, incluindo o
  fluxo de sincronização com o GitHub por `git push`.

### Segurança

- Verificação de segredos, chaves privadas, caminhos locais e arquivos proibidos nos
  arquivos rastreados, com opção de varredura do histórico Git.
- Workflows usam permissões mínimas, não utilizam `pull_request_target` e não expõem
  segredos a pull requests de forks.
- Certificados de assinatura são temporários no runner e removidos ao final.

### Limitações conhecidas

- A suíte rápida não executa testes que dependem de FFmpeg, WebView2 visível ou de
  arquivos de mídia não versionados.
- A compilação de distribuição depende dos ativos `runtime-package.zip` e
  `ffmpeg-corresponding-source.zip`, publicados separadamente em uma release de mídia.
- A publicação no GitHub depende de configuração manual do ambiente protegido `release`
  e de autorização explícita do mantenedor.
- O projeto ainda não possui licença de uso e redistribuição definida.

<!--
Ao publicar a primeira versão estável, renomeie a seção acima para:

## [1.0.0] - AAAA-MM-DD

O workflow de release exige uma seção com a versão exata e rejeita marcadores como
"Não publicado" ou "A definir". Revise o conteúdo antes de criar a tag.
-->
