# Changelog

Todas as mudanças notáveis do Organessônium Desktop são registradas neste arquivo.

O formato segue os princípios de [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
A versão pública segue [Semantic Versioning](https://semver.org/lang/pt-BR/), com um
número de build independente. A fonte única dos dois valores é `Version.props`.

## [Não lançado]

### Segurança

- O token do bot e a senha do proxy passam a ser protegidos com DPAPI no escopo do usuário
  atual. Valores em texto simples são migrados automaticamente e as chaves legadas são
  removidas; a interface recebe apenas indicadores de configuração.
- O arquivo de recuperação passou ao formato `OEK2`, protegido por senha do usuário com
  PBKDF2-SHA256 e AES-GCM. Arquivos `OEK1` continuam importáveis como formato legado.
- Mensagens do WebView2 são validadas por origem, allowlist de comandos, limite de tamanho
  e estrutura antes de chegar ao código C#. Navegação externa é bloqueada e links
  legítimos são abertos no navegador do sistema.
- As páginas receberam CSP restritiva. Markdown, imagens e o editor removem scripts,
  eventos, estilos não permitidos e esquemas de URL inseguros.
- A senha de proxy salva é preservada quando o campo não é alterado e continua disponível
  para o teste de conexão autenticado.
- A migração de credenciais usa exclusão segura no SQLite e mantém valores protegidos
  indisponíveis ao salvar outras preferências. Respostas de erro do servidor não são
  reproduzidas integralmente na interface.
- A autenticação do bot fica restrita às requisições da API do Discord e não é enviada
  junto aos downloads de anexos.
- A política do WebView2 valida os tipos dos argumentos e preserva a leitura de PDFs.
  Os workflows fixam ações externas por commit.

## [1.0.0] - 2026-11-19

Primeira versão estável do Organessônium Desktop, com biblioteca armazenada no Discord,
índice serial por pasta, uploads e downloads retomáveis, prévias de mídia, armazenamento
compartilhado V2 e sincronização entre dispositivos.

### Adicionado

- Distribuição em executáveis portáteis e instaladores MSI para x86, x64 e arm64.
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
- Opção `-SkipStorePackages` em `release/Build-Releases.ps1` e
  `release/Test-ReleaseArtifacts.ps1` para compilar e validar apenas portáteis, MSIs,
  fontes e metadados, sem MSIX nem MSIXBundle.

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
- Branch `main` publicada no remoto `origin`, com tracking configurado.
- `build.yml` compila portáteis e MSIs por padrão; os pacotes da Store são opcionais por
  `include_store_packages`. `release.yml` usa o mesmo controle.
- `build.yml` publica os artefatos antes dos testes rápidos. Uma falha de teste deixa o
  job vermelho, mas não impede a análise dos portáteis e instaladores gerados.

### Corrigido

- A reconstrução do serial de favoritos aguarda a publicação de seriais, evitando falhas
  de autenticação durante atualizações concorrentes de miniaturas.
- O download de anexos múltiplos preserva o erro original ao encontrar uma parte inválida,
  em vez de encapsulá-lo.
- A compilação do projeto de testes não exige mais os ativos de mídia; a verificação do
  runtime FFmpeg ocorre apenas na publicação.
- Os workflows montam parâmetros nomeados com hashtable; switches como
  `-SkipStorePackages` não são mais atribuídos ao parâmetro `-Version`.

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
- A publicação no GitHub usa o ambiente protegido `release` e exige autorização
  explícita do mantenedor.
- O projeto ainda não possui licença de uso e redistribuição definida.
